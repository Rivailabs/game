"""Billing integration: Razorpay and Paddle adapters behind one interface.

* **Webhook signatures** are verified before anything is parsed:
  Razorpay sends ``X-Razorpay-Signature`` = hex HMAC-SHA256 of the raw body with the webhook
  secret; Paddle Billing sends ``Paddle-Signature: ts=<unix>;h1=<hex>`` where ``h1`` is HMAC-SHA256
  of ``"<ts>:<raw body>"``, and the timestamp must be recent (replay window).
* Events are normalized (subscription activated/renewed/cancelled, payment, refund), stored once
  per (provider, event id) in an append-only table, and applied idempotently.
* Money movements go to an append-only money ledger with the currency, the tax-inclusive flag of
  the price and the provider reference; refunds are recorded as negative entries, never edits.
* Refunds and subscription calls go through an injected transport; there is no network here and no
  default HTTP client, so nothing can call a payment provider by accident.
* Orders are refused while the displayed price does not state whether tax is included.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import time
from dataclasses import dataclass, field
from typing import Callable, Optional, Protocol

from .db import HostedDB
from .plans import Price, sellable_price
from .tenancy import NotFound, Principal, TenantService


class SignatureError(Exception):
    pass


class BillingError(Exception):
    pass


@dataclass
class BillingEvent:
    provider: str
    event_id: str
    kind: str  # subscription_active | subscription_renewed | subscription_cancelled | payment | refund | other
    raw_type: str
    tenant_id: Optional[str] = None
    external_id: Optional[str] = None  # subscription / payment id
    plan_ref: Optional[str] = None
    amount_minor: Optional[int] = None
    currency: Optional[str] = None
    period_start: Optional[float] = None
    period_end: Optional[float] = None
    payload: dict = field(default_factory=dict)


Transport = Callable[[str, str, dict, Optional[dict]], tuple[int, dict]]


class BillingAdapter(Protocol):
    name: str

    def verify(self, headers: dict[str, str], raw: bytes) -> None: ...

    def parse(self, raw: bytes) -> BillingEvent: ...

    def refund(self, payment_id: str, amount_minor: int, reason: str) -> dict: ...


def _hmac_hex(secret: str, msg: bytes) -> str:
    return hmac.new(secret.encode(), msg, hashlib.sha256).hexdigest()


def _iso_ts(v) -> Optional[float]:
    if v in (None, ""):
        return None
    if isinstance(v, (int, float)):
        return float(v)
    import datetime as _dt

    return _dt.datetime.fromisoformat(str(v).replace("Z", "+00:00")).timestamp()


class RazorpayAdapter:
    name = "razorpay"
    TYPES = {"subscription.activated": "subscription_active", "subscription.charged": "subscription_renewed",
             "subscription.cancelled": "subscription_cancelled", "subscription.halted": "subscription_cancelled",
             "subscription.completed": "subscription_cancelled", "payment.captured": "payment",
             "refund.processed": "refund"}

    def __init__(self, webhook_secret: str, transport: Optional[Transport] = None, *, key_id: str = ""):
        self.secret, self.transport, self.key_id = webhook_secret, transport, key_id

    def verify(self, headers: dict[str, str], raw: bytes) -> None:
        sig = {k.lower(): v for k, v in headers.items()}.get("x-razorpay-signature", "")
        if not sig or not hmac.compare_digest(sig, _hmac_hex(self.secret, raw)):
            raise SignatureError("Razorpay webhook signature mismatch")

    def parse(self, raw: bytes) -> BillingEvent:
        d = json.loads(raw)
        etype = d.get("event", "")
        payload = d.get("payload", {})
        sub = (payload.get("subscription") or {}).get("entity") or {}
        pay = (payload.get("payment") or {}).get("entity") or {}
        ref = (payload.get("refund") or {}).get("entity") or {}
        notes = sub.get("notes") or pay.get("notes") or ref.get("notes") or {}
        ev_id = d.get("id") or hashlib.sha256(raw).hexdigest()  # header x-razorpay-event-id preferred upstream
        kind = self.TYPES.get(etype, "other")
        amount = ref.get("amount") if kind == "refund" else pay.get("amount")
        return BillingEvent(
            provider=self.name, event_id=ev_id, kind=kind, raw_type=etype,
            tenant_id=notes.get("tenant_id") if isinstance(notes, dict) else None,
            external_id=sub.get("id") or ref.get("payment_id") or pay.get("id"), plan_ref=sub.get("plan_id"),
            amount_minor=amount, currency=(ref.get("currency") or pay.get("currency")),
            period_start=_iso_ts(sub.get("current_start")), period_end=_iso_ts(sub.get("current_end")), payload=d)

    def refund(self, payment_id: str, amount_minor: int, reason: str) -> dict:
        if self.transport is None:
            raise BillingError("no Razorpay transport configured")
        status, data = self.transport("POST", f"https://api.razorpay.com/v1/payments/{payment_id}/refund",
                                      {"Content-Type": "application/json"},
                                      {"amount": amount_minor, "notes": {"reason": reason}})
        if status >= 300:
            raise BillingError(f"Razorpay refund failed ({status}): {data}")
        return data


class PaddleAdapter:
    name = "paddle"
    TYPES = {"subscription.activated": "subscription_active", "subscription.created": "subscription_active",
             "subscription.updated": "subscription_renewed", "subscription.canceled": "subscription_cancelled",
             "transaction.completed": "payment", "adjustment.created": "refund", "adjustment.updated": "refund"}

    def __init__(self, webhook_secret: str, transport: Optional[Transport] = None, *, tolerance_s: int = 300,
                 clock=time.time):
        self.secret, self.transport, self.tolerance, self.clock = webhook_secret, transport, tolerance_s, clock

    def verify(self, headers: dict[str, str], raw: bytes) -> None:
        h = {k.lower(): v for k, v in headers.items()}.get("paddle-signature", "")
        parts = dict(p.split("=", 1) for p in h.split(";") if "=" in p)
        ts, h1 = parts.get("ts"), parts.get("h1")
        if not ts or not h1:
            raise SignatureError("Paddle-Signature header missing ts or h1")
        if abs(self.clock() - int(ts)) > self.tolerance:
            raise SignatureError("Paddle webhook timestamp outside the replay window")
        if not hmac.compare_digest(h1, _hmac_hex(self.secret, ts.encode() + b":" + raw)):
            raise SignatureError("Paddle webhook signature mismatch")

    def parse(self, raw: bytes) -> BillingEvent:
        d = json.loads(raw)
        etype = d.get("event_type", "")
        data = d.get("data") or {}
        custom = data.get("custom_data") or {}
        period = data.get("current_billing_period") or {}
        items = data.get("items") or []
        kind = self.TYPES.get(etype, "other")
        if kind == "refund" and data.get("action") != "refund":
            kind = "other"
        totals = (data.get("details") or {}).get("totals") or data.get("totals") or {}
        amount = totals.get("total") if totals else data.get("amount")
        return BillingEvent(
            provider=self.name, event_id=d.get("event_id") or hashlib.sha256(raw).hexdigest(), kind=kind,
            raw_type=etype, tenant_id=custom.get("tenant_id"),
            external_id=data.get("transaction_id") if kind == "refund" else data.get("id"),
            plan_ref=((items[0].get("price") or {}).get("id") if items else None),
            amount_minor=int(amount) if amount not in (None, "") else None,
            currency=data.get("currency_code"), period_start=_iso_ts(period.get("starts_at")),
            period_end=_iso_ts(period.get("ends_at")), payload=d)

    def refund(self, payment_id: str, amount_minor: int, reason: str) -> dict:
        if self.transport is None:
            raise BillingError("no Paddle transport configured")
        status, data = self.transport("POST", "https://api.paddle.com/adjustments", {"Content-Type": "application/json"},
                                      {"action": "refund", "transaction_id": payment_id, "reason": reason,
                                       "type": "partial", "items": [], "amount": str(amount_minor)})
        if status >= 300:
            raise BillingError(f"Paddle refund failed ({status}): {data}")
        return data


class BillingService:
    def __init__(self, db: HostedDB, tenants: TenantService, adapters: dict[str, BillingAdapter],
                 plan_refs: dict[str, str], prices: dict):
        self.db, self.tenants, self.adapters = db, tenants, adapters
        self.plan_refs = plan_refs  # provider plan/price id -> forge plan
        self.prices = prices

    def checkout_price(self, plan: str, region: str) -> Price:
        """The price an order may be taken at; refuses while tax inclusion is undeclared."""
        return sellable_price(self.prices, plan, region)

    def handle_webhook(self, provider: str, headers: dict[str, str], raw: bytes) -> dict:
        adapter = self.adapters.get(provider)
        if adapter is None:
            raise BillingError(f"unknown billing provider {provider}")
        adapter.verify(headers, raw)  # before parsing anything
        ev = adapter.parse(raw)
        with self.db.tx() as c:
            dup = c.execute("SELECT 1 FROM billing_events WHERE provider=? AND event_id=?",
                            (provider, ev.event_id)).fetchone()
            if dup:
                return {"status": "duplicate", "event_id": ev.event_id}
            tenant_ok = ev.tenant_id and c.execute("SELECT 1 FROM tenants WHERE id=?", (ev.tenant_id,)).fetchone()
            c.execute("INSERT INTO billing_events(provider, event_id, type, tenant_id, external_id, amount_minor, "
                      "currency, payload, received_at) VALUES(?,?,?,?,?,?,?,?,?)",
                      (provider, ev.event_id, ev.raw_type, ev.tenant_id if tenant_ok else None, ev.external_id,
                       ev.amount_minor, ev.currency, json.dumps(ev.payload, sort_keys=True), self.db.clock()))
            if not tenant_ok:
                return {"status": "unassigned", "event_id": ev.event_id,
                        "note": "no matching tenant; held for manual reconciliation"}
            self._apply(c, ev)
        return {"status": "applied", "event_id": ev.event_id, "kind": ev.kind}

    def _apply(self, c, ev: BillingEvent) -> None:
        now = self.db.clock()
        if ev.kind in ("subscription_active", "subscription_renewed"):
            plan = self.plan_refs.get(ev.plan_ref or "", None)
            row = c.execute("SELECT plan FROM subscriptions WHERE tenant_id=?", (ev.tenant_id,)).fetchone()
            plan = plan or (row["plan"] if row else None)
            if plan is None:
                raise BillingError(f"unknown plan reference {ev.plan_ref}")
            c.execute("INSERT INTO subscriptions(tenant_id, provider, external_id, plan, status, period_start, period_end, "
                      "updated_at) VALUES(?,?,?,?,?,?,?,?) ON CONFLICT(tenant_id) DO UPDATE SET provider=excluded.provider, "
                      "external_id=excluded.external_id, plan=excluded.plan, status=excluded.status, "
                      "period_start=coalesce(excluded.period_start, period_start), "
                      "period_end=coalesce(excluded.period_end, period_end), updated_at=excluded.updated_at",
                      (ev.tenant_id, ev.provider, ev.external_id or "", plan, "active", ev.period_start, ev.period_end, now))
            c.execute("UPDATE tenants SET plan=? WHERE id=?", (plan, ev.tenant_id))
        elif ev.kind == "subscription_cancelled":
            c.execute("UPDATE subscriptions SET status='cancelled', updated_at=? WHERE tenant_id=?", (now, ev.tenant_id))
        elif ev.kind in ("payment", "refund") and ev.amount_minor is not None:
            sign = -1 if ev.kind == "refund" else 1
            t = c.execute("SELECT plan, region FROM tenants WHERE id=?", (ev.tenant_id,)).fetchone()
            price = self.prices.get((t["plan"], t["region"])) if t else None
            tax_inclusive = None if price is None or price.tax_inclusive is None else int(price.tax_inclusive)
            c.execute("INSERT INTO money_ledger(tenant_id, provider, kind, amount_minor, currency, tax_inclusive, ref, at) "
                      "VALUES(?,?,?,?,?,?,?,?)", (ev.tenant_id, ev.provider, ev.kind, sign * abs(ev.amount_minor),
                                                  ev.currency or "", tax_inclusive, ev.external_id or ev.event_id, now))

    def request_refund(self, actor: Principal, provider: str, payment_id: str, amount_minor: int, reason: str) -> dict:
        self.tenants.authorize(actor, "manage_billing", actor.tenant_id)
        if amount_minor <= 0 or not reason.strip():
            raise BillingError("a refund needs a positive amount and a reason")
        paid = self.db.conn.execute("SELECT coalesce(sum(amount_minor),0) FROM money_ledger WHERE tenant_id=? "
                                    "AND ref=?", (actor.tenant_id, payment_id)).fetchone()[0]
        if paid <= 0:
            raise NotFound("not found")
        if amount_minor > paid:
            raise BillingError(f"refund {amount_minor} exceeds the unrefunded amount {paid}")
        res = self.adapters[provider].refund(payment_id, amount_minor, reason)
        with self.db.tx():
            self.db.audit(actor.user_id, "refund_requested", tenant_id=actor.tenant_id, target=payment_id,
                          detail=f"{amount_minor}: {reason}")
        return {"provider_response": res,
                "note": "The ledger records the refund when the provider's refund webhook arrives."}

    def money(self, tenant_id: str) -> list[dict]:
        return [dict(r) for r in self.db.conn.execute("SELECT * FROM money_ledger WHERE tenant_id=? ORDER BY seq",
                                                      (tenant_id,))]
