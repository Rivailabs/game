"""Active-work timeout watchdog.

Every root task carries ``active_work_timeout_s`` (the plan's "maximum active duration"). R1 only
recorded it; this module enforces it in three places:

1. :func:`call_with_deadline` runs a blocking provider call in a worker thread and, when the
   deadline passes, asks the provider to cancel and raises :class:`ActiveWorkTimeout` in the
   scheduler. The abandoned call's charge is unknown, so the reservation becomes
   "provider completion/charge pending" (never silently released).
2. :func:`run_check_with_deadline` arms a timer that kills a sandboxed check's process group when
   the attempt's deadline passes (checks also keep their own per-check timeouts).
3. :func:`enforce_active_work_timeouts` is a scheduler sweep (run every ``tick``) for attempts that
   are still RUNNING/VERIFYING past their deadline - a provider job that keeps reporting
   "running", or a worker that died mid-attempt. It cancels the provider job where possible and
   moves the root to PAUSED (RUNNING) or RETRY_PENDING/FAILED (VERIFYING), recording the attempt
   as a failed quality attempt so the attempt limit still applies.
"""

from __future__ import annotations

import threading
from typing import TYPE_CHECKING, Any, Callable, Optional

from ..checks.base import Check, CheckContext, CheckOutcome
from ..models import AttemptStatus, EvidenceStatus, FailureCategory, RootTask, TaskState
from ..models import CandidateAttempt

if TYPE_CHECKING:  # pragma: no cover
    from ..orchestrator import Orchestrator

S = TaskState


class ActiveWorkTimeout(Exception):
    """The root's active-work timeout passed while work was in progress."""


def attempt_deadline(root: RootTask, attempt: CandidateAttempt) -> float:
    return float(attempt.started_at) + float(root.active_work_timeout_s)


def remaining_s(root: RootTask, attempt: CandidateAttempt, now: float) -> float:
    return attempt_deadline(root, attempt) - now


def call_with_deadline(fn: Callable[[], Any], timeout_s: float, *, on_timeout: Optional[Callable[[], None]] = None,
                       what: str = "work") -> Any:
    """Run ``fn`` and return its result, or raise :class:`ActiveWorkTimeout` after ``timeout_s`` seconds.

    The call runs in a daemon thread; on timeout ``on_timeout`` is invoked (cancel the provider job,
    kill processes) and the thread is abandoned - Python cannot kill a thread, so whatever it later
    returns is discarded and its charge stays pending until reconciled.
    """
    if timeout_s <= 0:
        if on_timeout:
            on_timeout()
        raise ActiveWorkTimeout(f"active-work timeout already passed before {what} started")
    box: dict[str, Any] = {}
    done = threading.Event()

    def target() -> None:
        try:
            box["result"] = fn()
        except BaseException as e:  # re-raised in the caller's thread
            box["error"] = e
        finally:
            done.set()

    t = threading.Thread(target=target, daemon=True, name="forge-deadline")
    t.start()
    if not done.wait(timeout_s):
        if on_timeout:
            try:
                on_timeout()
            except Exception:
                pass
        raise ActiveWorkTimeout(f"active-work timeout: {what} still running after {timeout_s:.0f}s")
    if "error" in box:
        raise box["error"]
    return box.get("result")


def run_check_with_deadline(chk: Check, workdir, ctx: CheckContext, remaining: Optional[float]) -> CheckOutcome:
    """Run a check; kill its sandboxed processes when the attempt deadline passes."""
    if remaining is not None and remaining <= 0:
        return CheckOutcome(chk.name, chk.evidence_class, EvidenceStatus.INCOMPLETE,
                            "active-work timeout reached before this check could run")
    cancel = getattr(chk, "cancel", None)
    timer = None
    if remaining is not None and isinstance(cancel, threading.Event):
        timer = threading.Timer(remaining, cancel.set)
        timer.daemon = True
        timer.start()
    try:
        oc = chk.run(workdir, ctx)
    finally:
        if timer is not None:
            timer.cancel()
    if isinstance(cancel, threading.Event) and cancel.is_set():
        oc.status = EvidenceStatus.FAIL
        oc.summary = f"killed by the active-work timeout watchdog: {oc.summary}"
        oc.details = {**oc.details, "active_work_timeout": True}
    return oc


def enforce_active_work_timeouts(orch: "Orchestrator") -> list[str]:
    """Scheduler sweep. Returns human-readable actions (as ``tick`` does)."""
    from ..orchestrator import _holder_alive  # local import: orchestrator imports this module

    actions: list[str] = []
    now = orch.store.now()
    for root in orch.store.list_roots(orch.project.id, [S.RUNNING, S.VERIFYING]):
        attempt = orch.store.latest_attempt(root.id)
        if attempt is None or attempt.status not in (AttemptStatus.ACTIVE, AttemptStatus.CANDIDATE):
            continue
        if remaining_s(root, attempt, now) > 0:
            continue
        lease = orch.leases.holder(f"attempt:{attempt.id}")
        if lease and lease["holder"] != orch.rt.worker_id and not orch.leases.is_expired(f"attempt:{attempt.id}") \
                and _holder_alive(lease["holder"]):
            orch._event("active_work_timeout_pending", root, attempt, holder=lease["holder"],
                        note="another live worker holds the attempt; it enforces its own deadline")
            continue
        over = now - attempt_deadline(root, attempt)
        reason = (f"active-work timeout: attempt {attempt.number} exceeded {root.active_work_timeout_s}s "
                  f"(by {over:.0f}s)")
        orch._event("active_work_timeout", root, attempt, state=root.state.value, timeout_s=root.active_work_timeout_s,
                    overrun_s=round(over, 1))
        actions.append(f"{root.ticket or root.id}: {reason}")
        if root.state == S.RUNNING:
            _cancel_running(orch, root, attempt, reason)
        else:
            prefix = attempt.repair_instructions + "\n" if attempt.repair_instructions else ""
            attempt.repair_instructions = prefix + ("Previous attempt exceeded the active-work timeout during "
                                                    "verification.")
            orch.store.save_attempt(attempt)
            orch._fail_attempt(root, attempt, FailureCategory.OTHER, reason)
    return actions


def _cancel_running(orch: "Orchestrator", root: RootTask, attempt: CandidateAttempt, reason: str) -> None:
    outcomes = []
    provider = orch.rt.providers.get(attempt.provider or "")
    for job in orch.store.provider_jobs_for_attempt(attempt.id):
        if job["status"] in ("COMPLETED", "FAILED", "CANCELLED"):
            continue
        outcome = "uncertain"
        if provider is not None and job.get("provider_job_id"):
            try:
                outcome = provider.cancel(job["provider_job_id"])
            except Exception:
                outcome = "uncertain"
        outcomes.append(outcome)
        orch.store.update_provider_job(job["idempotency_key"],
                                       status="CANCELLED" if outcome == "confirmed" else "UNKNOWN")
    if attempt.reservation_id:
        if outcomes and all(o == "confirmed" for o in outcomes) or not outcomes:
            orch.budget.settle(attempt.reservation_id)
        else:
            orch.budget.mark_charge_pending(attempt.reservation_id, "active-work timeout; cancellation uncertain")
            reason += "; provider completion/charge pending"
    attempt.status = AttemptStatus.FAILED  # a timed-out attempt consumed a quality attempt
    attempt.failure_category = FailureCategory.OTHER
    attempt.finished_at = orch.store.now()
    attempt.repair_instructions = "Previous attempt exceeded the active-work timeout; deliver a smaller change."
    orch.store.save_attempt(attempt)
    orch.leases.release(f"attempt:{attempt.id}", attempt.lease_holder or orch.rt.worker_id)
    orch.leases.release(f"attempt:{attempt.id}", orch.rt.worker_id)
    orch._to(orch.store.get_root(root.id), S.PAUSED, reason)
