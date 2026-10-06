"""``python -m forge.production <lane> ...``: run one production lane as a command check.

Exit codes: 0 PASS, 1 FAIL, 2 NEEDS_INPUT, 3 BLOCKED/INCOMPLETE. The JSON report goes to stdout
(and to ``--report-out`` when given) so the check's log is the evidence.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="python -m forge.production")
    sub = p.add_subparsers(dest="lane", required=True)
    u = sub.add_parser("ui", help="check a screens.json against the reference strings")
    u.add_argument("screens")
    u.add_argument("--strings")
    a = sub.add_parser("audio-select", help="pick licensed clips for audio briefs and write a rights record")
    a.add_argument("briefs")
    a.add_argument("library")
    a.add_argument("--library-dir")
    a.add_argument("--rights-out", required=True)
    v = sub.add_parser("audio-verify", help="re-check a rights record (hash, licence, attribution)")
    v.add_argument("rights")
    v.add_argument("--library-dir")
    v.add_argument("--allowed", nargs="*")
    lo = sub.add_parser("localization", help="extract keys and check the string tables under an Assets folder")
    lo.add_argument("assets")
    lo.add_argument("--reference", default="en")
    lo.add_argument("--ledger", help="translation review ledger (JSON lines)")
    b = sub.add_parser("balance", help="run the simulator twice (same seed) and screen the results")
    b.add_argument("--sim-project", required=True)
    b.add_argument("--out", dest="out_dir", required=True)
    b.add_argument("--matches", type=int, default=600)
    b.add_argument("--seed", type=int, default=20261006)
    b.add_argument("--catalog", default="full")
    b.add_argument("--max-share", type=float)
    b.add_argument("--requirement")
    bv = sub.add_parser("balance-verify", help="re-read a written balance report")
    bv.add_argument("report")
    for sp in (u, a, v, lo, b, bv):
        sp.add_argument("--report-out", help="also write the JSON report here")
    args = p.parse_args(argv)

    if args.lane == "ui":
        from .ui import check_screens_file

        rep = check_screens_file(args.screens, args.strings)
    elif args.lane == "audio-select":
        from .audio import select, write_rights

        lib_path = Path(args.library)
        rep, picks = select(json.loads(Path(args.briefs).read_text()), json.loads(lib_path.read_text()),
                            library_dir=args.library_dir or lib_path.parent)
        write_rights(picks, args.rights_out)
    elif args.lane == "audio-verify":
        from .audio import verify_rights

        rep = verify_rights(args.rights, library_dir=args.library_dir, allowed=args.allowed)
    elif args.lane == "localization":
        from .localization import check_assets

        rep = check_assets(args.assets, reference=args.reference, ledger_path=args.ledger)
    elif args.lane == "balance":
        from .balance import DotnetSimRunner, Thresholds, run_balance

        res = run_balance(DotnetSimRunner(Path(args.sim_project)), args.out_dir, matches=args.matches, seed=args.seed,
                          catalog=args.catalog, thresholds=Thresholds(max_share=args.max_share,
                                                                      requirement_id=args.requirement))
        rep = res.report
        rep.write(Path(args.out_dir) / "balance.json")
    else:
        from .balance import verify_report

        rep = verify_report(args.report)
    out = getattr(args, "report_out", None)
    if out:
        rep.write(out)
    print(rep.to_json())
    print(rep.summary, file=sys.stderr)
    return rep.exit_code


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
