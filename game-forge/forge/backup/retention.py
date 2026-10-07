"""Retention enforcement for Forge's local evidence (plan: "Data and artifact retention defaults").

Defaults (overridable by a stricter project policy, ``[policy] retention_days_*``):

* **failed candidates** - every artifact of an attempt that did not become the accepted result
  (logs, diffs, renders, videos) and its ``forge/cand/<attempt>`` branch: **14 days** after the
  attempt finished;
* **raw diagnostics and video** of the accepted attempt (check logs, device logcat, captures listed
  as ``details.raw_refs``): **30 days** after the evidence was recorded;
* **accepted source, release evidence and provenance**: kept until the owner deletes the project.

Holds: evidence attached to an *unresolved* defect is preserved - every artifact of a root that is
not in a terminal state is kept, and the owner can place an explicit hold on a root
(``forge retention hold <task>``). An artifact referenced by anything that is kept is never deleted
(the store is content-addressed, so one blob can serve several records).

The owner can export everything a run would delete (``--export DIR``) before deleting it. Each
deletion is recorded in the append-only event log; evidence rows keep their metadata so the review
page still shows that the bytes were removed by retention.
"""

from __future__ import annotations

import shutil
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from ..artifacts import ArtifactStore
from ..models import AttemptStatus, Evidence, EvidenceClass, ProjectPolicy, TERMINAL_STATES, TaskState
from ..store import Store

DAY = 86_400.0
HOLD_PREFIX = "retention.hold:"


@dataclass
class RetentionPolicy:
    failed_candidates_days: int = 14
    raw_diagnostics_days: int = 30

    @classmethod
    def from_project(cls, policy: ProjectPolicy) -> "RetentionPolicy":
        """The owner's project policy (defaults 14 / 30 days); non-positive values fall back to the defaults."""
        return cls(policy.retention_days_failed_candidates if policy.retention_days_failed_candidates > 0 else 14,
                   policy.retention_days_raw_diagnostics if policy.retention_days_raw_diagnostics > 0 else 30)


@dataclass
class RetentionItem:
    digest: str
    reason: str
    root_id: str
    attempt_id: Optional[str]
    evidence: str
    age_days: float


@dataclass
class RetentionPlan:
    delete: list[RetentionItem] = field(default_factory=list)
    branches: list[dict] = field(default_factory=list)  # {"branch", "root_id", "attempt_id"}
    kept: dict[str, str] = field(default_factory=dict)  # digest -> why it is kept
    held_roots: list[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {"delete": [i.__dict__ for i in self.delete], "branches": self.branches,
                "kept_count": len(self.kept), "held_roots": self.held_roots}


def hold(store: Store, root_id: str, by: str, reason: str = "") -> None:
    store.set_setting(HOLD_PREFIX + root_id, reason or "owner hold")
    store.append_event("retention_hold", root_id=root_id, by=by, reason=reason)


def release_hold(store: Store, root_id: str, by: str) -> None:
    with store.tx() as c:
        c.execute("DELETE FROM settings WHERE key=?", (HOLD_PREFIX + root_id,))
        store.append_event("retention_hold_released", root_id=root_id, by=by)


def _raw_refs(ev: Evidence) -> set[str]:
    d = ev.details or {}
    refs = set((d.get("logs") or {}).values()) | set(d.get("raw_refs") or [])
    return {r for r in refs if isinstance(r, str)}


def _all_refs(ev: Evidence) -> set[str]:
    refs = set(ev.artifact_refs) | _raw_refs(ev)
    for k in ("diff",):
        if isinstance((ev.details or {}).get(k), str):
            refs.add(ev.details[k])
    for v in ((ev.details or {}).get("renders") or {}).values():
        if isinstance(v, str):
            refs.add(v)
    return {r for r in refs if len(r) == 64}


def plan_retention(store: Store, project_id: str, policy: RetentionPolicy, *, now: Optional[float] = None
                   ) -> RetentionPlan:
    now = store.now() if now is None else now
    plan = RetentionPlan()
    candidates: dict[str, RetentionItem] = {}

    def keep(d: str, why: str) -> None:
        plan.kept.setdefault(d, why)

    for root in store.list_roots(project_id):
        held = store.get_setting(HOLD_PREFIX + root.id)
        evs = store.list_evidence(root.id)
        if held or root.state not in TERMINAL_STATES:
            if held:
                plan.held_roots.append(root.id)
            for ev in evs:
                for d in _all_refs(ev):
                    keep(d, "owner hold" if held else "unresolved task (not terminal)")
            continue
        attempts = {a.id: a for a in store.list_attempts(root.id)}
        accepted_attempt = None
        if root.state == TaskState.ACCEPTED:
            accepted_attempt = next((a.id for a in attempts.values() if a.status == AttemptStatus.INTEGRATED), None)
        for ev in evs:
            age = (now - ev.created_at) / DAY
            if ev.attempt_id is None or ev.attempt_id == accepted_attempt:
                raw = _raw_refs(ev)
                for d in _all_refs(ev) - raw:
                    keep(d, "accepted result / release evidence / provenance")
                for d in raw:
                    if ev.evidence_class == EvidenceClass.HUMAN:
                        keep(d, "human decision record")
                    elif age >= policy.raw_diagnostics_days:
                        candidates.setdefault(d, RetentionItem(d, f"raw diagnostic older than "
                                                                  f"{policy.raw_diagnostics_days} days",
                                                               root.id, ev.attempt_id, ev.name, round(age, 1)))
                    else:
                        keep(d, "raw diagnostic within retention")
                continue
            a = attempts.get(ev.attempt_id)
            finished = (a.finished_at if a and a.finished_at else ev.created_at)
            fage = (now - finished) / DAY
            for d in _all_refs(ev):
                if fage >= policy.failed_candidates_days:
                    candidates.setdefault(d, RetentionItem(d, f"failed candidate older than "
                                                              f"{policy.failed_candidates_days} days",
                                                           root.id, ev.attempt_id, ev.name, round(fage, 1)))
                else:
                    keep(d, "failed candidate within retention")
        for a in attempts.values():
            if a.id == accepted_attempt or not a.branch:
                continue
            if a.status in (AttemptStatus.INTEGRATED,):
                continue
            finished = a.finished_at or a.started_at
            if (now - finished) / DAY >= policy.failed_candidates_days:
                plan.branches.append({"branch": a.branch, "root_id": root.id, "attempt_id": a.id})
    plan.delete = [i for d, i in sorted(candidates.items()) if d not in plan.kept]
    return plan


@dataclass
class RetentionReport:
    deleted: list[str]
    exported: list[str]
    branches_deleted: list[str]
    missing: list[str]
    freed_bytes: int

    def to_dict(self) -> dict:
        return dict(self.__dict__)


def apply_retention(store: Store, artifacts: ArtifactStore, plan: RetentionPlan, *, repo_path: Path | None = None,
                    export_dir: Path | None = None, by: str = "forge") -> RetentionReport:
    from ..gitops import git

    deleted, exported, missing, branches = [], [], [], []
    freed = 0
    if export_dir is not None:
        export_dir.mkdir(parents=True, exist_ok=True)
    for item in plan.delete:
        try:
            p = artifacts.path_for(item.digest)
        except ValueError:
            continue
        if not p.exists():
            missing.append(item.digest)
            continue
        if export_dir is not None:
            dst = export_dir / item.root_id / item.digest
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(p, dst)
            exported.append(str(dst))
        freed += p.stat().st_size
        p.unlink()
        deleted.append(item.digest)
    if repo_path is not None:
        for b in plan.branches:
            git(["branch", "-D", b["branch"]], repo_path, check=False)
            branches.append(b["branch"])
    store.append_event("retention_applied", by=by, deleted=len(deleted), branches=branches, freed_bytes=freed,
                       exported=len(exported), digests=deleted[:500],
                       reasons=sorted({i.reason for i in plan.delete}))
    return RetentionReport(deleted, exported, branches, missing, freed)
