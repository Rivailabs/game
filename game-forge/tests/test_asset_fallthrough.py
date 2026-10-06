"""Catalogue first, generation lane only when the catalogue returns NONE (prop/weapon path)."""

from __future__ import annotations

import json

from forge.assets.adapters import CreditPricing, MeshyAdapter
from forge.assets.blender_tools import BlenderAssetTools
from forge.assets.generation import AssetsConfig, GenerationLane
from forge.assets.registry import AssetRegistry
from forge.assets.routes import LicenceGate
from forge.budget import BudgetLedger
from forge.models import TaskState as S, TaskType
from forge.providers import FakeProvider

from .asset_fakes import FakeBlender, FakeTransport, tech_report
from .catalogue_fakes import bow_brief, build_index, lane_config
from .test_asset_generation import CRATE_BRIEF, KEY, meshy_routes


def build(make_env, tmp_path):
    env = make_env(provider=FakeProvider(judge_pick="NONE", judge_cost_micros=4_000), checks={})
    index = tmp_path / "index.sqlite"
    build_index(index)
    pol = env.project.policy
    pol.allowed_data_classes += ["render_image", "reference_image", "mesh"]
    pol.allowed_vendors.append("meshy")
    pol.intended_release_countries = ["IN"]
    briefs = tmp_path / "briefs"
    briefs.mkdir()
    (briefs / "wooden_longbow.json").write_text(json.dumps(bow_brief().model_dump()))
    asset = dict(CRATE_BRIEF, id="wooden_longbow", kind="weapon", title="Wooden longbow", scale_m=1.8,
                 catalogue_first=True, attachment_points=["bow_string_top", "bow_string_bottom"], pivot="center")
    (briefs / "wooden_longbow.asset.json").write_text(json.dumps(asset))
    transport = FakeTransport(meshy_routes())
    blender = FakeBlender({"normalize": lambda o: tech_report(2400, tex=512, size=(1.8, 0.1, 0.3)) | {
        "attachments": [{"name": "bow_string_top"}, {"name": "bow_string_bottom"}]}})
    gl = GenerationLane(AssetsConfig(poll_interval_s=0),
                        {"meshy": MeshyAdapter(lambda: KEY, transport=transport,
                                               pricing=CreditPricing(0.02, {"text_to_3d": 20}))},
                        BlenderAssetTools("/fake/blender", runner=blender))
    env.orch.rt.catalogue = lane_config(tmp_path, index)
    env.orch.rt.generation_lane = gl
    gl.bind(env.orch)
    reg = AssetRegistry(env.store)
    reg.record_licence_gate(LicenceGate("meshy", "meshy terms", "2026-09", "commercial", "IN", False, "owner", 1.0))
    reg.record_certification("meshy", [{"check": "fake", "ok": True}], by="owner", version="t")
    root = env.task("B1", task_type=TaskType.ASSET, title="Wooden longbow",
                    asset_brief=str(briefs / "wooden_longbow.json"),
                    permitted_paths=["game/assets/source/wooden_longbow/**"], permitted_routes=["fake", "meshy"],
                    verification_checks=[])
    env.orch.approve_task(root.id)
    return env, root, gl, transport, blender


def test_catalogue_none_falls_through_to_the_generation_lane(make_env, tmp_path):
    env, root, gl, transport, blender = build(make_env, tmp_path)
    env.orch.run_until_idle()
    r = env.store.get_root(root.id)
    assert r.state == S.AWAITING_APPROVAL, r.state_reason
    assert "visual approval of wooden_longbow v1" in r.state_reason
    names = [e.name for e in env.store.list_evidence(root.id)]
    assert "catalogue-lane" in names and "generation-lane" in names and "asset-contract-check" in names
    assert "catalogue-budget-check" not in names
    # the judge call and the generation job are both in the ledger, each with its own reservation
    res = BudgetLedger(env.store).reservations(root_id=root.id)
    assert sorted(x["settled_micros"] for x in res) == [4_000, 400_000]
    norm = blender.calls[0]
    assert norm[norm.index("--auto-attachments") + 1] == "bow_string_top,bow_string_bottom"


def test_generation_preflight_runs_before_any_generation_spend(make_env, tmp_path):
    env, root, gl, transport, blender = build(make_env, tmp_path)
    gl.blender = BlenderAssetTools(None)
    env.orch.run_until_idle()
    r = env.store.get_root(root.id)
    assert r.state == S.PAUSED and "generation lane blocked" in r.state_reason and "Blender not found" in r.state_reason
    assert transport.calls == []  # nothing was sent to the generation provider
