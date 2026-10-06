"""Generation adapter interface and the job runner shared by every route.

An adapter turns one :class:`GenerationJob` into a provider job: ``submit`` -> ``poll`` -> ``fetch``.
The runner (:func:`run_job`) owns durability: the job row (idempotency key) is written *before* the
external call, the provider job id right after it, and after a crash or timeout the provider is
reconciled before anything is resubmitted - polling an existing job never generates a fresh asset.
Costs reported by the provider are charged to the reservation; an uncertain outcome keeps the
reservation "provider completion/charge pending".
"""

from __future__ import annotations

import hashlib
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

from ..routes import RouteDescriptor


class AdapterError(Exception):
    """The provider rejected the job or returned something unusable (charges may apply)."""


class AdapterUnavailable(Exception):
    """The route cannot run here (not configured, no credential, tool missing). Nothing was sent."""


class ManualStepPending(Exception):
    """A manual step is waiting for the owner (Mixamo, stock purchase)."""


@dataclass
class GenerationJob:
    idempotency_key: str
    asset_id: str
    route_id: str
    stage: str  # route stage profile (e.g. "shape+texture") or lane stage name
    operation: str  # text_to_3d | image_to_3d | rig | animate | retarget | text_to_motion | video_to_motion |
    #                 concept_image | library_import
    inputs: dict[str, Any] = field(default_factory=dict)  # prompt, image (Path), model (Path), clip, video ...
    params: dict[str, Any] = field(default_factory=dict)  # target_polycount, seed, height_m, ...
    out_dir: Path = Path(".")
    gpu_uuid: Optional[str] = None


@dataclass
class JobStatus:
    state: str  # queued | running | succeeded | failed | cancelled | manual_pending
    provider_job_id: str = ""
    progress: float = 0.0
    error: str = ""
    outputs: dict[str, str] = field(default_factory=dict)  # name -> URL or local path
    cost_micros: Optional[int] = None  # None: the provider does not report a cost
    usage: dict[str, Any] = field(default_factory=dict)
    raw: dict[str, Any] = field(default_factory=dict)

    @property
    def terminal(self) -> bool:
        return self.state in ("succeeded", "failed", "cancelled")


@dataclass
class JobResult:
    status: JobStatus
    files: dict[str, Path]  # name -> local file
    hashes: dict[str, str]  # name -> sha256
    provider_job_id: str
    model: str = ""
    model_version: str = ""
    polls: int = 0


class GenerationAdapter(ABC):
    route: RouteDescriptor
    #: model / service version reported in provenance
    model: str = ""
    model_version: str = ""

    def available(self) -> tuple[bool, str]:
        return True, ""

    @abstractmethod
    def estimate_ceiling_micros(self, job: GenerationJob) -> Optional[int]:
        """Upper bound of what one job can cost (None = unknown -> refused in unattended mode)."""

    @abstractmethod
    def submit(self, job: GenerationJob) -> str:
        """Start the job; return the provider job id."""

    @abstractmethod
    def poll(self, provider_job_id: str) -> JobStatus:
        ...

    @abstractmethod
    def fetch(self, status: JobStatus, out_dir: Path) -> dict[str, Path]:
        """Download / collect the outputs of a succeeded job into ``out_dir``."""

    def cancel(self, provider_job_id: str) -> str:
        return "uncertain"

    def reconcile(self, job: GenerationJob) -> Optional[str]:
        """Provider job id for an earlier submission of this job, if the provider can tell (None = unknown)."""
        return None

    def adapter_checks(self) -> list[dict]:
        """Self-checks used for certification (shape of requests/responses, credentials present)."""
        ok, why = self.available()
        return [{"check": "available", "ok": ok, "detail": why}]


def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


class UncertainSubmission(Exception):
    """An earlier submission may have reached the provider but its job id was never recorded."""


class JobTimeout(Exception):
    def __init__(self, provider_job_id: str, msg: str):
        super().__init__(msg)
        self.provider_job_id = provider_job_id


def run_job(adapter: GenerationAdapter, job: GenerationJob, *, registry, root_id: Optional[str],
            reservation_id: Optional[str], sleep: Callable[[float], None], now: Callable[[], float],
            deadline: Optional[float] = None, poll_interval_s: float = 10.0,
            on_cost: Callable[[int, dict], None] | None = None) -> JobResult:
    """Submit (or reconcile) one job, poll it to completion and fetch its outputs.

    ``registry`` is an :class:`~forge.assets.registry.AssetRegistry`. ``deadline`` is the attempt's
    active-work deadline (store clock); when it passes the job is cancelled and :class:`JobTimeout`
    raised. Costs are reported once through ``on_cost``.
    """
    key = job.idempotency_key
    rec = registry.record_job(key, asset_id=job.asset_id, root_id=root_id, route=adapter.route.id, stage=job.stage,
                              reservation_id=reservation_id,
                              data={"operation": job.operation, "params": job.params,
                                    "inputs": {k: str(v) for k, v in job.inputs.items()}})
    pid = rec.get("provider_job_id")
    if pid:
        pass  # submitted before (restart or retry): poll the existing job, never resubmit
    elif rec["created"]:
        pid = adapter.submit(job)
        registry.update_job(key, provider_job_id=pid, status="SUBMITTED")
    else:
        # a previous run crashed between the external call and recording its id: reconcile first
        pid = adapter.reconcile(job)
        if not pid:
            registry.update_job(key, status="UNKNOWN")
            raise UncertainSubmission(f"{adapter.route.id}: an earlier submission of {key} may have reached the "
                                      "provider and cannot be reconciled; provider completion/charge pending")
        registry.update_job(key, provider_job_id=pid, status="RECONCILED")
    polls = 0
    while True:
        st = adapter.poll(pid)
        polls += 1
        if st.state == "manual_pending":  # never block the scheduler on a person: pause and resume later
            registry.update_job(key, status="MANUAL_PENDING")
            raise ManualStepPending(st.error or f"{adapter.route.id}: manual step pending")
        if st.terminal:
            break
        if deadline is not None and now() >= deadline:
            outcome = adapter.cancel(pid)
            registry.update_job(key, status="CANCELLED" if outcome == "confirmed" else "UNKNOWN",
                                data={**rec["data"], "cancel": outcome})
            raise JobTimeout(pid, f"{adapter.route.id} job {pid} exceeded the active-work deadline "
                                  f"(cancellation {outcome})")
        sleep(poll_interval_s)
    cost = st.cost_micros
    if cost is not None and rec.get("cost_micros", 0) == 0 and on_cost is not None:
        on_cost(cost, st.usage)
    if st.state != "succeeded":
        registry.update_job(key, status=st.state.upper(), cost_micros=cost or 0,
                            data={**rec["data"], "error": st.error})
        raise AdapterError(f"{adapter.route.id} job {pid} {st.state}: {st.error or 'no detail'}")
    job.out_dir.mkdir(parents=True, exist_ok=True)
    files = adapter.fetch(st, job.out_dir)
    hashes = {name: sha256_file(p) for name, p in files.items()}
    registry.update_job(key, status="SUCCEEDED", cost_micros=cost or 0,
                        data={**rec["data"], "outputs": hashes, "polls": polls})
    return JobResult(st, files, hashes, pid, adapter.model, adapter.model_version, polls)
