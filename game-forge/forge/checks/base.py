"""Check runner primitives. Checks produce structured outcomes that become Evidence."""

from __future__ import annotations

import contextvars
import os
import shutil
import subprocess
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Optional

from ..credentials import redact, scrubbed_env
from ..models import EvidenceClass, EvidenceStatus, ToolchainManifest


@dataclass
class CheckOutcome:
    name: str
    evidence_class: EvidenceClass
    status: EvidenceStatus
    summary: str
    details: dict[str, Any] = field(default_factory=dict)
    logs: dict[str, str] = field(default_factory=dict)  # name -> text (stored as artifacts)

    @property
    def passed(self) -> bool:
        return self.status == EvidenceStatus.PASS


@dataclass
class CheckContext:
    manifest: Optional[ToolchainManifest] = None
    candidate_hash: Optional[str] = None
    scratch_dir: Optional[Path] = None


class Check:
    name: str = "check"
    evidence_class: EvidenceClass = EvidenceClass.RULES
    #: device/unity checks mark resources the scheduler must lease and verify up-front
    requires: tuple[str, ...] = ()

    def run(self, workdir: Path, ctx: CheckContext) -> CheckOutcome:  # pragma: no cover - interface
        raise NotImplementedError

    def availability(self, ctx: CheckContext) -> tuple[bool, str]:
        """Can this check produce evidence on this machine at all? (used to BLOCK early)."""
        return True, ""


@dataclass
class ProcResult:
    returncode: Optional[int]
    stdout: str
    stderr: str
    duration_s: float
    timed_out: bool = False
    missing_executable: bool = False


#: Optional process wrapper installed by ``forge.sandbox`` while a sandboxed check runs. When set,
#: every ``run_proc`` call made by that check executes inside the isolation backend instead of
#: directly on the host. Context-local, so concurrent checks never see each other's wrapper.
PROC_WRAPPER: contextvars.ContextVar = contextvars.ContextVar("forge_proc_wrapper", default=None)


def run_proc(argv: list[str], cwd: Path, timeout_s: float, env_extra: dict[str, str] | None = None) -> ProcResult:
    wrapper = PROC_WRAPPER.get()
    if wrapper is not None:
        return wrapper(argv, cwd, timeout_s, env_extra)
    return run_proc_direct(argv, cwd, timeout_s, env_extra)


def run_proc_direct(argv: list[str], cwd: Path, timeout_s: float, env_extra: dict[str, str] | None = None
                    ) -> ProcResult:
    """Run on the host (scrubbed environment, no isolation). Used directly only by the sandbox runner."""
    t0 = time.monotonic()
    exe = argv[0]
    if os.sep not in exe and shutil.which(exe) is None:
        return ProcResult(None, "", f"executable not found: {exe}", 0.0, missing_executable=True)
    try:
        p = subprocess.run(
            argv, cwd=str(cwd), capture_output=True, text=True, timeout=timeout_s,
            env=scrubbed_env(extra=env_extra), stdin=subprocess.DEVNULL,
        )
        return ProcResult(p.returncode, redact(p.stdout), redact(p.stderr), time.monotonic() - t0)
    except subprocess.TimeoutExpired as e:
        out = e.stdout.decode(errors="replace") if isinstance(e.stdout, bytes) else (e.stdout or "")
        err = e.stderr.decode(errors="replace") if isinstance(e.stderr, bytes) else (e.stderr or "")
        return ProcResult(None, redact(out), redact(err), time.monotonic() - t0, timed_out=True)
    except OSError as e:  # missing or non-executable binary
        return ProcResult(None, "", str(e), time.monotonic() - t0, missing_executable=True)


class CommandCheck(Check):
    """Generic protected command check (argv list, never a shell string)."""

    def __init__(self, name: str, argv: list[str], *, cwd: str = ".", timeout_s: float = 600,
                 evidence_class: EvidenceClass = EvidenceClass.RULES, env: dict[str, str] | None = None):
        if isinstance(argv, str):
            raise TypeError("argv must be a list (no shell strings)")
        self.name, self.argv, self.cwd, self.timeout_s = name, list(argv), cwd, timeout_s
        self.evidence_class, self.env = evidence_class, env or {}

    def availability(self, ctx: CheckContext) -> tuple[bool, str]:
        exe = self.argv[0]
        if os.sep not in exe and shutil.which(exe) is None:
            return False, f"{exe} is not installed"
        return True, ""

    def run(self, workdir: Path, ctx: CheckContext) -> CheckOutcome:
        r = run_proc(self.argv, workdir / self.cwd, self.timeout_s, self.env)
        details = {"argv": self.argv, "cwd": self.cwd, "exit_code": r.returncode,
                   "duration_s": round(r.duration_s, 3), "timed_out": r.timed_out}
        logs = {"stdout": r.stdout, "stderr": r.stderr}
        if r.missing_executable:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.BLOCKED,
                                f"{self.argv[0]} not available", details, logs)
        if r.timed_out:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.FAIL,
                                f"timed out after {self.timeout_s}s", details, logs)
        status = EvidenceStatus.PASS if r.returncode == 0 else EvidenceStatus.FAIL
        return CheckOutcome(self.name, self.evidence_class, status,
                            f"exit code {r.returncode}", details, logs)
