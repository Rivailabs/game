"""Private hosted review with short-lived signed URLs.

Plan: "Hosted review is a separate upload destination, not an automatic consequence of selecting an
API model" and "Review downloads use short-lived authenticated access."

* Upload is allowed only for a project whose policy enables hosted review (set explicitly by an
  owner/admin), within the plan's limits (see :mod:`forge.hosted.entitlements`).
* Objects live under a per-tenant directory of a private object store and are never listed
  publicly.
* A review link is ``/review/<object>?u=<user>&exp=<unix>&sig=<hmac>``. The HMAC covers object,
  tenant, user and expiry with a server secret; links live at most 15 minutes; on use the server
  re-checks the signature, the expiry, that the object belongs to that tenant and is not deleted or
  expired, and that the user is *still* a member allowed to view reviews.
"""

from __future__ import annotations

import hashlib
import hmac
import shutil
import tempfile
from pathlib import Path
from urllib.parse import parse_qs, urlencode, urlparse

from .entitlements import EntitlementService
from .tenancy import Forbidden, NotFound, Principal, TenantService

MAX_LINK_SECONDS = 900


class LinkInvalid(Exception):
    pass


class HostedReview:
    def __init__(self, tenants: TenantService, entitlements: EntitlementService, object_root: str | Path,
                 url_secret: bytes):
        if len(url_secret) < 32:
            raise ValueError("the review URL secret must be at least 32 bytes")
        self.tenants, self.ent = tenants, entitlements
        self.db = tenants.db
        self.root = Path(object_root)
        self.secret = url_secret

    def _path(self, tenant_id: str, object_id: str) -> Path:
        return self.root / tenant_id / object_id

    def upload(self, actor: Principal, project_id: str, src: str | Path, *, name: str | None = None) -> dict:
        project = self.tenants.project(actor, project_id, "upload_review")
        if not project["hosted_review_allowed"]:
            raise Forbidden("this project's policy does not allow hosted review uploads")
        src = Path(src)
        size = src.stat().st_size
        h = hashlib.sha256()
        with open(src, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 16), b""):
                h.update(chunk)
        oid, expires = self.ent.reserve_upload(actor, project_id, name or src.name, size, h.hexdigest())
        dest = self._path(actor.tenant_id, oid)
        dest.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(dir=dest.parent, delete=False) as tmp:
            with open(src, "rb") as fh:
                shutil.copyfileobj(fh, tmp)
        Path(tmp.name).replace(dest)
        dest.chmod(0o600)
        return {"object_id": oid, "sha256": h.hexdigest(), "size_bytes": size, "expires_at": expires}

    def _sig(self, object_id: str, tenant_id: str, user_id: str, exp: int) -> str:
        msg = f"{object_id}|{tenant_id}|{user_id}|{exp}".encode()
        return hmac.new(self.secret, msg, hashlib.sha256).hexdigest()

    def signed_url(self, actor: Principal, object_id: str, *, ttl_s: int = 300, base: str = "/review") -> str:
        if not 0 < ttl_s <= MAX_LINK_SECONDS:
            raise ValueError(f"review links live between 1 and {MAX_LINK_SECONDS} seconds")
        row = self.db.conn.execute("SELECT tenant_id FROM objects WHERE id=? AND tenant_id=? AND deleted_at IS NULL",
                                   (object_id, actor.tenant_id)).fetchone()
        if row is None:
            raise NotFound("not found")
        self.tenants.authorize(actor, "view_review", row["tenant_id"])
        exp = int(self.db.clock()) + ttl_s
        q = urlencode({"t": actor.tenant_id, "u": actor.user_id, "exp": exp,
                       "sig": self._sig(object_id, actor.tenant_id, actor.user_id, exp)})
        return f"{base}/{object_id}?{q}"

    def open_url(self, url: str) -> Path:
        """Resolve a review link to the object's bytes, or raise :class:`LinkInvalid`."""
        u = urlparse(url)
        object_id = u.path.rstrip("/").rsplit("/", 1)[-1]
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        try:
            tenant, user, exp, sig = q["t"], q["u"], int(q["exp"]), q["sig"]
        except (KeyError, ValueError):
            raise LinkInvalid("malformed review link") from None
        if not hmac.compare_digest(sig, self._sig(object_id, tenant, user, exp)):
            raise LinkInvalid("bad signature")
        now = self.db.clock()
        if now > exp:
            raise LinkInvalid("link expired")
        row = self.db.conn.execute("SELECT * FROM objects WHERE id=? AND tenant_id=? AND deleted_at IS NULL "
                                   "AND (expires_at IS NULL OR expires_at>?)", (object_id, tenant, now)).fetchone()
        if row is None:
            raise LinkInvalid("object not available")
        try:
            self.tenants.authorize(Principal(user, tenant, "viewer"), "view_review", tenant)
        except (NotFound, Forbidden) as e:
            raise LinkInvalid("user no longer has review access") from e
        return self._path(tenant, object_id)

    def expire(self) -> int:
        """Delete objects past their retention; returns the number removed."""
        now = self.db.clock()
        rows = self.db.conn.execute("SELECT id, tenant_id, size_bytes FROM objects WHERE deleted_at IS NULL AND "
                                    "expires_at<=?", (now,)).fetchall()
        with self.db.tx() as c:
            for r in rows:
                c.execute("UPDATE objects SET deleted_at=? WHERE id=?", (now, r["id"]))
                c.execute("INSERT INTO usage_ledger(tenant_id, period, kind, amount, ref, at) VALUES(?,?,?,?,?,?)",
                          (r["tenant_id"], self.ent.period(r["tenant_id"]), "storage_bytes_expired", -r["size_bytes"],
                           r["id"], now))
                self._path(r["tenant_id"], r["id"]).unlink(missing_ok=True)
        return len(rows)
