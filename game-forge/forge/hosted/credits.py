"""Managed-credits ledger: implements the plan's job-outcome table exactly.

Plan ("Managed credits are a later separately metered service"). Disabled unless the separate
managed-credits flag is on (and the hosted flag). The table:

| Job outcome                                   | Customer ledger treatment                                   |
|-----------------------------------------------|-------------------------------------------------------------|
| Not submitted / rejected before acceptance    | Release the reservation; no usage charge                    |
| Successful output within the authorised scope | Settle the disclosed actual usage; release the unused part  |
| Platform fault prevents delivery              | Restore the customer's credits; upstream charge is Forge's  |
| Provider fails without a usable output        | Restore the customer's credits (default); vendor cost Forge's|
| Valid output rejected for taste/direction     | The disclosed completed-generation charge stands            |
| Provider status unknown after a timeout       | Reconcile the existing job id first; no duplicate job/charge |

Mechanics:

* Credits are denominated in a published charging-schedule *unit* (``forge-credit-v1``...). Each
  job names a schedule entry (provider, model/version, price per job unit); a job can draw only on
  the account in that entry's unit, so credits from different schedules or vendors never mix.
* ``reserve`` is atomic (``BEGIN IMMEDIATE``) and checks the available balance, the account-wide
  period limit and the daily limit, all *including running reservations*; the maximum charge must
  be within the customer's authorised maximum. Reusing an idempotency key returns the existing job
  (no second reservation), which is how a retry after a timeout avoids a duplicate charge.
* Ledger rows are append-only (available / reserved / charged / Forge-cost deltas); balances are
  sums of the ledger.
"""

from __future__ import annotations

import datetime as _dt
import json
import secrets
from dataclasses import dataclass
from enum import Enum
from typing import Callable, Optional

from .db import HostedDB
from .flags import Flags, require_managed_credits
from .tenancy import NotFound, Principal, TenantService


class Outcome(str, Enum):
    NOT_SUBMITTED = "not_submitted"  # also: rejected before acceptance
    SUCCESS = "success"
    PLATFORM_FAULT = "platform_fault"
    PROVIDER_FAILURE = "provider_failure"
    TASTE_REJECTION = "taste_rejection"
    UNKNOWN = "unknown"


class CreditsError(Exception):
    pass


class CreditsExhausted(CreditsError):
    def __init__(self, msg: str, evidence: dict):
        super().__init__(msg)
        self.evidence = evidence


@dataclass(frozen=True)
class ScheduleEntry:
    schedule_id: str
    unit: str
    provider: str
    model: str
    credits_per_job_unit: int
    job_unit: str  # e.g. "image", "1k output tokens", "second of video"


@dataclass(frozen=True)
class CreditTerms:
    version: str
    expiry_days: Optional[int]
    minimum_topup: int
    refund_policy: str
    cancellation: str
    unused_balance: str
    export_access: str


@dataclass
class Limits:
    period_limit: Optional[int] = None  # account-wide per calendar month, incl. reservations
    daily_limit: Optional[int] = None


class CreditsLedger:
    def __init__(self, db: HostedDB, tenants: TenantService, flags: Flags, schedule: list[ScheduleEntry],
                 terms: CreditTerms):
        require_managed_credits(flags)
        self.db, self.tenants = db, tenants
        self.schedule = {s.schedule_id: s for s in schedule}
        self.terms = terms

    # ------------------------------------------------------------------ ledger primitives
    def _entry(self, c, tenant_id: str, unit: str, kind: str, *, available: int = 0, reserved: int = 0,
               charged: int = 0, forge_cost: int = 0, job_id: str | None = None, note: str = "") -> None:
        c.execute("INSERT INTO credit_ledger(tenant_id, unit, kind, available_delta, reserved_delta, charged_delta, "
                  "forge_cost_delta, job_id, note, at) VALUES(?,?,?,?,?,?,?,?,?,?)",
                  (tenant_id, unit, kind, available, reserved, charged, forge_cost, job_id, note, self.db.clock()))

    def balance(self, tenant_id: str, unit: str) -> dict:
        r = self.db.conn.execute(
            "SELECT coalesce(sum(available_delta),0) a, coalesce(sum(reserved_delta),0) r, coalesce(sum(charged_delta),0) c, "
            "coalesce(sum(forge_cost_delta),0) f FROM credit_ledger WHERE tenant_id=? AND unit=?",
            (tenant_id, unit)).fetchone()
        return {"available": r["a"], "reserved": r["r"], "charged": r["c"], "forge_cost": r["f"]}

    def _spent_since(self, tenant_id: str, unit: str, since: float) -> int:
        return self.db.conn.execute("SELECT coalesce(sum(charged_delta),0) FROM credit_ledger WHERE tenant_id=? "
                                    "AND unit=? AND at>=?", (tenant_id, unit, since)).fetchone()[0]

    def set_limits(self, actor: Principal, limits: Limits) -> None:
        """Limits change only by an explicit owner action; nothing raises them automatically."""
        self.tenants.authorize(actor, "manage_billing", actor.tenant_id)
        with self.db.tx() as c:
            c.execute("INSERT INTO credit_limits(tenant_id, period_limit, daily_limit, set_by, set_at) VALUES(?,?,?,?,?) "
                      "ON CONFLICT(tenant_id) DO UPDATE SET period_limit=excluded.period_limit, "
                      "daily_limit=excluded.daily_limit, set_by=excluded.set_by, set_at=excluded.set_at",
                      (actor.tenant_id, limits.period_limit, limits.daily_limit, actor.user_id, self.db.clock()))
            self.db.audit(actor.user_id, "credit_limits_set", tenant_id=actor.tenant_id, detail=json.dumps(limits.__dict__))

    def limits(self, tenant_id: str) -> Limits:
        r = self.db.conn.execute("SELECT period_limit, daily_limit FROM credit_limits WHERE tenant_id=?",
                                 (tenant_id,)).fetchone()
        return Limits(r["period_limit"], r["daily_limit"]) if r else Limits()

    # ------------------------------------------------------------------ top-up
    def top_up(self, actor: Principal, unit: str, credits: int, *, payment_ref: str, terms_version: str) -> dict:
        self.tenants.authorize(actor, "manage_billing", actor.tenant_id)
        if terms_version != self.terms.version:
            raise CreditsError(f"accept the current credit terms ({self.terms.version}) before buying credits")
        if credits < self.terms.minimum_topup:
            raise CreditsError(f"the minimum top-up is {self.terms.minimum_topup} credits")
        if unit not in {s.unit for s in self.schedule.values()}:
            raise CreditsError(f"no published charging schedule uses unit {unit}")
        with self.db.tx() as c:
            self._entry(c, actor.tenant_id, unit, "topup", available=credits, note=payment_ref)
            self.db.audit(actor.user_id, "credits_topup", tenant_id=actor.tenant_id, detail=f"{credits} {unit}")
        return self.balance(actor.tenant_id, unit)

    # ------------------------------------------------------------------ jobs
    def reserve(self, actor: Principal, *, schedule_id: str, job_units: int, params: dict, idempotency_key: str,
                authorised_max_credits: int) -> dict:
        self.tenants.authorize(actor, "start_workflow", actor.tenant_id)
        entry = self.schedule.get(schedule_id)
        if entry is None:
            raise CreditsError(f"unknown charging schedule {schedule_id}")
        max_charge = entry.credits_per_job_unit * job_units
        if max_charge > authorised_max_credits:
            raise CreditsError(f"estimated maximum {max_charge} exceeds the customer's authorised {authorised_max_credits}")
        with self.db.tx() as c:
            existing = c.execute("SELECT * FROM credit_jobs WHERE tenant_id=? AND idempotency_key=?",
                                 (actor.tenant_id, idempotency_key)).fetchone()
            if existing:
                return dict(existing)  # retry after a timeout: same job, no second reservation
            bal = self.balance(actor.tenant_id, entry.unit)
            lim = self.limits(actor.tenant_id)
            now = self.db.clock()
            day = _dt.datetime.fromtimestamp(now, tz=_dt.timezone.utc).replace(hour=0, minute=0, second=0, microsecond=0)
            month = day.replace(day=1)
            evidence = {**bal, "needed": max_charge, "limits": lim.__dict__,
                        "options": ["top up", "lower the job size", "raise the limit explicitly"]}
            if bal["available"] < max_charge:
                raise CreditsExhausted("not enough credits; work paused with nothing charged", evidence)
            if lim.daily_limit is not None and self._spent_since(actor.tenant_id, entry.unit, day.timestamp()) + \
                    bal["reserved"] + max_charge > lim.daily_limit:
                raise CreditsExhausted("daily limit reached (running reservations included)", evidence)
            if lim.period_limit is not None and self._spent_since(actor.tenant_id, entry.unit, month.timestamp()) + \
                    bal["reserved"] + max_charge > lim.period_limit:
                raise CreditsExhausted("account period limit reached (running reservations included)", evidence)
            jid = f"cj_{secrets.token_hex(8)}"
            c.execute("INSERT INTO credit_jobs(id, tenant_id, unit, idempotency_key, schedule_id, provider, model, params, "
                      "max_units, authorised_by, status, created_at, updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)",
                      (jid, actor.tenant_id, entry.unit, idempotency_key, schedule_id, entry.provider, entry.model,
                       json.dumps(params, sort_keys=True), max_charge, actor.user_id, "RESERVED", now, now))
            self._entry(c, actor.tenant_id, entry.unit, "reserve", available=-max_charge, reserved=max_charge, job_id=jid)
            return dict(c.execute("SELECT * FROM credit_jobs WHERE id=?", (jid,)).fetchone())

    def _job(self, tenant_id: str, job_id: str):
        row = self.db.conn.execute("SELECT * FROM credit_jobs WHERE id=? AND tenant_id=?", (job_id, tenant_id)).fetchone()
        if row is None:
            raise NotFound("not found")
        return row

    def mark_submitted(self, actor: Principal, job_id: str, provider_job_id: str) -> None:
        with self.db.tx() as c:
            j = self._job(actor.tenant_id, job_id)
            if j["status"] != "RESERVED":
                raise CreditsError(f"job is {j['status']}")
            c.execute("UPDATE credit_jobs SET status='SUBMITTED', provider_job_id=?, updated_at=? WHERE id=?",
                      (provider_job_id, self.db.clock(), job_id))

    def resolve(self, actor: Principal, job_id: str, outcome: Outcome, *, actual_credits: int = 0,
                upstream_cost: int = 0) -> dict:
        """Apply the outcome table. ``actual_credits``: the disclosed usage charge (schedule-priced);
        ``upstream_cost``: what the vendor charged Forge, recorded as Forge's cost where the table says so."""
        with self.db.tx() as c:
            j = self._job(actor.tenant_id, job_id)
            if j["status"] in ("SETTLED", "RELEASED", "RESTORED"):
                raise CreditsError(f"job already closed ({j['status']})")
            held, unit, t = j["max_units"], j["unit"], actor.tenant_id
            if outcome == Outcome.NOT_SUBMITTED:
                self._entry(c, t, unit, "release", available=held, reserved=-held, job_id=job_id,
                            note="not submitted / rejected before acceptance: no charge")
                status, charged = "RELEASED", 0
            elif outcome in (Outcome.SUCCESS, Outcome.TASTE_REJECTION):
                if actual_credits < 0:
                    raise CreditsError("actual usage cannot be negative")
                charge = min(actual_credits, held)
                over = actual_credits - charge
                self._entry(c, t, unit, "settle", available=held - charge, reserved=-held, charged=charge,
                            forge_cost=max(over, 0), job_id=job_id,
                            note=("taste rejection: completed-generation charge stands; regeneration is a new job"
                                  if outcome == Outcome.TASTE_REJECTION else "settled actual usage")
                            + (f"; {over} above the authorised maximum absorbed by Forge" if over > 0 else ""))
                status, charged = "SETTLED", charge
            elif outcome in (Outcome.PLATFORM_FAULT, Outcome.PROVIDER_FAILURE):
                self._entry(c, t, unit, "restore", available=held, reserved=-held, forge_cost=upstream_cost,
                            job_id=job_id, note=f"{outcome.value}: customer credits restored; upstream cost is Forge's")
                status, charged = "RESTORED", 0
            elif outcome == Outcome.UNKNOWN:
                c.execute("UPDATE credit_jobs SET status='UNKNOWN', outcome=?, updated_at=? WHERE id=?",
                          (outcome.value, self.db.clock(), job_id))
                return {"status": "UNKNOWN", "note": "reservation held; reconcile the provider job id before any "
                                                     "resubmission"}
            else:  # pragma: no cover - exhaustive enum
                raise CreditsError(f"unhandled outcome {outcome}")
            c.execute("UPDATE credit_jobs SET status=?, outcome=?, charged_units=?, updated_at=? WHERE id=?",
                      (status, outcome.value, charged, self.db.clock(), job_id))
            return {"status": status, "job_charged": charged, "balance": self.balance(t, unit)}

    def reconcile(self, actor: Principal, job_id: str, lookup: Callable[[str], dict]) -> dict:
        """Resolve an UNKNOWN job from the provider's own record of the existing job id."""
        j = self._job(actor.tenant_id, job_id)
        if j["status"] != "UNKNOWN":
            raise CreditsError(f"job is {j['status']}, not UNKNOWN")
        if not j["provider_job_id"]:
            return self.resolve(actor, job_id, Outcome.NOT_SUBMITTED)
        r = lookup(j["provider_job_id"])
        state = r.get("status")
        if state == "completed":
            return self.resolve(actor, job_id, Outcome.SUCCESS, actual_credits=int(r.get("credits", 0)))
        if state == "failed":
            return self.resolve(actor, job_id, Outcome.PROVIDER_FAILURE, upstream_cost=int(r.get("upstream_cost", 0)))
        if state == "not_found":
            return self.resolve(actor, job_id, Outcome.NOT_SUBMITTED)
        return {"status": "UNKNOWN", "note": f"provider still reports {state!r}; reservation stays held"}

    def obligations(self, tenant_id: str) -> dict:
        """Outstanding service obligations: unused balances and held reservations, per unit."""
        units = [r[0] for r in self.db.conn.execute("SELECT DISTINCT unit FROM credit_ledger WHERE tenant_id=?",
                                                    (tenant_id,))]
        return {u: self.balance(tenant_id, u) for u in units}
