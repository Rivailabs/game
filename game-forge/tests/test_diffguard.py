"""Diff guard: deleted tests, skipped tests, threshold edits, disabled gates, scope."""

import pytest

from forge.diffguard import analyze
from forge.gitops import ChangedFile, WorkspaceManager

from .conftest import git


def make_diff(path, minus, plus, old=None):
    old = old or path
    body = "\n".join([f"-{l}" for l in minus] + [f"+{l}" for l in plus])
    return f"diff --git a/{old} b/{path}\n--- a/{old}\n+++ b/{path}\n@@ -1,{len(minus)} +1,{len(plus)} @@\n{body}\n"


def test_clean_source_change_passes():
    d = make_diff("game/src/calc.py", ["    return a + b"], ["    return a * b"])
    rep = analyze(d, [ChangedFile("M", "game/src/calc.py")], permitted_paths=["game/src/**"])
    assert rep.clean and not rep.requires_separate_review


def test_adding_tests_is_clean():
    d = make_diff("tests/Rules.Tests/DuelTests.cs", [], ["    [Test]", "    public void Tie() { Assert.That(1, Is.EqualTo(1)); }"])
    rep = analyze(d, [ChangedFile("A", "tests/Rules.Tests/DuelTests.cs")], permitted_paths=["tests/**"])
    assert rep.clean


def test_deleted_test_file_flagged():
    d = "diff --git a/game/tests/test_mul.py b/game/tests/test_mul.py\ndeleted file mode 100644\n" \
        "--- a/game/tests/test_mul.py\n+++ /dev/null\n@@ -1,2 +0,0 @@\n-def test_mul():\n-    assert mul(3, 4) == 12\n"
    rep = analyze(d, [ChangedFile("D", "game/tests/test_mul.py")])
    assert {"test_file_deleted", "test_case_removed"} <= rep.kinds()
    assert rep.requires_separate_review


def test_renaming_test_out_of_test_tree_flagged():
    rep = analyze("", [ChangedFile("R", "game/src/unused.py", "game/tests/test_mul.py")])
    assert "test_file_deleted" in rep.kinds()


def test_removed_nunit_test_case_flagged():
    d = make_diff("astra/tests/Rules.Tests/ElementTests.cs",
                  ["    [Test]", "    public void FireBeatsWind() { Assert.That(Chart.Beats(Fire, Wind)); }"], [])
    rep = analyze(d, [ChangedFile("M", "astra/tests/Rules.Tests/ElementTests.cs")])
    assert {"test_case_removed", "assertion_removed"} <= rep.kinds()


@pytest.mark.parametrize("line", [
    "    [Ignore(\"flaky\")]",
    "    [Test, Explicit]",
    "        Assert.Ignore(\"later\");",
    "@pytest.mark.skip(reason='later')",
    "    pytest.skip('nope')",
    "    [Fact(Skip = \"broken\")]",
    "xit('resolves ties', () => {})",
])
def test_added_skip_markers_flagged(line):
    d = make_diff("game/tests/test_x.py", [], [line])
    rep = analyze(d, [ChangedFile("M", "game/tests/test_x.py")])
    assert "test_skipped" in rep.kinds(), line


@pytest.mark.parametrize("minus,plus", [
    ("MaxFrameTimeMs = 16.6;", "MaxFrameTimeMs = 33.3;"),
    ("    Assert.AreEqual(51040, board.TotalCells);", "    Assert.AreEqual(51000, board.TotalCells);"),
    ("download_budget_mb = 80", "download_budget_mb = 120"),
    ("tolerance: 0.001", "tolerance: 0.5"),
    ("public const int MaxVolleys = 3;", "public const int MaxVolleys = 4;"),
])
def test_threshold_edits_flagged(minus, plus):
    d = make_diff("astra/src/Rules/Constants.cs", [minus], [plus])
    rep = analyze(d, [ChangedFile("M", "astra/src/Rules/Constants.cs")])
    assert "threshold_changed" in rep.kinds()


def test_non_threshold_number_change_not_flagged():
    d = make_diff("game/src/names.py", ["greeting = 'v1'"], ["greeting = 'v2'"])
    assert "threshold_changed" not in analyze(d, [ChangedFile("M", "game/src/names.py")]).kinds()


@pytest.mark.parametrize("path,line", [
    (".github/workflows/ci.yml", "        continue-on-error: true"),
    ("scripts/check.sh", "dotnet test || true"),
    ("tests/Rules.Tests/Rules.Tests.csproj", "    <IsTestProject>false</IsTestProject>"),
    ("ci/run.sh", "dotnet test --filter Category!=Slow"),
])
def test_disabled_gates_flagged(path, line):
    d = make_diff(path, [], [line])
    rep = analyze(d, [ChangedFile("M", path)])
    assert "gate_disabled" in rep.kinds()


def test_scope_violation_and_protected_path():
    d = make_diff("game/tests/test_mul.py", ["    assert mul(3, 4) == 12"], ["    assert True"])
    changed = [ChangedFile("M", "game/tests/test_mul.py"), ChangedFile("A", "secrets.txt")]
    rep = analyze(d, changed, permitted_paths=["game/src/**"], protected_paths=["game/tests/**"])
    assert set(rep.scope_violations) == {"game/tests/test_mul.py", "secrets.txt"}
    assert "protected_path_modified" in rep.kinds()


def test_guard_on_real_git_diff(repo, tmp_path):
    ws = WorkspaceManager(repo, tmp_path / "wt")
    base = ws.head("main")
    path, _ = ws.create("att1", base)
    (path / "game" / "tests" / "test_mul.py").unlink()
    t = path / "game" / "tests" / "test_add.py"
    t.write_text(t.read_text().replace("    def test_add(self):", "    @unittest.skip('later')\n    def test_add(self):"))
    cand = ws.commit_candidate(path, "weaken")
    rep = analyze(ws.diff(base, cand), ws.changed_files(base, cand), permitted_paths=["game/**"],
                  protected_paths=["game/tests/test_*.py"])
    assert {"test_file_deleted", "test_skipped", "protected_path_modified"} <= rep.kinds()
    assert not rep.scope_violations
    git(repo, "worktree", "list")
