"""Restore drill: back up, restore into a scratch directory and compare with the live project.

The plan requires "a restore drill from the database, repository and artifact backup before
calling the workflow recoverable" and a verified restore before an update or machine migration
is considered safe. The drill result is appended to the live event log (``restore_drill``) and
the last successful drill time is kept in ``settings`` so the UI/CLI can show it.
"""

from __future__ import annotations

import json
import sqlite3
import tempfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from ..artifacts import ArtifactStore
from ..store import Store
from .archive import BackupError, BackupSources, _git, create_backup, restore_backup, verify_backup

LAST_DRILL_KEY = "backup.last_restore_drill"


@dataclass
class DrillReport:
    ok: bool
    problems: list[str]
    archive: Optional[str] = None
    checked: dict = field(default_factory=dict)

    def to_dict(self) -> dict:
        return {"ok": self.ok, "problems": self.problems, "archive": self.archive, "checked": self.checked}


def _referenced_artifacts(db: Path) -> set[str]:
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        refs: set[str] = set()
        for (js,) in con.execute("SELECT json FROM evidence"):
            ev = json.loads(js)
            refs.update(ev.get("artifact_refs") or [])
            refs.update((ev.get("details") or {}).get("logs", {}).values())
        return {r for r in refs if isinstance(r, str) and len(r) == 64}
    finally:
        con.close()


def restore_drill(sources: BackupSources, store: Store, passphrase: bytes, *, work_dir: str | Path | None = None,
                  keep_archive: str | Path | None = None, log2_n: int | None = None) -> DrillReport:
    problems: list[str] = []
    checked: dict = {}
    with tempfile.TemporaryDirectory(prefix="forge-drill-", dir=str(work_dir) if work_dir else None) as tmp:
        tmp_p = Path(tmp)
        archive = Path(keep_archive) if keep_archive else tmp_p / "drill.fgbk"
        try:
            manifest = create_backup(sources, archive, passphrase, clock=store.clock, log2_n=log2_n)
            vr = verify_backup(archive, passphrase)
            problems += [f"verify: {p}" for p in vr.problems]
            rr = restore_backup(archive, passphrase, tmp_p / "restore")
            problems += [f"restore: {p}" for p in rr.problems]
        except (BackupError, OSError) as e:
            problems.append(str(e))
            return _record(store, DrillReport(False, problems, str(archive) if keep_archive else None, checked))
        # 1. the restored database opens with Forge and matches the live counts taken in the backup
        restored = Store(rr.data_dir / "forge.db", clock=store.clock)
        try:
            live_roots = {r.id: r.state for r in store.list_roots()}
            got_roots = {r.id: r.state for r in restored.list_roots()}
            checked["roots"] = len(got_roots)
            if live_roots != got_roots:
                problems.append("restored task states differ from the live database")
            checked["approvals"] = sum(len(restored.list_approvals(rid)) for rid in got_roots)
            live_events = len(store.events(limit=10_000_000))
            got_events = len(restored.events(limit=10_000_000))
            checked["events"] = got_events
            if got_events < manifest.counts.get("events", 0):
                problems.append(f"restored event log has {got_events} events, backup recorded "
                                f"{manifest.counts.get('events')}")
            if got_events > live_events:
                problems.append("restored event log is longer than the live one")
        finally:
            restored.close()
        # 2. every artifact referenced by evidence is present and intact
        refs = _referenced_artifacts(rr.data_dir / "forge.db")
        rstore = ArtifactStore(rr.data_dir / "artifacts")
        missing = []
        for d in sorted(refs):
            try:
                rstore.get_bytes(d)
            except (OSError, ValueError):
                missing.append(d)
        checked["artifacts_referenced"] = len(refs)
        live_store = ArtifactStore(sources.artifacts_dir)
        missing_live = [d for d in missing if live_store.exists(d)]
        if missing_live:
            problems.append(f"{len(missing_live)} referenced artifact(s) missing from the restore: "
                            + ", ".join(m[:12] for m in missing_live[:5]))
        checked["artifacts_already_deleted_by_retention"] = len(missing) - len(missing_live)
        # 3. the accepted branch in the restored repository equals the live accepted head at backup time
        try:
            live_head = _git(["rev-parse", f"refs/heads/{sources.accepted_branch}"], sources.repo_path)
        except BackupError:
            live_head = None
        checked["accepted_head"] = manifest.accepted_head
        if manifest.accepted_head and live_head and manifest.accepted_head != live_head:
            checked["note"] = "accepted branch advanced after the backup was taken"
        report = DrillReport(not problems, problems, str(archive) if keep_archive else None, checked)
    return _record(store, report)


def _record(store: Store, report: DrillReport) -> DrillReport:
    store.append_event("restore_drill", ok=report.ok, problems=report.problems[:20], checked=report.checked)
    if report.ok:
        store.set_setting(LAST_DRILL_KEY, str(store.now()))
    return report
