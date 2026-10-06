"""Asset tasks in the orchestrator: catalogue first, generation only on NONE; review page; CLI; Claude judge."""

from __future__ import annotations

import io
import json
import socket
import sys
from pathlib import Path
from types import SimpleNamespace as NS

import pytest

from forge.budget import BudgetLedger
from forge.models import Decision, EvidenceClass, TaskState as S, TaskType
from forge.lanes.run_catalogue import EXIT_NONE, EXIT_PICKED, LaneResult
from forge.providers import FakeProvider, JudgeCandidate, JudgeRequest

from .catalogue_fakes import bow_brief, build_index, lane_config, png_bytes
from .conftest import git


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    def refuse(*a, **k):
        raise AssertionError("network access attempted")

    monkeypatch.setattr(socket.socket, "connect", refuse)
    monkeypatch.setattr(socket, "create_connection", refuse)
    monkeypatch.setitem(sys.modules, "objaverse", None)


def asset_env(make_env, tmp_path, judge_pick="first", allow_images=True, **cfg_kw):
    env = make_env(provider=FakeProvider(judge_pick=judge_pick, judge_cost_micros=4_000), checks={})
    index = tmp_path / "index.sqlite"
    build_index(index)
    env.orch.rt.catalogue = lane_config(tmp_path, index, **cfg_kw)
    if allow_images:
        env.orch.project.policy.allowed_data_classes.append("render_image")
    brief = tmp_path / "briefs" / "wooden_longbow.json"
    brief.parent.mkdir()
    brief.write_text(json.dumps(bow_brief().model_dump()))
    root = env.task("A1", task_type=TaskType.ASSET, title="Wooden longbow", asset_brief=str(brief),
                    permitted_paths=["game/assets/source/**"], verification_checks=[])
    return env, root


def test_asset_task_catalogue_pick_awaits_owner_approval_then_integrates(make_env, tmp_path):
    env, root = asset_env(make_env, tmp_path)
    env.orch.approve_task(root.id)
    env.orch.run_until_idle()
    r = env.store.get_root(root.id)
    assert r.state == S.AWAITING_APPROVAL, r.state_reason
    assert "owner approval of the catalogue pick" in r.state_reason and "licence" in r.state_reason
    att = env.store.latest_attempt(root.id)
    files = git(env.repo, "show", "--name-only", "--format=", att.candidate_hash).splitlines()
    assert "game/assets/source/wooden_longbow/credits.json" in files
    assert "game/assets/source/wooden_longbow/bow001.glb" in files
    evs = {e.name: e for e in env.store.list_evidence(root.id)}
    lane = evs["catalogue-lane"]
    assert lane.evidence_class == EvidenceClass.ASSET and lane.details["pick"] == "bow001"
    assert set(lane.details["renders"]) == {"front", "side", "back", "three_quarter"}
    assert evs["catalogue-budget-check"].status.value == "PASS"
    apr = env.store.list_approvals(root.id)[0]
    assert apr.technical_pass.decision == Decision.APPROVED and apr.visual_approval.decision == Decision.PENDING
    # judge cost went through the ledger
    res = BudgetLedger(env.store).reservations(root_id=root.id)
    assert len(res) == 1 and res[0]["settled_micros"] == 4_000 and res[0]["status"] == "SETTLED"
    # review page shows the pick, licence warning and renders
    from forge.web import ReviewApp

    app = ReviewApp(env.store, {"demo": env.orch})
    html = _get(app, f"/task/{root.id}")
    assert "Catalogue pick" in html and "bow001" in html and "CHECK LICENCE" in html and "/artifact-image/" in html
    digest = lane.details["renders"]["front"]
    assert _get(app, f"/artifact-image/{digest}", raw=True).startswith(b"\x89PNG")
    # owner approves the exact hash -> integrated into accepted main
    env.orch.review(root.id, "approve", candidate_hash=att.candidate_hash)
    env.orch.run_until_idle()
    r = env.store.get_root(root.id)
    if r.state == S.AWAITING_APPROVAL:  # merge produced a new hash: approve the integrated bytes
        env.orch.review(root.id, "approve", candidate_hash=env.store.latest_attempt(root.id).integrated_hash)
        env.orch.run_until_idle()
        r = env.store.get_root(root.id)
    assert r.state == S.ACCEPTED
    credits = json.loads(env.accepted_file("game/assets/source/wooden_longbow/credits.json"))
    assert credits[0]["uid"] == "bow001" and credits[0]["licence"] == "CC-BY"


def test_none_falls_through_to_needs_input_when_no_generation_lane(make_env, tmp_path):
    env, root = asset_env(make_env, tmp_path, judge_pick="NONE")
    env.orch.approve_task(root.id)
    env.orch.run_until_idle()
    r = env.store.get_root(root.id)
    assert r.state == S.NEEDS_INPUT and r.state_reason == "no catalogue match; generation lane not available"
    assert env.states(root.id)[-3:] == ["RUNNING", "PAUSED", "NEEDS_INPUT"]
    assert env.store.latest_attempt(root.id).status.value == "ABANDONED"
    assert BudgetLedger(env.store).reservations(root_id=root.id)[0]["settled_micros"] == 4_000
    assert not list((tmp_path / "objaverse-cache").rglob("*.glb"))


def test_generation_lane_runs_only_on_exit_2(make_env, tmp_path):
    calls = []

    def generation(root, target, brief):
        calls.append(brief.id)
        return LaneResult(EXIT_NONE, brief.id, "generation lane stub produced nothing")

    env, root = asset_env(make_env, tmp_path, judge_pick="NONE")
    env.orch.rt.generation_lane = generation
    env.orch.approve_task(root.id)
    env.orch.run_until_idle()
    assert calls == ["wooden_longbow"]
    r = env.store.get_root(root.id)
    assert r.state == S.NEEDS_INPUT and "generation lane stub" in r.state_reason
    types = [e["type"] for e in env.store.events(root_id=root.id)]
    assert "asset_generation_fallthrough" in types


def test_generation_lane_not_called_when_catalogue_picks(make_env, tmp_path):
    calls = []
    env, root = asset_env(make_env, tmp_path)
    env.orch.rt.generation_lane = lambda *a: calls.append(a) or LaneResult(EXIT_PICKED, "x", "should not run")
    env.orch.approve_task(root.id)
    env.orch.run_until_idle()
    assert calls == [] and env.store.get_root(root.id).state == S.AWAITING_APPROVAL


def test_preconditions_block_before_dispatch(make_env, tmp_path):
    env, root = asset_env(make_env, tmp_path, blender=None)
    env.orch.approve_task(root.id)
    env.orch.run_until_idle()
    r = env.store.get_root(root.id)
    assert r.state == S.BLOCKED and "Blender" in r.state_reason
    assert BudgetLedger(env.store).reservations(root_id=root.id) == []
    assert env.orch.rt.catalogue.downloader.calls == []


def test_disk_guard_blocks_asset_task(make_env, tmp_path):
    env, root = asset_env(make_env, tmp_path, free_gb=5)
    env.orch.approve_task(root.id)
    env.orch.run_until_idle()
    r = env.store.get_root(root.id)
    assert r.state == S.BLOCKED and "disk guard" in r.state_reason


def test_policy_without_render_image_blocks_judge(make_env, tmp_path):
    env, root = asset_env(make_env, tmp_path, allow_images=False)
    env.orch.approve_task(root.id)
    env.orch.run_until_idle()
    r = env.store.get_root(root.id)
    assert r.state == S.BLOCKED and "render_image" in r.state_reason


def test_asset_task_needs_brief_and_permitted_path(make_env, tmp_path):
    env, root = asset_env(make_env, tmp_path)
    t2 = env.task("A2", task_type=TaskType.ASSET, permitted_paths=["game/assets/source/**"])
    assert env.orch.approve_task(t2.id).state == S.NEEDS_INPUT
    t3 = env.task("A3", task_type=TaskType.ASSET, asset_brief=root.asset_brief, permitted_paths=["game/src/**"],
                  verification_checks=[])
    env.orch.approve_task(t3.id)
    env.orch.run_until_idle()
    r = env.store.get_root(t3.id)
    assert r.state == S.BLOCKED and "game/assets/source/wooden_longbow/**" in r.state_reason


def test_lane_blocked_while_running_pauses(make_env, tmp_path):
    from forge.checks.base import ProcResult

    runner = lambda argv, cwd, t: ProcResult(None, "", "gone", 0, missing_executable=True)  # noqa: E731
    env, root = asset_env(make_env, tmp_path, runner=runner)
    env.orch.approve_task(root.id)
    env.orch.run_until_idle()
    r = env.store.get_root(root.id)
    assert r.state == S.PAUSED and r.state_reason.startswith("catalogue lane blocked:")
    assert BudgetLedger(env.store).reservations(root_id=root.id)[0]["status"] in ("RELEASED", "SETTLED")


def test_crash_during_lane_recovers_to_paused_with_charge_pending(make_env, tmp_path):
    from forge.providers import SimulatedCrash

    def crash(req):
        raise SimulatedCrash("forge died while the judge call was in flight")

    env, root = asset_env(make_env, tmp_path, judge_pick=crash)
    env.orch.approve_task(root.id)
    with pytest.raises(SimulatedCrash):
        env.orch.run_until_idle()
    assert env.store.get_root(root.id).state == S.RUNNING
    assert not list((tmp_path / "objaverse-cache").rglob("*.glb"))  # cleanup ran in `finally`
    orch2 = env.make_orch("w2")
    orch2.rt.catalogue = env.orch.rt.catalogue
    env.clock.advance(301)  # the dead worker's lease expires
    orch2.run_until_idle()
    r = env.store.get_root(root.id)
    assert r.state == S.PAUSED and "charge pending" in r.state_reason
    assert BudgetLedger(env.store).reservations(root_id=root.id)[0]["status"] == "CHARGE_PENDING"


# ------------------------------------------------------------------ CLI

def test_cli_search_and_run_blocked_without_objaverse(tmp_path, capsys):
    from forge.cli import main

    index = tmp_path / "index.sqlite"
    build_index(index)
    brief = Path(__file__).resolve().parent.parent / "briefs" / "astra_v1" / "wooden_longbow.json"
    main(["catalogue", "search", str(brief), "--index", str(index), "--json"])
    out = json.loads(capsys.readouterr().out)
    assert [c["uid"] for c in out][:1] == ["bow001"]
    with pytest.raises(SystemExit) as ex:
        main(["catalogue", "run", str(brief), "--target", str(tmp_path / "t"), "--index", str(index),
              "--judge", "fake:first", "--blender", sys.executable])
    assert ex.value.code == 3
    res = json.loads(capsys.readouterr().out)
    assert res["status"] == "BLOCKED" and "objaverse" in res["reason"]
    with pytest.raises(SystemExit) as ex:
        main(["catalogue", "search", str(brief), "--index", str(tmp_path / "missing.sqlite")])
    assert ex.value.code == 3


def test_cli_run_paid_judge_requires_task(tmp_path, capsys, monkeypatch):
    from forge.cli import main

    index = tmp_path / "index.sqlite"
    build_index(index)
    brief = Path(__file__).resolve().parent.parent / "briefs" / "astra_v1" / "wooden_longbow.json"
    with pytest.raises(SystemExit) as ex:
        main(["catalogue", "run", str(brief), "--target", str(tmp_path / "t"), "--index", str(index)])
    assert ex.value.code == 3
    res = json.loads(capsys.readouterr().out)
    # the Astra policy does not allow render_image yet, so the default judge route is refused
    assert "render_image" in res["reason"] or "--task" in res["reason"]


# ------------------------------------------------------------------ Claude judge adapter

def test_claude_judge_sends_four_renders_and_parses_strict_json(tmp_path):
    from forge.credentials import CredentialBroker
    from forge.providers.claude_api import JUDGE_PROMPT, ClaudeAPIProvider

    calls = []

    class Msgs:
        def __init__(self, replies):
            self.replies = replies

        def create(self, **kw):
            calls.append(kw)
            text = self.replies.pop(0)
            return NS(id="m1", model="claude-opus-5-5", stop_reason="end_turn",
                      content=[NS(type="text", text=text)],
                      usage=NS(input_tokens=3000, output_tokens=60, cache_read_input_tokens=0,
                               cache_creation_input_tokens=0))

    client = NS(messages=Msgs(['{"pick": "u2", "reason": "best bow"}', '{"pick": "zzz", "reason": "x"}',
                               '{"pick": "NONE", "reason": "looks like a franchise prop"}']))
    p = ClaudeAPIProvider(project_id="p", broker=CredentialBroker(repo_roots=[str(tmp_path)]), client=client)
    imgs = {}
    for v in ("front", "side", "back", "three_quarter"):
        imgs[v] = tmp_path / f"{v}.png"
        imgs[v].write_bytes(png_bytes())
    req = JudgeRequest("wooden_longbow", "{}", [JudgeCandidate("u1", "Bow A", imgs), JudgeCandidate("u2", "Bow B", imgs)],
                       "Return NONE for anything resembling a recognisable game, film or brand asset.", "k1")
    r = p.judge(req)
    assert r.pick == "u2" and r.cost_micros > 0 and r.usage.requests == 1
    kw = calls[0]
    assert kw["system"] == JUDGE_PROMPT and "recognisable game, film" in JUDGE_PROMPT and "NONE" in JUDGE_PROMPT
    blocks = kw["messages"][0]["content"]
    images = [b for b in blocks if b["type"] == "image"]
    assert len(images) == 8 and all(b["source"]["media_type"] == "image/png" for b in images)
    assert kw["output_config"]["format"]["type"] == "json_schema"
    assert p.judge(req).pick == "NONE"  # unknown uid -> NONE
    assert p.judge(req).pick == "NONE"
    ceiling = p.judge_ceiling_micros(8)
    assert ceiling and ceiling > r.cost_micros


def _get(app, path, raw=False):
    environ = {"REQUEST_METHOD": "GET", "PATH_INFO": path, "QUERY_STRING": "", "CONTENT_LENGTH": "0",
               "wsgi.input": io.BytesIO(b"")}
    out = {}

    def start(status, headers):
        out["status"] = status

    data = b"".join(app(environ, start))
    assert out["status"].startswith("200"), out["status"]
    return data if raw else data.decode()
