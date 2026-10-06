"""Budget reservations, cash ledger and founder-hours capacity ledger.

Spending model (plan: "Budget reservations recovery and resource scheduling"):

* ``available = cap - outstanding_reservations - settled_usage`` for every scope
  (root, milestone, project). A reservation must fit in *all* scopes.
* Reservation happens inside one ``BEGIN IMMEDIATE`` transaction, so two
  workers can never each spend the same remaining allowance.
* After the provider call, actual usage is settled and the remainder released.
* A call with no billable ceiling is refused in unattended mode.
* Uncertain cancellation keeps the reservation as CHARGE_PENDING
  ("provider completion/charge pending") until reconciled.
* Limits are only ever changed by an explicit owner command (``set_cap``);
  nothing in Forge raises a limit automatically.
* The owner kill switch stops new dispatch immediately; committed jobs remain
  visible until settled.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Optional

from .store import Store
from .util import new_id

DISPATCH_STOPPED_KEY = "dispatch_stopped"


class BudgetError(Exception):
    pass


class DispatchStopped(BudgetError):
    pass


class UnknownCeiling(BudgetError):
    pass


class NoCapConfigured(BudgetError):
    pass


class BudgetExceeded(BudgetError):
    def __init__(self, scope: str, needed: int, available: int):
        self.scope, self.needed, self.available = scope, needed, available
        super().__init__(f"budget exhausted for {scope}: need {needed} micros, available {available}")


@dataclass
class ScopeSummary:
    scope: str
    cap: Optional[int]
    reserved: int  # outstanding (ACTIVE + CHARGE_PENDING) beyond what is already settled
    settled: int
    pending_charge: int

    @property
    def available(self) -> Optional[int]:
        if self.cap is None:
            return None
        return self.cap - self.reserved - self.settled


def project_scope(pid: str) -> str:
    return f"project:{pid}"


def milestone_scope(mid: str) -> str:
    return f"milestone:{mid}"


def root_scope(rid: str) -> str:
    return f"root:{rid}"


_SCOPE_COLUMN = {"project": "project_id", "milestone": "milestone_id", "root": "root_id"}

# Outstanding amount still held for a reservation in each status.
_OUTSTANDING_SQL = (
    "CASE WHEN status IN ('ACTIVE','CHARGE_PENDING') "
    "THEN MAX(amount_micros - settled_micros, 0) ELSE 0 END"
)


class BudgetLedger:
    def __init__(self, store: Store):
        self.store = store

    # ------------------------------------------------------------- kill switch
    def stop_dispatch(self, by: str = "owner", reason: str = "") -> None:
        self.store.set_setting(DISPATCH_STOPPED_KEY, "1")
        self.store.append_event("dispatch_stopped", by=by, reason=reason)

    def resume_dispatch(self, by: str = "owner") -> None:
        self.store.set_setting(DISPATCH_STOPPED_KEY, "0")
        self.store.append_event("dispatch_resumed", by=by)

    def dispatch_stopped(self) -> bool:
        return self.store.get_setting(DISPATCH_STOPPED_KEY, "0") == "1"

    # ------------------------------------------------------------- caps
    def set_cap(self, scope: str, cap_micros: int, by: str = "owner") -> None:
        if cap_micros < 0:
            raise BudgetError("cap must be >= 0")
        kind = scope.split(":", 1)[0]
        if kind not in _SCOPE_COLUMN:
            raise BudgetError(f"unknown scope {scope}")
        with self.store.tx() as c:
            old = c.execute("SELECT cap_micros FROM budget_caps WHERE scope=?", (scope,)).fetchone()
            c.execute(
                "INSERT INTO budget_caps(scope,cap_micros,set_by,set_at) VALUES(?,?,?,?) "
                "ON CONFLICT(scope) DO UPDATE SET cap_micros=excluded.cap_micros, set_by=excluded.set_by, set_at=excluded.set_at",
                (scope, cap_micros, by, self.store.now()),
            )
            self.store.append_event("budget_cap_set", scope=scope, cap_micros=cap_micros,
                                    previous=old["cap_micros"] if old else None, by=by)

    def get_cap(self, scope: str) -> Optional[int]:
        r = self.store.conn.execute("SELECT cap_micros FROM budget_caps WHERE scope=?", (scope,)).fetchone()
        return int(r["cap_micros"]) if r else None

    # ------------------------------------------------------------- summaries
    def summary(self, scope: str) -> ScopeSummary:
        kind, ident = scope.split(":", 1)
        col = _SCOPE_COLUMN[kind]
        r = self.store.conn.execute(
            f"SELECT COALESCE(SUM({_OUTSTANDING_SQL}),0) AS reserved, COALESCE(SUM(settled_micros),0) AS settled, "
            f"COALESCE(SUM(CASE WHEN status='CHARGE_PENDING' THEN amount_micros ELSE 0 END),0) AS pending "
            f"FROM reservations WHERE {col}=?",
            (ident,),
        ).fetchone()
        return ScopeSummary(scope, self.get_cap(scope), int(r["reserved"]), int(r["settled"]), int(r["pending"]))

    # ------------------------------------------------------------- reservations
    def reserve(
        self,
        *,
        project_id: str,
        milestone_id: str,
        root_id: str,
        amount_micros: Optional[int],
        attempt_id: str | None = None,
        provider: str | None = None,
        purpose: str = "",
        unattended: bool = True,
    ) -> str:
        if amount_micros is None:
            if unattended:
                raise UnknownCeiling(
                    f"{provider or 'call'} has no billable ceiling; unavailable in unattended mode"
                )
            raise UnknownCeiling("supervised calls without a ceiling must be reserved manually")
        if amount_micros <= 0:
            raise BudgetError("reservation amount must be positive")
        scopes = [project_scope(project_id), milestone_scope(milestone_id), root_scope(root_id)]
        with self.store.tx() as c:
            if self.dispatch_stopped():
                raise DispatchStopped("owner stopped new dispatch")
            for scope in scopes:
                s = self.summary(scope)
                if s.cap is None:
                    raise NoCapConfigured(f"no spending cap configured for {scope}; refusing dispatch")
                if s.available is None or amount_micros > s.available:
                    self.store.append_event("reservation_refused", root_id=root_id, scope=scope,
                                            needed=amount_micros, available=s.available)
                    raise BudgetExceeded(scope, amount_micros, s.available or 0)
            rid = new_id("res")
            now = self.store.now()
            c.execute(
                "INSERT INTO reservations(id,project_id,milestone_id,root_id,attempt_id,provider,purpose,"
                "amount_micros,settled_micros,status,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,0,'ACTIVE',?,?)",
                (rid, project_id, milestone_id, root_id, attempt_id, provider, purpose, amount_micros, now, now),
            )
            self.store.append_event("budget_reserved", project_id=project_id, root_id=root_id, attempt_id=attempt_id,
                                    reservation_id=rid, amount_micros=amount_micros, provider=provider)
            return rid

    def get(self, reservation_id: str) -> dict:
        r = self.store.conn.execute("SELECT * FROM reservations WHERE id=?", (reservation_id,)).fetchone()
        if not r:
            raise BudgetError(f"unknown reservation {reservation_id}")
        return dict(r)

    def record_usage(
        self, reservation_id: str, cost_micros: int, *, model: str | None = None, usage: dict | None = None
    ) -> None:
        """Record an actual provider charge against a reservation (may be called repeatedly)."""
        if cost_micros < 0:
            raise BudgetError("usage cost cannot be negative")
        with self.store.tx() as c:
            r = self.get(reservation_id)
            if r["status"] in ("SETTLED", "RELEASED"):
                raise BudgetError(f"reservation {reservation_id} is closed ({r['status']})")
            settled = r["settled_micros"] + cost_micros
            c.execute("UPDATE reservations SET settled_micros=?, updated_at=? WHERE id=?",
                      (settled, self.store.now(), reservation_id))
            c.execute(
                "INSERT INTO usage_records(id,reservation_id,project_id,milestone_id,root_id,provider,model,"
                "cost_micros,usage_json,at) VALUES(?,?,?,?,?,?,?,?,?,?)",
                (new_id("use"), reservation_id, r["project_id"], r["milestone_id"], r["root_id"], r["provider"],
                 model, cost_micros, json.dumps(usage or {}), self.store.now()),
            )
            self.store.append_event("usage_recorded", root_id=r["root_id"], reservation_id=reservation_id,
                                    cost_micros=cost_micros, model=model)
            if settled > r["amount_micros"]:
                self.store.append_event("reservation_overrun", root_id=r["root_id"], reservation_id=reservation_id,
                                        reserved=r["amount_micros"], settled=settled)

    def settle(self, reservation_id: str, actual_micros: int | None = None, **kw) -> int:
        """Close a reservation. ``actual_micros`` (if given) is recorded as final usage.

        Returns the released remainder.
        """
        with self.store.tx() as c:
            if actual_micros:
                self.record_usage(reservation_id, actual_micros, **kw)
            r = self.get(reservation_id)
            if r["status"] in ("SETTLED", "RELEASED"):
                return 0
            status = "SETTLED" if r["settled_micros"] > 0 else "RELEASED"
            c.execute("UPDATE reservations SET status=?, updated_at=? WHERE id=?", (status, self.store.now(), reservation_id))
            released = max(r["amount_micros"] - r["settled_micros"], 0)
            self.store.append_event("budget_settled", root_id=r["root_id"], reservation_id=reservation_id,
                                    settled_micros=r["settled_micros"], released_micros=released, status=status)
            return released

    def release(self, reservation_id: str) -> int:
        """Job not submitted / rejected before acceptance: release with no extra charge."""
        return self.settle(reservation_id)

    def mark_charge_pending(self, reservation_id: str, reason: str = "") -> None:
        with self.store.tx() as c:
            r = self.get(reservation_id)
            if r["status"] != "ACTIVE":
                return
            c.execute("UPDATE reservations SET status='CHARGE_PENDING', updated_at=? WHERE id=?",
                      (self.store.now(), reservation_id))
            self.store.append_event("charge_pending", root_id=r["root_id"], reservation_id=reservation_id,
                                    reason=reason or "provider completion/charge pending")

    def resolve_charge_pending(self, reservation_id: str, actual_micros: int) -> int:
        with self.store.tx() as c:
            r = self.get(reservation_id)
            if r["status"] != "CHARGE_PENDING":
                raise BudgetError("reservation is not charge-pending")
            c.execute("UPDATE reservations SET status='ACTIVE' WHERE id=?", (reservation_id,))
            return self.settle(reservation_id, actual_micros)

    def reservations(self, root_id: str | None = None, status: str | None = None) -> list[dict]:
        q, args = "SELECT * FROM reservations WHERE 1=1", []
        if root_id:
            q += " AND root_id=?"
            args.append(root_id)
        if status:
            q += " AND status=?"
            args.append(status)
        return [dict(r) for r in self.store.conn.execute(q + " ORDER BY created_at", args)]

    def root_cost(self, root_id: str) -> int:
        r = self.store.conn.execute(
            "SELECT COALESCE(SUM(settled_micros),0) AS s FROM reservations WHERE root_id=?", (root_id,)
        ).fetchone()
        return int(r["s"])


# ============================================================================ cash ledger


class CashLedger:
    """One cash ledger for both projects, with explicit allocation rules.

    Amounts are integer minor units (paise / cents). An expense is recorded once;
    allocations split it between projects with shares that sum to exactly 100%,
    using largest-remainder rounding so allocated amounts sum to the expense.
    Promotional credit is recorded separately and is never counted as cash.
    """

    def __init__(self, store: Store):
        self.store = store

    @staticmethod
    def _shares_ppm(allocation: dict[str, float]) -> dict[str, int]:
        if not allocation:
            raise BudgetError("allocation rule must name at least one project")
        if any(v < 0 for v in allocation.values()):
            raise BudgetError("allocation shares must be non-negative")
        total = sum(allocation.values())
        if total <= 0:
            raise BudgetError("allocation shares must sum to a positive value")
        if abs(total - 1.0) > 1e-9 and abs(total - 100.0) > 1e-9:
            raise BudgetError(f"allocation shares must sum to 1.0 (or 100); got {total}")
        raw = {k: v / total * 1_000_000 for k, v in allocation.items()}
        ppm = {k: int(v) for k, v in raw.items()}
        rem = 1_000_000 - sum(ppm.values())
        for k in sorted(raw, key=lambda k: raw[k] - ppm[k], reverse=True)[:rem]:
            ppm[k] += 1
        return ppm

    @staticmethod
    def _split(amount: int, ppm: dict[str, int]) -> dict[str, int]:
        exact = {k: amount * v / 1_000_000 for k, v in ppm.items()}
        out = {k: int(x) if amount >= 0 else -int(-x) for k, x in exact.items()}
        rem = amount - sum(out.values())
        step = 1 if rem >= 0 else -1
        for k in sorted(exact, key=lambda k: abs(exact[k] - out[k]), reverse=True)[: abs(rem)]:
            out[k] += step
        return out

    def add_expense(
        self,
        *,
        date: str,
        vendor: str,
        invoice_id: str | None,
        cash_paid_minor: int,
        taxes_fees_minor: int = 0,
        promo_credit_minor: int = 0,
        normal_rate_minor: int | None = None,
        currency: str = "INR",
        milestone_id: str | None = None,
        category: str = "shared_ai_gpu_build",
        allocation: dict[str, float] | None = None,
        note: str = "",
        corrects_id: str | None = None,
    ) -> str:
        if cash_paid_minor < 0 and not corrects_id:
            raise BudgetError("negative amounts are only allowed as corrections (corrects_id)")
        if (promo_credit_minor < 0 or taxes_fees_minor < 0) and not corrects_id:
            raise BudgetError("fees and promo credit must be non-negative")
        if allocation is None:
            allocation = {"unallocated": 1.0}
        ppm = self._shares_ppm(allocation)
        eid = new_id("exp")
        total_cash = cash_paid_minor + taxes_fees_minor
        with self.store.tx() as c:
            if invoice_id and not corrects_id:
                dup = c.execute(
                    "SELECT id FROM cash_expenses WHERE vendor=? AND invoice_id=? AND corrects_id IS NULL",
                    (vendor, invoice_id),
                ).fetchone()
                if dup:
                    raise BudgetError(f"invoice {vendor}/{invoice_id} already recorded as {dup['id']} (no double counting)")
            c.execute(
                "INSERT INTO cash_expenses(id,date,vendor,invoice_id,currency,cash_paid_minor,taxes_fees_minor,"
                "promo_credit_minor,normal_rate_minor,milestone_id,category,allocation_rule,note,recorded_at,corrects_id)"
                " VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (eid, date, vendor, invoice_id, currency, cash_paid_minor, taxes_fees_minor, promo_credit_minor,
                 normal_rate_minor, milestone_id, category, json.dumps(allocation, sort_keys=True), note,
                 self.store.now(), corrects_id),
            )
            for key, amt in self._split(total_cash, ppm).items():
                c.execute(
                    "INSERT INTO expense_allocations(expense_id,project_key,share_ppm,amount_minor) VALUES(?,?,?,?)",
                    (eid, key, ppm[key], amt),
                )
            self.store.append_event("expense_recorded", expense_id=eid, vendor=vendor, invoice_id=invoice_id,
                                    total_cash_minor=total_cash, promo_credit_minor=promo_credit_minor,
                                    allocation=allocation)
        return eid

    def expenses(self) -> list[dict]:
        return [dict(r) for r in self.store.conn.execute("SELECT * FROM cash_expenses ORDER BY date, recorded_at")]

    def report(self, currency: str = "INR") -> dict:
        c = self.store.conn
        total = c.execute(
            "SELECT COALESCE(SUM(cash_paid_minor+taxes_fees_minor),0) t, COALESCE(SUM(promo_credit_minor),0) p, "
            "COALESCE(SUM(COALESCE(normal_rate_minor, cash_paid_minor+taxes_fees_minor+promo_credit_minor)),0) n "
            "FROM cash_expenses WHERE currency=?",
            (currency,),
        ).fetchone()
        per = {
            r["project_key"]: int(r["a"])
            for r in c.execute(
                "SELECT project_key, SUM(amount_minor) a FROM expense_allocations ea JOIN cash_expenses e "
                "ON e.id=ea.expense_id WHERE e.currency=? GROUP BY project_key",
                (currency,),
            )
        }
        per_milestone = {
            (r["milestone_id"] or "-"): int(r["a"])
            for r in c.execute(
                "SELECT milestone_id, SUM(cash_paid_minor+taxes_fees_minor) a FROM cash_expenses WHERE currency=? "
                "GROUP BY milestone_id",
                (currency,),
            )
        }
        combined = int(total["t"])
        if sum(per.values()) != combined:  # invariant: allocation never double counts
            raise BudgetError("allocation invariant violated: allocated total != combined cash")
        return {
            "currency": currency,
            "combined_cash_minor": combined,
            "promo_credit_minor": int(total["p"]),
            "normal_rate_equivalent_minor": int(total["n"]),
            "per_project_minor": per,
            "per_milestone_minor": per_milestone,
        }


class CapacityLedger:
    """Founder hours: a separate capacity ledger (not cash)."""

    KINDS = ("development", "review", "support", "incident", "operations")

    def __init__(self, store: Store):
        self.store = store

    def log(self, *, date: str, hours: float, project_key: str, milestone_id: str | None = None,
            activity: str = "", kind: str = "development") -> str:
        if hours <= 0 or hours > 24:
            raise BudgetError("hours must be in (0, 24]")
        if kind not in self.KINDS:
            raise BudgetError(f"kind must be one of {self.KINDS}")
        hid = new_id("hrs")
        with self.store.tx() as c:
            c.execute(
                "INSERT INTO founder_hours(id,date,hours,project_key,milestone_id,activity,kind,recorded_at)"
                " VALUES(?,?,?,?,?,?,?,?)",
                (hid, date, hours, project_key, milestone_id, activity, kind, self.store.now()),
            )
            self.store.append_event("founder_hours_logged", hours=hours, project_key=project_key,
                                    milestone_id=milestone_id, kind=kind)
        return hid

    def summary(self, milestone_id: str | None = None) -> dict:
        q, args = "SELECT kind, SUM(hours) h FROM founder_hours", []
        if milestone_id:
            q += " WHERE milestone_id=?"
            args.append(milestone_id)
        rows = {r["kind"]: float(r["h"]) for r in self.store.conn.execute(q + " GROUP BY kind", args)}
        return {"by_kind": rows, "total": sum(rows.values())}
