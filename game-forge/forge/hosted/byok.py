"""BYOK: customer provider keys stored encrypted, used only inside the gateway, never readable back.

Plan ("Secrets egress and signed releases"): keys stay in a trusted broker/secret manager outside
model context and generated subprocesses; the broker validates tenant/project, provider, request
class and remaining reservation before authenticating a request and returns normalized data and
usage, not secret values; managed mode uses tenant-scoped gateway credentials, per-project
authorization and revocation; arbitrary destination URLs are not exposed.

* :class:`KeyVault` encrypts keys with Fernet (AES-128-CBC + HMAC-SHA256, from ``cryptography``)
  under a server master key. There is no method that returns a stored key; the API shows only a
  fingerprint (``sk-...wxyz``). Without ``cryptography`` storing a key is refused (fail closed).
* :class:`Gateway` issues short-lived, tenant + project + provider scoped credentials (random
  tokens; only their sha256 is stored). A call names a *registered* provider adapter; the payload
  may not carry a URL; the decrypted key is passed only to that adapter and redacted from any error.
* Revoking a key revokes every gateway credential derived from it.
"""

from __future__ import annotations

import hashlib
import secrets
from dataclasses import dataclass
from typing import Any, Callable

from ..credentials import redact, register_secret
from .db import HostedDB
from .tenancy import NotFound, Principal, TenantService

ProviderCall = Callable[[str, dict], dict]  # (api_key, payload) -> normalized {"data":..., "usage":...}


class VaultUnavailable(Exception):
    pass


class GatewayDenied(Exception):
    pass


def _fernet(master_key: bytes):
    try:
        from cryptography.fernet import Fernet
    except ImportError as e:  # pragma: no cover - environment dependent
        raise VaultUnavailable("BYOK storage needs the `cryptography` package; keys are never stored unencrypted") from e
    return Fernet(master_key)


def new_master_key() -> bytes:
    from cryptography.fernet import Fernet

    return Fernet.generate_key()


def fingerprint(secret: str) -> str:
    return f"{secret[:3]}...{secret[-4:]}" if len(secret) >= 12 else "..." + hashlib.sha256(secret.encode()).hexdigest()[:6]


class KeyVault:
    def __init__(self, db: HostedDB, tenants: TenantService, master_key: bytes):
        self.db, self.tenants = db, tenants
        self._f = _fernet(master_key)

    def store_key(self, actor: Principal, provider: str, secret: str) -> dict:
        self.tenants.authorize(actor, "manage_keys", actor.tenant_id)
        secret = secret.strip()
        if len(secret) < 8:
            raise ValueError("that does not look like an API key")
        kid = f"key_{secrets.token_hex(8)}"
        token = self._f.encrypt(secret.encode())
        with self.db.tx() as c:
            c.execute("INSERT INTO byok_keys(id, tenant_id, provider, ciphertext, fingerprint, created_by, created_at) "
                      "VALUES(?,?,?,?,?,?,?)", (kid, actor.tenant_id, provider, token, fingerprint(secret),
                                                actor.user_id, self.db.clock()))
            self.db.audit(actor.user_id, "byok_key_stored", tenant_id=actor.tenant_id, target=kid, detail=provider)
        return {"key_id": kid, "provider": provider, "fingerprint": fingerprint(secret)}

    def list_keys(self, actor: Principal) -> list[dict]:
        self.tenants.authorize(actor, "manage_keys", actor.tenant_id)
        return [dict(r) for r in self.db.conn.execute(
            "SELECT id, provider, fingerprint, created_at, revoked_at FROM byok_keys WHERE tenant_id=? "
            "ORDER BY created_at", (actor.tenant_id,))]

    def revoke(self, actor: Principal, key_id: str) -> None:
        self.tenants.authorize(actor, "manage_keys", actor.tenant_id)
        with self.db.tx() as c:
            n = c.execute("UPDATE byok_keys SET revoked_at=? WHERE id=? AND tenant_id=? AND revoked_at IS NULL",
                          (self.db.clock(), key_id, actor.tenant_id)).rowcount
            if n != 1:
                raise NotFound("not found")
            c.execute("UPDATE gateway_credentials SET revoked_at=? WHERE key_id=? AND revoked_at IS NULL",
                      (self.db.clock(), key_id))
            self.db.audit(actor.user_id, "byok_key_revoked", tenant_id=actor.tenant_id, target=key_id)

    def _decrypt_for_gateway(self, tenant_id: str, key_id: str) -> str:
        """Private to the gateway: the plaintext never leaves :meth:`Gateway.call`."""
        row = self.db.conn.execute("SELECT ciphertext FROM byok_keys WHERE id=? AND tenant_id=? AND revoked_at IS NULL",
                                   (key_id, tenant_id)).fetchone()
        if row is None:
            raise GatewayDenied("key revoked or not found")
        return self._f.decrypt(row["ciphertext"]).decode()


@dataclass
class GatewayCredential:
    token: str
    expires_at: float
    provider: str
    project_id: str


class Gateway:
    def __init__(self, db: HostedDB, tenants: TenantService, vault: KeyVault, adapters: dict[str, ProviderCall],
                 *, max_ttl_s: int = 3600):
        self.db, self.tenants, self.vault = db, tenants, vault
        self.adapters = adapters
        self.max_ttl_s = max_ttl_s

    def issue(self, actor: Principal, project_id: str, provider: str, *, request_classes: list[str],
              ttl_s: int = 900) -> GatewayCredential:
        self.tenants.project(actor, project_id, "start_workflow")
        if provider not in self.adapters:
            raise GatewayDenied(f"no certified adapter for {provider}")
        if not 0 < ttl_s <= self.max_ttl_s:
            raise ValueError("gateway credentials are short-lived")
        key = self.db.conn.execute("SELECT id FROM byok_keys WHERE tenant_id=? AND provider=? AND revoked_at IS NULL "
                                   "ORDER BY created_at DESC LIMIT 1", (actor.tenant_id, provider)).fetchone()
        if key is None:
            raise GatewayDenied(f"no active {provider} key for this tenant")
        token = "fgw_" + secrets.token_urlsafe(32)
        exp = self.db.clock() + ttl_s
        with self.db.tx() as c:
            c.execute("INSERT INTO gateway_credentials(token_hash, tenant_id, project_id, provider, key_id, "
                      "request_classes, issued_to, expires_at) VALUES(?,?,?,?,?,?,?,?)",
                      (hashlib.sha256(token.encode()).hexdigest(), actor.tenant_id, project_id, provider, key["id"],
                       ",".join(sorted(request_classes)), actor.user_id, exp))
            self.db.audit(actor.user_id, "gateway_credential_issued", tenant_id=actor.tenant_id, target=project_id,
                          detail=provider)
        return GatewayCredential(token, exp, provider, project_id)

    def call(self, token: str, *, tenant_id: str, project_id: str, provider: str, request_class: str,
             payload: dict[str, Any], reservation_ok: Callable[[], bool] = lambda: True) -> dict:
        row = self.db.conn.execute("SELECT * FROM gateway_credentials WHERE token_hash=?",
                                   (hashlib.sha256(token.encode()).hexdigest(),)).fetchone()
        if row is None or row["revoked_at"] is not None or row["expires_at"] <= self.db.clock():
            raise GatewayDenied("credential invalid, expired or revoked")
        if (row["tenant_id"], row["project_id"], row["provider"]) != (tenant_id, project_id, provider):
            raise GatewayDenied("credential is not valid for this tenant/project/provider")
        if request_class not in row["request_classes"].split(","):
            raise GatewayDenied(f"request class {request_class} not permitted by this credential")
        if any(k in payload for k in ("url", "base_url", "endpoint", "host")):
            raise GatewayDenied("the gateway does not forward to caller-chosen destinations")
        if not reservation_ok():
            raise GatewayDenied("no budget reservation covers this request")
        adapter = self.adapters.get(provider)
        if adapter is None:
            raise GatewayDenied(f"no certified adapter for {provider}")
        key = self.vault._decrypt_for_gateway(tenant_id, row["key_id"])
        register_secret(key)
        try:
            result = adapter(key, dict(payload))
        except Exception as e:  # noqa: BLE001 - never let the key escape in an error message
            raise GatewayDenied(f"provider call failed: {redact(str(e)).replace(key, '[REDACTED]')}") from None
        finally:
            del key
        return {"data": result.get("data"), "usage": result.get("usage", {})}
