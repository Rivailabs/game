"""Orchestrator / scheduler: readiness, reservations, leases, attempts, verification,
serial integration, owner decisions and restart recovery.

All decisions are taken from durable state. A provider saying "done" never marks
a task accepted: only protected checks, the integration writer's atomic promotion
and recorded owner approvals do.
"""

from __future__ import annotations

import json
import os
import socket
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from . import diffguard
from .artifacts import ArtifactStore
from .budget import (
    BudgetError,
    BudgetExceeded,
    BudgetLedger,
    DispatchStopped,
    NoCapConfigured,
    UnknownCeiling,
    root_scope,
)
from .checks import Check, CheckContext, CheckOutcome
from .credentials import CredentialError, redact
from .gitops import BaseMoved, GitError, IntegrationWriter, WorkspaceManager
from .leases import LeaseManager
from .models import (
    AttemptStatus,
    CandidateAttempt,
    Decision,
    Evidence,
    EvidenceClass,
    EvidenceStatus,
    FailureCategory,
    GateRecord,
    ReviewAction,
    ReviewDecision,
    RootTask,
    TaskState,
    TaskType,
)
from .providers.base import (
    CodingRequest,
    PolicyViolation,
    ProviderAdapter,
    ProviderRejected,
    ProviderResult,
    ProviderUnavailable,
    ReviewRequest,
    SupervisedOnly,
    TransportError,
    check_policy,
)
from .runtime import ProjectRuntime
from .sandbox.check import containment_of
from .sandbox.watchdog import (
    ActiveWorkTimeout,
    call_with_deadline,
    enforce_active_work_timeouts,
    remaining_s,
    run_check_with_deadline,
)
from .statemachine import can_transition, is_terminal
from .store import Store
from .util import new_id

S = TaskState


class ReviewError(Exception):
    pass


@dataclass
class TickReport:
    actions: list[str]

    @property
    def changed(self) -> bool:
        return bool(self.actions)


def _holder_alive(holder: str) -> bool:
    """Leases held by a dead process on this host are stale even before expiry."""
    try:
        host, pid = holder.split("|", 1)
        pid_i = int(pid.split("|")[0])
    except ValueError:
        return True
    if host != socket.gethostname():
        return True
    if pid_i == os.getpid():
        return True
    try:
        os.kill(pid_i, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


class Orchestrator:
    def __init__(self, store: Store, rt: ProjectRuntime):
        self.store = store
        self.rt = rt
        self.project = rt.project
        self.budget = BudgetLedger(store)
        self.leases = LeaseManager(store, default_ttl=rt.lease_ttl_s)
        self.artifacts = ArtifactStore(rt.artifacts_dir)
        self.ws = WorkspaceManager(rt.repo_path, rt.worktrees_dir)
        self.writer = IntegrationWriter(rt.repo_path, self.project.accepted_branch, rt.staging_dir)
        self.ctx = CheckContext(manifest=rt.manifest, scratch_dir=None)
        if hasattr(rt.generation_lane, "bind"):  # R2 generation lane needs the durable store and ledger
            rt.generation_lane.bind(self)

    # ================================================================ helpers
    def _event(self, type_: str, root: RootTask | None = None, attempt: CandidateAttempt | None = None, **kw) -> None:
        self.store.append_event(type_, project_id=self.project.id, root_id=root.id if root else None,
                                attempt_id=attempt.id if attempt else None, **kw)

    def _to(self, root: RootTask, dst: TaskState, reason: str = "", **kw) -> RootTask:
        return self.store.transition(root.id, dst, reason, **kw)

    def _asset_lane(self, root: RootTask):
        """The R2 generation lane when it owns this asset root (continuing a lane, or no catalogue first)."""
        gen = self.rt.generation_lane
        if root.task_type == TaskType.ASSET and gen is not None and hasattr(gen, "handles") and gen.handles(root):
            return gen
        return None

    def quality_attempts(self, root_id: str) -> list[CandidateAttempt]:
        return [a for a in self.store.list_attempts(root_id) if a.status != AttemptStatus.ABANDONED]

    def attempts_remaining(self, root: RootTask) -> int:
        return root.max_attempts - len(self.quality_attempts(root.id))

    def next_attempt_number(self, root_id: str) -> int:
        """Attempt rows are numbered uniquely; abandoned (transport/blocked) attempts keep their numbers."""
        return max((a.number for a in self.store.list_attempts(root_id)), default=0) + 1

    def _evidence(self, root: RootTask, attempt: CandidateAttempt | None, outcome: CheckOutcome,
                  candidate_hash: str | None = None) -> Evidence:
        refs = []
        details = dict(outcome.details)
        log_refs = {}
        for name, text in outcome.logs.items():
            if text:
                digest = self.artifacts.put_text(text)
                refs.append(digest)
                log_refs[name] = digest
        if log_refs:
            details["logs"] = log_refs
        ev = Evidence(id=new_id("ev"), root_id=root.id, attempt_id=attempt.id if attempt else None,
                      candidate_hash=candidate_hash or (attempt.candidate_hash if attempt else None),
                      evidence_class=outcome.evidence_class, status=outcome.status, name=outcome.name,
                      summary=outcome.summary, details=details, artifact_refs=refs)
        return self.store.add_evidence(ev)

    def accepted_head(self) -> str:
        return self.ws.ensure_branch(self.project.accepted_branch)

    # ================================================================ owner: task approval
    def approve_task(self, root_id: str, by: str = "owner") -> RootTask:
        root = self.store.get_root(root_id)
        if root.state != S.DRAFT:
            raise ReviewError(f"task {root_id} is {root.state.value}, not DRAFT")
        problems = []
        if not root.acceptance_cases:
            problems.append("no acceptance cases")
        if root.reservation_ceiling_micros is None:
            problems.append("no reservation ceiling")
        if root.task_type == TaskType.CODE and not root.permitted_paths:
            problems.append("no permitted paths")
        if not root.permitted_routes:
            problems.append("no permitted provider routes")
        if root.task_type == TaskType.ASSET and not root.asset_brief:
            problems.append("no asset brief")
        if problems:
            self._to(root, S.NEEDS_INPUT, "; ".join(problems))
            return self.store.get_root(root_id)
        self.budget.set_cap(root_scope(root.id), root.reservation_ceiling_micros, by=by)
        root = self._to(root, S.APPROVED, f"approved by {by}")
        self.store.add_evidence(Evidence(
            id=new_id("ev"), root_id=root.id, evidence_class=EvidenceClass.HUMAN, status=EvidenceStatus.INFO,
            name="task-approval", summary=f"scope and limits approved by {by}",
            details={"spec_version": root.spec_version, "reservation_ceiling_micros": root.reservation_ceiling_micros}))
        return root

    def revise_task(self, root_id: str, by: str = "owner", **changes) -> RootTask:
        """Material spec change: new linked root showing the previous cost (never a free retry)."""
        root = self.store.get_root(root_id)
        if root.state == S.RUNNING:
            self.cancel(root_id, by)
        cost = self.budget.root_cost(root_id)
        new = self.store.revise_root(root_id, cost_micros=cost, **changes)
        self._event("spec_revised", new, previous_root_id=root_id, previous_cost_micros=new.previous_cost_micros, by=by)
        return new

    # ================================================================ readiness
    def _dependency_status(self, root: RootTask) -> tuple[bool, str, list[tuple[int, str]]]:
        """Return (ready, reason, pins_to_record)."""
        pins: list[tuple[int, str]] = []
        for i, dep in enumerate(root.dependencies):
            try:
                head = self.store.lineage_head(dep.root_id)
            except Exception:
                return False, f"dependency {dep.root_id} does not exist", pins
            if head.state != S.ACCEPTED or not head.accepted_artifact_hash:
                # an older accepted member of the lineage still counts until replaced
                base = self.store.get_root(dep.root_id)
                if base.state == S.ACCEPTED and base.accepted_artifact_hash and head.id != base.id:
                    head = base
                else:
                    return False, f"waiting for dependency {head.ticket or head.id} ({head.state.value})", pins
            if dep.artifact_hash is None:
                pins.append((i, head.accepted_artifact_hash))
            elif dep.artifact_hash != head.accepted_artifact_hash:
                return False, (f"dependency {head.ticket or head.id} was replaced "
                               f"({dep.artifact_hash[:12]} -> {head.accepted_artifact_hash[:12]}); "
                               f"consumer is stale - acknowledge to re-pin"), pins
        if self.store.stale_marks(root.id):
            return False, "stale dependency awaiting owner acknowledgement", pins
        return True, "", pins

    def _route(self, root: RootTask) -> tuple[Optional[str], Optional[ProviderAdapter], str]:
        reasons = []
        for name in root.permitted_routes:
            p = self.rt.providers.get(name)
            if p is None:
                reasons.append(f"{name}: not configured")
                continue
            try:
                check_policy(p.descriptor, self.project.policy, unattended=self.rt.unattended)
                return name, p, ""
            except PolicyViolation as e:
                reasons.append(f"{name}: {e}")
        return None, None, "no permitted provider route: " + "; ".join(reasons or ["none listed"])

    def _resource_status(self, root: RootTask, check_names: list[str]) -> tuple[bool, str]:
        prof = root.resource_profile
        cap = self.rt.capability
        if prof.gpu_vram_gb is not None and (cap is None or not cap.satisfies(prof)):
            return False, f"no verified worker with >= {prof.gpu_vram_gb} GB VRAM"
        for name in check_names:
            chk = self.rt.checks.get(name)
            if chk is None:
                return False, f"protected check {name!r} is not defined in the project configuration"
            ok, why = chk.availability(self.ctx)
            if not ok:
                return False, f"{name}: {why}"
        if prof.needs_unity and not (self.rt.manifest and self.rt.manifest.unity_editor_path):
            return False, "Unity editor not available (toolchain manifest has no verified editor)"
        if prof.needs_device and not any("device" in self.rt.checks[n].requires for n in check_names
                                         if n in self.rt.checks):
            from .checks.device import AdbDeviceService

            d, why = AdbDeviceService().select(prof.device_serial)
            if d is None:
                return False, why
        return True, ""

    def evaluate_readiness(self, root: RootTask) -> RootTask:
        ok, reason, pins = self._dependency_status(root)
        if pins:
            deps = list(root.dependencies)
            for i, h in pins:
                deps[i] = deps[i].model_copy(update={"artifact_hash": h})
            root = self.store.update_root(root, dependencies=deps)
            self._event("dependencies_pinned", root, pins=[h for _, h in pins])
        if ok and self._asset_lane(root) is None:  # generation routes are checked by the asset lane itself
            name, _, why = self._route(root)
            if name is None:
                ok, reason = False, why
        if ok:
            ok, reason = self._resource_status(root, root.verification_checks + root.integration_checks)
        if ok and root.task_type == TaskType.ASSET:
            reason = self._asset_preflight(root)
            ok = not reason
        if ok:
            if root.state != S.READY:
                return self._to(root, S.READY, "dependencies match, policy passes, resources available")
            return root
        if root.state in (S.APPROVED, S.READY):
            return self._to(root, S.BLOCKED, reason)
        if root.state == S.BLOCKED and root.state_reason != reason:
            return self.store.update_root(root, state_reason=reason)
        return root

    def acknowledge_dependency_change(self, root_id: str, by: str = "owner") -> RootTask:
        root = self.store.get_root(root_id)
        deps = [d.model_copy(update={"artifact_hash": None}) for d in root.dependencies]
        self.store.clear_stale(root_id)
        if is_terminal(root.state):
            self._event("stale_acknowledged", root, by=by, note="terminal root keeps its acceptance record")
            return root
        root = self.store.update_root(root, dependencies=deps)
        self._event("stale_acknowledged", root, by=by)
        return root

    def _mark_consumers_stale(self, accepted: RootTask) -> None:
        lineage = {accepted.id}
        cur = accepted
        while cur.previous_root_id:
            lineage.add(cur.previous_root_id)
            cur = self.store.get_root(cur.previous_root_id)
        for consumer in self.store.list_roots(self.project.id):
            for dep in consumer.dependencies:
                if dep.root_id in lineage and dep.artifact_hash and dep.artifact_hash != accepted.accepted_artifact_hash:
                    self.store.mark_stale(consumer.id, dep.root_id, dep.artifact_hash, accepted.accepted_artifact_hash)
                    if consumer.state in (S.APPROVED, S.READY):
                        self._to(consumer, S.BLOCKED, f"dependency {accepted.ticket or accepted.id} replaced; stale")
                    elif consumer.state == S.INTEGRATION_READY:
                        self._to(consumer, S.BLOCKED, f"dependency {accepted.ticket or accepted.id} replaced; stale")

    # ================================================================ scheduler loop
    def tick(self) -> TickReport:
        actions: list[str] = []
        actions += self.recover()
        actions += enforce_active_work_timeouts(self)
        for root in self.store.list_roots(self.project.id):
            root = self.store.get_root(root.id)
            if root.held:
                continue
            before = root.state
            try:
                if root.state in (S.APPROVED, S.BLOCKED):
                    if root.state == S.BLOCKED and self._has_pending_integration(root):
                        ok, reason, _ = self._dependency_status(root)
                        if ok:
                            ok, reason = self._resource_status(
                                root, root.verification_checks + root.integration_checks)
                        if ok:
                            root = self._to(root, S.READY, "blocked integration can be revalidated")
                    else:
                        root = self.evaluate_readiness(root)
                elif root.state == S.RETRY_PENDING:
                    root = self._retry_or_fail(root)
                elif root.state == S.READY:
                    root = self.dispatch(root)
                elif root.state == S.INTEGRATION_READY:
                    root = self.integrate(root)
                elif root.state == S.CANCEL_REQUESTED:
                    root = self._complete_cancel(root)
            except DispatchStopped:
                pass
            root = self.store.get_root(root.id)
            if root.state != before:
                actions.append(f"{root.ticket or root.id}: {before.value} -> {root.state.value}")
        return TickReport(actions)

    def run_until_idle(self, max_ticks: int = 50) -> list[str]:
        log: list[str] = []
        for _ in range(max_ticks):
            rep = self.tick()
            log += rep.actions
            if not rep.changed:
                break
        return log

    def _retry_or_fail(self, root: RootTask) -> RootTask:
        if self.attempts_remaining(root) <= 0:
            return self._to(root, S.FAILED, f"attempt limit reached ({root.max_attempts} total)")
        return self._to(root, S.READY, f"repair attempt {len(self.quality_attempts(root.id)) + 1} of {root.max_attempts}")

    def _has_pending_integration(self, root: RootTask) -> bool:
        a = self.store.latest_attempt(root.id)
        return bool(a and a.status == AttemptStatus.VERIFIED and a.candidate_hash)

    # ================================================================ dispatch
    def dispatch(self, root: RootTask) -> RootTask:
        if self.budget.dispatch_stopped():
            self._event("dispatch_skipped", root, reason="owner stop-dispatch switch is on")
            raise DispatchStopped()
        ok, reason, pins = self._dependency_status(root)
        if not ok or pins:
            # dependencies changed since readiness was evaluated: re-check them first
            return self.evaluate_readiness(root) if ok else self._to(root, S.BLOCKED, reason)
        latest = self.store.latest_attempt(root.id)
        if latest and latest.status == AttemptStatus.VERIFIED and latest.candidate_hash:
            return self._revalidate(root, latest)
        if self.attempts_remaining(root) <= 0:
            return self._to(root, S.PAUSED, "attempt limit reached; owner decision required (cancel or revise)")
        if root.task_type == TaskType.ASSET:
            gen = self._asset_lane(root)
            return gen.dispatch(root) if gen is not None else self._dispatch_asset(root)
        name, provider, why = self._route(root)
        if provider is None:
            return self._to(root, S.BLOCKED, why)
        base = self.accepted_head()
        number = self.next_attempt_number(root.id)
        quality = self.quality_attempts(root.id)
        prev = quality[-1] if quality else None
        attempt = CandidateAttempt(id=new_id("att"), root_id=root.id, number=number, base_commit=base,
                                   provider=name, started_at=self.store.now())
        req = self._coding_request(root, attempt, prev, self.rt.worktrees_dir / attempt.id)
        ceiling = provider.estimate_ceiling_micros(req)
        reviewer = self.rt.providers.get(self.rt.reviewer_route) if self.rt.reviewer_route else None
        if ceiling is not None and reviewer is not None:
            rc = reviewer.review_ceiling_micros(ReviewRequest(root, "", "", root.acceptance_cases, "", ""))
            ceiling = None if rc is None else ceiling + rc
        try:
            res_id = self.budget.reserve(project_id=root.project_id, milestone_id=root.milestone_id,
                                         root_id=root.id, amount_micros=ceiling, attempt_id=attempt.id,
                                         provider=name, purpose=f"attempt {number}", unattended=self.rt.unattended)
        except UnknownCeiling as e:
            return self._to(root, S.BLOCKED, str(e))
        except DispatchStopped:
            raise
        except (BudgetExceeded, NoCapConfigured) as e:
            return self._to(root, S.PAUSED, f"budget: {e}")
        keys = [f"attempt:{attempt.id}"]
        if root.resource_profile.needs_device:
            keys.append(f"device:{root.resource_profile.device_serial or 'default'}")
        if root.resource_profile.gpu_vram_gb is not None:
            keys.append(f"gpu:{(self.rt.capability.worker_id if self.rt.capability else 'local')}")
        if not self.leases.acquire_all(keys, self.rt.worker_id, root_id=root.id, attempt_id=attempt.id):
            self.budget.release(res_id)
            self._event("dispatch_waiting_for_resource", root, keys=keys)
            return root
        attempt.reservation_id = res_id
        attempt.lease_holder = self.rt.worker_id
        attempt.lease_expires_at = self.store.now() + self.rt.lease_ttl_s
        attempt.heartbeat_at = self.store.now()
        self.store.create_attempt(attempt)
        # persist RUNNING before touching git or the provider
        root = self._to(root, S.RUNNING, f"attempt {number} dispatched to {name}", attempt_id=attempt.id,
                        reservation_id=res_id)
        try:
            # Every attempt starts from the current accepted main; repairs receive the
            # previous findings as instructions (a bad candidate is never built upon).
            path, branch = self.ws.create(attempt.id, base)
        except GitError as e:
            self.budget.release(res_id)
            attempt.status = AttemptStatus.ABANDONED
            self.store.save_attempt(attempt)
            self.leases.release_for_holder(self.rt.worker_id)
            return self._to(root, S.PAUSED, f"workspace error: {e}")
        attempt.workspace_path, attempt.branch = str(path), branch
        self.store.save_attempt(attempt)
        req.workspace = path
        return self._run_provider(root, attempt, provider, req)

    def _coding_request(self, root: RootTask, attempt: CandidateAttempt, prev: Optional[CandidateAttempt],
                        path: Path) -> CodingRequest:
        notes = ""
        if prev is not None:
            notes = prev.repair_instructions or "previous attempt did not pass; see evidence"
        return CodingRequest(
            root=root, attempt=attempt, workspace=path, project_workdir=self.project.workdir,
            idempotency_key=f"{root.id}:{attempt.id}:code",
            instructions=root.description, repair_notes=notes, protected_paths=self.project.protected_paths,
        )

    def _heartbeat(self, attempt: CandidateAttempt) -> None:
        self.leases.heartbeat(f"attempt:{attempt.id}", self.rt.worker_id, self.rt.lease_ttl_s)
        attempt.heartbeat_at = self.store.now()
        attempt.lease_expires_at = self.store.now() + self.rt.lease_ttl_s
        self.store.save_attempt(attempt)

    def _run_provider(self, root: RootTask, attempt: CandidateAttempt, provider: ProviderAdapter,
                      req: CodingRequest, *, recovered: bool = False) -> RootTask:
        key = req.idempotency_key
        self.store.record_provider_job(idempotency_key=key, root_id=root.id, attempt_id=attempt.id,
                                       provider=provider.descriptor.name, operation="code",
                                       reservation_id=attempt.reservation_id)
        result: Optional[ProviderResult] = None
        tries = 0
        while result is None:
            if tries > 0 or recovered:
                rec = provider.reconcile(key)  # reconcile before any resubmission
                self._event("provider_reconcile", root, attempt, idempotency_key=key, status=rec.status)
                if rec.status == "completed" and rec.result is not None:
                    result = rec.result
                    break
                if rec.status == "running":
                    self._heartbeat(attempt)
                    return root
                if rec.status == "unknown":
                    return self._charge_pending(root, attempt, rec.detail or "provider status unknown")
            try:
                result = call_with_deadline(lambda: provider.submit(req),
                                            remaining_s(root, attempt, self.store.now()),
                                            what=f"provider {provider.descriptor.name} submission")
                self.store.update_provider_job(key, status="SUBMITTED", provider_job_id=result.provider_job_id)
            except ActiveWorkTimeout as e:
                self.leases.release(f"attempt:{attempt.id}", self.rt.worker_id)
                self.store.update_provider_job(key, status="UNKNOWN")
                self.budget.mark_charge_pending(attempt.reservation_id, str(e))
                attempt.status = AttemptStatus.FAILED  # the attempt consumed its active duration
                attempt.failure_category = FailureCategory.OTHER
                attempt.finished_at = self.store.now()
                attempt.repair_instructions = ("Previous attempt exceeded the active-work timeout; "
                                               "deliver a smaller change.")
                self.store.save_attempt(attempt)
                return self._to(root, S.PAUSED, f"{e}; provider completion/charge pending")
            except TransportError as e:
                if e.cost_micros:
                    self.budget.record_usage(attempt.reservation_id, e.cost_micros, model=e.usage.model,
                                             usage=e.usage.to_dict())
                tries += 1
                attempt.transport_retries += 1
                self.store.save_attempt(attempt)
                self._event("transport_retry", root, attempt, error=str(e), retry=tries,
                            charged_micros=e.cost_micros)
                if tries > self.rt.transport_retries:
                    self.store.update_provider_job(key, status="FAILED")
                    attempt.status = AttemptStatus.ABANDONED  # transport handling, not a quality attempt
                    attempt.finished_at = self.store.now()
                    self.store.save_attempt(attempt)
                    self.budget.settle(attempt.reservation_id)
                    self.leases.release(f"attempt:{attempt.id}", self.rt.worker_id)
                    return self._to(root, S.PAUSED, f"provider unreachable after {tries} transport attempts; "
                                                    f"charges so far are recorded against this root")
                self.rt_sleep(self.rt.backoff_s * (2 ** (tries - 1)))
                self._heartbeat(attempt)
            except (SupervisedOnly, PolicyViolation, CredentialError, ProviderUnavailable) as e:
                # nothing was sent to the provider: release the reservation in full
                self.store.update_provider_job(key, status="FAILED")
                attempt.status = AttemptStatus.ABANDONED
                self.store.save_attempt(attempt)
                self.budget.release(attempt.reservation_id)
                self.leases.release(f"attempt:{attempt.id}", self.rt.worker_id)
                return self._to(root, S.PAUSED, f"{type(e).__name__}: {e}")
            except ProviderRejected as e:
                if e.cost_micros:
                    self.budget.record_usage(attempt.reservation_id, e.cost_micros, model=e.usage.model,
                                             usage=e.usage.to_dict())
                self.store.update_provider_job(key, status="FAILED")
                attempt.status = AttemptStatus.ABANDONED
                attempt.finished_at = self.store.now()
                self.store.save_attempt(attempt)
                self.budget.settle(attempt.reservation_id)
                self.leases.release(f"attempt:{attempt.id}", self.rt.worker_id)
                return self._to(root, S.PAUSED, redact(f"provider rejected the request: {e}"))
            except Exception as e:  # unexpected provider error: charges are unknown, so keep them pending
                self.leases.release(f"attempt:{attempt.id}", self.rt.worker_id)
                return self._charge_pending(root, attempt, redact(f"provider error {type(e).__name__}: {e}"))
        return self._ingest_result(root, attempt, result, key)

    def rt_sleep(self, seconds: float) -> None:
        self.store.clock.sleep(seconds)

    def _ingest_result(self, root: RootTask, attempt: CandidateAttempt, result: ProviderResult, key: str) -> RootTask:
        job = self.store.get_provider_job(key)
        if job and job["status"] != "COMPLETED":
            if result.cost_micros:
                self.budget.record_usage(attempt.reservation_id, result.cost_micros, model=result.usage.model,
                                         usage=result.usage.to_dict())
            self.store.update_provider_job(key, status="COMPLETED", provider_job_id=result.provider_job_id,
                                           result_json=json.dumps({"status": result.status, "summary": result.summary,
                                                                   "files": result.files_written}),
                                           usage_json=json.dumps(result.usage.to_dict()))
        transcript_ref = self.artifacts.put_text(result.transcript or "")
        self.store.add_evidence(Evidence(
            id=new_id("ev"), root_id=root.id, attempt_id=attempt.id, evidence_class=EvidenceClass.MODEL_REVIEW,
            status=EvidenceStatus.INFO, name="builder-report",
            summary=f"builder ({attempt.provider}) status={result.status}: {result.summary}"[:500],
            details={"files_written": result.files_written, "usage": result.usage.to_dict(),
                     "cost_micros": result.cost_micros, "transport_retries": result.transport_retries,
                     "note": "provider prose is evidence, not a state transition"},
            artifact_refs=[transcript_ref]))
        self._heartbeat(attempt)
        try:
            cand = self.ws.commit_candidate(attempt.workspace_path,
                                            f"Forge candidate {root.ticket or root.id} attempt {attempt.number}\n\n"
                                            f"root={root.id} attempt={attempt.id}")
        except GitError as e:
            cand = None
            result.summary += f" (commit failed: {e})"
        if cand is None:
            attempt.repair_instructions = (f"Previous attempt produced no changes (provider status {result.status}): "
                                           f"{result.summary}")
            return self._fail_attempt(root, attempt, FailureCategory.OTHER, "no candidate produced: " + result.summary)
        attempt.candidate_hash = cand
        attempt.status = AttemptStatus.CANDIDATE
        self.store.save_attempt(attempt)
        root = self._to(root, S.VERIFYING, f"candidate {cand[:12]} produced", candidate_hash=cand)
        return self.verify(root, attempt)

    def _charge_pending(self, root: RootTask, attempt: CandidateAttempt, detail: str) -> RootTask:
        self.budget.mark_charge_pending(attempt.reservation_id, detail)
        attempt.status = AttemptStatus.ABANDONED
        attempt.finished_at = self.store.now()
        self.store.save_attempt(attempt)
        self.store.update_provider_job(f"{root.id}:{attempt.id}:code", status="UNKNOWN")
        return self._to(root, S.PAUSED, "provider completion/charge pending: " + detail)

    # ================================================================ asset tasks (catalogue lane first)
    # State mapping (the plan's state machine has no RUNNING -> BLOCKED / NEEDS_INPUT edge):
    # * preconditions (disk guard, Blender, index, packages, judge policy, brief) are checked during
    #   readiness and in READY, so a missing tool gives BLOCKED before anything is spent;
    # * lane exit 3 discovered while RUNNING -> PAUSED with "catalogue lane blocked: <reason>";
    # * lane exit 2 (NONE) -> the generation lane if one is configured, otherwise
    #   RUNNING -> PAUSED -> NEEDS_INPUT "no catalogue match; generation lane not available".
    NO_GENERATION_LANE = "no catalogue match; generation lane not available"

    def _brief_path(self, root: RootTask) -> Path:
        p = Path(root.asset_brief or "")
        return p if p.is_absolute() else self.rt.repo_path / p

    def _asset_target_rel(self, brief_id: str) -> str:
        wd = (self.project.workdir or ".").strip("/")
        rel = f"assets/source/{brief_id}"
        return rel if wd in ("", ".") else f"{wd}/{rel}"

    def _asset_preflight(self, root: RootTask) -> str:
        """Reason an asset task cannot be dispatched ("" = ready). Nothing is downloaded or sent."""
        from .lanes.catalogue import CatalogueError, load_brief
        from .lanes.run_catalogue import preflight
        from .pathglob import match_path
        from .providers.base import check_judge_policy

        if not root.asset_brief:
            return "asset task has no brief"
        gen = self._asset_lane(root)
        if gen is not None:
            return gen.preflight(root)
        try:
            brief = load_brief(self._brief_path(root))
        except (CatalogueError, ValueError) as e:
            return f"asset brief invalid: {e}"[:500]
        probe = f"{self._asset_target_rel(brief.id)}/credits.json"
        if not match_path(probe, root.permitted_paths):
            return f"permitted_paths must include {self._asset_target_rel(brief.id)}/**"
        if self.rt.catalogue is None:
            return "catalogue lane not configured for this project"
        name, provider, why = self._route(root)
        if provider is None:
            return why
        try:
            check_judge_policy(provider.descriptor, self.project.policy, unattended=self.rt.unattended)
        except PolicyViolation as e:
            return f"visual judge route {name}: {e} (the owner can allow 'render_image' in [policy])"
        why = preflight(brief, self.rt.repo_path / (self.project.workdir or "."), self.rt.catalogue, judge=provider)
        return f"catalogue lane: {why}" if why else ""

    def _dispatch_asset(self, root: RootTask) -> RootTask:
        from .lanes.catalogue import load_brief
        from .lanes.run_catalogue import EXIT_BLOCKED, EXIT_NONE, EXIT_PICKED, run_brief

        why = self._asset_preflight(root)
        if why:
            return self._to(root, S.BLOCKED, why)
        brief = load_brief(self._brief_path(root))
        name, provider, _ = self._route(root)
        base = self.accepted_head()
        number = self.next_attempt_number(root.id)
        attempt = CandidateAttempt(id=new_id("att"), root_id=root.id, number=number, base_commit=base,
                                   provider=name, started_at=self.store.now())
        ceiling = provider.judge_ceiling_micros(brief.candidates)
        try:
            res_id = self.budget.reserve(project_id=root.project_id, milestone_id=root.milestone_id,
                                         root_id=root.id, amount_micros=ceiling, attempt_id=attempt.id,
                                         provider=name, purpose=f"catalogue judge {brief.id} (attempt {number})",
                                         unattended=self.rt.unattended)
        except UnknownCeiling as e:
            return self._to(root, S.BLOCKED, str(e))
        except DispatchStopped:
            raise
        except (BudgetExceeded, NoCapConfigured) as e:
            return self._to(root, S.PAUSED, f"budget: {e}")
        if not self.leases.acquire_all([f"attempt:{attempt.id}"], self.rt.worker_id, root_id=root.id,
                                       attempt_id=attempt.id):
            self.budget.release(res_id)
            return root
        attempt.reservation_id = res_id
        attempt.lease_holder = self.rt.worker_id
        attempt.lease_expires_at = self.store.now() + self.rt.lease_ttl_s
        attempt.heartbeat_at = self.store.now()
        self.store.create_attempt(attempt)
        root = self._to(root, S.RUNNING, f"catalogue lane for brief {brief.id} (attempt {number}, judge {name})",
                        attempt_id=attempt.id, reservation_id=res_id)
        try:
            path, branch = self.ws.create(attempt.id, base)
        except GitError as e:
            return self._end_asset_attempt(root, attempt, S.PAUSED, f"workspace error: {e}", release=True)
        attempt.workspace_path, attempt.branch = str(path), branch
        self.store.save_attempt(attempt)
        key = f"{root.id}:{attempt.id}:judge"
        self.store.record_provider_job(idempotency_key=key, root_id=root.id, attempt_id=attempt.id,
                                       provider=provider.descriptor.name, operation="visual_judge",
                                       reservation_id=res_id)
        target = Path(path) / (self.project.workdir or ".")

        def on_cost(jr) -> None:
            if jr.cost_micros:
                self.budget.record_usage(res_id, jr.cost_micros, model=jr.usage.model, usage=jr.usage.to_dict())
            self.store.update_provider_job(key, status="COMPLETED", usage_json=json.dumps(jr.usage.to_dict()),
                                           result_json=json.dumps({"pick": jr.pick, "reason": jr.reason}))

        try:
            res = run_brief(brief, target, self.rt.catalogue, provider, on_cost=on_cost, idempotency_key=key)
        except (TransportError, ProviderRejected) as e:
            if e.cost_micros:
                self.budget.record_usage(res_id, e.cost_micros, model=e.usage.model, usage=e.usage.to_dict())
            self.store.update_provider_job(key, status="FAILED")
            return self._end_asset_attempt(root, attempt, S.PAUSED, redact(f"visual judge failed: {e}"))
        except (SupervisedOnly, PolicyViolation, CredentialError, ProviderUnavailable) as e:
            self.store.update_provider_job(key, status="FAILED")
            return self._end_asset_attempt(root, attempt, S.PAUSED, f"{type(e).__name__}: {e}", release=True)
        except Exception as e:  # unknown outcome: the judge may have charged
            self.store.update_provider_job(key, status="UNKNOWN")
            self.budget.mark_charge_pending(res_id, redact(f"catalogue lane error {type(e).__name__}: {e}"))
            return self._end_asset_attempt(root, attempt, S.PAUSED,
                                           redact(f"provider completion/charge pending: catalogue lane error "
                                                  f"{type(e).__name__}: {e}"), settle=False)
        self._lane_evidence(root, attempt, res, "catalogue-lane")
        lane_name = "catalogue lane"
        if res.exit_code == EXIT_NONE and self.rt.generation_lane is not None:
            lane_name = "generation lane"
            self._event("asset_generation_fallthrough", root, attempt, brief=brief.id, reason=res.reason)
            res = self.rt.generation_lane(root, target, brief)
            self._lane_evidence(root, attempt, res, "generation-lane")
        if res.exit_code == EXIT_PICKED:
            return self._asset_candidate(root, attempt, brief, res)
        if res.exit_code == EXIT_BLOCKED:
            return self._end_asset_attempt(root, attempt, S.PAUSED, f"{lane_name} blocked: {res.reason}")
        # NONE with no generation lane
        reason = self.NO_GENERATION_LANE if self.rt.generation_lane is None else f"no asset produced: {res.reason}"
        root = self._end_asset_attempt(root, attempt, S.PAUSED, reason)
        return self._to(root, S.NEEDS_INPUT, reason)

    def _end_asset_attempt(self, root: RootTask, attempt: CandidateAttempt, dst: TaskState, reason: str, *,
                           release: bool = False, settle: bool = True) -> RootTask:
        """Close an asset attempt that produced no candidate (not a quality attempt)."""
        attempt.status = AttemptStatus.ABANDONED
        attempt.finished_at = self.store.now()
        self.store.save_attempt(attempt)
        if attempt.reservation_id and settle:
            (self.budget.release if release else self.budget.settle)(attempt.reservation_id)
        self.leases.release(f"attempt:{attempt.id}", self.rt.worker_id)
        if attempt.workspace_path and Path(attempt.workspace_path).exists():
            self.ws.remove(attempt.workspace_path)
        return self._to(self.store.get_root(root.id), dst, reason)

    def _lane_evidence(self, root: RootTask, attempt: CandidateAttempt, res, name: str) -> Evidence:
        details = res.to_dict() if hasattr(res, "to_dict") else dict(res.__dict__)
        renders = {}
        refs = []
        if getattr(res, "out_dir", None) and getattr(res, "exit_code", None) == 0:
            for view in ("front", "side", "back", "three_quarter"):
                p = Path(res.out_dir) / f"{view}.png"
                if p.exists():
                    renders[view] = self.artifacts.put_file(p)
                    refs.append(renders[view])
        details["renders"] = renders
        status = {0: EvidenceStatus.INFO, 2: EvidenceStatus.INFO, 3: EvidenceStatus.BLOCKED}.get(res.exit_code,
                                                                                               EvidenceStatus.INFO)
        summary = f"{name}: {res.status} - {res.reason}"
        if res.exit_code == 0:
            summary += " - owner approval required; licence labels can be wrong"
        return self.store.add_evidence(Evidence(
            id=new_id("ev"), root_id=root.id, attempt_id=attempt.id, evidence_class=EvidenceClass.ASSET,
            status=status, name=name, summary=summary[:500], details=details, artifact_refs=refs))

    def _asset_candidate(self, root: RootTask, attempt: CandidateAttempt, brief, res) -> RootTask:
        if getattr(res, "asset_stage", None):  # generation lane: its own asset-contract evidence
            from .assets.generation import contract_evidence

            self.store.add_evidence(contract_evidence(root, attempt, res))
        else:
            self.store.add_evidence(Evidence(
                id=new_id("ev"), root_id=root.id, attempt_id=attempt.id, evidence_class=EvidenceClass.STATIC,
                status=EvidenceStatus.PASS, name="catalogue-budget-check",
                summary=f"{res.pick}: within {brief.max_tris} triangles, {brief.max_texture}px textures, "
                        f"{brief.size_m} m",
                details={"brief": brief.id, "max_tris": brief.max_tris, "max_texture": brief.max_texture,
                         "size_m": brief.size_m}))
        try:
            cand = self.ws.commit_candidate(attempt.workspace_path,
                                            f"Forge asset candidate {root.ticket or root.id}: catalogue pick "
                                            f"{res.pick} for {brief.id}\n\nroot={root.id} attempt={attempt.id}")
        except GitError as e:
            cand = None
            res.reason += f" (commit failed: {e})"
        if cand is None:
            attempt.repair_instructions = f"catalogue pick produced no change: {res.reason}"
            return self._fail_attempt(root, attempt, FailureCategory.OTHER, "no candidate produced: " + res.reason)
        attempt.candidate_hash = cand
        attempt.status = AttemptStatus.CANDIDATE
        self.store.save_attempt(attempt)
        root = self._to(root, S.VERIFYING, f"catalogue candidate {cand[:12]} ({res.pick})", candidate_hash=cand)
        return self.verify(root, attempt)

    def _needs_owner_visual(self, root: RootTask) -> bool:
        return root.visual_review_required or root.task_type == TaskType.ASSET

    def _lane_budget_passed(self, root: RootTask, attempt: CandidateAttempt) -> bool:
        if root.task_type != TaskType.ASSET:
            return False
        return any(e.name in ("catalogue-budget-check", "asset-contract-check") and e.status == EvidenceStatus.PASS
                   for e in self.store.list_evidence(root.id, attempt.id))

    # ================================================================ verification
    def _run_checks(self, root: RootTask, attempt: CandidateAttempt, names: list[str], workdir: Path,
                    candidate_hash: str) -> list[tuple[CheckOutcome, Evidence]]:
        out = []
        for name in names:
            chk: Check | None = self.rt.checks.get(name)
            if chk is None:
                oc = CheckOutcome(name, EvidenceClass.STATIC, EvidenceStatus.BLOCKED,
                                  f"check {name} not defined in project configuration")
            else:
                try:
                    oc = run_check_with_deadline(chk, workdir, self.ctx,
                                                 remaining_s(root, attempt, self.store.now()))
                except Exception as e:  # a crashing checker is incomplete evidence, never a pass
                    oc = CheckOutcome(name, chk.evidence_class, EvidenceStatus.INCOMPLETE, f"check crashed: {e}")
            out.append((oc, self._evidence(root, attempt, oc, candidate_hash)))
            self._heartbeat(attempt)
        return out

    @staticmethod
    def _repair_notes(outcomes: list[CheckOutcome]) -> str:
        lines = []
        for oc in outcomes:
            if oc.status == EvidenceStatus.PASS:
                continue
            lines.append(f"- {oc.name}: {oc.status.value} - {oc.summary}")
            for f in (oc.details.get("failures") or [])[:10]:
                lines.append(f"    * {f.get('name')}: {str(f.get('message', ''))[:400]}")
            for e in (oc.details.get("build_errors") or [])[:10]:
                lines.append(f"    * {e[:300]}")
            if not oc.details.get("failures") and not oc.details.get("build_errors"):
                tail = (oc.logs.get("stdout") or oc.logs.get("test.log") or oc.logs.get("build.log") or "")[-1500:]
                if tail:
                    lines.append("    log tail:\n" + "\n".join("      " + l for l in tail.splitlines()[-25:]))
        return "\n".join(lines)

    def verify(self, root: RootTask, attempt: CandidateAttempt) -> RootTask:
        cand = attempt.candidate_hash
        changed = self.ws.changed_files(attempt.base_commit, cand)
        diff = self.ws.diff(attempt.base_commit, cand)
        diff_ref = self.artifacts.put_text(diff)
        guard = diffguard.analyze(diff, changed, permitted_paths=root.permitted_paths,
                                  protected_paths=self.project.protected_paths)
        attempt.gate_change_flags = sorted(guard.kinds())
        self.store.save_attempt(attempt)
        guard_status = EvidenceStatus.FAIL if guard.scope_violations else (
            EvidenceStatus.INFO if guard.flags else EvidenceStatus.PASS)
        self.store.add_evidence(Evidence(
            id=new_id("ev"), root_id=root.id, attempt_id=attempt.id, candidate_hash=cand,
            evidence_class=EvidenceClass.STATIC, status=guard_status, name="diff-guard",
            summary=("scope violation: " + ", ".join(guard.scope_violations)) if guard.scope_violations else
            ("gate-weakening patterns flagged for separate review: " + ", ".join(sorted(guard.kinds())))
            if guard.flags else "no gate-weakening patterns; all changes inside permitted paths",
            details={**guard.to_dict(), "changed_files": [c.__dict__ for c in changed], "diff": diff_ref,
                     "base_commit": attempt.base_commit}, artifact_refs=[diff_ref]))
        if guard.scope_violations:
            attempt.repair_instructions = ("Changes outside the permitted paths are not allowed: "
                                           + ", ".join(guard.scope_violations))
            return self._fail_attempt(root, attempt, FailureCategory.SCOPE_VIOLATION,
                                      "candidate changed files outside permitted paths")
        restored = self.ws.restore_protected(attempt.workspace_path, attempt.base_commit, self.project.protected_paths)
        if restored:
            self._event("protected_paths_restored", root, attempt, paths=restored)
        results = self._run_checks(root, attempt, root.verification_checks, Path(attempt.workspace_path), cand)
        outcomes = [oc for oc, _ in results]
        approval = self.store.get_or_create_approval(root.id, attempt.id, cand)
        failed = [oc for oc in outcomes if oc.status == EvidenceStatus.FAIL]
        incomplete = [oc for oc in outcomes if oc.status in (EvidenceStatus.INCOMPLETE, EvidenceStatus.BLOCKED)]
        if failed:
            cat = FailureCategory.COMPILE_ERROR if any(o.details.get("failure_category") == "compile_error"
                                                       for o in failed) else FailureCategory.TEST_FAILURE
            approval.technical_pass = GateRecord(decision=Decision.REJECTED, by="forge", at=self.store.now(),
                                                 reason="; ".join(f"{o.name}: {o.summary}" for o in failed))
            self.store.save_approval(approval)
            attempt.repair_instructions = self._repair_notes(outcomes)
            return self._fail_attempt(root, attempt, cat, "protected checks failed")
        if not root.verification_checks:
            incomplete_reason = "no protected verification checks configured for this task"
        else:
            incomplete_reason = "; ".join(f"{o.name}: {o.summary}" for o in incomplete)
        technical_ok = not incomplete and (bool(root.verification_checks) or self._lane_budget_passed(root, attempt))
        # independent reviewer (opinion, not authority)
        review_verdict = self._independent_review(root, attempt, diff, outcomes)
        if attempt.reservation_id:  # asset-lane attempts reserve per provider job instead
            self.budget.settle(attempt.reservation_id)
        self.leases.release(f"attempt:{attempt.id}", self.rt.worker_id)
        approval.technical_pass = GateRecord(
            decision=Decision.APPROVED if technical_ok else Decision.PENDING, by="forge", at=self.store.now(),
            reason="all protected checks passed" if technical_ok else f"incomplete evidence: {incomplete_reason}")
        if guard.flags:
            approval.gate_change_review = GateRecord(decision=Decision.PENDING, reason="; ".join(
                f"{f.kind}: {f.path}" for f in guard.flags))
        approval.visual_approval = GateRecord(decision=Decision.PENDING if self._needs_owner_visual(root)
                                              else Decision.NOT_REQUIRED)
        self.store.save_approval(approval)
        attempt.status = AttemptStatus.VERIFIED if technical_ok else AttemptStatus.CANDIDATE
        attempt.finished_at = self.store.now()
        self.store.save_attempt(attempt)
        reasons = []
        if not technical_ok:
            reasons.append("technical evidence incomplete: " + incomplete_reason)
        if guard.flags:
            reasons.append("gate/test changes require separate review")
        supervised = [o.name for o in outcomes if containment_of(o.details) == "supervised"]
        if supervised:  # generated code ran on the host account: the owner supervises, never auto-pass
            reasons.append("supervised mode: generated code ran without enforced containment "
                           f"({', '.join(supervised)})")
        if root.task_type == TaskType.ASSET:
            gen = self._asset_lane(root)
            reasons.append(gen.approval_reason(root) if gen is not None else
                           "owner approval of the catalogue pick required (check the renders, source page and "
                           "licence; licence labels can be wrong)")
        elif root.visual_review_required:
            reasons.append("owner visual approval required")
        if review_verdict in ("reject", "uncertain"):
            reasons.append(f"independent reviewer verdict: {review_verdict}")
        if reasons:
            return self._to(root, S.AWAITING_APPROVAL, "; ".join(reasons), candidate_hash=cand)
        return self._to(root, S.INTEGRATION_READY, "technical checks passed; within approved scope",
                        candidate_hash=cand)

    def _independent_review(self, root: RootTask, attempt: CandidateAttempt, diff: str,
                            outcomes: list[CheckOutcome]) -> Optional[str]:
        if not self.rt.reviewer_route or root.task_type != TaskType.CODE:
            return None
        reviewer = self.rt.providers.get(self.rt.reviewer_route)
        if reviewer is None:
            return None
        summary = "\n".join(f"{o.name}: {o.status.value} - {o.summary}" for o in outcomes)
        try:
            rr = reviewer.review(ReviewRequest(root, attempt.candidate_hash, diff, root.acceptance_cases, summary,
                                               f"{root.id}:{attempt.id}:review"))
        except Exception as e:
            self._event("review_failed", root, attempt, error=str(e))
            return "uncertain"
        if rr.cost_micros:
            try:
                self.budget.record_usage(attempt.reservation_id, rr.cost_micros, model=rr.usage.model,
                                         usage=rr.usage.to_dict())
            except BudgetError:
                pass
        self.store.add_evidence(Evidence(
            id=new_id("ev"), root_id=root.id, attempt_id=attempt.id, candidate_hash=attempt.candidate_hash,
            evidence_class=EvidenceClass.MODEL_REVIEW,
            status=EvidenceStatus.PASS if rr.verdict == "approve" else EvidenceStatus.INFO,
            name="independent-review", summary=f"reviewer verdict: {rr.verdict}",
            details={"findings": rr.findings, "cost_micros": rr.cost_micros,
                     "note": "model agreement is not proof of correctness; deterministic tests are the authority"}))
        return rr.verdict

    def _revalidate(self, root: RootTask, attempt: CandidateAttempt) -> RootTask:
        """Re-run protected checks for an already-approved candidate (no provider call)."""
        root = self._to(root, S.RUNNING, f"revalidating candidate {attempt.candidate_hash[:12]} (no new attempt)",
                        attempt_id=attempt.id)
        if not attempt.workspace_path or not Path(attempt.workspace_path).exists():
            path, branch = self.ws.create(attempt.id, attempt.candidate_hash)
            attempt.workspace_path, attempt.branch = str(path), branch
            self.store.save_attempt(attempt)
        root = self._to(root, S.VERIFYING, "revalidation")
        self.ws.restore_protected(attempt.workspace_path, attempt.base_commit, self.project.protected_paths)
        results = self._run_checks(root, attempt, root.verification_checks, Path(attempt.workspace_path),
                                   attempt.candidate_hash)
        outcomes = [oc for oc, _ in results]
        if any(oc.status == EvidenceStatus.FAIL for oc in outcomes):
            attempt.repair_instructions = self._repair_notes(outcomes)
            return self._fail_attempt(root, attempt, FailureCategory.TEST_FAILURE, "revalidation failed")
        approval = self.store.find_approval(root.id, attempt.candidate_hash)
        if any(oc.status != EvidenceStatus.PASS for oc in outcomes):
            return self._to(root, S.AWAITING_APPROVAL, "revalidation evidence incomplete")
        if self._needs_owner_visual(root) and (approval is None or approval.visual_approval.decision != Decision.APPROVED):
            return self._to(root, S.AWAITING_APPROVAL, "owner visual approval required")
        return self._to(root, S.INTEGRATION_READY, "revalidated; approvals for this exact candidate still apply")

    def _fail_attempt(self, root: RootTask, attempt: CandidateAttempt, category: FailureCategory,
                      reason: str) -> RootTask:
        attempt.status = AttemptStatus.FAILED
        attempt.failure_category = category
        attempt.finished_at = self.store.now()
        self.store.save_attempt(attempt)
        if attempt.reservation_id:
            self.budget.settle(attempt.reservation_id)
        self.leases.release(f"attempt:{attempt.id}", self.rt.worker_id)
        if attempt.workspace_path and Path(attempt.workspace_path).exists():
            self.ws.remove(attempt.workspace_path)  # branch keeps the candidate for review/retention
        root = self.store.get_root(root.id)
        if root.state == S.INTEGRATING or root.state in (S.RUNNING, S.VERIFYING, S.AWAITING_APPROVAL):
            root = self._to(root, S.RETRY_PENDING, f"{category.value}: {reason}", attempt_id=attempt.id)
        if self.attempts_remaining(root) <= 0:
            root = self._to(root, S.FAILED, f"attempt limit reached ({root.max_attempts} total); last: {reason}")
        return root

    # ================================================================ integration (single writer)
    def integrate(self, root: RootTask) -> RootTask:
        attempt = self.store.latest_attempt(root.id)
        if attempt is None or not attempt.candidate_hash:
            return self._to(root, S.BLOCKED, "no candidate to integrate")
        ok, reason, _ = self._dependency_status(root)
        if not ok:
            return self._to(root, S.BLOCKED, "revalidation before integration: " + reason)
        res_ok, res_reason = self._resource_status(root, root.integration_checks)
        if not res_ok:
            return self._to(root, S.BLOCKED, "integration resource unavailable: " + res_reason)
        keys = [f"integration:{self.project.id}", f"workspace:{self.rt.staging_dir}"]
        if not self.leases.acquire_all(keys, self.rt.worker_id, root_id=root.id, attempt_id=attempt.id):
            self._event("integration_waiting", root, attempt)
            return root
        try:
            root = self._to(root, S.INTEGRATING, f"staging {attempt.candidate_hash[:12]} on current accepted main")
            return self._integrate_body(root, attempt)
        finally:
            for k in keys:
                self.leases.release(k, self.rt.worker_id)

    def _integrate_body(self, root: RootTask, attempt: CandidateAttempt) -> RootTask:
        for restage in range(self.rt.max_restage):
            staged = self.writer.stage(attempt.candidate_hash, f"Forge: accept {root.ticket or root.id} "
                                                              f"({root.title})\n\nroot={root.id} attempt={attempt.id}")
            if not staged.ok:
                self.store.add_evidence(Evidence(
                    id=new_id("ev"), root_id=root.id, attempt_id=attempt.id, candidate_hash=attempt.candidate_hash,
                    evidence_class=EvidenceClass.INTEGRATION, status=EvidenceStatus.FAIL, name="integration-merge",
                    summary="merge conflict with current accepted main",
                    details={"base_main": staged.base_main, "detail": staged.detail}))
                attempt.repair_instructions = "Merge conflict against the current accepted main:\n" + staged.detail
                return self._fail_attempt(root, attempt, FailureCategory.OTHER, "merge conflict (repair under same root)")
            attempt.integrated_hash = staged.staged_sha
            self.store.save_attempt(attempt)
            names = list(dict.fromkeys(root.verification_checks + root.integration_checks))
            results = self._run_checks(root, attempt, names, staged.path, staged.staged_sha)
            outcomes = [oc for oc, _ in results]
            manifest_hash = self.artifacts.put_text(self.rt.manifest.model_dump_json()) if self.rt.manifest else None
            summary_status = (EvidenceStatus.FAIL if any(o.status == EvidenceStatus.FAIL for o in outcomes) else
                              EvidenceStatus.PASS if all(o.status == EvidenceStatus.PASS for o in outcomes) and outcomes
                              else EvidenceStatus.INCOMPLETE)
            if not outcomes and self._lane_budget_passed(root, attempt):
                summary_status = EvidenceStatus.PASS  # asset task: the lane's budget check is the technical gate
            self.store.add_evidence(Evidence(
                id=new_id("ev"), root_id=root.id, attempt_id=attempt.id, candidate_hash=staged.staged_sha,
                evidence_class=EvidenceClass.INTEGRATION, status=summary_status, name="integration-summary",
                summary=f"staged {staged.staged_sha[:12]} on main {staged.base_main[:12]}",
                details={"merge_commit": staged.staged_sha, "base_main": staged.base_main,
                         "fast_forward": staged.fast_forward, "toolchain_manifest": manifest_hash,
                         "checks": {o.name: o.status.value for o in outcomes}},
                artifact_refs=[manifest_hash] if manifest_hash else []))
            if summary_status == EvidenceStatus.FAIL:
                attempt.repair_instructions = "Integrated candidate failed protected checks:\n" + self._repair_notes(outcomes)
                return self._fail_attempt(root, attempt, FailureCategory.TEST_FAILURE, "integration checks failed")
            if summary_status == EvidenceStatus.INCOMPLETE:
                return self._to(root, S.AWAITING_APPROVAL, "integration evidence incomplete: " + "; ".join(
                    f"{o.name}: {o.summary}" for o in outcomes if o.status != EvidenceStatus.PASS)
                    + " - approve to re-run integration once available")
            if staged.staged_sha != attempt.candidate_hash and self._needs_owner_visual(root):
                apr = self.store.get_or_create_approval(root.id, attempt.id, staged.staged_sha)
                apr.technical_pass = GateRecord(decision=Decision.APPROVED, by="forge", at=self.store.now(),
                                                reason="integration checks passed")
                apr.visual_approval = GateRecord(decision=Decision.PENDING,
                                                 reason="integration changed bytes; prior approval does not cover them")
                self.store.save_approval(apr)
                self._event("integration_awaiting_approval", root, attempt, integrated_hash=staged.staged_sha,
                            base_main=staged.base_main)
                attempt.repair_instructions = ""
                self.store.save_attempt(attempt)
                self.store.set_setting(f"integration_base:{attempt.id}", staged.base_main)
                return self._to(root, S.AWAITING_APPROVAL, "integration changed the approved output; "
                                                           "approve the integrated hash", integrated_hash=staged.staged_sha)
            try:
                self.writer.promote(staged.staged_sha, staged.base_main)
            except BaseMoved as e:
                self._event("integration_base_moved", root, attempt, detail=str(e), restage=restage + 1)
                continue
            return self._accept(root, attempt, staged.staged_sha)
        return self._to(root, S.AWAITING_APPROVAL,
                        f"accepted main kept moving during integration ({self.rt.max_restage} restages)")

    def _accept(self, root: RootTask, attempt: CandidateAttempt, sha: str) -> RootTask:
        apr = self.store.get_or_create_approval(root.id, attempt.id, attempt.candidate_hash)
        apr.integrated_acceptance = GateRecord(decision=Decision.APPROVED, by="forge", at=self.store.now(),
                                               reason=f"integrated as {sha} after protected checks")
        self.store.save_approval(apr)
        attempt.status = AttemptStatus.INTEGRATED
        attempt.integrated_hash = sha
        self.store.save_attempt(attempt)
        root = self.store.update_root(self.store.get_root(root.id), accepted_artifact_hash=sha)
        root = self._to(root, S.ACCEPTED, f"integrated into {self.project.accepted_branch} at {sha[:12]}",
                        accepted_hash=sha)
        if attempt.workspace_path and Path(attempt.workspace_path).exists():
            self.ws.remove(attempt.workspace_path)
        self._mark_consumers_stale(root)
        return root

    # ================================================================ owner review actions
    def review(self, root_id: str, action: ReviewAction | str, *, reviewer: str = "owner",
               candidate_hash: str | None = None, correction: str = "",
               failure_category: FailureCategory | str | None = None,
               acknowledge_gate_change: bool = False, accept_incomplete: bool = False) -> RootTask:
        action = ReviewAction(action)
        if isinstance(failure_category, str) and failure_category:
            failure_category = FailureCategory(failure_category)
        root = self.store.get_root(root_id)
        attempt = self.store.latest_attempt(root_id)
        if is_terminal(root.state):
            raise ReviewError(f"{root_id} is {root.state.value}; terminal states are immutable")
        current_hash = None
        if attempt:
            current_hash = attempt.candidate_hash
        if root.state == S.AWAITING_APPROVAL:
            valid = {h for h in (attempt.candidate_hash, attempt.integrated_hash) if h}
            if candidate_hash is None:
                raise ReviewError("a review decision must name the exact candidate hash")
            if candidate_hash not in valid:
                raise ReviewError(f"candidate {candidate_hash[:12]} is not the current candidate; "
                                  "approvals never cover different bytes")
            current_hash = candidate_hash
        if action == ReviewAction.REPAIR and not (correction.strip() or failure_category):
            raise ReviewError("a repair request needs a specific correction or a failure category")
        self.store.add_review_decision(ReviewDecision(
            id=new_id("rd"), root_id=root_id, attempt_id=attempt.id if attempt else None,
            candidate_hash=current_hash, action=action, reviewer=reviewer, correction=correction,
            failure_category=failure_category or None, at=self.store.now()))
        self.store.add_evidence(Evidence(
            id=new_id("ev"), root_id=root_id, attempt_id=attempt.id if attempt else None, candidate_hash=current_hash,
            evidence_class=EvidenceClass.HUMAN, status=EvidenceStatus.INFO, name=f"owner-{action.value}",
            summary=f"{reviewer}: {action.value}" + (f" - {correction}" if correction else ""),
            details={"failure_category": failure_category.value if failure_category else None}))

        if action == ReviewAction.APPROVE:
            return self._approve(root, attempt, current_hash, reviewer, acknowledge_gate_change, accept_incomplete)
        if action == ReviewAction.REPAIR:
            if root.state != S.AWAITING_APPROVAL:
                raise ReviewError("repair can only be requested for a candidate awaiting approval")
            apr = self.store.get_or_create_approval(root.id, attempt.id, current_hash)
            apr.visual_approval = GateRecord(decision=Decision.REJECTED, by=reviewer, at=self.store.now(),
                                             reason=correction or failure_category.value)
            self.store.save_approval(apr)
            attempt.status = AttemptStatus.REJECTED
            attempt.repair_instructions = (f"Owner requested repair ({failure_category.value if failure_category else 'correction'}): "
                                           f"{correction}")
            self.store.save_attempt(attempt)
            root = self.store.update_root(root, held=False)
            root = self._to(root, S.RETRY_PENDING, "owner requested repair: " + (correction or failure_category.value))
            if self.attempts_remaining(root) <= 0:
                root = self._to(root, S.FAILED, f"attempt limit reached ({root.max_attempts} total, "
                                                "visual rejections included)")
            return root
        if action == ReviewAction.HOLD:
            if can_transition(root.state, S.PAUSED):
                return self._to(self.store.update_root(root, held=False), S.PAUSED, f"owner hold by {reviewer}")
            return self.store.update_root(root, held=True, state_reason=f"on hold by {reviewer}")
        if action == ReviewAction.RESUME:
            if root.state == S.PAUSED:
                return self._to(self.store.update_root(root, held=False), S.READY, f"resumed by {reviewer}")
            return self.store.update_root(root, held=False)
        if action == ReviewAction.CANCEL:
            return self.cancel(root_id, reviewer)
        raise ReviewError(f"unsupported action {action}")

    def _approve(self, root: RootTask, attempt: CandidateAttempt | None, current_hash: str | None, reviewer: str,
                 acknowledge_gate_change: bool, accept_incomplete: bool) -> RootTask:
        if root.state != S.AWAITING_APPROVAL or attempt is None:
            raise ReviewError(f"nothing to approve: {root.id} is {root.state.value}")
        apr = self.store.get_or_create_approval(root.id, attempt.id, current_hash)
        cand_apr = apr if current_hash == attempt.candidate_hash else (
            self.store.find_approval(root.id, attempt.candidate_hash) or apr)
        if cand_apr.gate_change_review.decision == Decision.PENDING:
            if not acknowledge_gate_change:
                raise ReviewError("this candidate changes tests/gates/thresholds; separate review must be "
                                  "acknowledged explicitly (acknowledge_gate_change)")
            cand_apr.gate_change_review = GateRecord(decision=Decision.APPROVED, by=reviewer, at=self.store.now(),
                                                     reason="gate change reviewed separately by owner")
        if apr.technical_pass.decision != Decision.APPROVED and cand_apr.technical_pass.decision != Decision.APPROVED:
            if not accept_incomplete:
                raise ReviewError("technical evidence is incomplete; approving requires accept_incomplete "
                                  "(recorded as an explicit owner override)")
            cand_apr.technical_pass = GateRecord(decision=Decision.APPROVED, by=reviewer, at=self.store.now(),
                                                 reason="owner accepted incomplete technical evidence")
        if self._needs_owner_visual(root):
            apr.visual_approval = GateRecord(decision=Decision.APPROVED, by=reviewer, at=self.store.now(),
                                             reason="owner approved this exact hash")
            if apr.id != cand_apr.id and cand_apr.visual_approval.decision == Decision.PENDING:
                cand_apr.visual_approval = GateRecord(decision=Decision.APPROVED, by=reviewer, at=self.store.now(),
                                                      reason="approved via integrated hash")
        self.store.save_approval(cand_apr)
        if apr.id != cand_apr.id:
            self.store.save_approval(apr)
        attempt.status = AttemptStatus.VERIFIED
        self.store.save_attempt(attempt)
        root = self.store.update_root(root, held=False)
        # Approval of an already-integrated (staged, verified) hash: promote if base unchanged.
        base = self.store.get_setting(f"integration_base:{attempt.id}")
        if attempt.integrated_hash and current_hash == attempt.integrated_hash and base:
            try:
                self.writer.promote(attempt.integrated_hash, base)
                return self._accept(root, attempt, attempt.integrated_hash)
            except BaseMoved as e:
                return self._to(root, S.INTEGRATION_READY, f"accepted main moved while approval was pending ({e}); "
                                                           "revalidating the new integrated result")
        return self._to(root, S.INTEGRATION_READY, f"approved by {reviewer}")

    # ================================================================ cancellation
    def cancel(self, root_id: str, by: str = "owner") -> RootTask:
        root = self.store.get_root(root_id)
        if root.state == S.RUNNING:
            return self._to(root, S.CANCEL_REQUESTED, f"cancel requested by {by}")
        if can_transition(root.state, S.CANCELLED):
            return self._to(root, S.CANCELLED, f"cancelled by {by}")
        raise ReviewError(f"{root_id} cannot be cancelled from {root.state.value}")

    def _complete_cancel(self, root: RootTask) -> RootTask:
        attempt = self.store.latest_attempt(root.id)
        outcome = "confirmed"
        if attempt and attempt.provider:
            prov = self.rt.providers.get(attempt.provider)
            job = self.store.get_provider_job(f"{root.id}:{attempt.id}:code")
            if prov and job and job["status"] in ("SUBMITTING", "SUBMITTED"):
                outcome = prov.cancel(job.get("provider_job_id") or job["idempotency_key"])
            if attempt.reservation_id:
                if outcome == "confirmed":
                    self.budget.settle(attempt.reservation_id)
                else:
                    self.budget.mark_charge_pending(attempt.reservation_id, "uncertain cancellation")
            attempt.status = AttemptStatus.ABANDONED
            self.store.save_attempt(attempt)
            self.leases.release(f"attempt:{attempt.id}", attempt.lease_holder or self.rt.worker_id)
        reason = "cancelled" if outcome == "confirmed" else "cancelled; provider completion/charge pending"
        return self._to(root, S.CANCELLED, reason)

    # ================================================================ recovery
    def recover(self) -> list[str]:
        """Restart recovery: jobs enter a recovery check instead of restarting blindly."""
        actions = []
        for root in self.store.list_roots(self.project.id, [S.RUNNING, S.VERIFYING, S.INTEGRATING]):
            attempt = self.store.latest_attempt(root.id)
            if attempt is None:
                continue
            lease = self.leases.holder(f"attempt:{attempt.id}")
            if root.state == S.RUNNING:
                if lease and lease["holder"] != self.rt.worker_id and not self.leases.is_expired(
                        f"attempt:{attempt.id}") and _holder_alive(lease["holder"]):
                    continue  # another live worker owns it
                if lease and lease["holder"] == self.rt.worker_id:
                    continue  # we are running it right now
            self._event("recovery_check", root, attempt, state=root.state.value,
                        previous_holder=lease["holder"] if lease else None)
            actions.append(f"{root.ticket or root.id}: recovery check ({root.state.value})")
            if not self.leases.acquire(f"attempt:{attempt.id}", self.rt.worker_id, root_id=root.id,
                                       attempt_id=attempt.id, ttl=self.rt.lease_ttl_s,
                                       force=bool(lease and not _holder_alive(lease["holder"]))):
                continue
            attempt.lease_holder = self.rt.worker_id
            self.store.save_attempt(attempt)
            if root.state == S.RUNNING:
                if attempt.candidate_hash:
                    self._to(root, S.VERIFYING, "recovered: candidate already committed")
                    self.verify(self.store.get_root(root.id), attempt)
                    continue
                if root.task_type == TaskType.ASSET and self._asset_lane(root) is not None:
                    self._asset_lane(root).recover(root, attempt)  # jobs are durable; resume re-polls them
                    continue
                if root.task_type == TaskType.ASSET:
                    # The lane is not resumable mid-run: a judge call may or may not have been charged.
                    job = self.store.get_provider_job(f"{root.id}:{attempt.id}:judge")
                    charged = bool(job and job["status"] == "COMPLETED")
                    if attempt.reservation_id and not charged:
                        self.budget.mark_charge_pending(attempt.reservation_id, "catalogue lane interrupted")
                    self._end_asset_attempt(
                        root, attempt, S.PAUSED,
                        "recovered: catalogue lane was interrupted" + ("" if charged else
                                                                        "; provider completion/charge pending"),
                        settle=charged)
                    continue
                provider = self.rt.providers.get(attempt.provider or "")
                if provider is None:
                    self._charge_pending(root, attempt, f"provider route {attempt.provider} unavailable after restart")
                    continue
                if not attempt.workspace_path or not Path(attempt.workspace_path).exists():
                    path, branch = self.ws.create(attempt.id, attempt.base_commit)
                    attempt.workspace_path, attempt.branch = str(path), branch
                    self.store.save_attempt(attempt)
                prev = [a for a in self.quality_attempts(root.id) if a.number < attempt.number]
                req = self._coding_request(root, attempt, prev[-1] if prev else None, Path(attempt.workspace_path))
                job = self.store.get_provider_job(req.idempotency_key)
                if job and job["status"] == "COMPLETED":
                    rec = provider.reconcile(req.idempotency_key)
                    if rec.result is not None:
                        self._ingest_result(root, attempt, rec.result, req.idempotency_key)
                        continue
                self._run_provider(root, attempt, provider, req, recovered=True)
            elif root.state == S.VERIFYING:
                if not attempt.workspace_path or not Path(attempt.workspace_path).exists():
                    path, branch = self.ws.create(attempt.id, attempt.candidate_hash)
                    attempt.workspace_path = str(path)
                    self.store.save_attempt(attempt)
                self.verify(root, attempt)
            elif root.state == S.INTEGRATING:
                try:
                    main = self.writer.current_main()
                except GitError:
                    main = None
                if attempt.integrated_hash and main == attempt.integrated_hash:
                    self._accept(root, attempt, attempt.integrated_hash)  # promotion happened before the crash
                else:
                    self._integrate_body(root, attempt)
        return actions

    # ================================================================ reporting
    def status(self) -> dict:
        roots = self.store.list_roots(self.project.id)
        by_state: dict[str, int] = {}
        for r in roots:
            by_state[r.state.value] = by_state.get(r.state.value, 0) + 1
        return {"project": self.project.id, "dispatch_stopped": self.budget.dispatch_stopped(),
                "by_state": by_state, "roots": len(roots)}
