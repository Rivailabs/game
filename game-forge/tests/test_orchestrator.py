"""End-to-end orchestration with the deterministic FakeProvider on temporary git repos."""

import os
import subprocess

import pytest

from forge.budget import BudgetLedger, root_scope
from forge.models import AttemptStatus, Decision, DependencyRef, EvidenceClass, ResourceProfile, ReviewAction
from forge.models import TaskState as S
from forge.orchestrator import ReviewError
from forge.providers import FakeBehaviour, FakeProvider, SimulatedCrash
from forge.providers.base import ReconcileResult

from .conftest import BAD_MUL, CALC, GOOD_MUL, git

TEST_MUL_PATH = "game/tests/test_mul.py"


def approve_and_run(env, root, max_ticks=50):
    env.orch.approve_task(root.id)
    env.orch.run_until_idle(max_ticks)
    return env.store.get_root(root.id)


# ---------------------------------------------------------------- happy path

def test_e2e_draft_to_accepted_with_evidence_and_settled_ledger(env):
    t = env.task()
    assert t.state == S.DRAFT
    r = approve_and_run(env, t)
    assert r.state == S.ACCEPTED
    assert env.states(t.id) == ["DRAFT", "APPROVED", "READY", "RUNNING", "VERIFYING", "INTEGRATION_READY",
                                "INTEGRATING", "ACCEPTED"]
    # accepted main holds the change; the user's working copy branch is untouched
    assert "def mul" in env.accepted_file("game/src/calc.py")
    assert r.accepted_artifact_hash == git(env.repo, "rev-parse", "forge/accepted")
    assert "def mul" not in git(env.repo, "show", "main:game/src/calc.py")
    # evidence: static guard, rules checks (candidate + integration), integration summary, human approval
    ev = env.store.list_evidence(t.id)
    names = [e.name for e in ev]
    assert {"diff-guard", "add", "mul", "integration-summary", "builder-report", "task-approval"} <= set(names)
    assert all(e.status.value == "PASS" for e in ev if e.name in ("add", "mul", "integration-summary"))
    assert any(e.evidence_class == EvidenceClass.INTEGRATION for e in ev)
    # logs stored content-addressed
    log_ref = next(e for e in ev if e.name == "mul").details["logs"]["stderr"]
    assert "OK" in env.orch.artifacts.get_text(log_ref)
    # ledger: reserved upper bound, settled actual, remainder released
    res = BudgetLedger(env.store).reservations(root_id=t.id)
    assert len(res) == 1 and res[0]["status"] == "SETTLED"
    assert res[0]["amount_micros"] == 50_000 and res[0]["settled_micros"] == 10_000
    s = BudgetLedger(env.store).summary(root_scope(t.id))
    assert (s.settled, s.reserved, s.available) == (10_000, 0, 990_000)
    # approvals: distinct fields on the exact candidate hash
    apr = env.store.list_approvals(t.id)[0]
    assert apr.technical_pass.decision == Decision.APPROVED
    assert apr.visual_approval.decision == Decision.NOT_REQUIRED
    assert apr.integrated_acceptance.decision == Decision.APPROVED
    assert apr.release_approval.decision == Decision.PENDING
    # provider job persisted before/after the external call
    jobs = env.store.provider_jobs_for_attempt(env.store.latest_attempt(t.id).id)
    assert jobs[0]["status"] == "COMPLETED"
    types = [e["type"] for e in env.store.events(root_id=t.id)]
    assert types.index("provider_job_submitting") < types.index("provider_job_updated")
    # worktree cleaned up, candidate branch retained
    att = env.store.latest_attempt(t.id)
    assert not os.path.exists(att.workspace_path)
    assert git(env.repo, "rev-parse", att.branch) == att.candidate_hash


def test_approval_requires_complete_task_contract(env):
    t = env.task(acceptance_cases=[], reservation_ceiling_micros=None)
    r = env.orch.approve_task(t.id)
    assert r.state == S.NEEDS_INPUT
    assert "acceptance" in r.state_reason and "ceiling" in r.state_reason


# ---------------------------------------------------------------- failures and attempt limits

def test_broken_candidate_retry_pending_then_failed(make_env):
    env = make_env(provider=FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": BAD_MUL})}))
    t = env.task()
    r = approve_and_run(env, t)
    assert r.state == S.FAILED
    assert "attempt limit" in r.state_reason
    atts = env.store.list_attempts(t.id)
    assert [a.number for a in atts] == [1, 2, 3]
    assert all(a.status == AttemptStatus.FAILED for a in atts)
    seq = env.states(t.id)
    assert seq.count("RETRY_PENDING") == 3 and seq[-2:] == ["RETRY_PENDING", "FAILED"]
    assert seq.count("RUNNING") == 3
    # failing rule detected with structured evidence; repair notes fed to the next attempt
    fails = [e for e in env.store.list_evidence(t.id) if e.name == "mul" and e.status.value == "FAIL"]
    assert len(fails) == 3
    assert "mul" in atts[0].repair_instructions
    assert BudgetLedger(env.store).summary(root_scope(t.id)).settled == 30_000
    assert git(env.repo, "rev-parse", "forge/accepted") == git(env.repo, "rev-parse", "main")  # nothing integrated


def test_repair_succeeds_on_second_attempt(make_env):
    env = make_env(provider=FakeProvider({1: FakeBehaviour(files={"game/src/calc.py": BAD_MUL}),
                                          2: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL})}))
    t = env.task()
    r = approve_and_run(env, t)
    assert r.state == S.ACCEPTED
    assert len(env.store.list_attempts(t.id)) == 2
    assert "RETRY_PENDING" in env.states(t.id)


def test_max_attempts_is_configurable(make_env):
    env = make_env(provider=FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": BAD_MUL})}))
    t = env.task(max_attempts=1)
    assert approve_and_run(env, t).state == S.FAILED
    assert len(env.store.list_attempts(t.id)) == 1


def test_no_change_candidate_is_a_failed_attempt(make_env):
    env = make_env(provider=FakeProvider({0: FakeBehaviour(files={})}))
    r = approve_and_run(env, env.task())
    assert r.state == S.FAILED


def test_scope_violation_fails_attempt(make_env):
    env = make_env(provider=FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL,
                                                                  "ops/deploy.sh": "rm -rf /"},
                                                           ignore_permitted_paths=True)}))
    t = env.task(max_attempts=1)
    r = approve_and_run(env, t)
    assert r.state == S.FAILED
    assert env.store.latest_attempt(t.id).failure_category.value == "scope_violation"
    guard = next(e for e in env.store.list_evidence(t.id) if e.name == "diff-guard")
    assert guard.details["scope_violations"] == ["ops/deploy.sh"]


# ---------------------------------------------------------------- visual review

def test_visual_rejection_consumes_attempt_allowance(make_env):
    env = make_env()
    t = env.task(visual_review_required=True)
    env.orch.approve_task(t.id)
    for n in range(1, 4):
        env.orch.run_until_idle()
        r = env.store.get_root(t.id)
        assert r.state == S.AWAITING_APPROVAL, n
        cand = env.store.latest_attempt(t.id).candidate_hash
        with pytest.raises(ReviewError):  # repair needs a correction or category
            env.orch.review(t.id, ReviewAction.REPAIR, candidate_hash=cand)
        r = env.orch.review(t.id, ReviewAction.REPAIR, candidate_hash=cand, correction="hand does not grip bow",
                            failure_category="visual_mismatch")
    assert r.state == S.FAILED
    assert len(env.store.list_attempts(t.id)) == 3
    rejected = [a for a in env.store.list_approvals(t.id) if a.visual_approval.decision == Decision.REJECTED]
    assert len(rejected) == 3
    assert env.store.list_attempts(t.id)[0].repair_instructions.endswith("hand does not grip bow")


def test_visual_approval_binds_to_exact_hash(make_env):
    env = make_env()
    t = env.task(visual_review_required=True)
    approve_and_run(env, t)
    att = env.store.latest_attempt(t.id)
    with pytest.raises(ReviewError, match="exact candidate"):
        env.orch.review(t.id, ReviewAction.APPROVE)
    with pytest.raises(ReviewError, match="not the current candidate"):
        env.orch.review(t.id, ReviewAction.APPROVE, candidate_hash="0" * 40)
    r = env.orch.review(t.id, ReviewAction.APPROVE, candidate_hash=att.candidate_hash)
    assert r.state == S.INTEGRATION_READY
    env.orch.run_until_idle()
    r = env.store.get_root(t.id)
    assert r.state == S.ACCEPTED
    apr = env.store.find_approval(t.id, att.candidate_hash)
    assert apr.visual_approval.decision == Decision.APPROVED and apr.visual_approval.by == "owner"
    decisions = env.store.list_review_decisions(t.id)
    assert decisions[-1].candidate_hash == att.candidate_hash


def test_hold_and_resume(make_env):
    env = make_env()
    t = env.task(visual_review_required=True)
    approve_and_run(env, t)
    cand = env.store.latest_attempt(t.id).candidate_hash
    r = env.orch.review(t.id, ReviewAction.HOLD, candidate_hash=cand)
    assert r.state == S.AWAITING_APPROVAL and r.held
    r = env.orch.review(t.id, ReviewAction.RESUME, candidate_hash=cand)
    assert not r.held
    t2 = env.task("2")
    env.orch.approve_task(t2.id)
    r2 = env.orch.review(t2.id, ReviewAction.HOLD)
    assert r2.state == S.PAUSED
    env.orch.run_until_idle()
    assert env.store.get_root(t2.id).state == S.PAUSED
    env.orch.review(t2.id, ReviewAction.RESUME)
    env.orch.run_until_idle()
    assert env.store.get_root(t2.id).state == S.ACCEPTED


# ---------------------------------------------------------------- diff guard in the loop

def test_candidate_deleting_test_cannot_auto_pass(make_env):
    env = make_env(provider=FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": CALC + "\n"},
                                                           delete=[TEST_MUL_PATH])}))
    t = env.task(permitted_paths=["game/**"], verification_checks=["add"])
    r = approve_and_run(env, t)
    assert r.state == S.AWAITING_APPROVAL
    att = env.store.latest_attempt(t.id)
    assert "test_file_deleted" in att.gate_change_flags
    apr = env.store.find_approval(t.id, att.candidate_hash)
    assert apr.gate_change_review.decision == Decision.PENDING
    with pytest.raises(ReviewError, match="separate review"):
        env.orch.review(t.id, ReviewAction.APPROVE, candidate_hash=att.candidate_hash)
    r = env.orch.review(t.id, ReviewAction.APPROVE, candidate_hash=att.candidate_hash, acknowledge_gate_change=True)
    assert r.state == S.INTEGRATION_READY
    assert env.store.find_approval(t.id, att.candidate_hash).gate_change_review.decision == Decision.APPROVED


def test_protected_tests_are_restored_before_checks(make_env):
    weakened = "import unittest\nclass MulTest(unittest.TestCase):\n    def test_mul(self):\n        pass\n"
    env = make_env(provider=FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": BAD_MUL,
                                                                  TEST_MUL_PATH: weakened})}))
    t = env.task(permitted_paths=["game/**"], max_attempts=1)
    r = approve_and_run(env, t)
    # the weakened test never ran: the protected copy failed the bad candidate
    assert r.state == S.FAILED
    assert any(e["type"] == "protected_paths_restored" for e in env.store.events(root_id=t.id))


def test_incomplete_evidence_requires_explicit_override(make_env):
    env = make_env()
    t = env.task(verification_checks=[])
    r = approve_and_run(env, t)
    assert r.state == S.AWAITING_APPROVAL and "incomplete" in r.state_reason
    cand = env.store.latest_attempt(t.id).candidate_hash
    with pytest.raises(ReviewError, match="incomplete"):
        env.orch.review(t.id, ReviewAction.APPROVE, candidate_hash=cand)
    assert env.orch.review(t.id, ReviewAction.APPROVE, candidate_hash=cand,
                           accept_incomplete=True).state == S.INTEGRATION_READY


def test_independent_reviewer_rejection_routes_to_owner(make_env):
    prov = FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL})}, review_verdict="reject")
    env = make_env(provider=prov, reviewer=True)
    t = env.task()
    r = approve_and_run(env, t)
    assert r.state == S.AWAITING_APPROVAL and "reviewer" in r.state_reason
    rev = next(e for e in env.store.list_evidence(t.id) if e.name == "independent-review")
    assert rev.details["findings"][0]["acceptance_case"] == "AC1"
    # reviewer cost charged to the same root reservation
    assert BudgetLedger(env.store).summary(root_scope(t.id)).settled == 12_000


# ---------------------------------------------------------------- budget, kill switch, policy

def test_kill_switch_stops_new_dispatch(env):
    t = env.task()
    env.orch.approve_task(t.id)
    BudgetLedger(env.store).stop_dispatch(reason="test")
    env.orch.run_until_idle()
    assert env.store.get_root(t.id).state == S.READY
    assert env.provider.submissions == []
    assert BudgetLedger(env.store).reservations(root_id=t.id) == []
    BudgetLedger(env.store).resume_dispatch()
    env.orch.run_until_idle()
    assert env.store.get_root(t.id).state == S.ACCEPTED


def test_budget_exhaustion_pauses_without_raising_caps(make_env):
    env = make_env(milestone_cap=30_000)  # below the 50k reservation upper bound
    t = env.task()
    r = approve_and_run(env, t)
    assert r.state == S.PAUSED and "budget" in r.state_reason
    assert env.provider.submissions == []
    assert BudgetLedger(env.store).get_cap("milestone:m1") == 30_000


def test_cap_stops_further_dispatch_after_spend(make_env):
    env = make_env(provider=FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": BAD_MUL},
                                                           cost_micros=40_000)}))
    t = env.task(reservation_ceiling_micros=100_000)  # attempt 2 needs 40k settled + 50k reserved; attempt 3 cannot fit
    r = approve_and_run(env, t)
    assert r.state == S.PAUSED and "budget" in r.state_reason
    assert len(env.store.list_attempts(t.id)) == 2


def test_unknown_ceiling_is_blocked_in_unattended_mode(make_env):
    env = make_env(provider=FakeProvider(ceiling_micros=None))
    r = approve_and_run(env, env.task())
    assert r.state == S.BLOCKED and "ceiling" in r.state_reason


def test_policy_refuses_unlisted_vendor(make_env):
    env = make_env(provider=FakeProvider(vendor="unknown-vendor"))
    r = approve_and_run(env, env.task())
    assert r.state == S.BLOCKED and "vendor" in r.state_reason


def test_supervised_cli_route_never_unattended(make_env):
    from forge.providers.cli_connector import OfficialCLIConnector

    env = make_env()
    env.orch.rt.providers["cli"] = OfficialCLIConnector()
    t = env.task(permitted_routes=["cli"])
    r = approve_and_run(env, t)
    assert r.state == S.BLOCKED and "supervised" in r.state_reason


# ---------------------------------------------------------------- transport handling

def test_bounded_transport_retries_with_backoff_count_against_root(make_env):
    prov = FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL}, transport_failures=2,
                                          charge_on_transport_failure=1_000)})
    env = make_env(provider=prov)
    t = env.task()
    r = approve_and_run(env, t)
    assert r.state == S.ACCEPTED
    assert env.clock.slept == [1.0, 2.0]  # exponential backoff
    att = env.store.latest_attempt(t.id)
    assert att.transport_retries == 2 and att.number == 1  # transport handling is not a quality attempt
    assert BudgetLedger(env.store).summary(root_scope(t.id)).settled == 12_000
    assert len(prov.submissions) == 1


def test_transport_exhaustion_pauses_and_does_not_consume_quality_attempt(make_env):
    prov = FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL}, transport_failures=10)})
    env = make_env(provider=prov)
    t = env.task()
    r = approve_and_run(env, t)
    assert r.state == S.PAUSED and "transport" in r.state_reason
    assert env.store.latest_attempt(t.id).status == AttemptStatus.ABANDONED
    assert env.orch.attempts_remaining(env.store.get_root(t.id)) == 3


# ---------------------------------------------------------------- restart recovery

def test_restart_recovery_reconciles_before_resubmitting(make_env):
    prov = FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL}, crash_after_submit=True)})
    env = make_env(provider=prov)
    t = env.task()
    env.orch.approve_task(t.id)
    with pytest.raises(SimulatedCrash):
        env.orch.run_until_idle()
    assert env.store.get_root(t.id).state == S.RUNNING
    att = env.store.latest_attempt(t.id)
    assert env.store.get_provider_job(f"{t.id}:{att.id}:code")["status"] == "SUBMITTING"
    # a new worker process starts; the old lease has not expired yet -> no takeover
    orch2 = env.make_orch("w2")
    assert orch2.recover() == []
    env.clock.advance(301)
    log = orch2.run_until_idle()
    assert any("recovery check" in line for line in log)
    r = env.store.get_root(t.id)
    assert r.state == S.ACCEPTED
    assert len(prov.submissions) == 1  # reconciled by idempotency key, not resubmitted
    assert BudgetLedger(env.store).summary(root_scope(t.id)).settled == 10_000
    assert env.store.events(root_id=t.id, type_="recovery_check")


def test_restart_recovery_resubmits_only_when_provider_has_no_job(make_env):
    prov = FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL}, crash_after_submit=True)})
    env = make_env(provider=prov)
    t = env.task()
    env.orch.approve_task(t.id)
    with pytest.raises(SimulatedCrash):
        env.orch.run_until_idle()
    prov.jobs.clear()  # provider never actually received it
    env.clock.advance(301)
    env.make_orch("w2").run_until_idle()
    assert env.store.get_root(t.id).state == S.ACCEPTED
    assert len(prov.submissions) == 2


def test_restart_with_unknown_provider_status_marks_charge_pending(make_env):
    class Unknowable(FakeProvider):
        def reconcile(self, key):
            return ReconcileResult("unknown", detail="no job lookup")

    prov = Unknowable({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL}, crash_after_submit=True)})
    env = make_env(provider=prov)
    t = env.task()
    env.orch.approve_task(t.id)
    with pytest.raises(SimulatedCrash):
        env.orch.run_until_idle()
    env.clock.advance(301)
    env.make_orch("w2").run_until_idle()
    r = env.store.get_root(t.id)
    assert r.state == S.PAUSED and "charge pending" in r.state_reason
    res = BudgetLedger(env.store).reservations(root_id=t.id)[0]
    assert res["status"] == "CHARGE_PENDING"


def test_dead_local_process_lease_is_recovered_immediately(make_env):
    import socket

    prov = FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL}, crash_after_submit=True)})
    p = subprocess.Popen(["true"])
    p.wait()
    dead_pid = p.pid  # reaped: no such process any more
    env = make_env(provider=prov, worker_id=f"{socket.gethostname()}|{dead_pid}")
    t = env.task()
    env.orch.approve_task(t.id)
    with pytest.raises(SimulatedCrash):
        env.orch.run_until_idle()
    env.make_orch(f"{socket.gethostname()}|{os.getpid()}").run_until_idle()  # no clock advance needed
    assert env.store.get_root(t.id).state == S.ACCEPTED


def test_cancel_running_task(make_env):
    prov = FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL}, crash_after_submit=True)})
    env = make_env(provider=prov)
    t = env.task()
    env.orch.approve_task(t.id)
    with pytest.raises(SimulatedCrash):
        env.orch.run_until_idle()
    r = env.orch.cancel(t.id)
    assert r.state == S.CANCEL_REQUESTED
    env.orch.tick()
    assert env.store.get_root(t.id).state == S.CANCELLED
    assert prov.cancelled


# ---------------------------------------------------------------- dependencies

def test_dependency_readiness_and_pinning(make_env):
    env = make_env(provider=FakeProvider(per_ticket_script()))
    a = env.task("1")
    b = env.task("2", dependencies=[DependencyRef(root_id=a.id)], verification_checks=["add"],
                 description="second task")
    env.orch.approve_task(b.id)
    env.orch.tick()
    rb = env.store.get_root(b.id)
    assert rb.state == S.BLOCKED and "waiting for dependency" in rb.state_reason
    env.orch.approve_task(a.id)
    env.orch.run_until_idle()
    ra, rb = env.store.get_root(a.id), env.store.get_root(b.id)
    assert ra.state == S.ACCEPTED
    assert rb.dependencies[0].artifact_hash == ra.accepted_artifact_hash  # pinned
    assert rb.state == S.ACCEPTED


def per_ticket_script(version=""):
    def script(req):
        if req.root.ticket == "1":
            return FakeBehaviour(files={"game/src/calc.py": GOOD_MUL + version})
        return FakeBehaviour(files={f"game/src/extra_{req.root.ticket}.py": f"X = {req.root.ticket}\n"})
    return script


def test_replacing_dependency_marks_consumers_stale(make_env):
    env = make_env(provider=FakeProvider(per_ticket_script()))
    a = env.task("1")
    b = env.task("2", dependencies=[DependencyRef(root_id=a.id)], verification_checks=["add"])
    c = env.task("3", dependencies=[DependencyRef(root_id=a.id)], verification_checks=["add"])
    env.orch.approve_task(a.id)
    env.orch.approve_task(b.id)
    env.orch.run_until_idle()
    assert env.store.get_root(b.id).state == S.ACCEPTED
    env.orch.approve_task(c.id)
    env.orch.tick()  # pins dependency hash
    env.orch.review(c.id, ReviewAction.HOLD)
    assert env.store.get_root(c.id).state == S.PAUSED
    old_hash = env.store.get_root(a.id).accepted_artifact_hash
    # owner changes the spec of A: new linked root, then accepted with different bytes
    env.provider.script = per_ticket_script("\n# v2\n")
    a2 = env.orch.revise_task(a.id, title="mul v2")
    assert a2.previous_root_id == a.id and a2.previous_cost_micros == 10_000
    env.orch.approve_task(a2.id)
    env.orch.run_until_idle()
    ra2 = env.store.get_root(a2.id)
    assert ra2.state == S.ACCEPTED and ra2.accepted_artifact_hash != old_hash
    # accepted consumer keeps its acceptance record but is marked stale
    assert env.store.get_root(b.id).state == S.ACCEPTED
    assert env.store.stale_marks(b.id)
    # paused consumer is blocked as stale when it resumes
    env.orch.review(c.id, ReviewAction.RESUME)
    env.orch.tick()
    rc = env.store.get_root(c.id)
    assert rc.state == S.BLOCKED and "stale" in rc.state_reason
    env.orch.acknowledge_dependency_change(c.id)
    env.orch.run_until_idle()
    rc = env.store.get_root(c.id)
    assert rc.dependencies[0].artifact_hash == ra2.accepted_artifact_hash
    assert rc.state == S.ACCEPTED


# ---------------------------------------------------------------- integration writer races

def test_base_moved_during_integration_is_restaged(make_env):
    env = make_env()
    t = env.task()
    env.orch.approve_task(t.id)
    writer = env.orch.writer
    real_stage = writer.stage
    calls = []

    def racing_stage(cand, msg=""):
        st = real_stage(cand, msg)
        if not calls:  # someone else advances accepted main right after our first staging
            other = git(env.repo, "commit-tree", git(env.repo, "rev-parse", "forge/accepted^{tree}"),
                        "-p", st.base_main, "-m", "external")
            git(env.repo, "update-ref", "refs/heads/forge/accepted", other, st.base_main)
        calls.append(st)
        return st

    writer.stage = racing_stage
    env.orch.run_until_idle()
    r = env.store.get_root(t.id)
    assert r.state == S.ACCEPTED
    assert len(calls) == 2 and calls[0].base_main != calls[1].base_main
    assert env.store.events(root_id=t.id, type_="integration_base_moved")
    assert "def mul" in env.accepted_file("game/src/calc.py")


def test_approval_pending_when_main_moves_returns_to_integration_ready(make_env):
    env = make_env()
    t = env.task(visual_review_required=True)
    approve_and_run(env, t)
    att = env.store.latest_attempt(t.id)
    # accepted main advances (another task integrated) before the owner approves
    other = git(env.repo, "commit-tree", git(env.repo, "rev-parse", "forge/accepted^{tree}"), "-p",
                git(env.repo, "rev-parse", "forge/accepted"), "-m", "other task")
    git(env.repo, "update-ref", "refs/heads/forge/accepted", other)
    env.orch.review(t.id, ReviewAction.APPROVE, candidate_hash=att.candidate_hash)
    env.orch.run_until_idle()
    r = env.store.get_root(t.id)
    # integration produced different bytes (a merge) -> fresh approval against the integrated hash
    assert r.state == S.AWAITING_APPROVAL and "integrated hash" in r.state_reason
    att = env.store.latest_attempt(t.id)
    assert att.integrated_hash and att.integrated_hash != att.candidate_hash
    # and main moves again while that approval is pending
    other2 = git(env.repo, "commit-tree", git(env.repo, "rev-parse", "forge/accepted^{tree}"), "-p", other,
                 "-m", "yet another")
    git(env.repo, "update-ref", "refs/heads/forge/accepted", other2)
    r = env.orch.review(t.id, ReviewAction.APPROVE, candidate_hash=att.integrated_hash)
    assert r.state == S.INTEGRATION_READY and "moved" in r.state_reason
    env.orch.run_until_idle()
    att = env.store.latest_attempt(t.id)
    r = env.orch.review(t.id, ReviewAction.APPROVE, candidate_hash=att.integrated_hash)
    assert r.state == S.ACCEPTED
    assert git(env.repo, "rev-parse", "forge/accepted") == att.integrated_hash


def test_merge_conflict_is_repair_under_same_root(make_env):
    env = make_env()
    t = env.task(max_attempts=1)
    env.orch.approve_task(t.id)
    real_stage = env.orch.writer.stage

    def conflicting(cand, msg=""):
        base = git(env.repo, "rev-parse", "forge/accepted")
        wt = env.tmp / "external"
        if not wt.exists():
            git(env.repo, "worktree", "add", "-q", "--detach", str(wt), base)
            (wt / "game" / "src" / "calc.py").write_text("def add(a, b):\n    return b + a\n\n\ndef mul(a, b):\n    return 0\n")
            git(wt, "commit", "-qam", "conflicting")
            git(env.repo, "update-ref", "refs/heads/forge/accepted", git(wt, "rev-parse", "HEAD"), base)
        return real_stage(cand, msg)

    env.orch.writer.stage = conflicting
    env.orch.run_until_idle()
    r = env.store.get_root(t.id)
    assert r.state == S.FAILED and "RETRY_PENDING" in env.states(t.id)
    assert "INTEGRATING" in env.states(t.id)


# ---------------------------------------------------------------- devices / unity resources

def test_device_task_blocked_when_phone_absent(make_env, monkeypatch, tmp_path):
    monkeypatch.setenv("PATH", "/usr/bin:/bin")  # no adb here
    from forge.checks import DeviceScenarioCheck

    env = make_env()
    env.orch.rt.checks["device-smoke"] = DeviceScenarioCheck("device-smoke", serial="R58N",
                                                             adb_path=str(tmp_path / "missing-adb"))
    t = env.task(integration_checks=["device-smoke"], resource_profile=ResourceProfile(needs_device=True,
                                                                                       device_serial="R58N"))
    r = approve_and_run(env, t)
    assert r.state == S.BLOCKED and "adb" in r.state_reason
    assert env.provider.submissions == []  # nothing spent on a task that cannot be accepted


def test_unity_task_blocked_without_editor(make_env):
    from forge.checks import UnityBuildCheck

    env = make_env()
    env.orch.rt.checks["android-build"] = UnityBuildCheck()
    t = env.task(integration_checks=["android-build"], resource_profile=ResourceProfile(needs_unity=True))
    r = approve_and_run(env, t)
    assert r.state == S.BLOCKED and ("preflight" in r.state_reason or "Unity" in r.state_reason)


def test_gpu_profile_requires_capable_worker(make_env):
    env = make_env()
    t = env.task(resource_profile=ResourceProfile(gpu_vram_gb=24))
    r = approve_and_run(env, t)
    assert r.state == S.BLOCKED and "VRAM" in r.state_reason


def test_device_disconnect_during_integration_is_incomplete_not_pass(make_env):
    from forge.checks.base import Check, CheckOutcome
    from forge.models import EvidenceStatus

    class FlakyDevice(Check):
        name = "device"
        evidence_class = EvidenceClass.DEVICE
        requires = ("device",)

        def __init__(self):
            self.connected = False

        def run(self, workdir, ctx):
            if not self.connected:
                return CheckOutcome("device", EvidenceClass.DEVICE, EvidenceStatus.INCOMPLETE, "phone disconnected")
            return CheckOutcome("device", EvidenceClass.DEVICE, EvidenceStatus.PASS, "scenario passed")

    env = make_env()
    dev = FlakyDevice()
    env.orch.rt.checks["device"] = dev
    t = env.task(integration_checks=["device"])
    r = approve_and_run(env, t)
    assert r.state == S.AWAITING_APPROVAL and "phone disconnected" in r.state_reason
    assert git(env.repo, "rev-parse", "forge/accepted") == git(env.repo, "rev-parse", "main")
    dev.connected = True
    att = env.store.latest_attempt(t.id)
    r = env.orch.review(t.id, ReviewAction.APPROVE, candidate_hash=att.candidate_hash)
    assert r.state == S.INTEGRATION_READY
    env.orch.run_until_idle()
    assert env.store.get_root(t.id).state == S.ACCEPTED


# ---------------------------------------------------------------- provider error classes

@pytest.mark.parametrize("exc,state_word,res_status", [
    ("unavailable", "ProviderUnavailable", "RELEASED"),
    ("rejected", "rejected", "SETTLED"),
    ("boom", "charge pending", "CHARGE_PENDING"),
])
def test_provider_error_classes(make_env, exc, state_word, res_status):
    from forge.providers.base import ProviderRejected, ProviderUnavailable, Usage

    class Failing(FakeProvider):
        def submit(self, req):
            if exc == "unavailable":
                raise ProviderUnavailable("no SDK installed")
            if exc == "rejected":
                raise ProviderRejected("AuthenticationError: invalid x-api-key sk-ant-api03-SECRETSECRETSECRET",
                                       usage=Usage(model="fake-model", requests=1), cost_micros=700)
            raise RuntimeError("unexpected")

    env = make_env(provider=Failing())
    t = env.task()
    r = approve_and_run(env, t)
    assert r.state == S.PAUSED and state_word in r.state_reason
    assert "SECRETSECRET" not in r.state_reason
    res = BudgetLedger(env.store).reservations(root_id=t.id)[0]
    assert res["status"] == res_status
    if exc == "rejected":
        assert res["settled_micros"] == 700
    assert env.orch.attempts_remaining(env.store.get_root(t.id)) == 3  # not a quality attempt
