"""CLI: ``forge setup``, ``forge update``, ``forge uninstall``, ``forge diag``, ``forge telemetry``,
``forge licences``, ``forge export`` (Release 4)."""

from __future__ import annotations

import json
import os
import sys
import urllib.request
from pathlib import Path

DEFAULT_INSTALL_ROOT = os.environ.get("FORGE_INSTALL_ROOT", os.path.expanduser("~/.local/share/game-forge"))


def _cfg(args):
    from ..config import load_project_config

    return load_project_config(args.project_file)


def _creds(cfg) -> dict[str, bool]:
    out = {}
    try:
        from ..config import build_providers

        for name, p in build_providers(cfg).items():
            broker = getattr(p, "broker", None)
            if broker is not None:
                out[name] = broker.has_credential(name)
    except Exception:  # noqa: BLE001 - a broken provider config is reported by preflight, not here
        pass
    return out


def cmd_setup(args):
    from .setup import run_setup

    cfg = _cfg(args)
    res = run_setup(cfg.data_dir, provider_credentials=_creds(cfg), skip_sample=args.skip_sample)
    print(json.dumps(res.to_dict(), indent=2, default=str) if args.json else res.markdown())
    print(f"report: {cfg.data_dir / 'setup-report.json'}")


class HttpsFetcher:
    def fetch(self, url: str, dest: Path) -> None:
        if not url.startswith("https://"):
            raise ValueError("updates are fetched over https only")
        with urllib.request.urlopen(url, timeout=600) as r, open(dest, "wb") as fh:  # noqa: S310 - https enforced
            while chunk := r.read(1 << 16):
                fh.write(chunk)


def cmd_update(args):
    from ..store import Store
    from .updates import Updater, UpdateBlocked, UpdateError

    cfg = _cfg(args)
    db = cfg.data_dir / "forge.db"
    up = Updater(args.install_root, trusted_keys=args.trusted_key or [], store=Store(db) if db.exists() else None,
                 db_path=db)
    try:
        if args.update_cmd == "rollback":
            print(json.dumps(up.rollback(by=args.by), indent=2))
            return
        manifest = json.loads(Path(args.manifest).read_text())
        if args.update_cmd == "check":
            from .updates import verify_manifest

            print(verify_manifest(manifest, args.trusted_key or []).note)
            print(up.compatibility(manifest).markdown())
            return
        print(json.dumps(up.apply(manifest, HttpsFetcher(), by=args.by, allow_checksum_only=args.allow_checksum_only),
                         indent=2))
    except (UpdateBlocked, UpdateError) as e:
        sys.exit(f"refused: {e}")


def cmd_uninstall(args):
    from .uninstall import execute, plan_uninstall

    cfg = _cfg(args)
    plan = plan_uninstall(args.install_root, projects=[cfg.project.repo_path] + (args.project or []),
                          data_dirs=[cfg.data_dir], caches=args.cache or [], models=args.models or [],
                          remove_caches=args.remove_caches, remove_models=args.remove_models)
    print(plan.markdown())
    if not args.yes:
        print("dry run: nothing removed (pass --yes to remove the listed application files)")
        return
    for p in execute(plan, confirm=True):
        print(f"removed {p}")


def cmd_diag(args):
    from ..store import Store
    from .privacy import collect_diagnostics, export_diagnostics

    cfg = _cfg(args)
    db = cfg.data_dir / "forge.db"
    bundle = collect_diagnostics(cfg.data_dir, store=Store(db) if db.exists() else None, project_file=cfg.path)
    if args.diag_cmd == "preview":
        print(bundle.preview())
        print(f"\nTo export exactly this: forge diag export --confirm {bundle.digest} --out <file.zip>")
        return
    try:
        out = export_diagnostics(bundle, args.out, confirm_digest=args.confirm)
    except PermissionError as e:
        sys.exit(f"refused: {e} (the content changed or was not previewed)")
    print(f"wrote {out}. Nothing was sent; share it yourself if you choose to.")


def cmd_telemetry(args):
    from .privacy import PrivacySettings, Telemetry

    cfg = _cfg(args)
    if args.telemetry_cmd in ("on", "off"):
        s = PrivacySettings(cfg.data_dir).set_telemetry(args.telemetry_cmd == "on", by=args.by)
        print(f"telemetry {'ON (operational counts and timings only)' if s['telemetry_enabled'] else 'OFF'}")
    elif args.telemetry_cmd == "preview":
        print(Telemetry(cfg.data_dir).preview())
    else:
        t = Telemetry(cfg.data_dir)
        print(f"telemetry: {'on' if t.enabled else 'off (default)'}")


def cmd_licences(args):
    from .. import __version__
    from ..models import ToolchainManifest
    from .downloads import DownloadsManifest
    from .licences import inventory, markdown, to_dicts

    cfg = _cfg(args)
    tm = cfg.data_dir / "toolchain-manifest.json"
    entries = inventory(toolchain=ToolchainManifest.model_validate_json(tm.read_text()) if tm.exists() else None,
                        downloads=DownloadsManifest.load(), forge_version=__version__)
    print(json.dumps(to_dicts(entries), indent=2) if args.json else markdown(entries))


def cmd_export(args):
    from .export import ExportError, export_project, verify_export

    cfg = _cfg(args)
    try:
        m = export_project(project_id=cfg.project.id, repo_path=cfg.project.repo_path, branch=cfg.project.accepted_branch,
                           db_path=cfg.data_dir / "forge.db", out_dir=args.out,
                           spec_dir=cfg.data_dir / "specs" / cfg.project.id, artifacts_dir=cfg.data_dir / "artifacts")
    except ExportError as e:
        sys.exit(f"refused: {e}")
    problems = verify_export(args.out)
    print(f"exported {len(m['files'])} file(s) to {args.out}; verification: {'OK' if not problems else problems}")


def register(sub) -> None:
    sp = sub.add_parser("setup", help="R4 setup assistant: prerequisites, manual steps, sample minimal build")
    sp.add_argument("--skip-sample", action="store_true")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(fn=cmd_setup)

    sp = sub.add_parser("update", help="R4 signed update channel, compatibility report and rollback")
    sp.add_argument("--install-root", default=DEFAULT_INSTALL_ROOT)
    sp.add_argument("--trusted-key", action="append", help="Ed25519 public key (hex) trusted for update manifests")
    us = sp.add_subparsers(dest="update_cmd", required=True)
    for name in ("check", "apply"):
        u = us.add_parser(name)
        u.add_argument("manifest")
        if name == "apply":
            u.add_argument("--allow-checksum-only", action="store_true",
                           help="proceed when the signature cannot be verified (cryptography missing)")
    us.add_parser("rollback")
    sp.set_defaults(fn=cmd_update)

    sp = sub.add_parser("uninstall", help="R4 remove the application; projects and data are kept (dry run by default)")
    sp.add_argument("--install-root", default=DEFAULT_INSTALL_ROOT)
    sp.add_argument("--project", action="append")
    sp.add_argument("--cache", action="append")
    sp.add_argument("--models", action="append")
    sp.add_argument("--remove-caches", action="store_true")
    sp.add_argument("--remove-models", action="store_true")
    sp.add_argument("--yes", action="store_true")
    sp.set_defaults(fn=cmd_uninstall)

    sp = sub.add_parser("diag", help="R4 redacted diagnostic bundle: preview first, then export the same digest")
    ds = sp.add_subparsers(dest="diag_cmd", required=True)
    ds.add_parser("preview")
    d = ds.add_parser("export")
    d.add_argument("--confirm", required=True, help="digest printed by `forge diag preview`")
    d.add_argument("--out", required=True)
    sp.set_defaults(fn=cmd_diag)

    sp = sub.add_parser("telemetry", help="R4 privacy: telemetry is off by default")
    sp.add_argument("telemetry_cmd", choices=["status", "on", "off", "preview"])
    sp.set_defaults(fn=cmd_telemetry)

    sp = sub.add_parser("licences", help="R4 licence inventory (Forge vs dependencies, toolchain, outputs)")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(fn=cmd_licences)

    sp = sub.add_parser("export", help="R4 project export that needs no Forge subscription")
    sp.add_argument("--out", required=True)
    sp.set_defaults(fn=cmd_export)
