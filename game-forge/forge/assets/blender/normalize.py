"""Blender normalization: provider GLB/FBX -> contract-shaped FBX + textures + technical report + turntable.

Run by Forge as::

    blender --background --factory-startup --python normalize.py -- \
        --input raw/model.glb --output out/archer.fbx --report out/report.json --renders out/renders \
        --texture-dir out/textures --size-m 1.8 --scale-axis height --pivot bottom_center \
        --max-tris 8000 --max-texture 1024 --attachments bow_grip_socket,quiver_socket \
        --turntable 16 --resolution 512 [--keep-rig]

Steps: import (GLB/glTF/FBX/OBJ) -> drop cameras/lights -> (static) bake and drop armatures, or
(--keep-rig) keep the armature -> apply transforms -> join meshes -> scale to ``size_m`` (largest
dimension or height) -> pivot -> decimate to the triangle budget -> resize textures to a power of two
within the budget and write PNGs -> FBX export for Unity (metres, +Y up, +Z forward, triangulated, no
leaf bones) -> technical report (triangles, bones, weights per vertex, materials, UVs, bounds,
attachments, topology hash) -> front/side/back/three-quarter renders and a turntable.

The procedural bowstring is never generated here: it is driven at runtime from the draw state and the
bow's ``bow_string_top`` / ``bow_string_bottom`` attachment points. A mesh object whose name contains
``bowstring`` is removed and reported.
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy  # type: ignore  # noqa: E402

import forge_bpy as fb  # noqa: E402


def parse():
    p = argparse.ArgumentParser()
    p.add_argument("--input", required=True)
    p.add_argument("--output", required=True)
    p.add_argument("--report", required=True)
    p.add_argument("--renders", required=True)
    p.add_argument("--texture-dir", required=True)
    p.add_argument("--size-m", type=float, required=True)
    p.add_argument("--scale-axis", choices=["largest", "height"], default="largest")
    p.add_argument("--pivot", choices=["bottom_center", "center", "grip", "nock"], default="bottom_center")
    p.add_argument("--max-tris", type=int, required=True)
    p.add_argument("--max-texture", type=int, default=1024)
    p.add_argument("--attachments", default="")
    p.add_argument("--auto-attachments", default="",
                   help="names to place automatically (bow string ends, nock, tip, rest) when the source has none")
    p.add_argument("--turntable", type=int, default=16)
    p.add_argument("--resolution", type=int, default=512)
    p.add_argument("--keep-rig", action="store_true")
    return p.parse_args(fb.script_args())


def place_attachments(obj, names, notes):
    """Empties at geometric landmarks along the longest axis for items generated without markers."""
    lo, hi = fb.world_bounds([obj])
    axis = max(range(3), key=lambda i: hi[i] - lo[i])
    centre = (lo + hi) / 2
    for n in names:
        if bpy.data.objects.get(n) is not None:
            continue
        pos = centre.copy()
        if n in ("bow_string_top", "tip"):
            pos[axis] = hi[axis]
        elif n in ("bow_string_bottom", "nock"):
            pos[axis] = lo[axis]
        empty = bpy.data.objects.new(n, None)
        empty.location = pos
        bpy.context.scene.collection.objects.link(empty)
        empty.parent = obj
        notes.append(f"attachment {n} auto-placed at a geometric landmark: verify at visual approval")


def main():
    a = parse()
    fb.reset_scene()
    fb.import_any(a.input)
    notes = []
    for o in list(bpy.context.scene.objects):
        if o.type in ("CAMERA", "LIGHT"):
            bpy.data.objects.remove(o, do_unlink=True)
        elif o.type == "MESH" and "bowstring" in o.name.lower():
            notes.append(f"removed mesh {o.name}: the bowstring is procedural at runtime")
            bpy.data.objects.remove(o, do_unlink=True)
    meshes = fb.mesh_objects()
    if not meshes:
        raise SystemExit("no mesh objects in the input")
    original_tris = fb.triangle_count(meshes)
    arm = fb.armatures()[0] if a.keep_rig and fb.armatures() else None
    if arm is None:
        for o in meshes:
            for m in list(o.modifiers):
                if m.type == "ARMATURE":
                    fb.select_only([o])
                    bpy.ops.object.modifier_apply(modifier=m.name)
        for o in fb.armatures():
            bpy.data.objects.remove(o, do_unlink=True)
    fb.select_only(meshes)
    bpy.ops.object.make_single_user(object=True, obdata=True)
    if arm is None:
        bpy.ops.object.parent_clear(type="CLEAR_KEEP_TRANSFORM")
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    lo, hi = fb.world_bounds([obj])
    dims = hi - lo
    ref = dims.z if a.scale_axis == "height" else max(dims.x, dims.y, dims.z)
    if ref <= 0:
        raise SystemExit("degenerate geometry (zero size)")
    f = a.size_m / ref
    targets = [obj] + ([arm] if arm else [])
    for t in targets:
        t.scale = (t.scale[0] * f, t.scale[1] * f, t.scale[2] * f)
    fb.select_only(targets)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    lo, hi = fb.world_bounds([obj])
    if a.pivot == "bottom_center":
        offset = ((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z)
    elif a.pivot == "center":
        offset = tuple((lo + hi) / 2)
    else:
        marker = bpy.data.objects.get(a.pivot)
        if marker is not None:
            offset = tuple(marker.matrix_world.translation)
        else:  # generated meshes carry no markers: estimate, and say so (verified at visual approval)
            axis = max(range(3), key=lambda i: hi[i] - lo[i])
            c = list((lo + hi) / 2)
            if a.pivot == "nock":
                c[axis] = lo[axis]
            offset = tuple(c)
            notes.append(f"pivot {a.pivot} auto-placed (no marker in source): verify at visual approval")
    for t in targets:
        t.location = (t.location.x - offset[0], t.location.y - offset[1], t.location.z - offset[2])
    fb.select_only(targets)
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
    tris = fb.triangle_count([obj])
    decimations = 0
    while tris > a.max_tris and decimations < 3:
        mod = obj.modifiers.new("ForgeDecimate", "DECIMATE")
        mod.ratio = max(a.max_tris / tris * 0.97, 0.001)
        fb.select_only([obj])
        bpy.ops.object.modifier_apply(modifier=mod.name)
        decimations += 1
        tris = fb.triangle_count([obj])
    if tris > a.max_tris:
        notes.append(f"could not decimate below {a.max_tris} triangles (now {tris})")
    if arm is not None and decimations:
        notes.append("decimated a skinned mesh: skin weights must be re-reviewed (topology changed)")
    out_dir = os.path.dirname(os.path.abspath(a.output))
    mats, textures = fb.material_report([obj], a.texture_dir, a.max_texture)
    uv_layers, uv_inside = fb.uv_report(obj)
    names = [n for n in a.attachments.split(",") if n]
    auto = [n for n in a.auto_attachments.split(",") if n]
    if auto:
        place_attachments(obj, auto, notes)
    attachments = fb.attachments_report(names)
    for att in attachments:
        att["auto"] = att["name"] in auto
    missing = [n for n in names if not any(x["name"] == n for x in attachments)]
    if missing:
        notes.append(f"missing attachments {missing}")
    fb.export_fbx(a.output, [obj], with_armature=arm is not None)
    lo, hi = fb.world_bounds([obj])
    renders = fb.render_views([obj], a.renders, a.resolution, turntable=a.turntable)
    rep = {
        "input": os.path.basename(a.input), "output": os.path.relpath(a.output, out_dir),
        "units": "m", "up_axis": "+Y", "forward_axis": "+Z", "pivot": a.pivot,
        "original_triangles": original_tris, "triangles": tris, "vertices": len(obj.data.vertices),
        "decimations": decimations, "topology_hash": fb.topology_hash(obj),
        "bounds": {"min": list(lo), "max": list(hi), "size": list(hi - lo)},
        "has_normals": len(obj.data.polygons) > 0, "uv_layers": uv_layers, "uvs_in_unit_square": uv_inside,
        "materials": mats, "textures": textures, "attachments": attachments, "renders": renders,
        "notes": notes,
    }
    rep.update(fb.skeleton_report([obj], arm))
    fb.write_report(a.report, rep)


if __name__ == "__main__":
    main()
