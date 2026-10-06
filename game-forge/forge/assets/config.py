"""Build the R2 generation lane from ``[assets]`` in the owner's ``project.toml``.

Example::

    [assets]
    unity_project_dir = "unity"                       # relative to the project workdir
    device_checks = ["android-build", "device-smoke"] # reused for the device-evidence gate
    public_demo = false
    preferences.mesh = ["trellis2", "meshy"]          # owner order per lane; tasks may narrow it
    preferences.rig = ["unirig", "meshy"]
    preferences.motion = ["hy-motion-1.0", "stock-animation"]
    preferences.concept_image = ["flux-schnell-local"]
    preferences.audio = ["licensed-sound-library"]

    [assets.routes.meshy]
    key_env = "MESHY_API_KEY"                         # or key_file outside the repository (chmod 600)
    credit_usd = 0.02
    credits = { text_to_3d = 20, image_to_3d = 30, rig = 5 }

    [assets.routes.trellis2]
    argv = ["python", "/opt/TRELLIS.2/forge_entry.py", "--image", "{image}", "--out", "{out_dir}"]
    install_dir = "/opt/TRELLIS.2"
    model_version = "trellis2@<commit>"
"""

from __future__ import annotations

from pathlib import Path
from typing import Any, Optional

from ..models import ToolchainManifest
from ..sandbox.policy import SandboxConfigError, default_config_path, load_sandbox_config
from ..sandbox.runner import SandboxRunner
from .adapters import build_adapters
from .blender_tools import BlenderAssetTools
from .generation import AssetsConfig, GenerationLane
from .routes import ROUTES
from .unity_prefab import UnityPrefabStep


def assets_config(raw: dict[str, Any], base: Path) -> AssetsConfig:
    prefs = dict(raw.get("preferences") or {})
    for kind, ids in prefs.items():
        unknown = [r for r in ids if r not in ROUTES]
        if unknown:
            raise ValueError(f"[assets] preferences.{kind}: unknown routes {unknown}")
    bd = raw.get("briefs_dir")
    return AssetsConfig(preferences={k: list(v) for k, v in prefs.items()}, routes=dict(raw.get("routes") or {}),
                        unity_project_dir=raw.get("unity_project_dir", "unity"),
                        unity_art_root=raw.get("unity_art_root", "Assets/Forge/Art"),
                        device_checks=list(raw.get("device_checks") or []),
                        public_demo=bool(raw.get("public_demo", False)),
                        poll_interval_s=float(raw.get("poll_interval_s", 10)), fps=float(raw.get("fps", 30)),
                        briefs_dir=(base / bd).resolve() if bd else None)


def generation_lane_from_config(raw: dict[str, Any], *, base: Path, project_id: str, repo_root: Path, data_dir: Path,
                                manifest: Optional[ToolchainManifest], blender: Optional[str] = None,
                                transports: dict | None = None) -> GenerationLane:
    from ..lanes.asset_tools import find_blender

    cfg = assets_config(raw, base)
    try:
        sandbox = load_sandbox_config(raw.get("sandbox_config") or default_config_path(),
                                      writable_roots=[repo_root, data_dir])
    except SandboxConfigError:
        from ..sandbox.policy import SandboxConfig

        sandbox = SandboxConfig(require_containment=True)  # a bad enforcement config never runs open
    adapters = build_adapters(cfg.routes, project_id=project_id, repo_roots=[str(repo_root)], data_dir=data_dir,
                              runner=SandboxRunner(sandbox), transports=transports)
    tools = BlenderAssetTools(find_blender(blender or raw.get("blender"), manifest))
    return GenerationLane(cfg, adapters, tools, UnityPrefabStep(manifest))
