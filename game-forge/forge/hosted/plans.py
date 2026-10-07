"""Offerings, test prices and the initial paid entitlement table (plan: "Launch Forge with prices
that have defined obligations" and "Initial paid Forge entitlement experiment").

These are the plan's *proposed test configuration*, not a validated cost envelope. Neither tier
includes pooled Claude Code usage or any model-credit allowance: inference is billed through the
customer's own provider account (BYOK).
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

MB = 1024 * 1024
GB = 1024 * MB


@dataclass(frozen=True)
class Entitlements:
    plan: str
    seats: int
    active_projects: int
    storage_bytes: int
    upload_bundles_per_month: int
    max_bundle_bytes: int
    review_retention_days: int
    concurrent_workflows: int
    inference: str
    managed_credits_included: int
    support: str
    export: str
    pooled: bool


MAKER = Entitlements(
    plan="maker", seats=1, active_projects=3, storage_bytes=5 * GB, upload_bundles_per_month=10,
    max_bundle_bytes=250 * MB, review_retention_days=30, concurrent_workflows=1,
    inference="Customer's own approved account or API billing", managed_credits_included=0,
    support="Asynchronous setup/bug intake; no guaranteed response SLA",
    export="Complete project and accepted artifacts remain exportable", pooled=False)

STUDIO = Entitlements(
    plan="studio", seats=5, active_projects=10, storage_bytes=25 * GB, upload_bundles_per_month=50,
    max_bundle_bytes=250 * MB, review_retention_days=90, concurrent_workflows=3,
    inference="Customer's own approved account or API billing", managed_credits_included=0,
    support="Proposed two-business-day initial response for up to two supported-workflow incidents per month, "
            "only after staffing is confirmed",
    export="Complete project and accepted artifacts remain exportable, with team permissions and audit history",
    pooled=True)

PLANS = {"maker": MAKER, "studio": STUDIO}


@dataclass(frozen=True)
class Price:
    plan: str
    region: str  # "IN" or "ROW"
    currency: str
    amount_minor: int  # paise / cents per month
    #: Whether the displayed amount includes applicable taxes. None = not yet declared, and the
    #: plan says orders must not be accepted until it is stated.
    tax_inclusive: Optional[bool]
    test_price: bool = True


#: Proposed test prices. tax_inclusive is deliberately undeclared: the operator must state it.
TEST_PRICES = {
    ("maker", "IN"): Price("maker", "IN", "INR", 99_900, None),
    ("maker", "ROW"): Price("maker", "ROW", "USD", 1_900, None),
    ("studio", "IN"): Price("studio", "IN", "INR", 499_900, None),
    ("studio", "ROW"): Price("studio", "ROW", "USD", 9_900, None),
}


class PriceNotSellable(Exception):
    pass


def sellable_price(prices: dict, plan: str, region: str) -> Price:
    p = prices.get((plan, region))
    if p is None:
        raise PriceNotSellable(f"no price for {plan} in {region}")
    if p.tax_inclusive is None:
        raise PriceNotSellable(f"{plan}/{region}: state whether the displayed price includes applicable taxes "
                               "before accepting orders")
    return p
