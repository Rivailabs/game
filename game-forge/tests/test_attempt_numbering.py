"""Regression: an abandoned attempt (transport failure) keeps its number; the resumed attempt gets a new one."""

from forge.models import AttemptStatus, ReviewAction, TaskState as S
from forge.providers import FakeBehaviour, FakeProvider

from .conftest import GOOD_MUL


def test_resume_after_abandoned_attempt_gets_a_fresh_number(make_env):
    provider = FakeProvider({1: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL}, transport_failures=3),
                             2: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL})})
    env = make_env(provider=provider)
    t = env.task()
    env.orch.approve_task(t.id)
    env.orch.run_until_idle()
    assert env.store.get_root(t.id).state == S.PAUSED
    first = env.store.latest_attempt(t.id)
    assert first.status == AttemptStatus.ABANDONED and first.number == 1
    env.orch.review(t.id, ReviewAction.RESUME)
    env.orch.run_until_idle()
    atts = env.store.list_attempts(t.id)
    assert [a.number for a in atts] == [1, 2]
    assert env.store.get_root(t.id).state == S.ACCEPTED
