"""Separate signing service: ``python -m forge.release.signer --config <signer.toml> sign ...``.

Plan ("Secrets egress and signed releases"; "Release and rollback procedure"): builds are unsigned
until the release gate; the owner approves a candidate hash; a separate signing service receives
that exact artifact and the approved metadata; no build script gets private signing keys; the
signed artifact hash, certificate fingerprint and approval record are archived.

This module is that service. It is meant to run as its own process (ideally under its own OS
account) and imports nothing from the orchestrator. It accepts only:

* an artifact whose sha256 equals the approval's ``artifact_sha256``;
* an approval record of kind ``release_approval``, decision ``APPROVED``, destination ``signing``,
  signed (Ed25519) by a key listed in the signer's *own* configuration;
* a package/version that has not already been signed with different bytes.

Signing uses ``apksigner`` for ``.apk`` and ``jarsigner`` for ``.aab``/``.jar``; when the needed tool
is absent the result is BLOCKED (exit 3), never a fake signature. The keystore and its password
file must live outside every git work tree and must not be readable by other users. The password
reaches the tool only as a ``file:`` reference, the tool runs with a minimal environment (no Forge
or provider variables), and its output is scrubbed of the password before being archived.

``signer.toml``::

    keystore = "/home/release/keys/upload.jks"     # Play App Signing: this is the UPLOAD key
    key_alias = "upload"
    store_password_file = "/home/release/keys/upload.pass"
    archive_dir = "/home/release/archive"
    trusted_approvers = ["<ed25519 public key hex>"]
    # apksigner = "/opt/android/build-tools/35.0.0/apksigner"   # optional explicit paths
    # jarsigner = "/usr/bin/jarsigner"
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import tomllib
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from .keys import CryptoUnavailable, SignatureInvalid, verify_record

EXIT_SIGNED, EXIT_REFUSED, EXIT_BLOCKED = 0, 2, 3
_SAFE_ENV = ("PATH", "HOME", "LANG", "LC_ALL", "JAVA_HOME", "TMPDIR")


class SignRefused(Exception):
    pass


class SignBlocked(Exception):
    pass


@dataclass
class SignerConfig:
    keystore: Path
    key_alias: str
    store_password_file: Path
    archive_dir: Path
    trusted_approvers: list[str]
    apksigner: Optional[str] = None
    jarsigner: Optional[str] = None
    keytool: Optional[str] = None


def load_config(path: str | Path) -> SignerConfig:
    raw = tomllib.loads(Path(path).read_text())
    try:
        return SignerConfig(
            keystore=Path(raw["keystore"]).expanduser(), key_alias=raw["key_alias"],
            store_password_file=Path(raw["store_password_file"]).expanduser(),
            archive_dir=Path(raw["archive_dir"]).expanduser(), trusted_approvers=list(raw["trusted_approvers"]),
            apksigner=raw.get("apksigner"), jarsigner=raw.get("jarsigner"), keytool=raw.get("keytool"))
    except KeyError as e:
        raise SignRefused(f"signer config missing {e.args[0]}") from None


def inside_git_worktree(path: Path) -> Optional[Path]:
    for parent in [path.resolve(), *path.resolve().parents]:
        if (parent / ".git").exists():
            return parent
    return None


def _check_secret_file(p: Path, what: str) -> None:
    if not p.is_file():
        raise SignBlocked(f"{what} not found: {p}")
    repo = inside_git_worktree(p)
    if repo is not None:
        raise SignRefused(f"{what} is inside the git work tree {repo}; signing material must live outside "
                          "every repository and workspace")
    if os.name == "posix" and p.stat().st_mode & 0o077:
        raise SignRefused(f"{what} {p} is readable by other users; chmod 600 it")


def _env() -> dict[str, str]:
    return {k: os.environ[k] for k in _SAFE_ENV if k in os.environ}


def _sha(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def _run(argv: list[str], password: str, timeout: float = 600) -> str:
    p = subprocess.run(argv, capture_output=True, text=True, env=_env(), timeout=timeout)
    out = (p.stdout + p.stderr).replace(password, "[REDACTED]") if password else p.stdout + p.stderr
    if p.returncode != 0:
        raise SignRefused(f"{Path(argv[0]).name} failed ({p.returncode}): {out.strip()[-800:]}")
    return out


def _archive_index(cfg: SignerConfig) -> list[dict]:
    idx = cfg.archive_dir / "index.jsonl"
    return [json.loads(x) for x in idx.read_text().splitlines() if x.strip()] if idx.exists() else []


def check_approval(approval: dict, artifact: Path, cfg: SignerConfig) -> None:
    try:
        verify_record(approval, cfg.trusted_approvers)
    except CryptoUnavailable as e:
        raise SignBlocked(str(e)) from e
    except SignatureInvalid as e:
        raise SignRefused(f"approval rejected: {e}") from e
    if approval.get("kind") != "release_approval" or approval.get("decision") != "APPROVED":
        raise SignRefused("not an APPROVED release approval")
    if approval.get("destination") != "signing":
        raise SignRefused(f"approval destination is {approval.get('destination')!r}, not 'signing'")
    for f in ("candidate_hash", "artifact_sha256", "package_id", "version_code"):
        if not approval.get(f):
            raise SignRefused(f"approval lacks {f}")
    actual = _sha(artifact)
    if actual != approval["artifact_sha256"]:
        raise SignRefused(f"artifact sha256 {actual[:12]}... is not the approved {approval['artifact_sha256'][:12]}... "
                          "(a different file, even with the same name, is never signed)")
    for prior in _archive_index(cfg):
        if prior["package_id"] == approval["package_id"] and prior["version_code"] == approval["version_code"] \
                and prior["unsigned_sha256"] != actual:
            raise SignRefused(f"{approval['package_id']} version {approval['version_code']} was already signed from "
                              "different bytes; build a new version")


def _tool(name: str, configured: Optional[str]) -> str:
    exe = configured or shutil.which(name)
    if not exe or not Path(exe).exists():
        raise SignBlocked(f"{name} not found (install the Android build-tools / a JDK, or set its path in signer.toml)")
    return exe


def _fingerprint(text: str) -> Optional[str]:
    m = re.search(r"certificate SHA-256 digest:\s*([0-9a-fA-F:]+)", text) or \
        re.search(r"SHA256:\s*([0-9A-Fa-f:]{64,})", text)
    return m.group(1).replace(":", "").lower() if m else None


def sign(artifact: str | Path, approval: dict, cfg: SignerConfig, out_dir: str | Path, *, clock=time.time) -> dict:
    artifact, out_dir = Path(artifact), Path(out_dir)
    if not artifact.is_file():
        raise SignRefused(f"artifact not found: {artifact}")
    check_approval(approval, artifact, cfg)
    _check_secret_file(cfg.keystore, "keystore")
    _check_secret_file(cfg.store_password_file, "keystore password file")
    password = cfg.store_password_file.read_text().strip()
    suffix = artifact.suffix.lower()
    stem = f"{approval['package_id']}-{approval.get('version_name') or approval['version_code']}-signed{suffix}"
    out_dir.mkdir(parents=True, exist_ok=True)
    final = out_dir / stem
    with tempfile.TemporaryDirectory(prefix="forge-sign-") as tmp:
        work_in = Path(tmp) / f"in{suffix}"
        shutil.copyfile(artifact, work_in)
        if _sha(work_in) != approval["artifact_sha256"]:  # the bytes we sign are the bytes approved
            raise SignRefused("artifact changed while being copied for signing")
        signed = Path(tmp) / f"signed{suffix}"
        if suffix == ".apk":
            tool = _tool("apksigner", cfg.apksigner)
            _run([tool, "sign", "--ks", str(cfg.keystore), "--ks-key-alias", cfg.key_alias,
                  "--ks-pass", f"file:{cfg.store_password_file}", "--out", str(signed), str(work_in)], password)
            verify_out = _run([tool, "verify", "--print-certs", str(signed)], password)
            fp = _fingerprint(verify_out)
            tool_name = "apksigner"
        elif suffix in (".aab", ".jar"):
            tool = _tool("jarsigner", cfg.jarsigner)
            _run([tool, "-keystore", str(cfg.keystore), "-storepass:file", str(cfg.store_password_file),
                  "-digestalg", "SHA-256", "-signedjar", str(signed), str(work_in), cfg.key_alias], password)
            verify_out = _run([tool, "-verify", str(signed)], password)
            if "jar verified" not in verify_out:
                raise SignRefused(f"jarsigner could not verify the signed file: {verify_out.strip()[-400:]}")
            keytool = _tool("keytool", cfg.keytool)
            fp = _fingerprint(_run([keytool, "-list", "-v", "-keystore", str(cfg.keystore), "-alias", cfg.key_alias,
                                    "-storepass:file", str(cfg.store_password_file)], password))
            tool_name = "jarsigner"
        else:
            raise SignRefused(f"unsupported artifact type {suffix!r} (expected .apk, .aab or .jar)")
        if not fp:
            raise SignRefused("could not read the signing certificate fingerprint")
        shutil.copyfile(signed, final)
    record = {"package_id": approval["package_id"], "version_code": approval["version_code"],
              "version_name": approval.get("version_name"), "candidate_hash": approval["candidate_hash"],
              "unsigned_sha256": approval["artifact_sha256"], "signed_sha256": _sha(final),
              "signed_file": final.name, "certificate_sha256": fp, "tool": tool_name,
              "approval": approval, "signed_at": clock(),
              "note": "Play App Signing: this is the upload-key signature; Google re-signs with the app-signing key."}
    cfg.archive_dir.mkdir(parents=True, exist_ok=True)
    rec_path = cfg.archive_dir / f"{approval['package_id']}-{approval['version_code']}-{record['signed_sha256'][:12]}.json"
    rec_path.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n")
    with open(cfg.archive_dir / "index.jsonl", "a", encoding="utf-8") as fh:
        fh.write(json.dumps({k: record[k] for k in ("package_id", "version_code", "candidate_hash", "unsigned_sha256",
                                                      "signed_sha256", "certificate_sha256", "signed_at")},
                            sort_keys=True) + "\n")
    return record


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="python -m forge.release.signer",
                                description="Separate signing service: signs only an approved artifact.")
    p.add_argument("--config", required=True, help="signer.toml (outside every repository)")
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("sign")
    s.add_argument("--artifact", required=True)
    s.add_argument("--approval", required=True, help="signed release approval JSON")
    s.add_argument("--out", required=True)
    sub.add_parser("archive", help="list signed releases")
    args = p.parse_args(argv)
    try:
        cfg = load_config(args.config)
        if args.cmd == "archive":
            for r in _archive_index(cfg):
                print(json.dumps(r, sort_keys=True))
            return 0
        rec = sign(args.artifact, json.loads(Path(args.approval).read_text()), cfg, args.out)
    except SignBlocked as e:
        print(f"BLOCKED: {e}", file=sys.stderr)
        return EXIT_BLOCKED
    except SignRefused as e:
        print(f"REFUSED: {e}", file=sys.stderr)
        return EXIT_REFUSED
    print(json.dumps({k: rec[k] for k in ("signed_file", "signed_sha256", "certificate_sha256", "candidate_hash")},
                     indent=2))
    return EXIT_SIGNED


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
