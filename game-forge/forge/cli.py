"""`forge` command-line interface."""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path

from .budget import BudgetLedger, CapacityLedger, CashLedger, milestone_scope, project_scope, root_scope
from .config import build_runtime, dump_json, init_project, load_project_config
from .models import ReviewAction
from .orchestrator import Orchestrator, ReviewError
from .preflight import run_preflight, write_manifest
from .store import Store
from .taskfile import import_tasks, load_task_file
from .util import micros_to_usd, usd_to_micros

DEFAULT_PROJECT = Path(__file__).resolve().parent.parent / "projects" / "astra-kingdoms" / "project.toml"


def _ctx(args):
    cfg = load_project_config(args.project_file)
    store = Store(cfg.data_dir / "forge.db")
    return cfg, store


def _orch(args):
    cfg, store = _ctx(args)
    try:
        store.get_project(cfg.project.id)
    except Exception:
        sys.exit("project not initialised: run `forge init` first")
    return cfg, store, Orchestrator(store, build_runtime(cfg))


def _resolve_root(store: Store, project_id: str, ref: str) -> str:
    try:
        store.get_root(ref)
        return ref
    except Exception:
        r = store.find_root_by_ticket(project_id, ref)
        if r is None:
            sys.exit(f"no task {ref!r}")
        return r.id


def cmd_init(args):
    cfg, store = _ctx(args)
    init_project(cfg, store)
    print(f"initialised {cfg.project.id}: db={cfg.data_dir / 'forge.db'} accepted branch={cfg.project.accepted_branch}")


def cmd_preflight(args):
    cfg = load_project_config(args.project_file)
    creds = {}
    try:
        from .config import build_providers

        for name, p in build_providers(cfg).items():
            broker = getattr(p, "broker", None)
            if broker is not None:
                creds[name] = broker.has_credential(name)
    except Exception:
        pass
    m = run_preflight(cfg.data_dir, unity_editor=args.unity_editor, provider_credentials=creds)
    path = write_manifest(m, args.out or cfg.data_dir)
    if args.json:
        print(m.model_dump_json(indent=2))
    else:
        print(f"toolchain manifest: {path}")
        for t in m.tools.values():
            print(f"  {t.name:8} {'found' if t.found else 'MISSING':8} {t.version or '':16} "
                  f"{'verified' if t.verified else 'not verified'}  {t.note}")
        print(f"  GPUs: {m.gpus or 'none detected'}")
        print(f"  devices: {m.devices or 'none'}")
        print("  not verified:")
        for n in m.not_verified:
            print(f"    - {n}")
        print(f"  compatible with supported profile: {m.compatible}")
        for n in m.compatibility_notes:
            print(f"    * {n}")


def cmd_import(args):
    cfg, store = _ctx(args)
    created, skipped = import_tasks(store, cfg.project.id, load_task_file(args.task_file),
                                    default_root_ceiling_micros=cfg.default_root_ceiling_micros)
    print(f"imported {len(created)} task(s) as DRAFT; {len(skipped)} already present (root IDs are immutable)")


def cmd_approve(args):
    cfg, store, orch = _orch(args)
    rid = _resolve_root(store, cfg.project.id, args.task)
    root = store.get_root(rid)
    try:
        if root.state.value == "DRAFT":
            root = orch.approve_task(rid, by=args.by)
        else:
            root = orch.review(rid, ReviewAction.APPROVE, reviewer=args.by, candidate_hash=args.candidate,
                               acknowledge_gate_change=args.ack_gate_change, accept_incomplete=args.accept_incomplete)
    except ReviewError as e:
        sys.exit(f"refused: {e}")
    print(f"{root.ticket or root.id}: {root.state.value} ({root.state_reason})")


def cmd_review(args):
    cfg, store, orch = _orch(args)
    rid = _resolve_root(store, cfg.project.id, args.task)
    try:
        root = orch.review(rid, ReviewAction(args.action), reviewer=args.by, candidate_hash=args.candidate,
                           correction=args.correction or "", failure_category=args.category)
    except ReviewError as e:
        sys.exit(f"refused: {e}")
    print(f"{root.ticket or root.id}: {root.state.value} ({root.state_reason})")


def cmd_run(args):
    cfg, store, orch = _orch(args)
    ticks = 0
    while True:
        rep = orch.tick()
        for a in rep.actions:
            print(a)
        ticks += 1
        if not args.loop:
            if not rep.changed or ticks >= args.max_ticks:
                break
            continue
        if args.max_ticks and ticks >= args.max_ticks:
            break
        time.sleep(args.interval)


def cmd_status(args):
    cfg, store, orch = _orch(args)
    roots = store.list_roots(cfg.project.id)
    if args.json:
        print(dump_json({"status": orch.status(), "tasks": [
            {"id": r.id, "ticket": r.ticket, "title": r.title, "state": r.state.value, "reason": r.state_reason}
            for r in roots]}))
        return
    st = orch.status()
    print(f"project {cfg.project.id}  dispatch {'STOPPED' if st['dispatch_stopped'] else 'enabled'}  {st['by_state']}")
    for r in roots:
        print(f"  {r.ticket or r.id:>4}  {r.state.value:18} {r.title[:60]:60} {r.state_reason[:70]}")


def cmd_events(args):
    cfg, store = _ctx(args)
    rid = _resolve_root(store, cfg.project.id, args.task) if args.task else None
    for ev in store.events(root_id=rid, limit=args.limit):
        print(f"{ev['seq']:6} {ev['type']:24} {ev['root_id'] or '-':24} {json.dumps(ev['payload'])[:160]}")


def cmd_review_server(args):
    cfg, store, orch = _orch(args)
    from .web import ReviewApp, serve

    serve(ReviewApp(store, {cfg.project.id: orch}, reviewer=args.by), host=args.host, port=args.port)


def cmd_budget(args):
    cfg, store = _ctx(args)
    ledger = BudgetLedger(store)
    if args.budget_cmd == "set":
        scope = args.scope
        if ":" not in scope:
            scope = {"project": project_scope(cfg.project.id)}.get(scope, milestone_scope(scope))
        ledger.set_cap(scope, usd_to_micros(args.usd), by=args.by)
        print(f"cap for {scope} set to {micros_to_usd(ledger.get_cap(scope))} (explicit owner change)")
        return
    scopes = [project_scope(cfg.project.id)] + [milestone_scope(m.id) for m in store.list_milestones(cfg.project.id)]
    if args.task:
        scopes.append(root_scope(_resolve_root(store, cfg.project.id, args.task)))
    print(f"dispatch: {'STOPPED' if ledger.dispatch_stopped() else 'enabled'}")
    print(f"{'scope':36} {'cap':>14} {'settled':>14} {'reserved':>14} {'pending':>14} {'available':>14}")
    for s in (ledger.summary(x) for x in scopes):
        print(f"{s.scope:36} {micros_to_usd(s.cap):>14} {micros_to_usd(s.settled):>14} {micros_to_usd(s.reserved):>14} "
              f"{micros_to_usd(s.pending_charge):>14} {micros_to_usd(s.available):>14}")


def cmd_stop(args):
    cfg, store = _ctx(args)
    BudgetLedger(store).stop_dispatch(by=args.by, reason=args.reason or "cli")
    print("new dispatch STOPPED; committed jobs remain visible until settled")


def cmd_resume(args):
    cfg, store = _ctx(args)
    BudgetLedger(store).resume_dispatch(by=args.by)
    print("dispatch resumed")


def _minor(v: str) -> int:
    from decimal import Decimal

    return int((Decimal(v) * 100).to_integral_value())


def cmd_ledger(args):
    cfg, store = _ctx(args)
    if args.ledger_cmd == "add-expense":
        alloc = json.loads(args.allocation) if args.allocation else {cfg.project.id: 1.0}
        eid = CashLedger(store).add_expense(
            date=args.date, vendor=args.vendor, invoice_id=args.invoice, currency=args.currency,
            cash_paid_minor=_minor(args.cash), taxes_fees_minor=_minor(args.fees),
            promo_credit_minor=_minor(args.promo), normal_rate_minor=_minor(args.normal_rate) if args.normal_rate else None,
            milestone_id=args.milestone, category=args.category, allocation=alloc, note=args.note or "")
        print(f"recorded {eid}")
    elif args.ledger_cmd == "hours":
        hid = CapacityLedger(store).log(date=args.date, hours=args.hours, project_key=args.project_key or cfg.project.id,
                                        milestone_id=args.milestone, activity=args.activity or "", kind=args.kind)
        print(f"recorded {hid}")
    else:
        print(dump_json({"cash": CashLedger(store).report(args.currency), "hours": CapacityLedger(store).summary()}))


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="forge", description="Game Forge R1: reliable code/build/device loop")
    p.add_argument("--project-file", default=os.environ.get("FORGE_PROJECT", str(DEFAULT_PROJECT)))
    p.add_argument("--by", default=os.environ.get("FORGE_OWNER", "owner"), help="who is acting (recorded)")
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("init", help="create the database, project, milestones, caps and accepted branch").set_defaults(fn=cmd_init)
    sp = sub.add_parser("preflight", help="record the toolchain manifest / compatibility report")
    sp.add_argument("--out")
    sp.add_argument("--unity-editor")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(fn=cmd_preflight)
    sp = sub.add_parser("import", help="import a structured task file (TOML or JSON) as DRAFT tasks")
    sp.add_argument("task_file")
    sp.set_defaults(fn=cmd_import)
    sp = sub.add_parser("approve", help="approve a DRAFT task, or a candidate awaiting approval")
    sp.add_argument("task")
    sp.add_argument("--candidate", help="exact candidate hash being approved")
    sp.add_argument("--ack-gate-change", action="store_true")
    sp.add_argument("--accept-incomplete", action="store_true")
    sp.set_defaults(fn=cmd_approve)
    sp = sub.add_parser("review", help="repair / hold / cancel / resume a task")
    sp.add_argument("task")
    sp.add_argument("action", choices=["repair", "hold", "cancel", "resume"])
    sp.add_argument("--candidate")
    sp.add_argument("--correction")
    sp.add_argument("--category")
    sp.set_defaults(fn=cmd_review)
    sp = sub.add_parser("run", help="run the scheduler (one pass until idle, or --loop)")
    sp.add_argument("--loop", action="store_true")
    sp.add_argument("--interval", type=float, default=10.0)
    sp.add_argument("--max-ticks", type=int, default=50)
    sp.set_defaults(fn=cmd_run)
    sp = sub.add_parser("status")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(fn=cmd_status)
    sp = sub.add_parser("events")
    sp.add_argument("task", nargs="?")
    sp.add_argument("--limit", type=int, default=200)
    sp.set_defaults(fn=cmd_events)
    sp = sub.add_parser("review-server", help="local review web UI (127.0.0.1)")
    sp.add_argument("--host", default="127.0.0.1")
    sp.add_argument("--port", type=int, default=8765)
    sp.set_defaults(fn=cmd_review_server)
    sp = sub.add_parser("budget")
    bsub = sp.add_subparsers(dest="budget_cmd", required=True)
    b = bsub.add_parser("show")
    b.add_argument("--task")
    b.set_defaults(fn=cmd_budget)
    b = bsub.add_parser("set", help="explicitly set a cap: scope = project | <milestone> | root:<id>")
    b.add_argument("scope")
    b.add_argument("usd")
    b.set_defaults(fn=cmd_budget)
    sp = sub.add_parser("stop-dispatch", help="owner kill switch")
    sp.add_argument("--reason")
    sp.set_defaults(fn=cmd_stop)
    sub.add_parser("resume-dispatch").set_defaults(fn=cmd_resume)
    sp = sub.add_parser("ledger", help="cash and founder-hours ledgers")
    lsub = sp.add_subparsers(dest="ledger_cmd", required=True)
    le = lsub.add_parser("add-expense")
    le.add_argument("--date", required=True)
    le.add_argument("--vendor", required=True)
    le.add_argument("--invoice")
    le.add_argument("--cash", required=True, help="cash paid (major units, e.g. 1234.50)")
    le.add_argument("--fees", default="0", help="taxes and fees")
    le.add_argument("--promo", default="0", help="promotional credit consumed (not cash)")
    le.add_argument("--normal-rate", help="equivalent usage at the normal rate")
    le.add_argument("--currency", default="INR")
    le.add_argument("--milestone")
    le.add_argument("--category", default="shared_ai_gpu_build")
    le.add_argument("--allocation", help='JSON shares, e.g. {"astra":0.7,"forge":0.3}')
    le.add_argument("--note")
    le.set_defaults(fn=cmd_ledger)
    lh = lsub.add_parser("hours")
    lh.add_argument("--date", required=True)
    lh.add_argument("--hours", type=float, required=True)
    lh.add_argument("--project-key")
    lh.add_argument("--milestone")
    lh.add_argument("--activity")
    lh.add_argument("--kind", default="development")
    lh.set_defaults(fn=cmd_ledger)
    lr = lsub.add_parser("report")
    lr.add_argument("--currency", default="INR")
    lr.set_defaults(fn=cmd_ledger)
    return p


def main(argv: list[str] | None = None) -> None:
    args = build_parser().parse_args(argv)
    args.fn(args)


if __name__ == "__main__":  # pragma: no cover
    main()
