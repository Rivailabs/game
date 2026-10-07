"""Blender batch script: clean one GLB, check it, and render four views.

Run by Forge as::

    blender --background --factory-startup --python blender_cleanup.py -- \
        --input in.glb --output out.glb --report report.json --renders DIR \
        --size-m 1.8 --max-tris 3000 --max-texture 512

Steps: import GLB -> drop cameras/lights/empties/armatures (pose baked) -> apply
transforms -> join meshes -> scale so the largest dimension is ``size_m`` -> origin at
bottom centre -> decimate when above ``max_tris`` -> downsize textures above
``max_texture`` -> export GLB -> write a JSON report -> render front, side, back and
three-quarter PNGs with the Workbench engine (no GPU needed).

This file runs INSIDE Blender (it imports ``bpy``); Forge never imports it.
Tested against the Blender 4.x Python API names; not yet run on a real Blender here.
"""

import argparse
import json
import math
import os
import sys

import bpy  # type: ignore  # noqa: E402
from mathutils import Vector  # type: ignore  # noqa: E402

VIEWS = {  # view -> azimuth in degrees around +Z, looking at the object (-Y is "front" in Blender)
    "front": 0.0,
    "side": 90.0,
    "back": 180.0,
    "three_quarter": 45.0,
}


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--input", required=True)
    p.add_argument("--output", required=True)
    p.add_argument("--report", required=True)
    p.add_argument("--renders", required=True)
    p.add_argument("--size-m", type=float, required=True)
    p.add_argument("--max-tris", type=int, required=True)
    p.add_argument("--max-texture", type=int, required=True)
    p.add_argument("--resolution", type=int, default=512)
    return p.parse_args(argv)


def mesh_objects():
    return [o for o in bpy.context.scene.objects if o.type == "MESH"]


def triangle_count(objs):
    deps = bpy.context.evaluated_depsgraph_get()
    total = 0
    for o in objs:
        ev = o.evaluated_get(deps)
        me = ev.to_mesh()
        total += sum(max(len(p.vertices) - 2, 0) for p in me.polygons)
        ev.to_mesh_clear()
    return total


def world_bounds(objs):
    lo = Vector((math.inf, math.inf, math.inf))
    hi = Vector((-math.inf, -math.inf, -math.inf))
    for o in objs:
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            lo = Vector((min(lo.x, w.x), min(lo.y, w.y), min(lo.z, w.z)))
            hi = Vector((max(hi.x, w.x), max(hi.y, w.y), max(hi.z, w.z)))
    return lo, hi


def select_only(objs, active=None):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active or (objs[0] if objs else None)


def main():
    a = parse_args()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=a.input)
    problems = []
    meshes = mesh_objects()
    if not meshes:
        raise SystemExit("no mesh objects in the GLB")
    original_tris = triangle_count(meshes)
    # bake armature deformation (current pose) and drop rigs: the lane delivers a base mesh
    for o in meshes:
        for m in list(o.modifiers):
            if m.type == "ARMATURE":
                select_only([o])
                bpy.ops.object.modifier_apply(modifier=m.name)
    select_only(meshes)
    bpy.ops.object.parent_clear(type="CLEAR_KEEP_TRANSFORM")
    for o in list(bpy.context.scene.objects):
        if o.type != "MESH":
            bpy.data.objects.remove(o, do_unlink=True)
    meshes = mesh_objects()
    select_only(meshes)
    bpy.ops.object.make_single_user(object=True, obdata=True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    # scale so the largest dimension equals size_m; origin at bottom centre
    lo, hi = world_bounds([obj])
    dims = hi - lo
    largest = max(dims.x, dims.y, dims.z)
    if largest <= 0:
        raise SystemExit("degenerate geometry (zero size)")
    f = a.size_m / largest
    obj.scale = (f, f, f)
    select_only([obj])
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    lo, hi = world_bounds([obj])
    obj.location = (obj.location.x - (lo.x + hi.x) / 2, obj.location.y - (lo.y + hi.y) / 2, obj.location.z - lo.z)
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
    # decimate above budget
    tris = triangle_count([obj])
    decimated = False
    if tris > a.max_tris:
        mod = obj.modifiers.new("ForgeDecimate", "DECIMATE")
        mod.ratio = max(a.max_tris / tris * 0.98, 0.001)
        bpy.ops.object.modifier_apply(modifier=mod.name)
        decimated = True
        tris = triangle_count([obj])
    # downsize textures
    textures = []
    for img in bpy.data.images:
        if img.size[0] == 0 or img.size[1] == 0:
            continue
        w, h = img.size[0], img.size[1]
        if max(w, h) > a.max_texture:
            s = a.max_texture / max(w, h)
            img.scale(max(int(w * s), 1), max(int(h * s), 1))
            if img.packed_file is not None or not img.filepath:
                img.pack()
        textures.append({"name": img.name, "width": img.size[0], "height": img.size[1]})
    os.makedirs(os.path.dirname(os.path.abspath(a.output)), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=a.output, export_format="GLB", use_selection=False)
    lo, hi = world_bounds([obj])
    renders = render_views(obj, lo, hi, a.renders, a.resolution)
    report = {
        "input": os.path.basename(a.input),
        "output": os.path.basename(a.output),
        "original_triangles": original_tris,
        "triangles": tris,
        "decimated": decimated,
        "textures": textures,
        "bounds": {"min": list(lo), "max": list(hi), "size": list(hi - lo)},
        "renders": renders,
        "blender_version": bpy.app.version_string,
        "problems": problems,
    }
    with open(a.report, "w") as fh:
        json.dump(report, fh, indent=2)


def render_views(obj, lo, hi, out_dir, res):
    os.makedirs(out_dir, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "TEXTURE"
    scene.render.resolution_x = res
    scene.render.resolution_y = res
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"
    if scene.world is None:
        scene.world = bpy.data.worlds.new("ForgeWorld")
    centre = (lo + hi) / 2
    radius = max((hi - lo).length / 2, 0.01)
    cam_data = bpy.data.cameras.new("ForgeCam")
    cam_data.lens = 50
    cam = bpy.data.objects.new("ForgeCam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    dist = radius / math.tan(cam_data.angle / 2) * 1.15
    out = {}
    for view, az in VIEWS.items():
        t = math.radians(az)
        elev = math.radians(15.0)
        cam.location = centre + Vector((math.sin(t) * math.cos(elev), -math.cos(t) * math.cos(elev),
                                        math.sin(elev))) * dist
        direction = centre - cam.location
        cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
        path = os.path.join(out_dir, f"{view}.png")
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        out[view] = os.path.basename(path)
    return out


if __name__ == "__main__":
    main()
