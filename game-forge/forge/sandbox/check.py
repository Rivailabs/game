"""Run any protected check inside the sandbox.

``SandboxedCheck`` wraps an existing check (command, dotnet_test, unity_build ...). While the
inner check runs, every ``run_proc`` call it makes is routed through :class:`SandboxRunner`
(``forge.checks.base.PROC_WRAPPER``), so the check code itself is unchanged. An optional
``restore_argv`` runs first under the dependency-restore role (allow-listed registries); the
check then runs under its own role, which denies network by default.

Evidence always carries ``details["containment"]``. When any step ran supervised (no backend)
the outcome summary says so and the orchestrator routes the candidate to owner approval: a
supervised run is never presented as contained.
"""

from __future__ import annotations

import threading
from pathlib import Path
from typing import Any, Optional

from ..checks.base import PROC_WRAPPER, Check, CheckContext, CheckOutcome, ProcResult
from ..models import EvidenceStatus
from .policy import (
    SandboxConfig,
    SandboxConfigError,
    SandboxRefused,
    WorkerRole,
    default_config_path,
    load_sandbox_config,
)
from .runner import ENFORCED, NOT_RUN, SUPERVISED, SandboxResult, SandboxRunner


class SandboxedCheck(Check):
    def __init__(self, inner: Check, runner: SandboxRunner | None = None, *,
                 role: WorkerRole = WorkerRole.GENERATED_CODE_CHECK, restore_argv: list[str] | None = None,
                 restore_cwd: str = ".", restore_timeout_s: float = 900, config_error: str = ""):
        self.inner = inner
        self.name = inner.name
        self.evidence_class = inner.evidence_class
        self.requires = inner.requires
        self.runner = runner or SandboxRunner()
        self.role = role
        self.restore_argv = list(restore_argv) if restore_argv else None
        self.restore_cwd = restore_cwd
        self.restore_timeout_s = restore_timeout_s
        self.config_error = config_error
        #: Set by the active-work watchdog to kill the running process group.
        self.cancel = threading.Event()

    @classmethod
    def from_spec(cls, inner: Check, spec: dict[str, Any], *, runner: SandboxRunner | None = None) -> "SandboxedCheck":
        role = WorkerRole(spec.get("sandbox") if spec.get("sandbox") not in (True, "true") else
                          WorkerRole.GENERATED_CODE_CHECK.value)
        err = ""
        if runner is None:
            try:
                cfg = load_sandbox_config(spec.get("sandbox_config") or default_config_path())
            except SandboxConfigError as e:
                cfg, err = SandboxConfig(require_containment=True), str(e)
            runner = SandboxRunner(cfg)
        return cls(inner, runner, role=role, restore_argv=spec.get("restore_argv"),
                   restore_cwd=spec.get("restore_cwd", "."), restore_timeout_s=float(spec.get("restore_timeout_s", 900)),
                   config_error=err)

    def availability(self, ctx: CheckContext) -> tuple[bool, str]:
        if self.config_error:
            return False, f"sandbox configuration refused: {self.config_error}"
        return self.inner.availability(ctx)

    def run(self, workdir: Path, ctx: CheckContext) -> CheckOutcome:
        if self.config_error:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.BLOCKED,
                                f"sandbox configuration refused: {self.config_error}",
                                {"containment": {"containment": NOT_RUN, "reasons": [self.config_error]}})
        records: list[SandboxResult] = []
        refused: list[str] = []
        logs: dict[str, str] = {}
        self.cancel.clear()

        def wrapper(role: WorkerRole):
            def _run(argv: list[str], cwd: Path, timeout_s: float, env_extra: Optional[dict] = None) -> ProcResult:
                try:
                    r = self.runner.run(argv, workdir=workdir, role=role, cwd=cwd, timeout_s=timeout_s,
                                        env_extra=env_extra, cancel=self.cancel)
                except SandboxRefused as e:
                    refused.append(str(e))
                    return ProcResult(None, "", f"sandbox refused: {e}", 0.0, missing_executable=True)
                records.append(r)
                return r.proc
            return _run

        if self.restore_argv:
            tok = PROC_WRAPPER.set(wrapper(WorkerRole.DEPENDENCY_RESTORE))
            try:
                from ..checks.base import run_proc

                rr = run_proc(self.restore_argv, workdir / self.restore_cwd, self.restore_timeout_s)
            finally:
                PROC_WRAPPER.reset(tok)
            logs["restore.log"] = rr.stdout + "\n" + rr.stderr
            if refused or rr.missing_executable or rr.timed_out or rr.returncode != 0:
                why = refused[0] if refused else ("timed out" if rr.timed_out else f"exit {rr.returncode}")
                return CheckOutcome(self.name, self.evidence_class,
                                    EvidenceStatus.BLOCKED if refused or rr.missing_executable else EvidenceStatus.FAIL,
                                    f"dependency restore failed: {why}", self._details(records, refused), logs)
        tok = PROC_WRAPPER.set(wrapper(self.role))
        try:
            oc = self.inner.run(workdir, ctx)
        finally:
            PROC_WRAPPER.reset(tok)
        oc.details = {**oc.details, **self._details(records, refused)}
        oc.logs = {**logs, **oc.logs}
        if refused:
            oc.status = EvidenceStatus.BLOCKED
            oc.summary = f"sandbox refused: {refused[0]}"
        elif any(r.containment == SUPERVISED for r in records):
            oc.summary = f"{oc.summary} [SUPERVISED: not contained]"
        return oc

    def _details(self, records: list[SandboxResult], refused: list[str]) -> dict:
        if refused:
            mode = NOT_RUN
        elif records and all(r.containment == ENFORCED for r in records):
            mode = ENFORCED
        elif any(r.containment == SUPERVISED for r in records):
            mode = SUPERVISED
        else:
            mode = NOT_RUN
        return {"containment": {"containment": mode, "refused": refused,
                                "executions": [r.evidence() for r in records]}}


def containment_of(details: dict) -> Optional[str]:
    """``enforced`` / ``supervised`` / ``not_run`` for evidence produced by a sandboxed check, else None."""
    c = details.get("containment")
    return c.get("containment") if isinstance(c, dict) else None
