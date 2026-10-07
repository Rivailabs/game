"""CLI flows on a temporary project, seed files, preflight, leases."""

import json
import tomllib
from pathlib import Path

import pytest

from forge.cli import main
from forge.config import build_runtime, load_project_config
from forge.leases import LeaseManager
from forge.models import TaskState as S
from forge.preflight import run_preflight, write_manifest
from forge.store import Store
from forge.taskfile import TaskFileError, import_tasks, load_task_file
from forge.util import FakeClock

ROOT = Path(__file__).resolve().parent.parent
SEED_PROJECT = ROOT / "projects" / "astra-kingdoms" / "project.toml"
SEED_TASKS = ROOT / "projects" / "astra-kingdoms" / "tasks" / "pilot.toml"
PLAN = ROOT.parent / "docs" / "PLAN.md"


def test_seed_task_register_is_complete_and_verbatim():
    data = load_task_file(SEED_TASKS)
    tasks = data["tasks"]
    assert [int(t["ticket"]) for t in tasks] == list(range(1, 83))
    groups = {}
    for t in tasks:
        groups[t["group"]] = groups.get(t["group"], 0) + 1
    assert groups == {"Pilot": 10, "A": 10, "B": 5, "C": 11, "D": 6, "E": 6, "F": 8, "G": 8, "H": 8, "I": 10}
    if PLAN.exists():
        plan = PLAN.read_text()
        for t in tasks:
            assert f"| {t['ticket']}" in plan
            assert t["deliverable"] in plan, t["ticket"]
            assert t["acceptance"] in plan, t["ticket"]
    pilot = {t["ticket"]: t for t in tasks if t["group"] == "Pilot"}
    assert pilot["7"]["acceptance"].startswith("Ownership totals exactly 51,040 cells")
    assert pilot["9"]["depends_on"] == ["5", "6", "8"]
    assert all(t.get("milestone") == "v1" for t in tasks if t["group"] != "Pilot")


def test_seed_project_config_loads():
    cfg = load_project_config(SEED_PROJECT)
    assert cfg.project.id == "astra-kingdoms" and cfg.project.accepted_branch == "forge/accepted"
    assert Path(cfg.project.repo_path, "astra-kingdoms").is_dir()
    assert cfg.check_specs["rules"]["type"] == "dotnet_test"
    assert cfg.provider_specs["claude-api"]["builder_model"] == "claude-opus-5-5"
    assert cfg.provider_specs["claude-api"]["refusal_fallback"] is False
    assert cfg.provider_specs["claude-cli"]["type"] == "official_cli"
    assert {m.id for m in cfg.milestones} == {"pilot", "v1"}
    assert "game-forge/**" in cfg.project.protected_paths
    rt = build_runtime(cfg)
    assert set(rt.providers) == {"claude-api", "claude-cli"}
    assert rt.providers["claude-cli"].descriptor.supervised_only


def test_seed_import_all_draft(tmp_path):
    store = Store(tmp_path / "s.db")
    created, skipped = import_tasks(store, "astra-kingdoms", load_task_file(SEED_TASKS))
    assert len(created) == 82 and not skipped
    assert all(r.state == S.DRAFT for r in created)
    t9 = store.find_root_by_ticket("astra-kingdoms", "9")
    assert [d.root_id for d in t9.dependencies] == ["astra-kingdoms-t5", "astra-kingdoms-t6", "astra-kingdoms-t8"]
    assert t9.resource_profile.needs_device and t9.visual_review_required
    assert store.find_root_by_ticket("astra-kingdoms", "1").reservation_ceiling_micros == 45_000_000
    created2, skipped2 = import_tasks(store, "astra-kingdoms", load_task_file(SEED_TASKS))
    assert not created2 and len(skipped2) == 82  # idempotent, immutable root IDs


def test_task_file_errors(tmp_path):
    store = Store(tmp_path / "s.db")
    with pytest.raises(TaskFileError):
        import_tasks(store, "p", {"tasks": [{"ticket": "1", "title": "x", "depends_on": ["99"]}]})
    with pytest.raises(TaskFileError):
        import_tasks(store, "p", {"tasks": [{"title": "no ticket"}]})
    p = tmp_path / "t.json"
    p.write_text(json.dumps({"tasks": [{"ticket": "1", "title": "json task", "acceptance": "works"}]}))
    created, _ = import_tasks(store, "p", load_task_file(p))
    assert created[0].acceptance_cases[0].description == "works"


def write_project(tmp_path, repo):
    proj = tmp_path / "proj"
    (proj / "tasks").mkdir(parents=True)
    (proj / "project.toml").write_text(f"""
[project]
id = "demo"
name = "Demo"
repo_path = "{repo}"
workdir = "game"
accepted_branch = "forge/accepted"
protected_paths = ["game/tests/**"]

[policy]
allowed_vendors = ["fake"]

[budget]
project_cap_usd = 5
default_root_ceiling_usd = 1

[milestones.pilot]
name = "Pilot"
cap_usd = 2

[checks.mul]
type = "command"
argv = ["python3", "-m", "unittest", "discover", "-s", "game/tests", "-p", "test_mul.py"]

[providers.fake]
type = "fake"
vendor = "fake"
ceiling_usd = 0.05

[forge]
data_dir = "data"
backoff_s = 0
""")
    (proj / "tasks" / "t.toml").write_text("""
[defaults]
milestone = "pilot"
permitted_routes = ["fake"]
verification_checks = ["mul"]

[[tasks]]
ticket = "1"
title = "no-op task"
acceptance = "mul works"
permitted_paths = ["game/src/**"]
""")
    return proj


def test_cli_end_to_end(tmp_path, repo, capsys):
    proj = write_project(tmp_path, repo)
    pf = ["--project-file", str(proj / "project.toml")]
    main(pf + ["init"])
    main(pf + ["import", str(proj / "tasks" / "t.toml")])
    main(pf + ["status"])
    out = capsys.readouterr().out
    assert "imported 1 task(s) as DRAFT" in out and "DRAFT" in out
    main(pf + ["approve", "1"])
    assert "APPROVED" in capsys.readouterr().out
    main(pf + ["stop-dispatch", "--reason", "test"])
    main(pf + ["run"])
    capsys.readouterr()
    main(pf + ["status", "--json"])
    status = json.loads(capsys.readouterr().out)
    assert status["status"]["dispatch_stopped"] is True
    store = Store(proj / "data" / "forge.db")
    assert store.find_root_by_ticket("demo", "1").state == S.READY  # kill switch held dispatch
    main(pf + ["resume-dispatch"])
    main(pf + ["run"])
    # FakeProvider from config writes nothing -> no candidate -> attempts fail -> FAILED (honest)
    assert store.find_root_by_ticket("demo", "1").state == S.FAILED
    main(pf + ["budget", "show"])
    main(pf + ["budget", "set", "pilot", "3"])
    out = capsys.readouterr().out
    assert "milestone:pilot" in out and "$3.0000" in out
    main(pf + ["ledger", "add-expense", "--date", "2026-10-01", "--vendor", "Anthropic", "--invoice", "I1",
               "--cash", "1000", "--fees", "180", "--allocation", '{"astra": 0.5, "forge": 0.5}'])
    main(pf + ["ledger", "hours", "--date", "2026-10-01", "--hours", "2.5"])
    main(pf + ["ledger", "report"])
    out = capsys.readouterr().out
    rep = json.loads(out[out.index("{"):])
    assert rep["cash"]["combined_cash_minor"] == 118_000 and rep["hours"]["total"] == 2.5
    main(pf + ["events", "1", "--limit", "5"])
    assert "root_created" in capsys.readouterr().out


def test_preflight_manifest_is_honest(tmp_path):
    m = run_preflight(tmp_path, unity_editor=str(tmp_path / "no-unity"))
    assert m.tools["git"].found and m.tools["git"].verified
    assert m.tools["unity"].verified is False
    assert any("Unity licence" in n for n in m.not_verified)
    if not m.unity_editor_path:
        assert not m.compatible and any("Unity missing" in n for n in m.compatibility_notes)
    path = write_manifest(m, tmp_path)
    data = json.loads(path.read_text())
    assert data["profile"] == "linux-x86_64-ubuntu-lts" and "not_verified" in data


def test_leases_expiry_heartbeat_and_exclusivity(tmp_path):
    clock = FakeClock()
    store = Store(tmp_path / "l.db", clock=clock)
    lm = LeaseManager(store, default_ttl=60)
    assert lm.acquire("gpu:0", "w1")
    assert not lm.acquire("gpu:0", "w2")  # one generation job per GPU
    assert lm.acquire("workspace:/a", "w2")
    assert not lm.acquire("workspace:/a", "w1")  # one Unity build per workspace
    clock.advance(50)
    assert lm.heartbeat("gpu:0", "w1")
    clock.advance(50)
    assert not lm.acquire("gpu:0", "w2")  # heartbeat extended the lease
    clock.advance(61)
    assert lm.acquire("gpu:0", "w2")  # expired -> takeover recorded
    assert store.events(type_="lease_expired_takeover")
    assert not lm.heartbeat("gpu:0", "w1")
    assert lm.acquire("workspace:/a", "w2")  # renew (it expired while the clock advanced)
    assert not lm.acquire_all(["device:S1", "workspace:/a"], "w3")
    assert lm.holder("device:S1") is None  # all-or-nothing
    lm.release("gpu:0", "w2")
    assert lm.holder("gpu:0") is None
