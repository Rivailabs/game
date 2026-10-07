"""Real `dotnet build` + `dotnet test` against a copy of astra-kingdoms/ (skipped without dotnet)."""

import shutil
from pathlib import Path

import pytest

from forge.checks import CheckContext, DotnetTestCheck
from forge.models import AcceptanceCase, EvidenceStatus
from forge.models import TaskState as S
from forge.providers import FakeBehaviour, FakeProvider

from .conftest import Env, git

ASTRA = Path(__file__).resolve().parent.parent.parent / "astra-kingdoms"
pytestmark = [pytest.mark.dotnet,
              pytest.mark.skipif(shutil.which("dotnet") is None, reason="dotnet SDK not on PATH"),
              pytest.mark.skipif(not ASTRA.is_dir(), reason="astra-kingdoms/ not present")]

PASSING = """namespace AstraKingdoms.Rules.Tests;

public class ForgeSmokeTests
{
    [Test]
    public void Fire_counter_relationship_is_defined()
    {
        Assert.That(2 + 2, Is.EqualTo(4));
    }
}
"""
FAILING = """namespace AstraKingdoms.Rules.Tests;

public class ForgeInjectedFailureTests
{
    [Test]
    public void Injected_rule_failure()
    {
        Assert.That(1, Is.EqualTo(2), "injected rule failure");
    }
}
"""
COMPILE_ERROR = "namespace AstraKingdoms.Rules.Core { public class Broken { int x = \"not an int\"; } }\n"
TESTS_DIR = Path("tests/AstraKingdoms.Rules.Tests")


def copy_astra(dst: Path) -> Path:
    shutil.copytree(ASTRA, dst, ignore=shutil.ignore_patterns("bin", "obj", "TestResults"))
    return dst


def test_real_dotnet_check_against_astra(tmp_path):
    work = tmp_path / "w"
    work.mkdir()
    copy_astra(work / "astra-kingdoms")
    chk = DotnetTestCheck("rules", project_dir="astra-kingdoms")
    ctx = CheckContext()
    out = chk.run(work, ctx)
    # the scaffold may or may not contain tests yet; zero tests must never be reported as a pass
    assert out.status in (EvidenceStatus.PASS, EvidenceStatus.INCOMPLETE), out.summary
    if out.details["total"] == 0:
        assert out.status == EvidenceStatus.INCOMPLETE
    assert out.details["build_exit_code"] == 0

    (work / "astra-kingdoms" / TESTS_DIR / "ForgeSmokeTests.cs").write_text(PASSING)
    out = chk.run(work, ctx)
    assert out.status == EvidenceStatus.PASS, out.summary
    assert out.details["passed"] >= 1 and out.details["failed"] == 0 and out.details["source"] == "trx"

    (work / "astra-kingdoms" / TESTS_DIR / "ForgeInjectedFailureTests.cs").write_text(FAILING)
    out = chk.run(work, ctx)
    assert out.status == EvidenceStatus.FAIL
    assert out.details["failed"] == 1
    assert out.details["failures"][0]["name"].endswith("Injected_rule_failure")
    assert "injected rule failure" in out.details["failures"][0]["message"]

    (work / "astra-kingdoms" / TESTS_DIR / "ForgeInjectedFailureTests.cs").unlink()
    (work / "astra-kingdoms" / "src" / "AstraKingdoms.Rules" / "Broken.cs").write_text(COMPILE_ERROR)
    out = chk.run(work, ctx)
    assert out.status == EvidenceStatus.FAIL and out.details["failure_category"] == "compile_error"
    assert out.details["build_errors"]


def test_orchestrator_e2e_with_real_dotnet(tmp_path):
    repo = tmp_path / "repo"
    repo.mkdir()
    copy_astra(repo / "astra-kingdoms")
    git(repo, "init", "-q", "-b", "main")
    git(repo, "config", "user.email", "t@example.com")
    git(repo, "config", "user.name", "Test")
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "astra scaffold")
    git(repo, "branch", "forge/accepted")
    tests_rel = f"astra-kingdoms/{TESTS_DIR.as_posix()}"
    prov = FakeProvider({
        1: FakeBehaviour(files={f"{tests_rel}/ForgeInjectedFailureTests.cs": FAILING}),  # injected rule failure
        2: FakeBehaviour(files={f"{tests_rel}/ForgeSmokeTests.cs": PASSING}),
    })
    env = Env(tmp_path, repo, provider=prov, checks={"rules": DotnetTestCheck("rules", project_dir="astra-kingdoms")})
    t = env.task(permitted_paths=["astra-kingdoms/src/**", "astra-kingdoms/tests/AstraKingdoms.Rules.Tests/**"],
                 verification_checks=["rules"],
                 acceptance_cases=[AcceptanceCase(id="AC1", description="rules tests pass")])
    env.orch.approve_task(t.id)
    env.orch.run_until_idle()
    r = env.store.get_root(t.id)
    assert r.state == S.ACCEPTED, r.state_reason
    seq = env.states(t.id)
    assert "RETRY_PENDING" in seq  # the injected failure was detected and repaired
    fail_ev = [e for e in env.store.list_evidence(t.id) if e.name == "rules" and e.status == EvidenceStatus.FAIL]
    assert fail_ev and fail_ev[0].details["failed"] == 1
    assert "ForgeSmokeTests.cs" in git(repo, "ls-tree", "-r", "--name-only", "forge/accepted")
