"""Offering measurement report (plan: "Offering measurement and scope decisions").

"Measure accepted root outcomes, not raw merged ticket counts. Report completed roots divided by
approved roots attempted, including failures and manual fixes; cost per accepted root/build; human
minutes per root; time to first playable build; first-pass visual acceptance; escaped defects;
recovery success; and external projects retained. State sample size and scope. A 70% completion
target is a hypothesis for a defined workload, not proof of general autonomy."

Facts the store cannot know (human minutes per root, roots that needed a manual fix, defects found
after acceptance, retained external projects) are inputs; a metric without data says so instead
of defaulting to zero. Works on a local Forge store; it does not need the hosted service.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from typing import Optional

from ..budget import BudgetLedger
from ..models import Decision, EvidenceClass, EvidenceStatus, TaskState
from ..util import micros_to_usd

HYPOTHESIS = ("A completion target (e.g. 70%) is a hypothesis for this defined workload, not proof of general "
              "autonomy.")


@dataclass
class Metric:
    name: str
    value: Optional[float]
    numerator: Optional[float] = None
    denominator: Optional[float] = None
    unit: str = ""
    note: str = ""


@dataclass
class MetricsReport:
    project_id: str
    scope: str
    sample_size: int
    metrics: list[Metric] = field(default_factory=list)
    caveats: list[str] = field(default_factory=list)

    def get(self, name: str) -> Metric:
        return next(m for m in self.metrics if m.name == name)

    def to_dict(self) -> dict:
        return asdict(self)

    def markdown(self) -> str:
        out = [f"# Offering metrics: {self.project_id}", "", f"Scope: {self.scope}. Sample: {self.sample_size} "
               "attempted root(s).", "", "| Metric | Value | Basis | Note |", "|---|---|---|---|"]
        for m in self.metrics:
            if m.value is None:
                val = "no data"
            elif m.unit == "usd_micros":
                val = micros_to_usd(int(m.value))
            elif m.unit == "ratio":
                val = f"{m.value:.0%}"
            else:
                val = f"{m.value:g} {m.unit}".strip()
            basis = f"{m.numerator:g}/{m.denominator:g}" if m.numerator is not None and m.denominator else "-"
            out.append(f"| {m.name} | {val} | {basis} | {m.note} |")
        out += [""] + [f"- {c}" for c in self.caveats]
        return "\n".join(out) + "\n"


def _ratio(n: float, d: float) -> Optional[float]:
    return n / d if d else None


def measure(store, project_id: str, *, scope: str, human_minutes: dict[str, float] | None = None,
            manual_fix_roots: set[str] | None = None, escaped_defects: list[dict] | None = None,
            external_projects_retained: Optional[int] = None) -> MetricsReport:
    human_minutes = human_minutes or {}
    manual = manual_fix_roots or set()
    roots = store.list_roots(project_id)
    attempted = [r for r in roots if store.list_attempts(r.id)]
    accepted = [r for r in attempted if r.state == TaskState.ACCEPTED]
    clean = [r for r in accepted if r.id not in manual]
    ledger = BudgetLedger(store)
    cost = sum(ledger.root_cost(r.id) for r in attempted)  # failures and repairs included
    m: list[Metric] = []
    m.append(Metric("accepted_roots_per_attempted", _ratio(len(accepted), len(attempted)), len(accepted),
                    len(attempted), "ratio", "failures and manual fixes stay in the denominator"))
    m.append(Metric("accepted_without_manual_fix", _ratio(len(clean), len(attempted)), len(clean), len(attempted),
                    "ratio", f"{len(manual & {r.id for r in accepted})} accepted root(s) needed a manual fix"))
    m.append(Metric("cost_per_accepted_root", cost / len(accepted) if accepted else None, cost, len(accepted),
                    "usd_micros", "all settled spend on attempted roots (incl. failed attempts) / accepted roots"))
    with_minutes = [human_minutes[r.id] for r in attempted if r.id in human_minutes]
    m.append(Metric("human_minutes_per_root", sum(with_minutes) / len(with_minutes) if with_minutes else None,
                    sum(with_minutes) if with_minutes else None, len(with_minutes), "min",
                    f"recorded for {len(with_minutes)}/{len(attempted)} attempted roots"))
    project = store.get_project(project_id)
    first_build = None
    for r in roots:
        for e in store.list_evidence(r.id):
            if e.evidence_class == EvidenceClass.DEVICE and e.status == EvidenceStatus.PASS:
                first_build = e.created_at if first_build is None else min(first_build, e.created_at)
    m.append(Metric("time_to_first_playable_build", (first_build - project.created_at) / 3600 if first_build else None,
                    unit="h", note="first passing physical-device evidence after project creation"
                    if first_build else "no passing device evidence yet"))
    visual_total = visual_first = 0
    for r in roots:
        if not r.visual_review_required:
            continue
        decisions = []
        for a in store.list_approvals(r.id):
            if a.visual_approval.decision in (Decision.APPROVED, Decision.REJECTED):
                decisions.append((a.visual_approval.at or 0, store.get_attempt(a.attempt_id).number,
                                  a.visual_approval.decision))
        if decisions:
            visual_total += 1
            _, number, decision = sorted(decisions)[0]
            visual_first += int(number == 1 and decision == Decision.APPROVED)
    m.append(Metric("first_pass_visual_acceptance", _ratio(visual_first, visual_total), visual_first, visual_total,
                    "ratio", "first visual decision was an approval of attempt 1"))
    if escaped_defects is None:
        m.append(Metric("escaped_defects", None, note="not recorded"))
    else:
        m.append(Metric("escaped_defects", len(escaped_defects), len(escaped_defects), len(accepted), "",
                        "defects found after acceptance"))
    recovered = {e["root_id"] for e in store.events(type_="recovery_check", limit=100_000) if e.get("root_id")}
    ok = [rid for rid in recovered if store.get_root(rid).state not in (TaskState.FAILED, TaskState.CANCELLED)]
    m.append(Metric("recovery_success", _ratio(len(ok), len(recovered)), len(ok), len(recovered), "ratio",
                    "roots that went through a restart recovery check and did not end FAILED/CANCELLED"))
    m.append(Metric("external_projects_retained", external_projects_retained,
                    note="external customer projects still active" if external_projects_retained is not None
                    else "not recorded (needs real external projects)"))
    caveats = [HYPOTHESIS, "Small samples: treat every ratio as indicative only and report n with it."]
    return MetricsReport(project_id, scope, len(attempted), m, caveats)
