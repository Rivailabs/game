"""Requirement -> task -> test traceability matrix.

R3 exit gate: "requirements trace to checks". Every plannable requirement must reach at least one
task, at least one frozen acceptance case and at least one protected check. The matrix lists the
gaps instead of hiding them. When a Forge store is supplied, each row also shows the live state of
the root task(s) that carry it, so "traced" and "verified" are never confused: a requirement is
*verified* only when every root carrying it is ACCEPTED.
"""

from __future__ import annotations

import csv
import io
from dataclasses import dataclass, field
from typing import Optional

from ..intake.spec import SpecAcceptanceCase, Specification
from .planner import Backlog


@dataclass
class TraceRow:
    requirement_id: str
    kind: str
    status: str
    text: str
    tickets: list[str]
    cases: list[str]
    checks: list[str]
    root_states: dict[str, str] = field(default_factory=dict)

    @property
    def traced(self) -> bool:
        return bool(self.tickets and self.cases and self.checks)

    @property
    def verified(self) -> bool:
        return bool(self.root_states) and all(s == "ACCEPTED" for s in self.root_states.values())


@dataclass
class TraceabilityMatrix:
    project_id: str
    spec_version: int
    rows: list[TraceRow]
    requirements_without_task: list[str]
    requirements_without_case: list[str]
    requirements_without_check: list[str]
    template_only_tasks: list[str]  # tasks carrying only template skeleton cases
    orphan_cases: list[str]  # task cases that trace to no current requirement or template module
    superseded_cases_in_use: list[str]

    @property
    def complete(self) -> bool:
        return not (self.requirements_without_task or self.requirements_without_case
                    or self.requirements_without_check or self.orphan_cases or self.superseded_cases_in_use)

    def summary(self) -> dict:
        return {"requirements": len(self.rows), "traced": sum(r.traced for r in self.rows),
                "verified": sum(r.verified for r in self.rows), "complete": self.complete,
                "gaps": {"without_task": self.requirements_without_task, "without_case": self.requirements_without_case,
                         "without_check": self.requirements_without_check, "orphan_cases": self.orphan_cases,
                         "superseded_cases_in_use": self.superseded_cases_in_use},
                "template_only_tasks": self.template_only_tasks}

    def to_csv(self) -> str:
        buf = io.StringIO()
        w = csv.writer(buf)
        w.writerow(["requirement", "kind", "status", "tickets", "cases", "checks", "root_states", "traced",
                    "verified", "text"])
        for r in self.rows:
            w.writerow([r.requirement_id, r.kind, r.status, ";".join(r.tickets), ";".join(r.cases),
                        ";".join(r.checks), ";".join(f"{k}={v}" for k, v in r.root_states.items()),
                        r.traced, r.verified, r.text])
        return buf.getvalue()

    def markdown(self) -> str:
        s = self.summary()
        out = [f"# Traceability: {self.project_id} spec v{self.spec_version}", "",
               f"{s['traced']}/{s['requirements']} requirements traced to task + case + check; "
               f"{s['verified']} verified by ACCEPTED roots. Complete: **{self.complete}**.", "",
               "| Req | Kind | Tasks | Cases | Checks | Root states | Text |", "|---|---|---|---|---|---|---|"]
        for r in self.rows:
            states = ", ".join(f"{k}:{v}" for k, v in r.root_states.items()) or "-"
            out.append(f"| {r.requirement_id} | {r.kind} | {', '.join(r.tickets) or '**none**'} | "
                       f"{', '.join(r.cases) or '**none**'} | {', '.join(r.checks) or '**none**'} | {states} | "
                       f"{r.text.replace('|', '/')} |")
        for title, items in (("Requirements without a task", self.requirements_without_task),
                             ("Requirements without an acceptance case", self.requirements_without_case),
                             ("Requirements without a check", self.requirements_without_check),
                             ("Orphan cases", self.orphan_cases),
                             ("Superseded cases still used by tasks", self.superseded_cases_in_use)):
            if items:
                out += ["", f"**{title}:** {', '.join(items)}"]
        return "\n".join(out) + "\n"


def build_matrix(spec: Specification, backlog: Backlog, cases: list[SpecAcceptanceCase] | None = None, *,
                 superseded: Optional[dict[str, int]] = None, store=None) -> TraceabilityMatrix:
    by_req: dict[str, list] = {}
    case_ids_in_tasks: dict[str, str] = {}
    for t in backlog.tasks:
        for rid in t.requirement_ids:
            by_req.setdefault(rid, []).append(t)
        for c in t.acceptance_cases:
            case_ids_in_tasks[c["id"]] = t.ticket
    known_cases = {c.id: c for c in cases or []}
    root_state: dict[str, str] = {}
    if store is not None:
        for t in backlog.tasks:
            root = store.find_root_by_ticket(spec.project_id, t.ticket)
            if root is not None:
                root_state[t.ticket] = root.state.value

    rows = []
    for r in spec.plannable():
        tasks = by_req.get(r.id, [])
        case_ids = sorted({c["id"] for t in tasks for c in t.acceptance_cases
                           if c["description"].startswith(f"[{r.id}]")})
        rows.append(TraceRow(
            requirement_id=r.id, kind=r.kind, status=r.status.value, text=r.effective_text,
            tickets=[t.ticket for t in tasks], cases=case_ids, checks=sorted({c for t in tasks for c in t.checks}),
            root_states={t.ticket: root_state[t.ticket] for t in tasks if t.ticket in root_state}))
    req_ids = {r.id for r in spec.plannable()}
    orphans = []
    for cid, tk in case_ids_in_tasks.items():
        if cid.startswith("TPL-"):
            continue
        c = known_cases.get(cid)
        rid = c.requirement_id if c else cid.removeprefix("AC-").split(".")[0]
        if rid not in req_ids:
            orphans.append(cid)
    sup = superseded or {}
    return TraceabilityMatrix(
        project_id=spec.project_id, spec_version=spec.version, rows=rows,
        requirements_without_task=[r.requirement_id for r in rows if not r.tickets],
        requirements_without_case=[r.requirement_id for r in rows if r.tickets and not r.cases],
        requirements_without_check=[r.requirement_id for r in rows if r.tickets and not r.checks],
        template_only_tasks=[t.ticket for t in backlog.tasks if not t.requirement_ids],
        orphan_cases=sorted(orphans), superseded_cases_in_use=sorted(c for c in case_ids_in_tasks if c in sup))
