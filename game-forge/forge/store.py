"""Durable SQLite store (WAL) with an append-only event log.

Design rules:
* Every state change is written in one ``BEGIN IMMEDIATE`` transaction together
  with its event, so the log and the state can never disagree.
* The ``events`` table is append-only (UPDATE/DELETE are rejected by triggers).
* Each thread gets its own connection; SQLite's write lock serialises writers,
  which is what makes budget reservations atomic across workers.
"""

from __future__ import annotations

import contextlib
import json
import sqlite3
import threading
from pathlib import Path
from typing import Any, Iterator, Optional

from .models import (
    ApprovalRecord,
    CandidateAttempt,
    Evidence,
    Milestone,
    Project,
    ReviewDecision,
    RootTask,
    TaskState,
)
from .statemachine import can_transition, check_transition, is_terminal
from .util import Clock, SystemClock, new_id

SCHEMA = """
CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS milestones (
    id TEXT PRIMARY KEY, project_id TEXT NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS roots (
    id TEXT PRIMARY KEY, project_id TEXT NOT NULL, milestone_id TEXT NOT NULL,
    ticket TEXT, state TEXT NOT NULL, json TEXT NOT NULL, updated_at REAL NOT NULL);
CREATE INDEX IF NOT EXISTS roots_state ON roots(state);
CREATE TABLE IF NOT EXISTS attempts (
    id TEXT PRIMARY KEY, root_id TEXT NOT NULL, number INTEGER NOT NULL, json TEXT NOT NULL,
    UNIQUE(root_id, number));
CREATE TABLE IF NOT EXISTS evidence (
    id TEXT PRIMARY KEY, root_id TEXT NOT NULL, attempt_id TEXT, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS approvals (
    id TEXT PRIMARY KEY, root_id TEXT NOT NULL, candidate_hash TEXT NOT NULL, json TEXT NOT NULL,
    UNIQUE(root_id, candidate_hash));
CREATE TABLE IF NOT EXISTS review_decisions (
    id TEXT PRIMARY KEY, root_id TEXT NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS stale_marks (
    id INTEGER PRIMARY KEY AUTOINCREMENT, root_id TEXT NOT NULL, dep_root_id TEXT NOT NULL,
    pinned_hash TEXT, current_hash TEXT, at REAL NOT NULL, cleared INTEGER NOT NULL DEFAULT 0);

CREATE TABLE IF NOT EXISTS events (
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    ts REAL NOT NULL,
    type TEXT NOT NULL,
    project_id TEXT, root_id TEXT, attempt_id TEXT,
    correlation_id TEXT,
    payload TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS events_root ON events(root_id);
CREATE TRIGGER IF NOT EXISTS events_no_update BEFORE UPDATE ON events
    BEGIN SELECT RAISE(ABORT, 'events are append-only'); END;
CREATE TRIGGER IF NOT EXISTS events_no_delete BEFORE DELETE ON events
    BEGIN SELECT RAISE(ABORT, 'events are append-only'); END;

CREATE TABLE IF NOT EXISTS provider_jobs (
    id TEXT PRIMARY KEY, idempotency_key TEXT NOT NULL UNIQUE,
    root_id TEXT NOT NULL, attempt_id TEXT NOT NULL, provider TEXT NOT NULL,
    operation TEXT NOT NULL, provider_job_id TEXT, status TEXT NOT NULL,
    reservation_id TEXT, result_json TEXT, usage_json TEXT,
    created_at REAL NOT NULL, updated_at REAL NOT NULL);

CREATE TABLE IF NOT EXISTS leases (
    resource_key TEXT PRIMARY KEY, holder TEXT NOT NULL, root_id TEXT, attempt_id TEXT,
    acquired_at REAL NOT NULL, expires_at REAL NOT NULL, heartbeat_at REAL NOT NULL);

CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);

CREATE TABLE IF NOT EXISTS budget_caps (
    scope TEXT PRIMARY KEY, cap_micros INTEGER NOT NULL, set_by TEXT, set_at REAL);
CREATE TABLE IF NOT EXISTS reservations (
    id TEXT PRIMARY KEY, project_id TEXT NOT NULL, milestone_id TEXT NOT NULL,
    root_id TEXT NOT NULL, attempt_id TEXT, provider TEXT, purpose TEXT,
    amount_micros INTEGER NOT NULL, settled_micros INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL, created_at REAL NOT NULL, updated_at REAL NOT NULL);
CREATE INDEX IF NOT EXISTS reservations_root ON reservations(root_id);
CREATE TABLE IF NOT EXISTS usage_records (
    id TEXT PRIMARY KEY, reservation_id TEXT, project_id TEXT, milestone_id TEXT,
    root_id TEXT, provider TEXT, model TEXT, cost_micros INTEGER NOT NULL,
    usage_json TEXT, at REAL NOT NULL);

CREATE TABLE IF NOT EXISTS cash_expenses (
    id TEXT PRIMARY KEY, date TEXT NOT NULL, vendor TEXT NOT NULL, invoice_id TEXT,
    currency TEXT NOT NULL, cash_paid_minor INTEGER NOT NULL, taxes_fees_minor INTEGER NOT NULL,
    promo_credit_minor INTEGER NOT NULL, normal_rate_minor INTEGER, milestone_id TEXT,
    category TEXT, allocation_rule TEXT NOT NULL, note TEXT, recorded_at REAL NOT NULL,
    corrects_id TEXT);
CREATE TABLE IF NOT EXISTS expense_allocations (
    expense_id TEXT NOT NULL, project_key TEXT NOT NULL, share_ppm INTEGER NOT NULL,
    amount_minor INTEGER NOT NULL, PRIMARY KEY(expense_id, project_key));
CREATE TABLE IF NOT EXISTS founder_hours (
    id TEXT PRIMARY KEY, date TEXT NOT NULL, hours REAL NOT NULL, project_key TEXT NOT NULL,
    milestone_id TEXT, activity TEXT, kind TEXT NOT NULL, recorded_at REAL NOT NULL);
"""


class StoreError(Exception):
    pass


class Store:
    def __init__(self, path: str | Path, clock: Clock | None = None):
        self.path = str(path)
        self.clock = clock or SystemClock()
        self._local = threading.local()
        Path(self.path).parent.mkdir(parents=True, exist_ok=True)
        conn = self.conn
        conn.execute("PRAGMA journal_mode=WAL")
        conn.executescript(SCHEMA)

    # ------------------------------------------------------------------ plumbing
    @property
    def conn(self) -> sqlite3.Connection:
        c = getattr(self._local, "conn", None)
        if c is None:
            c = sqlite3.connect(self.path, timeout=30.0, isolation_level=None, check_same_thread=False)
            c.row_factory = sqlite3.Row
            c.execute("PRAGMA foreign_keys=ON")
            c.execute("PRAGMA busy_timeout=30000")
            c.execute("PRAGMA synchronous=NORMAL")
            self._local.conn = c
        return c

    @contextlib.contextmanager
    def tx(self) -> Iterator[sqlite3.Connection]:
        """Write transaction (BEGIN IMMEDIATE). Nested use joins the outer one."""
        c = self.conn
        depth = getattr(self._local, "depth", 0)
        if depth:
            self._local.depth = depth + 1
            try:
                yield c
            finally:
                self._local.depth -= 1
            return
        c.execute("BEGIN IMMEDIATE")
        self._local.depth = 1
        try:
            yield c
            c.execute("COMMIT")
        except BaseException:
            c.execute("ROLLBACK")
            raise
        finally:
            self._local.depth = 0

    def close(self) -> None:
        c = getattr(self._local, "conn", None)
        if c is not None:
            c.close()
            self._local.conn = None

    def now(self) -> float:
        return self.clock.now()

    # ------------------------------------------------------------------ events
    def append_event(
        self,
        type_: str,
        *,
        project_id: str | None = None,
        root_id: str | None = None,
        attempt_id: str | None = None,
        correlation_id: str | None = None,
        **payload: Any,
    ) -> int:
        from .credentials import redact

        with self.tx() as c:
            cur = c.execute(
                "INSERT INTO events(ts,type,project_id,root_id,attempt_id,correlation_id,payload)"
                " VALUES (?,?,?,?,?,?,?)",
                (
                    self.now(),
                    type_,
                    project_id,
                    root_id,
                    attempt_id,
                    correlation_id or root_id,
                    redact(json.dumps(payload, default=str, sort_keys=True)),
                ),
            )
            return int(cur.lastrowid)

    def events(self, root_id: str | None = None, type_: str | None = None, limit: int = 1000) -> list[dict]:
        q = "SELECT * FROM events WHERE 1=1"
        args: list[Any] = []
        if root_id:
            q += " AND root_id=?"
            args.append(root_id)
        if type_:
            q += " AND type=?"
            args.append(type_)
        q += " ORDER BY seq ASC LIMIT ?"
        args.append(limit)
        out = []
        for r in self.conn.execute(q, args):
            d = dict(r)
            d["payload"] = json.loads(d["payload"])
            out.append(d)
        return out

    # ------------------------------------------------------------------ settings
    def get_setting(self, key: str, default: str | None = None) -> str | None:
        r = self.conn.execute("SELECT value FROM settings WHERE key=?", (key,)).fetchone()
        return r["value"] if r else default

    def set_setting(self, key: str, value: str) -> None:
        with self.tx() as c:
            c.execute(
                "INSERT INTO settings(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (key, value),
            )

    # ------------------------------------------------------------------ projects
    def put_project(self, p: Project) -> None:
        with self.tx() as c:
            c.execute(
                "INSERT INTO projects(id,json) VALUES(?,?) ON CONFLICT(id) DO UPDATE SET json=excluded.json",
                (p.id, p.model_dump_json()),
            )
            self.append_event("project_saved", project_id=p.id, name=p.name)

    def get_project(self, project_id: str) -> Project:
        r = self.conn.execute("SELECT json FROM projects WHERE id=?", (project_id,)).fetchone()
        if not r:
            raise StoreError(f"unknown project {project_id}")
        return Project.model_validate_json(r["json"])

    def list_projects(self) -> list[Project]:
        return [Project.model_validate_json(r["json"]) for r in self.conn.execute("SELECT json FROM projects ORDER BY id")]

    def put_milestone(self, m: Milestone) -> None:
        with self.tx() as c:
            c.execute(
                "INSERT INTO milestones(id,project_id,json) VALUES(?,?,?) "
                "ON CONFLICT(id) DO UPDATE SET json=excluded.json",
                (m.id, m.project_id, m.model_dump_json()),
            )

    def get_milestone(self, milestone_id: str) -> Milestone:
        r = self.conn.execute("SELECT json FROM milestones WHERE id=?", (milestone_id,)).fetchone()
        if not r:
            raise StoreError(f"unknown milestone {milestone_id}")
        return Milestone.model_validate_json(r["json"])

    def list_milestones(self, project_id: str) -> list[Milestone]:
        return [
            Milestone.model_validate_json(r["json"])
            for r in self.conn.execute("SELECT json FROM milestones WHERE project_id=? ORDER BY id", (project_id,))
        ]

    # ------------------------------------------------------------------ roots
    def create_root(self, t: RootTask) -> RootTask:
        now = self.now()
        t = t.model_copy(update={"created_at": t.created_at or now, "updated_at": now})
        with self.tx() as c:
            if c.execute("SELECT 1 FROM roots WHERE id=?", (t.id,)).fetchone():
                raise StoreError(f"root {t.id} already exists (root IDs are immutable)")
            c.execute(
                "INSERT INTO roots(id,project_id,milestone_id,ticket,state,json,updated_at) VALUES(?,?,?,?,?,?,?)",
                (t.id, t.project_id, t.milestone_id, t.ticket, t.state.value, t.model_dump_json(), now),
            )
            self.append_event(
                "root_created", project_id=t.project_id, root_id=t.id, state=t.state.value,
                ticket=t.ticket, spec_version=t.spec_version, previous_root_id=t.previous_root_id,
            )
        return t

    def get_root(self, root_id: str) -> RootTask:
        r = self.conn.execute("SELECT json FROM roots WHERE id=?", (root_id,)).fetchone()
        if not r:
            raise StoreError(f"unknown root {root_id}")
        return RootTask.model_validate_json(r["json"])

    def find_root_by_ticket(self, project_id: str, ticket: str) -> Optional[RootTask]:
        rows = self.conn.execute(
            "SELECT json FROM roots WHERE project_id=? AND ticket=? ORDER BY updated_at DESC", (project_id, ticket)
        ).fetchall()
        return RootTask.model_validate_json(rows[0]["json"]) if rows else None

    def list_roots(self, project_id: str | None = None, states: list[TaskState] | None = None) -> list[RootTask]:
        q, args = "SELECT json FROM roots WHERE 1=1", []
        if project_id:
            q += " AND project_id=?"
            args.append(project_id)
        if states:
            q += f" AND state IN ({','.join('?' * len(states))})"
            args += [s.value for s in states]
        q += " ORDER BY rowid"
        return [RootTask.model_validate_json(r["json"]) for r in self.conn.execute(q, args)]

    def update_root(self, t: RootTask, **fields: Any) -> RootTask:
        """Update non-state fields. State changes must go through ``transition``."""
        if "state" in fields:
            raise StoreError("use transition() to change state")
        cur = self.get_root(t.id)
        if is_terminal(cur.state):
            allowed = {"evidence_refs", "accepted_artifact_hash"} if cur.state == TaskState.ACCEPTED else set()
            bad = set(fields) - allowed - {"updated_at"}
            if bad:
                raise StoreError(f"root {t.id} is terminal ({cur.state.value}); cannot modify {sorted(bad)}")
        new = cur.model_copy(update={**fields, "updated_at": self.now()})
        with self.tx() as c:
            c.execute("UPDATE roots SET json=?, updated_at=? WHERE id=?", (new.model_dump_json(), new.updated_at, t.id))
        return new

    def transition(
        self, root_id: str, dst: TaskState, reason: str = "", *, correlation_id: str | None = None, **payload: Any
    ) -> RootTask:
        with self.tx() as c:
            cur = self.get_root(root_id)
            check_transition(cur.state, dst, root_id)
            new = cur.model_copy(update={"state": dst, "state_reason": reason, "updated_at": self.now()})
            res = c.execute(
                "UPDATE roots SET state=?, json=?, updated_at=? WHERE id=? AND state=?",
                (dst.value, new.model_dump_json(), new.updated_at, root_id, cur.state.value),
            )
            if res.rowcount != 1:  # pragma: no cover - guarded by BEGIN IMMEDIATE
                raise StoreError(f"concurrent state change on {root_id}")
            self.append_event(
                "state_transition", project_id=cur.project_id, root_id=root_id,
                correlation_id=correlation_id, src=cur.state.value, dst=dst.value, reason=reason, **payload,
            )
        return new

    # ------------------------------------------------------------------ attempts
    def create_attempt(self, a: CandidateAttempt) -> CandidateAttempt:
        with self.tx() as c:
            c.execute(
                "INSERT INTO attempts(id,root_id,number,json) VALUES(?,?,?,?)",
                (a.id, a.root_id, a.number, a.model_dump_json()),
            )
            self.append_event("attempt_created", root_id=a.root_id, attempt_id=a.id, number=a.number)
        return a

    def save_attempt(self, a: CandidateAttempt) -> CandidateAttempt:
        with self.tx() as c:
            c.execute("UPDATE attempts SET json=? WHERE id=?", (a.model_dump_json(), a.id))
        return a

    def get_attempt(self, attempt_id: str) -> CandidateAttempt:
        r = self.conn.execute("SELECT json FROM attempts WHERE id=?", (attempt_id,)).fetchone()
        if not r:
            raise StoreError(f"unknown attempt {attempt_id}")
        return CandidateAttempt.model_validate_json(r["json"])

    def list_attempts(self, root_id: str) -> list[CandidateAttempt]:
        return [
            CandidateAttempt.model_validate_json(r["json"])
            for r in self.conn.execute("SELECT json FROM attempts WHERE root_id=? ORDER BY number", (root_id,))
        ]

    def latest_attempt(self, root_id: str) -> Optional[CandidateAttempt]:
        atts = self.list_attempts(root_id)
        return atts[-1] if atts else None

    # ------------------------------------------------------------------ evidence
    def add_evidence(self, e: Evidence) -> Evidence:
        e = e.model_copy(update={"created_at": e.created_at or self.now()})
        with self.tx() as c:
            c.execute(
                "INSERT INTO evidence(id,root_id,attempt_id,json) VALUES(?,?,?,?)",
                (e.id, e.root_id, e.attempt_id, e.model_dump_json()),
            )
            self.append_event(
                "evidence_recorded", root_id=e.root_id, attempt_id=e.attempt_id,
                evidence_id=e.id, evidence_class=e.evidence_class.value, status=e.status.value, name=e.name,
            )
            root = self.get_root(e.root_id)
            refs = list(root.evidence_refs) + [e.id]
            new = root.model_copy(update={"evidence_refs": refs[-200:]})
            c.execute("UPDATE roots SET json=? WHERE id=?", (new.model_dump_json(), root.id))
        return e

    def list_evidence(self, root_id: str, attempt_id: str | None = None) -> list[Evidence]:
        q, args = "SELECT json FROM evidence WHERE root_id=?", [root_id]
        if attempt_id:
            q += " AND attempt_id=?"
            args.append(attempt_id)
        return [Evidence.model_validate_json(r["json"]) for r in self.conn.execute(q + " ORDER BY rowid", args)]

    # ------------------------------------------------------------------ approvals
    def get_or_create_approval(self, root_id: str, attempt_id: str, candidate_hash: str) -> ApprovalRecord:
        r = self.conn.execute(
            "SELECT json FROM approvals WHERE root_id=? AND candidate_hash=?", (root_id, candidate_hash)
        ).fetchone()
        if r:
            return ApprovalRecord.model_validate_json(r["json"])
        rec = ApprovalRecord(
            id=new_id("apr"), root_id=root_id, attempt_id=attempt_id, candidate_hash=candidate_hash,
            created_at=self.now(),
        )
        with self.tx() as c:
            c.execute(
                "INSERT INTO approvals(id,root_id,candidate_hash,json) VALUES(?,?,?,?)",
                (rec.id, root_id, candidate_hash, rec.model_dump_json()),
            )
        return rec

    def save_approval(self, rec: ApprovalRecord) -> None:
        with self.tx() as c:
            c.execute("UPDATE approvals SET json=? WHERE id=?", (rec.model_dump_json(), rec.id))
            self.append_event("approval_updated", root_id=rec.root_id, attempt_id=rec.attempt_id,
                              candidate_hash=rec.candidate_hash)

    def find_approval(self, root_id: str, candidate_hash: str) -> Optional[ApprovalRecord]:
        r = self.conn.execute(
            "SELECT json FROM approvals WHERE root_id=? AND candidate_hash=?", (root_id, candidate_hash)
        ).fetchone()
        return ApprovalRecord.model_validate_json(r["json"]) if r else None

    def list_approvals(self, root_id: str) -> list[ApprovalRecord]:
        return [
            ApprovalRecord.model_validate_json(r["json"])
            for r in self.conn.execute("SELECT json FROM approvals WHERE root_id=? ORDER BY rowid", (root_id,))
        ]

    def add_review_decision(self, d: ReviewDecision) -> None:
        with self.tx() as c:
            c.execute("INSERT INTO review_decisions(id,root_id,json) VALUES(?,?,?)", (d.id, d.root_id, d.model_dump_json()))
            self.append_event(
                "review_decision", root_id=d.root_id, attempt_id=d.attempt_id, action=d.action.value,
                reviewer=d.reviewer, candidate_hash=d.candidate_hash, correction=d.correction,
                failure_category=d.failure_category.value if d.failure_category else None,
            )

    def list_review_decisions(self, root_id: str) -> list[ReviewDecision]:
        return [
            ReviewDecision.model_validate_json(r["json"])
            for r in self.conn.execute("SELECT json FROM review_decisions WHERE root_id=? ORDER BY rowid", (root_id,))
        ]

    # ------------------------------------------------------------------ staleness
    def mark_stale(self, root_id: str, dep_root_id: str, pinned: str | None, current: str | None) -> None:
        with self.tx() as c:
            c.execute(
                "INSERT INTO stale_marks(root_id,dep_root_id,pinned_hash,current_hash,at) VALUES(?,?,?,?,?)",
                (root_id, dep_root_id, pinned, current, self.now()),
            )
            self.append_event("dependency_stale", root_id=root_id, dep_root_id=dep_root_id,
                              pinned_hash=pinned, current_hash=current)

    def stale_marks(self, root_id: str, include_cleared: bool = False) -> list[dict]:
        q = "SELECT * FROM stale_marks WHERE root_id=?" + ("" if include_cleared else " AND cleared=0")
        return [dict(r) for r in self.conn.execute(q, (root_id,))]

    def clear_stale(self, root_id: str) -> None:
        with self.tx() as c:
            c.execute("UPDATE stale_marks SET cleared=1 WHERE root_id=?", (root_id,))

    # ------------------------------------------------------------------ provider jobs
    def record_provider_job(
        self, *, idempotency_key: str, root_id: str, attempt_id: str, provider: str, operation: str,
        reservation_id: str | None,
    ) -> dict:
        now = self.now()
        with self.tx() as c:
            existing = c.execute("SELECT * FROM provider_jobs WHERE idempotency_key=?", (idempotency_key,)).fetchone()
            if existing:
                return dict(existing)
            jid = new_id("pjob")
            c.execute(
                "INSERT INTO provider_jobs(id,idempotency_key,root_id,attempt_id,provider,operation,status,"
                "reservation_id,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?)",
                (jid, idempotency_key, root_id, attempt_id, provider, operation, "SUBMITTING", reservation_id, now, now),
            )
            self.append_event("provider_job_submitting", root_id=root_id, attempt_id=attempt_id,
                              idempotency_key=idempotency_key, provider=provider, operation=operation)
            return dict(c.execute("SELECT * FROM provider_jobs WHERE id=?", (jid,)).fetchone())

    def update_provider_job(self, idempotency_key: str, **fields: Any) -> None:
        if not fields:
            return
        fields["updated_at"] = self.now()
        cols = ",".join(f"{k}=?" for k in fields)
        with self.tx() as c:
            c.execute(f"UPDATE provider_jobs SET {cols} WHERE idempotency_key=?", (*fields.values(), idempotency_key))
            row = c.execute("SELECT root_id, attempt_id FROM provider_jobs WHERE idempotency_key=?", (idempotency_key,)).fetchone()
            self.append_event("provider_job_updated", root_id=row["root_id"], attempt_id=row["attempt_id"],
                              idempotency_key=idempotency_key,
                              **{k: v for k, v in fields.items() if k in ("status", "provider_job_id")})

    def get_provider_job(self, idempotency_key: str) -> Optional[dict]:
        r = self.conn.execute("SELECT * FROM provider_jobs WHERE idempotency_key=?", (idempotency_key,)).fetchone()
        return dict(r) if r else None

    def provider_jobs_for_attempt(self, attempt_id: str) -> list[dict]:
        return [dict(r) for r in self.conn.execute(
            "SELECT * FROM provider_jobs WHERE attempt_id=? ORDER BY created_at", (attempt_id,))]

    # ------------------------------------------------------------------ revisions
    def revise_root(self, root_id: str, *, new_id_: str | None = None, cost_micros: int = 0, **changes: Any) -> RootTask:
        """Spec change: create an explicitly linked new root that shows prior cost.

        The old root is cancelled if it is not terminal yet. Its attempts and
        spending stay attached to it; the new root does not get a "free retry"
        label - ``previous_cost_micros`` carries the cumulative lineage cost.
        """
        old = self.get_root(root_id)
        if not is_terminal(old.state):
            if can_transition(old.state, TaskState.CANCELLED):
                self.transition(root_id, TaskState.CANCELLED, "superseded by specification revision")
        try:
            new_spec = str(int(old.spec_version) + 1)
        except ValueError:
            new_spec = old.spec_version + ".1"
        data = old.model_dump()
        data.update(
            id=new_id_ or new_id("root"), state=TaskState.DRAFT, state_reason="specification revision",
            spec_version=changes.pop("spec_version", new_spec), previous_root_id=old.id,
            previous_cost_micros=old.previous_cost_micros + cost_micros, evidence_refs=[],
            accepted_artifact_hash=None, held=False, created_at=0.0, updated_at=0.0,
        )
        data.update(changes)
        new = self.create_root(RootTask.model_validate(data))
        self.append_event("root_revised", project_id=old.project_id, root_id=new.id,
                          previous_root_id=old.id, previous_cost_micros=new.previous_cost_micros)
        return new

    def lineage_head(self, root_id: str) -> RootTask:
        """Latest root in the revision chain that starts at (or passes through) ``root_id``."""
        cur = self.get_root(root_id)
        while True:
            r = self.conn.execute(
                "SELECT json FROM roots WHERE json_extract(json,'$.previous_root_id')=? ORDER BY rowid DESC LIMIT 1",
                (cur.id,),
            ).fetchone()
            if not r:
                return cur
            cur = RootTask.model_validate_json(r["json"])
