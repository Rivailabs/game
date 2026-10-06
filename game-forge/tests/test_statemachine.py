"""Every legal and illegal transition of the plan's exact state machine."""

import itertools

import pytest

from forge.models import TaskState as S
from forge.statemachine import TRANSITIONS, IllegalTransition, can_transition, check_transition, is_terminal
from forge.store import StoreError

# Copied independently from docs/PLAN.md "Task contract and exact state machine".
PLAN_TABLE = {
    "DRAFT": {"NEEDS_INPUT", "APPROVED", "CANCELLED"},
    "NEEDS_INPUT": {"DRAFT", "CANCELLED"},
    "APPROVED": {"BLOCKED", "READY", "PAUSED", "CANCELLED"},
    "BLOCKED": {"READY", "PAUSED", "CANCELLED"},
    "READY": {"RUNNING", "BLOCKED", "PAUSED", "CANCELLED"},
    "RUNNING": {"VERIFYING", "RETRY_PENDING", "PAUSED", "FAILED", "CANCEL_REQUESTED"},
    "VERIFYING": {"AWAITING_APPROVAL", "INTEGRATION_READY", "RETRY_PENDING", "FAILED"},
    "AWAITING_APPROVAL": {"INTEGRATION_READY", "ACCEPTED", "RETRY_PENDING", "NEEDS_INPUT", "CANCELLED"},
    "INTEGRATION_READY": {"INTEGRATING", "BLOCKED", "CANCELLED"},
    "INTEGRATING": {"ACCEPTED", "AWAITING_APPROVAL", "RETRY_PENDING", "FAILED"},
    "RETRY_PENDING": {"READY", "PAUSED", "FAILED"},
    "PAUSED": {"READY", "NEEDS_INPUT", "CANCELLED"},
    "CANCEL_REQUESTED": {"CANCELLED", "FAILED"},
    "ACCEPTED": set(),
    "FAILED": set(),
    "CANCELLED": set(),
}

ALL_PAIRS = list(itertools.product(list(S), list(S)))
LEGAL = [(a, b) for a, b in ALL_PAIRS if b.value in PLAN_TABLE[a.value]]
ILLEGAL = [(a, b) for a, b in ALL_PAIRS if b.value not in PLAN_TABLE[a.value]]


def test_table_matches_plan_exactly():
    assert {s.value for s in S} == set(PLAN_TABLE)
    for src, dsts in TRANSITIONS.items():
        assert {d.value for d in dsts} == PLAN_TABLE[src.value], src
    assert len(LEGAL) == 45
    assert len(LEGAL) + len(ILLEGAL) == 256


@pytest.mark.parametrize("src,dst", LEGAL, ids=[f"{a.value}->{b.value}" for a, b in LEGAL])
def test_legal_transition_persists(env, src, dst):
    t = env.task("x", state=src)
    new = env.store.transition(t.id, dst, "test")
    assert new.state == dst
    assert env.store.get_root(t.id).state == dst
    ev = env.store.events(root_id=t.id, type_="state_transition")[-1]
    assert ev["payload"]["src"] == src.value and ev["payload"]["dst"] == dst.value


@pytest.mark.parametrize("src,dst", ILLEGAL, ids=[f"{a.value}->{b.value}" for a, b in ILLEGAL])
def test_illegal_transition_raises(src, dst):
    assert not can_transition(src, dst)
    with pytest.raises(IllegalTransition):
        check_transition(src, dst)


@pytest.mark.parametrize("src,dst", [(S.DRAFT, S.RUNNING), (S.ACCEPTED, S.READY), (S.FAILED, S.RETRY_PENDING),
                                     (S.VERIFYING, S.ACCEPTED), (S.RUNNING, S.ACCEPTED)])
def test_illegal_transition_rejected_by_store_and_state_unchanged(env, src, dst):
    t = env.task("x", state=src)
    before = len(env.store.events(root_id=t.id))
    with pytest.raises(IllegalTransition):
        env.store.transition(t.id, dst)
    assert env.store.get_root(t.id).state == src
    assert len(env.store.events(root_id=t.id)) == before  # nothing logged for a refused change


@pytest.mark.parametrize("terminal", [S.ACCEPTED, S.FAILED, S.CANCELLED])
def test_terminal_states_are_immutable(env, terminal):
    assert is_terminal(terminal)
    t = env.task("x", state=terminal)
    for dst in S:
        with pytest.raises(IllegalTransition):
            env.store.transition(t.id, dst)
    with pytest.raises(StoreError):
        env.store.update_root(t, title="changed")
    with pytest.raises(StoreError):
        env.store.update_root(t, acceptance_cases=[])


def test_root_ids_are_immutable(env):
    env.task("1")
    with pytest.raises(StoreError):
        env.task("1")


def test_spec_change_creates_linked_root_with_previous_cost(env):
    t = env.task("1", state=S.FAILED)
    new = env.store.revise_root(t.id, cost_micros=123_456, title="clarified mul rules")
    assert new.id != t.id and new.previous_root_id == t.id
    assert new.previous_cost_micros == 123_456
    assert new.state == S.DRAFT and new.spec_version == "2"
    assert env.store.get_root(t.id).state == S.FAILED  # old root untouched
    assert env.store.lineage_head(t.id).id == new.id
    newer = env.store.revise_root(new.id, cost_micros=1_000)
    assert newer.previous_cost_micros == 124_456  # cumulative lineage cost, never a free retry
    assert env.store.lineage_head(t.id).id == newer.id


def test_revising_active_root_cancels_it(env):
    t = env.task("1", state=S.APPROVED)
    env.store.revise_root(t.id)
    assert env.store.get_root(t.id).state == S.CANCELLED


def test_events_are_append_only(env):
    import sqlite3

    env.task("1")
    with pytest.raises(sqlite3.DatabaseError):
        env.store.conn.execute("UPDATE events SET type='x'")
    with pytest.raises(sqlite3.DatabaseError):
        env.store.conn.execute("DELETE FROM events")


def test_events_carry_correlation_ids(env):
    t = env.task("1")
    env.store.transition(t.id, S.APPROVED, "ok", correlation_id="corr-42")
    ev = env.store.events(root_id=t.id, type_="state_transition")[-1]
    assert ev["correlation_id"] == "corr-42"
    created = env.store.events(root_id=t.id, type_="root_created")[0]
    assert created["correlation_id"] == t.id  # defaults to the root ID
