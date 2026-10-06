"""Blender script wrappers (fake Blender), report -> contracts, Unity prefab step, provenance inventory."""

from __future__ import annotations

import json
import py_compile
from pathlib import Path

import pytest

from forge.assets.blender_tools import (
    SCRIPTS,
    BlenderAssetTools,
    BlenderFailed,
    geometry_from_report,
    materials_from_report,
    motion_from_report,
    skeleton_from_report,
)
from forge.assets.clips import needs_cleanup
from forge.assets.contracts import (
    InputReference,
    ProvenanceRecord,
    TerritoryFlags,
    Transformation,
    validate_geometry,
    validate_materials,
)
from forge.assets.provenance import inventory_csv, inventory_rows, write_asset_provenance
from forge.assets.skeleton import ARCHER_JOINTS, ARCHER_SKELETON_HASH
from forge.assets.unity_prefab import PREFAB_METHOD, PrefabManifest, UnityPrefabStep
from forge.checks.base import ProcResult
from forge.lanes.asset_tools import ToolMissing
from forge.models import ToolchainManifest

from .asset_fakes import FakeBlender, tech_report
from .test_asset_generation import motion_report, rig_report


def test_blender_scripts_compile_and_never_generate_a_bowstring(tmp_path):
    for name in ("forge_bpy", "normalize", "rig", "motion"):
        py_compile.compile(str(SCRIPTS / f"{name}.py"), cfile=str(tmp_path / f"{name}.pyc"), doraise=True)
    text = (SCRIPTS / "normalize.py").read_text()
    assert "bowstring" in text and "procedural at runtime" in text
    fb = (SCRIPTS / "forge_bpy.py").read_text()
    assert 'axis_up="Y"' in fb and "add_leaf_bones=False" in fb and "use_triangles=True" in fb


def test_normalize_argv_and_report(tmp_path):
    fake = FakeBlender({"normalize": tech_report(2900, materials=1, tex=512, size=(1.3, 0.1, 0.2))})
    tools = BlenderAssetTools("/opt/blender/blender", runner=fake, turntable=8)
    src = tmp_path / "raw.glb"
    src.write_bytes(b"glTF")
    run = tools.normalize(src, tmp_path / "out", asset_id="recurve_bow", size_m=1.3, scale_axis="largest",
                          pivot="grip", max_tris=1500, max_texture=512, attachments=["bow_string_top"],
                          auto_attachments=["bow_string_top"])
    argv = fake.calls[0]
    assert argv[:4] == ["/opt/blender/blender", "--background", "--factory-startup", "--python"]
    assert argv[4].endswith("forge/assets/blender/normalize.py")
    for flag, val in (("--max-tris", "1500"), ("--pivot", "grip"), ("--turntable", "8"),
                      ("--auto-attachments", "bow_string_top")):
        assert argv[argv.index(flag) + 1] == val
    assert run.output is not None and set(run.renders) == {"front", "side", "back", "three_quarter"}
    geo = geometry_from_report(run.report, collider="none")
    assert geo.triangles == 2900 and geo.bounds_size_m == (1.3, 0.1, 0.2)
    assert validate_geometry(geo, max_triangles=1500)[0] == "2900 triangles > budget 1500"
    mats = materials_from_report(run.report)
    assert mats.materials[0].unity_shader == "Universal Render Pipeline/Lit"
    assert validate_materials(mats, max_texture_px=512) == []


def test_blender_missing_or_failing(tmp_path):
    with pytest.raises(ToolMissing):
        BlenderAssetTools(None).normalize(tmp_path / "x.glb", tmp_path, asset_id="a", size_m=1, scale_axis="largest",
                                          pivot="center", max_tris=10, max_texture=64, attachments=[])
    tools = BlenderAssetTools("/b", runner=FakeBlender(fail={"normalize"}))
    with pytest.raises(BlenderFailed, match="normalize failed"):
        tools.normalize(tmp_path / "x.glb", tmp_path, asset_id="a", size_m=1, scale_axis="largest", pivot="center",
                        max_tris=10, max_texture=64, attachments=[])
    gone = BlenderAssetTools("/b", runner=lambda a, c, t: ProcResult(None, "", "No such file", 0,
                                                                     missing_executable=True))
    with pytest.raises(ToolMissing, match="could not be started"):
        gone.rig(tmp_path / "r.fbx", tmp_path / "m.json", tmp_path, asset_id="a", max_weights=4)


def test_rig_and_motion_reports_become_contracts(tmp_path):
    fake = FakeBlender({"rig": rig_report, "motion": motion_report})
    tools = BlenderAssetTools("/b", runner=fake)
    run = tools.rig(tmp_path / "rigged.fbx", tmp_path / "m.json", tmp_path / "rig", asset_id="archer", max_weights=4)
    sk = skeleton_from_report(run.report, mapping_sha256="abc")
    assert sk.skeleton_hash == ARCHER_SKELETON_HASH and sk.root == "root" and len(sk.joints) == len(ARCHER_JOINTS)
    flags = needs_cleanup("hy-motion-1.0", "idle")
    mrun = tools.motion(tmp_path / "idle.bvh", tmp_path / "rig.fbx", tmp_path / "m.json", tmp_path / "clips",
                        clip="idle", fps=30, loop_cleanup="loop_cleanup" in flags,
                        root_motion_conversion="root_motion_conversion" in flags, markers={"x": 0.1})
    argv = fake.calls[-1]
    assert "--loop-cleanup" in argv and "--root-motion-conversion" in argv
    assert argv[argv.index("--loop-policy") + 1] == "loop" and argv[argv.index("--root-motion") + 1] == "in_place"
    assert json.loads(argv[argv.index("--markers") + 1]) == {"x": 0.1}
    mc = motion_from_report(mrun.report, route="hy-motion-1.0", skeleton_hash_=ARCHER_SKELETON_HASH)
    assert mc.cleanup_applied == ["loop_cleanup", "root_motion_conversion"]  # retarget:<src> is not a cleanup
    assert {t.to_clip for t in mc.transitions} >= {"draw", "hit"}


def test_unity_prefab_step(tmp_path):
    proj = tmp_path / "unity"
    proj.mkdir()
    manifest = tmp_path / "m.json"
    manifest.write_text(PrefabManifest("crate", 1, "prop", "Assets/x.fbx", "Assets/x.prefab").to_json())
    assert UnityPrefabStep(None).run(proj, manifest, tmp_path / "r.json").status == "BLOCKED"
    tm = ToolchainManifest(generated_at=0, profile="t", unity_editor_path=str(tmp_path / "missing"))
    assert "does not exist" in UnityPrefabStep(tm).run(proj, manifest, tmp_path / "r.json").reason
    editor = tmp_path / "Unity"
    editor.write_text("")
    tm = ToolchainManifest(generated_at=0, profile="t", unity_editor_path=str(editor))
    seen = []

    def no_result(argv, cwd, t):
        seen.append(argv)
        return ProcResult(1, "", "", 1)
    r = UnityPrefabStep(tm, runner=no_result).run(proj, manifest, tmp_path / "r.json")
    assert r.status == "FAIL" and PREFAB_METHOD in r.reason
    assert seen[0][seen[0].index("-executeMethod") + 1] == PREFAB_METHOD and "-batchmode" in seen[0]

    def ok(argv, cwd, t):
        Path(argv[argv.index("-forgeResult") + 1]).write_text(json.dumps({"ok": True, "prefab": "Assets/x.prefab"}))
        return ProcResult(0, "", "", 1)
    r = UnityPrefabStep(tm, runner=ok).run(proj, manifest, tmp_path / "r2.json")
    assert r.status == "PASS" and r.prefab == "Assets/x.prefab"


def test_licence_inventory_joins_provenance_and_catalogue_credits(tmp_path):
    rec = ProvenanceRecord(asset_id="archer", version=1, stage="generation", route="hunyuan3d-2.1",
                           model="hunyuan3d-2.1", model_version="2.1", service="local GPU", generated_at=5.0,
                           inputs=[InputReference(kind="prompt", description="p", rights="owner")],
                           transformations=[Transformation(step="rig", tool="mixamo", manual=True)],
                           territory=TerritoryFlags(distribution_countries=["IN"], excluded_territories=["EU", "UK",
                                                                                                        "KR"]),
                           final_hashes={"a/b.fbx": "f" * 64})
    credits = tmp_path / "assets" / "source" / "wooden_longbow" / "credits.json"
    credits.parent.mkdir(parents=True)
    credits.write_text(json.dumps([{"uid": "u1", "name": "Longbow", "artist": "someone", "licence": "CC-BY",
                                    "source_url": "https://sketchfab.com/x", "download_date": "2026-10-01"}]))
    rows = inventory_rows([rec], [credits])
    assert rows[0]["excluded_territories"] == "EU,UK,KR" and rows[0]["manual_steps"] == "rig"
    assert rows[1]["route_or_licence"] == "CC-BY" and rows[1]["attribution"] == "Longbow by someone"
    csv_text = inventory_csv(rows)
    assert csv_text.splitlines()[0].startswith("asset_id,version,stage") and "hunyuan3d-2.1" in csv_text
    p = write_asset_provenance(tmp_path, [rec])
    assert json.loads(p.read_text())[0]["route"] == "hunyuan3d-2.1"
