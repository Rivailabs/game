"""CLI: ``forge spec ...``, ``forge plan``, ``forge trace``, ``forge scaffold`` (Release 3)."""

from __future__ import annotations

import json
import sys
from pathlib import Path


def _repo(args):
    from ..config import load_project_config
    from ..planning.template import load_template
    from .repository import SpecRepository

    cfg = load_project_config(args.project_file)
    template = load_template(args.template or cfg.runtime.get("template", "turn-duel-2p"))
    return cfg, template, SpecRepository(cfg.data_dir / "specs" / cfg.project.id, cfg.project.id, template)


def _print_spec(spec) -> None:
    print(f"spec v{spec.version} [{spec.status.value}] {spec.title}: {len(spec.plannable())} requirement(s), "
          f"release countries: {', '.join(spec.release_countries) or 'not declared'}")
    for q in spec.questions:
        if q.status.value == "OPEN":
            print(f"  {'BLOCKING' if q.blocking else 'info    '} {q.id} {q.category}: {q.text}")
    pending = [r for r in spec.requirements if r.status.value in ("CHANGE_PROPOSED", "REMOVAL_PROPOSED")]
    for r in pending:
        print(f"  PROPOSED CHANGE {r.id}: approved \"{r.approved_text}\" -> \"{r.text or '(removed)'}\"")


def cmd_spec(args):
    from .repository import SpecError

    cfg, template, repo = _repo(args)
    try:
        if args.spec_cmd == "ingest":
            model_pass = None
            if args.model_pass:
                from ..config import build_providers
                from .model_pass import run_model_pass

                provider = build_providers(cfg)[args.model_pass]
                model_pass = lambda s, text: run_model_pass(s, text, provider, cfg.project.policy,  # noqa: E731
                                                            unattended=False).questions
            spec, report = repo.ingest(args.brief, model_pass=model_pass)
            print(report.markdown())
            _print_spec(spec)
        elif args.spec_cmd == "status":
            spec = repo.latest()
            if spec is None:
                sys.exit("no specification yet: `forge spec ingest <brief>`")
            _print_spec(spec)
        elif args.spec_cmd == "answer":
            _print_spec(repo.answer(args.question, args.text, by=args.by,
                                    overrides=[x for x in (args.overrides or "").split(",") if x], kind=args.kind))
        elif args.spec_cmd == "dismiss":
            _print_spec(repo.dismiss(args.question, args.reason, by=args.by))
        elif args.spec_cmd in ("accept-change", "reject-change"):
            fn = repo.accept_change if args.spec_cmd == "accept-change" else repo.reject_change
            _print_spec(fn(args.requirement, by=args.by))
        elif args.spec_cmd == "approve":
            latest = repo.latest()
            spec = repo.approve(latest.version if latest else 0, by=args.by,
                                release_countries=[c for c in (args.release_countries or "").split(",") if c] or None)
            print(f"approved spec v{spec.version} (digest {spec.digest[:16]}); "
                  f"{len(repo.acceptance_cases())} active acceptance case(s)")
        elif args.spec_cmd == "changes":
            v = args.version or (repo.latest().version if repo.latest() else 0)
            print(repo.change_report(v).markdown())
    except SpecError as e:
        sys.exit(f"refused: {e}")


def cmd_plan(args):
    from ..planning.parallel import Capacity, from_backlog, schedule
    from ..planning.planner import PlanningError, plan_backlog

    cfg, template, repo = _repo(args)
    spec = repo.approved() if not args.preview else repo.latest()
    if spec is None:
        sys.exit("no approved specification: `forge spec approve` first (or --preview)")
    try:
        backlog = plan_backlog(spec, template, repo.acceptance_cases(), allow_preview=args.preview)
    except PlanningError as e:
        sys.exit(f"refused: {e}")
    print(backlog.markdown())
    sch = schedule(from_backlog(backlog), Capacity(max_parallel=args.max_parallel,
                                                  resources={"workspace": args.max_parallel, "unity": args.unity,
                                                             "device": args.devices, "gpu": 1}))
    print(sch.markdown())
    if not backlog.preview:
        out = Path(args.out) if args.out else cfg.data_dir / "plans" / f"backlog-v{spec.version}.json"
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(backlog.to_task_file(), indent=2))
        out.with_suffix(".md").write_text(backlog.markdown() + "\n" + sch.markdown())
        print(f"task file: {out}\nReview it, then `forge import {out}` (tasks start as DRAFT and still need approval).")


def cmd_trace(args):
    from ..planning.planner import plan_backlog
    from ..planning.traceability import build_matrix
    from ..store import Store

    cfg, template, repo = _repo(args)
    spec = repo.approved()
    if spec is None:
        sys.exit("no approved specification")
    backlog = plan_backlog(spec, template, repo.acceptance_cases())
    db = cfg.data_dir / "forge.db"
    store = Store(db) if db.exists() else None
    m = build_matrix(spec, backlog, repo.acceptance_cases(), superseded=repo.superseded_cases(), store=store)
    print(m.to_csv() if args.csv else m.markdown())
    if not m.complete:
        sys.exit(1)


def cmd_scaffold(args):
    from ..planning.scaffold import ScaffoldError, generate
    from ..planning.template import load_template

    try:
        res = generate(load_template(args.template or "turn-duel-2p"), args.out, game_name=args.name,
                       game_id=args.game_id)
    except ScaffoldError as e:
        sys.exit(f"refused: {e}")
    print(f"generated {len(res.files)} file(s) in {res.out_dir}; run `dotnet test tests/Rules.Tests` there")


def register(sub) -> None:
    sp = sub.add_parser("spec", help="R3 document intake: brief -> versioned specification")
    sp.add_argument("--template", help="supported template id (default: [forge] template or turn-duel-2p)")
    ssub = sp.add_subparsers(dest="spec_cmd", required=True)
    s = ssub.add_parser("ingest", help="read a brief (.md/.txt/.docx); new version + change report if it changed")
    s.add_argument("brief")
    s.add_argument("--model-pass", help="provider route for an optional model review (supervised; sends the brief)")
    ssub.add_parser("status")
    s = ssub.add_parser("answer", help="answer an open question (your words become an owner requirement)")
    s.add_argument("question")
    s.add_argument("text")
    s.add_argument("--overrides", help="comma-separated requirement ids your answer supersedes")
    s.add_argument("--kind")
    s = ssub.add_parser("dismiss", help="dismiss a finding with a reason")
    s.add_argument("question")
    s.add_argument("--reason", required=True)
    for name in ("accept-change", "reject-change"):
        s = ssub.add_parser(name, help="decide a proposed change to an approved requirement")
        s.add_argument("requirement")
    s = ssub.add_parser("approve", help="approve the latest version (freezes acceptance cases)")
    s.add_argument("--release-countries", help="comma-separated, if the brief does not state them")
    s = ssub.add_parser("changes")
    s.add_argument("--version", type=int)
    sp.set_defaults(fn=cmd_spec)

    sp = sub.add_parser("plan", help="R3 milestone backlog, estimates, checkpoints and bounded parallel schedule")
    sp.add_argument("--template")
    sp.add_argument("--preview", action="store_true", help="plan an unapproved spec (cannot be exported)")
    sp.add_argument("--out")
    sp.add_argument("--max-parallel", type=int, default=2)
    sp.add_argument("--unity", type=int, default=1)
    sp.add_argument("--devices", type=int, default=1)
    sp.set_defaults(fn=cmd_plan)

    sp = sub.add_parser("trace", help="R3 requirement -> task -> test traceability matrix (exit 1 on gaps)")
    sp.add_argument("--template")
    sp.add_argument("--csv", action="store_true")
    sp.set_defaults(fn=cmd_trace)

    sp = sub.add_parser("scaffold", help="generate a new project from a supported template")
    sp.add_argument("out")
    sp.add_argument("--name", required=True)
    sp.add_argument("--game-id")
    sp.add_argument("--template")
    sp.set_defaults(fn=cmd_scaffold)
