"""R3 planning: template, backlog, traceability, bounded parallelism, scaffolding, and the end-to-end
intake -> plan -> traceability flow for the second sample game (Rune Duel) with FakeProvider."""

from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path

import pytest

from forge.intake import SpecRepository
from forge.intake.model_pass import run_model_pass
from forge.models import ProjectPolicy, TaskState
from forge.planning.parallel import Capacity, SchedTask, ScheduleError, from_backlog, paths_overlap, schedule
from forge.planning.planner import PlanningError, change_impact, critical_path, plan_backlog, route_requirement
from forge.planning.scaffold import ScaffoldError, generate, namespace_for
from forge.planning.template import TemplateError, load_template
from forge.planning.traceability import build_matrix
from forge.providers import FakeProvider
from forge.store import Store
from forge.taskfile import import_tasks

ROOT = Path(__file__).resolve().parent.parent
RUNE = ROOT / "templates" / "turn-duel-2p" / "examples" / "rune-duel-brief.md"


@pytest.fixture
def template():
    return load_template("turn-duel-2p")


@pytest.fixture
def approved(tmp_path, template):
    repo = SpecRepository(tmp_path / "specs", "rune-duel", template)
    spec, _ = repo.ingest(RUNE)
    return repo, repo.approve(spec.version, by="owner")


def test_template_manifest_is_consistent(template):
    ids = [m.id for m in template.modules]
    assert {"rules", "match_state", "replay", "menu", "results", "rematch"} <= set(ids)
    order = [m.id for m in template.module_order()]
    for m in template.modules:
        for d in m.depends_on:
            assert order.index(d) < order.index(m.id)
    assert template.info["players_min"] == template.info["players_max"] == 2


def test_template_rejects_cycles(tmp_path):
    src = (ROOT / "templates" / "turn-duel-2p" / "template.toml").read_text()
    bad = src.replace('id = "rules"\nname = "Shared rules library"\nmilestone = "M1"\ndepends_on = []',
                      'id = "rules"\nname = "Shared rules library"\nmilestone = "M1"\ndepends_on = ["replay"]')
    assert bad != src
    (tmp_path / "template.toml").write_text(bad)
    with pytest.raises(TemplateError, match="cycle"):
        load_template(tmp_path)


def test_planning_requires_an_approved_spec(tmp_path, template):
    repo = SpecRepository(tmp_path / "s", "g", template)
    spec, _ = repo.ingest(RUNE)
    with pytest.raises(PlanningError, match="approve it"):
        plan_backlog(spec, template)
    preview = plan_backlog(spec, template, allow_preview=True)
    assert preview.preview and "PREVIEW" in preview.markdown()
    with pytest.raises(PlanningError, match="preview"):
        preview.to_task_file()


def test_routing_is_deterministic_and_keyword_based(approved, template):
    _, spec = approved
    by_text = {r.effective_text: route_requirement(r, template) for r in spec.plannable()}
    assert by_text["Each player has 20 seconds to choose a rune."] == "match_state"
    assert by_text[next(t for t in by_text if t.startswith("Every match can be replayed"))] == "replay"
    assert by_text[next(t for t in by_text if t.startswith("After the results screen"))] == "rematch"
    assert by_text["Every button has a touch target of at least 48 by 48 dp."] == "ui"
    assert by_text[next(t for t in by_text if t.startswith("No rune type should win"))] == "balance"
    # "both" must not match the bot keyword.
    assert by_text[next(t for t in by_text if t.startswith("In each round both players"))] == "rules"


def test_backlog_dependencies_estimates_and_checkpoints(approved, template):
    repo, spec = approved
    b = plan_backlog(spec, template, repo.acceptance_cases())
    tickets = [t.ticket for t in b.tasks]
    assert tickets.index("M1-rules") < tickets.index("M1-match_state") < tickets.index("M1-replay")
    assert b.task("M2-rematch").depends_on == ["M2-results", "M1-match_state"]
    release = b.task("M3-release")
    assert set(release.depends_on) == set(tickets) - {"M3-release"}
    assert {"release_approval", "store_submission"} <= set(release.checkpoints)
    assert "visual_approval" in b.task("M2-ui").checkpoints
    for t in b.tasks:
        assert t.estimate_hours[0] <= t.estimate_hours[1] and t.estimate_cost_usd[0] <= t.estimate_cost_usd[1]
    (hl, hh), (cl, ch) = b.totals()
    assert hl < hh and cl < ch
    m2 = next(m for m in b.milestones if m.id == "M2")
    assert {"milestone_approval", "visual_approval", "device_play"} == set(m2.checkpoints)
    length, path = critical_path(b)
    assert path[0] == "M1-rules" and path[-1] == "M3-release" and length > 0
    md = b.markdown()
    assert "Estimate range" in md and "M3-release" in md


def test_end_to_end_rune_duel_intake_plan_trace_import(tmp_path, template):
    """R3 exit-gate slice (software side): a second brief in the template traces to checks."""
    provider = FakeProvider(review_verdict="approve")
    policy = ProjectPolicy(allowed_vendors=["fake"], allowed_data_classes=["prompt", "code", "design_document"])
    repo = SpecRepository(tmp_path / "specs", "rune-duel", template)
    spec, report = repo.ingest(RUNE, model_pass=lambda s, t: run_model_pass(s, t, provider, policy).questions)
    assert len(provider.reviews) == 1 and spec.open_blocking() == []
    spec = repo.approve(spec.version, by="owner")
    cases = repo.acceptance_cases()
    backlog = plan_backlog(spec, template, cases)
    matrix = build_matrix(spec, backlog, cases, superseded=repo.superseded_cases())
    assert matrix.complete, matrix.summary()
    assert all(r.traced for r in matrix.rows)
    assert {"M2-menu", "M2-results"} >= set(matrix.template_only_tasks)
    csv_text = matrix.to_csv()
    assert csv_text.splitlines()[0].startswith("requirement,kind,status,tickets")

    # The exported task file is the existing structured format: every task imports as DRAFT.
    store = Store(tmp_path / "forge.db")
    data = json.loads(json.dumps(backlog.to_task_file()))
    created, skipped = import_tasks(store, "rune-duel", data)
    assert len(created) == len(backlog.tasks) and not skipped
    assert all(r.state == TaskState.DRAFT for r in created)
    rules_root = store.find_root_by_ticket("rune-duel", "M1-rules")
    assert rules_root.reservation_ceiling_micros == int(backlog.task("M1-rules").estimate_cost_usd[1] * 1_000_000)
    assert {c.id for c in rules_root.acceptance_cases} == {c["id"] for c in backlog.task("M1-rules").acceptance_cases}
    live = build_matrix(spec, backlog, cases, store=store)
    assert all(set(r.root_states.values()) == {"DRAFT"} for r in live.rows)
    assert not any(r.verified for r in live.rows)  # traced is not verified


def test_traceability_reports_gaps(approved, template):
    repo, spec = approved
    b = plan_backlog(spec, template, repo.acceptance_cases())
    victim = b.task("M2-audio")
    victim.checks = []
    b.task("M1-rules").acceptance_cases.append({"id": "AC-R999", "description": "[R999] ghost", "evidence_class": "rules"})
    m = build_matrix(spec, b, repo.acceptance_cases())
    assert not m.complete
    assert set(m.requirements_without_check) == set(victim.requirement_ids)
    assert m.orphan_cases == ["AC-R999"]
    assert "Requirements without a check" in m.markdown()


def test_change_impact_lists_tasks_needing_new_roots(approved, template):
    repo, spec = approved
    b = plan_backlog(spec, template, repo.acceptance_cases())
    timer = next(r.id for r in spec.plannable() if "20 seconds" in r.effective_text)
    assert change_impact(b, [timer]) == ["M1-match_state"]


# ------------------------------------------------------------------ bounded parallelism


def test_schedule_respects_parallel_limit_resources_paths_and_deps():
    tasks = [
        SchedTask("a", 4, resources={"workspace": 1}, paths=["src/A/**"]),
        SchedTask("b", 2, resources={"workspace": 1, "unity": 1}, paths=["unity/x/**"]),
        SchedTask("c", 2, resources={"workspace": 1, "unity": 1}, paths=["unity/y/**"]),
        SchedTask("d", 1, depends_on=["a"], resources={"workspace": 1}, paths=["src/A/sub*"]),
        SchedTask("e", 3, resources={"workspace": 1}, paths=["src/A/other/**"]),
    ]
    s = schedule(tasks, Capacity(max_parallel=3, resources={"workspace": 3, "unity": 1}))
    assert s.peak_parallel <= 3
    b, c = s.slot("b"), s.slot("c")
    assert b.end <= c.start or c.end <= b.start  # one Unity at a time
    assert s.slot("d").start >= s.slot("a").end
    a, e = s.slot("a"), s.slot("e")
    assert a.end <= e.start or e.end <= a.start  # overlapping paths never run together
    assert any("path overlap" in w for w in s.waits["e"]) or any("path overlap" in w for w in s.waits.get("a", []))
    assert s.makespan < s.serial_hours


def test_schedule_errors():
    with pytest.raises(ScheduleError, match="unknown"):
        schedule([SchedTask("a", 1, depends_on=["zz"])], Capacity())
    with pytest.raises(ScheduleError, match="cycle"):
        schedule([SchedTask("a", 1, depends_on=["b"]), SchedTask("b", 1, depends_on=["a"])], Capacity())
    with pytest.raises(ScheduleError, match="capacity"):
        schedule([SchedTask("a", 1, resources={"gpu": 2})], Capacity(resources={"gpu": 1}))


def test_paths_overlap():
    assert paths_overlap(["src/Match/**"], ["src/Match/Rematch*"])
    assert not paths_overlap(["unity/Assets/Scripts/UI/Menu/**"], ["unity/Assets/Scripts/UI/Results/**"])


def test_backlog_schedule_is_bounded(approved, template):
    repo, spec = approved
    b = plan_backlog(spec, template, repo.acceptance_cases())
    s1 = schedule(from_backlog(b), Capacity(max_parallel=1))
    s3 = schedule(from_backlog(b), Capacity(max_parallel=3, resources={"workspace": 3, "unity": 1, "device": 1}))
    assert s1.peak_parallel == 1 and s1.makespan == s1.serial_hours
    assert s3.makespan <= s1.makespan and s3.peak_parallel <= 3
    assert "Makespan" in s3.markdown()


# ------------------------------------------------------------------ scaffolding


def test_scaffold_generates_every_module_and_records_hashes(tmp_path, template):
    res = generate(template, tmp_path / "game", game_name="Rune Duel")
    out = tmp_path / "game"
    manifest = json.loads((out / "forge-template.json").read_text())
    assert manifest["template"] == "turn-duel-2p" and manifest["namespace"] == "RuneDuel"
    for mod, files in res.modules.items():
        for f in files:
            assert (out / f).exists(), (mod, f)
    cs = (out / "src" / "Rules" / "RulesLibrary.cs").read_text()
    assert "namespace RuneDuel.Rules" in cs and "__NAMESPACE__" not in cs
    assert (out / ".gitignore").exists()
    assert "__GAME_ID__" not in (out / "forge" / "project.toml").read_text()
    with pytest.raises(ScaffoldError, match="not empty"):
        generate(template, out, game_name="Rune Duel")
    assert namespace_for("9 lives!") == "Game9Lives"


def test_scaffold_ui_and_localization_lanes_pass(tmp_path, template):
    from forge.production.localization import check_assets
    from forge.production.ui import check_screens_file

    generate(template, tmp_path / "g", game_name="Rune Duel")
    g = tmp_path / "g"
    ui = check_screens_file(g / "design/ui/screens.json", g / "unity/Assets/Resources/Localization/en.txt")
    assert ui.status == "PASS", ui.findings
    loc = check_assets(g / "unity/Assets")
    assert loc.status == "PASS", loc.findings


@pytest.mark.dotnet
@pytest.mark.skipif(shutil.which("dotnet") is None, reason="dotnet SDK not installed")
def test_scaffold_sample_build_passes_with_real_dotnet(tmp_path, template):
    generate(template, tmp_path / "g", game_name="Rune Duel")
    p = subprocess.run(["dotnet", "test", "tests/Rules.Tests"], cwd=tmp_path / "g", capture_output=True, text=True,
                       timeout=900)
    assert p.returncode == 0, p.stdout[-2000:] + p.stderr[-2000:]
    assert "Passed!" in p.stdout
