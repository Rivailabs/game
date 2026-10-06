"""Domain models (pydantic). Schemas are UI- and storage-independent.

Every component shares the same identifiers: project ID, specification version,
root task ID, candidate (attempt) ID and artifact hashes.
"""

from __future__ import annotations

from enum import Enum
from typing import Any, Optional

from pydantic import BaseModel, ConfigDict, Field, field_validator


class TaskState(str, Enum):
    DRAFT = "DRAFT"
    NEEDS_INPUT = "NEEDS_INPUT"
    APPROVED = "APPROVED"
    BLOCKED = "BLOCKED"
    READY = "READY"
    RUNNING = "RUNNING"
    VERIFYING = "VERIFYING"
    AWAITING_APPROVAL = "AWAITING_APPROVAL"
    INTEGRATION_READY = "INTEGRATION_READY"
    INTEGRATING = "INTEGRATING"
    RETRY_PENDING = "RETRY_PENDING"
    PAUSED = "PAUSED"
    CANCEL_REQUESTED = "CANCEL_REQUESTED"
    ACCEPTED = "ACCEPTED"
    FAILED = "FAILED"
    CANCELLED = "CANCELLED"


TERMINAL_STATES = frozenset({TaskState.ACCEPTED, TaskState.FAILED, TaskState.CANCELLED})


class TaskType(str, Enum):
    CODE = "code"
    UNITY_SCENE = "unity_scene"
    BUILD = "build"
    DEVICE_TEST = "device_test"
    ASSET = "asset"  # catalogue lane first, generation lane only when the catalogue returns NONE
    MEASUREMENT = "measurement"
    DOCS = "docs"


class EvidenceClass(str, Enum):
    RULES = "rules"
    REPLAY = "replay"
    INTEGRATION = "integration"
    DEVICE = "device"
    PERFORMANCE = "performance"
    HUMAN = "human"
    # Supplementary classes (not acceptance authorities on their own):
    STATIC = "static"  # diff guard, permitted-path and policy checks
    MODEL_REVIEW = "model_review"  # independent reviewer model opinion
    ASSET = "asset"  # asset-lane records: catalogue pick, renders, licence/credit (owner approval needed)


class EvidenceStatus(str, Enum):
    PASS = "PASS"
    FAIL = "FAIL"
    INCOMPLETE = "INCOMPLETE"  # e.g. phone disconnected; never converted into a pass
    BLOCKED = "BLOCKED"  # required tool (Unity, adb) is absent
    INFO = "INFO"


class Decision(str, Enum):
    APPROVED = "APPROVED"
    REJECTED = "REJECTED"
    PENDING = "PENDING"
    NOT_REQUIRED = "NOT_REQUIRED"


class ReviewAction(str, Enum):
    APPROVE = "approve"
    REPAIR = "repair"
    HOLD = "hold"
    CANCEL = "cancel"
    RESUME = "resume"


class FailureCategory(str, Enum):
    RULE_INCORRECT = "rule_incorrect"
    TEST_FAILURE = "test_failure"
    COMPILE_ERROR = "compile_error"
    VISUAL_MISMATCH = "visual_mismatch"
    INTERACTION_PROBLEM = "interaction_problem"
    PERFORMANCE = "performance"
    SCOPE_VIOLATION = "scope_violation"
    GATE_TAMPERING = "gate_tampering"
    OTHER = "other"


# --------------------------------------------------------------------------- policy


class ProjectPolicy(BaseModel):
    """Where project data may go. Checked before any provider job is dispatched."""

    model_config = ConfigDict(extra="forbid")

    allowed_vendors: list[str] = Field(default_factory=list)
    allowed_regions: list[str] = Field(default_factory=lambda: ["global"])
    allowed_data_classes: list[str] = Field(
        default_factory=lambda: ["prompt", "code", "test_excerpt"]
    )
    hosted_review_allowed: bool = False
    retention_days_failed_candidates: int = 14
    retention_days_raw_diagnostics: int = 30
    intended_release_countries: list[str] = Field(default_factory=list)
    unattended_mode: bool = True


class BudgetCaps(BaseModel):
    """Monetary caps in micro-USD. ``None`` means no cap configured -> dispatch refused."""

    project_cap_micros: Optional[int] = None
    milestone_caps_micros: dict[str, int] = Field(default_factory=dict)


# --------------------------------------------------------------------------- core


class Project(BaseModel):
    id: str
    name: str
    repo_path: str
    workdir: str = "."  # project sub-directory inside the repo
    accepted_branch: str = "forge/accepted"
    spec_version: str = "1"
    policy: ProjectPolicy = Field(default_factory=ProjectPolicy)
    protected_paths: list[str] = Field(default_factory=list)
    toolchain_profile: str = "linux-x86_64-ubuntu-lts"
    created_at: float = 0.0


class Milestone(BaseModel):
    id: str
    project_id: str
    name: str
    description: str = ""
    cap_micros: Optional[int] = None
    founder_hours_allowance: Optional[float] = None
    review_date: Optional[str] = None
    minimum_deliverable: str = ""


class DependencyRef(BaseModel):
    root_id: str
    #: Accepted artifact hash this task was planned against. Filled ("pinned")
    #: when the dependency is first seen ACCEPTED. A different current hash
    #: marks this consumer stale.
    artifact_hash: Optional[str] = None


class AcceptanceCase(BaseModel):
    id: str
    description: str
    evidence_class: EvidenceClass = EvidenceClass.RULES


class ResourceProfile(BaseModel):
    build_workspace: bool = True
    needs_unity: bool = False
    needs_device: bool = False
    gpu_vram_gb: Optional[float] = None
    device_serial: Optional[str] = None


class RootTask(BaseModel):
    """One owner-approved outcome. Children (attempts) never create spend authority."""

    id: str  # immutable root ID
    project_id: str
    milestone_id: str
    ticket: Optional[str] = None  # register ID (e.g. "7")
    title: str
    group: Optional[str] = None
    task_type: TaskType = TaskType.CODE
    spec_version: str = "1"
    state: TaskState = TaskState.DRAFT
    state_reason: str = ""
    description: str = ""
    deliverable: str = ""
    dependencies: list[DependencyRef] = Field(default_factory=list)
    permitted_paths: list[str] = Field(default_factory=list)
    permitted_tools: list[str] = Field(default_factory=lambda: ["read_file", "write_file", "list_files"])
    input_contract: str = ""
    output_contract: str = ""
    acceptance_cases: list[AcceptanceCase] = Field(default_factory=list)
    visual_review_required: bool = False
    resource_profile: ResourceProfile = Field(default_factory=ResourceProfile)
    permitted_routes: list[str] = Field(default_factory=list)
    verification_checks: list[str] = Field(default_factory=list)
    integration_checks: list[str] = Field(default_factory=list)
    max_attempts: int = 3  # initial candidate + at most two repairs
    active_work_timeout_s: int = 3600
    reservation_ceiling_micros: Optional[int] = None
    #: Asset tasks: catalogue brief JSON, relative to the repository root (or absolute).
    asset_brief: Optional[str] = None
    evidence_refs: list[str] = Field(default_factory=list)
    previous_root_id: Optional[str] = None  # set for spec-change revisions
    previous_cost_micros: int = 0
    accepted_artifact_hash: Optional[str] = None
    held: bool = False
    created_at: float = 0.0
    updated_at: float = 0.0

    @field_validator("max_attempts")
    @classmethod
    def _attempts_positive(cls, v: int) -> int:
        if v < 1:
            raise ValueError("max_attempts must be >= 1")
        return v


class AttemptStatus(str, Enum):
    ACTIVE = "ACTIVE"
    CANDIDATE = "CANDIDATE"  # produced a candidate hash
    VERIFIED = "VERIFIED"
    REJECTED = "REJECTED"
    FAILED = "FAILED"
    INTEGRATED = "INTEGRATED"
    ABANDONED = "ABANDONED"


class CandidateAttempt(BaseModel):
    id: str
    root_id: str
    number: int  # 1-based quality attempt number
    status: AttemptStatus = AttemptStatus.ACTIVE
    base_commit: Optional[str] = None
    candidate_hash: Optional[str] = None  # candidate commit sha
    integrated_hash: Optional[str] = None
    workspace_path: Optional[str] = None
    branch: Optional[str] = None
    provider: Optional[str] = None
    repair_instructions: str = ""
    failure_category: Optional[FailureCategory] = None
    lease_holder: Optional[str] = None
    lease_expires_at: Optional[float] = None
    heartbeat_at: Optional[float] = None
    reservation_id: Optional[str] = None
    transport_retries: int = 0
    gate_change_flags: list[str] = Field(default_factory=list)
    started_at: float = 0.0
    finished_at: Optional[float] = None


class Evidence(BaseModel):
    id: str
    root_id: str
    attempt_id: Optional[str] = None
    candidate_hash: Optional[str] = None
    evidence_class: EvidenceClass
    status: EvidenceStatus
    name: str
    summary: str = ""
    details: dict[str, Any] = Field(default_factory=dict)
    artifact_refs: list[str] = Field(default_factory=list)  # sha256 in artifact store
    created_at: float = 0.0


class GateRecord(BaseModel):
    decision: Decision = Decision.PENDING
    by: Optional[str] = None
    at: Optional[float] = None
    reason: str = ""
    evidence_refs: list[str] = Field(default_factory=list)


class ApprovalRecord(BaseModel):
    """Approvals bound to one exact candidate hash.

    Technical pass, visual approval, integrated acceptance and release approval
    are distinct fields: one never implies another, and a changed hash gets a
    fresh record (prior approvals never cover changed bytes).
    """

    id: str
    root_id: str
    attempt_id: str
    candidate_hash: str
    technical_pass: GateRecord = Field(default_factory=GateRecord)
    visual_approval: GateRecord = Field(default_factory=GateRecord)
    integrated_acceptance: GateRecord = Field(default_factory=GateRecord)
    release_approval: GateRecord = Field(default_factory=GateRecord)
    gate_change_review: GateRecord = Field(
        default_factory=lambda: GateRecord(decision=Decision.NOT_REQUIRED)
    )
    created_at: float = 0.0


class ReviewDecision(BaseModel):
    """An owner action from the review page (append-only)."""

    id: str
    root_id: str
    attempt_id: Optional[str]
    candidate_hash: Optional[str]
    action: ReviewAction
    reviewer: str
    correction: str = ""
    failure_category: Optional[FailureCategory] = None
    at: float = 0.0


class ToolStatus(BaseModel):
    name: str
    found: bool
    path: Optional[str] = None
    version: Optional[str] = None
    verified: bool = False
    note: str = ""


class ToolchainManifest(BaseModel):
    generated_at: float
    profile: str
    os: dict[str, Any] = Field(default_factory=dict)
    cpu: dict[str, Any] = Field(default_factory=dict)
    ram_gb: Optional[float] = None
    disk_free_gb: Optional[float] = None
    gpus: list[dict[str, Any]] = Field(default_factory=list)
    tools: dict[str, ToolStatus] = Field(default_factory=dict)
    devices: list[dict[str, Any]] = Field(default_factory=list)
    unity_editor_path: Optional[str] = None
    not_verified: list[str] = Field(default_factory=list)
    compatible: bool = False
    compatibility_notes: list[str] = Field(default_factory=list)


class WorkerCapability(BaseModel):
    worker_id: str
    host: str
    os: str = ""
    gpu_model: Optional[str] = None
    vram_gb: Optional[float] = None
    compute_capability: Optional[str] = None
    ram_gb: Optional[float] = None
    disk_free_gb: Optional[float] = None
    driver_versions: dict[str, str] = Field(default_factory=dict)
    route_benchmarks: dict[str, str] = Field(default_factory=dict)
    roles: list[str] = Field(default_factory=lambda: ["coding", "integration"])

    def satisfies(self, profile: ResourceProfile) -> bool:
        if profile.gpu_vram_gb is not None:
            if self.vram_gb is None or self.vram_gb < profile.gpu_vram_gb:
                return False
        return True
