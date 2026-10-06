"""Sandbox runner: execute generated-code work inside the best available isolation backend.

Containment is reported, never assumed:

* ``containment = "enforced"`` only when a detected rootless backend ran the command with the
  validated mounts and the role's network policy enforced by that backend;
* otherwise the command runs **supervised** on the host (scrubbed environment, no isolation) and
  the evidence says so with the reasons - or, when the owner set ``require_containment``, it is
  refused and the check reports BLOCKED.

Forbidden mounts (home directory, Docker/Podman sockets, signing keystores, credential
directories) are refused before anything runs, in every mode.
"""

from __future__ import annotations

import os
import signal
import subprocess
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional

from ..checks.base import ProcResult
from ..credentials import redact, scrubbed_env
from ..util import new_id
from .backends import Backend, BackendInfo, SandboxRequest, detect_backends, make_backend
from .policy import (
    Mount,
    NetworkMode,
    SandboxConfig,
    SandboxRefused,
    WorkerRole,
    builtin_forbidden_paths,
    validate_mounts,
)

ENFORCED = "enforced"
SUPERVISED = "supervised"
NOT_RUN = "not_run"  # the backend itself could not be started: nothing executed


@dataclass
class SandboxResult:
    proc: ProcResult
    containment: str  # "enforced" | "supervised" | "not_run"
    backend: Optional[str]
    role: str
    network: str
    mounts: list[dict] = field(default_factory=list)
    reasons: list[str] = field(default_factory=list)  # why supervised / backend notes
    argv: list[str] = field(default_factory=list)  # the command as requested (not the backend wrapper)
    config_source: str = "defaults"

    @property
    def contained(self) -> bool:
        return self.containment == ENFORCED

    def evidence(self) -> dict:
        """Evidence fragment: what isolation actually applied to this execution."""
        return {
            "containment": self.containment, "backend": self.backend, "role": self.role,
            "network": self.network, "mounts": self.mounts, "reasons": self.reasons,
            "config_source": self.config_source,
            "statement": (f"executed inside {self.backend} ({self.network}); mounts limited to the listed paths"
                          if self.contained else
                          "NOT RUN: the isolation backend could not be started" if self.containment == NOT_RUN else
                          "NOT CONTAINED: executed supervised on the host account with a scrubbed environment "
                          "but full filesystem and network access"),
        }


Executor = Callable[[list[str], Path, float, dict, Optional[threading.Event]], ProcResult]


def execute(argv: list[str], cwd: Path, timeout_s: float, env: dict, cancel: Optional[threading.Event] = None,
            on_kill: Optional[Callable[[], None]] = None) -> ProcResult:
    """Run ``argv`` in its own process group; kill the whole group on timeout or ``cancel``."""
    t0 = time.monotonic()
    try:
        p = subprocess.Popen(argv, cwd=str(cwd), env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE, text=True, start_new_session=True)
    except OSError as e:
        return ProcResult(None, "", str(e), 0.0, missing_executable=True)
    out: list[str] = []
    err: list[str] = []
    readers = [threading.Thread(target=lambda: out.append(p.stdout.read()), daemon=True),
               threading.Thread(target=lambda: err.append(p.stderr.read()), daemon=True)]
    for r in readers:
        r.start()
    timed_out = False
    while p.poll() is None:
        if time.monotonic() - t0 > timeout_s or (cancel is not None and cancel.is_set()):
            timed_out = True
            try:
                os.killpg(p.pid, signal.SIGKILL)
            except (ProcessLookupError, PermissionError):
                p.kill()
            if on_kill:
                on_kill()
            break
        time.sleep(0.05)
    p.wait()
    for r in readers:
        r.join(timeout=5)
    return ProcResult(None if timed_out else p.returncode, redact("".join(out)), redact("".join(err)),
                      time.monotonic() - t0, timed_out=timed_out)


def _default_executor(argv, cwd, timeout_s, env, cancel=None):
    return execute(argv, cwd, timeout_s, env, cancel)


class SandboxRunner:
    """Chooses a backend per request and executes. Inject ``backends``/``executor`` in tests."""

    def __init__(self, config: SandboxConfig | None = None, *, backends: list[BackendInfo] | None = None,
                 executor: Executor | None = None, forbidden: list[Path] | None = None,
                 home: Path | None = None):
        self.config = config or SandboxConfig()
        self.home = Path(home) if home else Path.home()
        self._backends = backends
        self.executor = executor or _default_executor
        self.forbidden = (forbidden if forbidden is not None else builtin_forbidden_paths(self.home)) + [
            Path(p).expanduser() for p in self.config.forbidden_paths]
        if self.config.source not in ("defaults", ""):
            self.forbidden.append(Path(self.config.source))  # the enforcement config itself

    @property
    def backends(self) -> list[BackendInfo]:
        if self._backends is None:
            self._backends = detect_backends()
        return self._backends

    def choose(self, role: WorkerRole) -> tuple[Optional[Backend], list[str]]:
        """The first available backend that can enforce this role's network policy (+ reasons for skips)."""
        net = self.config.network_for(role)
        reasons: list[str] = []
        if self.config.backend == "none":
            return None, ["owner configured backend = none"]
        wanted = [self.config.backend] if self.config.backend != "auto" else None
        for info in self.backends:
            if wanted and info.name not in wanted:
                continue
            if not info.available:
                reasons.append(f"{info.name}: {info.reason}")
                continue
            b = make_backend(info, self.config)
            ok, why = b.can_enforce(net)
            if not ok:
                reasons.append(f"{info.name}: {why}")
                continue
            return b, reasons
        if wanted and not any(i.name in wanted for i in self.backends):
            reasons.append(f"configured backend {self.config.backend} was not probed")
        return None, reasons or ["no isolation backend available"]

    def run(self, argv: list[str], *, workdir: Path, role: WorkerRole, cwd: Path | None = None,
            timeout_s: float = 600, env_extra: dict[str, str] | None = None,
            extra_mounts: list[Mount] | None = None, gpu: Optional[str] = None,
            cancel: Optional[threading.Event] = None) -> SandboxResult:
        if isinstance(argv, str):
            raise TypeError("argv must be a list (no shell strings)")
        workdir = Path(workdir).resolve()
        cwd = Path(cwd).resolve() if cwd else workdir
        mounts = [Mount(workdir, str(workdir), read_only=False)] + list(extra_mounts or [])
        validate_mounts(mounts, forbidden=self.forbidden, home=self.home)  # raises SandboxRefused before anything runs
        net = self.config.network_for(role)
        backend, reasons = self.choose(role)
        env = {k: v for k, v in scrubbed_env(extra=env_extra).items()} if backend is None else \
            {k: v for k, v in (env_extra or {}).items()}
        if backend is None:
            if self.config.require_containment:
                raise SandboxRefused("no isolation backend can enforce role "
                                     f"{role.value} and the owner requires containment: " + "; ".join(reasons))
            proc = self.executor(list(argv), cwd, timeout_s, env, cancel)
            return SandboxResult(proc, SUPERVISED, None, role.value,
                                 "unrestricted (supervised: no network enforcement)" if net.mode == NetworkMode.DENY
                                 else "unrestricted (supervised: allow-list not enforced)",
                                 [m.to_dict() for m in mounts], reasons, list(argv), self.config.source)
        name = f"forge-{new_id('sbx').split('_', 1)[1]}"
        req = SandboxRequest(argv=list(argv), cwd=cwd, mounts=mounts, network=net, env=env, name=name, gpu=gpu)
        wrapped = backend.argv(req)
        host_env = scrubbed_env()
        proc = self.executor(wrapped, cwd, timeout_s, host_env, cancel)
        if proc.timed_out:
            k = backend.kill_argv(name)
            if k:  # the container may outlive its client process
                self.executor(k, cwd, 60, host_env, None)
        if proc.missing_executable:
            reasons.append(f"{backend.name} could not be started")
        return SandboxResult(proc, NOT_RUN if proc.missing_executable else ENFORCED, backend.name, role.value,
                             backend.network_description(net), [m.to_dict() for m in mounts], reasons, list(argv),
                             self.config.source)

    def describe(self) -> dict:
        return {"config": self.config.source, "backend_setting": self.config.backend,
                "require_containment": self.config.require_containment,
                "backends": [b.to_dict() for b in self.backends],
                "roles": {r.value: self.config.network_for(r).describe() for r in WorkerRole},
                "choice": {r.value: (self.choose(r)[0].name if self.choose(r)[0] else SUPERVISED) for r in WorkerRole}}
