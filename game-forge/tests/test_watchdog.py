"""Active-work timeout watchdog: in-call deadline, check kill timer and the scheduler sweep."""

from __future__ import annotations

import sys
import threading
import time
from pathlib import Path

import pytest

from forge.budget import BudgetLedger
from forge.checks import CommandCheck
from forge.checks.base import CheckContext
from forge.models import AttemptStatus, EvidenceStatus, TaskState as S
from forge.providers import FakeBehaviour, FakeProvider
from forge.providers.base import ReconcileResult
from forge.providers.fake import SimulatedCrash
from forge.sandbox import ActiveWorkTimeout, SandboxConfig, SandboxedCheck, SandboxRunner, call_with_deadline
from forge.sandbox.policy import builtin_forbidden_paths
from forge.sandbox.watchdog import run_check_with_deadline

from .conftest import GOOD_MUL


def test_call_with_deadline_returns_raises_and_cancels():
    assert call_with_deadline(lambda: 42, 5) == 42
    with pytest.raises(ValueError):
        call_with_deadline(lambda: (_ for _ in ()).throw(ValueError("boom")), 5)
    cancelled = []
    gate = threading.Event()
    t0 = time.monotonic()
    with pytest.raises(ActiveWorkTimeout):
        call_with_deadline(lambda: gate.wait(10), 0.2, on_timeout=lambda: cancelled.append(True))
    gate.set()
    assert cancelled == [True] and time.monotonic() - t0 < 5
    with pytest.raises(ActiveWorkTimeout, match="already passed"):
        call_with_deadline(lambda: 1, 0)


def test_check_killed_when_deadline_passes(tmp_path):
    home = Path("/nonexistent-forge-home")
    runner = SandboxRunner(SandboxConfig(), backends=[], forbidden=builtin_forbidden_paths(home=home), home=home)
    chk = SandboxedCheck(CommandCheck("slow", [sys.executable, "-c", "import time; time.sleep(30)"]), runner)
    t0 = time.monotonic()
    oc = run_check_with_deadline(chk, tmp_path, CheckContext(), 0.3)
    assert time.monotonic() - t0 < 15
    assert oc.status == EvidenceStatus.FAIL and "watchdog" in oc.summary
    oc = run_check_with_deadline(chk, tmp_path, CheckContext(), -1)
    assert oc.status == EvidenceStatus.INCOMPLETE


class SlowProvider(FakeProvider):
    def __init__(self, delay: float):
        super().__init__({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL})})
        self.delay = delay

    def submit(self, req):
        time.sleep(self.delay)
        return super().submit(req)


def test_provider_call_interrupted_by_active_work_timeout(make_env):
    env = make_env(provider=SlowProvider(3.0))
    t = env.task(active_work_timeout_s=1)
    env.orch.approve_task(t.id)
    t0 = time.monotonic()
    env.orch.run_until_idle(max_ticks=3)
    assert time.monotonic() - t0 < 3.0  # the scheduler did not wait for the provider
    root = env.store.get_root(t.id)
    assert root.state == S.PAUSED and "active-work timeout" in root.state_reason
    assert "charge pending" in root.state_reason
    a = env.store.latest_attempt(t.id)
    assert a.status == AttemptStatus.FAILED  # the timed-out attempt counts against the limit
    res = BudgetLedger(env.store).reservations(root_id=t.id)
    assert res[0]["status"] == "CHARGE_PENDING"


class LongJobProvider(FakeProvider):
    """Accepts the job, Forge crashes, and the provider then keeps reporting 'running'."""

    def __init__(self):
        super().__init__({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL}, crash_after_submit=True)})

    def reconcile(self, idempotency_key):
        return ReconcileResult("running")


def test_sweep_times_out_stuck_running_attempt(make_env):
    provider = LongJobProvider()
    env = make_env(provider=provider)
    t = env.task(active_work_timeout_s=600)
    env.orch.approve_task(t.id)
    with pytest.raises(SimulatedCrash):
        env.orch.run_until_idle()
    assert env.store.get_root(t.id).state == S.RUNNING
    env.clock.advance(400)
    w2 = env.make_orch("w2", provider)
    w2.leases.release_for_holder("w1")  # w1 is dead
    w2.tick()
    assert env.store.get_root(t.id).state == S.RUNNING  # still within its active duration: keeps polling
    env.clock.advance(400)
    rep = w2.tick()
    root = env.store.get_root(t.id)
    assert root.state == S.PAUSED and "active-work timeout" in root.state_reason
    assert any("active-work timeout" in a for a in rep.actions)
    assert env.store.events(root_id=t.id, type_="active_work_timeout")
    assert BudgetLedger(env.store).reservations(root_id=t.id)[0]["status"] == "CHARGE_PENDING"
    assert w2.leases.holder(f"attempt:{env.store.latest_attempt(t.id).id}") is None
