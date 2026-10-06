"""Blender motion stage: retarget one clip onto the archer rig, loop cleanup, root-motion conversion,
clip report and a contact sheet.

Run by Forge as::

    blender --background --factory-startup --python motion.py -- \
        --clip raw/idle.fbx --rig out/archer_rigged.fbx --mapping retarget_mixamo_archer.json \
        --output out/clips/idle.fbx --report out/clips/idle.json --renders out/clips/idle_sheet \
        --clip-name idle --fps 30 --loop-policy loop --root-motion in_place \
        [--loop-cleanup] [--root-motion-conversion] [--markers '{"release": 0.42}'] [--frames 8]

HY-Motion 1.0 documents seamless loops and in-place modes as unsupported, so Forge applies its own
``--loop-cleanup`` (cross-fade the last frames into the first pose, then measure the seam) and
``--root-motion-conversion`` (strip horizontal hips travel for in-place clips, or move it onto the
``root`` bone for root-motion clips). Every applied step is listed in ``cleanup_applied``.

Markers passed by the owner are used as given; otherwise Forge proposes them from the motion
(e.g. ``release`` at the peak speed of the string hand) and flags them ``proposed`` for review.
"""

import argparse
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy  # type: ignore  # noqa: E402
from mathutils import Vector  # type: ignore  # noqa: E402

import forge_bpy as fb  # noqa: E402

FOOT_BONES = {"left_foot": "foot_l", "right_foot": "foot_r"}
CONTACT_HEIGHT_M = 0.05
CONTACT_SPEED_MPS = 0.15


def parse():
    p = argparse.ArgumentParser()
    p.add_argument("--clip", required=True)
    p.add_argument("--rig", required=True)
    p.add_argument("--mapping", required=True)
    p.add_argument("--output", required=True)
    p.add_argument("--report", required=True)
    p.add_argument("--renders", required=True)
    p.add_argument("--clip-name", required=True)
    p.add_argument("--fps", type=float, default=30.0)
    p.add_argument("--loop-policy", choices=["loop", "once", "hold_last"], default="once")
    p.add_argument("--root-motion", choices=["in_place", "root_motion"], default="in_place")
    p.add_argument("--loop-cleanup", action="store_true")
    p.add_argument("--root-motion-conversion", action="store_true")
    p.add_argument("--markers", default="")
    p.add_argument("--frames", type=int, default=8)
    p.add_argument("--blend-frames", type=int, default=6)
    p.add_argument("--resolution", type=int, default=384)
    return p.parse_args(fb.script_args())


def import_armature(path):
    before = set(bpy.data.objects)
    fb.import_any(path)
    new = [o for o in bpy.data.objects if o not in before]
    arms = [o for o in new if o.type == "ARMATURE"]
    if not arms:
        raise SystemExit(f"{path} has no armature")
    return arms[0], [o for o in new if o.type == "MESH"]


def retarget(target, source, mapping, fps):
    scene = bpy.context.scene
    act = source.animation_data.action if source.animation_data else None
    if act is None:
        raise SystemExit("source clip has no animation")
    start, end = (int(round(x)) for x in act.frame_range)
    scene.render.fps = int(round(fps))
    scene.frame_start, scene.frame_end = start, end
    scale = float(mapping.get("source_scale_to_m", 1.0))
    source.scale = (source.scale[0] * scale, source.scale[1] * scale, source.scale[2] * scale)
    for tgt, src in mapping["bones"].items():
        pb = target.pose.bones.get(tgt)
        if pb is None or source.pose.bones.get(src) is None:
            continue
        c = pb.constraints.new("COPY_ROTATION")
        c.target, c.subtarget = source, src
        c.target_space = c.owner_space = "WORLD"
        if tgt == "hips":
            cl = pb.constraints.new("COPY_LOCATION")
            cl.target, cl.subtarget = source, src
            cl.target_space = cl.owner_space = "WORLD"
    fb.select_only([target], target)
    bpy.ops.object.mode_set(mode="POSE")
    bpy.ops.pose.select_all(action="SELECT")
    bpy.ops.nla.bake(frame_start=start, frame_end=end, only_selected=True, visual_keying=True,
                     clear_constraints=True, use_current_action=False, bake_types={"POSE"})
    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.data.objects.remove(source, do_unlink=True)
    return start, end


def bone_world(arm, name):
    pb = arm.pose.bones.get(name)
    return (arm.matrix_world @ pb.head) if pb else None


def sample(arm, start, end, names):
    out = {n: [] for n in names}
    for f in range(start, end + 1):
        bpy.context.scene.frame_set(f)
        for n in names:
            p = bone_world(arm, n)
            out[n].append(Vector(p) if p is not None else None)
    return out


def loop_cleanup(arm, start, end, blend):
    """Cross-fade the last ``blend`` frames toward the first frame's values on every F-curve."""
    act = arm.animation_data.action
    for fc in act.fcurves:
        first = fc.evaluate(start)
        last = fc.evaluate(end)
        delta = first - last
        for kp in fc.keyframe_points:
            f = kp.co[0]
            if f >= end - blend:
                t = (f - (end - blend)) / max(blend, 1)
                kp.co[1] += delta * t
                kp.handle_left[1] += delta * t
                kp.handle_right[1] += delta * t
        fc.update()


def seam_error(arm, start, end):
    worst = 0.0
    for pb in arm.pose.bones:
        bpy.context.scene.frame_set(start)
        q0 = pb.matrix_basis.to_quaternion()
        bpy.context.scene.frame_set(end)
        q1 = pb.matrix_basis.to_quaternion()
        worst = max(worst, q0.rotation_difference(q1).angle)
    return worst


def convert_root_motion(arm, start, end, mode):
    """In place: remove horizontal hips travel. Root motion: move it onto the root bone."""
    act = arm.animation_data.action
    hips = 'pose.bones["hips"].location'
    curves = {fc.array_index: fc for fc in act.fcurves if fc.data_path == hips}
    if not curves:
        return
    base = {i: curves[i].evaluate(start) for i in curves}
    root_curves = {}
    if mode == "root_motion" and arm.pose.bones.get("root") is not None:
        for i in (0, 1):  # horizontal axes of the bone's local space (Blender: X, Y; Z is up)
            root_curves[i] = act.fcurves.find('pose.bones["root"].location', index=i) or \
                act.fcurves.new('pose.bones["root"].location', index=i, action_group="root")
    for i in (0, 1):
        fc = curves.get(i)
        if fc is None:
            continue
        for kp in fc.keyframe_points:
            travel = kp.co[1] - base[i]
            kp.co[1] -= travel
            kp.handle_left[1] -= travel
            kp.handle_right[1] -= travel
            if i in root_curves:
                root_curves[i].keyframe_points.insert(kp.co[0], travel)
        fc.update()


def horizontal_travel(series):
    pts = [p for p in series if p is not None]
    if len(pts) < 2:
        return 0.0
    return math.hypot(pts[-1].x - pts[0].x, pts[-1].y - pts[0].y)


def contacts(samples, fps, start):
    out = []
    for name, bone in FOOT_BONES.items():
        pts = samples.get(bone) or []
        window = None
        for i, p in enumerate(pts):
            if p is None:
                continue
            speed = (p - pts[i - 1]).length * fps if i and pts[i - 1] is not None else 0.0
            on = p.z <= CONTACT_HEIGHT_M and speed <= CONTACT_SPEED_MPS
            t = i / fps
            if on and window is None:
                window = [t, t]
            elif on:
                window[1] = t
            elif window is not None:
                out.append({"name": name, "start_s": window[0], "end_s": window[1]})
                window = None
        if window is not None:
            out.append({"name": name, "start_s": window[0], "end_s": window[1]})
    if samples.get("hand_l"):
        out.append({"name": "bow_hand_grip", "start_s": 0.0, "end_s": (len(samples["hand_l"]) - 1) / fps})
    return out


def propose_markers(clip, samples, fps):
    hand = [p for p in samples.get("hand_r") or [] if p is not None]
    if len(hand) < 3:
        return {}
    speeds = [(hand[i] - hand[i - 1]).length * fps for i in range(1, len(hand))]
    peak = max(range(len(speeds)), key=lambda i: speeds[i]) + 1
    dur = (len(hand) - 1) / fps
    if clip == "release":
        return {"release": peak / fps}
    if clip == "draw":
        far = max(range(len(hand)), key=lambda i: (hand[i] - (samples["hand_l"][i] or hand[i])).length)
        return {"draw_start": 0.0, "draw_full": far / fps}
    defaults = {"recover": {"recover_end": dur}, "hit": {"hit_react": min(0.1, dur)},
                "dodge_left": {"dodge_start": 0.0, "dodge_end": dur}, "dodge_right": {"dodge_start": 0.0,
                                                                                      "dodge_end": dur},
                "jump": {"takeoff": dur * 0.3, "land": dur * 0.8}, "defeat": {"defeat_settle": dur}}
    return defaults.get(clip, {})


def main():
    a = parse()
    mapping = json.load(open(a.mapping))
    fb.reset_scene()
    target, meshes = import_armature(a.rig)
    source, src_meshes = import_armature(a.clip)
    for m in src_meshes:
        bpy.data.objects.remove(m, do_unlink=True)
    start, end = retarget(target, source, mapping, a.fps)
    cleanup = ["retarget:" + mapping.get("source_skeleton", "?")]
    names = ["hips", "foot_l", "foot_r", "hand_l", "hand_r"]
    before = sample(target, start, end, names)
    travel_before = horizontal_travel(before["hips"])
    if a.root_motion_conversion:
        convert_root_motion(target, start, end, a.root_motion)
        cleanup.append("root_motion_conversion")
    if a.loop_cleanup:
        loop_cleanup(target, start, end, a.blend_frames)
        cleanup.append("loop_cleanup")
    after = sample(target, start, end, names)
    travel = horizontal_travel(after["hips"])
    markers = json.loads(a.markers) if a.markers else {}
    proposed = False
    if not markers:
        markers = propose_markers(a.clip_name, after, a.fps)
        proposed = bool(markers)
    fb.export_fbx(a.output, meshes, with_armature=True, bake_anim=True)
    os.makedirs(a.renders, exist_ok=True)
    scene, cam = fb.setup_render(a.resolution)
    lo, hi = fb.world_bounds(meshes)
    centre, radius = (lo + hi) / 2, max((hi - lo).length / 2, 0.01) * 1.3
    fb.aim(cam, centre, radius, 90.0, 10.0)
    renders = {}
    for i in range(a.frames):
        f = start + round((end - start) * i / max(a.frames - 1, 1))
        scene.frame_set(f)
        name = f"frame_{i:02d}.png"
        scene.render.filepath = os.path.join(a.renders, name)
        bpy.ops.render.render(write_still=True)
        renders[f"frame_{i:02d}"] = name
    rep = {
        "clip_name": a.clip_name, "frame_rate": a.fps, "duration_s": (end - start) / a.fps,
        "frames": end - start + 1, "root_motion": a.root_motion, "loop_policy": a.loop_policy,
        "loop_seam_error": seam_error(target, start, end) if a.loop_policy == "loop" else None,
        "root_horizontal_travel_m": travel if a.root_motion == "in_place" else 0.0,
        "root_travel_before_conversion_m": travel_before,
        "contacts": contacts(after, a.fps, start),
        "events": [{"name": k, "time_s": v} for k, v in sorted(markers.items(), key=lambda kv: kv[1])],
        "markers_proposed": proposed, "cleanup_applied": cleanup, "renders": renders,
    }
    fb.write_report(a.report, rep)


if __name__ == "__main__":
    main()
