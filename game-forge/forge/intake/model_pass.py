"""Optional model pass over a brief, through the existing provider abstraction.

The rule-based checks are the baseline. A model reviewer can *add* findings (possible
contradictions, missing rules) that become owner questions; it can never answer a question,
add a requirement or change one. The pass uses the adapter's ``review`` operation, so it goes
through the same descriptor, policy and ceiling rules as every other provider call:

* the project policy must allow the vendor, region and the ``design_document`` data class
  (the brief is sent to the provider; the owner sees that destination before first use);
* a route with no billable ceiling is refused in unattended mode;
* the caller may pass a budget hook so the call is reserved and settled like any other.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional, Protocol

from ..models import AcceptanceCase, ProjectPolicy, RootTask, TaskType
from ..providers.base import PolicyViolation, ProviderAdapter, ProviderDescriptor, ReviewRequest, check_policy
from .analysis import qid
from .spec import Question, Specification

#: What an intake model pass sends to the provider.
INTAKE_DATA_CLASSES = ["prompt", "design_document"]

INSTRUCTIONS = (
    "You are reviewing a game brief that will become a versioned specification. Report ONLY problems: "
    "contradictions between statements, rules that are referenced but never defined, and statements too vague "
    "to test. Cite the requirement id in `acceptance_case` and quote the text in `evidence`. Do not propose "
    "rules, values or fixes; the owner decides. Return verdict 'approve' when you find nothing."
)


class ModelPassUnavailable(Exception):
    pass


class BudgetHook(Protocol):
    def reserve(self, amount_micros: int, purpose: str) -> str: ...

    def settle(self, reservation_id: str, actual_micros: int) -> None: ...


@dataclass
class ModelPassResult:
    questions: list[Question]
    provider: str
    verdict: str
    cost_micros: int
    raw: str


def check_intake_policy(desc: ProviderDescriptor, policy: ProjectPolicy, *, unattended: bool) -> None:
    if "review" not in desc.operations:
        raise PolicyViolation(f"{desc.name} does not offer the review operation")
    probe = ProviderDescriptor(**{**desc.to_dict(), "data_classes_sent": list(INTAKE_DATA_CLASSES)})
    check_policy(probe, policy, unattended=unattended)


def run_model_pass(spec: Specification, source_text: str, provider: ProviderAdapter, policy: ProjectPolicy, *,
                   unattended: bool = True, budget: Optional[BudgetHook] = None) -> ModelPassResult:
    check_intake_policy(provider.descriptor, policy, unattended=unattended)
    reqs = spec.plannable()
    root = RootTask(id=f"{spec.project_id}-intake-v{spec.version}", project_id=spec.project_id, milestone_id="intake",
                    title="Specification intake review", task_type=TaskType.DOCS, spec_version=str(spec.version),
                    description=INSTRUCTIONS)
    listing = "\n".join(f"{r.id} [{r.kind}] ({r.section}) {r.effective_text}" for r in reqs)
    rule_findings = "\n".join(f"- {q.id} {q.category}: {q.text}" for q in spec.questions) or "(none)"
    req = ReviewRequest(
        root=root, candidate_hash=spec.source_sha256,
        diff=f"--- brief ---\n{source_text}\n--- extracted requirements ---\n{listing}",
        acceptance_cases=[AcceptanceCase(id=r.id, description=r.effective_text) for r in reqs],
        evidence_summary=f"Rule-based findings already raised:\n{rule_findings}",
        idempotency_key=f"intake-{spec.project_id}-{spec.source_sha256[:16]}-v{spec.version}",
    )
    ceiling = provider.review_ceiling_micros(req)
    if ceiling is None and unattended:
        raise ModelPassUnavailable(f"{provider.descriptor.name} has no billable ceiling; unavailable unattended")
    reservation = budget.reserve(ceiling, "intake model pass") if (budget and ceiling) else None
    try:
        result = provider.review(req)
    except Exception:
        if reservation:
            budget.settle(reservation, 0)  # type: ignore[union-attr]
        raise
    if reservation:
        budget.settle(reservation, result.cost_micros)  # type: ignore[union-attr]
    known = {r.id for r in reqs}
    questions = []
    for i, f in enumerate(result.findings, 1):
        rid = str(f.get("acceptance_case", ""))
        comment = str(f.get("comment", "")).strip() or "unspecified concern"
        category = str(f.get("category", "model_finding"))
        questions.append(Question(
            id=qid("model_finding", f"{provider.descriptor.name}|{rid}|{comment}"), category="model_finding",
            text=f"Model reviewer ({provider.descriptor.name}) flagged {rid or 'the brief'}: {comment}",
            requirement_ids=[rid] if rid in known else [],
            blocking=category in ("contradiction", "missing_rule"), source=f"model:{provider.descriptor.name}",
            detail={"evidence": f.get("evidence", ""), "category": category},
        ))
    return ModelPassResult(questions, provider.descriptor.name, result.verdict, result.cost_micros, result.raw)
