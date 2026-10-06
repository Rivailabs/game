"""Protected check runners and the registry built from project configuration.

Check definitions live in the project's ``project.toml`` (owned by the owner,
outside every worker's writable scope) - never in the candidate workspace.
"""

from __future__ import annotations

from typing import Any

from ..models import EvidenceClass
from .base import Check, CheckContext, CheckOutcome, CommandCheck, run_proc
from .device import AdbDeviceService, DeviceScenarioCheck, parse_adb_devices
from .dotnet import DotnetTestCheck, parse_console_summary, parse_trx
from .unity import UnityBuildCheck

__all__ = [
    "Check", "CheckContext", "CheckOutcome", "CommandCheck", "DotnetTestCheck", "DeviceScenarioCheck",
    "UnityBuildCheck", "AdbDeviceService", "parse_adb_devices", "parse_console_summary", "parse_trx",
    "build_check", "build_registry", "run_proc",
]


def build_check(name: str, spec: dict[str, Any]) -> Check:
    kind = spec.get("type", "command")
    if kind == "command":
        return CommandCheck(name, list(spec["argv"]), cwd=spec.get("cwd", "."),
                            timeout_s=float(spec.get("timeout_s", 600)),
                            evidence_class=EvidenceClass(spec.get("evidence_class", "rules")))
    if kind == "dotnet_test":
        return DotnetTestCheck(name, project_dir=spec.get("project_dir", "."), target=spec.get("target"),
                               configuration=spec.get("configuration", "Debug"),
                               timeout_s=float(spec.get("timeout_s", 900)))
    if kind == "device":
        return DeviceScenarioCheck(name, serial=spec.get("serial"), apk=spec.get("apk"),
                                   package=spec.get("package", ""), activity=spec.get("activity", ""),
                                   scenario=spec.get("scenario", "smoke"), wait_s=float(spec.get("wait_s", 30)),
                                   adb_path=spec.get("adb_path"))
    if kind == "unity_build":
        return UnityBuildCheck(name, project_dir=spec.get("project_dir", "unity"),
                               build_method=spec.get("build_method", "Forge.Build.BuildAndroid"),
                               output=spec.get("output", "Builds/Android/game.apk"),
                               timeout_s=float(spec.get("timeout_s", 3600)))
    raise ValueError(f"unknown check type {kind!r} for {name}")


def build_registry(specs: dict[str, dict[str, Any]]) -> dict[str, Check]:
    return {name: build_check(name, spec) for name, spec in specs.items()}
