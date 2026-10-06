"""Tenants, team identities, roles and per-tenant authorization.

* A tenant has one plan (Maker: 1 seat; Studio: 5 seats). Adding a member beyond the seat count is
  refused; nothing is charged automatically.
* Roles: owner, admin, approver, member, viewer. Permissions are an explicit table.
* Every lookup is scoped by tenant. A principal asking for another tenant's project, object or key
  gets :class:`NotFound` (the same answer as for an ID that does not exist), so identifiers cannot
  be probed across tenants.
* Support staff have no standing access. A tenant owner grants time-limited, reasoned access;
  every staff read is checked against an unexpired grant and audited.
"""

from __future__ import annotations

import secrets
from dataclasses import dataclass

from .db import HostedDB
from .flags import Flags, require_hosted
from .plans import PLANS

ROLES = ("owner", "admin", "approver", "member", "viewer")

PERMISSIONS: dict[str, set[str]] = {
    "owner": {"manage_members", "manage_billing", "manage_keys", "manage_projects", "upload_review", "view_review",
              "approve", "start_workflow", "view_usage", "open_ticket", "grant_support", "export"},
    "admin": {"manage_members", "manage_keys", "manage_projects", "upload_review", "view_review", "approve",
              "start_workflow", "view_usage", "open_ticket", "export"},
    "approver": {"upload_review", "view_review", "approve", "start_workflow", "view_usage", "open_ticket"},
    "member": {"upload_review", "view_review", "start_workflow", "open_ticket"},
    "viewer": {"view_review"},
}


class NotFound(Exception):
    pass


class Forbidden(Exception):
    pass


class SeatLimit(Exception):
    pass


@dataclass(frozen=True)
class Principal:
    user_id: str
    tenant_id: str
    role: str

    def can(self, permission: str) -> bool:
        return permission in PERMISSIONS.get(self.role, set())


def _id(prefix: str) -> str:
    return f"{prefix}_{secrets.token_hex(8)}"


class TenantService:
    def __init__(self, db: HostedDB, flags: Flags):
        require_hosted(flags)
        self.db, self.flags = db, flags

    # ------------------------------------------------------------------ identities
    def _user(self, email: str, *, staff: bool = False) -> str:
        email = email.strip().lower()
        row = self.db.conn.execute("SELECT id FROM users WHERE email=?", (email,)).fetchone()
        if row:
            return row["id"]
        uid = _id("usr")
        self.db.conn.execute("INSERT INTO users(id, email, created_at, staff) VALUES(?,?,?,?)",
                             (uid, email, self.db.clock(), int(staff)))
        return uid

    def create_staff(self, email: str) -> str:
        with self.db.tx():
            return self._user(email, staff=True)

    def create_tenant(self, name: str, plan: str, owner_email: str, *, region: str = "ROW") -> Principal:
        if plan not in PLANS:
            raise ValueError(f"unknown plan {plan!r}")
        with self.db.tx() as c:
            tid = _id("ten")
            c.execute("INSERT INTO tenants(id, name, plan, region, created_at) VALUES(?,?,?,?,?)",
                      (tid, name, plan, region, self.db.clock()))
            uid = self._user(owner_email)
            c.execute("INSERT INTO memberships(tenant_id, user_id, role, created_at) VALUES(?,?,?,?)",
                      (tid, uid, "owner", self.db.clock()))
            self.db.audit(uid, "tenant_created", tenant_id=tid, detail=plan)
        return Principal(uid, tid, "owner")

    def tenant(self, tenant_id: str) -> dict:
        row = self.db.conn.execute("SELECT * FROM tenants WHERE id=?", (tenant_id,)).fetchone()
        if not row:
            raise NotFound("tenant")
        return dict(row)

    def principal(self, user_id: str, tenant_id: str) -> Principal:
        row = self.db.conn.execute("SELECT role FROM memberships WHERE tenant_id=? AND user_id=?",
                                   (tenant_id, user_id)).fetchone()
        if not row:
            raise NotFound("membership")
        return Principal(user_id, tenant_id, row["role"])

    def members(self, actor: Principal) -> list[dict]:
        self.authorize(actor, "view_review", actor.tenant_id)
        return [dict(r) for r in self.db.conn.execute(
            "SELECT u.email, m.role FROM memberships m JOIN users u ON u.id=m.user_id WHERE m.tenant_id=? "
            "ORDER BY m.created_at", (actor.tenant_id,))]

    def add_member(self, actor: Principal, email: str, role: str) -> Principal:
        if role not in ROLES or role == "owner":
            raise ValueError(f"cannot add a member with role {role!r}")
        self.authorize(actor, "manage_members", actor.tenant_id)
        with self.db.tx() as c:
            plan = PLANS[self.tenant(actor.tenant_id)["plan"]]
            used = c.execute("SELECT count(*) FROM memberships WHERE tenant_id=?", (actor.tenant_id,)).fetchone()[0]
            if used >= plan.seats:
                raise SeatLimit(f"{plan.plan} includes {plan.seats} seat(s); all are in use. Remove a member or "
                                "change plan (no seat is added or charged automatically).")
            uid = self._user(email)
            c.execute("INSERT INTO memberships(tenant_id, user_id, role, created_at) VALUES(?,?,?,?)",
                      (actor.tenant_id, uid, role, self.db.clock()))
            self.db.audit(actor.user_id, "member_added", tenant_id=actor.tenant_id, target=uid, detail=role)
        return Principal(uid, actor.tenant_id, role)

    def remove_member(self, actor: Principal, user_id: str) -> None:
        self.authorize(actor, "manage_members", actor.tenant_id)
        target = self.principal(user_id, actor.tenant_id)
        if target.role == "owner":
            raise Forbidden("the owner cannot be removed")
        with self.db.tx() as c:
            c.execute("DELETE FROM memberships WHERE tenant_id=? AND user_id=?", (actor.tenant_id, user_id))
            self.db.audit(actor.user_id, "member_removed", tenant_id=actor.tenant_id, target=user_id)

    # ------------------------------------------------------------------ authorization
    def authorize(self, actor: Principal, permission: str, tenant_id: str) -> None:
        if actor.tenant_id != tenant_id:
            raise NotFound("not found")  # never reveal that another tenant's resource exists
        current = self.db.conn.execute("SELECT role FROM memberships WHERE tenant_id=? AND user_id=?",
                                       (tenant_id, actor.user_id)).fetchone()
        if current is None:
            raise NotFound("not found")  # membership revoked since the principal was issued
        if permission not in PERMISSIONS.get(current["role"], set()):
            raise Forbidden(f"role {current['role']} cannot {permission}")

    def project(self, actor: Principal, project_id: str, permission: str = "view_review") -> dict:
        row = self.db.conn.execute("SELECT * FROM projects WHERE id=? AND tenant_id=?",
                                   (project_id, actor.tenant_id)).fetchone()
        if row is None:
            raise NotFound("not found")
        self.authorize(actor, permission, row["tenant_id"])
        return dict(row)

    # ------------------------------------------------------------------ support access
    def grant_support_access(self, actor: Principal, staff_email: str, *, hours: float, reason: str) -> str:
        self.authorize(actor, "grant_support", actor.tenant_id)
        if not reason.strip():
            raise ValueError("support access needs a reason")
        if hours <= 0 or hours > 72:
            raise ValueError("support access lasts between 0 and 72 hours")
        row = self.db.conn.execute("SELECT id FROM users WHERE email=? AND staff=1",
                                   (staff_email.strip().lower(),)).fetchone()
        if row is None:
            raise NotFound("staff user")
        gid = _id("grant")
        with self.db.tx() as c:
            c.execute("INSERT INTO support_grants(id, tenant_id, staff_user_id, granted_by, reason, expires_at, "
                      "created_at) VALUES(?,?,?,?,?,?,?)", (gid, actor.tenant_id, row["id"], actor.user_id, reason,
                                                            self.db.clock() + hours * 3600, self.db.clock()))
            self.db.audit(actor.user_id, "support_access_granted", tenant_id=actor.tenant_id, target=row["id"],
                          detail=reason)
        return gid

    def staff_read(self, staff_user_id: str, tenant_id: str, what: str) -> None:
        """Check (and audit) a staff member's read of tenant material."""
        ok = self.db.conn.execute("SELECT 1 FROM support_grants WHERE tenant_id=? AND staff_user_id=? AND expires_at>?",
                                  (tenant_id, staff_user_id, self.db.clock())).fetchone()
        with self.db.tx():
            self.db.audit(staff_user_id, "staff_read" if ok else "staff_read_denied", tenant_id=tenant_id, target=what)
        if not ok:
            raise Forbidden("no active support-access grant from this tenant")
