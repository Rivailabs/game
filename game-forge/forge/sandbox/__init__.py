"""Enforced worker containment (R1 gap): isolation backends, network policy per worker role,
validated mounts, the sandboxed check wrapper and the active-work timeout watchdog."""

from .backends import BackendInfo, detect_backends
from .check import SandboxedCheck, containment_of
from .policy import (
    Mount,
    NetworkMode,
    NetworkPolicy,
    SandboxConfig,
    SandboxConfigError,
    SandboxRefused,
    WorkerRole,
    load_sandbox_config,
)
from .runner import ENFORCED, NOT_RUN, SUPERVISED, SandboxResult, SandboxRunner
from .watchdog import ActiveWorkTimeout, call_with_deadline, enforce_active_work_timeouts

__all__ = [
    "ActiveWorkTimeout", "BackendInfo", "ENFORCED", "Mount", "NOT_RUN", "NetworkMode", "NetworkPolicy", "SUPERVISED",
    "SandboxConfig", "SandboxConfigError", "SandboxRefused", "SandboxResult", "SandboxRunner", "SandboxedCheck",
    "WorkerRole", "call_with_deadline", "containment_of", "detect_backends", "enforce_active_work_timeouts",
    "load_sandbox_config",
]
