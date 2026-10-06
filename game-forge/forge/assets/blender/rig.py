"""Blender rig normalization + deformation review renders.

Run by Forge as::

    blender --background --factory-startup --python rig.py -- \
        --input rigged.fbx --mapping retarget_mixamo_archer.json --output out/archer_rigged.fbx \
        --report out/rig_report.json --renders out/deform --max-weights 4 --resolution 512

Steps: import the rigging route's output -> rename source bones to the stable ``astra_archer_v1``
names using the mapping (target <- source) -> merge weights of extra deforming bones (e.g. finger
tips, ``HeadTop_End``) into their nearest mapped ancestor and delete them -> add the non-deforming
sockets (bow grip, string hand, arrow nock, quiver) when missing -> limit to ``--max-weights``
influences and normalize -> FBX export -> skeleton report (joints, deforming count, influences,
normalization, unweighted vertices) -> deformation test poses rendered front and side (arms raised,
full draw, crouch, torso twist) so the owner reviews hands, shoulders, elbows and feet in motion poses,
not only the rest pose.
"""

import argparse
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy  # type: ignore  # noqa: E402
from mathutils import Euler  # type: ignore  # noqa: E402

import forge_bpy as fb  # noqa: E402

SOCKETS = {"bow_grip_socket": "hand_l", "string_hand_socket": "hand_r", "arrow_nock_socket": "hand_r",
           "quiver_socket": "spine_03"}
#: Test poses: bone -> (x, y, z) Euler degrees (pose space). Chosen to stress shoulders, elbows, grip and knees.
POSES = {
    "arms_raised": {"upperarm_l": (0, 0, 80), "upperarm_r": (0, 0, -80)},
    "full_draw": {"upperarm_l": (0, 0, 85), "lowerarm_l": (0, 0, 0), "upperarm_r": (0, -30, -80),
                  "lowerarm_r": (0, -120, 0), "spine_03": (0, 15, 0)},
    "crouch": {"thigh_l": (-70, 0, 0), "thigh_r": (-70, 0, 0), "calf_l": (110, 0, 0), "calf_r": (110, 0, 0)},
    "twist": {"spine_01": (0, 25, 0), "spine_02": (0, 20, 0), "spine_03": (0, 15, 0)},
}


def parse():
    p = argparse.ArgumentParser()
    p.add_argument("--input", required=True)
    p.add_argument("--mapping", required=True)
    p.add_argument("--output", required=True)
    p.add_argument("--report", required=True)
    p.add_argument("--renders", required=True)
    p.add_argument("--max-weights", type=int, default=4)
    p.add_argument("--resolution", type=int, default=512)
    return p.parse_args(fb.script_args())


def main():
    a = parse()
    mapping = json.load(open(a.mapping))
    fb.reset_scene()
    fb.import_any(a.input)
    arms = fb.armatures()
    if not arms:
        raise SystemExit("rigging output has no armature")
    arm = arms[0]
    meshes = fb.mesh_objects()
    src_to_tgt = {v: k for k, v in mapping["bones"].items()}
    notes = []
    fb.select_only([arm], arm)
    bpy.ops.object.mode_set(mode="EDIT")
    ebones = arm.data.edit_bones
    # merge extra deforming bones into their nearest mapped ancestor
    extra = [b.name for b in ebones if b.name not in src_to_tgt]
    bpy.ops.object.mode_set(mode="OBJECT")
    for name in extra:
        bone = arm.data.bones.get(name)
        anc = bone.parent
        while anc is not None and anc.name not in src_to_tgt:
            anc = anc.parent
        for o in meshes:
            g = o.vertex_groups.get(name)
            if g is None:
                continue
            if anc is not None:
                tgt = o.vertex_groups.get(anc.name) or o.vertex_groups.new(name=anc.name)
                for v in o.data.vertices:
                    for vg in v.groups:
                        if vg.group == g.index and vg.weight > 0:
                            tgt.add([v.index], vg.weight, "ADD")
            o.vertex_groups.remove(g)
        notes.append(f"merged extra bone {name} into {anc.name if anc else 'nothing'}")
    fb.select_only([arm], arm)
    bpy.ops.object.mode_set(mode="EDIT")
    for name in extra:
        eb = arm.data.edit_bones.get(name)
        if eb is not None:
            arm.data.edit_bones.remove(eb)
    for eb in arm.data.edit_bones:
        if eb.name in src_to_tgt:
            eb.name = src_to_tgt[eb.name]  # vertex groups are renamed with the bone
    for sock, parent in SOCKETS.items():
        if arm.data.edit_bones.get(sock) is None and arm.data.edit_bones.get(parent) is not None:
            p = arm.data.edit_bones[parent]
            sb = arm.data.edit_bones.new(sock)
            sb.head = p.tail
            sb.tail = p.tail + (p.tail - p.head) * 0.25
            sb.parent = p
            sb.use_deform = False
            notes.append(f"added socket {sock} at the tip of {parent} (owner to adjust in review)")
    if arm.data.edit_bones.get("root") is None and arm.data.edit_bones.get("hips") is not None:
        hips = arm.data.edit_bones["hips"]
        root = arm.data.edit_bones.new("root")  # stable non-deforming root at the ground origin
        root.head = (hips.head[0], hips.head[1], 0.0)
        root.tail = (hips.head[0], hips.head[1] + 0.1, 0.0)
        root.use_deform = False
        hips.parent = root
        notes.append("added non-deforming 'root' bone under the hips (root motion carrier)")
    bpy.ops.object.mode_set(mode="OBJECT")
    for o in meshes:
        fb.select_only([o], o)
        bpy.ops.object.vertex_group_limit_total(group_select_mode="BONE_DEFORM", limit=a.max_weights)
        bpy.ops.object.vertex_group_normalize_all(group_select_mode="BONE_DEFORM", lock_active=False)
    fb.export_fbx(a.output, meshes, with_armature=True)
    renders = fb.render_views(meshes, a.renders, a.resolution, prefix="rest_")
    scene, cam = fb.setup_render(a.resolution)
    lo, hi = fb.world_bounds(meshes)
    centre, radius = (lo + hi) / 2, max((hi - lo).length / 2, 0.01)
    for pose, bones in POSES.items():
        for pb in arm.pose.bones:
            pb.rotation_mode = "XYZ"
            pb.rotation_euler = (0, 0, 0)
        for bname, (x, y, z) in bones.items():
            pb = arm.pose.bones.get(bname)
            if pb is not None:
                pb.rotation_mode = "XYZ"
                pb.rotation_euler = Euler((math.radians(x), math.radians(y), math.radians(z)))
        bpy.context.view_layer.update()
        for view, az in (("front", 0.0), ("side", 90.0)):
            fb.aim(cam, centre, radius, az)
            name = f"deform_{pose}_{view}.png"
            scene.render.filepath = os.path.join(a.renders, name)
            bpy.ops.render.render(write_still=True)
            renders[f"deform_{pose}_{view}"] = name
    rep = {"input": os.path.basename(a.input), "output": os.path.basename(a.output), "renders": renders,
           "notes": notes, "triangles": fb.triangle_count(meshes),
           "topology_hash": fb.topology_hash(meshes[0]) if len(meshes) == 1 else ""}
    rep.update(fb.skeleton_report(meshes, arm))
    fb.write_report(a.report, rep)


if __name__ == "__main__":
    main()
