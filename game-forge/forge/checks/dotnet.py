"""`dotnet build` + `dotnet test` preset with structured result parsing (TRX + console)."""

from __future__ import annotations

import re
import shutil
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

from ..models import EvidenceClass, EvidenceStatus
from .base import Check, CheckContext, CheckOutcome, run_proc

DOTNET_ENV = {"DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1",
              "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1", "MSBUILDDISABLENODEREUSE": "1"}

SUMMARY_RE = re.compile(
    r"(?P<verdict>Passed|Failed)!\s*-\s*Failed:\s*(?P<failed>\d+),\s*Passed:\s*(?P<passed>\d+),\s*"
    r"Skipped:\s*(?P<skipped>\d+),\s*Total:\s*(?P<total>\d+)"
)
LEGACY_RE = re.compile(r"Total tests:\s*(?P<total>\d+)")
NO_TESTS_RE = re.compile(r"No test is available|No test matches the given testcase filter", re.I)
BUILD_ERR_RE = re.compile(r"error [A-Z]+\d+:.*")


def parse_console_summary(text: str) -> dict:
    """Aggregate every per-assembly summary line ("Passed!  - Failed: 0, Passed: 3, ...")."""
    agg = {"total": 0, "passed": 0, "failed": 0, "skipped": 0, "assemblies": 0}
    for m in SUMMARY_RE.finditer(text):
        agg["assemblies"] += 1
        for k in ("total", "passed", "failed", "skipped"):
            agg[k] += int(m.group(k))
    if agg["assemblies"] == 0:
        m = LEGACY_RE.search(text)
        if m:
            agg["total"] = int(m.group("total"))
    agg["no_tests"] = bool(NO_TESTS_RE.search(text))
    return agg


def parse_trx(path: Path) -> dict:
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    root = ET.parse(path).getroot()
    c = root.find(".//t:ResultSummary/t:Counters", ns)
    counters = {k: int(v) for k, v in (c.attrib.items() if c is not None else []) if v.isdigit()}
    failures = []
    tests = []
    for r in root.findall(".//t:Results/t:UnitTestResult", ns):
        outcome = r.get("outcome", "")
        tests.append({"name": r.get("testName"), "outcome": outcome, "duration": r.get("duration")})
        if outcome.lower() == "failed":
            msg = r.find(".//t:ErrorInfo/t:Message", ns)
            failures.append({"name": r.get("testName"), "message": (msg.text or "").strip()[:2000] if msg is not None else ""})
    return {"counters": counters, "failures": failures, "tests": tests}


class DotnetTestCheck(Check):
    evidence_class = EvidenceClass.RULES

    def __init__(self, name: str = "dotnet-test", *, project_dir: str = ".", target: str | None = None,
                 configuration: str = "Debug", timeout_s: float = 900):
        self.name, self.project_dir, self.target = name, project_dir, target
        self.configuration, self.timeout_s = configuration, timeout_s

    def availability(self, ctx: CheckContext) -> tuple[bool, str]:
        if shutil.which("dotnet") is None:
            return False, "dotnet SDK is not installed"
        return True, ""

    def run(self, workdir: Path, ctx: CheckContext) -> CheckOutcome:
        cwd = workdir / self.project_dir
        tgt = [self.target] if self.target else []
        logs: dict[str, str] = {}
        build = run_proc(["dotnet", "build", *tgt, "-c", self.configuration, "-nologo"], cwd, self.timeout_s, DOTNET_ENV)
        logs["build.log"] = build.stdout + "\n" + build.stderr
        details: dict = {"project_dir": self.project_dir, "configuration": self.configuration,
                         "build_exit_code": build.returncode, "build_duration_s": round(build.duration_s, 2)}
        if build.missing_executable:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.BLOCKED,
                                "dotnet SDK not available", details, logs)
        if build.timed_out or build.returncode != 0:
            errs = sorted(set(BUILD_ERR_RE.findall(build.stdout)))[:30]
            details["build_errors"] = errs
            details["failure_category"] = "compile_error"
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.FAIL,
                                "build timed out" if build.timed_out else f"build failed ({len(errs)} error(s))",
                                details, logs)
        results = Path(tempfile.mkdtemp(prefix="forge-trx-", dir=ctx.scratch_dir))
        test = run_proc(
            ["dotnet", "test", *tgt, "-c", self.configuration, "--no-build", "-nologo",
             "--logger", "trx;LogFileName=forge.trx", "--results-directory", str(results)],
            cwd, self.timeout_s, DOTNET_ENV,
        )
        logs["test.log"] = test.stdout + "\n" + test.stderr
        console = parse_console_summary(test.stdout + test.stderr)
        details.update({"test_exit_code": test.returncode, "test_duration_s": round(test.duration_s, 2),
                        "console_summary": console})
        trx_files = sorted(results.rglob("*.trx"))
        total = passed = failed = skipped = 0
        failures: list = []
        if trx_files:
            for f in trx_files:
                logs[f"trx/{f.name}"] = f.read_text(errors="replace")
                t = parse_trx(f)
                cnt = t["counters"]
                total += cnt.get("total", 0)
                passed += cnt.get("passed", 0)
                failed += cnt.get("failed", 0) + cnt.get("error", 0) + cnt.get("timeout", 0) + cnt.get("aborted", 0)
                skipped += cnt.get("notExecuted", 0)
                failures += t["failures"]
            details["source"] = "trx"
        else:
            total, passed, failed, skipped = console["total"], console["passed"], console["failed"], console["skipped"]
            details["source"] = "console"
        shutil.rmtree(results, ignore_errors=True)
        details.update({"total": total, "passed": passed, "failed": failed, "skipped": skipped, "failures": failures})
        if test.timed_out:
            details["failure_category"] = "test_failure"
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.FAIL,
                                f"dotnet test timed out after {self.timeout_s}s", details, logs)
        if failed or (test.returncode not in (0, None) and not console["no_tests"]):
            details["failure_category"] = "test_failure"
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.FAIL,
                                f"{failed} failed / {total} tests", details, logs)
        if total == 0:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.INCOMPLETE,
                                "no tests were discovered; this is not a pass", details, logs)
        return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.PASS,
                            f"{passed} passed, {skipped} skipped, {total} total", details, logs)
