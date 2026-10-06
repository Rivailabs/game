"""Shared helpers for Forge's Blender batch scripts (runs INSIDE Blender; Forge never imports it).

Written against the Blender 4.2 LTS Python API. **Not yet run on a real Blender in this
environment** (no Blender here); the Python side of Forge is tested with a fake Blender that writes
the same report shape, so the first real run is the verification of these scripts.
"""

import hashlib
import json
import math
import os
import sys

import bpy  # type: ignore
from mathutils import Vector  # type: ignore

VIEWS = {"front": 0.0, "side": 90.0, "back": 180.0, "three_quarter": 45.0}


def script_args():
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_any(path):
    ext = os.path.splitext(path)[1].lower()
    if ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == ".fbx":
        bpy.ops.import_scene.fbx(filepath=path, automatic_bone_orientation=True)
    elif ext == ".obj":
        bpy.ops.wm.obj_import(filepath=path)
    elif ext == ".bvh":
        bpy.ops.import_anim.bvh(filepath=path, update_scene_fps=True, update_scene_duration=True)
    else:
        raise SystemExit(f"unsupported input format {ext}")


def mesh_objects():
    return [o for o in bpy.context.scene.objects if o.type == "MESH"]


def armatures():
    return [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]


def select_only(objs, active=None):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active or (objs[0] if objs else None)


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


def topology_hash(obj):
    """Hash of connectivity only (vertex count + polygon vertex indices): unchanged by moves or UV edits."""
    h = hashlib.sha256()
    me = obj.data
    h.update(str(len(me.vertices)).encode())
    for p in me.polygons:
        h.update((",".join(str(i) for i in p.vertices) + ";").encode())
    return h.hexdigest()


def uv_report(obj):
    me = obj.data
    layers = len(me.uv_layers)
    inside = True
    if layers:
        uv = me.uv_layers.active.data
        for d in uv:
            u, v = d.uv
            if u < -1e-4 or u > 1 + 1e-4 or v < -1e-4 or v > 1 + 1e-4:
                inside = False
                break
    return layers, inside


ROLE_BY_SOCKET = {"Base Color": "base_color", "Normal": "normal", "Roughness": "roughness", "Metallic": "metallic",
                  "Alpha": "opacity", "Emission Color": "emission"}


def material_report(objs, texture_dir, max_texture):
    """Materials, texture roles/colour space, power-of-two resize to the budget, PNG export."""
    os.makedirs(texture_dir, exist_ok=True)
    mats, textures, seen = [], [], {}
    for o in objs:
        for slot in o.material_slots:
            m = slot.material
            if m is None or m.name in seen:
                continue
            seen[m.name] = True
            entry = {"name": m.name, "surface": "opaque" if getattr(m, "blend_method", "OPAQUE") == "OPAQUE"
                     else ("alpha_clip" if m.blend_method == "CLIP" else "transparent"), "textures": []}
            if m.use_nodes:
                for node in m.node_tree.nodes:
                    if node.type != "TEX_IMAGE" or node.image is None:
                        continue
                    role = "base_color"
                    for link in node.outputs[0].links:
                        to = link.to_node
                        if to.type == "NORMAL_MAP":
                            role = "normal"
                        elif to.type == "BSDF_PRINCIPLED":
                            role = ROLE_BY_SOCKET.get(link.to_socket.name, "mask")
                        elif to.type == "SEPRGB" or to.type == "SEPARATE_COLOR":
                            role = "mask"
                    img = node.image
                    w, h = img.size[0], img.size[1]
                    if w == 0 or h == 0:
                        continue
                    edge = max(w, h)
                    target = 1
                    while target * 2 <= min(edge, max_texture):
                        target *= 2
                    s = target / edge
                    nw, nh = max(int(w * s), 1), max(int(h * s), 1)
                    nw, nh = 1 << (nw.bit_length() - 1), 1 << (nh.bit_length() - 1)  # power of two
                    if (nw, nh) != (w, h):
                        img.scale(nw, nh)
                    fname = f"{bpy.path.clean_name(m.name)}_{role}.png"
                    img.filepath_raw = os.path.join(texture_dir, fname)
                    img.file_format = "PNG"
                    img.save()
                    srgb = img.colorspace_settings.name.lower().startswith("srgb")
                    t = {"name": img.name, "file": f"textures/{fname}", "role": role, "width": img.size[0],
                         "height": img.size[1], "colour_space": "sRGB" if srgb else "linear",
                         "compression": "ASTC_6x6" if role != "normal" else "ASTC_5x5", "material": m.name}
                    entry["textures"].append(img.name)
                    textures.append(t)
            mats.append(entry)
    return mats, textures


def skeleton_report(objs, arm):
    """Bones, deforming count, influences per vertex, normalization and unweighted vertices."""
    if arm is None:
        return {"bones": 0, "deforming_bones": 0, "max_weights_per_vertex": 0, "weights_normalized": True,
                "unweighted_vertices": 0, "joints": []}
    deform = {b.name for b in arm.data.bones if b.use_deform}
    joints = [{"name": b.name, "parent": b.parent.name if b.parent else None, "deforming": b.use_deform}
              for b in arm.data.bones]
    max_inf, unweighted, normalized = 0, 0, True
    for o in objs:
        names = {g.index: g.name for g in o.vertex_groups}
        for v in o.data.vertices:
            ws = [g.weight for g in v.groups if names.get(g.group) in deform and g.weight > 1e-4]
            max_inf = max(max_inf, len(ws))
            if not ws:
                unweighted += 1
            elif abs(sum(ws) - 1.0) > 1e-3:
                normalized = False
    return {"bones": len(arm.data.bones), "deforming_bones": len(deform), "max_weights_per_vertex": max_inf,
            "weights_normalized": normalized, "unweighted_vertices": unweighted, "joints": joints}


def attachments_report(names):
    out = []
    for o in bpy.context.scene.objects:
        if o.type == "EMPTY" and (o.name in names or o.name.endswith("_socket")):
            q = o.matrix_world.to_quaternion()  # Blender stores (w, x, y, z); the contract uses x, y, z, w
            out.append({"name": o.name, "parent": o.parent_bone or (o.parent.name if o.parent else None),
                        "position_m": list(o.matrix_world.translation), "rotation_xyzw": [q.x, q.y, q.z, q.w]})
    for arm in armatures():
        for b in arm.data.bones:
            if b.name in names and not any(a["name"] == b.name for a in out):
                out.append({"name": b.name, "parent": b.parent.name if b.parent else None,
                            "position_m": list(arm.matrix_world @ b.head_local), "rotation_xyzw": [0, 0, 0, 1]})
    return out


def export_fbx(path, objs, *, with_armature=True, bake_anim=False):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    sel = list(objs) + (armatures() if with_armature else [])
    select_only(sel)
    # Unity convention: metres, +Y up, +Z forward after import; leaf bones off; triangulated.
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, apply_unit_scale=True,
                             apply_scale_options="FBX_SCALE_ALL", bake_space_transform=True, axis_forward="-Z",
                             axis_up="Y", object_types={"MESH", "ARMATURE", "EMPTY"}, use_mesh_modifiers=True,
                             mesh_smooth_type="FACE", use_triangles=True, add_leaf_bones=False,
                             primary_bone_axis="Y", secondary_bone_axis="X", bake_anim=bake_anim,
                             bake_anim_use_all_actions=False, path_mode="RELATIVE", embed_textures=False)


def setup_render(res):
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
    cam = scene.camera
    if cam is None:
        cam_data = bpy.data.cameras.new("ForgeCam")
        cam_data.lens = 50
        cam = bpy.data.objects.new("ForgeCam", cam_data)
        scene.collection.objects.link(cam)
        scene.camera = cam
    return scene, cam


def aim(cam, centre, radius, azimuth_deg, elevation_deg=15.0):
    dist = radius / math.tan(cam.data.angle / 2) * 1.15
    t, e = math.radians(azimuth_deg), math.radians(elevation_deg)
    cam.location = centre + Vector((math.sin(t) * math.cos(e), -math.cos(t) * math.cos(e), math.sin(e))) * dist
    cam.rotation_euler = (centre - cam.location).to_track_quat("-Z", "Y").to_euler()


def render_views(objs, out_dir, res, *, turntable=0, prefix=""):
    """Front/side/back/three-quarter stills plus ``turntable`` evenly spaced frames."""
    os.makedirs(out_dir, exist_ok=True)
    scene, cam = setup_render(res)
    lo, hi = world_bounds(objs)
    centre, radius = (lo + hi) / 2, max((hi - lo).length / 2, 0.01)
    out = {}
    for view, az in VIEWS.items():
        aim(cam, centre, radius, az)
        name = f"{prefix}{view}.png"
        scene.render.filepath = os.path.join(out_dir, name)
        bpy.ops.render.render(write_still=True)
        out[f"{prefix}{view}"] = name
    for i in range(turntable):
        aim(cam, centre, radius, 360.0 * i / turntable)
        name = f"{prefix}turntable_{i:02d}.png"
        scene.render.filepath = os.path.join(out_dir, name)
        bpy.ops.render.render(write_still=True)
        out[f"{prefix}turntable_{i:02d}"] = name
    return out


def write_report(path, data):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    data["blender_version"] = bpy.app.version_string
    with open(path, "w") as fh:
        json.dump(data, fh, indent=2, default=str)
