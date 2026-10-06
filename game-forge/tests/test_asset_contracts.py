"""Asset contracts, initial budgets, the archer skeleton/retarget mapping and the clip set."""

from __future__ import annotations

import copy

import pytest

from forge.assets.budgets import BUDGETS, SceneMeasurement, all_ok, budget_for, check_asset, check_scene
from forge.assets.clips import ARCHER_CLIPS, REQUIRED_ARCHER_CLIPS, check_clip, check_clip_set, needs_cleanup
from forge.assets.contracts import (
    Attachment,
    AudioContract,
    ContactWindow,
    EventMarker,
    GeometryContract,
    InputReference,
    MaterialSpec,
    MaterialsContract,
    MotionContract,
    ProvenanceRecord,
    RightsRecord,
    SkeletonContract,
    SkinWeights,
    TerritoryFlags,
    TermsRef,
    TextureSpec,
    Transformation,
    TransitionExpectation,
    validate_audio,
    validate_geometry,
    validate_materials,
    validate_provenance,
    validate_skeleton,
)
from forge.assets.skeleton import (
    ARCHER_ATTACHMENTS,
    ARCHER_JOINTS,
    ARCHER_SKELETON_HASH,
    builtin_mapping,
    deforming_count,
    mapping_sha256,
    validate_mapping,
)

from .asset_fakes import tech_report


def geometry(**kw) -> GeometryContract:
    base = dict(pivot="bottom_center", bounds_size_m=(0.6, 1.8, 0.4), triangles=7_500, topology_hash="t1",
                has_normals=True, uv_layers=1, collider_policy="capsule",
                attachments=[Attachment(name=a, parent="hand_l") for a in ARCHER_ATTACHMENTS])
    base.update(kw)
    return GeometryContract(**base)


def test_plan_budget_table():
    a = BUDGETS["archer"]
    assert (a.max_triangles, a.max_deforming_bones, a.max_weights_per_vertex, a.max_materials) == (8000, 50, 4, 2)
    assert BUDGETS["bow"].max_triangles == 1500 and BUDGETS["arrow"].max_triangles == 200
    assert BUDGETS["arena"].max_triangles == 40000 and BUDGETS["arena"].max_opaque_material_batches == 16
    assert a.max_texture_px == 1024
    assert budget_for("wooden_barricade", max_triangles=3000).max_triangles == 3000


def test_check_asset_and_scene_budgets():
    lines = check_asset(tech_report(), BUDGETS["archer"])
    assert all_ok(lines)
    bad = check_asset(tech_report(9000, bones=55, weights=6, materials=3, tex=2048), BUDGETS["archer"])
    failed = {line.metric for line in bad if not line.ok}
    assert {"triangles", "deforming bones", "weights per vertex", "materials", "texture base0"} <= failed
    rep = tech_report(tex=2048)
    rep["textures"][0]["measured_approval"] = True
    assert all(line.ok for line in check_asset(rep, BUDGETS["archer"]) if line.metric.startswith("texture"))
    scene = check_scene(SceneMeasurement(71_000, 58, "ProfilerRecorder 'Draw Calls Count'", 12, 64))
    assert [line.ok for line in scene] == [False, True, True, True]
    with pytest.raises(ValueError, match="name the draw-call counter"):
        check_scene(SceneMeasurement(1, 1, ""))


def test_geometry_contract():
    assert validate_geometry(geometry(), size_m=1.8, max_triangles=8000, required_attachments=ARCHER_ATTACHMENTS) == []
    p = validate_geometry(geometry(triangles=9000, has_normals=False, uv_layers=0, attachments=[], topology_hash="",
                                   bounds_size_m=(0.5, 2.5, 0.4)),
                          size_m=1.8, max_triangles=8000, required_attachments=["bow_grip_socket"])
    text = " ".join(p)
    for frag in ("9000 triangles", "no normals", "no UV layer", "missing named attachment", "topology hash",
                 "largest dimension"):
        assert frag in text, frag


def test_materials_contract():
    tex = [TextureSpec(name="base", file="t/base.png", role="base_color", width=1024, height=1024, colour_space="sRGB",
                       compression="ASTC_6x6"),
           TextureSpec(name="nrm", file="t/n.png", role="normal", width=1024, height=1024, colour_space="linear",
                       compression="ASTC_6x6"),
           TextureSpec(name="mask", file="t/m.png", role="mask", width=512, height=512, colour_space="linear",
                       compression="ASTC_8x8", channel_packing={"R": "metallic", "G": "occlusion", "A": "smoothness"})]
    good = MaterialSpec(name="body", unity_shader="Universal Render Pipeline/Lit", base_color="base", normal="nrm",
                        roughness_interpretation="smoothness = 1 - roughness",
                        metallic_interpretation="packed in mask", opacity_interpretation="none", textures=tex)
    assert validate_materials(MaterialsContract(materials=[good]), max_materials=2) == []
    bad_tex = [t.model_copy(update={"colour_space": "linear"}) if t.name == "base" else t for t in tex]
    bad_tex.append(TextureSpec(name="big", file="b.png", role="base_color", width=2048, height=1000,
                               colour_space="sRGB", compression="DXT5"))
    bad = good.model_copy(update={"unity_shader": "Standard", "surface": "transparent", "textures": bad_tex})
    p = " ".join(validate_materials(MaterialsContract(materials=[bad, good, good]), max_materials=2))
    for frag in ("3 materials", "not an explicit URP", "transparency", "must be sRGB", "2048px", "power-of-two",
                 "DXT5"):
        assert frag in p, frag


def test_archer_skeleton_and_contract():
    assert deforming_count(ARCHER_JOINTS) == 38 <= 50
    s = SkeletonContract(skeleton_id="astra_archer_v1", joints=ARCHER_JOINTS, root="root", rest_pose="A",
                         skin_weights=SkinWeights(max_influences=4, normalized=True),
                         retarget_mapping_sha256="m1", skeleton_hash=ARCHER_SKELETON_HASH)
    assert validate_skeleton(s, reference=ARCHER_JOINTS, max_deforming=50, max_weights=4, approved_mapping="m1") == []
    joints = [j.model_copy(update={"parent": "spine_01"}) if j.name == "neck" else j for j in ARCHER_JOINTS]
    bad = s.model_copy(update={"joints": joints, "skin_weights": SkinWeights(max_influences=8, normalized=False,
                                                                              unweighted_vertices=3)})
    p = " ".join(validate_skeleton(bad, reference=ARCHER_JOINTS, max_deforming=30, max_weights=4,
                                   approved_mapping="m2"))
    for frag in ("hierarchy not stable", "38 deforming bones > budget 30", "8 weights", "not normalized",
                 "no skin weight", "differs from the owner-approved"):
        assert frag in p, frag


def test_builtin_retarget_mappings_validate_and_hash():
    mix = builtin_mapping("mixamo")
    assert validate_mapping(mix, source_bones=list(mix["bones"].values())) == []
    smpl = builtin_mapping("smpl")
    assert validate_mapping(smpl) == [] and "thumb_01_l" in smpl["unmapped_ok"]
    h = mapping_sha256(mix)
    assert len(h) == 64 and mapping_sha256(copy.deepcopy(mix)) == h
    broken = copy.deepcopy(mix)
    broken["bones"]["spine_02"] = broken["bones"]["spine_01"]
    del broken["bones"]["hips"]
    broken["target_skeleton_hash"] = "old"
    broken["unmapped_ok"] = ["hips"]
    p = " ".join(validate_mapping(broken, source_bones=["mixamorig:Spine"]))
    for frag in ("skeleton changed", "source bones used twice", "body joint hips cannot be left unmapped",
                 "source rig lacks bones", "root_motion_source"):
        assert frag in p, frag


def clip(name: str, **kw) -> MotionContract:
    spec = ARCHER_CLIPS[name]
    dur = (spec.min_s + spec.max_s) / 2
    base = dict(clip_name=name, frame_rate=30, duration_s=dur, root_motion=spec.root_motion,
                loop_policy="loop" if spec.loop else "once", loop_seam_error=0.0 if spec.loop else None,
                contacts=[ContactWindow(name=c, start_s=0, end_s=dur) for c in spec.contacts],
                transitions=[TransitionExpectation(to_clip=t) for t in spec.transitions],
                events=[EventMarker(name=m, time_s=dur * (i + 1) / (len(spec.markers) + 1))
                        for i, m in enumerate(spec.markers)], skeleton_hash=ARCHER_SKELETON_HASH)
    base.update(kw)
    return MotionContract(**base)


def test_required_clip_set_and_markers():
    assert REQUIRED_ARCHER_CLIPS == ("idle", "draw", "hold", "release", "recover", "hit", "dodge_left",
                                     "dodge_right", "jump", "defeat", "victory")
    assert ARCHER_CLIPS["idle"].loop and ARCHER_CLIPS["hold"].loop
    assert "release" in ARCHER_CLIPS["release"].markers
    checks = check_clip_set([clip(n) for n in REQUIRED_ARCHER_CLIPS], skeleton_hash=ARCHER_SKELETON_HASH)
    assert all(c.ok for c in checks), [c.problems for c in checks if not c.ok]
    taunt = clip("victory").model_copy(update={"clip_name": "taunt"})
    checks = check_clip_set([clip(n) for n in REQUIRED_ARCHER_CLIPS if n != "jump"] + [taunt])
    assert any(c.clip == "jump" and "missing" in c.problems[0] for c in checks)
    assert any(c.clip == "taunt" and "not part of the archer clip set" in c.problems[0] for c in checks)


def test_clip_problems_and_hy_motion_cleanup():
    p = check_clip(clip("idle", loop_policy="once", loop_seam_error=None, root_horizontal_travel_m=0.3,
                        frame_rate=25)).problems
    text = " ".join(p)
    for frag in ("frame rate", "must loop", "loop not tested", "root drift"):
        assert frag in text, frag
    p = check_clip(clip("release", events=[], transitions=[])).problems
    assert "missing event marker release" in p and "no transition to recover" in p
    hy = check_clip(clip("hold", source_route="hy-motion-1.0")).problems
    assert any("loop_cleanup stage required" in x for x in hy)
    assert any("root_motion_conversion stage required" in x for x in hy)
    ok = check_clip(clip("hold", source_route="hy-motion-1.0",
                         cleanup_applied=["loop_cleanup", "root_motion_conversion"]))
    assert ok.ok
    assert needs_cleanup("hy-motion-1.0", "idle") == ["loop_cleanup", "root_motion_conversion"]
    assert needs_cleanup("hy-motion-1.0", "dodge_left") == []
    assert needs_cleanup("meshy", "idle") == []
    other = check_clip_set([clip("idle", skeleton_hash="old")], skeleton_hash=ARCHER_SKELETON_HASH)
    assert any("different skeleton" in x for c in other for x in c.problems)


def test_audio_contract():
    good = AudioContract(clip_name="bow_draw_01", purpose="draw", rights=RightsRecord(source="Example SFX",
                                                                                      licence="single seat"),
                         format="wav", sample_rate_hz=48_000, channels=1, duration_s=0.8, loudness_lufs=-18,
                         loudness_target_lufs=-18, peak_dbfs=-1.5, load_type="DecompressOnLoad", compression="ADPCM")
    assert validate_audio(good) == []
    bad = good.model_copy(update={"rights": None, "channels": 2, "peak_dbfs": 0.2, "loudness_lufs": -10,
                                  "loop": True, "sample_rate_hz": 22050})
    text = " ".join(validate_audio(bad))
    for frag in ("rights", "mono", "clips", "loudness", "loop boundaries", "sample rate"):
        assert frag in text, frag
    music = good.model_copy(update={"purpose": "music", "channels": 2, "load_type": "CompressedInMemory"})
    assert "music must stream (load type Streaming)" in validate_audio(music)


def test_provenance_contract():
    rec = ProvenanceRecord(
        asset_id="archer", version=1, stage="generation", route="meshy", model="meshy", service="Meshy API",
        generated_at=1.0, inputs=[InputReference(kind="prompt", description="original archer", rights="owner-created")],
        terms=[TermsRef(name="Meshy terms", version="2026-09")],
        transformations=[Transformation(step="generation", tool="meshy")],
        territory=TerritoryFlags(distribution_countries=["IN"]),
        final_hashes={"assets/source/archer/archer.fbx": "a" * 64})
    assert validate_provenance(rec) == []
    bad = rec.model_copy(update={"inputs": [InputReference(kind="reference_image", description="pin", rights="")],
                                 "terms": [], "final_hashes": {"x": "short"},
                                 "territory": TerritoryFlags(distribution_countries=["DE"],
                                                             excluded_territories=["EU"])})
    text = " ".join(validate_provenance(bad))
    for frag in ("no rights statement", "no applicable terms", "not a sha256", "excluded in declared"):
        assert frag in text, frag
