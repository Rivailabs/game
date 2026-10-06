"""R4: setup assistant, downloads, updates/rollback, uninstall, privacy, licences, export."""

from __future__ import annotations

import hashlib
import io
import json
import shutil
import sqlite3
import subprocess
import tarfile
from pathlib import Path

import pytest

from forge.installer import updates as updates_mod
from forge.installer.downloads import (ChecksumMismatch, DownloadEntry, DownloadError, DownloadsManifest, TermsLedger,
                                       TermsNotAccepted, fetch_verified)
from forge.installer.export import export_project, verify_export
from forge.installer.licences import inventory, markdown as licence_md
from forge.installer.prereqs import assess
from forge.installer.privacy import (PrivacySettings, Telemetry, TelemetryRejected, collect_diagnostics,
                                     export_diagnostics, scrub)
from forge.installer.setup import SampleBuild, run_sample_build, run_setup
from forge.installer.uninstall import UninstallError, execute, plan_uninstall
from forge.installer.updates import Updater, UpdateBlocked, UpdateError
from forge.models import Project, RootTask, TaskState, ToolchainManifest, ToolStatus
from forge.release.keys import CryptoUnavailable, generate_key, public_hex, sign_record
from forge.store import Store

from .conftest import git


def _manifest(**over) -> ToolchainManifest:
    tools = {n: ToolStatus(name=n, found=True, path=f"/usr/bin/{n}", version="1.0", verified=True)
             for n in ("git", "dotnet", "adb", "python")}
    tools["unity"] = ToolStatus(name="unity", found=False, note="Unity editor not found")
    kw = dict(generated_at=0, profile="linux-x86_64-ubuntu-lts",
              os={"system": "Linux", "distribution_id": "ubuntu", "distribution": "Ubuntu 24.04 LTS"},
              cpu={"machine": "x86_64"}, disk_free_gb=120.0, tools=tools, devices=[])
    kw.update(over)
    return ToolchainManifest(**kw)


# ------------------------------------------------------------------ prerequisites and setup


def test_prerequisites_are_reported_not_concealed():
    rep = assess(_manifest(), provider_credentials={"claude-api": False})
    st = {i.id: i.status for i in rep.items}
    assert st["git"] == st["dotnet"] == "OK" and st["unity"] == "MISSING"
    assert st["unity_activation"] == "MISSING" and st["android_modules"] == "MISSING"
    assert st["phone"] == "MANUAL" and st["gpu"] == "OPTIONAL" and st["provider:claude-api"] == "MISSING"
    assert rep.by_id("unity_activation").manual_licence_decision and rep.by_id("unity").needs_account
    ready = rep.to_dict()["ready"]
    assert ready["rules_loop"] is True and ready["android_build"] is False and ready["device_evidence"] is False
    assert "needs your licence decision" in rep.markdown()


def test_unity_found_still_leaves_activation_manual_and_phone_ok(tmp_path):
    editor = tmp_path / "Hub" / "Editor" / "6000.0.1f1" / "Editor" / "Unity"
    (editor.parent / "Data" / "PlaybackEngines" / "AndroidPlayer").mkdir(parents=True)
    editor.write_text("")
    m = _manifest(unity_editor_path=str(editor), devices=[{"serial": "R58", "state": "device", "model": "Pixel",
                                                           "emulator": False}])
    m.tools["unity"] = ToolStatus(name="unity", found=True, path=str(editor), version="6000.0.1f1")
    rep = assess(m)
    st = {i.id: i.status for i in rep.items}
    assert st["unity"] == "OK" and st["android_modules"] == "OK" and st["phone"] == "OK"
    assert st["unity_activation"] == "MANUAL"  # Forge cannot see activation: never claims OK
    assert rep.ready_for("android_build") is False


def test_setup_runs_sample_build_first_and_writes_report(tmp_path):
    res = run_setup(tmp_path, manifest=_manifest(), sample=lambda: SampleBuild("PASS", "ok"))
    assert res.sample.status == "PASS"
    data = json.loads((tmp_path / "setup-report.json").read_text())
    assert "unity-editor" in data["manual_steps"]
    assert any("BLOCKED until Unity" in n for n in res.notes)
    blocked = run_setup(tmp_path, manifest=_manifest(), sample=lambda: SampleBuild("BLOCKED", "no dotnet"))
    assert any("sample build passes" in n for n in blocked.notes)
    assert "Manual steps" in blocked.markdown()


def test_sample_build_blocks_without_dotnet(tmp_path):
    sb = run_sample_build(tmp_path, dotnet="definitely-not-dotnet")
    assert sb.status == "BLOCKED" and "not installed" in sb.detail


@pytest.mark.dotnet
@pytest.mark.skipif(shutil.which("dotnet") is None, reason="dotnet SDK not installed")
def test_sample_build_passes_with_real_dotnet(tmp_path):
    sb = run_sample_build(tmp_path)
    assert sb.status == "PASS", sb.log_tail


# ------------------------------------------------------------------ downloads


class CopyFetcher:
    def __init__(self, files: dict[str, bytes]):
        self.files = files
        self.urls = []

    def fetch(self, url, dest):
        self.urls.append(url)
        dest.write_bytes(self.files[url])


def _entry(data: bytes, **over):
    kw = dict(name="tool", version="1.2.3", kind="tool", licence="MIT", terms_url="https://example.org/terms",
              url="https://example.org/tool-1.2.3.tar.gz", sha256=hashlib.sha256(data).hexdigest(),
              size_bytes=len(data))
    kw.update(over)
    return DownloadEntry(**kw)


def test_shipped_downloads_manifest_is_valid_and_honest():
    m = DownloadsManifest.load()
    assert m.automatic() == []  # no automatic downloads without recorded checksums
    names = {e.name for e in m.manual_steps()}
    assert {"unity-editor", "android-sdk-terms"} <= names
    unity = next(e for e in m.entries if e.name == "unity-editor")
    assert unity.account_required and "proprietary" in unity.licence


def test_downloads_verify_checksum_and_terms(tmp_path):
    data = b"payload"
    e = _entry(data, requires_acceptance=True)
    f = CopyFetcher({e.url: data})
    terms = TermsLedger(tmp_path / "terms.jsonl")
    with pytest.raises(TermsNotAccepted):
        fetch_verified(e, tmp_path / "dl", f, terms=terms)
    terms.accept(e, by="owner")
    out = fetch_verified(e, tmp_path / "dl", f, terms=terms)
    assert out.read_bytes() == data and out.name == "tool-1.2.3.tar.gz"
    assert fetch_verified(e, tmp_path / "dl", f, terms=terms) == out and len(f.urls) == 1  # cached by hash
    bad = _entry(b"other", name="bad", url="https://example.org/bad.tgz")
    with pytest.raises(ChecksumMismatch):
        fetch_verified(bad, tmp_path / "dl", CopyFetcher({bad.url: b"tampered"}))
    assert not list((tmp_path / "dl").glob(".dl-*")) and not (tmp_path / "dl" / "bad-1.2.3.tgz").exists()


def test_download_entries_need_exact_versions_checksums_and_terms(tmp_path):
    assert any("exact version" in p for p in _entry(b"x", version="latest").problems())
    assert any("sha256" in p for p in _entry(b"x", sha256="").problems())
    assert any("https" in p for p in _entry(b"x", url="http://insecure").problems())
    assert any("terms" in p for p in _entry(b"x", terms_url="").problems())
    with pytest.raises(DownloadError, match="manual step"):
        fetch_verified(_entry(b"x", delivery="manual"), tmp_path, CopyFetcher({}))
    (tmp_path / "m.json").write_text(json.dumps({"manifest_version": "1", "entries": [
        {"name": "x", "version": "latest", "kind": "tool", "licence": "MIT", "terms_url": "https://t"}]}))
    with pytest.raises(DownloadError):
        DownloadsManifest.load(tmp_path / "m.json")


# ------------------------------------------------------------------ updates


def _dist(version: str) -> bytes:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz") as tf:
        data = f"forge {version}\n".encode()
        info = tarfile.TarInfo("forge/VERSION")
        info.size = len(data)
        tf.addfile(info, io.BytesIO(data))
    return buf.getvalue()


@pytest.fixture
def update_env(tmp_path):
    key = tmp_path / "keys" / "updates.key"
    generate_key(key)
    dists = {f"https://updates.example.org/forge-{v}.tar.gz": _dist(v) for v in ("0.2.0", "0.3.0")}

    def manifest(version, **compat):
        url = f"https://updates.example.org/forge-{version}.tar.gz"
        body = {"channel": "stable", "version": version, "artifact": {"url": url,
                "sha256": hashlib.sha256(dists[url]).hexdigest()},
                "compatibility": {"python_min": "3.11", "schema_version": 1, "templates": {"turn-duel-2p": ["1.0.0"]},
                                  **compat}}
        return sign_record(body, key)

    store = Store(tmp_path / "data" / "forge.db")
    up = Updater(tmp_path / "install", trusted_keys=[public_hex(key)], store=store, db_path=tmp_path / "data" / "forge.db",
                 template_versions={"turn-duel-2p": "1.0.0"})
    return up, manifest, CopyFetcher(dists), store, key


def test_update_apply_backup_and_rollback(update_env):
    up, manifest, fetcher, store, _ = update_env
    r1 = up.apply(manifest("0.2.0"), fetcher, by="owner")
    assert up.current == "0.2.0" and r1["signature_verified"] and Path(r1["backup"]).exists()
    with sqlite3.connect(r1["backup"]) as con:
        assert con.execute("SELECT count(*) FROM sqlite_master").fetchone()[0] > 0
    up.apply(manifest("0.3.0", schema_version=2), fetcher, by="owner")
    assert up.current == "0.3.0" and up.previous == "0.2.0"
    assert (up.root / "versions" / "0.3.0" / "forge" / "VERSION").read_text() == "forge 0.3.0\n"
    rb = up.rollback(by="owner")
    assert up.current == "0.2.0" and up.previous == "0.3.0" and "NOT restored automatically" in rb["note"]
    assert set(up.installed_versions()) == {"0.2.0", "0.3.0"}  # previous version kept available
    assert [h["event"] for h in up.history()] == ["update_applied", "update_applied", "rollback"]
    with pytest.raises(UpdateError, match="already installed"):
        up.apply(manifest("0.3.0"), fetcher, by="owner")


def test_update_never_runs_mid_task(update_env):
    up, manifest, fetcher, store, _ = update_env
    store.put_project(Project(id="p", name="p", repo_path="."))
    store.create_root(RootTask(id="r1", project_id="p", milestone_id="m", ticket="7", title="t", state=TaskState.RUNNING))
    rep = up.compatibility(manifest("0.2.0"))
    assert not rep.ok and rep.active_tasks == ["7 (RUNNING)"]
    with pytest.raises(UpdateBlocked, match="mid-flight"):
        up.apply(manifest("0.2.0"), fetcher, by="owner")
    assert up.current is None and fetcher.urls == []


def test_update_rejects_tampered_or_untrusted_manifests(update_env, tmp_path):
    up, manifest, fetcher, _, _ = update_env
    m = manifest("0.2.0")
    with pytest.raises(UpdateError, match="rejected"):
        up.apply({**m, "version": "9.9.9"}, fetcher, by="owner")
    other = tmp_path / "other.key"
    generate_key(other)
    with pytest.raises(UpdateError, match="does not trust"):
        up.apply(sign_record({k: v for k, v in m.items() if k not in ("signature", "signer_public_key")}, other),
                 fetcher, by="owner")
    fetcher.files[m["artifact"]["url"]] = b"tampered"
    with pytest.raises(UpdateError, match="sha256"):
        up.apply(m, fetcher, by="owner")
    assert up.current is None


def test_update_checksum_only_mode_needs_explicit_consent(update_env, monkeypatch):
    up, manifest, fetcher, _, _ = update_env
    m = manifest("0.2.0")

    def no_crypto(*a, **k):
        raise CryptoUnavailable("cryptography missing")

    monkeypatch.setattr(updates_mod, "verify_record", no_crypto)
    with pytest.raises(UpdateBlocked, match="signature NOT verified"):
        up.apply(m, fetcher, by="owner")
    res = up.apply(m, fetcher, by="owner", allow_checksum_only=True)
    assert res["signature_verified"] is False and "NOT verified" in res["note"]


def test_compatibility_report_lists_blockers_and_migrations(update_env):
    up, manifest, *_ = update_env
    rep = up.compatibility(manifest("0.2.0", python_min="9.0", templates={"turn-duel-2p": ["2.0.0"]},
                                    disabled_adapters=["meshy"]))
    assert any("Python 9.0" in b for b in rep.blockers)
    assert any("template turn-duel-2p" in m for m in rep.migrations)
    assert any("meshy" in w for w in rep.warnings)
    assert "BLOCKED" in rep.markdown()


def test_update_refuses_unsafe_archives(update_env, tmp_path):
    up, manifest, fetcher, _, key = update_env
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz") as tf:
        info = tarfile.TarInfo("../escape")
        info.size = 1
        tf.addfile(info, io.BytesIO(b"x"))
    url = "https://updates.example.org/evil.tar.gz"
    fetcher.files[url] = buf.getvalue()
    body = {"channel": "stable", "version": "0.4.0", "artifact": {"url": url, "sha256":
            hashlib.sha256(buf.getvalue()).hexdigest()}, "compatibility": {}}
    with pytest.raises(UpdateError, match="unsafe path"):
        up.apply(sign_record(body, key), fetcher, by="owner")


# ------------------------------------------------------------------ uninstall


def test_uninstall_removes_app_keeps_projects_and_asks_for_caches(tmp_path):
    root = tmp_path / "install"
    (root / "versions" / "0.2.0").mkdir(parents=True)
    (root / "current").write_text("0.2.0\n")
    (root / "backups").mkdir()
    project = tmp_path / "projects" / "rune"
    project.mkdir(parents=True)
    data = tmp_path / "data"
    data.mkdir()
    cache = tmp_path / "cache"
    cache.mkdir()
    (cache / "blob").write_bytes(b"x" * 2048)
    plan = plan_uninstall(root, projects=[project], data_dirs=[data], caches=[cache])
    assert set(plan.remove) == {root / "versions", root / "current"}
    assert plan.optional and plan.optional[0][0] == cache
    assert "--remove-caches" in plan.markdown()
    with pytest.raises(UninstallError):
        execute(plan, confirm=False)
    execute(plan, confirm=True)
    assert not (root / "versions").exists() and project.exists() and data.exists() and cache.exists()
    assert (root / "backups").exists()
    # A cache inside a project is never removed even when asked.
    inside = project / ".cache"
    inside.mkdir()
    plan2 = plan_uninstall(root, projects=[project], data_dirs=[data], caches=[inside], remove_caches=True)
    assert inside not in plan2.remove


# ------------------------------------------------------------------ privacy


def test_telemetry_is_off_by_default_and_counts_only(tmp_path):
    t = Telemetry(tmp_path)
    assert not t.enabled and t.record("tasks_started") is False
    assert not (tmp_path / "telemetry-queue.json").exists()
    with pytest.raises(TelemetryRejected):
        t.record("prompt_text", 1)
    with pytest.raises(TelemetryRejected):
        t.record("tasks_started", "hello")  # type: ignore[arg-type]
    PrivacySettings(tmp_path).set_telemetry(True, by="owner")
    assert t.record("tasks_started") and t.record("check_duration_s", 12.5)
    payload = t.payload()
    assert payload["metrics"]["check_duration_s"] == {"count": 1, "sum": 12.5}
    assert set(payload) == {"install_id", "metrics", "schema"}
    sent = []
    assert t.send(sent.append) and sent[0]["metrics"]["tasks_started"]["count"] == 1
    PrivacySettings(tmp_path).set_telemetry(False, by="owner")
    assert t.send(sent.append) is False and len(sent) == 1


def test_diagnostics_are_redacted_previewed_and_confirmed(tmp_path, monkeypatch):
    monkeypatch.setenv("HOME", str(tmp_path / "home"))
    (tmp_path / "toolchain-manifest.json").write_text(json.dumps(
        {"path": str(tmp_path / "home" / "Unity"), "owner": "dev@example.com", "ip": "10.1.2.3"}))
    proj = tmp_path / "project.toml"
    proj.write_text('[providers.x]\napi_key = "sk-ant-abcdefghijklmnop"\nkey_file = "~/.config/k"\nmodel = "m"\n')
    store = Store(tmp_path / "forge.db")
    store.append_event("check_failed", root_id="r1", status="FAIL", prompt="SECRET PROMPT TEXT", code="int x;")
    b = collect_diagnostics(tmp_path, store=store, project_file=proj)
    text = b.preview()
    assert "SECRET PROMPT TEXT" not in text and "int x;" not in text and "sk-ant-" not in text
    assert "dev@example.com" not in text and "10.1.2.3" not in text and str(tmp_path / "home") not in text
    assert '"type": "check_failed"' in b.files["events.json"] and '"status": "FAIL"' in b.files["events.json"]
    assert 'model = "m"' in b.files["project.toml"] and "[REDACTED]" in b.files["project.toml"]
    with pytest.raises(PermissionError):
        export_diagnostics(b, tmp_path / "d.zip", confirm_digest="0" * 64)
    out = export_diagnostics(b, tmp_path / "d.zip", confirm_digest=b.digest)
    assert out.exists()
    assert scrub("Bearer abcdefghijklmnopqrstuvwxyz") == "[REDACTED]"


# ------------------------------------------------------------------ licences


def test_licence_inventory_separates_forge_from_everything_else():
    entries = inventory(toolchain=_manifest(), downloads=DownloadsManifest.load(), forge_version="0.1.0")
    by = {e.component: e for e in entries}
    assert by["game-forge"].licence == "Apache-2.0" and "Forge-owned code only" in by["game-forge"].note
    assert by["pydantic"].scope == "python-dependency" and by["pydantic"].licence != ""
    assert by["unity-editor"].scope == "download" and "proprietary" in by["unity-editor"].licence
    gen = next(e for e in entries if e.scope == "generated-output")
    assert "Not covered" in gen.licence
    assert "| game-forge |" in licence_md(entries)


# ------------------------------------------------------------------ export


def test_project_export_works_without_forge(tmp_path):
    repo = tmp_path / "repo"
    repo.mkdir()
    git(repo, "init", "-q", "-b", "main")
    git(repo, "config", "user.email", "t@example.com")
    git(repo, "config", "user.name", "T")
    (repo / "a.txt").write_text("accepted\n")
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "a")
    git(repo, "branch", "forge/accepted")
    from forge.artifacts import ArtifactStore
    from forge.models import Evidence, EvidenceClass, EvidenceStatus

    arts = ArtifactStore(tmp_path / "artifacts")
    digest = arts.put_text("test log: 3 passed")
    store = Store(tmp_path / "forge.db")
    store.put_project(Project(id="p", name="p", repo_path=str(repo)))
    store.create_root(RootTask(id="r1", project_id="p", milestone_id="m", ticket="1", title="t"))
    store.add_evidence(Evidence(id="e1", root_id="r1", evidence_class=EvidenceClass.RULES, status=EvidenceStatus.PASS,
                                name="rules", artifact_refs=[digest]))
    specs = tmp_path / "specs"
    specs.mkdir()
    (specs / "index.json").write_text("{}")
    m = export_project(project_id="p", repo_path=repo, branch="forge/accepted", db_path=tmp_path / "forge.db",
                       out_dir=tmp_path / "export", spec_dir=specs, artifacts_dir=tmp_path / "artifacts")
    out = tmp_path / "export"
    assert m["needs_forge"] is False and digest in m["artifacts"]
    assert (out / "artifacts" / digest).read_text() == "test log: 3 passed"
    roots = json.loads((out / "records" / "roots.json").read_text())
    assert roots[0]["id"] == "r1"
    assert verify_export(out) == []
    clone = tmp_path / "clone"
    subprocess.run(["git", "clone", "-q", "--branch", "forge/accepted", str(out / "source.bundle"), str(clone)],
                   check=True)
    assert (clone / "a.txt").read_text() == "accepted\n"
    (out / "EXPORT.md").write_text("changed")
    assert verify_export(out) == ["EXPORT.md: changed"]
