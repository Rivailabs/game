"""The exact root-task state machine from the plan ("Task contract and exact state machine").

Only the transitions listed in the plan's table are legal. Terminal states
(ACCEPTED / FAILED / CANCELLED) are immutable: a new specification revision
requires an explicitly linked new root (see ``Store.revise_root``).
"""

from __future__ import annotations

from .models import TERMINAL_STATES, TaskState as S

TRANSITIONS: dict[S, frozenset[S]] = {
    S.DRAFT: frozenset({S.NEEDS_INPUT, S.APPROVED, S.CANCELLED}),
    S.NEEDS_INPUT: frozenset({S.DRAFT, S.CANCELLED}),
    S.APPROVED: frozenset({S.BLOCKED, S.READY, S.PAUSED, S.CANCELLED}),
    S.BLOCKED: frozenset({S.READY, S.PAUSED, S.CANCELLED}),
    S.READY: frozenset({S.RUNNING, S.BLOCKED, S.PAUSED, S.CANCELLED}),
    S.RUNNING: frozenset(
        {S.VERIFYING, S.RETRY_PENDING, S.PAUSED, S.FAILED, S.CANCEL_REQUESTED}
    ),
    S.VERIFYING: frozenset(
        {S.AWAITING_APPROVAL, S.INTEGRATION_READY, S.RETRY_PENDING, S.FAILED}
    ),
    # "ACCEPTED after verified integration" - guarded in the orchestrator.
    S.AWAITING_APPROVAL: frozenset(
        {S.INTEGRATION_READY, S.ACCEPTED, S.RETRY_PENDING, S.NEEDS_INPUT, S.CANCELLED}
    ),
    S.INTEGRATION_READY: frozenset({S.INTEGRATING, S.BLOCKED, S.CANCELLED}),
    S.INTEGRATING: frozenset({S.ACCEPTED, S.AWAITING_APPROVAL, S.RETRY_PENDING, S.FAILED}),
    S.RETRY_PENDING: frozenset({S.READY, S.PAUSED, S.FAILED}),
    S.PAUSED: frozenset({S.READY, S.NEEDS_INPUT, S.CANCELLED}),
    S.CANCEL_REQUESTED: frozenset({S.CANCELLED, S.FAILED}),
    S.ACCEPTED: frozenset(),
    S.FAILED: frozenset(),
    S.CANCELLED: frozenset(),
}

assert set(TRANSITIONS) == set(S), "every state must appear in the table"


class IllegalTransition(Exception):
    def __init__(self, src: S, dst: S, root_id: str = ""):
        self.src, self.dst, self.root_id = src, dst, root_id
        extra = " (terminal states are immutable; revise via a new linked root)" if src in TERMINAL_STATES else ""
        super().__init__(f"illegal transition {src.value} -> {dst.value} for {root_id or 'task'}{extra}")


def is_terminal(state: S) -> bool:
    return state in TERMINAL_STATES


def can_transition(src: S, dst: S) -> bool:
    return dst in TRANSITIONS[src]


def check_transition(src: S, dst: S, root_id: str = "") -> None:
    if not can_transition(src, dst):
        raise IllegalTransition(src, dst, root_id)
