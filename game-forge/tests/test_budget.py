"""Reservations, settlement, caps, concurrency, kill switch, cash and capacity ledgers."""

import threading

import pytest

from forge.budget import (
    BudgetError,
    BudgetExceeded,
    BudgetLedger,
    CapacityLedger,
    CashLedger,
    DispatchStopped,
    NoCapConfigured,
    UnknownCeiling,
    milestone_scope,
    project_scope,
    root_scope,
)
from forge.store import Store
from forge.util import FakeClock


@pytest.fixture
def ledger(tmp_path):
    store = Store(tmp_path / "b.db", clock=FakeClock())
    led = BudgetLedger(store)
    led.set_cap(project_scope("p"), 1_000_000)
    led.set_cap(milestone_scope("m"), 800_000)
    led.set_cap(root_scope("r"), 500_000)
    return led


def reserve(led, amount, root="r", **kw):
    return led.reserve(project_id="p", milestone_id="m", root_id=root, amount_micros=amount, **kw)


def test_reserve_settle_release_math(ledger):
    rid = reserve(ledger, 300_000)
    s = ledger.summary(root_scope("r"))
    assert (s.reserved, s.settled, s.available) == (300_000, 0, 200_000)
    released = ledger.settle(rid, 120_000)
    assert released == 180_000
    s = ledger.summary(root_scope("r"))
    assert (s.reserved, s.settled, s.available) == (0, 120_000, 380_000)
    assert ledger.get(rid)["status"] == "SETTLED"
    # milestone and project see the same settled usage exactly once
    assert ledger.summary(milestone_scope("m")).settled == 120_000
    assert ledger.summary(project_scope("p")).available == 880_000
    # settling twice does not double count
    assert ledger.settle(rid) == 0
    assert ledger.summary(project_scope("p")).settled == 120_000


def test_incremental_usage_and_release_without_charge(ledger):
    rid = reserve(ledger, 100_000)
    ledger.record_usage(rid, 10_000)
    ledger.record_usage(rid, 15_000)
    assert ledger.summary(root_scope("r")).reserved == 75_000
    assert ledger.settle(rid) == 75_000
    assert ledger.summary(root_scope("r")).settled == 25_000
    rid2 = reserve(ledger, 50_000)
    assert ledger.release(rid2) == 50_000
    assert ledger.get(rid2)["status"] == "RELEASED"
    with pytest.raises(BudgetError):
        ledger.record_usage(rid2, 1)


def test_overrun_is_recorded_not_hidden(ledger):
    rid = reserve(ledger, 10_000)
    ledger.settle(rid, 25_000)
    assert ledger.summary(root_scope("r")).settled == 25_000
    assert ledger.store.events(type_="reservation_overrun")


@pytest.mark.parametrize("scope,amount", [("root", 500_001), ("milestone", 800_001), ("project", 1_000_001)])
def test_caps_enforced_at_every_scope(ledger, scope, amount):
    with pytest.raises(BudgetExceeded) as ei:
        reserve(ledger, amount)
    assert ei.value.scope.startswith(scope)


def test_root_cap_counts_cumulative_spend(ledger):
    a = reserve(ledger, 400_000)
    ledger.settle(a, 400_000)
    with pytest.raises(BudgetExceeded):
        reserve(ledger, 100_001)
    reserve(ledger, 100_000)


def test_unknown_ceiling_rejected_for_unattended(ledger):
    with pytest.raises(UnknownCeiling):
        reserve(ledger, None, unattended=True)
    with pytest.raises(UnknownCeiling):
        reserve(ledger, None, unattended=False)


def test_no_cap_configured_refuses(ledger):
    with pytest.raises(NoCapConfigured):
        reserve(ledger, 1, root="other-root")


def test_kill_switch_blocks_reservations(ledger):
    ledger.stop_dispatch(reason="test")
    with pytest.raises(DispatchStopped):
        reserve(ledger, 1)
    ledger.resume_dispatch()
    reserve(ledger, 1)
    types = [e["type"] for e in ledger.store.events()]
    assert "dispatch_stopped" in types and "dispatch_resumed" in types


def test_charge_pending_keeps_allowance_reserved(ledger):
    rid = reserve(ledger, 200_000)
    ledger.mark_charge_pending(rid, "uncertain cancellation")
    s = ledger.summary(root_scope("r"))
    assert s.reserved == 200_000 and s.pending_charge == 200_000
    assert ledger.get(rid)["status"] == "CHARGE_PENDING"
    ledger.resolve_charge_pending(rid, 50_000)
    s = ledger.summary(root_scope("r"))
    assert (s.reserved, s.settled, s.pending_charge) == (0, 50_000, 0)


def test_caps_never_raised_automatically(ledger):
    for _ in range(3):
        with pytest.raises(BudgetExceeded):
            reserve(ledger, 600_000)
    assert ledger.get_cap(root_scope("r")) == 500_000
    assert not [e for e in ledger.store.events(type_="budget_cap_set") if e["payload"]["cap_micros"] != 500_000
                and e["payload"]["scope"] == root_scope("r")]


def test_concurrent_reservations_cannot_overspend(tmp_path):
    db = tmp_path / "c.db"
    setup = BudgetLedger(Store(db))
    setup.set_cap(project_scope("p"), 300_000)
    setup.set_cap(milestone_scope("m"), 10_000_000)
    for i in range(20):
        setup.set_cap(root_scope(f"r{i}"), 10_000_000)
    barrier = threading.Barrier(20)
    ok, refused, errors = [], [], []

    def worker(i):
        led = BudgetLedger(Store(db))  # separate connection, like a separate worker
        barrier.wait()
        try:
            ok.append(led.reserve(project_id="p", milestone_id="m", root_id=f"r{i}", amount_micros=100_000))
        except BudgetExceeded:
            refused.append(i)
        except Exception as e:  # pragma: no cover
            errors.append(e)

    threads = [threading.Thread(target=worker, args=(i,)) for i in range(20)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert not errors
    assert len(ok) == 3 and len(refused) == 17
    assert setup.summary(project_scope("p")).reserved == 300_000
    assert setup.summary(project_scope("p")).available == 0


def test_two_threads_same_remaining_allowance(tmp_path):
    db = tmp_path / "d.db"
    setup = BudgetLedger(Store(db))
    setup.set_cap(project_scope("p"), 100)
    setup.set_cap(milestone_scope("m"), 100)
    setup.set_cap(root_scope("a"), 100)
    setup.set_cap(root_scope("b"), 100)
    for _ in range(25):  # repeat to shake out races
        results = []
        barrier = threading.Barrier(2)

        def go(root):
            led = BudgetLedger(Store(db))
            barrier.wait()
            try:
                results.append(led.reserve(project_id="p", milestone_id="m", root_id=root, amount_micros=60))
            except BudgetExceeded:
                results.append(None)

        ts = [threading.Thread(target=go, args=(r,)) for r in ("a", "b")]
        [t.start() for t in ts]
        [t.join() for t in ts]
        granted = [r for r in results if r]
        assert len(granted) == 1
        setup.release(granted[0])


# ------------------------------------------------------------------ cash ledger

def test_shared_cost_allocated_once(tmp_path):
    cash = CashLedger(Store(tmp_path / "e.db"))
    cash.add_expense(date="2026-10-01", vendor="Anthropic", invoice_id="INV-1", cash_paid_minor=100_000,
                     taxes_fees_minor=18_000, milestone_id="pilot", allocation={"astra": 0.6, "forge": 0.4})
    cash.add_expense(date="2026-10-02", vendor="GPU Rental", invoice_id="G-7", cash_paid_minor=100,
                     allocation={"astra": 1 / 3, "forge": 1 / 3, "ops": 1 / 3})
    rep = cash.report("INR")
    assert rep["combined_cash_minor"] == 118_100
    assert sum(rep["per_project_minor"].values()) == rep["combined_cash_minor"]
    assert rep["per_project_minor"]["astra"] == 70_800 + 34
    assert rep["per_project_minor"]["forge"] == 47_200 + 33
    assert rep["per_project_minor"]["ops"] == 33


def test_duplicate_invoice_rejected_and_corrections_append_only(tmp_path):
    cash = CashLedger(Store(tmp_path / "f.db"))
    eid = cash.add_expense(date="2026-10-01", vendor="V", invoice_id="1", cash_paid_minor=500)
    with pytest.raises(BudgetError):
        cash.add_expense(date="2026-10-01", vendor="V", invoice_id="1", cash_paid_minor=500)
    with pytest.raises(BudgetError):
        cash.add_expense(date="2026-10-01", vendor="V", invoice_id="2", cash_paid_minor=-5)
    cash.add_expense(date="2026-10-03", vendor="V", invoice_id="1", cash_paid_minor=-200, corrects_id=eid)
    assert cash.report()["combined_cash_minor"] == 300
    assert len(cash.expenses()) == 2


def test_promo_credit_is_not_cash(tmp_path):
    cash = CashLedger(Store(tmp_path / "g.db"))
    cash.add_expense(date="2026-10-01", vendor="Azure", invoice_id="a", cash_paid_minor=0, promo_credit_minor=50_000,
                     normal_rate_minor=50_000)
    rep = cash.report()
    assert rep["combined_cash_minor"] == 0
    assert rep["promo_credit_minor"] == 50_000
    assert rep["normal_rate_equivalent_minor"] == 50_000


@pytest.mark.parametrize("alloc", [{"a": 0.5, "b": 0.6}, {}, {"a": -1, "b": 2}])
def test_bad_allocation_rules(tmp_path, alloc):
    cash = CashLedger(Store(tmp_path / "h.db"))
    with pytest.raises(BudgetError):
        cash.add_expense(date="d", vendor="v", invoice_id=None, cash_paid_minor=1, allocation=alloc)


def test_founder_hours_capacity_ledger(tmp_path):
    cap = CapacityLedger(Store(tmp_path / "i.db"))
    cap.log(date="2026-10-01", hours=3, project_key="astra", milestone_id="pilot")
    cap.log(date="2026-10-01", hours=1.5, project_key="forge", milestone_id="pilot", kind="support")
    s = cap.summary("pilot")
    assert s["total"] == 4.5 and s["by_kind"] == {"development": 3.0, "support": 1.5}
    with pytest.raises(BudgetError):
        cap.log(date="d", hours=0, project_key="x")
    with pytest.raises(BudgetError):
        cap.log(date="d", hours=1, project_key="x", kind="fun")
