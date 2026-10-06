"""Update channels, signed manifests, compatibility report, rollback, and no updates mid-task.

Plan: "Updates use versioned channels, signed/checksummed distributions and a compatibility report.
Back up project state before database/schema or template migrations. Do not update a model, editor,
provider adapter or acceptance rubric halfway through an active task. Keep the previous working
version available for rollback."

Layout under the install root::

    versions/<version>/      unpacked distributions (never deleted by an update)
    current                  the active version (written atomically)
    previous                 the version to roll back to
    backups/                 pre-update database snapshots (sqlite backup API)
    history.jsonl            every apply/rollback

Update manifests are JSON signed with Ed25519 (:mod:`forge.release.keys`) by a key the installer
trusts. Without the ``cryptography`` package the signature cannot be checked: the updater then
verifies only the artifact's sha256, says so in the report, and proceeds only when the caller
explicitly passes ``allow_checksum_only=True``.
"""

from __future__ import annotations

import json
import os
import sqlite3
import sys
import tarfile
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from ..models import TaskState
from ..release.keys import CryptoUnavailable, SignatureInvalid, verify_record
from .downloads import Fetcher, sha256_file

#: Root states that mean a task is mid-flight: an update now could change the toolchain,
#: provider adapter or acceptance rubric under it.
ACTIVE_STATES = (TaskState.READY, TaskState.RUNNING, TaskState.VERIFYING, TaskState.AWAITING_APPROVAL,
                 TaskState.INTEGRATION_READY, TaskState.INTEGRATING, TaskState.RETRY_PENDING,
                 TaskState.CANCEL_REQUESTED, TaskState.PAUSED)
CHANNELS = ("stable", "beta")


class UpdateError(Exception):
    pass


class UpdateBlocked(UpdateError):
    pass


def _vtuple(v: str) -> tuple:
    parts = []
    for p in v.split("."):
        num = "".join(ch for ch in p if ch.isdigit())
        parts.append(int(num) if num else 0)
    return tuple(parts)


@dataclass
class ManifestCheck:
    signature_verified: bool
    checksum_only: bool
    note: str


def verify_manifest(manifest: dict, trusted_keys: list[str]) -> ManifestCheck:
    try:
        verify_record(manifest, trusted_keys)
        return ManifestCheck(True, False, "Ed25519 signature verified with a trusted key")
    except CryptoUnavailable:
        return ManifestCheck(False, True, "signature NOT verified: the `cryptography` package is not installed; "
                                          "only the artifact sha256 will be checked")
    except SignatureInvalid as e:
        raise UpdateError(f"update manifest rejected: {e}") from e


@dataclass
class CompatibilityReport:
    current: Optional[str]
    target: str
    channel: str
    blockers: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    migrations: list[str] = field(default_factory=list)
    active_tasks: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return not self.blockers

    def markdown(self) -> str:
        out = [f"Update {self.current or '(none)'} -> {self.target} on channel {self.channel}: "
               f"{'OK' if self.ok else 'BLOCKED'}"]
        for title, items in (("Blockers", self.blockers), ("Warnings", self.warnings), ("Migrations", self.migrations)):
            if items:
                out += [f"{title}:"] + [f"  - {i}" for i in items]
        return "\n".join(out) + "\n"


class Updater:
    def __init__(self, install_root: str | Path, *, trusted_keys: list[str], store=None, db_path: str | Path | None = None,
                 schema_version: int = 1, template_versions: dict[str, str] | None = None, clock=time.time):
        self.root = Path(install_root)
        self.trusted_keys = trusted_keys
        self.store = store
        self.db_path = Path(db_path) if db_path else None
        self.schema_version = schema_version
        self.template_versions = template_versions or {}
        self.clock = clock
        (self.root / "versions").mkdir(parents=True, exist_ok=True)

    # ---------------------------------------------------------------- pointers
    def _read(self, name: str) -> Optional[str]:
        p = self.root / name
        return p.read_text().strip() or None if p.exists() else None

    def _write(self, name: str, value: str) -> None:
        fd, tmp = tempfile.mkstemp(dir=self.root, prefix=f".{name}-")
        with os.fdopen(fd, "w") as fh:
            fh.write(value + "\n")
        os.replace(tmp, self.root / name)

    @property
    def current(self) -> Optional[str]:
        return self._read("current")

    @property
    def previous(self) -> Optional[str]:
        return self._read("previous")

    def installed_versions(self) -> list[str]:
        return sorted((p.name for p in (self.root / "versions").iterdir() if p.is_dir()), key=_vtuple)

    def _log(self, event: str, **data) -> None:
        with open(self.root / "history.jsonl", "a", encoding="utf-8") as fh:
            fh.write(json.dumps({"at": self.clock(), "event": event, **data}, sort_keys=True) + "\n")

    def history(self) -> list[dict]:
        p = self.root / "history.jsonl"
        return [json.loads(x) for x in p.read_text().splitlines()] if p.exists() else []

    # ---------------------------------------------------------------- checks
    def active_tasks(self) -> list[str]:
        if self.store is None:
            return []
        return [f"{r.ticket or r.id} ({r.state.value})" for r in self.store.list_roots(states=list(ACTIVE_STATES))]

    def compatibility(self, manifest: dict) -> CompatibilityReport:
        target = manifest.get("version", "")
        rep = CompatibilityReport(current=self.current, target=target, channel=manifest.get("channel", "?"))
        if manifest.get("channel") not in CHANNELS:
            rep.blockers.append(f"unknown channel {manifest.get('channel')!r}")
        if not target:
            rep.blockers.append("manifest has no version")
        compat = manifest.get("compatibility", {})
        py = compat.get("python_min")
        if py and _vtuple(f"{sys.version_info.major}.{sys.version_info.minor}") < _vtuple(py):
            rep.blockers.append(f"needs Python {py}+, this is {sys.version_info.major}.{sys.version_info.minor}")
        new_schema = int(compat.get("schema_version", self.schema_version))
        if new_schema < self.schema_version:
            rep.blockers.append(f"target schema {new_schema} is older than the current {self.schema_version}; "
                                "use rollback, not an update")
        elif new_schema > self.schema_version:
            rep.migrations.append(f"database schema {self.schema_version} -> {new_schema} (backup taken first)")
        for tid, ver in self.template_versions.items():
            supported = compat.get("templates", {}).get(tid)
            if supported is None:
                rep.warnings.append(f"template {tid}@{ver}: not listed by the update")
            elif ver not in supported:
                rep.migrations.append(f"template {tid} {ver} -> one of {supported} (backup taken first)")
        for name in compat.get("disabled_adapters", []):
            rep.warnings.append(f"provider adapter {name} is disabled for new jobs by this update "
                                "(accepted artifacts and evidence stay accessible)")
        if self.current and target and _vtuple(target) <= _vtuple(self.current):
            rep.warnings.append(f"{target} is not newer than the current {self.current}")
        rep.active_tasks = self.active_tasks()
        if rep.active_tasks:
            rep.blockers.append("tasks are mid-flight; finish, hold or cancel them before updating: "
                                + ", ".join(rep.active_tasks))
        return rep

    # ---------------------------------------------------------------- apply / rollback
    def backup_db(self, label: str) -> Optional[Path]:
        if not self.db_path or not self.db_path.exists():
            return None
        dest = self.root / "backups" / f"forge-{label}-{int(self.clock())}.db"
        dest.parent.mkdir(parents=True, exist_ok=True)
        src = sqlite3.connect(str(self.db_path))
        try:
            dst = sqlite3.connect(str(dest))
            with dst:
                src.backup(dst)
            dst.close()
        finally:
            src.close()
        return dest

    def apply(self, manifest: dict, fetcher: Fetcher, *, by: str, allow_checksum_only: bool = False) -> dict:
        check = verify_manifest(manifest, self.trusted_keys)
        if check.checksum_only and not allow_checksum_only:
            raise UpdateBlocked(check.note + "; pass allow_checksum_only to proceed knowingly")
        rep = self.compatibility(manifest)
        if not rep.ok:
            raise UpdateBlocked("; ".join(rep.blockers))
        art = manifest.get("artifact") or {}
        if not art.get("url") or not art.get("sha256"):
            raise UpdateError("manifest lacks artifact url/sha256")
        version = manifest["version"]
        dest = self.root / "versions" / version
        if dest.exists():
            raise UpdateError(f"version {version} is already installed; use `rollback` or a newer version")
        backup = self.backup_db(f"pre-{version}")
        with tempfile.TemporaryDirectory(dir=self.root) as tmp:
            dl = Path(tmp) / "dist.tar.gz"
            fetcher.fetch(art["url"], dl)
            actual = sha256_file(dl)
            if actual != art["sha256"]:
                raise UpdateError(f"artifact sha256 {actual} != manifest {art['sha256']}")
            staging = Path(tmp) / "unpacked"
            _safe_extract(dl, staging)
            os.replace(staging, dest)
        prev = self.current
        if prev:
            self._write("previous", prev)
        self._write("current", version)
        result = {"version": version, "previous": prev, "backup": str(backup) if backup else None,
                  "signature_verified": check.signature_verified, "note": check.note, "by": by,
                  "migrations": rep.migrations, "warnings": rep.warnings}
        self._log("update_applied", **result)
        return result

    def rollback(self, *, by: str) -> dict:
        prev = self.previous
        if not prev or not (self.root / "versions" / prev).is_dir():
            raise UpdateError("no previous version is available to roll back to")
        active = self.active_tasks()
        if active:
            raise UpdateBlocked("tasks are mid-flight: " + ", ".join(active))
        cur = self.current
        self._write("current", prev)
        if cur:
            self._write("previous", cur)
        self._log("rollback", to=prev, from_=cur, by=by)
        return {"current": prev, "previous": cur,
                "note": "Database snapshots in backups/ are NOT restored automatically; restore one only after "
                        "checking it will not lose newer accepted work or ledger entries."}


def _safe_extract(archive: Path, dest: Path) -> None:
    dest.mkdir(parents=True)
    with tarfile.open(archive, "r:gz") as tf:
        for m in tf.getmembers():
            p = Path(m.name)
            if p.is_absolute() or ".." in p.parts or m.issym() or m.islnk() or m.isdev():
                raise UpdateError(f"unsafe path in distribution: {m.name}")
        tf.extractall(dest, filter="data")
