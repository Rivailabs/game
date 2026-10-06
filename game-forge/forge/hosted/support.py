"""Support ticket intake with *stated* (not guaranteed) response expectations.

Plan: "Publish support boundaries and response expectations actually staffed by the business.
Unresolved arbitrary-game debugging is not hidden inside a promise of unlimited priority support."
and the entitlement table's human-support row.

* Maker: asynchronous setup/bug intake; no guaranteed response SLA.
* Studio: the proposed two-business-day initial response for up to two supported-workflow incidents
  per month applies **only when staffing is confirmed** (a flag the operator sets). Until then, and
  for a third incident in a month, the ticket is asynchronous with no stated response time.
* Debugging an arbitrary game outside the supported template is outside the support boundary; the
  ticket says so and points to a separately priced guided service.
* Descriptions are redacted before they are stored (no keys or tokens in tickets).
"""

from __future__ import annotations

import datetime as _dt
import secrets

from ..credentials import redact
from .db import HostedDB
from .flags import Flags
from .tenancy import Principal, TenantService

CATEGORIES = ("setup", "supported_workflow_bug", "billing", "security", "account", "arbitrary_game_debugging", "other")
SEVERITIES = ("low", "normal", "high", "security")

MAKER_TEXT = "Asynchronous intake. There is no guaranteed response time (no SLA)."
STUDIO_TEXT = ("Initial response expected within two business days (supported-workflow incident {n} of 2 this "
               "month). This is a stated expectation, not a guaranteed SLA.")
STUDIO_UNSTAFFED = ("Studio's two-business-day expectation is not active yet (support staffing not confirmed); "
                    "handled asynchronously with no stated response time.")
STUDIO_OVER = ("This month's two supported-workflow incidents are used; handled asynchronously with no stated "
               "response time.")
OUT_OF_BOUNDARY = ("Debugging a game outside the supported template is outside the support boundary. We can scope it "
                   "as a separately priced guided service; nothing is promised under the subscription.")
SECURITY_TEXT = "Security reports are triaged as received; credentials involved should be revoked immediately."


class SupportDesk:
    def __init__(self, db: HostedDB, tenants: TenantService, flags: Flags):
        self.db, self.tenants, self.flags = db, tenants, flags

    def _incidents_this_month(self, tenant_id: str) -> int:
        now = _dt.datetime.fromtimestamp(self.db.clock(), tz=_dt.timezone.utc)
        start = now.replace(day=1, hour=0, minute=0, second=0, microsecond=0).timestamp()
        return self.db.conn.execute("SELECT count(*) FROM tickets WHERE tenant_id=? AND counts_against_allowance=1 "
                                    "AND created_at>=?", (tenant_id, start)).fetchone()[0]

    def open_ticket(self, actor: Principal, *, category: str, severity: str, summary: str, description: str) -> dict:
        self.tenants.authorize(actor, "open_ticket", actor.tenant_id)
        if category not in CATEGORIES or severity not in SEVERITIES:
            raise ValueError("unknown category or severity")
        plan = self.tenants.tenant(actor.tenant_id)["plan"]
        counts = 0
        if category == "arbitrary_game_debugging":
            expectation = OUT_OF_BOUNDARY
        elif category == "security" or severity == "security":
            expectation = SECURITY_TEXT
        elif plan == "studio" and category == "supported_workflow_bug":
            if not self.flags.studio_support_staffed:
                expectation = STUDIO_UNSTAFFED
            else:
                n = self._incidents_this_month(actor.tenant_id) + 1
                if n <= 2:
                    expectation, counts = STUDIO_TEXT.format(n=n), 1
                else:
                    expectation = STUDIO_OVER
        else:
            expectation = MAKER_TEXT if plan == "maker" else "Asynchronous intake; no stated response time."
        tid = f"tkt_{secrets.token_hex(6)}"
        with self.db.tx() as c:
            c.execute("INSERT INTO tickets(id, tenant_id, user_id, category, severity, summary, description, expectation, "
                      "counts_against_allowance, status, created_at) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                      (tid, actor.tenant_id, actor.user_id, category, severity, redact(summary)[:200],
                       redact(description)[:10_000], expectation, counts, "open", self.db.clock()))
            self.db.audit(actor.user_id, "ticket_opened", tenant_id=actor.tenant_id, target=tid, detail=category)
        return {"ticket_id": tid, "expectation": expectation, "counts_against_allowance": bool(counts)}

    def tickets(self, actor: Principal) -> list[dict]:
        self.tenants.authorize(actor, "open_ticket", actor.tenant_id)
        return [dict(r) for r in self.db.conn.execute("SELECT * FROM tickets WHERE tenant_id=? ORDER BY created_at",
                                                      (actor.tenant_id,))]
