"""R2 end to end through the orchestrator with fakes: a prop and an archer (with the required clips)
move through every lane stage and owner gate into accepted main; failures, rejections, refusals and
recovery behave as the plan requires. No network, GPU, Blender or Unity is used."""

from __future__ import annotations

import dataclasses
import json
import sys
from pathlib import Path

import pytest

from forge.assets.adapters import ImageAPIAdapter, LicensedLibraryAdapter, MeshyAdapter
from forge.assets.blender_tools import BlenderAssetTools
from forge.assets.clips import ARCHER_CLIPS, REQUIRED_ARCHER_CLIPS
from forge.assets.generation import AssetsConfig, GenerationLane
from forge.assets.lane import AssetLane, Stage, StageStatus
from forge.assets.registry import AssetRegistry
from forge.assets.routes import LicenceGate
from forge.assets.skeleton import ARCHER_JOINTS
from forge.assets.unity_prefab import UnityPrefabStep
from forge.budget import BudgetLedger
from forge.checks import CommandCheck
from forge.checks.base import ProcResult
from forge.models import (
    AcceptanceCase,
    EvidenceClass,
    ProjectPolicy,
    ReviewAction,
    TaskState as S,
    TaskType,
    ToolchainManifest,
)
from forge.orchestrator import Orchestrator
from forge.runtime import ProjectRuntime
from forge.web import ReviewApp

from .asset_fakes import FakeBlender, FakeTransport, sequence, tech_report
from .conftest import git
from .test_web import call

KEY = "msy_test_key_abcdefghijklmnop"
PNG = b"\x89PNG\r\n\x1a\n" + b"\x00" * 32

CRATE_BRIEF = {
    "id": "supply_crate", "kind": "prop", "title": "Supply crate", "intended_screen_size": "about 6% of screen height",
    "art_reference": [{"description": "owner sketch of a rope-bound wooden crate", "rights": "owner-created"}],
    "silhouette": "squat box with rope bands", "scale_m": 0.8, "materials": "one wood material", "max_triangles": 3000,
    "max_texture_px": 512, "prompt": "a rope-bound wooden supply crate, hand-crafted, pre-modern", "collider": "box",
    "catalogue_first": False,
    "skip_stages": ["concept", "concept_approval", "rig", "deformation_review", "motion_retarget",
                    "contact_transition_review"],
}

ARCHER_BRIEF = {
    "id": "astra_archer", "kind": "character", "title": "Base archer",
    "intended_screen_size": "about 18% of screen height at the gameplay camera",
    "art_reference": [{"description": "owner mood board: original mortal archer, Indian-epic fantasy textiles",
                       "rights": "owner-created"}],
    "silhouette": "readable bow arm and quiver, flowing sash", "scale_m": 1.8, "scale_axis": "height",
    "materials": "at most two materials: body and cloth", "budget": "archer",
    "attachment_points": ["bow_grip_socket", "string_hand_socket", "arrow_nock_socket", "quiver_socket"],
    "animation_requirements": list(REQUIRED_ARCHER_CLIPS), "collider": "capsule",
    "prompt": "an original mortal fantasy archer in a neutral A-pose, Indian-epic fantasy textiles",
    "catalogue_first": False, "separate_assets": ["bow", "arrow"], "procedural_parts": ["bowstring"],
}


def meshy_routes(model_id="t-1"):
    done = {"id": model_id, "status": "SUCCEEDED", "progress": 100,
            "model_urls": {"glb": f"https://assets.meshy.ai/{model_id}/model.glb"},
            "thumbnail_url": f"https://assets.meshy.ai/{model_id}/thumb.png"}
    return {
        ("POST", "https://api.meshy.ai/openapi/v2/text-to-3d"): {"result": model_id},
        ("POST", "https://api.meshy.ai/openapi/v1/image-to-3d"): {"result": model_id},
        ("GET", f"https://api.meshy.ai/openapi/v2/text-to-3d/{model_id}"): sequence(
            {"id": model_id, "status": "IN_PROGRESS", "progress": 50}, done),
        ("GET", f"https://api.meshy.ai/openapi/v1/image-to-3d/{model_id}"): done,
        ("POST", "https://api.meshy.ai/openapi/v1/rigging"): {"result": "rig-1"},
        ("GET", "https://api.meshy.ai/openapi/v1/rigging/rig-1"): {
            "status": "SUCCEEDED", "result": {"rigged_character_fbx_url": "https://assets.meshy.ai/rig-1/r.fbx"}},
        ("GET", "https://assets.meshy.ai/"): b"glTF-binary",
    }


def rig_report(opts) -> dict:
    joints = [{"name": j.name, "parent": j.parent, "deforming": j.deforming} for j in ARCHER_JOINTS]
    return {"triangles": 7400, "bones": len(joints), "deforming_bones": 38, "max_weights_per_vertex": 4,
            "weights_normalized": True, "unweighted_vertices": 0, "joints": joints, "notes": [],
            "render_names": ["rest_front", "deform_full_draw_front", "deform_crouch_side"]}


def motion_report(opts) -> dict:
    clip = opts["clip_name"]
    spec = ARCHER_CLIPS[clip]
    dur = round((spec.min_s + spec.max_s) / 2, 3)
    return {"clip_name": clip, "frame_rate": float(opts["fps"]), "duration_s": dur,
            "root_motion": opts["root_motion"], "loop_policy": opts["loop_policy"],
            "loop_seam_error": 0.004 if spec.loop else None, "root_horizontal_travel_m": 0.0,
            "contacts": [{"name": c, "start_s": 0.0, "end_s": dur} for c in spec.contacts],
            "events": [{"name": m, "time_s": round(dur * (i + 1) / (len(spec.markers) + 1), 3)}
                       for i, m in enumerate(spec.markers)],
            "markers_proposed": True, "cleanup_applied": ["retarget:mixamo"] +
            (["loop_cleanup"] if opts.get("loop_cleanup") else []) +
            (["root_motion_conversion"] if opts.get("root_motion_conversion") else []),
            "render_names": ["frame_00", "frame_01"]}


class FakeUnity:
    def __init__(self, ok=True):
        self.ok = ok
        self.calls = []

    def __call__(self, argv, cwd, timeout):
        self.calls.append(argv)
        manifest = json.loads(Path(argv[argv.index("-forgeManifest") + 1]).read_text())
        result = Path(argv[argv.index("-forgeResult") + 1])
        project = Path(argv[argv.index("-projectPath") + 1])
        prefab = project / manifest["prefab_path"]
        prefab.write_text("%YAML 1.1 fake prefab\n")
        result.write_text(json.dumps({"ok": self.ok, "prefab": manifest["prefab_path"], "errors": [] if self.ok else
                                      ["shader missing"], "counters": {"triangles": 1}}))
        return ProcResult(0, "", "", 1.0)


class LaneEnv:
    def __init__(self, env, tmp_path, *, adapters, blender_reports, routes=("meshy",), unity_ok=True, countries=("IN",),
                 brief=CRATE_BRIEF, unattended=True, device_check_code="print('FORGE_SCENARIO_RESULT: PASS')"):
        self.env, self.tmp = env, tmp_path
        env.project = env.project.model_copy(update={"policy": ProjectPolicy(
            allowed_vendors=["fake", "meshy", "configured"], intended_release_countries=list(countries),
            allowed_data_classes=["prompt", "code", "test_excerpt", "reference_image", "mesh", "render_image"],
            unattended_mode=unattended)})
        env.store.put_project(env.project)
        (env.repo / "briefs").mkdir(exist_ok=True)
        self.write_brief(brief)
        editor = tmp_path / "Unity" / "Editor" / "Unity"
        editor.parent.mkdir(parents=True)
        editor.write_text("#!/bin/sh\n")
        (env.repo / "game" / "unity" / "Assets").mkdir(parents=True, exist_ok=True)
        self.blender = FakeBlender(blender_reports)
        self.unity = FakeUnity(unity_ok)
        manifest = ToolchainManifest(generated_at=0, profile="test", unity_editor_path=str(editor))
        self.gl = GenerationLane(AssetsConfig(device_checks=["device-fake"], poll_interval_s=0),
                                 adapters, BlenderAssetTools("/fake/blender", runner=self.blender),
                                 UnityPrefabStep(manifest, runner=self.unity))
        checks = dict(env.checks)
        checks["device-fake"] = CommandCheck("device-fake", [sys.executable, "-c", device_check_code],
                                             evidence_class=EvidenceClass.DEVICE)
        rt = ProjectRuntime(project=env.project, data_dir=env.tmp / "data", checks=checks,
                            providers={"fake": env.provider}, worker_id="w1", transport_retries=2, backoff_s=1.0,
                            lease_ttl_s=300, generation_lane=self.gl, manifest=manifest,
                            unattended=unattended)
        self.orch = Orchestrator(env.store, rt)
        self.reg = AssetRegistry(env.store)
        for r in routes:
            self.reg.record_licence_gate(LicenceGate(r, f"{r} terms", "2026-09", "commercial use of outputs",
                                                     "IN only", False, "owner", 1.0))
            self.reg.record_certification(r, [{"check": "fake", "ok": True}], by="owner", version="test")

    def write_brief(self, brief):
        cat = {"id": brief["id"], "kind": "prop", "search_terms": ["x"], "size_m": brief["scale_m"],
               "max_tris": 3000, "max_texture": 512}
        (self.env.repo / "briefs" / f"{brief['id']}.json").write_text(json.dumps(cat))
        (self.env.repo / "briefs" / f"{brief['id']}.asset.json").write_text(json.dumps(brief))

    def task(self, ticket, brief_id, paths, routes, **kw):
        return self.env.task(ticket, task_type=TaskType.ASSET, asset_brief=f"briefs/{brief_id}.json",
                             permitted_paths=paths, permitted_routes=list(routes), verification_checks=[],
                             acceptance_cases=[AcceptanceCase(id="A1", description="asset approved at its gate",
                                                              evidence_class=EvidenceClass.HUMAN)],
                             reservation_ceiling_micros=5_000_000, **kw)

    def run(self, root_id):
        self.orch.run_until_idle()
        return self.env.store.get_root(root_id)

    def approve(self, root_id):
        cand = self.env.store.latest_attempt(root_id).candidate_hash
        self.orch.review(root_id, ReviewAction.APPROVE, candidate_hash=cand)
        return self.run(root_id)

    def lane(self, asset_id) -> AssetLane:
        lane = AssetLane.model_validate(self.reg.load_lane(asset_id))
        self.gl.sync_gates(lane)
        return AssetLane.model_validate(self.reg.load_lane(asset_id))


def crate_reports():
    return {"normalize": lambda o: tech_report(2500, materials=1, tex=512, size=(0.8, 0.6, 0.8), bones=0)}


@pytest.fixture
def crate(make_env, tmp_path):
    env = make_env()
    t = FakeTransport(meshy_routes())
    adapters = {"meshy": MeshyAdapter(lambda: KEY, transport=t,
                                      pricing=__import__("forge.assets.adapters", fromlist=["CreditPricing"])
                                      .CreditPricing(0.02, {"text_to_3d": 20, "image_to_3d": 30, "rig": 5}))}
    le = LaneEnv(env, tmp_path, adapters=adapters, blender_reports=crate_reports())
    le.transport = t
    return le


CRATE_PATHS = ["game/assets/source/supply_crate/**", "game/unity/Assets/Forge/Art/supply_crate/**"]


def test_prop_goes_through_generation_visual_gate_prefab_and_device_gate(crate):
    le, env = crate, crate.env
    t1 = le.task("P1", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t1.id)
    root = le.run(t1.id)
    assert root.state == S.AWAITING_APPROVAL, root.state_reason
    assert "visual approval of supply_crate v1" in root.state_reason
    att = env.store.latest_attempt(t1.id)
    files = git(env.repo, "show", "--name-only", "--format=", att.candidate_hash).splitlines()
    for f in ("game/assets/source/supply_crate/supply_crate.fbx", "game/assets/source/supply_crate/provenance.json",
              "game/assets/source/supply_crate/contract_report.json",
              "game/assets/source/supply_crate/review/visual_approval.json",
              "game/assets/source/supply_crate/technical_report.json"):
        assert f in files, f
    assert not any("/raw/" in f for f in files)  # provider GLB is an intermediate, kept in the artifact store
    post = next(c for c in le.transport.calls if c["method"] == "POST")
    assert post["json"]["target_polycount"] == 3000 and post["json"]["mode"] == "preview"
    # cost: Meshy reports none, so the reserved ceiling is charged as a flagged estimate
    res = BudgetLedger(env.store).reservations(root_id=t1.id)
    assert [(r["provider"], r["status"], r["settled_micros"]) for r in res] == [("meshy", "SETTLED", 400_000)]
    prov = json.loads(git(env.repo, "show", f"{att.candidate_hash}:game/assets/source/supply_crate/provenance.json"))
    gen = next(p for p in prov if p["stage"] == "generation")
    assert gen["route"] == "meshy" and gen["terms"][0]["name"] == "meshy terms"
    assert gen["territory"]["distribution_countries"] == ["IN"] and gen["provider_job_ids"] == ["t2d:t-1"]
    ev = next(e for e in env.store.list_evidence(t1.id) if e.name == "generation-lane")
    stage = ev.details["asset_stage"]
    assert stage["gate"] == "visual_approval" and set(stage["renders"]["after"]) == {"front", "side", "back",
                                                                                      "three_quarter"}
    assert stage["renders"]["before"]  # unmodified provider output rendered for comparison
    assert all(line["ok"] for line in stage["review"]["budget"])
    # the review page shows before/after renders, budgets and provenance for this gate
    app = ReviewApp(env.store, {"demo": le.orch})
    st, _, html = call(app, "GET", f"/task/{t1.id}")
    assert "Before (provider output, unmodified)" in html and "After (normalized, as it ships)" in html
    assert "Budgets (plan ceilings" in html and "Provenance" in html and "/artifact-image/" in html
    img = stage["renders"]["after"]["front"]
    out = {}
    body = b"".join(app({"REQUEST_METHOD": "GET", "PATH_INFO": f"/artifact-image/{img}", "QUERY_STRING": ""},
                        lambda status, headers: out.update(status=status)))
    assert out["status"].startswith("200") and body.startswith(b"\x89PNG")

    root = le.approve(t1.id)
    assert root.state == S.ACCEPTED
    assert le.lane("supply_crate").rec(Stage.VISUAL_APPROVAL).status == StageStatus.APPROVED

    t2 = le.task("P2", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t2.id)
    root = le.run(t2.id)
    assert root.state == S.AWAITING_APPROVAL, root.state_reason
    assert "device evidence" in root.state_reason
    manifest = json.loads(git(env.repo, "show", f"{env.store.latest_attempt(t2.id).candidate_hash}:"
                                                "game/unity/Assets/Forge/Art/supply_crate/forge_prefab_manifest.json"))
    assert manifest["format"] == "forge-prefab/1" and manifest["prefab_path"].endswith("supply_crate.prefab")
    assert le.unity.calls and "-executeMethod" in le.unity.calls[0]
    assert any(e.name == "device-fake" and e.status.value == "PASS" for e in env.store.list_evidence(t2.id))
    root = le.approve(t2.id)
    assert root.state == S.ACCEPTED
    lane = le.lane("supply_crate")
    assert lane.done(), lane.summary()
    assert "game/unity/Assets/Forge/Art/supply_crate/supply_crate.prefab" in git(
        env.repo, "ls-tree", "-r", "--name-only", "forge/accepted").splitlines()


def test_visual_rejection_reruns_generation_with_the_correction(crate):
    le, env = crate, crate.env
    t1 = le.task("P1", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t1.id)
    le.run(t1.id)
    cand = env.store.latest_attempt(t1.id).candidate_hash
    env.orch.review(t1.id, ReviewAction.REPAIR, candidate_hash=cand, correction="rope bands too thin to read")
    le.transport.routes[("POST", "https://api.meshy.ai/openapi/v2/text-to-3d")] = {"result": "t-1"}
    root = le.run(t1.id)
    assert root.state == S.AWAITING_APPROVAL
    posts = [c for c in le.transport.calls if c["method"] == "POST"]
    assert len(posts) == 2 and "Owner corrections: rope bands too thin to read" in posts[1]["json"]["prompt"]
    lane = le.lane("supply_crate")
    assert lane.corrections(Stage.VISUAL_APPROVAL) == ["rope bands too thin to read"]
    assert len([a for a in env.store.list_attempts(t1.id) if a.status.value != "ABANDONED"]) == 2


def test_contract_failure_pauses_with_incompatibility_report(make_env, tmp_path):
    env = make_env()
    from forge.assets.adapters import CreditPricing

    adapters = {"meshy": MeshyAdapter(lambda: KEY, transport=FakeTransport(meshy_routes()),
                                      pricing=CreditPricing(0.02, {"text_to_3d": 20}))}
    le = LaneEnv(env, tmp_path, adapters=adapters,
                 blender_reports={"normalize": lambda o: tech_report(4200, tex=2048, size=(0.8, 0.6, 0.8))})
    t1 = le.task("P1", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t1.id)
    root = le.run(t1.id)
    assert root.state == S.PAUSED
    assert "asset contract not met" in root.state_reason and "4200 triangles" in root.state_reason
    lane = le.lane("supply_crate")
    assert lane.rec(Stage.TECHNICAL_REPORT).status == StageStatus.FAILED
    assert lane.rec(Stage.GENERATION).status == StageStatus.DONE  # paid work is kept; resume does not regenerate


def test_route_refused_for_territory_goes_to_needs_input(make_env, tmp_path):
    env = make_env()
    from forge.assets.adapters import LocalModelAdapter

    adapters = {"hunyuan3d-2.1": LocalModelAdapter("hunyuan3d-2.1", argv=["true"])}
    le = LaneEnv(env, tmp_path, adapters=adapters, blender_reports=crate_reports(), routes=("hunyuan3d-2.1",),
                 countries=("IN", "DE"))
    t1 = le.task("P1", "supply_crate", CRATE_PATHS, ["hunyuan3d-2.1"])
    env.orch.approve_task(t1.id)
    root = le.run(t1.id)
    assert root.state == S.NEEDS_INPUT
    assert "licence excludes EU" in root.state_reason
    assert BudgetLedger(env.store).reservations(root_id=t1.id) == []  # nothing reserved or sent


def test_missing_blender_blocks_before_anything_is_spent(crate):
    le, env = crate, crate.env
    le.gl.blender = BlenderAssetTools(None)
    t1 = le.task("P1", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t1.id)
    root = le.run(t1.id)
    assert root.state == S.BLOCKED and "Blender not found" in root.state_reason
    assert le.transport.calls == []


def test_unknown_cost_is_refused_in_unattended_mode(make_env, tmp_path):
    env = make_env()
    t = FakeTransport(meshy_routes())
    le = LaneEnv(env, tmp_path, adapters={"meshy": MeshyAdapter(lambda: KEY, transport=t)},
                 blender_reports=crate_reports())
    t1 = le.task("P1", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t1.id)
    root = le.run(t1.id)
    assert root.state == S.PAUSED and "configure credit prices" in root.state_reason
    assert t.calls == []


def test_unity_absent_blocks_prefab_stage_honestly(crate):
    le, env = crate, crate.env
    t1 = le.task("P1", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t1.id)
    le.run(t1.id)
    le.approve(t1.id)
    le.gl.unity = UnityPrefabStep(None)
    t2 = le.task("P2", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t2.id)
    root = le.run(t2.id)
    assert root.state == S.PAUSED and "prefab assembly BLOCKED" in root.state_reason


def test_device_evidence_incomplete_never_reaches_the_gate(make_env, tmp_path):
    env = make_env()
    from forge.assets.adapters import CreditPricing

    adapters = {"meshy": MeshyAdapter(lambda: KEY, transport=FakeTransport(meshy_routes()),
                                      pricing=CreditPricing(0.02, {"text_to_3d": 20}))}
    le = LaneEnv(env, tmp_path, adapters=adapters, blender_reports=crate_reports(),
                 device_check_code="import sys; sys.exit(3)")
    t1 = le.task("P1", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t1.id)
    le.run(t1.id)
    le.approve(t1.id)
    t2 = le.task("P2", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t2.id)
    root = le.run(t2.id)
    assert root.state == S.PAUSED and "device evidence incomplete" in root.state_reason
    assert le.lane("supply_crate").rec(Stage.DEVICE_EVIDENCE).status == StageStatus.PENDING


def test_restart_mid_segment_repolls_instead_of_resubmitting(crate):
    le, env = crate, crate.env
    t1 = le.task("P1", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t1.id)
    real = le.gl.blender.normalize

    class Crash(BaseException):
        pass

    def crash(*a, **k):
        raise Crash()
    le.gl.blender.normalize = crash
    with pytest.raises(Crash):
        le.orch.run_until_idle()
    assert env.store.get_root(t1.id).state == S.RUNNING
    le.gl.blender.normalize = real
    le.orch.leases.release_for_holder("w1")  # the crashed worker's leases are gone
    w2 = Orchestrator(env.store, dataclasses.replace(le.orch.rt, worker_id="w2"))
    w2.run_until_idle()
    root = env.store.get_root(t1.id)
    assert root.state == S.PAUSED and "asset lane was interrupted" in root.state_reason
    w2.review(t1.id, ReviewAction.RESUME)
    w2.run_until_idle()
    assert env.store.get_root(t1.id).state == S.AWAITING_APPROVAL
    assert len([c for c in le.transport.calls if c["method"] == "POST"]) == 1  # generated once, paid once


# ------------------------------------------------------------------ the archer with the required clip set


@pytest.fixture
def archer(make_env, tmp_path):
    env = make_env()
    from forge.assets.adapters import CreditPricing

    img = __import__("base64").b64encode(PNG).decode()
    image_t = FakeTransport({("POST", "https://images.example.com/v1/generate"): {"images": [{"b64": img}]}})
    lib = tmp_path / "anims"
    lib.mkdir()
    items = []
    for clip in REQUIRED_ARCHER_CLIPS:
        (lib / f"{clip}.fbx").write_bytes(b"FBX " + clip.encode())
        items.append({"file": f"{clip}.fbx", "tags": [clip], "purpose": clip})
    (lib / "library.json").write_text(json.dumps({"library": "Example Bow Pack", "licence": "commercial seat",
                                                  "licence_id": "RCPT-77", "items": items}))
    adapters = {
        "api-image-service": ImageAPIAdapter(lambda: "img_key_000000000", endpoint="https://images.example.com/v1/"
                                             "generate", vendor="configured", transport=image_t,
                                             pricing=CreditPricing(0.01, {"concept_image": 4})),
        "meshy": MeshyAdapter(lambda: KEY, transport=FakeTransport(meshy_routes()),
                              pricing=CreditPricing(0.02, {"text_to_3d": 20, "image_to_3d": 30, "rig": 5})),
        "stock-animation": LicensedLibraryAdapter("stock-animation", str(lib)),
    }
    reports = {
        "normalize": lambda o: tech_report(7200, materials=2, tex=1024, size=(0.7, 1.8, 0.4)) | {
            "attachments": [{"name": n, "parent": "hand_l"} for n in ARCHER_BRIEF["attachment_points"]],
            "pivot": "bottom_center"},
        "rig": rig_report, "motion": motion_report,
    }
    le = LaneEnv(env, tmp_path, adapters=adapters, blender_reports=reports, brief=ARCHER_BRIEF,
                 routes=("api-image-service", "meshy", "stock-animation"))
    le.gl.cfg.routes["meshy"] = {"mapping": str(Path(__file__).resolve().parents[1] / "forge" / "assets" / "data" /
                                                "retarget_mixamo_archer.json")}
    return le


ARCHER_PATHS = ["game/assets/source/astra_archer/**", "game/unity/Assets/Forge/Art/astra_archer/**"]


def test_archer_with_required_clips_through_every_gate(archer):
    le, env = archer, archer.env
    routes = ["api-image-service", "meshy", "stock-animation"]
    gates = []
    for i, expect in enumerate(["concept approval", "visual approval", "deformation review",
                                "contact transition review", "device evidence"]):
        t = le.task(f"A{i}", "astra_archer", ARCHER_PATHS, routes)
        env.orch.approve_task(t.id)
        root = le.run(t.id)
        assert root.state == S.AWAITING_APPROVAL, (expect, root.state_reason)
        assert expect in root.state_reason
        gates.append(expect)
        root = le.approve(t.id)
        assert root.state == S.ACCEPTED, root.state_reason
    lane = le.lane("astra_archer")
    assert lane.done(), lane.summary()
    tree = git(env.repo, "ls-tree", "-r", "--name-only", "forge/accepted").splitlines()
    base = "game/assets/source/astra_archer"
    for clip in REQUIRED_ARCHER_CLIPS:
        assert f"{base}/clips/{clip}.fbx" in tree, clip
    assert f"{base}/rig/retarget_mapping.json" in tree and f"{base}/concept/concept_0.png" in tree
    checks = json.loads(git(env.repo, "show", f"forge/accepted:{base}/clips/clip_checks.json"))
    assert all(v["ok"] for v in checks.values()) and set(checks) == set(REQUIRED_ARCHER_CLIPS)
    assert {e["name"] for e in checks["release"]["contract"]["events"]} == {"release"}
    manifest = json.loads(git(env.repo, "show", "forge/accepted:game/unity/Assets/Forge/Art/astra_archer/"
                                                "forge_prefab_manifest.json"))
    assert manifest["rig"] == {"animation_type": "Generic", "root": "root"}
    assert {c["name"] for c in manifest["clips"]} == set(REQUIRED_ARCHER_CLIPS)
    assert next(c for c in manifest["clips"] if c["name"] == "idle")["loop"] is True
    # the image-to-3D request used the approved concept
    meshy = le.gl.adapters["meshy"].transport
    i2d = next(c for c in meshy.calls if c["url"].endswith("/image-to-3d"))
    assert i2d["json"]["image_url"].startswith("data:image/png;base64,")
    # provenance covers every stage; library clips carry the receipt id
    recs = le.reg.provenance("astra_archer")
    assert {r.stage for r in recs} >= {"concept", "generation", "normalization", "rig", "motion_retarget"}
    assert BudgetLedger(env.store).root_cost(env.store.find_root_by_ticket("demo", "A1").id) > 0


def test_archer_rig_over_budget_pauses_with_skeleton_report(archer):
    le, env = archer, archer.env
    le.blender.reports["rig"] = lambda o: rig_report(o) | {"max_weights_per_vertex": 8}
    routes = ["api-image-service", "meshy", "stock-animation"]
    for i in range(2):
        t = le.task(f"A{i}", "astra_archer", ARCHER_PATHS, routes)
        env.orch.approve_task(t.id)
        le.run(t.id)
        le.approve(t.id)
    t = le.task("A2", "astra_archer", ARCHER_PATHS, routes)
    env.orch.approve_task(t.id)
    root = le.run(t.id)
    assert root.state == S.PAUSED and "skeleton contract not met" in root.state_reason
    assert "8 weights per vertex > budget 4" in root.state_reason


def test_brief_change_after_work_started_needs_a_revision(crate):
    le, env = crate, crate.env
    t1 = le.task("P1", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t1.id)
    le.run(t1.id)
    le.approve(t1.id)
    changed = dict(CRATE_BRIEF, silhouette="tall barrel")
    le.write_brief(changed)
    t2 = le.task("P2", "supply_crate", CRATE_PATHS, ["meshy"])
    env.orch.approve_task(t2.id)
    root = le.run(t2.id)
    assert root.state == S.NEEDS_INPUT and "brief changed" in root.state_reason
