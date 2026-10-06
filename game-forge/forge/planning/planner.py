"""Dependency planner: approved specification + template -> milestone backlog.

Plan step 4 ("Approve a milestone"): the owner reviews tasks, dependencies, an estimate *range*
and the required human checkpoints, and approves a backlog of root tasks, each with a bounded
output and completion condition. This module proposes that backlog; nothing here approves it.

* Every plannable requirement is routed to exactly one template module (section heading words
  weigh most, then requirement words, then the requirement kind). Routing is deterministic and
  shown in the backlog so the owner can disagree.
* Core template modules always get a task. Optional lanes (UI, audio, localization, balance)
  get one only when a requirement routes to them.
* A task's acceptance cases are the frozen specification cases of its requirements; a core
  module with no requirement gets a template case ``TPL-<module>`` (its skeleton checks).
* Estimates are ranges (module base + per requirement). The reservation ceiling of each task is
  the *high* cost estimate, so dispatch can never silently spend beyond the plan.
* The task file it exports is the existing structured format (``forge import``); every task
  starts as DRAFT.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from ..intake.spec import KIND_EVIDENCE, Requirement, SpecAcceptanceCase, Specification, SpecStatus
from .template import Module, Template


class PlanningError(Exception):
    pass


@dataclass
class PlannedTask:
    ticket: str
    module: str
    title: str
    milestone: str
    depends_on: list[str]
    requirement_ids: list[str]
    acceptance_cases: list[dict]
    checks: list[str]
    permitted_paths: list[str]
    visual_review: bool
    needs_unity: bool
    needs_device: bool
    estimate_hours: tuple[float, float]
    estimate_cost_usd: tuple[float, float]
    checkpoints: list[str]
    evidence: list[str]

    def to_task(self, spec_version: int) -> dict:
        reqs = ", ".join(self.requirement_ids) or "template skeleton only"
        return {
            "ticket": self.ticket, "title": self.title, "milestone": self.milestone, "depends_on": self.depends_on,
            "spec_version": str(spec_version), "task_type": "code",
            "description": (f"Template module `{self.module}`. Requirements: {reqs}. Estimate "
                            f"{self.estimate_hours[0]:g}-{self.estimate_hours[1]:g} h, "
                            f"${self.estimate_cost_usd[0]:.2f}-${self.estimate_cost_usd[1]:.2f}. "
                            f"Human checkpoints: {', '.join(self.checkpoints) or 'none'}."),
            "deliverable": self.title, "permitted_paths": self.permitted_paths,
            "acceptance_cases": self.acceptance_cases, "visual_review_required": self.visual_review,
            "resource_profile": {"needs_unity": self.needs_unity, "needs_device": self.needs_device},
            "verification_checks": self.checks, "reservation_ceiling_usd": round(self.estimate_cost_usd[1], 2),
        }


@dataclass
class MilestonePlan:
    id: str
    name: str
    tickets: list[str]
    estimate_hours: tuple[float, float]
    estimate_cost_usd: tuple[float, float]
    checkpoints: list[str]


@dataclass
class Backlog:
    project_id: str
    template_id: str
    template_version: str
    spec_version: int
    spec_digest: str
    preview: bool
    tasks: list[PlannedTask]
    milestones: list[MilestonePlan]
    routing: dict[str, str] = field(default_factory=dict)  # requirement id -> module id
    checkpoint_text: dict[str, str] = field(default_factory=dict)

    def task(self, ticket: str) -> PlannedTask:
        for t in self.tasks:
            if t.ticket == ticket:
                return t
        raise KeyError(ticket)

    def to_task_file(self) -> dict:
        if self.preview:
            raise PlanningError("this backlog is a preview of an unapproved specification; approve the "
                                "specification before exporting tasks")
        return {"source": {"spec_version": self.spec_version, "spec_digest": self.spec_digest,
                           "template": f"{self.template_id}@{self.template_version}"},
                "tasks": [t.to_task(self.spec_version) for t in self.tasks]}

    def totals(self) -> tuple[tuple[float, float], tuple[float, float]]:
        return (sum(m.estimate_hours[0] for m in self.milestones), sum(m.estimate_hours[1] for m in self.milestones)), \
            (sum(m.estimate_cost_usd[0] for m in self.milestones), sum(m.estimate_cost_usd[1] for m in self.milestones))

    def markdown(self) -> str:
        (hl, hh), (cl, ch) = self.totals()
        head = "PREVIEW (specification not approved) - " if self.preview else ""
        out = [f"# {head}Backlog for {self.project_id} (spec v{self.spec_version}, {self.template_id}@{self.template_version})",
               "", f"Estimate range: {hl:g}-{hh:g} agent hours, ${cl:.2f}-${ch:.2f} model spend "
               "(ranges, not commitments; the owner's budget caps still apply).", ""]
        for m in self.milestones:
            out += [f"## {m.id}: {m.name}", "",
                    f"Estimate {m.estimate_hours[0]:g}-{m.estimate_hours[1]:g} h, "
                    f"${m.estimate_cost_usd[0]:.2f}-${m.estimate_cost_usd[1]:.2f}. "
                    f"Human checkpoints: {', '.join(m.checkpoints)}.", "",
                    "| Task | Depends on | Requirements | Cases | Checks | Estimate | Checkpoints |",
                    "|---|---|---|---|---|---|---|"]
            for tk in m.tickets:
                t = self.task(tk)
                out.append(f"| {t.ticket} {t.title} | {', '.join(t.depends_on) or '-'} | "
                           f"{', '.join(t.requirement_ids) or '-'} | {len(t.acceptance_cases)} | {', '.join(t.checks)} | "
                           f"{t.estimate_hours[0]:g}-{t.estimate_hours[1]:g} h | {', '.join(t.checkpoints) or '-'} |")
            out.append("")
        return "\n".join(out)


def keyword_pattern(word: str) -> str:
    """Whole word (plus plural/-ed/-ing forms); a trailing ``*`` means any word with that prefix."""
    if word.endswith("*"):
        return r"(?<![a-z])" + re.escape(word[:-1])
    return r"(?<![a-z])" + re.escape(word) + r"(?:s|es|ed|d|ing)?(?![a-z])"


def _hits(words: tuple[str, ...], text: str) -> int:
    t = text.lower()
    return sum(1 for w in words if re.search(keyword_pattern(w), t))


def route_requirement(req: Requirement, template: Template) -> str:
    """Pick the module for one requirement.

    Score = 3 x keyword hits in the section heading + 2 x keyword hits in the text + 1 if a keyword
    is the sentence's subject (within its first three words) + 2 if the module takes the
    requirement's kind. Ties prefer the kind's module, then manifest order.
    """
    best, best_key = None, None
    subject = " ".join(req.effective_text.split()[:3])
    for order, m in enumerate(template.modules):
        kind_match = req.kind in m.kinds
        score = 3 * _hits(m.keywords, req.section) + 2 * _hits(m.keywords, req.effective_text) + \
            (1 if _hits(m.keywords, subject) else 0) + (2 if kind_match else 0)
        key = (score, kind_match, -order)
        if best_key is None or key > best_key:
            best, best_key = m, key
    if best is None or best_key[0] <= 0:
        fallback = next((m for m in template.modules if req.kind in m.kinds), None) or template.modules[0]
        return fallback.id
    return best.id


def _checkpoints(m: Module) -> list[str]:
    cps = []
    if m.visual_review:
        cps.append("visual_approval")
    if "device" in m.evidence:
        cps.append("device_play")
    if m.id == "release" or "release" in m.kinds:
        cps += ["release_approval", "store_submission"]
    return cps


def plan_backlog(spec: Specification, template: Template, cases: list[SpecAcceptanceCase] | None = None, *,
                 allow_preview: bool = False) -> Backlog:
    if spec.template_id != template.id:
        raise PlanningError(f"specification is for template {spec.template_id}, not {template.id}")
    preview = spec.status != SpecStatus.APPROVED
    if preview and not allow_preview:
        raise PlanningError(f"specification v{spec.version} is {spec.status.value}; approve it before planning "
                            "(or request a preview)")
    cases_by_req: dict[str, list[SpecAcceptanceCase]] = {}
    for c in cases or []:
        cases_by_req.setdefault(c.requirement_id, []).append(c)

    routing = {r.id: route_requirement(r, template) for r in spec.plannable()}
    reqs_by_module: dict[str, list[Requirement]] = {}
    for r in spec.plannable():
        reqs_by_module.setdefault(routing[r.id], []).append(r)
    core = set(template.info.get("core_modules", [m.id for m in template.modules]))
    included = [m for m in template.module_order() if m.id in core or reqs_by_module.get(m.id)]
    included_ids = {m.id for m in included}

    def ticket(m: Module) -> str:
        return f"{m.milestone}-{m.id}"

    tasks: list[PlannedTask] = []
    for m in included:
        reqs = reqs_by_module.get(m.id, [])
        acs: list[dict] = []
        for r in reqs:
            rc = cases_by_req.get(r.id)
            if rc:
                acs += [{"id": c.id, "description": f"[{r.id}] {c.description}", "evidence_class": c.evidence_class}
                        for c in rc]
            else:  # preview: cases are frozen only at approval
                acs.append({"id": f"AC-{r.id}", "description": f"[{r.id}] {r.effective_text}",
                            "evidence_class": KIND_EVIDENCE.get(r.kind, "rules")})
        if not acs:
            acs.append({"id": f"TPL-{m.id}", "description": f"Template module '{m.name}' skeleton builds and its "
                        f"template checks ({', '.join(m.checks) or 'none'}) pass", "evidence_class": m.evidence[0]})
        n = len(reqs)
        hours = (m.estimate_hours[0] + n * m.per_requirement_hours[0], m.estimate_hours[1] + n * m.per_requirement_hours[1])
        cost = (m.cost_usd[0] + n * m.per_requirement_cost_usd[0], m.cost_usd[1] + n * m.per_requirement_cost_usd[1])
        tasks.append(PlannedTask(
            ticket=ticket(m), module=m.id, title=m.name, milestone=m.milestone,
            depends_on=[ticket(template.module(d)) for d in m.depends_on if d in included_ids],
            requirement_ids=[r.id for r in reqs], acceptance_cases=acs, checks=list(m.checks),
            permitted_paths=list(m.paths), visual_review=m.visual_review, needs_unity=m.needs_unity,
            needs_device="device" in m.evidence, estimate_hours=(round(hours[0], 2), round(hours[1], 2)),
            estimate_cost_usd=(round(cost[0], 2), round(cost[1], 2)), checkpoints=_checkpoints(m),
            evidence=list(m.evidence)))

    milestones = []
    for mid, mdef in template.milestones.items():
        ts = [t for t in tasks if t.milestone == mid]
        if not ts:
            continue
        milestones.append(MilestonePlan(
            id=mid, name=mdef.get("name", mid), tickets=[t.ticket for t in ts],
            estimate_hours=(round(sum(t.estimate_hours[0] for t in ts), 2), round(sum(t.estimate_hours[1] for t in ts), 2)),
            estimate_cost_usd=(round(sum(t.estimate_cost_usd[0] for t in ts), 2),
                               round(sum(t.estimate_cost_usd[1] for t in ts), 2)),
            checkpoints=list(mdef.get("checkpoints", ["milestone_approval"]))))
    return Backlog(project_id=spec.project_id, template_id=template.id, template_version=template.version,
                   spec_version=spec.version, spec_digest=spec.digest, preview=preview, tasks=tasks,
                   milestones=milestones, routing=routing, checkpoint_text=dict(template.checkpoints))


def change_impact(backlog: Backlog, changed_requirements: list[str]) -> list[str]:
    """Tickets whose requirements changed: each needs an explicitly linked *new* root (never a silent edit)."""
    changed = set(changed_requirements)
    return [t.ticket for t in backlog.tasks if changed & set(t.requirement_ids)]


def critical_path(backlog: Backlog, *, use_high: bool = True) -> tuple[float, list[str]]:
    idx = 1 if use_high else 0
    memo: dict[str, tuple[float, list[str]]] = {}

    def longest(tk: str) -> tuple[float, list[str]]:
        if tk in memo:
            return memo[tk]
        t = backlog.task(tk)
        best: tuple[float, list[str]] = (0.0, [])
        for d in t.depends_on:
            cand = longest(d)
            if cand[0] > best[0]:
                best = cand
        memo[tk] = (best[0] + t.estimate_hours[idx], best[1] + [tk])
        return memo[tk]

    result: tuple[float, list[str]] = (0.0, [])
    for t in backlog.tasks:
        cand = longest(t.ticket)
        if cand[0] > result[0]:
            result = cand
    return round(result[0], 2), result[1]
