"""Device service: adb wrapper for a dedicated physical test phone (selected by serial).

Absence never becomes a pass: missing adb, no device, an unauthorised device,
an emulator, a missing build or a scenario that reports nothing all return
INCOMPLETE evidence with an owner-readable reason ("phone disconnected").
Only the scenario's own result marker in logcat can produce PASS/FAIL.
"""

from __future__ import annotations

import re
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from ..models import EvidenceClass, EvidenceStatus
from .base import Check, CheckContext, CheckOutcome, run_proc

RESULT_MARKER = re.compile(r"FORGE_SCENARIO_RESULT:\s*(PASS|FAIL)\b(.*)")


@dataclass
class DeviceInfo:
    serial: str
    state: str
    model: str = ""
    product: str = ""
    transport: str = ""

    @property
    def is_emulator(self) -> bool:
        return self.serial.startswith("emulator-") or "sdk_gphone" in self.product or "generic" in self.product


def parse_adb_devices(text: str) -> list[DeviceInfo]:
    out: list[DeviceInfo] = []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("List of devices") or line.startswith("*"):
            continue
        parts = line.split()
        if len(parts) < 2:
            continue
        info = DeviceInfo(serial=parts[0], state=parts[1])
        for kv in parts[2:]:
            if ":" in kv:
                k, v = kv.split(":", 1)
                if k == "model":
                    info.model = v
                elif k == "product":
                    info.product = v
                elif k == "transport_id":
                    info.transport = v
        out.append(info)
    return out


class AdbDeviceService:
    def __init__(self, adb_path: Optional[str] = None, timeout_s: float = 60):
        self.adb = adb_path or shutil.which("adb")
        self.timeout_s = timeout_s

    def available(self) -> tuple[bool, str]:
        if not self.adb or not Path(self.adb).exists():
            return False, "adb is not installed (Android platform-tools missing)"
        return True, ""

    def _adb(self, args: list[str], serial: str | None = None, timeout: float | None = None):
        argv = [self.adb] + (["-s", serial] if serial else []) + args
        return run_proc(argv, Path.cwd(), timeout or self.timeout_s)

    def list_devices(self) -> list[DeviceInfo]:
        ok, _ = self.available()
        if not ok:
            return []
        r = self._adb(["devices", "-l"])
        if r.returncode != 0:
            return []
        return parse_adb_devices(r.stdout)

    def select(self, serial: str | None) -> tuple[Optional[DeviceInfo], str]:
        ok, reason = self.available()
        if not ok:
            return None, reason
        devices = self.list_devices()
        if serial:
            match = [d for d in devices if d.serial == serial]
            if not match:
                return None, f"phone disconnected (serial {serial} not attached)"
            d = match[0]
        else:
            physical = [d for d in devices if not d.is_emulator]
            if len(physical) != 1:
                return None, ("no physical phone connected" if not physical
                              else "several phones connected; configure a device serial")
            d = physical[0]
        if d.state == "unauthorized":
            return None, f"phone {d.serial} has not authorised USB debugging (accept the dialog on the phone)"
        if d.state != "device":
            return None, f"phone {d.serial} is {d.state}"
        if d.is_emulator:
            return None, f"{d.serial} is an emulator; emulators never satisfy the physical-device gate"
        return d, ""

    def getprop(self, serial: str, prop: str) -> str:
        r = self._adb(["shell", "getprop", prop], serial)
        return (r.stdout or "").strip()


class DeviceScenarioCheck(Check):
    evidence_class = EvidenceClass.DEVICE
    requires = ("device",)

    def __init__(self, name: str = "device-scenario", *, serial: str | None = None, apk: str | None = None,
                 package: str = "", activity: str = "", scenario: str = "smoke", wait_s: float = 30,
                 adb_path: str | None = None):
        self.name, self.serial, self.apk, self.package = name, serial, apk, package
        self.activity, self.scenario, self.wait_s = activity, scenario, wait_s
        self.service = AdbDeviceService(adb_path)

    def availability(self, ctx: CheckContext) -> tuple[bool, str]:
        d, reason = self.service.select(self.serial)
        return (d is not None), reason

    def run(self, workdir: Path, ctx: CheckContext) -> CheckOutcome:
        d, reason = self.service.select(self.serial)
        details: dict = {"serial": self.serial, "scenario": self.scenario}
        if d is None:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.INCOMPLETE, reason, details)
        details.update({
            "serial": d.serial, "model": d.model or self.service.getprop(d.serial, "ro.product.model"),
            "android_version": self.service.getprop(d.serial, "ro.build.version.release"),
            "abi": self.service.getprop(d.serial, "ro.product.cpu.abi"),
        })
        logs: dict[str, str] = {}
        if not self.apk or not (workdir / self.apk).exists():
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.INCOMPLETE,
                                "no build artifact to install (Android build not produced)", details)
        inst = self.service._adb(["install", "-r", str(workdir / self.apk)], d.serial, timeout=300)
        logs["install.log"] = inst.stdout + inst.stderr
        if inst.returncode != 0:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.INCOMPLETE,
                                "install failed or device disconnected during install", details, logs)
        self.service._adb(["logcat", "-c"], d.serial)
        start = self.service._adb(["shell", "am", "start", "-W", "-n", f"{self.package}/{self.activity}",
                                   "-e", "forge_scenario", self.scenario], d.serial)
        logs["launch.log"] = start.stdout + start.stderr
        self.service._adb(["shell", "sleep", str(int(self.wait_s))], d.serial, timeout=self.wait_s + 30)
        lc = self.service._adb(["logcat", "-d", "-v", "time"], d.serial)
        logs["logcat.txt"] = lc.stdout
        shot = self.service._adb(["exec-out", "screencap", "-p"], d.serial)
        details["screenshot_captured"] = bool(shot.stdout)
        if lc.returncode != 0:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.INCOMPLETE,
                                "phone disconnected while collecting logs", details, logs)
        m = None
        for m in RESULT_MARKER.finditer(lc.stdout):
            pass
        if m is None:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.INCOMPLETE,
                                "scenario did not report a result (interrupted run?)", details, logs)
        status = EvidenceStatus.PASS if m.group(1) == "PASS" else EvidenceStatus.FAIL
        details["scenario_report"] = m.group(2).strip()
        return CheckOutcome(self.name, self.evidence_class, status, f"scenario {self.scenario}: {m.group(1)}",
                            details, logs)
