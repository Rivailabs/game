"""The outbound cloud link: authenticated job messages a worker accepts from a hosted queue.

Plan ("Workspace isolation Unity integration and the cloud link"): keep the link outbound from the
worker. An authenticated queue message names a permitted workflow, project, commit, artifacts, lease
and expiry. It is not an arbitrary shell command. Authenticate artifacts by hash, reject replayed jobs
and use short-lived project-scoped identities. A hosted review service cannot grant itself
code-execution or signing authority.

The worker *polls* (outbound only; nothing here listens on a port). :func:`verify` accepts a message
only when all of these hold:

* the schema is exact (unknown fields such as ``command``/``shell``/``argv`` are rejected);
* the HMAC-SHA256 signature matches the **project-scoped** key for ``key_id`` (keys are short-lived:
  each has an expiry and the message may not outlive it);
* the workflow is in the worker's allow-list, which never contains signing or publishing;
* the project, commit (40-hex) and every artifact (sha256) are well formed and the project matches;
* ``issued_at <= now < expires_at`` with a bounded lifetime, and the nonce has never been seen (the
  nonce is recorded durably, so a replay is rejected even after a restart).
"""

from __future__ import annotations

import hashlib
import hmac
import json
import re
from dataclasses import dataclass
from typing import Callable, Optional

from pydantic import BaseModel, ConfigDict, Field, ValidationError

from ..store import Store

#: Workflows a worker may run on behalf of the hosted queue. Signing, store submission and any
#: free-form execution are deliberately absent: they are separate, locally approved operations.
DEFAULT_WORKFLOWS = frozenset({"verify_candidate", "integrate_candidate", "unity_build", "device_scenario",
                               "asset_segment", "backup_drill"})
FORBIDDEN_WORKFLOWS = frozenset({"sign_release", "publish_store", "shell", "exec", "run_command"})
MAX_LIFETIME_S = 15 * 60
_SHA = re.compile(r"^[0-9a-f]{64}$")
_COMMIT = re.compile(r"^[0-9a-f]{40}$")

SCHEMA = """
CREATE TABLE IF NOT EXISTS link_nonces (nonce TEXT PRIMARY KEY, key_id TEXT NOT NULL, seen_at REAL NOT NULL);
"""


class LinkRejected(Exception):
    pass


class JobMessage(BaseModel):
    model_config = ConfigDict(extra="forbid")  # no command/shell/argv field can ride along

    key_id: str
    project_id: str
    workflow: str
    root_id: str
    commit: str
    artifacts: dict[str, str] = Field(default_factory=dict)  # name -> sha256
    lease: str  # resource lease the job must hold (e.g. "workspace:...", "device:<serial>")
    issued_at: float
    expires_at: float
    nonce: str
    signature: str = ""


@dataclass(frozen=True)
class LinkKey:
    key_id: str
    project_id: str
    secret: bytes
    expires_at: float


def canonical(msg: dict) -> bytes:
    body = {k: v for k, v in msg.items() if k != "signature"}
    return json.dumps(body, sort_keys=True, separators=(",", ":")).encode()


def sign(msg: dict, key: LinkKey) -> dict:
    """Hosted-queue side (and tests): attach the HMAC signature."""
    out = dict(msg, key_id=key.key_id)
    out["signature"] = hmac.new(key.secret, canonical(out), hashlib.sha256).hexdigest()
    return out


class LinkVerifier:
    def __init__(self, store: Store, project_id: str, keys: Callable[[str], Optional[LinkKey]], *,
                 workflows: frozenset[str] = DEFAULT_WORKFLOWS):
        bad = workflows & FORBIDDEN_WORKFLOWS
        if bad:
            raise ValueError(f"workflows {sorted(bad)} can never be accepted over the cloud link")
        self.store, self.project_id, self.keys, self.workflows = store, project_id, keys, workflows
        store.conn.executescript(SCHEMA)

    def verify(self, raw: str | bytes | dict) -> JobMessage:
        try:
            data = raw if isinstance(raw, dict) else json.loads(raw)
            msg = JobMessage.model_validate(data)
        except (ValueError, ValidationError) as e:
            self._reject("malformed", str(e)[:200])
        key = self.keys(msg.key_id)
        now = self.store.now()
        if key is None:
            self._reject("unknown key", msg.key_id)
        expected = hmac.new(key.secret, canonical(data), hashlib.sha256).hexdigest()
        if not hmac.compare_digest(expected, msg.signature):
            self._reject("bad signature", msg.key_id)
        if key.project_id != self.project_id or msg.project_id != self.project_id:
            self._reject("wrong project", f"{msg.project_id} via key for {key.project_id}")
        if now >= key.expires_at or msg.expires_at > key.expires_at:
            self._reject("expired key", msg.key_id)
        if msg.workflow not in self.workflows:
            self._reject("workflow not permitted", msg.workflow)
        if not _COMMIT.match(msg.commit):
            self._reject("commit is not a full hash", msg.commit[:50])
        for name, digest in msg.artifacts.items():
            if not _SHA.match(digest):
                self._reject("artifact not addressed by sha256", name)
        if not (msg.issued_at <= now < msg.expires_at) or msg.expires_at - msg.issued_at > MAX_LIFETIME_S:
            self._reject("outside its validity window", f"{msg.issued_at}..{msg.expires_at} at {now}")
        with self.store.tx() as c:
            if c.execute("SELECT 1 FROM link_nonces WHERE nonce=?", (msg.nonce,)).fetchone():
                self.store.append_event("cloud_link_rejected", reason="replayed nonce", nonce=msg.nonce)
                raise LinkRejected("replayed job message")
            c.execute("INSERT INTO link_nonces(nonce,key_id,seen_at) VALUES(?,?,?)", (msg.nonce, msg.key_id, now))
            self.store.append_event("cloud_link_accepted", project_id=msg.project_id, root_id=msg.root_id,
                                    workflow=msg.workflow, commit=msg.commit, lease=msg.lease, nonce=msg.nonce)
        return msg

    def _reject(self, reason: str, detail: str):
        self.store.append_event("cloud_link_rejected", reason=reason, detail=detail)
        raise LinkRejected(f"{reason}: {detail}")
