"""Encrypted project backups, verification, restore and the restore drill.

What a backup holds (plan: "Installer support updates backups and telemetry"):

* ``db/forge.db`` - the durable queue, ledgers, approvals, evidence index and append-only events,
  copied with SQLite's online backup API (consistent while Forge runs);
* ``repo/repo.bundle`` - every branch and tag of the project repository (accepted main, candidate
  branches, source and specifications) as a ``git bundle``;
* ``artifacts/`` - the content-addressed artifact store (accepted bytes, renders, logs);
* ``records/approvals_provenance.json`` - approvals, owner decisions, human evidence, asset
  provenance and the licence inventory as plain JSON, readable without Forge;
* ``config/`` - the owner's project configuration files.

Credentials are excluded: key files, keystores, ``.env`` files and anything that matches the
secret patterns are refused at collection time and listed in the manifest's ``excluded`` list. The
archive is encrypted with :mod:`forge.backup.crypto`; the passphrase stays outside the archive.

``restore_drill`` proves recoverability: it creates a backup, restores it into a scratch
directory and compares the restored database, repository and artifacts with the live project.
"""

from __future__ import annotations

import fnmatch
import hashlib
import json
import os
import shutil
import sqlite3
import subprocess
import tarfile
import tempfile
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Optional

from ..credentials import redact
from ..util import SystemClock, iso
from .crypto import DecryptingReader, EncryptingWriter

FORMAT = "forge-backup/1"

#: File-name patterns that are never put into a backup (credentials and signing material).
CREDENTIAL_PATTERNS = ("*.key", "*.pem", "*.jks", "*.keystore", "*.p12", "*.pfx", ".env", ".env.*",
                       "*credential*", "*secret*", "*token*", "*.gpg", "id_rsa*", "id_ed25519*", "*passphrase*")


class BackupError(Exception):
    pass


@dataclass
class BackupSources:
    project_id: str
    data_dir: Path
    repo_path: Path
    accepted_branch: str = "forge/accepted"
    config_files: list[Path] = field(default_factory=list)
    #: Directories whose contents must never be included (credential stores).
    credential_dirs: list[Path] = field(default_factory=lambda: [Path("~/.config/game-forge").expanduser()])

    @property
    def db_path(self) -> Path:
        return self.data_dir / "forge.db"

    @property
    def artifacts_dir(self) -> Path:
        return self.data_dir / "artifacts"


@dataclass
class FileEntry:
    path: str
    sha256: str
    size: int


@dataclass
class BackupManifest:
    format: str
    project_id: str
    created_at: float
    accepted_branch: str
    accepted_head: Optional[str]
    counts: dict[str, int]
    files: list[FileEntry]
    excluded: list[dict[str, str]]
    credentials_excluded: list[str] = field(default_factory=lambda: list(CREDENTIAL_PATTERNS))

    def to_dict(self) -> dict:
        d = asdict(self)
        d["created"] = iso(self.created_at)
        return d

    @classmethod
    def from_dict(cls, d: dict) -> "BackupManifest":
        d = dict(d)
        d.pop("created", None)
        d["files"] = [FileEntry(**f) for f in d["files"]]
        return cls(**d)


@dataclass
class VerifyReport:
    ok: bool
    problems: list[str]
    manifest: Optional[BackupManifest] = None
    checked_files: int = 0

    def to_dict(self) -> dict:
        return {"ok": self.ok, "problems": self.problems, "checked_files": self.checked_files,
                "manifest": self.manifest.to_dict() if self.manifest else None}


def _sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def _git(args: list[str], cwd: Path) -> str:
    env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
    r = subprocess.run(["git", *args], cwd=str(cwd), capture_output=True, text=True, env=env)
    if r.returncode != 0:
        raise BackupError(f"git {' '.join(args)} failed: {r.stderr.strip()[:400]}")
    return r.stdout.strip()


def is_credential_name(name: str) -> bool:
    low = name.lower()
    return any(fnmatch.fnmatch(low, pat) for pat in CREDENTIAL_PATTERNS)


def _db_counts(db: Path) -> dict[str, int]:
    out = {}
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        names = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        for t in ("roots", "attempts", "evidence", "approvals", "review_decisions", "events", "reservations",
                  "usage_records", "cash_expenses", "asset_provenance", "asset_lanes", "asset_licence_gates"):
            if t in names:
                out[t] = con.execute(f"SELECT COUNT(*) FROM {t}").fetchone()[0]
    finally:
        con.close()
    return out


def export_records(db: Path) -> dict[str, Any]:
    """Approvals, decisions, human evidence and provenance as plain JSON (readable without Forge)."""
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    try:
        names = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}

        def rows(sql: str) -> list[dict]:
            return [json.loads(r["json"]) for r in con.execute(sql)]

        out: dict[str, Any] = {
            "approvals": rows("SELECT json FROM approvals ORDER BY id"),
            "review_decisions": rows("SELECT json FROM review_decisions ORDER BY id"),
            "human_evidence": [e for e in rows("SELECT json FROM evidence ORDER BY id")
                               if e.get("evidence_class") in ("human", "asset")],
            "accepted_roots": [r for r in rows("SELECT json FROM roots ORDER BY id") if r.get("state") == "ACCEPTED"],
        }
        if "asset_provenance" in names:
            out["asset_provenance"] = rows("SELECT json FROM asset_provenance ORDER BY asset_id, version")
        if "asset_licence_gates" in names:
            out["licence_gates"] = rows("SELECT json FROM asset_licence_gates ORDER BY route")
        return out
    finally:
        con.close()


def _collect(sources: BackupSources, stage: Path) -> tuple[list[tuple[Path, str]], list[dict], dict, Optional[str]]:
    """Stage DB copy, repo bundle and records; list (source path, archive name) pairs."""
    items: list[tuple[Path, str]] = []
    excluded: list[dict] = []
    if not sources.db_path.exists():
        raise BackupError(f"no Forge database at {sources.db_path}")
    db_copy = stage / "forge.db"
    src = sqlite3.connect(str(sources.db_path))
    dst = sqlite3.connect(str(db_copy))
    try:
        src.backup(dst)  # consistent online copy (WAL included)
    finally:
        dst.close()
        src.close()
    items.append((db_copy, "db/forge.db"))
    bundle = stage / "repo.bundle"
    _git(["bundle", "create", str(bundle), "--all"], sources.repo_path)
    items.append((bundle, "repo/repo.bundle"))
    try:
        head = _git(["rev-parse", f"refs/heads/{sources.accepted_branch}"], sources.repo_path)
    except BackupError:
        head = None
    records = stage / "approvals_provenance.json"
    records.write_text(json.dumps(export_records(db_copy), indent=1, sort_keys=True, default=str))
    items.append((records, "records/approvals_provenance.json"))
    if sources.artifacts_dir.exists():
        for p in sorted(sources.artifacts_dir.rglob("*")):
            if p.is_file() and not p.name.startswith(".tmp-"):
                items.append((p, "artifacts/" + p.relative_to(sources.artifacts_dir).as_posix()))
    cred_dirs = [d.resolve() for d in sources.credential_dirs if d.exists()]
    for cf in sources.config_files:
        cf = Path(cf)
        if not cf.exists():
            continue
        real = cf.resolve()
        why = None
        if is_credential_name(real.name):
            why = "credential-like file name"
        elif any(real == d or d in real.parents for d in cred_dirs):
            why = "inside a credential directory"
        else:
            text = real.read_text(errors="replace")
            if redact(text) != text:
                why = "content matches a secret pattern"
        if why:
            excluded.append({"path": str(real), "reason": why})
            continue
        items.append((real, f"config/{real.name}"))
    counts = _db_counts(db_copy)
    return items, excluded, counts, head


def create_backup(sources: BackupSources, out_path: str | Path, passphrase: bytes, *, clock=None,
                  log2_n: int | None = None) -> BackupManifest:
    """Write an encrypted backup archive to ``out_path``. The parent directory must exist."""
    out_path = Path(out_path)
    if out_path.exists():
        raise BackupError(f"{out_path} already exists; backups are never overwritten")
    clock = clock or SystemClock()
    with tempfile.TemporaryDirectory(prefix="forge-backup-") as tmp:
        stage = Path(tmp)
        items, excluded, counts, head = _collect(sources, stage)
        entries = [FileEntry(name, _sha256_file(p), p.stat().st_size) for p, name in items]
        manifest = BackupManifest(FORMAT, sources.project_id, clock.now(), sources.accepted_branch, head, counts,
                                  entries, excluded)
        mbytes = json.dumps(manifest.to_dict(), indent=1, sort_keys=True).encode()
        partial = out_path.with_name(out_path.name + ".partial")
        try:
            with open(partial, "wb") as raw:
                kw = {"log2_n": log2_n} if log2_n else {}
                enc = EncryptingWriter(raw, passphrase, **kw)
                with tarfile.open(fileobj=enc, mode="w|") as tar:
                    info = tarfile.TarInfo("manifest.json")
                    info.size, info.mtime = len(mbytes), int(manifest.created_at)
                    import io

                    tar.addfile(info, io.BytesIO(mbytes))
                    for p, name in items:
                        tar.add(str(p), arcname=name, recursive=False)
                enc.close()
                os.fsync(raw.fileno())
            os.chmod(partial, 0o600)
            os.replace(partial, out_path)
        finally:
            if partial.exists():
                partial.unlink()
    side = out_path.with_name(out_path.name + ".sha256")
    side.write_text(f"{_sha256_file(out_path)}  {out_path.name}\n")
    return manifest


def _open_archive(path: Path, passphrase: bytes):
    fh = open(path, "rb")
    reader = DecryptingReader(fh, passphrase)
    return fh, tarfile.open(fileobj=reader, mode="r|")


def _safe_member(m: tarfile.TarInfo) -> bool:
    name = m.name
    return (m.isfile() or m.isdir()) and not name.startswith("/") and ".." not in Path(name).parts


def extract(path: Path, passphrase: bytes, target: Path) -> BackupManifest:
    """Decrypt and extract into ``target`` (must be empty or absent). Only regular files are written."""
    if target.exists() and any(target.iterdir()):
        raise BackupError(f"restore target {target} is not empty; restore never overwrites existing data")
    target.mkdir(parents=True, exist_ok=True)
    fh, tar = _open_archive(path, passphrase)
    try:
        for m in tar:
            if not _safe_member(m):
                raise BackupError(f"archive member {m.name!r} is not a safe regular file")
            tar.extract(m, target, filter="data")
    finally:
        tar.close()
        fh.close()
    mp = target / "manifest.json"
    if not mp.exists():
        raise BackupError("archive has no manifest")
    return BackupManifest.from_dict(json.loads(mp.read_text()))


def _check_extracted(root: Path, manifest: BackupManifest) -> tuple[list[str], int]:
    problems = []
    n = 0
    listed = set()
    for f in manifest.files:
        listed.add(f.path)
        p = root / f.path
        if not p.exists():
            problems.append(f"missing {f.path}")
            continue
        n += 1
        if _sha256_file(p) != f.sha256:
            problems.append(f"hash mismatch {f.path}")
        if f.path.startswith("artifacts/") and p.name != f.sha256:
            problems.append(f"artifact {f.path} content does not match its address")
    for p in root.rglob("*"):
        if p.is_file():
            rel = p.relative_to(root).as_posix()
            if rel != "manifest.json" and rel not in listed and not rel.startswith("restored/"):
                problems.append(f"unlisted file {rel}")
            if is_credential_name(p.name):
                problems.append(f"credential-like file in backup: {rel}")
    db = root / "db" / "forge.db"
    if db.exists():
        con = sqlite3.connect(str(db))
        try:
            res = con.execute("PRAGMA integrity_check").fetchone()[0]
        finally:
            con.close()
        if res != "ok":
            problems.append(f"database integrity check: {res}")
        counts = _db_counts(db)
        if counts != manifest.counts:
            problems.append(f"database counts {counts} differ from manifest {manifest.counts}")
    bundle = root / "repo" / "repo.bundle"
    if bundle.exists():
        with tempfile.TemporaryDirectory(prefix="forge-bundle-") as scratch:
            try:  # `git bundle verify` needs a repository; an empty bare one checks a full bundle
                _git(["init", "--quiet", "--bare", scratch], root)
                _git(["bundle", "verify", "--quiet", str(bundle)], Path(scratch))
            except BackupError as e:
                problems.append(str(e))
    return problems, n


def verify_backup(path: str | Path, passphrase: bytes) -> VerifyReport:
    """Decrypt into a scratch directory and check every hash, the database and the bundle."""
    from .crypto import BackupCryptoError

    path = Path(path)
    side = path.with_name(path.name + ".sha256")
    problems = []
    if side.exists() and side.read_text().split()[0] != _sha256_file(path):
        problems.append("archive hash does not match its .sha256 sidecar")
    with tempfile.TemporaryDirectory(prefix="forge-verify-") as tmp:
        try:
            manifest = extract(path, passphrase, Path(tmp) / "x")
        except (BackupCryptoError, BackupError, tarfile.TarError) as e:
            return VerifyReport(False, problems + [str(e)])
        more, n = _check_extracted(Path(tmp) / "x", manifest)
    problems += more
    return VerifyReport(not problems, problems, manifest, n)


@dataclass
class RestoreReport:
    target: Path
    manifest: BackupManifest
    data_dir: Path
    repo_mirror: Path
    problems: list[str]

    @property
    def ok(self) -> bool:
        return not self.problems

    def to_dict(self) -> dict:
        return {"ok": self.ok, "target": str(self.target), "data_dir": str(self.data_dir),
                "repo_mirror": str(self.repo_mirror), "problems": self.problems,
                "accepted_head": self.manifest.accepted_head, "counts": self.manifest.counts}


def restore_backup(path: str | Path, passphrase: bytes, target: str | Path) -> RestoreReport:
    """Restore into ``target``: ``data/forge.db``, ``data/artifacts`` and a bare ``repo.git`` mirror.

    Restore never touches the live project. To switch to the restored copy, point
    ``[forge] data_dir`` at ``<target>/data`` and clone ``<target>/repo.git``.
    """
    target = Path(target)
    raw = target / "archive"
    manifest = extract(Path(path), passphrase, raw)
    problems, _ = _check_extracted(raw, manifest)
    data = target / "data"
    data.mkdir(parents=True, exist_ok=True)
    if (raw / "db" / "forge.db").exists():
        shutil.copy2(raw / "db" / "forge.db", data / "forge.db")
    if (raw / "artifacts").exists():
        shutil.copytree(raw / "artifacts", data / "artifacts")
    mirror = target / "repo.git"
    if (raw / "repo" / "repo.bundle").exists():
        _git(["clone", "--quiet", "--mirror", str(raw / "repo" / "repo.bundle"), str(mirror)], target)
        if manifest.accepted_head:
            try:
                got = _git(["rev-parse", f"refs/heads/{manifest.accepted_branch}"], mirror)
            except BackupError as e:
                got = str(e)
            if got != manifest.accepted_head:
                problems.append(f"restored {manifest.accepted_branch} is {got}, expected {manifest.accepted_head}")
    return RestoreReport(target, manifest, data, mirror, problems)
