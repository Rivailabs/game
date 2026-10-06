"""Structured task-file import (TOML via tomllib, or JSON).

Release 1 accepts an authored, structured task file (document intake is R3).
Imported tasks always start in DRAFT; nothing runs until the owner approves.
Re-importing is idempotent: root IDs are derived from (project, ticket) and an
existing root is never overwritten (root IDs are immutable).
"""

from __future__ import annotations

import json
import re
import tomllib
from pathlib import Path
from typing import Any

from .models import AcceptanceCase, DependencyRef, EvidenceClass, ResourceProfile, RootTask, TaskState, TaskType
from .store import Store
from .util import usd_to_micros


class TaskFileError(Exception):
    pass


def load_task_file(path: str | Path) -> dict[str, Any]:
    path = Path(path)
    if path.suffix == ".json":
        return json.loads(path.read_text())
    with open(path, "rb") as fh:
        return tomllib.load(fh)


def root_id_for(project_id: str, ticket: str) -> str:
    return f"{project_id}-t{re.sub(r'[^A-Za-z0-9]+', '-', str(ticket))}"


def _cases(t: dict) -> list[AcceptanceCase]:
    out = []
    raw = t.get("acceptance_cases")
    if raw:
        for i, c in enumerate(raw, 1):
            if isinstance(c, str):
                out.append(AcceptanceCase(id=f"AC{i}", description=c))
            else:
                out.append(AcceptanceCase(id=c.get("id", f"AC{i}"), description=c["description"],
                                          evidence_class=EvidenceClass(c.get("evidence_class", "rules"))))
    elif t.get("acceptance"):
        out.append(AcceptanceCase(id="AC1", description=t["acceptance"],
                                  evidence_class=EvidenceClass(t.get("acceptance_evidence_class", "rules"))))
    return out


def import_tasks(store: Store, project_id: str, data: dict[str, Any], *, default_root_ceiling_micros: int | None = None
                 ) -> tuple[list[RootTask], list[str]]:
    defaults = data.get("defaults") or {}
    tasks = data.get("tasks") or []
    if not isinstance(tasks, list):
        raise TaskFileError("`tasks` must be an array of tables")
    created: list[RootTask] = []
    skipped: list[str] = []
    tickets = {str(t.get("ticket")) for t in tasks}
    for t in tasks:
        merged = {**defaults, **t}
        if "ticket" not in merged or "title" not in merged:
            raise TaskFileError(f"every task needs ticket and title: {t!r}")
        ticket = str(merged["ticket"])
        rid = merged.get("id") or root_id_for(project_id, ticket)
        try:
            store.get_root(rid)
            skipped.append(rid)
            continue
        except Exception:
            pass
        deps = []
        for d in merged.get("depends_on", []):
            d = str(d)
            if d not in tickets and store.find_root_by_ticket(project_id, d) is None:
                raise TaskFileError(f"task {ticket} depends on unknown ticket {d}")
            deps.append(DependencyRef(root_id=root_id_for(project_id, d)))
        ceiling = merged.get("reservation_ceiling_usd")
        root = RootTask(
            id=rid, project_id=project_id, milestone_id=merged.get("milestone", "pilot"), ticket=ticket,
            title=merged["title"], group=merged.get("group"), task_type=TaskType(merged.get("task_type", "code")),
            spec_version=str(merged.get("spec_version", "1")), state=TaskState.DRAFT,
            description=merged.get("description", ""), deliverable=merged.get("deliverable", merged["title"]),
            dependencies=deps, permitted_paths=list(merged.get("permitted_paths", [])),
            input_contract=merged.get("input_contract", ""), output_contract=merged.get("output_contract", ""),
            acceptance_cases=_cases(merged), visual_review_required=bool(merged.get("visual_review_required", False)),
            resource_profile=ResourceProfile(**(merged.get("resource_profile") or {})),
            permitted_routes=list(merged.get("permitted_routes", [])),
            verification_checks=list(merged.get("verification_checks", [])),
            integration_checks=list(merged.get("integration_checks", [])),
            max_attempts=int(merged.get("max_attempts", 3)),
            active_work_timeout_s=int(merged.get("active_work_timeout_s", 3600)),
            reservation_ceiling_micros=usd_to_micros(ceiling) if ceiling is not None else default_root_ceiling_micros,
        )
        created.append(store.create_root(root))
    return created, skipped
