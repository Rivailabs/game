from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from forge.budget import BudgetLedger, milestone_scope, project_scope  # noqa: E402
from forge.checks import CommandCheck  # noqa: E402
from forge.models import AcceptanceCase, Milestone, Project, ProjectPolicy, RootTask  # noqa: E402
from forge.orchestrator import Orchestrator  # noqa: E402
from forge.providers import FakeBehaviour, FakeProvider  # noqa: E402
from forge.runtime import ProjectRuntime  # noqa: E402
from forge.store import Store  # noqa: E402
from forge.util import FakeClock  # noqa: E402

CALC = "def add(a, b):\n    return a + b\n"
GOOD_MUL = CALC + "\n\ndef mul(a, b):\n    return a * b\n"
BAD_MUL = CALC + "\n\ndef mul(a, b):\n    return a + b\n"
ACCEPT_ADD = """import sys, unittest
sys.path.insert(0, 'game/src')
import calc

class AddTest(unittest.TestCase):
    def test_add(self):
        self.assertEqual(calc.add(2, 3), 5)
"""
ACCEPT_MUL = """import sys, unittest
sys.path.insert(0, 'game/src')
import calc

class MulTest(unittest.TestCase):
    def test_mul(self):
        self.assertEqual(calc.mul(3, 4), 12)
"""


def git(cwd, *args):
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


@pytest.fixture
def repo(tmp_path) -> Path:
    r = tmp_path / "repo"
    (r / "game" / "src").mkdir(parents=True)
    (r / "game" / "tests").mkdir(parents=True)
    (r / "game" / "src" / "calc.py").write_text(CALC)
    (r / "game" / "tests" / "test_add.py").write_text(ACCEPT_ADD)
    (r / "game" / "tests" / "test_mul.py").write_text(ACCEPT_MUL)
    (r / "README.md").write_text("demo\n")
    git(r, "init", "-q", "-b", "main")
    git(r, "config", "user.email", "t@example.com")
    git(r, "config", "user.name", "Test")
    git(r, "add", "-A")
    git(r, "commit", "-q", "-m", "init")
    git(r, "branch", "forge/accepted")
    return r


def unittest_check(name: str, pattern: str) -> CommandCheck:
    return CommandCheck(name, [sys.executable, "-m", "unittest", "discover", "-s", "game/tests",
                               "-p", pattern], timeout_s=60)


class Env:
    def __init__(self, tmp_path: Path, repo: Path, provider: FakeProvider | None = None, *, worker_id="w1",
                 clock: FakeClock | None = None, reviewer: bool = False, checks=None, project_cap=10_000_000,
                 milestone_cap=5_000_000):
        self.tmp, self.repo = tmp_path, repo
        self.clock = clock or FakeClock()
        self.store = Store(tmp_path / "data" / "forge.db", clock=self.clock)
        self.provider = provider or FakeProvider({0: FakeBehaviour(files={"game/src/calc.py": GOOD_MUL})})
        self.project = Project(id="demo", name="Demo", repo_path=str(repo), workdir="game",
                               accepted_branch="forge/accepted",
                               policy=ProjectPolicy(allowed_vendors=["fake", "anthropic"]),
                               protected_paths=["game/tests/test_*.py"])
        self.store.put_project(self.project)
        self.store.put_milestone(Milestone(id="m1", project_id="demo", name="Milestone 1"))
        led = BudgetLedger(self.store)
        led.set_cap(project_scope("demo"), project_cap)
        led.set_cap(milestone_scope("m1"), milestone_cap)
        self.checks = checks if checks is not None else {
            "add": unittest_check("add", "test_add.py"), "mul": unittest_check("mul", "test_mul.py")}
        self.reviewer = reviewer
        self.worker_id = worker_id
        self.orch = self.make_orch(worker_id)

    def make_orch(self, worker_id: str, provider: FakeProvider | None = None) -> Orchestrator:
        providers = {"fake": provider or self.provider}
        rt = ProjectRuntime(project=self.project, data_dir=self.tmp / "data", checks=self.checks,
                            providers=providers, reviewer_route="fake" if self.reviewer else None,
                            worker_id=worker_id, transport_retries=2, backoff_s=1.0, lease_ttl_s=300)
        return Orchestrator(self.store, rt)

    def task(self, ticket: str = "1", **kw) -> RootTask:
        data = dict(id=f"demo-t{ticket}", project_id="demo", milestone_id="m1", ticket=ticket,
                    title=f"Task {ticket}: implement mul", permitted_paths=["game/src/**"],
                    acceptance_cases=[AcceptanceCase(id="AC1", description="mul(3,4) == 12")],
                    permitted_routes=["fake"], verification_checks=["add", "mul"],
                    reservation_ceiling_micros=1_000_000)
        data.update(kw)
        return self.store.create_root(RootTask(**data))

    def accepted_file(self, path: str) -> str:
        return git(self.repo, "show", f"forge/accepted:{path}")

    def states(self, root_id: str) -> list[str]:
        out = []
        for ev in self.store.events(root_id=root_id, type_="state_transition"):
            if not out:
                out.append(ev["payload"]["src"])
            out.append(ev["payload"]["dst"])
        return out


@pytest.fixture
def env(tmp_path, repo):
    return Env(tmp_path, repo)


@pytest.fixture
def make_env(tmp_path, repo):
    def _make(**kw):
        return Env(tmp_path, repo, **kw)
    return _make
