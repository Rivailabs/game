"""CLI: ``forge hosted status``, ``forge economics``, ``forge metrics`` (Release 5)."""

from __future__ import annotations

import json
import sys
from decimal import Decimal


def cmd_hosted(args):
    from .flags import Flags
    from .plans import PLANS, TEST_PRICES

    f = Flags.from_env()
    print(f"hosted service: {'ENABLED' if f.hosted else 'disabled (default)'}; managed credits: "
          f"{'enabled' if f.managed_credits else 'disabled'}; Studio support staffed: {f.studio_support_staffed}")
    for p in PLANS.values():
        print(f"  {p.plan}: {p.seats} seat(s), {p.active_projects} projects, {p.storage_bytes // 1024 ** 3} GB, "
              f"{p.upload_bundles_per_month} bundles/month <= 250 MB, {p.review_retention_days}-day retention, "
              f"{p.concurrent_workflows} workflow(s); support: {p.support}")
    for (plan, region), price in sorted(TEST_PRICES.items()):
        tax = {None: "tax treatment NOT declared (orders refused)", True: "tax inclusive", False: "plus tax"}[
            price.tax_inclusive]
        print(f"  test price {plan}/{region}: {price.amount_minor / 100:g} {price.currency}/month, {tax}")


def cmd_economics(args):
    from .economics import (PADDLE_STANDARD, RAZORPAY_STANDARD, CustomerMonth, billings_scenario, cohort,
                            format_inr)

    fee = {"razorpay": RAZORPAY_STANDARD, "paddle": PADDLE_STANDARD}[args.fee]
    minor = lambda v: int(Decimal(v) * 100)  # noqa: E731
    m = CustomerMonth(price_minor=minor(args.price), tax_inclusive=args.tax_inclusive,
                      tax_rate_percent=Decimal(args.tax_rate), provider_usage_minor=minor(args.provider_usage),
                      refunds_chargebacks_minor=minor(args.refunds), hosting_storage_minor=minor(args.hosting),
                      support_minor=minor(args.support), fee_model=fee)
    c = cohort(m, args.customers)
    out = {**c.to_dict(), "scenario": billings_scenario(c.gross_minor // max(args.customers, 1), args.customers)}
    if args.json:
        print(json.dumps(out, indent=2, default=str))
        return
    fmt = format_inr if fee.currency == "INR" else (lambda v: f"${v / 100:,.2f}")
    for k in ("gross_minor", "tax_minor", "revenue_ex_tax_minor", "payment_fees_minor", "provider_usage_minor",
              "refunds_chargebacks_minor", "hosting_storage_minor", "support_minor", "contribution_minor"):
        print(f"{k.removesuffix('_minor'):28} {fmt(out[k])}")
    print(f"annualized run-rate (gross): {fmt(out['scenario']['annualized_run_rate_minor'])}  "
          f"({out['scenario']['note']})")
    for n in c.notes:
        print(f"note: {n}")


def cmd_metrics(args):
    from ..config import load_project_config
    from ..store import Store
    from .metrics import measure

    cfg = load_project_config(args.project_file)
    db = cfg.data_dir / "forge.db"
    if not db.exists():
        sys.exit("no Forge database: run `forge init` first")
    extra = json.loads(open(args.inputs).read()) if args.inputs else {}
    rep = measure(Store(db), cfg.project.id, scope=args.scope, human_minutes=extra.get("human_minutes"),
                  manual_fix_roots=set(extra.get("manual_fix_roots", [])), escaped_defects=extra.get("escaped_defects"),
                  external_projects_retained=extra.get("external_projects_retained"))
    print(json.dumps(rep.to_dict(), indent=2) if args.json else rep.markdown())


def register(sub) -> None:
    sp = sub.add_parser("hosted", help="R5 hosted offering status (disabled by default)")
    sp.add_argument("hosted_cmd", choices=["status"])
    sp.set_defaults(fn=cmd_hosted)

    sp = sub.add_parser("economics", help="R5 customer contribution calculator (published fee rates)")
    sp.add_argument("--price", required=True, help="price per customer per month, major units (e.g. 999)")
    sp.add_argument("--tax-inclusive", action="store_true", help="the price already includes tax")
    sp.add_argument("--tax-rate", default="18", help="tax rate percent (e.g. 18 for GST; 0 if not applicable)")
    sp.add_argument("--fee", choices=["razorpay", "paddle"], default="razorpay")
    sp.add_argument("--customers", type=int, default=1)
    sp.add_argument("--provider-usage", default="0")
    sp.add_argument("--refunds", default="0")
    sp.add_argument("--hosting", default="0")
    sp.add_argument("--support", default="0")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(fn=cmd_economics)

    sp = sub.add_parser("metrics", help="R5 offering metrics: accepted roots, cost, human minutes, ...")
    sp.add_argument("--scope", required=True, help="what workload this sample covers (stated in the report)")
    sp.add_argument("--inputs", help="JSON with human_minutes, manual_fix_roots, escaped_defects, "
                                     "external_projects_retained")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(fn=cmd_metrics)
