"""R3 release: reproducible package, provenance, signed approvals, separate signer, store submission."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

import pytest

from forge.release import signer as signer_mod
from forge.release.candidate import ApprovalRefused, ReleaseRecord, approve_release
from forge.release.keys import SignatureInvalid, generate_key, public_hex, sign_record, verify_record
from forge.release.package import PackageError, build_package, check_provenance, main as package_main, verify_package
from forge.release.signer import SignBlocked, SignerConfig, SignRefused, sign
from forge.release.store import GooglePlayClient, SubmissionAuthorisation, SubmissionRefused, authorise, submit

from .conftest import git

HAS_JAVA_SIGNING = shutil.which("jarsigner") and shutil.which("keytool")


@pytest.fixture
def game_repo(tmp_path):
    r = tmp_path / "game"
    (r / "src").mkdir(parents=True)
    (r / "src" / "Rules.cs").write_text("class Rules {}\n")
    (r / "art").mkdir()
    (r / "art" / "bow.fbx").write_bytes(b"FBX-bow")
    git(r, "init", "-q", "-b", "main")
    git(r, "config", "user.email", "t@example.com")
    git(r, "config", "user.name", "Test")
    git(r, "add", "-A")
    git(r, "commit", "-q", "-m", "init")
    return r


def _jar(path: Path, version_code: int = 1) -> Path:
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("AndroidManifest.txt", f"versionCode={version_code}\n")
        z.writestr("classes.txt", "game\n")
    return path


def _prov(path, digest, **over):
    rec = {"path": path, "sha256": digest, "source": "catalogue:objaverse uid 123", "rights": "CC-BY-4.0",
           "generator": "Blender 4.2 normalisation", "generated_at": "2026-10-01", "terms": "CC-BY-4.0",
           "transformations": ["decimate", "rescale"], "attribution": "Model by A. Artist", "territory_flags": ["none"]}
    rec.update(over)
    return rec


TESTS = [{"name": "rules-tests", "status": "PASS", "evidence_class": "rules"},
         {"name": "device-smoke", "status": "PASS", "evidence_class": "device"}]


def _build(game_repo, out, artifact, **over):
    digest = hashlib.sha256(b"FBX-bow").hexdigest()
    kw = dict(repo=game_repo, commit="HEAD", out_dir=out, package_id="com.example.runeduel", version_name="1.0.0",
              version_code=1, artifact=artifact, assets={"art/bow.fbx": digest},
              provenance=[_prov("art/bow.fbx", digest)], tests=TESTS, project_id="rune-duel", rules_version="rd-1",
              spec_version=2, spec_digest="ab" * 32, dependency_manifest={"unity": "6000.0.x", "packages": []})
    kw.update(over)
    return build_package(**kw)


def test_package_is_reproducible_and_verifiable(tmp_path, game_repo):
    art = _jar(tmp_path / "game.aab")
    m1 = _build(game_repo, tmp_path / "p1", art)
    m2 = _build(game_repo, tmp_path / "p2", art)
    assert m1["files"] == m2["files"] and m1["candidate_hash"] == m2["candidate_hash"]
    assert m1["provenance_complete"] and m1["signed"] is False
    assert verify_package(tmp_path / "p1" / "package-manifest.json") == []
    assert package_main(["verify", str(tmp_path / "p1" / "package-manifest.json")]) == 0
    src = tmp_path / "p1" / "com.example.runeduel-1.0.0-source.tar.gz"
    listing = subprocess.run(["tar", "tzf", str(src)], capture_output=True, text=True).stdout.split()
    assert "com.example.runeduel-1.0.0/src/Rules.cs" in listing
    # Tampering is detected.
    (tmp_path / "p1" / "test-report.json").write_text("{}")
    assert any("test-report.json" in p for p in verify_package(tmp_path / "p1" / "package-manifest.json"))
    with pytest.raises(PackageError, match="not empty"):
        _build(game_repo, tmp_path / "p1", art)


def test_any_change_produces_a_new_candidate_hash(tmp_path, game_repo):
    art = _jar(tmp_path / "game.aab")
    base = _build(game_repo, tmp_path / "a", art)["candidate_hash"]
    art2 = _jar(tmp_path / "game2.aab", version_code=2)
    assert _build(game_repo, tmp_path / "b", art2)["candidate_hash"] != base
    tests = [dict(TESTS[0]), dict(TESTS[1], status="INCOMPLETE")]
    assert _build(game_repo, tmp_path / "c", art, tests=tests)["candidate_hash"] != base


def test_provenance_gaps_are_listed_not_hidden():
    d = "aa" * 32
    check = check_provenance({"a.fbx": d, "b.wav": "bb" * 32, "c.png": "cc" * 32},
                             [_prov("a.fbx", d), _prov("b.wav", "ff" * 32), _prov("c.png", "cc" * 32, rights="")])
    assert not check.complete
    text = " ".join(check.problems)
    assert "b.wav: provenance hash differs" in text and "c.png: provenance missing rights" in text
    check2 = check_provenance({}, [_prov("x", d)])
    assert check2.complete and check2.bundle["unreferenced_records"] == ["x"]


def _record(manifest, **over):
    from forge.release.candidate import ReleaseCandidate

    kw = dict(candidate=ReleaseCandidate.model_validate(manifest["candidate"]), scope="Rune Duel 1.0 India",
              device_results=["Pixel 6a: smoke PASS"], data_declarations="No personal data collected",
              support_contact="support@example.org", rollback_method="halt rollout; previous build stays live",
              last_restore_drill="2026-10-05 restore of project backup OK")
    kw.update(over)
    return ReleaseRecord(**kw)


@pytest.fixture
def owner_key(tmp_path):
    k = tmp_path / "keys" / "owner.key"
    generate_key(k)
    return k


def test_signed_records_verify_only_with_trusted_keys(tmp_path, owner_key):
    rec = sign_record({"a": 1}, owner_key)
    pub = public_hex(owner_key)
    assert verify_record(rec, [pub]) == pub
    with pytest.raises(SignatureInvalid, match="does not trust"):
        verify_record(rec, ["00" * 32])
    with pytest.raises(SignatureInvalid, match="does not match"):
        verify_record({**rec, "a": 2}, [pub])
    with pytest.raises(SignatureInvalid, match="not signed"):
        verify_record({"a": 1}, [pub])
    with pytest.raises(FileExistsError):
        generate_key(owner_key)
    assert oct(owner_key.stat().st_mode & 0o777) == "0o600"


def test_release_approval_requires_complete_record_and_evidence(tmp_path, game_repo, owner_key):
    m = _build(game_repo, tmp_path / "p", _jar(tmp_path / "g.aab"))
    with pytest.raises(ApprovalRefused) as e:
        approve_release(_record(m, support_contact="", last_restore_drill=""), approver="owner",
                        approver_key=owner_key, provenance_complete=True)
    assert "support_contact" in str(e.value) and "last_restore_drill" in str(e.value)
    m2 = _build(game_repo, tmp_path / "q", _jar(tmp_path / "g2.aab"),
                tests=[TESTS[0], dict(TESTS[1], status="INCOMPLETE")])
    with pytest.raises(ApprovalRefused, match="INCOMPLETE/BLOCKED never counts"):
        approve_release(_record(m2), approver="owner", approver_key=owner_key, provenance_complete=True)
    with pytest.raises(ApprovalRefused, match="provenance"):
        approve_release(_record(m), approver="owner", approver_key=owner_key, provenance_complete=False)
    ok = approve_release(_record(m), approver="owner", approver_key=owner_key, provenance_complete=True,
                         clock=lambda: 1.0)
    assert ok["candidate_hash"] == m["candidate_hash"] and ok["artifact_sha256"] == m["candidate"]["artifact_sha256"]
    assert ok["destination"] == "signing" and ok["signature"]


# ------------------------------------------------------------------ signer


def _keystore(d: Path) -> tuple[Path, Path]:
    d.mkdir(parents=True, exist_ok=True)
    ks, pw = d / "upload.jks", d / "upload.pass"
    pw.write_text("s3cret-pass\n")
    os.chmod(pw, 0o600)
    subprocess.run(["keytool", "-genkeypair", "-keystore", str(ks), "-storepass", "s3cret-pass", "-keypass",
                    "s3cret-pass", "-alias", "upload", "-keyalg", "RSA", "-keysize", "2048", "-validity", "2",
                    "-dname", "CN=Forge Test", "-storetype", "PKCS12"], check=True, capture_output=True)
    os.chmod(ks, 0o600)
    return ks, pw


@pytest.fixture
def signing_setup(tmp_path, game_repo, owner_key):
    m = _build(game_repo, tmp_path / "pkg", _jar(tmp_path / "game.aab"))
    artifact = tmp_path / "pkg" / m["candidate"]["artifact_name"]
    approval = approve_release(_record(m), approver="owner", approver_key=owner_key, provenance_complete=True)
    secrets = tmp_path / "signer-secrets"
    if HAS_JAVA_SIGNING:
        ks, pw = _keystore(secrets)
    else:  # pragma: no cover - environment dependent
        secrets.mkdir()
        ks, pw = secrets / "upload.jks", secrets / "upload.pass"
    cfg = SignerConfig(keystore=ks, key_alias="upload", store_password_file=pw, archive_dir=tmp_path / "archive",
                       trusted_approvers=[public_hex(owner_key)])
    return m, artifact, approval, cfg


@pytest.mark.skipif(not HAS_JAVA_SIGNING, reason="jarsigner/keytool not installed")
def test_signer_signs_exact_approved_artifact_with_real_jarsigner(tmp_path, signing_setup):
    m, artifact, approval, cfg = signing_setup
    rec = sign(artifact, approval, cfg, tmp_path / "signed", clock=lambda: 9.0)
    signed = tmp_path / "signed" / rec["signed_file"]
    assert signed.exists() and rec["signed_sha256"] != rec["unsigned_sha256"]
    assert len(rec["certificate_sha256"]) == 64 and rec["tool"] == "jarsigner"
    assert rec["approval"]["candidate_hash"] == m["candidate_hash"]
    verify = subprocess.run(["jarsigner", "-verify", str(signed)], capture_output=True, text=True)
    assert "jar verified" in verify.stdout
    index = (cfg.archive_dir / "index.jsonl").read_text().splitlines()
    assert len(index) == 1 and json.loads(index[0])["certificate_sha256"] == rec["certificate_sha256"]
    archived = json.loads(next(cfg.archive_dir.glob("*.json")).read_text())
    assert "s3cret-pass" not in json.dumps(archived)

    # Same version, different bytes: refused. Same bytes again: allowed (idempotent re-sign).
    other = tmp_path / "other.aab"
    _jar(other, version_code=7)
    forged = dict(approval, artifact_sha256=hashlib.sha256(other.read_bytes()).hexdigest())
    with pytest.raises(SignRefused, match="signature does not match"):
        sign(other, forged, cfg, tmp_path / "s2")


@pytest.mark.skipif(not HAS_JAVA_SIGNING, reason="jarsigner/keytool not installed")
def test_signer_refuses_unapproved_or_mismatched_inputs(tmp_path, signing_setup, owner_key):
    m, artifact, approval, cfg = signing_setup
    other = _jar(tmp_path / "renamed.aab", version_code=9)
    with pytest.raises(SignRefused, match="not the approved"):
        sign(other, approval, cfg, tmp_path / "o")  # different bytes, any name
    with pytest.raises(SignRefused, match="does not trust"):
        sign(artifact, approval, SignerConfig(**{**cfg.__dict__, "trusted_approvers": ["11" * 32]}), tmp_path / "o")
    bad_dest = sign_record({**{k: v for k, v in approval.items() if k not in ("signature", "signer_public_key")},
                            "destination": "store"}, owner_key)
    with pytest.raises(SignRefused, match="destination"):
        sign(artifact, bad_dest, cfg, tmp_path / "o")
    os.chmod(cfg.store_password_file, 0o644)
    with pytest.raises(SignRefused, match="readable by other users"):
        sign(artifact, approval, cfg, tmp_path / "o")
    os.chmod(cfg.store_password_file, 0o600)
    # A keystore inside a git work tree (e.g. the game repository) is refused.
    inside = tmp_path / "game" / "keys"
    inside.mkdir()
    shutil.copy(cfg.keystore, inside / "upload.jks")
    os.chmod(inside / "upload.jks", 0o600)
    with pytest.raises(SignRefused, match="git work tree"):
        sign(artifact, approval, SignerConfig(**{**cfg.__dict__, "keystore": inside / "upload.jks"}), tmp_path / "o")
    # A previously signed version with different bytes is never re-signed.
    sign(artifact, approval, cfg, tmp_path / "ok")
    cfg.archive_dir.joinpath("index.jsonl").write_text(json.dumps(
        {"package_id": approval["package_id"], "version_code": approval["version_code"], "unsigned_sha256": "0" * 64,
         "candidate_hash": "x", "signed_sha256": "y", "certificate_sha256": "z", "signed_at": 0}) + "\n")
    with pytest.raises(SignRefused, match="already signed"):
        sign(artifact, approval, cfg, tmp_path / "again")


@pytest.mark.skipif(not HAS_JAVA_SIGNING, reason="needs a real keystore for the checks that precede tool lookup")
def test_signer_blocks_without_signing_tool(tmp_path, signing_setup, monkeypatch):
    m, artifact, approval, cfg = signing_setup
    apk = tmp_path / "game.apk"
    shutil.copy(artifact, apk)  # identical approved bytes; only the suffix selects apksigner
    monkeypatch.setattr(signer_mod.shutil, "which", lambda name: None)
    with pytest.raises(SignBlocked, match="apksigner not found"):
        sign(apk, approval, cfg, tmp_path / "o")
    assert not list((tmp_path / "o").glob("*")) if (tmp_path / "o").exists() else True


@pytest.mark.skipif(not HAS_JAVA_SIGNING, reason="keytool not installed (needed for the keystore)")
def test_signer_uses_apksigner_for_apk_via_cli(tmp_path, signing_setup):
    """A scripted apksigner stands in for the Android build-tools (not installed here)."""
    m, artifact, approval, cfg = signing_setup
    apk = tmp_path / "game.apk"
    shutil.copy(artifact, apk)
    fake = tmp_path / "bin" / "apksigner"
    fake.parent.mkdir()
    fake.write_text("#!/bin/sh\n"
                    "if [ \"$1\" = sign ]; then\n"
                    "  while [ $# -gt 1 ]; do if [ \"$1\" = --out ]; then OUT=$2; fi; shift; done\n"
                    "  cp \"$1\" \"$OUT\"; printf 'SIGNED' >> \"$OUT\"; exit 0; fi\n"
                    "echo 'Signer #1 certificate SHA-256 digest: " + "ab" * 32 + "'\n")
    fake.chmod(0o755)
    toml = tmp_path / "signer.toml"
    toml.write_text(f'keystore = "{cfg.keystore}"\nkey_alias = "upload"\nstore_password_file = "{cfg.store_password_file}"\n'
                    f'archive_dir = "{cfg.archive_dir}"\ntrusted_approvers = ["{cfg.trusted_approvers[0]}"]\n'
                    f'apksigner = "{fake}"\n')
    appr = tmp_path / "approval.json"
    appr.write_text(json.dumps(approval))
    rc = signer_mod.main(["--config", str(toml), "sign", "--artifact", str(apk), "--approval", str(appr),
                          "--out", str(tmp_path / "out")])
    assert rc == 0
    rec = json.loads(next(cfg.archive_dir.glob("*.json")).read_text())
    assert rec["tool"] == "apksigner" and rec["certificate_sha256"] == "ab" * 32
    assert signer_mod.main(["--config", str(toml), "sign", "--artifact", str(tmp_path / "pkg" / "test-report.json"),
                            "--approval", str(appr), "--out", str(tmp_path / "x")]) == signer_mod.EXIT_REFUSED


def test_signer_runs_as_a_separate_process(tmp_path):
    """The signer is its own entry point: it imports nothing from the orchestrator."""
    src = Path(signer_mod.__file__).read_text()
    assert "orchestrator" not in src.split('"""', 2)[2]
    p = subprocess.run([sys.executable, "-m", "forge.release.signer", "--help"], capture_output=True, text=True,
                       cwd=Path(__file__).resolve().parent.parent)
    assert p.returncode == 0 and "approved artifact" in p.stdout


# ------------------------------------------------------------------ store submission


class FakePlay:
    def __init__(self, version_code=1, fail_on=None):
        self.calls = []
        self.version_code = version_code
        self.fail_on = fail_on

    def __call__(self, method, url, headers, body):
        assert headers["Authorization"] == "Bearer tok"
        self.calls.append((method, url.split("/applications/")[1]))
        if self.fail_on and self.fail_on in url:
            return 500, {"error": "boom"}
        if url.endswith("/edits") and method == "POST":
            return 200, {"id": "edit-1"}
        if "uploadType=media" in url:
            return 200, {"versionCode": self.version_code}
        if "/tracks/" in url:
            return 200, json.loads(body)
        if url.endswith(":commit"):
            return 200, {"id": "edit-1"}
        if method == "DELETE":
            return 204, {}
        return 404, {}


@pytest.fixture
def authority_key(tmp_path):
    k = tmp_path / "keys" / "release-authority.key"
    generate_key(k)
    return k


def _signed_artifact(tmp_path):
    a = tmp_path / "signed.aab"
    a.write_bytes(b"signed-bytes")
    rec = {"package_id": "com.example.runeduel", "version_code": 1,
           "signed_sha256": hashlib.sha256(b"signed-bytes").hexdigest()}
    return a, rec


def test_store_submission_needs_separate_authorisation_and_matches_bytes(tmp_path, authority_key):
    art, rec = _signed_artifact(tmp_path)
    auth = authorise(SubmissionAuthorisation("com.example.runeduel", 1, rec["signed_sha256"], "internal",
                                             "completed", None, release_notes={"en-US": "First"}),
                     by="release-authority", key_path=authority_key)
    play = FakePlay()
    out = submit(auth, rec, art, GooglePlayClient(play, lambda: "tok"), trusted_authorities=[public_hex(authority_key)],
                 log_dir=tmp_path / "subs")
    assert [c[0] for c in play.calls] == ["POST", "POST", "PUT", "POST"]
    assert out["track"] == "internal" and (tmp_path / "subs").glob("*.json")
    # A staged rollout must state its fraction; a completed one must not.
    with pytest.raises(SubmissionRefused, match="fraction"):
        authorise(SubmissionAuthorisation("p", 1, "x", "production", "inProgress", None), by="a", key_path=authority_key)
    with pytest.raises(SubmissionRefused, match="unknown track"):
        authorise(SubmissionAuthorisation("p", 1, "x", "everyone", "completed", None), by="a", key_path=authority_key)
    # Different bytes, untrusted key or a mismatched archive record are refused before any API call.
    play2 = FakePlay()
    art.write_bytes(b"other")
    with pytest.raises(SubmissionRefused, match="not the authorised"):
        submit(auth, rec, art, GooglePlayClient(play2, lambda: "tok"), trusted_authorities=[public_hex(authority_key)],
               log_dir=tmp_path)
    with pytest.raises(SubmissionRefused, match="does not trust"):
        submit(auth, rec, art, GooglePlayClient(play2, lambda: "tok"), trusted_authorities=["22" * 32], log_dir=tmp_path)
    with pytest.raises(SubmissionRefused, match="signing archive"):
        submit(auth, dict(rec, version_code=2), art, GooglePlayClient(play2, lambda: "tok"),
               trusted_authorities=[public_hex(authority_key)], log_dir=tmp_path)
    assert play2.calls == []


def test_store_submission_abandons_the_edit_on_failure(tmp_path, authority_key):
    art, rec = _signed_artifact(tmp_path)
    auth = authorise(SubmissionAuthorisation("com.example.runeduel", 1, rec["signed_sha256"], "production",
                                             "inProgress", 0.2), by="ra", key_path=authority_key)
    play = FakePlay(version_code=5)
    with pytest.raises(SubmissionRefused, match="versionCode 5"):
        submit(auth, rec, art, GooglePlayClient(play, lambda: "tok"), trusted_authorities=[public_hex(authority_key)],
               log_dir=tmp_path)
    assert play.calls[-1][0] == "DELETE"
    play2 = FakePlay(fail_on="/tracks/")
    with pytest.raises(SubmissionRefused, match="500"):
        submit(auth, rec, art, GooglePlayClient(play2, lambda: "tok"), trusted_authorities=[public_hex(authority_key)],
               log_dir=tmp_path)
    assert play2.calls[-1][0] == "DELETE" and not any(c[1].endswith(":commit") for c in play2.calls)


def test_nothing_in_the_orchestrator_can_sign_or_publish():
    root = Path(__file__).resolve().parent.parent / "forge"
    for name in ("orchestrator.py", "runtime.py", "statemachine.py"):
        text = (root / name).read_text()
        assert "release.signer" not in text and "release.store" not in text
        assert "from .release" not in text and "import forge.release" not in text
