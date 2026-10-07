"""Service monitoring endpoints (WSGI): ``/healthz``, ``/readyz`` and ``/metrics``.

Only aggregate operational numbers are exposed; no tenant names, IDs, emails or content. The
endpoints are meant for the operator's monitoring network, not the public Internet.
"""

from __future__ import annotations

import json
from typing import Callable, Iterable

from .db import HostedDB
from .flags import Flags


def metrics_text(db: HostedDB) -> str:
    c = db.conn
    now = db.clock()
    q = lambda sql, *a: c.execute(sql, a).fetchone()[0]  # noqa: E731
    lines = [
        ("forge_tenants_total", q("SELECT count(*) FROM tenants")),
        ("forge_active_workflows", q("SELECT count(*) FROM workflows WHERE finished_at IS NULL")),
        ("forge_review_objects_live", q("SELECT count(*) FROM objects WHERE deleted_at IS NULL AND "
                                        "(expires_at IS NULL OR expires_at>?)", now)),
        ("forge_review_bytes_live", q("SELECT coalesce(sum(size_bytes),0) FROM objects WHERE deleted_at IS NULL AND "
                                      "(expires_at IS NULL OR expires_at>?)", now)),
        ("forge_billing_events_total", q("SELECT count(*) FROM billing_events")),
        ("forge_billing_events_unassigned", q("SELECT count(*) FROM billing_events WHERE tenant_id IS NULL")),
        ("forge_credit_jobs_unknown", q("SELECT count(*) FROM credit_jobs WHERE status='UNKNOWN'")),
        ("forge_credit_jobs_held", q("SELECT count(*) FROM credit_jobs WHERE status IN ('RESERVED','SUBMITTED','UNKNOWN')")),
        ("forge_tickets_open", q("SELECT count(*) FROM tickets WHERE status='open'")),
    ]
    out = []
    for name, value in lines:
        out += [f"# TYPE {name} gauge", f"{name} {value}"]
    return "\n".join(out) + "\n"


def monitoring_app(db: HostedDB, flags: Flags) -> Callable:
    def app(environ: dict, start_response: Callable) -> Iterable[bytes]:
        path = environ.get("PATH_INFO", "/")

        def reply(status: str, body: str, ctype: str = "application/json") -> list[bytes]:
            data = body.encode()
            start_response(status, [("Content-Type", ctype), ("Content-Length", str(len(data))),
                                    ("Cache-Control", "no-store")])
            return [data]

        if path == "/healthz":
            return reply("200 OK", json.dumps({"status": "ok", "hosted_enabled": flags.hosted}))
        if path == "/readyz":
            try:
                db.conn.execute("SELECT 1").fetchone()
            except Exception as e:  # noqa: BLE001
                return reply("503 Service Unavailable", json.dumps({"status": "db unavailable", "error": type(e).__name__}))
            if not flags.hosted:
                return reply("503 Service Unavailable", json.dumps({"status": "hosted service disabled"}))
            return reply("200 OK", json.dumps({"status": "ready"}))
        if path == "/metrics":
            return reply("200 OK", metrics_text(db), "text/plain; version=0.0.4")
        return reply("404 Not Found", json.dumps({"status": "not found"}))

    return app
