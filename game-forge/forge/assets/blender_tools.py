"""Python side of the committed Blender scripts (``forge/assets/blender/*.py``).

Each method builds the argv, runs Blender headless (scrubbed environment; through the sandbox runner
as role ``asset_normalization`` when one is given), reads the JSON report the script writes and turns
it into contract objects. Blender absent -> :class:`ToolMissing` (the lane reports BLOCKED; nothing is
pretended). Tests inject a fake runner that writes the same report shape.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional

from ..checks.base import ProcResult, run_proc
from ..lanes.asset_tools import ToolMissing
from .contracts import (
    Attachment,
    ContactWindow,
    EventMarker,
    GeometryContract,
    Joint,
    MaterialSpec,
    MaterialsContract,
    MotionContract,
    SkeletonContract,
    SkinWeights,
    TextureSpec,
    TransitionExpectation,
)
from .clips import ARCHER_CLIPS
from .skeleton import SKELETON_ID, skeleton_hash

SCRIPTS = Path(__file__).with_name("blender")
Runner = Callable[[list[str], Path, float], ProcResult]


class BlenderFailed(Exception):
    pass


@dataclass
class BlenderRun:
    report: dict
    report_path: Path
    output: Optional[Path]
    renders: dict[str, Path] = field(default_factory=dict)


class BlenderAssetTools:
    def __init__(self, binary: Optional[str], *, runner: Runner | None = None, timeout_s: float = 1800,
                 resolution: int = 512, turntable: int = 16):
        self.binary = binary
        self.runner = runner or (lambda argv, cwd, t: run_proc(argv, cwd, t))
        self.timeout_s = timeout_s
        self.resolution = resolution
        self.turntable = turntable

    def available(self) -> tuple[bool, str]:
        if not self.binary:
            return False, ("Blender not found (install Blender 4.2 LTS, set FORGE_BLENDER or run `forge preflight`); "
                           "normalization cannot run")
        return True, ""

    def _run(self, script: str, args: list[str], report: Path, output: Optional[Path], renders: Optional[Path],
             cwd: Path) -> BlenderRun:
        ok, why = self.available()
        if not ok:
            raise ToolMissing(why)
        argv = [self.binary, "--background", "--factory-startup", "--python", str(SCRIPTS / f"{script}.py"), "--",
                *args]
        res = self.runner(argv, cwd, self.timeout_s)
        if res.missing_executable:
            raise ToolMissing(f"Blender could not be started: {res.stderr.strip()[:300]}")
        if res.timed_out:
            raise BlenderFailed(f"Blender {script} timed out after {self.timeout_s:.0f}s")
        if res.returncode != 0 or not report.exists():
            tail = (res.stderr or res.stdout or "").strip().splitlines()[-5:]
            raise BlenderFailed(f"Blender {script} failed (exit {res.returncode}): {' | '.join(tail)[:500]}")
        data = json.loads(report.read_text())
        rmap = {}
        if renders is not None:
            for k, name in (data.get("renders") or {}).items():
                p = renders / name
                if p.exists():
                    rmap[k] = p
        return BlenderRun(data, report, output if output and output.exists() else None, rmap)

    # ---------------------------------------------------------------- stages
    def normalize(self, src: Path, out_dir: Path, *, asset_id: str, size_m: float, scale_axis: str, pivot: str,
                  max_tris: int, max_texture: int, attachments: list[str], keep_rig: bool = False,
                  auto_attachments: list[str] | None = None) -> BlenderRun:
        out_dir.mkdir(parents=True, exist_ok=True)
        report, renders, output = out_dir / "technical_report.json", out_dir / "renders", out_dir / f"{asset_id}.fbx"
        args = ["--input", str(src), "--output", str(output), "--report", str(report), "--renders", str(renders),
                "--texture-dir", str(out_dir / "textures"), "--size-m", str(size_m), "--scale-axis", scale_axis,
                "--pivot", pivot, "--max-tris", str(max_tris), "--max-texture", str(max_texture),
                "--attachments", ",".join(attachments), "--turntable", str(self.turntable),
                "--resolution", str(self.resolution)]
        if keep_rig:
            args.append("--keep-rig")
        if auto_attachments:
            args += ["--auto-attachments", ",".join(auto_attachments)]
        return self._run("normalize", args, report, output, renders, out_dir)

    def render_source(self, src: Path, out_dir: Path) -> BlenderRun:
        """'Before' renders of the raw provider output (no changes), for the visual approval page."""
        out_dir.mkdir(parents=True, exist_ok=True)
        report, renders = out_dir / "source_report.json", out_dir / "source_renders"
        args = ["--input", str(src), "--output", str(out_dir / "source_preview.fbx"), "--report", str(report),
                "--renders", str(renders), "--texture-dir", str(out_dir / "source_textures"), "--size-m", "1",
                "--max-tris", "10000000", "--max-texture", "8192", "--turntable", "0",
                "--resolution", str(self.resolution)]
        return self._run("normalize", args, report, None, renders, out_dir)

    def rig(self, src: Path, mapping: Path, out_dir: Path, *, asset_id: str, max_weights: int) -> BlenderRun:
        out_dir.mkdir(parents=True, exist_ok=True)
        report, renders, output = out_dir / "rig_report.json", out_dir / "deform", out_dir / f"{asset_id}_rigged.fbx"
        args = ["--input", str(src), "--mapping", str(mapping), "--output", str(output), "--report", str(report),
                "--renders", str(renders), "--max-weights", str(max_weights), "--resolution", str(self.resolution)]
        return self._run("rig", args, report, output, renders, out_dir)

    def motion(self, clip_src: Path, rig_fbx: Path, mapping: Path, out_dir: Path, *, clip: str, fps: float,
               loop_cleanup: bool, root_motion_conversion: bool, markers: dict | None = None) -> BlenderRun:
        spec = ARCHER_CLIPS[clip]
        out_dir.mkdir(parents=True, exist_ok=True)
        report, renders, output = out_dir / f"{clip}.json", out_dir / f"{clip}_sheet", out_dir / f"{clip}.fbx"
        args = ["--clip", str(clip_src), "--rig", str(rig_fbx), "--mapping", str(mapping), "--output", str(output),
                "--report", str(report), "--renders", str(renders), "--clip-name", clip, "--fps", str(fps),
                "--loop-policy", "loop" if spec.loop else "once", "--root-motion", spec.root_motion]
        if loop_cleanup:
            args.append("--loop-cleanup")
        if root_motion_conversion:
            args.append("--root-motion-conversion")
        if markers:
            args += ["--markers", json.dumps(markers)]
        return self._run("motion", args, report, output, renders, out_dir)


# --------------------------------------------------------------------------- report -> contracts


def geometry_from_report(rep: dict, *, collider: str, lods_required: bool = False) -> GeometryContract:
    size = (rep.get("bounds") or {}).get("size") or [0, 0, 0]
    return GeometryContract(
        units=rep.get("units", "m"), up_axis=rep.get("up_axis", "+Y"), forward_axis=rep.get("forward_axis", "+Z"),
        pivot=rep.get("pivot", "bottom_center"), bounds_size_m=tuple(float(x) for x in size),
        triangles=int(rep.get("triangles", 0)), vertices=int(rep.get("vertices", 0)),
        topology_hash=rep.get("topology_hash", ""), lods=[], lods_required=lods_required,
        has_normals=bool(rep.get("has_normals")), uv_layers=int(rep.get("uv_layers", 0)),
        uvs_in_unit_square=bool(rep.get("uvs_in_unit_square", True)),
        attachments=[Attachment(name=a["name"], parent=a.get("parent"),
                                position_m=tuple(a.get("position_m", (0, 0, 0))),
                                rotation_xyzw=tuple(a.get("rotation_xyzw", (0, 0, 0, 1))))
                     for a in rep.get("attachments") or []],
        collider_policy=collider)


COMPRESSION_BY_ROLE = {"normal": "ASTC_5x5"}


def materials_from_report(rep: dict) -> MaterialsContract:
    tex_by_name = {}
    for t in rep.get("textures") or []:
        role = t.get("role", "base_color")
        tex_by_name[t["name"]] = TextureSpec(
            name=t["name"], file=t.get("file", ""), role=role, width=int(t.get("width", 0)),
            height=int(t.get("height", 0)),
            colour_space=t.get("colour_space") or ("sRGB" if role in ("base_color", "emission") else "linear"),
            compression=t.get("compression") or COMPRESSION_BY_ROLE.get(role, "ASTC_6x6"),
            channel_packing=t.get("channel_packing") or {}, sha256=t.get("sha256", ""),
            measured_approval=bool(t.get("measured_approval")))
    mats = []
    for m in rep.get("materials") or []:
        texs = [tex_by_name[n] for n in m.get("textures", []) if n in tex_by_name]
        base = next((t.name for t in texs if t.role == "base_color"), None)
        normal = next((t.name for t in texs if t.role == "normal"), None)
        rough = any(t.role == "roughness" for t in texs)
        metal = any(t.role == "metallic" for t in texs)
        surface = m.get("surface", "opaque")
        mats.append(MaterialSpec(
            name=m["name"], unity_shader=m.get("unity_shader", "Universal Render Pipeline/Lit"), surface=surface,
            base_color=base, normal=normal,
            roughness_interpretation="smoothness = 1 - roughness" if rough else "constant",
            metallic_interpretation="metallic map" if metal else "constant",
            opacity_interpretation="none" if surface == "opaque" else
            ("alpha clip" if surface == "alpha_clip" else "base alpha"),
            textures=texs))
    return MaterialsContract(materials=mats)


def skeleton_from_report(rep: dict, *, mapping_sha256: Optional[str]) -> SkeletonContract:
    joints = [Joint(name=j["name"], parent=j.get("parent"), deforming=bool(j.get("deforming", True)))
              for j in rep.get("joints") or []]
    root = next((j.name for j in joints if j.parent is None), "")
    return SkeletonContract(
        skeleton_id=SKELETON_ID, joints=joints, root=root, rest_pose=rep.get("rest_pose", "A"),
        skin_weights=SkinWeights(max_influences=int(rep.get("max_weights_per_vertex", 0)),
                                 normalized=bool(rep.get("weights_normalized", False)),
                                 unweighted_vertices=int(rep.get("unweighted_vertices", 0))),
        retarget_mapping_sha256=mapping_sha256, skeleton_hash=skeleton_hash(joints) if joints else "")


def motion_from_report(rep: dict, *, route: str, skeleton_hash_: str) -> MotionContract:
    spec = ARCHER_CLIPS.get(rep.get("clip_name", ""))
    return MotionContract(
        clip_name=rep["clip_name"], frame_rate=float(rep["frame_rate"]), duration_s=float(rep["duration_s"]),
        root_motion=rep.get("root_motion", "in_place"), loop_policy=rep.get("loop_policy", "once"),
        loop_seam_error=rep.get("loop_seam_error"),
        root_horizontal_travel_m=float(rep.get("root_horizontal_travel_m", 0.0)),
        contacts=[ContactWindow(**c) for c in rep.get("contacts") or []],
        # transitions are the declared expectations for the animator controller (validated in Unity/device)
        transitions=[TransitionExpectation(to_clip=t) for t in (spec.transitions if spec else ())],
        events=[EventMarker(**e) for e in rep.get("events") or []], skeleton_hash=skeleton_hash_,
        source_route=route, cleanup_applied=[c for c in rep.get("cleanup_applied") or [] if ":" not in c])
