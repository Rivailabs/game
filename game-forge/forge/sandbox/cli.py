"""``forge sandbox detect``: show which isolation backend each worker role would use (and why)."""

from __future__ import annotations

import json

from .policy import SandboxConfigError, default_config_path, load_sandbox_config
from .runner import SandboxRunner


def cmd_sandbox(args) -> None:
    try:
        cfg = load_sandbox_config(args.config or default_config_path())
    except SandboxConfigError as e:
        raise SystemExit(f"sandbox configuration refused: {e}")
    info = SandboxRunner(cfg).describe()
    if args.json:
        print(json.dumps(info, indent=2))
        return
    print(f"sandbox config: {info['config']}  backend setting: {info['backend_setting']}  "
          f"require containment: {info['require_containment']}")
    for b in info["backends"]:
        state = "available" if b["available"] else "unavailable"
        print(f"  {b['name']:7} {state:12} {b['version'] or '':10} {b['reason']}")
    for role, choice in info["choice"].items():
        print(f"  role {role:22} -> {choice:10} network policy: {info['roles'][role]}")
    if all(c == "supervised" for c in info["choice"].values()):
        print("  NO isolation backend usable: generated-code checks run SUPERVISED and evidence says so")


def register(sub) -> None:
    sp = sub.add_parser("sandbox", help="worker containment backends (podman/docker rootless, bubblewrap)")
    ssub = sp.add_subparsers(dest="sandbox_cmd", required=True)
    d = ssub.add_parser("detect", help="probe backends and show the choice per worker role")
    d.add_argument("--config", help="owner sandbox config (default ~/.config/game-forge/sandbox.toml)")
    d.add_argument("--json", action="store_true")
    d.set_defaults(fn=cmd_sandbox)
