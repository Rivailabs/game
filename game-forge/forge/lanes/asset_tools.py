"""Asset tool adapters for the catalogue lane: Blender cleanup + renders, budget check.

* :class:`BlenderAdapter` runs ``blender --background --python blender_cleanup.py`` with a
  scrubbed environment (no API keys). Blender is found from an explicit path, the
  ``FORGE_BLENDER`` variable, the toolchain manifest, or ``PATH``. When it is absent the
  lane reports BLOCKED (exit 3); Forge never pretends a cleanup happened.
* :func:`budget_check` is pure Python over the JSON report the script writes.
* The visual judge is a provider operation (``ProviderAdapter.judge``), see
  ``forge/providers``; :func:`judge_instructions` builds the lane-specific text.
"""

from __future__ import annotations

import json
import os
import shutil
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

from ..checks.base import ProcResult, run_proc
from ..models import ToolchainManifest
from .catalogue import Brief

SCRIPT = Path(__file__).with_name("blender_cleanup.py")
VIEWS = ("front", "side", "back", "three_quarter")
SIZE_TOLERANCE = 0.05  # cleaned largest dimension must be within 5% of size_m


class ToolMissing(Exception):
    """A required external tool is not installed: the lane is BLOCKED (exit 3)."""


class CleanupFailed(Exception):
    pass


def find_blender(explicit: str | None = None, manifest: ToolchainManifest | None = None) -> Optional[str]:
    for c in (explicit, os.environ.get("FORGE_BLENDER")):
        if c and Path(c).exists() and os.access(c, os.X_OK):
            return c
    if manifest is not None:
        t = manifest.tools.get("blender")
        if t and t.found and t.path and Path(t.path).exists():
            return t.path
    return shutil.which("blender")


@dataclass
class CleanupReport:
    uid: str
    glb: Path
    report_path: Path
    renders: dict[str, Path]
    data: dict[str, Any] = field(default_factory=dict)

    @property
    def triangles(self) -> int:
        return int(self.data.get("triangles", 0))


class BlenderAdapter:
    """Runs the committed bpy script. ``runner`` is injectable for tests (same shape as ``run_proc``)."""

    def __init__(self, binary: str | None, *, timeout_s: float = 900,
                 runner: Callable[[list[str], Path, float], ProcResult] | None = None, resolution: int = 512):
        self.binary = binary
        self.timeout_s = timeout_s
        self.runner = runner or (lambda argv, cwd, t: run_proc(argv, cwd, t))
        self.resolution = resolution

    def available(self) -> tuple[bool, str]:
        if not self.binary:
            return False, ("Blender not found (install Blender 4.x, put it on PATH, set FORGE_BLENDER, "
                           "or run `forge preflight` so the toolchain manifest records it)")
        return True, ""

    def cleanup(self, uid: str, src: Path, out_dir: Path, brief: Brief) -> CleanupReport:
        ok, why = self.available()
        if not ok:
            raise ToolMissing(why)
        out_dir.mkdir(parents=True, exist_ok=True)
        glb = out_dir / f"{uid}.glb"
        report = out_dir / f"{uid}.report.json"
        renders = out_dir / f"{uid}.renders"
        argv = [self.binary, "--background", "--factory-startup", "--python", str(SCRIPT), "--",
                "--input", str(src), "--output", str(glb), "--report", str(report), "--renders", str(renders),
                "--size-m", str(brief.size_m), "--max-tris", str(brief.max_tris),
                "--max-texture", str(brief.max_texture), "--resolution", str(self.resolution)]
        res = self.runner(argv, out_dir, self.timeout_s)
        if res.missing_executable:
            raise ToolMissing(f"Blender could not be started: {res.stderr.strip()[:300]}")
        if res.timed_out:
            raise CleanupFailed(f"Blender timed out after {self.timeout_s:.0f}s")
        if res.returncode != 0 or not report.exists() or not glb.exists():
            tail = (res.stderr or res.stdout or "").strip().splitlines()[-5:]
            raise CleanupFailed(f"Blender cleanup failed (exit {res.returncode}): {' | '.join(tail)[:500]}")
        data = json.loads(report.read_text())
        imgs = {v: renders / name for v, name in (data.get("renders") or {}).items()}
        return CleanupReport(uid, glb, report, imgs, data)


def budget_check(rep: CleanupReport, brief: Brief) -> list[str]:
    """Problems with a cleaned asset against the brief's budgets ([] = within budget)."""
    d = rep.data
    problems = []
    tris = d.get("triangles")
    if not isinstance(tris, int):
        problems.append("report has no triangle count")
    elif tris > brief.max_tris:
        problems.append(f"{tris} triangles > max {brief.max_tris}")
    for t in d.get("textures") or []:
        edge = max(int(t.get("width", 0)), int(t.get("height", 0)))
        if edge > brief.max_texture:
            problems.append(f"texture {t.get('name')} is {edge}px > max {brief.max_texture}px")
    if not d.get("textures"):
        problems.append("no textures after cleanup (untextured asset)")
    size = (d.get("bounds") or {}).get("size")
    if not size or len(size) != 3:
        problems.append("report has no bounds")
    else:
        largest = max(float(x) for x in size)
        if abs(largest - brief.size_m) > brief.size_m * SIZE_TOLERANCE:
            problems.append(f"largest dimension {largest:.3f} m is not {brief.size_m} m (+/-5%)")
    missing = [v for v in VIEWS if v not in rep.renders or not Path(rep.renders[v]).exists()]
    if missing:
        problems.append(f"missing renders: {', '.join(missing)}")
    return problems


def judge_instructions(brief: Brief) -> str:
    kind = brief.kind.replace("_", " ")
    lines = [
        f"The game needs a {kind}: {brief.title or brief.id.replace('_', ' ')}.",
        f"Search terms used: {', '.join(brief.search_terms)}.",
        f"It must NOT be: {', '.join(brief.reject_terms) or '(no extra exclusions)'}.",
        f"Target: one object, largest dimension about {brief.size_m} m, at most {brief.max_tris} triangles.",
        "Return NONE for anything that resembles a recognisable game, film, TV or brand asset "
        "(known characters, franchise props, logos, branded products), or that does not match this brief.",
    ]
    if brief.notes:
        lines.append(f"Notes: {brief.notes}")
    return "\n".join(lines)
