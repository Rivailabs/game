"""Unity prefab assembly step (contract) for normalized assets.

Forge writes a ``forge-prefab/1`` manifest and calls the Unity adapter's batch method::

    Unity -batchmode -nographics -quit -projectPath <unity> \
          -executeMethod Forge.Assets.PrefabAssembler.AssembleFromManifest \
          -forgeManifest <manifest.json> -forgeResult <result.json> -logFile <log>

The editor-side ``Forge.Assets.PrefabAssembler`` (in the Unity project, not in this folder) must:
import the FBX with the listed settings (scale 1, rig ``Generic`` with root ``root``), apply each
texture's max size, colour space (sRGB flag), ASTC compression and mip settings, create URP materials
from the explicit shader mapping, split clips with their loop flags, root-motion setting and gameplay
events (``OnForgeEvent(name)``), add the named attachment transforms and the collider policy, save the
prefab at ``prefab_path`` and write ``result.json``::

    {"ok": true, "prefab": "Assets/...prefab", "guid": "...", "import_warnings": [], "errors": [],
     "counters": {"triangles": 0, "materials": 0, "bones": 0}}

The editor path comes from the toolchain manifest only; with no editor this step is **BLOCKED** and
nothing pretends a prefab exists. Device evidence then reuses the existing build + device checks.
"""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Callable, Optional

from ..checks.base import ProcResult, run_proc
from ..models import ToolchainManifest

PREFAB_METHOD = "Forge.Assets.PrefabAssembler.AssembleFromManifest"
MANIFEST_FORMAT = "forge-prefab/1"


@dataclass
class TextureImport:
    path: str
    role: str
    srgb: bool
    max_size: int
    compression: str  # e.g. ASTC_6x6 (Android override)
    mipmaps: bool = True


@dataclass
class ClipImport:
    name: str
    fbx: str
    loop: bool
    root_motion: bool
    events: list[dict] = field(default_factory=list)  # {"name", "time_s", "function": "OnForgeEvent"}


@dataclass
class PrefabManifest:
    asset_id: str
    version: int
    kind: str
    fbx: str  # Unity project-relative (Assets/...)
    prefab_path: str
    textures: list[TextureImport] = field(default_factory=list)
    materials: list[dict] = field(default_factory=list)  # {"name", "shader", "surface", "maps": {slot: texture}}
    rig: Optional[dict] = None  # {"animation_type": "Generic", "root": "root"}
    clips: list[ClipImport] = field(default_factory=list)
    attachments: list[dict] = field(default_factory=list)
    collider: str = "box"
    format: str = MANIFEST_FORMAT

    def to_json(self) -> str:
        return json.dumps(asdict(self), indent=2, sort_keys=True)


@dataclass
class PrefabResult:
    status: str  # PASS | FAIL | BLOCKED
    reason: str
    prefab: Optional[str] = None
    details: dict = field(default_factory=dict)
    log: str = ""


Runner = Callable[[list[str], Path, float], ProcResult]


class UnityPrefabStep:
    def __init__(self, manifest: Optional[ToolchainManifest], *, runner: Runner | None = None, timeout_s: float = 1800):
        self.manifest = manifest
        self.runner = runner or (lambda argv, cwd, t: run_proc(argv, cwd, t))
        self.timeout_s = timeout_s

    def editor(self) -> tuple[Optional[str], str]:
        m = self.manifest
        if m is None:
            return None, "no toolchain manifest; run `forge preflight` first"
        if not m.unity_editor_path:
            return None, "Unity editor not recorded in the toolchain manifest (not installed or not frozen)"
        if not Path(m.unity_editor_path).exists():
            return None, f"Unity editor path {m.unity_editor_path} does not exist on this worker"
        return m.unity_editor_path, ""

    def run(self, unity_project: Path, manifest_path: Path, result_path: Path) -> PrefabResult:
        editor, why = self.editor()
        if editor is None:
            return PrefabResult("BLOCKED", f"prefab assembly BLOCKED: {why}")
        if not unity_project.exists():
            return PrefabResult("BLOCKED", f"Unity project {unity_project} does not exist")
        log = result_path.with_suffix(".log")
        r = self.runner([editor, "-batchmode", "-nographics", "-quit", "-projectPath", str(unity_project),
                         "-executeMethod", PREFAB_METHOD, "-forgeManifest", str(manifest_path),
                         "-forgeResult", str(result_path), "-logFile", str(log)], unity_project, self.timeout_s)
        text = log.read_text(errors="replace") if log.exists() else (r.stdout + r.stderr)
        if r.timed_out:
            return PrefabResult("FAIL", "Unity prefab assembly timed out", log=text)
        if not result_path.exists():
            return PrefabResult("FAIL", f"Unity exited {r.returncode} without a result file "
                                        f"(is {PREFAB_METHOD} present in the project?)", log=text)
        data = json.loads(result_path.read_text())
        if r.returncode != 0 or not data.get("ok"):
            return PrefabResult("FAIL", "; ".join(data.get("errors") or [f"exit {r.returncode}"])[:500], details=data,
                                log=text)
        return PrefabResult("PASS", "prefab assembled", data.get("prefab"), data, text)
