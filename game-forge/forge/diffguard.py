"""Diff guard: detect candidates that weaken their own gates.

Plan: "A change that deletes a failing test, disables a gate or changes a
threshold requires separate review and cannot earn a normal automatic pass."

The guard is deliberately conservative (it prefers false positives, which only
route a candidate to separate human review). It inspects a unified diff plus the
name-status list and reports:

* ``scope_violation``        - file changed outside the task's permitted paths (hard failure)
* ``protected_path_modified`` - a protected acceptance file was touched
* ``test_file_deleted``       - a test source file was deleted or renamed away
* ``test_case_removed``       - net removal of test cases (attributes / test functions)
* ``assertion_removed``       - net removal of assertions in a test file
* ``test_skipped``            - a skip/ignore marker was added
* ``threshold_changed``       - a numeric literal changed on a threshold-like line
* ``gate_disabled``           - CI / test configuration weakened or a gate bypass added
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from .gitops import ChangedFile
from .pathglob import match_path

TEST_FILE_PATTERNS = (
    "**/tests/**", "**/test/**", "**/Tests/**", "**/*Tests.cs", "**/*Test.cs", "**/*.Tests/**",
    "**/test_*.py", "**/*_test.py", "**/*.spec.ts", "**/*.test.ts", "**/*.spec.js", "**/*.test.js",
    "**/*_test.go", "**/Editor/Tests/**", "**/PlayMode/**", "**/EditMode/**",
)

GATE_FILE_PATTERNS = (
    ".github/workflows/**", "**/*.runsettings", "**/Directory.Build.props", "**/Directory.Build.targets",
    "**/*.csproj", "**/pytest.ini", "**/pyproject.toml", "**/setup.cfg", "**/tox.ini", "**/conftest.py",
    "**/.gitlab-ci.yml", "**/forge-gates/**", "**/*thresholds*", "**/*budgets*", "**/*.gate.*",
    "**/project.toml",
)

TEST_CASE_RE = re.compile(
    r"\[(?:Test|TestCase|TestCaseSource|Fact|Theory|UnityTest|TestMethod)\b|"
    r"^\s*(?:async\s+)?def\s+test_|^\s*(?:it|test)\s*\(|@Test\b|func\s+Test[A-Z]"
)
ASSERT_RE = re.compile(r"\bAssert(?:\.\w+)?\s*\(|\bassert\b|\bexpect\s*\(|\bShould\(|\.Should\w*\(")
SKIP_RE = re.compile(
    r"[\[,]\s*Ignore\b|[\[,]\s*Explicit\b|Assert\.Ignore\s*\(|Assert\.Inconclusive\s*\(|Assert\.Pass\s*\(|"
    r"\[Fact\s*\(\s*Skip|\[Theory\s*\(\s*Skip|Skip\s*=\s*\"|pytest\.mark\.skip|pytest\.skip\s*\(|"
    r"unittest\.skip|@skip\b|\bxit\s*\(|\bit\.skip\s*\(|\btest\.skip\s*\(|\bdescribe\.skip\s*\(|"
    r"#if\s+false|\[Category\s*\(\s*\"(?:Skip|Disabled)|return;\s*//\s*skip",
    re.I,
)
GATE_BYPASS_RE = re.compile(
    r"continue-on-error\s*:\s*true|\|\|\s*true\b|\bexit\s+0\b|--no-verify|if\s*:\s*false|"
    r"<IsTestProject>\s*false|<TreatWarningsAsErrors>\s*false|enabled\s*=\s*false|"
    r"--filter\s|TestCaseFilter|SkipTests|-DskipTests|--exclude|--ignore\b|\bdisable(?:d)?_?gate",
    re.I,
)
THRESHOLD_WORD_RE = re.compile(
    r"threshold|tolerance|epsilon|budget|limit|ceiling|floor|margin|max|min|timeout|percent|"
    r"fps|frame|latency|memory|\bmb\b|\bms\b|quota|cells|expected",
    re.I,
)
SCRIPT_CONFIG_PATTERNS = (
    "**/*.sh", "**/*.bash", "**/*.ps1", "**/*.cmd", "**/*.bat", "**/Makefile", "**/*.yml", "**/*.yaml",
    "**/*.toml", "**/*.ini", "**/*.cfg", "**/*.props", "**/*.targets", "**/*.csproj", "**/*.runsettings",
    "**/*.json",
)
NUM_RE = re.compile(r"(?<![A-Za-z_])[-+]?\d+(?:[.,_]\d+)*(?:[eE][-+]?\d+)?")


@dataclass
class GuardFlag:
    kind: str
    path: str
    detail: str
    line: str = ""


@dataclass
class DiffGuardReport:
    flags: list[GuardFlag] = field(default_factory=list)
    scope_violations: list[str] = field(default_factory=list)

    @property
    def requires_separate_review(self) -> bool:
        return bool(self.flags)

    @property
    def clean(self) -> bool:
        return not self.flags and not self.scope_violations

    def kinds(self) -> set[str]:
        return {f.kind for f in self.flags}

    def to_dict(self) -> dict:
        return {
            "flags": [f.__dict__ for f in self.flags],
            "scope_violations": self.scope_violations,
            "requires_separate_review": self.requires_separate_review,
        }


def _parse_unified(diff: str) -> dict[str, dict[str, list[list[str]]]]:
    """Return {path: {"hunks": [[lines...], ...]}} where lines keep their +/- prefix."""
    files: dict[str, dict[str, list[list[str]]]] = {}
    cur: str | None = None
    old: str | None = None
    hunk: list[str] | None = None
    for line in diff.splitlines():
        if line.startswith("diff --git "):
            cur, hunk = None, None
            continue
        if line.startswith("--- "):
            old = line[4:].strip()
            old = old[2:] if old.startswith("a/") else old
            continue
        if line.startswith("+++ "):
            new = line[4:].strip()
            new = new[2:] if new.startswith("b/") else new
            cur = old if new == "/dev/null" else new
            files.setdefault(cur, {"hunks": []})
            continue
        if line.startswith("@@"):
            if cur is not None:
                hunk = []
                files[cur]["hunks"].append(hunk)
            continue
        if hunk is not None and line[:1] in ("+", "-", " "):
            hunk.append(line)
    return files


def _skeleton(s: str) -> str:
    return NUM_RE.sub("#", s.strip())


def analyze(
    diff: str,
    changed: list[ChangedFile],
    *,
    permitted_paths: list[str] | None = None,
    protected_paths: list[str] | None = None,
) -> DiffGuardReport:
    rep = DiffGuardReport()
    protected_paths = protected_paths or []

    for cf in changed:
        paths = [cf.path] + ([cf.old_path] if cf.old_path else [])
        if permitted_paths is not None:
            for p in paths:
                if not match_path(p, permitted_paths) and p not in rep.scope_violations:
                    rep.scope_violations.append(p)
        for p in paths:
            if protected_paths and match_path(p, protected_paths):
                rep.flags.append(GuardFlag("protected_path_modified", p, f"{cf.status} on protected path"))
        if cf.status == "D" and match_path(cf.path, TEST_FILE_PATTERNS):
            rep.flags.append(GuardFlag("test_file_deleted", cf.path, "test source deleted"))
        if cf.status == "R" and cf.old_path and match_path(cf.old_path, TEST_FILE_PATTERNS) \
                and not match_path(cf.path, TEST_FILE_PATTERNS):
            rep.flags.append(GuardFlag("test_file_deleted", cf.old_path, f"test renamed out of test paths to {cf.path}"))
        if match_path(cf.path, GATE_FILE_PATTERNS) and cf.status in ("M", "D", "R"):
            rep.flags.append(GuardFlag("gate_disabled", cf.path, f"gate/test configuration {cf.status}"))

    for path, info in _parse_unified(diff).items():
        is_test = match_path(path, TEST_FILE_PATTERNS)
        is_gate_cfg = match_path(path, GATE_FILE_PATTERNS) or match_path(path, SCRIPT_CONFIG_PATTERNS)
        removed_cases = added_cases = removed_asserts = added_asserts = 0
        for hunk in info["hunks"]:
            minus = [l[1:] for l in hunk if l.startswith("-")]
            plus = [l[1:] for l in hunk if l.startswith("+")]
            removed_cases += sum(1 for l in minus if TEST_CASE_RE.search(l))
            added_cases += sum(1 for l in plus if TEST_CASE_RE.search(l))
            removed_asserts += sum(1 for l in minus if ASSERT_RE.search(l))
            added_asserts += sum(1 for l in plus if ASSERT_RE.search(l))
            for l in plus:
                if SKIP_RE.search(l) and not any(SKIP_RE.search(m) for m in minus):
                    rep.flags.append(GuardFlag("test_skipped", path, "skip/ignore marker added", l.strip()))
                if is_gate_cfg and GATE_BYPASS_RE.search(l):
                    rep.flags.append(GuardFlag("gate_disabled", path, "gate bypass pattern added", l.strip()))
            # threshold edits: same line skeleton, different numbers
            plus_by_skel: dict[str, list[str]] = {}
            for l in plus:
                if NUM_RE.search(l):
                    plus_by_skel.setdefault(_skeleton(l), []).append(l)
            for m in minus:
                if not NUM_RE.search(m) or not THRESHOLD_WORD_RE.search(m):
                    continue
                for p in plus_by_skel.get(_skeleton(m), []):
                    if NUM_RE.findall(p) != NUM_RE.findall(m):
                        rep.flags.append(GuardFlag("threshold_changed", path,
                                                   f"{m.strip()!r} -> {p.strip()!r}", p.strip()))
                        break
        if removed_cases > added_cases:
            rep.flags.append(GuardFlag("test_case_removed", path,
                                       f"{removed_cases - added_cases} test case(s) net removed"))
        if is_test and removed_asserts > added_asserts:
            rep.flags.append(GuardFlag("assertion_removed", path,
                                       f"{removed_asserts - added_asserts} assertion(s) net removed"))
    return rep
