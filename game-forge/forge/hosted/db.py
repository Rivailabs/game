"""Hosted multi-tenant store (SQLite here; the plan allows PostgreSQL for a real deployment
without changing semantics).

Every tenant-owned row carries ``tenant_id`` and every read in the services filters on it, so
changing an identifier never reaches another tenant's data. Ledgers (usage, credits, audit,
billing events) are append-only, enforced by triggers, and corrected only by new entries.
"""

from __future__ import annotations

import sqlite3
import threading
import time
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator

SCHEMA = """
CREATE TABLE IF NOT EXISTS tenants(id TEXT PRIMARY KEY, name TEXT NOT NULL, plan TEXT NOT NULL,
    region TEXT NOT NULL DEFAULT 'ROW', status TEXT NOT NULL DEFAULT 'active', created_at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY, email TEXT NOT NULL UNIQUE, created_at REAL NOT NULL,
    staff INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS memberships(tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, role TEXT NOT NULL,
    created_at REAL NOT NULL, PRIMARY KEY(tenant_id, user_id));
CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL,
    hosted_review_allowed INTEGER NOT NULL DEFAULT 0, archived INTEGER NOT NULL DEFAULT 0, created_at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS objects(id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, project_id TEXT NOT NULL,
    kind TEXT NOT NULL, name TEXT NOT NULL, size_bytes INTEGER NOT NULL, sha256 TEXT NOT NULL,
    created_at REAL NOT NULL, expires_at REAL, deleted_at REAL);
CREATE TABLE IF NOT EXISTS workflows(id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, project_id TEXT NOT NULL,
    started_at REAL NOT NULL, finished_at REAL);
CREATE TABLE IF NOT EXISTS usage_ledger(seq INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id TEXT NOT NULL,
    period TEXT NOT NULL, kind TEXT NOT NULL, amount INTEGER NOT NULL, ref TEXT, at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS byok_keys(id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, provider TEXT NOT NULL,
    ciphertext BLOB NOT NULL, fingerprint TEXT NOT NULL, created_by TEXT NOT NULL, created_at REAL NOT NULL,
    revoked_at REAL);
CREATE TABLE IF NOT EXISTS gateway_credentials(token_hash TEXT PRIMARY KEY, tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL, provider TEXT NOT NULL, key_id TEXT NOT NULL, request_classes TEXT NOT NULL,
    issued_to TEXT NOT NULL, expires_at REAL NOT NULL, revoked_at REAL);
CREATE TABLE IF NOT EXISTS subscriptions(tenant_id TEXT PRIMARY KEY, provider TEXT NOT NULL, external_id TEXT NOT NULL,
    plan TEXT NOT NULL, status TEXT NOT NULL, period_start REAL, period_end REAL, updated_at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS billing_events(provider TEXT NOT NULL, event_id TEXT NOT NULL, type TEXT NOT NULL,
    tenant_id TEXT, external_id TEXT, amount_minor INTEGER, currency TEXT, payload TEXT NOT NULL,
    received_at REAL NOT NULL, PRIMARY KEY(provider, event_id));
CREATE TABLE IF NOT EXISTS money_ledger(seq INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id TEXT, provider TEXT NOT NULL,
    kind TEXT NOT NULL, amount_minor INTEGER NOT NULL, currency TEXT NOT NULL, tax_inclusive INTEGER,
    ref TEXT NOT NULL, at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS credit_ledger(seq INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id TEXT NOT NULL,
    unit TEXT NOT NULL, kind TEXT NOT NULL, available_delta INTEGER NOT NULL, reserved_delta INTEGER NOT NULL,
    charged_delta INTEGER NOT NULL, forge_cost_delta INTEGER NOT NULL DEFAULT 0, job_id TEXT, note TEXT,
    at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS credit_jobs(id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, unit TEXT NOT NULL,
    idempotency_key TEXT NOT NULL, schedule_id TEXT NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL,
    params TEXT NOT NULL, max_units INTEGER NOT NULL, authorised_by TEXT NOT NULL, status TEXT NOT NULL,
    provider_job_id TEXT, outcome TEXT, charged_units INTEGER NOT NULL DEFAULT 0, created_at REAL NOT NULL,
    updated_at REAL NOT NULL, UNIQUE(tenant_id, idempotency_key));
CREATE TABLE IF NOT EXISTS credit_limits(tenant_id TEXT PRIMARY KEY, period_limit INTEGER, daily_limit INTEGER,
    set_by TEXT NOT NULL, set_at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS tickets(id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL,
    category TEXT NOT NULL, severity TEXT NOT NULL, summary TEXT NOT NULL, description TEXT NOT NULL,
    expectation TEXT NOT NULL, counts_against_allowance INTEGER NOT NULL, status TEXT NOT NULL, created_at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS support_grants(id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, staff_user_id TEXT NOT NULL,
    granted_by TEXT NOT NULL, reason TEXT NOT NULL, expires_at REAL NOT NULL, created_at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS audit(seq INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id TEXT, actor TEXT NOT NULL,
    action TEXT NOT NULL, target TEXT, detail TEXT, at REAL NOT NULL);
"""

APPEND_ONLY = ("usage_ledger", "money_ledger", "credit_ledger", "audit", "billing_events")


class HostedDB:
    def __init__(self, path: str | Path, clock=time.time):
        self.path = str(path)
        Path(self.path).parent.mkdir(parents=True, exist_ok=True)
        self.clock = clock
        self._local = threading.local()
        self.conn.executescript(SCHEMA)  # executescript manages its own transaction
        with self.tx() as c:
            for t in APPEND_ONLY:
                c.execute(f"CREATE TRIGGER IF NOT EXISTS {t}_no_update BEFORE UPDATE ON {t} "
                          f"BEGIN SELECT RAISE(ABORT, '{t} is append-only'); END")
                c.execute(f"CREATE TRIGGER IF NOT EXISTS {t}_no_delete BEFORE DELETE ON {t} "
                          f"BEGIN SELECT RAISE(ABORT, '{t} is append-only'); END")

    @property
    def conn(self) -> sqlite3.Connection:
        c = getattr(self._local, "conn", None)
        if c is None:
            c = sqlite3.connect(self.path, timeout=30, isolation_level=None, check_same_thread=False)
            c.row_factory = sqlite3.Row
            c.execute("PRAGMA journal_mode=WAL")
            c.execute("PRAGMA busy_timeout=30000")
            self._local.conn = c
            self._local.depth = 0
        return c

    @contextmanager
    def tx(self) -> Iterator[sqlite3.Connection]:
        """``BEGIN IMMEDIATE`` transaction (re-entrant): limit checks and writes are atomic."""
        c = self.conn
        if self._local.depth:
            self._local.depth += 1
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

    def audit(self, actor: str, action: str, *, tenant_id: str | None = None, target: str | None = None,
              detail: str = "") -> None:
        self.conn.execute("INSERT INTO audit(tenant_id, actor, action, target, detail, at) VALUES(?,?,?,?,?,?)",
                          (tenant_id, actor, action, target, detail, self.clock()))
