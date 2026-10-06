"""Unit-economics calculator.

Plan ("Managed credits are a later separately metered service"):

    Customer contribution = revenue excluding tax - provider usage - payment fees
                            - refunds/chargebacks - variable hosting/storage - variable support

Payment-fee models are configurable *published* rates, as checked by the plan (actual contracts
and tax treatment control the forecast):

* Razorpay standard: 2% of the transaction, plus GST (18%) on that fee;
* Paddle: 5% + $0.50 per checkout transaction.

All amounts are integer minor units (paise / cents) and rounding is explicit (half-up on fees), so
results are reproducible. These are arithmetic scenarios, not revenue forecasts or profit.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from decimal import ROUND_HALF_UP, Decimal



def _round(d: Decimal) -> int:
    return int(d.quantize(Decimal("1"), rounding=ROUND_HALF_UP))


@dataclass(frozen=True)
class FeeModel:
    name: str
    percent: Decimal
    fixed_minor: int = 0
    tax_on_fee_percent: Decimal = Decimal("0")
    currency: str = ""

    def fee(self, gross_minor: int) -> int:
        base = Decimal(gross_minor) * self.percent / 100 + self.fixed_minor
        return _round(base * (1 + self.tax_on_fee_percent / 100))


RAZORPAY_STANDARD = FeeModel("razorpay-standard", Decimal("2"), 0, Decimal("18"), "INR")
PADDLE_STANDARD = FeeModel("paddle-standard", Decimal("5"), 50, Decimal("0"), "USD")


@dataclass
class CustomerMonth:
    """One customer (or cohort) for one month. ``price_minor`` is what the customer pays."""

    price_minor: int
    tax_inclusive: bool
    tax_rate_percent: Decimal = Decimal("18")  # e.g. Indian GST on the subscription; configurable
    provider_usage_minor: int = 0  # managed usage only; BYOK usage is billed to the customer directly
    refunds_chargebacks_minor: int = 0
    hosting_storage_minor: int = 0
    support_minor: int = 0
    fee_model: FeeModel = RAZORPAY_STANDARD


@dataclass
class Contribution:
    gross_minor: int
    tax_minor: int
    revenue_ex_tax_minor: int
    payment_fees_minor: int
    provider_usage_minor: int
    refunds_chargebacks_minor: int
    hosting_storage_minor: int
    support_minor: int
    contribution_minor: int
    notes: list[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        return asdict(self)


def contribution(m: CustomerMonth) -> Contribution:
    if m.tax_inclusive:
        gross = m.price_minor
        revenue = _round(Decimal(gross) * 100 / (100 + m.tax_rate_percent))
        tax = gross - revenue
    else:
        revenue = m.price_minor
        tax = _round(Decimal(revenue) * m.tax_rate_percent / 100)
        gross = revenue + tax
    fees = m.fee_model.fee(gross)  # fees are charged on the amount actually collected
    contrib = revenue - m.provider_usage_minor - fees - m.refunds_chargebacks_minor - m.hosting_storage_minor - \
        m.support_minor
    notes = ["arithmetic scenario, not a forecast; actual contracts and tax treatment control"]
    if contrib < 0:
        notes.append("negative contribution: change limits, price or promise before selling")
    return Contribution(gross, tax, revenue, fees, m.provider_usage_minor, m.refunds_chargebacks_minor,
                        m.hosting_storage_minor, m.support_minor, contrib, notes)


def cohort(m: CustomerMonth, customers: int, *, months: int = 1) -> Contribution:
    one = contribution(m)
    k = customers * months
    return Contribution(**{f: getattr(one, f) * k for f in (
        "gross_minor", "tax_minor", "revenue_ex_tax_minor", "payment_fees_minor", "provider_usage_minor",
        "refunds_chargebacks_minor", "hosting_storage_minor", "support_minor", "contribution_minor")}, notes=one.notes)


def billings_scenario(price_minor: int, customers: int) -> dict:
    """The plan's run-rate arithmetic: monthly gross billings and the annualized run-rate."""
    monthly = price_minor * customers
    return {"monthly_gross_minor": monthly, "annualized_run_rate_minor": monthly * 12,
            "note": "an arithmetic scenario if that customer count and price persist; not first-year revenue"}


def studio_vs_makers(maker_minor: int, studio_minor: int, seats: int = 5) -> dict:
    five = maker_minor * seats
    return {"five_makers_minor": five, "studio_minor": studio_minor, "studio_minus_makers_minor": studio_minor - five,
            "studio_per_seat_minor": Decimal(studio_minor) / seats}


def format_inr(minor: int) -> str:
    """Indian digit grouping (e.g. 5,99,400)."""
    rupees, paise = divmod(abs(minor), 100)
    s = str(rupees)
    head, tail = s[:-3], s[-3:]
    groups = []
    while len(head) > 2:
        groups.insert(0, head[-2:])
        head = head[:-2]
    if head:
        groups.insert(0, head)
    out = ",".join(groups + [tail]) if groups else tail
    return f"{'-' if minor < 0 else ''}₹{out}" + (f".{paise:02d}" if paise else "")


