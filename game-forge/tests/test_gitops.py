"""Workspace isolation and the single integration writer."""

import pytest

from forge.gitops import BaseMoved, IntegrationWriter, WorkspaceManager
from forge.pathglob import is_safe_relative, match_path

from .conftest import GOOD_MUL, git


def test_worktree_rooted_at_pinned_commit(repo, tmp_path):
    ws = WorkspaceManager(repo, tmp_path / "wt")
    base = ws.head("forge/accepted")
    p1, b1 = ws.create("a1", base)
    p2, b2 = ws.create("a2", base)
    assert p1 != p2 and b1 != b2
    assert git(p1, "rev-parse", "HEAD") == base
    (p1 / "game" / "src" / "calc.py").write_text(GOOD_MUL)
    c1 = ws.commit_candidate(p1, "cand")
    assert c1 and c1 != base
    assert (p2 / "game" / "src" / "calc.py").read_text() != GOOD_MUL  # isolated
    assert ws.commit_candidate(p2, "nothing") is None
    assert git(repo, "rev-parse", "forge/accepted") == base  # workers never touch accepted main
    ws.remove(p1, None)
    assert not p1.exists()


def test_restore_protected_paths(repo, tmp_path):
    ws = WorkspaceManager(repo, tmp_path / "wt")
    base = ws.head("main")
    p, _ = ws.create("a1", base)
    (p / "game" / "tests" / "test_mul.py").write_text("tampered")
    (p / "game" / "tests" / "test_new.py").write_text("added")
    restored = ws.restore_protected(p, base, ["game/tests/test_*.py"])
    assert set(restored) == {"game/tests/test_mul.py", "game/tests/test_new.py"}
    assert "tampered" not in (p / "game" / "tests" / "test_mul.py").read_text()
    assert not (p / "game" / "tests" / "test_new.py").exists()


def _candidate(ws, name, base, content):
    p, _ = ws.create(name, base)
    (p / "game" / "src" / "calc.py").write_text(content)
    return ws.commit_candidate(p, name)


def test_integration_fast_forward_and_promote(repo, tmp_path):
    ws = WorkspaceManager(repo, tmp_path / "wt")
    base = ws.head("forge/accepted")
    cand = _candidate(ws, "a1", base, GOOD_MUL)
    w = IntegrationWriter(repo, "forge/accepted", tmp_path / "staging")
    st = w.stage(cand)
    assert st.ok and st.fast_forward and st.staged_sha == cand and st.base_main == base
    w.promote(st.staged_sha, st.base_main)
    assert git(repo, "rev-parse", "forge/accepted") == cand


def test_integration_rejects_when_base_moved(repo, tmp_path):
    ws = WorkspaceManager(repo, tmp_path / "wt")
    base = ws.head("forge/accepted")
    cand = _candidate(ws, "a1", base, GOOD_MUL)
    w = IntegrationWriter(repo, "forge/accepted", tmp_path / "staging")
    st = w.stage(cand)
    # someone else advanced accepted main between staging and promotion
    other = _candidate(ws, "a2", base, GOOD_MUL.replace("a * b", "b * a"))
    git(repo, "update-ref", "refs/heads/forge/accepted", other, base)
    with pytest.raises(BaseMoved):
        w.promote(st.staged_sha, st.base_main)
    assert git(repo, "rev-parse", "forge/accepted") == other  # untouched
    # restaging against the new main produces a merge commit (or conflict) - never a blind push
    st2 = w.stage(cand)
    assert st2.base_main == other
    assert st2.conflict or st2.staged_sha != cand


def test_merge_conflict_detected(repo, tmp_path):
    ws = WorkspaceManager(repo, tmp_path / "wt")
    base = ws.head("forge/accepted")
    c1 = _candidate(ws, "a1", base, "x = 1\n")
    c2 = _candidate(ws, "a2", base, "x = 2\n")
    w = IntegrationWriter(repo, "forge/accepted", tmp_path / "staging")
    st = w.stage(c1)
    w.promote(st.staged_sha, st.base_main)
    st2 = w.stage(c2)
    assert not st2.ok and st2.conflict
    st3 = w.stage(c1)  # staging workspace is reusable after an aborted merge
    assert st3.ok


def test_non_conflicting_merge_creates_new_commit(repo, tmp_path):
    ws = WorkspaceManager(repo, tmp_path / "wt")
    base = ws.head("forge/accepted")
    c1 = _candidate(ws, "a1", base, GOOD_MUL)
    p, _ = ws.create("a2", base)
    (p / "README.md").write_text("changed readme\n")
    c2 = ws.commit_candidate(p, "readme")
    w = IntegrationWriter(repo, "forge/accepted", tmp_path / "staging")
    st = w.stage(c1)
    w.promote(st.staged_sha, st.base_main)
    st2 = w.stage(c2)
    assert st2.ok and not st2.fast_forward and st2.staged_sha not in (c1, c2)
    w.promote(st2.staged_sha, st2.base_main)
    assert git(repo, "show", "forge/accepted:README.md") == "changed readme"


@pytest.mark.parametrize("path,patterns,expected", [
    ("game/src/calc.py", ["game/src/**"], True),
    ("game/src/deep/x.py", ["game/src/**"], True),
    ("game/tests/test_a.py", ["game/src/**"], False),
    ("game/tests/test_a.py", ["**/test_*.py"], True),
    ("a/b/Tests/X.cs", ["**/Tests/**"], True),
    ("README.md", ["*.md"], True),
    ("docs/README.md", ["*.md"], False),
])
def test_glob_matching(path, patterns, expected):
    assert match_path(path, patterns) is expected


@pytest.mark.parametrize("p,ok", [("a/b.txt", True), ("../x", False), ("/etc/passwd", False), (".git/config", False),
                                  ("a/../../b", False), ("a/.git/x", False)])
def test_safe_relative_paths(p, ok):
    assert is_safe_relative(p) is ok
