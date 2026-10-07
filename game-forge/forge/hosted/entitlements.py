"""Usage and entitlement ledger implementing the Maker/Studio table.

* Active hosted projects, hosted storage, uploaded review bundles per billing month (each at most
  250 MB), review retention and concurrent workflows are checked atomically (``BEGIN IMMEDIATE``)
  against the tenant's plan, so limits hold under concurrency.
* A refused upload never deletes the local result and never adds a charge: the refusal carries the
  current use, the expiry dates and the options (prune or export).
* The billing month comes from the subscription period when there is one (a renewal resets the
  monthly upload count); retained bytes keep counting against storage until they expire or are
  deleted.
* Every grant writes an append-only usage-ledger entry.
"""

from __future__ import annotations

import datetime as _dt
import secrets
from dataclasses import dataclass, field

from .db import HostedDB
from .plans import PLANS, Entitlements
from .tenancy import NotFound, Principal, TenantService


@dataclass
class Refusal(Exception):
    reason: str
    usage: dict = field(default_factory=dict)
    options: list[str] = field(default_factory=list)

    def __str__(self) -> str:
        return self.reason


class EntitlementService:
    def __init__(self, db: HostedDB, tenants: TenantService):
        self.db, self.tenants = db, tenants

    def plan(self, tenant_id: str) -> Entitlements:
        return PLANS[self.tenants.tenant(tenant_id)["plan"]]

    def period(self, tenant_id: str) -> str:
        sub = self.db.conn.execute("SELECT period_start, period_end FROM subscriptions WHERE tenant_id=?",
                                   (tenant_id,)).fetchone()
        now = self.db.clock()
        if sub and sub["period_start"] and sub["period_end"] and sub["period_start"] <= now < sub["period_end"]:
            return f"sub:{int(sub['period_start'])}"
        d = _dt.datetime.fromtimestamp(now, tz=_dt.timezone.utc)
        return f"cal:{d.year:04d}-{d.month:02d}"

    def _ledger(self, tenant_id: str, kind: str, amount: int, ref: str) -> None:
        self.db.conn.execute("INSERT INTO usage_ledger(tenant_id, period, kind, amount, ref, at) VALUES(?,?,?,?,?,?)",
                             (tenant_id, self.period(tenant_id), kind, amount, ref, self.db.clock()))

    def usage(self, tenant_id: str) -> dict:
        c = self.db.conn
        now = self.db.clock()
        plan = self.plan(tenant_id)
        stored = c.execute("SELECT coalesce(sum(size_bytes),0) FROM objects WHERE tenant_id=? AND deleted_at IS NULL "
                           "AND (expires_at IS NULL OR expires_at>?)", (tenant_id, now)).fetchone()[0]
        uploads = c.execute("SELECT coalesce(sum(amount),0) FROM usage_ledger WHERE tenant_id=? AND period=? "
                            "AND kind='review_upload'", (tenant_id, self.period(tenant_id))).fetchone()[0]
        projects = c.execute("SELECT count(*) FROM projects WHERE tenant_id=? AND archived=0", (tenant_id,)).fetchone()[0]
        active = c.execute("SELECT count(*) FROM workflows WHERE tenant_id=? AND finished_at IS NULL",
                           (tenant_id,)).fetchone()[0]
        expiring = [dict(r) for r in c.execute(
            "SELECT id, name, size_bytes, expires_at FROM objects WHERE tenant_id=? AND deleted_at IS NULL "
            "AND expires_at>? ORDER BY expires_at LIMIT 10", (tenant_id, now))]
        return {"plan": plan.plan, "storage_bytes": stored, "storage_limit": plan.storage_bytes,
                "uploads_this_period": uploads, "upload_limit": plan.upload_bundles_per_month,
                "max_bundle_bytes": plan.max_bundle_bytes, "active_projects": projects,
                "project_limit": plan.active_projects, "active_workflows": active,
                "workflow_limit": plan.concurrent_workflows, "retention_days": plan.review_retention_days,
                "next_expiring": expiring, "period": self.period(tenant_id)}

    # ------------------------------------------------------------------ projects
    def create_project(self, actor: Principal, name: str, *, hosted_review_allowed: bool = False) -> str:
        self.tenants.authorize(actor, "manage_projects", actor.tenant_id)
        with self.db.tx() as c:
            u = self.usage(actor.tenant_id)
            if u["active_projects"] >= u["project_limit"]:
                raise Refusal(f"{u['plan']} includes {u['project_limit']} active hosted projects; archive one or "
                              "export it first", u, ["archive a project", "export a project"])
            pid = f"prj_{secrets.token_hex(8)}"
            c.execute("INSERT INTO projects(id, tenant_id, name, hosted_review_allowed, created_at) VALUES(?,?,?,?,?)",
                      (pid, actor.tenant_id, name, int(hosted_review_allowed), self.db.clock()))
            self._ledger(actor.tenant_id, "project_created", 1, pid)
            self.db.audit(actor.user_id, "project_created", tenant_id=actor.tenant_id, target=pid)
        return pid

    def set_hosted_review(self, actor: Principal, project_id: str, allowed: bool) -> None:
        self.tenants.project(actor, project_id, "manage_projects")
        with self.db.tx() as c:
            c.execute("UPDATE projects SET hosted_review_allowed=? WHERE id=? AND tenant_id=?",
                      (int(allowed), project_id, actor.tenant_id))
            self.db.audit(actor.user_id, "hosted_review_policy", tenant_id=actor.tenant_id, target=project_id,
                          detail=str(allowed))

    # ------------------------------------------------------------------ uploads
    def preview_upload(self, actor: Principal, size_bytes: int) -> dict:
        """What an upload would use, and when it expires, shown *before* the upload."""
        self.tenants.authorize(actor, "upload_review", actor.tenant_id)
        u = self.usage(actor.tenant_id)
        return {**u, "bundle_bytes": size_bytes, "storage_after": u["storage_bytes"] + size_bytes,
                "expires_at": self.db.clock() + u["retention_days"] * 86400,
                "allowed": self._upload_problem(u, size_bytes) is None}

    @staticmethod
    def _upload_problem(u: dict, size: int) -> str | None:
        if size > u["max_bundle_bytes"]:
            return f"bundle is {size} bytes; the limit is {u['max_bundle_bytes']} bytes (250 MB)"
        if u["uploads_this_period"] >= u["upload_limit"]:
            return f"{u['upload_limit']} review bundles already uploaded this billing period"
        if u["storage_bytes"] + size > u["storage_limit"]:
            return "hosted storage limit would be exceeded"
        return None

    def reserve_upload(self, actor: Principal, project_id: str, name: str, size_bytes: int, sha256: str,
                       kind: str = "review_bundle") -> tuple[str, float]:
        """Atomically check limits and record the object. Returns (object id, expiry)."""
        self.tenants.project(actor, project_id, "upload_review")
        with self.db.tx() as c:
            u = self.usage(actor.tenant_id)
            problem = self._upload_problem(u, size_bytes)
            if problem:
                raise Refusal(problem + ". Your local result is unchanged; nothing was charged.", u,
                              ["prune expired or old bundles", "export the project", "upload a smaller bundle"])
            oid = f"obj_{secrets.token_hex(12)}"
            expires = self.db.clock() + u["retention_days"] * 86400
            c.execute("INSERT INTO objects(id, tenant_id, project_id, kind, name, size_bytes, sha256, created_at, "
                      "expires_at) VALUES(?,?,?,?,?,?,?,?,?)", (oid, actor.tenant_id, project_id, kind, name,
                                                                size_bytes, sha256, self.db.clock(), expires))
            self._ledger(actor.tenant_id, "review_upload", 1, oid)
            self._ledger(actor.tenant_id, "storage_bytes_added", size_bytes, oid)
        return oid, expires

    def delete_object(self, actor: Principal, object_id: str) -> None:
        row = self.db.conn.execute("SELECT * FROM objects WHERE id=? AND tenant_id=? AND deleted_at IS NULL",
                                   (object_id, actor.tenant_id)).fetchone()
        if row is None:
            raise NotFound("not found")
        self.tenants.authorize(actor, "upload_review", row["tenant_id"])
        with self.db.tx() as c:
            c.execute("UPDATE objects SET deleted_at=? WHERE id=?", (self.db.clock(), object_id))
            self._ledger(actor.tenant_id, "storage_bytes_removed", -row["size_bytes"], object_id)

    # ------------------------------------------------------------------ concurrency
    def start_workflow(self, actor: Principal, project_id: str) -> str:
        self.tenants.project(actor, project_id, "start_workflow")
        with self.db.tx() as c:
            u = self.usage(actor.tenant_id)
            if u["active_workflows"] >= u["workflow_limit"]:
                raise Refusal(f"{u['plan']} allows {u['workflow_limit']} active workflow(s); wait for one to finish",
                              u, ["wait", "stop a running workflow"])
            wid = f"wf_{secrets.token_hex(8)}"
            c.execute("INSERT INTO workflows(id, tenant_id, project_id, started_at) VALUES(?,?,?,?)",
                      (wid, actor.tenant_id, project_id, self.db.clock()))
            self._ledger(actor.tenant_id, "workflow_started", 1, wid)
        return wid

    def finish_workflow(self, actor: Principal, workflow_id: str) -> None:
        with self.db.tx() as c:
            n = c.execute("UPDATE workflows SET finished_at=? WHERE id=? AND tenant_id=? AND finished_at IS NULL",
                          (self.db.clock(), workflow_id, actor.tenant_id)).rowcount
            if n != 1:
                raise NotFound("not found")
