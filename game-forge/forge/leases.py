"""Leases with expiry + heartbeat for workers and physical resources.

Resource keys follow the plan: one lease per actual GPU / device / build
workspace (``gpu:<id>``, ``device:<serial>``, ``workspace:<path>``), plus
``integration:<project>`` for the single integration writer and
``attempt:<id>`` for the worker currently executing an attempt.
"""

from __future__ import annotations

from typing import Optional

from .store import Store


class LeaseUnavailable(Exception):
    pass


class LeaseManager:
    def __init__(self, store: Store, default_ttl: float = 300.0):
        self.store = store
        self.default_ttl = default_ttl

    def acquire(self, resource_key: str, holder: str, *, ttl: float | None = None,
                root_id: str | None = None, attempt_id: str | None = None, force: bool = False) -> bool:
        """Acquire or renew. ``force`` takes over a lease whose holder is known to be dead."""
        ttl = ttl or self.default_ttl
        now = self.store.now()
        with self.store.tx() as c:
            r = c.execute("SELECT * FROM leases WHERE resource_key=?", (resource_key,)).fetchone()
            if r and r["holder"] != holder and r["expires_at"] > now and not force:
                return False
            if r and r["holder"] != holder:
                self.store.append_event("lease_expired_takeover", resource_key=resource_key,
                                        previous_holder=r["holder"], holder=holder, root_id=root_id)
            c.execute(
                "INSERT INTO leases(resource_key,holder,root_id,attempt_id,acquired_at,expires_at,heartbeat_at)"
                " VALUES(?,?,?,?,?,?,?) ON CONFLICT(resource_key) DO UPDATE SET holder=excluded.holder,"
                " root_id=excluded.root_id, attempt_id=excluded.attempt_id, acquired_at=excluded.acquired_at,"
                " expires_at=excluded.expires_at, heartbeat_at=excluded.heartbeat_at",
                (resource_key, holder, root_id, attempt_id, now, now + ttl, now),
            )
            self.store.append_event("lease_acquired", resource_key=resource_key, holder=holder,
                                    root_id=root_id, attempt_id=attempt_id, expires_at=now + ttl)
        return True

    def acquire_all(self, keys: list[str], holder: str, **kw) -> bool:
        """All-or-nothing acquisition (avoids holding a GPU while waiting for a device)."""
        got: list[str] = []
        for k in keys:
            if not self.acquire(k, holder, **kw):
                for g in got:
                    self.release(g, holder)
                return False
            got.append(k)
        return True

    def heartbeat(self, resource_key: str, holder: str, ttl: float | None = None) -> bool:
        ttl = ttl or self.default_ttl
        now = self.store.now()
        with self.store.tx() as c:
            r = c.execute("SELECT holder FROM leases WHERE resource_key=?", (resource_key,)).fetchone()
            if not r or r["holder"] != holder:
                return False
            c.execute("UPDATE leases SET heartbeat_at=?, expires_at=? WHERE resource_key=?", (now, now + ttl, resource_key))
        return True

    def release(self, resource_key: str, holder: str) -> None:
        with self.store.tx() as c:
            c.execute("DELETE FROM leases WHERE resource_key=? AND holder=?", (resource_key, holder))
            self.store.append_event("lease_released", resource_key=resource_key, holder=holder)

    def holder(self, resource_key: str) -> Optional[dict]:
        r = self.store.conn.execute("SELECT * FROM leases WHERE resource_key=?", (resource_key,)).fetchone()
        return dict(r) if r else None

    def is_expired(self, resource_key: str) -> bool:
        r = self.holder(resource_key)
        return r is None or r["expires_at"] <= self.store.now()

    def all(self) -> list[dict]:
        return [dict(r) for r in self.store.conn.execute("SELECT * FROM leases ORDER BY resource_key")]

    def release_for_holder(self, holder: str) -> None:
        with self.store.tx() as c:
            c.execute("DELETE FROM leases WHERE holder=?", (holder,))
