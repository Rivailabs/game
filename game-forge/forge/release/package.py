"""Release packaging: reproducible source/export package, asset provenance bundle, test report and
the unsigned artifact, tied together by a package manifest.

Plan step 8 ("Package"): "Reproducible source/export package, asset provenance, test report and
unsigned release artifact". Reproducible means: the same commit, assets and artifact produce the
same bytes, so anyone can rebuild the package and compare hashes.

* Source archive: ``git archive`` of the exact commit (contents and metadata come from the commit,
  not the working tree), recompressed with gzip ``mtime=0`` and no file name.
* Provenance bundle: one record per release asset with the plan's provenance fields; an asset with
  no record, a missing field or a hash that differs from the asset bytes makes the bundle
  incomplete (listed, never hidden).
* Test report: the evidence statuses the candidate was built with.
* Unsigned artifact: copied in byte-for-byte; signing happens later in the separate signer.

``python -m forge.release.package verify <manifest>`` re-hashes every file (protected check).
"""

from __future__ import annotations

import gzip
import hashlib
import io
import json
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

from ..credentials import scrubbed_env

PROVENANCE_FIELDS = ("path", "sha256", "source", "rights", "generator", "generated_at", "terms",
                     "transformations", "attribution", "territory_flags")


class PackageError(Exception):
    pass


def sha256_file(path: str | Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def source_archive(repo: str | Path, commit: str, prefix: str) -> bytes:
    """Deterministic ``.tar.gz`` of one commit."""
    if not prefix.endswith("/"):
        prefix += "/"
    p = subprocess.run(["git", "-C", str(repo), "archive", "--format=tar", f"--prefix={prefix}", commit],
                       capture_output=True, env=scrubbed_env())
    if p.returncode != 0:
        raise PackageError(f"git archive failed: {p.stderr.decode(errors='replace').strip()}")
    buf = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", fileobj=buf, mtime=0, compresslevel=9) as gz:
        gz.write(p.stdout)
    return buf.getvalue()


def resolve_commit(repo: str | Path, ref: str) -> str:
    p = subprocess.run(["git", "-C", str(repo), "rev-parse", "--verify", f"{ref}^{{commit}}"],
                       capture_output=True, text=True, env=scrubbed_env())
    if p.returncode != 0:
        raise PackageError(f"unknown commit {ref!r}")
    return p.stdout.strip()


@dataclass
class ProvenanceCheck:
    complete: bool
    problems: list[str] = field(default_factory=list)
    bundle: dict = field(default_factory=dict)


def check_provenance(assets: dict[str, str], records: list[dict]) -> ProvenanceCheck:
    by_path = {r.get("path"): r for r in records}
    problems = []
    for path, digest in sorted(assets.items()):
        r = by_path.get(path)
        if r is None:
            problems.append(f"{path}: no provenance record")
            continue
        missing = [f for f in PROVENANCE_FIELDS if r.get(f) in (None, "")]
        if missing:
            problems.append(f"{path}: provenance missing {', '.join(missing)}")
        if r.get("sha256") and r.get("sha256") != digest:
            problems.append(f"{path}: provenance hash differs from the release asset (record is for other bytes)")
    extra = sorted(set(by_path) - set(assets))
    bundle = {"assets": [by_path[p] for p in sorted(assets) if p in by_path], "unreferenced_records": extra,
              "complete": not problems, "problems": problems}
    return ProvenanceCheck(not problems, problems, bundle)


def _dump(obj) -> bytes:
    return (json.dumps(obj, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")


def build_package(*, repo: str | Path, commit: str, out_dir: str | Path, package_id: str, version_name: str,
                  version_code: int, artifact: str | Path, assets: dict[str, str], provenance: list[dict],
                  tests: list[dict], project_id: str, rules_version: str, spec_version: int, spec_digest: str,
                  dependency_manifest: dict, economy_version: str | None = None,
                  toolchain_manifest_sha256: str = "") -> dict:
    """Write the package directory and return its manifest (also written as ``package-manifest.json``)."""
    from .candidate import ReleaseCandidate, TestResult

    out = Path(out_dir)
    if out.exists() and any(out.iterdir()):
        raise PackageError(f"{out} is not empty; a package directory is written once")
    out.mkdir(parents=True, exist_ok=True)
    full = resolve_commit(repo, commit)
    stem = f"{package_id}-{version_name}"
    files: dict[str, bytes] = {}
    files[f"{stem}-source.tar.gz"] = source_archive(repo, full, stem)
    prov = check_provenance(assets, provenance)
    files["provenance.json"] = _dump(prov.bundle)
    files["test-report.json"] = _dump({"tests": tests, "all_pass": all(t.get("status") == "PASS" for t in tests)})
    files["dependency-manifest.json"] = _dump(dependency_manifest)
    art = Path(artifact)
    if not art.is_file():
        raise PackageError(f"unsigned artifact not found: {art}")
    art_name = f"{stem}-unsigned{art.suffix}"
    for name, data in files.items():
        (out / name).write_bytes(data)
    shutil.copyfile(art, out / art_name)
    hashes = {name: hashlib.sha256(data).hexdigest() for name, data in files.items()}
    hashes[art_name] = sha256_file(out / art_name)
    cand = ReleaseCandidate(
        project_id=project_id, package_id=package_id, version_name=version_name, version_code=version_code,
        source_commit=full, source_archive_sha256=hashes[f"{stem}-source.tar.gz"], rules_version=rules_version,
        economy_version=economy_version, spec_version=spec_version, spec_digest=spec_digest, assets=dict(assets),
        dependency_manifest_sha256=hashes["dependency-manifest.json"], toolchain_manifest_sha256=toolchain_manifest_sha256,
        artifact_name=art_name, artifact_sha256=hashes[art_name], provenance_sha256=hashes["provenance.json"],
        tests=[TestResult(**t) for t in tests])
    manifest = {"format": "game-forge-release-package/1", "candidate": cand.model_dump(mode="json"),
                "candidate_hash": cand.candidate_hash, "files": hashes, "provenance_complete": prov.complete,
                "provenance_problems": prov.problems, "signed": False,
                "note": "Unsigned. Signing happens only in the separate signing service after owner approval."}
    (out / "package-manifest.json").write_bytes(_dump(manifest))
    return manifest


def verify_package(manifest_path: str | Path) -> list[str]:
    """Problems found re-hashing a package (empty list = intact)."""
    mp = Path(manifest_path)
    m = json.loads(mp.read_text())
    problems = []
    for name, digest in m.get("files", {}).items():
        f = mp.parent / name
        if not f.is_file():
            problems.append(f"{name}: missing")
        elif sha256_file(f) != digest:
            problems.append(f"{name}: bytes differ from the manifest")
    from .candidate import ReleaseCandidate

    cand = ReleaseCandidate.model_validate(m["candidate"])
    if cand.candidate_hash != m.get("candidate_hash"):
        problems.append("candidate hash does not match the candidate fields")
    if m.get("files", {}).get(cand.artifact_name) != cand.artifact_sha256:
        problems.append("artifact hash in the candidate differs from the package file list")
    if not m.get("provenance_complete"):
        problems.append("provenance incomplete: " + "; ".join(m.get("provenance_problems", [])))
    return problems


def main(argv: list[str] | None = None) -> int:
    import argparse

    p = argparse.ArgumentParser(prog="python -m forge.release.package")
    sub = p.add_subparsers(dest="cmd", required=True)
    v = sub.add_parser("verify")
    v.add_argument("manifest")
    args = p.parse_args(argv)
    problems = verify_package(args.manifest)
    for x in problems:
        print(f"FAIL: {x}")
    if not problems:
        print("PASS: package intact")
    return 1 if problems else 0


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
