"""Provider adapter interface.

Every adapter declares what it does and where data goes *before* first use:
supported operations, authentication method, direct billing party, data
destinations and data classes, quotas, timeouts, cancellation behaviour,
usage reporting, structured-output support and tested versions.

Provider prose is stored as evidence; it never drives a state transition.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Optional

from ..models import AcceptanceCase, CandidateAttempt, ProjectPolicy, RootTask


@dataclass
class ProviderDescriptor:
    name: str
    vendor: str
    operations: list[str]
    auth_method: str
    billing_party: str
    data_destinations: list[str]
    data_classes_sent: list[str]
    region: str = "global"
    quotas: str = "provider-side rate limits; local ledger caps apply"
    timeout_s: float = 600
    cancellation: str = "uncertain"  # "confirmed" | "uncertain" | "not_supported"
    usage_reporting: str = "per-request token usage"
    structured_output: bool = False
    tested_versions: list[str] = field(default_factory=list)
    unattended_allowed: bool = True
    supervised_only: bool = False
    notes: str = ""

    def to_dict(self) -> dict:
        return dict(self.__dict__)


@dataclass
class Usage:
    model: str = ""
    input_tokens: int = 0
    output_tokens: int = 0
    cache_read_input_tokens: int = 0
    cache_creation_input_tokens: int = 0
    requests: int = 0

    def add(self, other: "Usage") -> None:
        self.input_tokens += other.input_tokens
        self.output_tokens += other.output_tokens
        self.cache_read_input_tokens += other.cache_read_input_tokens
        self.cache_creation_input_tokens += other.cache_creation_input_tokens
        self.requests += other.requests
        self.model = other.model or self.model

    def to_dict(self) -> dict:
        return dict(self.__dict__)


@dataclass
class CodingRequest:
    root: RootTask
    attempt: CandidateAttempt
    workspace: Path
    project_workdir: str
    idempotency_key: str
    instructions: str
    repair_notes: str = ""
    protected_paths: list[str] = field(default_factory=list)


@dataclass
class ProviderResult:
    status: str  # completed | failed | refused | incomplete
    provider_job_id: Optional[str] = None
    summary: str = ""
    usage: Usage = field(default_factory=Usage)
    cost_micros: int = 0
    files_written: list[str] = field(default_factory=list)
    transcript: str = ""
    transport_retries: int = 0
    error: str = ""


@dataclass
class ReconcileResult:
    status: str  # completed | running | not_found | unknown
    result: Optional[ProviderResult] = None
    detail: str = ""


@dataclass
class ReviewRequest:
    root: RootTask
    candidate_hash: str
    diff: str
    acceptance_cases: list[AcceptanceCase]
    evidence_summary: str
    idempotency_key: str


@dataclass
class ReviewResult:
    verdict: str  # approve | reject | uncertain
    findings: list[dict[str, Any]] = field(default_factory=list)
    usage: Usage = field(default_factory=Usage)
    cost_micros: int = 0
    raw: str = ""


class TransportError(Exception):
    """Network/timeout/overload: the request may or may not have reached the provider."""

    def __init__(self, msg: str, usage: Usage | None = None, cost_micros: int = 0):
        super().__init__(msg)
        self.usage = usage or Usage()
        self.cost_micros = cost_micros


class SupervisedOnly(Exception):
    pass


class ProviderRejected(Exception):
    """The provider refused the request (auth, invalid request, permission). Not retried blindly.

    ``cost_micros`` carries charges already incurred by earlier requests of the same submission.
    """

    def __init__(self, msg: str, usage: Usage | None = None, cost_micros: int = 0):
        super().__init__(msg)
        self.usage = usage or Usage()
        self.cost_micros = cost_micros


class ProviderUnavailable(Exception):
    """The route cannot be used right now and nothing was sent (missing SDK, credentials, quota pause)."""


class PolicyViolation(Exception):
    pass


def check_policy(desc: ProviderDescriptor, policy: ProjectPolicy, *, unattended: bool) -> None:
    """Refuse routes the project policy does not permit (vendor, region, data classes)."""
    if desc.vendor not in policy.allowed_vendors:
        raise PolicyViolation(f"vendor {desc.vendor} is not in the project's allowed vendors")
    if desc.region not in policy.allowed_regions:
        raise PolicyViolation(f"region {desc.region} is not permitted by the project policy")
    extra = set(desc.data_classes_sent) - set(policy.allowed_data_classes)
    if extra:
        raise PolicyViolation(f"route would send data classes not permitted: {sorted(extra)}")
    if unattended and (desc.supervised_only or not desc.unattended_allowed):
        raise PolicyViolation(f"{desc.name} is a supervised route; not available in unattended mode")


class ProviderAdapter(ABC):
    descriptor: ProviderDescriptor

    @abstractmethod
    def estimate_ceiling_micros(self, req: CodingRequest) -> Optional[int]:
        """Upper bound of the billable charge for one submission (None = unknown)."""

    @abstractmethod
    def submit(self, req: CodingRequest) -> ProviderResult:
        ...

    @abstractmethod
    def reconcile(self, idempotency_key: str) -> ReconcileResult:
        ...

    def cancel(self, provider_job_id: str) -> str:
        return "uncertain"

    def review_ceiling_micros(self, req: ReviewRequest) -> Optional[int]:
        return None

    def review(self, req: ReviewRequest) -> ReviewResult:  # pragma: no cover - optional
        raise NotImplementedError(f"{self.descriptor.name} does not support review")
