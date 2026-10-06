"""Encrypted backup, verify, restore, the restore drill and retention enforcement."""

from __future__ import annotations

import io
import os

import pytest

from forge.backup import (
    BackupCryptoError,
    BackupError,
    BackupSources,
    RetentionPolicy,
    apply_retention,
    create_backup,
    load_passphrase,
    plan_retention,
    restore_backup,
    restore_drill,
    verify_backup,
)
from forge.backup.crypto import CHUNK, DecryptingReader, EncryptingWriter
from forge.backup.drill import LAST_DRILL_KEY
from forge.backup.retention import hold
from forge.models import ProjectPolicy, TaskState as S
from forge.providers import FakeBehaviour, FakeProvider

from .conftest import BAD_MUL, GOOD_MUL, git

PW = b"correct horse battery staple"
FAST = 11  # scrypt log2(N) for tests only (production default is 15)


def roundtrip(data: bytes, pw: bytes = PW) -> bytes:
    buf = io.BytesIO()
    w = EncryptingWriter(buf, pw, log2_n=FAST)
    w.write(data)
    w.close()
    return buf.getvalue()


def decrypt(blob: bytes, pw: bytes = PW) -> bytes:
    return DecryptingReader(io.BytesIO(blob), pw).read()


# ------------------------------------------------------------------ crypto


@pytest.mark.parametrize("size", [0, 10, CHUNK, CHUNK + 1, 2 * CHUNK + 5])
def test_stream_roundtrip(size):
    data = os.urandom(size)
    blob = roundtrip(data)
    assert data not in blob or size == 0
    assert decrypt(blob) == data


def test_wrong_passphrase_tamper_and_truncation_detected():
    blob = roundtrip(os.urandom(CHUNK + 100))
    with pytest.raises(BackupCryptoError, match="authentication failed"):
        decrypt(blob, b"another long passphrase")
    tampered = bytearray(blob)
    tampered[-5] ^= 1
    with pytest.raises(BackupCryptoError):
        decrypt(bytes(tampered))
    # drop the final chunk: the stream no longer ends with a "last" chunk
    first_len = int.from_bytes(blob[31:35], "big")
    with pytest.raises(BackupCryptoError, match="truncated"):
        decrypt(blob[:35 + first_len])
    with pytest.raises(BackupCryptoError, match="not a Forge backup"):
        decrypt(b"PK\x03\x04" + blob[4:])
    with pytest.raises(BackupCryptoError, match="at least 12"):
        roundtrip(b"x", b"short")


def test_passphrase_file_rules(tmp_path, monkeypatch):
    repo = tmp_path / "repo"
    repo.mkdir()
    inside = repo / "pw.txt"
    inside.write_text("a sufficiently long passphrase")
    os.chmod(inside, 0o600)
    with pytest.raises(BackupCryptoError, match="outside every project"):
        load_passphrase(str(inside), forbidden_roots=[repo])
    outside = tmp_path / "pw.txt"
    outside.write_text("a sufficiently long passphrase\n")
    os.chmod(outside, 0o644)
    with pytest.raises(BackupCryptoError, match="chmod 600"):
        load_passphrase(str(outside), forbidden_roots=[repo])
    os.chmod(outside, 0o600)
    assert load_passphrase(str(outside), forbidden_roots=[repo]) == b"a sufficiently long passphrase"
    monkeypatch.delenv("FORGE_BACKUP_PASSPHRASE", raising=False)
    with pytest.raises(BackupCryptoError, match="no backup passphrase"):
        load_passphrase(None)
    monkeypatch.setenv("FORGE_BACKUP_PASSPHRASE", "env passphrase value")
    assert load_passphrase(None) == b"env passphrase value"


# ------------------------------------------------------------------ backup / restore


def accepted_env(make_env, tmp_path):
    env = make_env()
    t = env.task()
    env.orch.approve_task(t.id)
    env.orch.run_until_idle()
    assert env.store.get_root(t.id).state == S.ACCEPTED
    return env, t


def sources(env, tmp_path, extra_config=()):
    cfg = tmp_path / "project.toml"
    cfg.write_text('[project]\nid = "demo"\n')
    return BackupSources(project_id="demo", data_dir=env.tmp / "data", repo_path=env.repo,
                         config_files=[cfg, *extra_config], credential_dirs=[tmp_path / "creds"])


def test_backup_verify_restore_round_trip_excludes_credentials(make_env, tmp_path):
    env, t = accepted_env(make_env, tmp_path)
    key = tmp_path / "anthropic.key"
    key.write_text("sk-ant-api03-abcdefghijklmnopqrstuvwxyz")
    leaky = tmp_path / "settings.toml"
    leaky.write_text("x-api-key: sk-ant-api03-zzzzzzzzzzzzzzzzzzzzzz\n")
    src = sources(env, tmp_path, [key, leaky])
    out = tmp_path / "b" / "demo.fgbk"
    out.parent.mkdir()
    m = create_backup(src, out, PW, log2_n=FAST)
    assert out.exists() and (out.parent / "demo.fgbk.sha256").exists()
    assert oct(out.stat().st_mode & 0o777) == "0o600"
    assert {x["reason"] for x in m.excluded} == {"credential-like file name", "content matches a secret pattern"}
    names = {f.path for f in m.files}
    assert {"db/forge.db", "repo/repo.bundle", "records/approvals_provenance.json", "config/project.toml"} <= names
    assert any(n.startswith("artifacts/") for n in names)
    assert m.accepted_head == git(env.repo, "rev-parse", "forge/accepted")
    assert m.counts["roots"] == 1 and m.counts["approvals"] == 1
    raw = out.read_bytes()
    assert b"SQLite format" not in raw and b"approvals" not in raw  # encrypted at rest
    with pytest.raises(BackupError, match="never overwritten"):
        create_backup(src, out, PW, log2_n=FAST)

    vr = verify_backup(out, PW)
    assert vr.ok, vr.problems
    assert vr.checked_files == len(m.files)
    assert not verify_backup(out, b"wrong but long passphrase").ok

    rr = restore_backup(out, PW, tmp_path / "restored")
    assert rr.ok, rr.problems
    assert (rr.data_dir / "forge.db").exists()
    assert git(rr.repo_mirror, "rev-parse", "refs/heads/forge/accepted") == m.accepted_head
    assert not any(p.name == "anthropic.key" for p in (tmp_path / "restored").rglob("*"))
    with pytest.raises(BackupError, match="not empty"):
        restore_backup(out, PW, tmp_path / "restored")


def test_verify_detects_modified_archive(make_env, tmp_path):
    env, _ = accepted_env(make_env, tmp_path)
    out = tmp_path / "x.fgbk"
    create_backup(sources(env, tmp_path), out, PW, log2_n=FAST)
    data = bytearray(out.read_bytes())
    data[len(data) // 2] ^= 0xFF
    out.write_bytes(bytes(data))
    vr = verify_backup(out, PW)
    assert not vr.ok
    assert any("sidecar" in p for p in vr.problems)


def test_restore_drill_passes_and_is_recorded(make_env, tmp_path):
    env, t = accepted_env(make_env, tmp_path)
    rep = restore_drill(sources(env, tmp_path), env.store, PW, work_dir=tmp_path, log2_n=FAST)
    assert rep.ok, rep.problems
    assert rep.checked["roots"] == 1 and rep.checked["artifacts_referenced"] > 0
    assert env.store.get_setting(LAST_DRILL_KEY) is not None
    ev = env.store.events(type_="restore_drill")
    assert ev and ev[-1]["payload"]["ok"] is True


def test_restore_drill_fails_when_artifacts_are_missing_from_the_backup(make_env, tmp_path, monkeypatch):
    env, t = accepted_env(make_env, tmp_path)
    src = sources(env, tmp_path)
    import forge.backup.archive as archive

    real_collect = archive._collect

    def lossy(sources, stage):
        items, excluded, counts, head = real_collect(sources, stage)
        return [(p, n) for p, n in items if not n.startswith("artifacts/")], excluded, counts, head

    monkeypatch.setattr(archive, "_collect", lossy)
    rep = restore_drill(src, env.store, PW, work_dir=tmp_path, log2_n=FAST)
    assert not rep.ok and any("missing from the restore" in p for p in rep.problems)
    assert env.store.events(type_="restore_drill")[-1]["payload"]["ok"] is False


# ------------------------------------------------------------------ retention

DAY = 86_400


def repaired_env(make_env):
    provider = FakeProvider({1: FakeBehaviour(files={"game/src/calc.py": BAD_MUL}),
                             2: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL})})
    env = make_env(provider=provider)
    t = env.task()
    env.orch.approve_task(t.id)
    env.orch.run_until_idle()
    assert env.store.get_root(t.id).state == S.ACCEPTED
    return env, t


def test_retention_deletes_failed_candidates_after_14_days_and_keeps_accepted(make_env):
    env, t = repaired_env(make_env)
    failed, accepted = env.store.list_attempts(t.id)
    policy = RetentionPolicy.from_project(ProjectPolicy())
    assert (policy.failed_candidates_days, policy.raw_diagnostics_days) == (14, 30)
    assert plan_retention(env.store, "demo", policy).delete == []  # nothing old yet
    env.clock.advance(15 * DAY)
    plan = plan_retention(env.store, "demo", policy)
    reasons = {i.reason for i in plan.delete}
    assert reasons == {"failed candidate older than 14 days"}
    assert {i.attempt_id for i in plan.delete} == {failed.id}
    assert [b["branch"] for b in plan.branches] == [failed.branch]
    accepted_refs = {d for e in env.store.list_evidence(t.id, accepted.id) for d in e.artifact_refs}
    assert not accepted_refs & {i.digest for i in plan.delete}
    export = env.tmp / "export"
    rep = apply_retention(env.store, env.orch.artifacts, plan, repo_path=env.repo, export_dir=export)
    assert rep.deleted and len(rep.exported) == len(rep.deleted) and rep.freed_bytes > 0
    for d in rep.deleted:
        assert not env.orch.artifacts.exists(d)
    for d in accepted_refs:
        assert env.orch.artifacts.exists(d)
    assert failed.branch not in git(env.repo, "branch", "--list", failed.branch)
    assert git(env.repo, "rev-parse", accepted.branch)  # accepted candidate branch kept
    assert env.store.events(type_="retention_applied")


def test_retention_raw_diagnostics_30_days_and_holds(make_env):
    env, t = repaired_env(make_env)
    accepted = env.store.list_attempts(t.id)[-1]
    evs = env.store.list_evidence(t.id, accepted.id)
    raw = {d for e in evs for d in (e.details.get("logs") or {}).values()}
    kept_forever = {d for e in evs for d in e.artifact_refs} - raw  # builder transcript, diff
    assert raw and kept_forever
    policy = RetentionPolicy()
    env.clock.advance(20 * DAY)  # failed candidate expired, accepted raw logs not yet
    plan = plan_retention(env.store, "demo", policy)
    assert not raw & {i.digest for i in plan.delete}
    assert all(plan.kept[d] == "raw diagnostic within retention" for d in raw)
    env.clock.advance(11 * DAY)  # 31 days
    hold(env.store, t.id, "owner", "defect investigation")
    plan = plan_retention(env.store, "demo", policy)
    assert plan.delete == [] and plan.held_roots == [t.id]
    from forge.backup.retention import release_hold

    release_hold(env.store, t.id, "owner")
    plan = plan_retention(env.store, "demo", policy)
    assert raw <= {i.digest for i in plan.delete}
    assert not kept_forever & {i.digest for i in plan.delete}
    assert "accepted result / release evidence / provenance" in set(plan.kept.values())


def test_retention_keeps_everything_of_unresolved_tasks(make_env):
    provider = FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": BAD_MUL})})
    env = make_env(provider=provider)
    t = env.task(max_attempts=3)
    env.orch.approve_task(t.id)
    env.orch.tick()
    env.orch.tick()
    root = env.store.get_root(t.id)
    assert root.state not in (S.ACCEPTED, S.FAILED, S.CANCELLED)
    env.clock.advance(60 * DAY)
    plan = plan_retention(env.store, "demo", RetentionPolicy())
    assert plan.delete == [] and plan.branches == []
    assert "unresolved task (not terminal)" in set(plan.kept.values())
