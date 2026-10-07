"""Deterministic provider for tests and dry runs. Never contacts a network."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable, Optional

from ..pathglob import is_safe_relative, match_path
from .base import (
    CodingRequest,
    ProviderAdapter,
    ProviderDescriptor,
    ProviderResult,
    ReconcileResult,
    ReviewRequest,
    JudgeRequest,
    JudgeResult,
    ReviewResult,
    TransportError,
    Usage,
)


class SimulatedCrash(BaseException):
    """Raised to simulate the Forge process dying mid-operation (not an Exception subclass)."""


@dataclass
class FakeBehaviour:
    files: dict[str, str] = field(default_factory=dict)  # path (repo-relative) -> content
    delete: list[str] = field(default_factory=list)
    cost_micros: int = 10_000
    transport_failures: int = 0  # raise TransportError this many times before succeeding
    charge_on_transport_failure: int = 0
    crash_after_submit: bool = False  # provider receives job, then Forge "dies"
    status: str = "completed"
    summary: str = "fake candidate"
    ignore_permitted_paths: bool = False  # simulate a misbehaving builder


class FakeProvider(ProviderAdapter):
    def __init__(self, script: dict[int, FakeBehaviour] | Callable[[CodingRequest], FakeBehaviour] | None = None,
                 *, ceiling_micros: Optional[int] = 50_000, review_verdict: str = "approve",
                 judge_pick: "str | Callable[[JudgeRequest], str]" = "first", judge_cost_micros: int = 3_000,
                 name: str = "fake", vendor: str = "fake"):
        self.script = script or {}
        self.ceiling = ceiling_micros
        self.review_verdict = review_verdict
        self.judge_pick = judge_pick
        self.judge_cost_micros = judge_cost_micros
        self.judgements: list[JudgeRequest] = []
        self.descriptor = ProviderDescriptor(
            name=name, vendor=vendor, operations=["code", "review", "visual_judge"], auth_method="none",
            billing_party="nobody (deterministic test double)", data_destinations=["local process memory"],
            data_classes_sent=["prompt", "code"], cancellation="confirmed", structured_output=True,
            tested_versions=["fake-1"],
        )
        self.jobs: dict[str, ProviderResult] = {}  # provider-side job state, keyed by idempotency key
        self.submissions: list[str] = []
        self._failures_seen: dict[str, int] = {}
        self.cancelled: list[str] = []
        self.reviews: list[ReviewRequest] = []

    def behaviour(self, req: CodingRequest) -> FakeBehaviour:
        if callable(self.script):
            return self.script(req)
        return self.script.get(req.attempt.number, self.script.get(0, FakeBehaviour()))

    def estimate_ceiling_micros(self, req: CodingRequest) -> Optional[int]:
        return self.ceiling

    def submit(self, req: CodingRequest) -> ProviderResult:
        key = req.idempotency_key
        if key in self.jobs:  # idempotent: re-submission returns the existing job
            return self.jobs[key]
        b = self.behaviour(req)
        seen = self._failures_seen.get(key, 0)
        if seen < b.transport_failures:
            self._failures_seen[key] = seen + 1
            raise TransportError(f"simulated transport failure {seen + 1}",
                                 cost_micros=b.charge_on_transport_failure)
        self.submissions.append(key)
        written: list[str] = []
        for rel, content in b.files.items():
            if not is_safe_relative(rel):
                continue
            if not b.ignore_permitted_paths and not match_path(rel, req.root.permitted_paths):
                continue
            p = req.workspace / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(content)
            written.append(rel)
        for rel in b.delete:
            p = req.workspace / rel
            if p.exists():
                p.unlink()
                written.append(rel)
        result = ProviderResult(
            status=b.status, provider_job_id=f"fakejob-{len(self.jobs) + 1}", summary=b.summary,
            usage=Usage(model="fake-model", input_tokens=1000, output_tokens=200, requests=1),
            cost_micros=b.cost_micros, files_written=written, transcript=f"fake transcript for {key}",
        )
        self.jobs[key] = result
        if b.crash_after_submit:
            b.crash_after_submit = False  # only crash once
            raise SimulatedCrash(f"forge crashed after provider accepted {key}")
        return result

    def reconcile(self, idempotency_key: str) -> ReconcileResult:
        if idempotency_key in self.jobs:
            return ReconcileResult("completed", self.jobs[idempotency_key])
        return ReconcileResult("not_found")

    def cancel(self, provider_job_id: str) -> str:
        self.cancelled.append(provider_job_id)
        return "confirmed"

    def review_ceiling_micros(self, req: ReviewRequest) -> Optional[int]:
        return self.ceiling

    def review(self, req: ReviewRequest) -> ReviewResult:
        self.reviews.append(req)
        findings = [] if self.review_verdict == "approve" else [
            {"acceptance_case": c.id, "evidence": "diff", "comment": "fake reviewer objection"}
            for c in req.acceptance_cases[:1]
        ]
        return ReviewResult(self.review_verdict, findings, Usage(model="fake-model", input_tokens=500,
                                                                 output_tokens=50, requests=1), 2_000)

    def judge_ceiling_micros(self, candidates: int, views: int = 4) -> Optional[int]:
        return self.ceiling

    def judge(self, req: JudgeRequest) -> JudgeResult:
        """Deterministic judge: ``judge_pick`` is "first", "NONE", a uid, or a callable."""
        self.judgements.append(req)
        pick = self.judge_pick(req) if callable(self.judge_pick) else self.judge_pick
        if pick == "first":
            pick = req.candidates[0].uid if req.candidates else "NONE"
        return JudgeResult(pick, f"fake judge picked {pick}",
                           Usage(model="fake-model", input_tokens=800, output_tokens=40, requests=1),
                           self.judge_cost_micros, raw=f'{{"pick": "{pick}"}}')


class FakeJudge(FakeProvider):
    """A FakeProvider used only as a visual judge in tests and offline dry runs."""

    def __init__(self, pick: "str | Callable[[JudgeRequest], str]" = "first", *, cost_micros: int = 3_000,
                 ceiling_micros: Optional[int] = 20_000, name: str = "fake-judge", vendor: str = "fake"):
        super().__init__(ceiling_micros=ceiling_micros, judge_pick=pick, judge_cost_micros=cost_micros,
                         name=name, vendor=vendor)
