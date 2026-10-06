"""``forge backup ...`` and ``forge retention ...`` commands (registered by ``forge.cli``)."""

from __future__ import annotations

import datetime as _dt
import json
import sys
from pathlib import Path

from ..artifacts import ArtifactStore
from ..config import load_project_config
from ..store import Store
from .archive import BackupError, BackupSources, create_backup, restore_backup, verify_backup
from .crypto import BackupCryptoError, load_passphrase
from .drill import LAST_DRILL_KEY, restore_drill
from .retention import RetentionPolicy, apply_retention, hold, plan_retention, release_hold


def _sources(cfg) -> BackupSources:
    return BackupSources(project_id=cfg.project.id, data_dir=cfg.data_dir, repo_path=Path(cfg.project.repo_path),
                         accepted_branch=cfg.project.accepted_branch, config_files=[cfg.path])


def _passphrase(args, cfg) -> bytes:
    try:
        return load_passphrase(args.passphrase_file, forbidden_roots=[Path(cfg.project.repo_path), cfg.data_dir])
    except BackupCryptoError as e:
        sys.exit(f"refused: {e}")


def cmd_backup(args) -> None:
    cfg = load_project_config(args.project_file)
    try:
        if args.backup_cmd == "create":
            pw = _passphrase(args, cfg)
            stamp = _dt.datetime.now(_dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
            out = Path(args.out) if args.out else cfg.data_dir / "backups" / f"{cfg.project.id}-{stamp}.fgbk"
            out.parent.mkdir(parents=True, exist_ok=True)
            m = create_backup(_sources(cfg), out, pw)
            Store(cfg.data_dir / "forge.db").append_event("backup_created", project_id=cfg.project.id, path=str(out),
                                                          counts=m.counts, excluded=m.excluded)
            print(f"encrypted backup written: {out}")
            print(f"  files: {len(m.files)}  counts: {m.counts}  accepted head: {m.accepted_head}")
            for x in m.excluded:
                print(f"  excluded {x['path']}: {x['reason']}")
            if not args.out:
                print("  note: store a copy away from this disk; a backup on the same disk is not a backup")
        elif args.backup_cmd == "verify":
            rep = verify_backup(args.archive, _passphrase(args, cfg))
            print(json.dumps(rep.to_dict() if args.json else {"ok": rep.ok, "problems": rep.problems,
                                                              "checked_files": rep.checked_files}, indent=2))
            sys.exit(0 if rep.ok else 1)
        elif args.backup_cmd == "restore":
            rep = restore_backup(args.archive, _passphrase(args, cfg), args.to)
            print(json.dumps(rep.to_dict(), indent=2))
            if rep.ok:
                print(f"restored. To use it: set [forge] data_dir = \"{rep.data_dir}\" and clone {rep.repo_mirror}")
            sys.exit(0 if rep.ok else 1)
        elif args.backup_cmd == "drill":
            store = Store(cfg.data_dir / "forge.db")
            rep = restore_drill(_sources(cfg), store, _passphrase(args, cfg), keep_archive=args.keep)
            print(json.dumps(rep.to_dict(), indent=2))
            print("restore drill PASSED" if rep.ok else "restore drill FAILED")
            sys.exit(0 if rep.ok else 1)
        elif args.backup_cmd == "status":
            store = Store(cfg.data_dir / "forge.db")
            last = store.get_setting(LAST_DRILL_KEY)
            when = _dt.datetime.fromtimestamp(float(last), _dt.timezone.utc).isoformat() if last else "never"
            print(f"last successful restore drill: {when}")
    except (BackupError, BackupCryptoError) as e:
        sys.exit(f"backup error: {e}")


def cmd_retention(args) -> None:
    cfg = load_project_config(args.project_file)
    store = Store(cfg.data_dir / "forge.db")
    if args.retention_cmd in ("hold", "release"):
        rid = args.task
        r = store.find_root_by_ticket(cfg.project.id, rid)
        rid = r.id if r else rid
        (hold(store, rid, args.by, args.reason or "") if args.retention_cmd == "hold"
         else release_hold(store, rid, args.by))
        print(f"{args.retention_cmd}: {rid}")
        return
    policy = RetentionPolicy.from_project(cfg.project.policy)
    plan = plan_retention(store, cfg.project.id, policy)
    confirmed = getattr(args, "yes", False)
    if args.retention_cmd == "plan" or not confirmed:
        if args.json:
            print(json.dumps(plan.to_dict(), indent=2))
        else:
            print(f"policy: failed candidates {policy.failed_candidates_days} d, raw diagnostics "
                  f"{policy.raw_diagnostics_days} d; accepted results and provenance kept")
            print(f"would delete {len(plan.delete)} artifact(s) and {len(plan.branches)} candidate branch(es); "
                  f"{len(plan.kept)} kept; held roots: {plan.held_roots or 'none'}")
            for i in plan.delete[:50]:
                print(f"  {i.digest[:12]} {i.reason} ({i.evidence}, root {i.root_id}, {i.age_days} d)")
        if args.retention_cmd == "apply" and not confirmed:
            print("dry run: pass --yes to delete (and --export DIR to keep a copy first)")
        return
    rep = apply_retention(store, ArtifactStore(cfg.data_dir / "artifacts"), plan,
                          repo_path=Path(cfg.project.repo_path), export_dir=Path(args.export) if args.export else None,
                          by=args.by)
    print(json.dumps(rep.to_dict() | {"deleted": len(rep.deleted)}, indent=2))


def register(sub) -> None:
    sp = sub.add_parser("backup", help="encrypted backups: create / verify / restore / drill (credentials excluded)")
    bsub = sp.add_subparsers(dest="backup_cmd", required=True)
    for name, hlp in (("create", "write an encrypted backup (db, repo bundle, artifacts, approvals/provenance)"),
                      ("verify", "decrypt and check every hash, the database and the repository bundle"),
                      ("restore", "restore into an empty directory (never over the live project)"),
                      ("drill", "back up, restore into scratch space and compare with the live project"),
                      ("status", "show the last successful restore drill")):
        b = bsub.add_parser(name, help=hlp)
        b.add_argument("--passphrase-file", help="file outside every project (chmod 600); "
                                                 "else FORGE_BACKUP_PASSPHRASE")
        if name in ("verify", "restore"):
            b.add_argument("archive")
        if name == "create":
            b.add_argument("--out")
        if name == "verify":
            b.add_argument("--json", action="store_true")
        if name == "restore":
            b.add_argument("--to", required=True)
        if name == "drill":
            b.add_argument("--keep", help="also keep the drill archive at this path")
        b.set_defaults(fn=cmd_backup)
    sp = sub.add_parser("retention", help="enforce failed-candidate (14 d) and raw-diagnostic (30 d) retention")
    rsub = sp.add_subparsers(dest="retention_cmd", required=True)
    r = rsub.add_parser("plan", help="show what retention would delete")
    r.add_argument("--json", action="store_true")
    r.set_defaults(fn=cmd_retention)
    r = rsub.add_parser("apply", help="delete expired artifacts and candidate branches (dry run without --yes)")
    r.add_argument("--yes", action="store_true")
    r.add_argument("--export", help="copy everything that will be deleted here first")
    r.add_argument("--json", action="store_true")
    r.set_defaults(fn=cmd_retention)
    for name in ("hold", "release"):
        r = rsub.add_parser(name, help=f"{name} a retention hold on a task (e.g. an unresolved defect)")
        r.add_argument("task")
        r.add_argument("--reason")
        r.set_defaults(fn=cmd_retention)
