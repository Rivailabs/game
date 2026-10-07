"""Preflight: record the actual machine and toolchain into a machine-readable manifest.

Facts that cannot be verified here (free VRAM under load, Unity activation,
Android build modules, provider entitlement, promotional credits...) are listed
under ``not_verified`` instead of being assumed.
"""

from __future__ import annotations

import glob
import os
import platform
import re
import shutil
from pathlib import Path
from typing import Optional

from .checks.base import run_proc
from .checks.device import AdbDeviceService
from .models import ToolchainManifest, ToolStatus
from .util import SystemClock

SUPPORTED_PROFILE = "linux-x86_64-ubuntu-lts"


def _version(argv: list[str], pattern: str = r"(\d+\.\d+(?:\.\d+)*\S*)", timeout: float = 30) -> tuple[bool, str, str]:
    exe = shutil.which(argv[0]) if os.sep not in argv[0] else (argv[0] if Path(argv[0]).exists() else None)
    if not exe:
        return False, "", ""
    r = run_proc([exe] + argv[1:], Path.cwd(), timeout)
    text = (r.stdout + "\n" + r.stderr).strip()
    m = re.search(pattern, text)
    return r.returncode == 0, (m.group(1) if m else text.splitlines()[0] if text else ""), exe


def _ram_gb() -> Optional[float]:
    try:
        for line in Path("/proc/meminfo").read_text().splitlines():
            if line.startswith("MemTotal:"):
                return round(int(line.split()[1]) / 1024 / 1024, 2)
    except OSError:
        pass
    return None


def _cpu() -> dict:
    info = {"machine": platform.machine(), "logical_cpus": os.cpu_count(), "model": platform.processor()}
    try:
        for line in Path("/proc/cpuinfo").read_text().splitlines():
            if line.startswith("model name"):
                info["model"] = line.split(":", 1)[1].strip()
                break
    except OSError:
        pass
    return info


def _os() -> dict:
    d = {"system": platform.system(), "release": platform.release(), "node": platform.node()}
    try:
        osr = dict(
            line.split("=", 1) for line in Path("/etc/os-release").read_text().splitlines() if "=" in line
        )
        d["distribution"] = osr.get("PRETTY_NAME", "").strip('"')
        d["distribution_id"] = osr.get("ID", "").strip('"')
        d["version_id"] = osr.get("VERSION_ID", "").strip('"')
    except OSError:
        pass
    return d


def _gpus() -> tuple[list[dict], str]:
    if not shutil.which("nvidia-smi"):
        return [], "nvidia-smi not found (no NVIDIA GPU/driver detected)"
    r = run_proc(["nvidia-smi", "--query-gpu=name,memory.total,memory.free,driver_version,compute_cap",
                  "--format=csv,noheader,nounits"], Path.cwd(), 30)
    if r.returncode != 0:
        return [], f"nvidia-smi failed: {r.stderr.strip()[:200]}"
    gpus = []
    for line in r.stdout.strip().splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) >= 4:
            gpus.append({"name": parts[0], "memory_total_gb": round(float(parts[1]) / 1024, 2),
                         "memory_free_gb_at_preflight": round(float(parts[2]) / 1024, 2),
                         "driver_version": parts[3], "compute_capability": parts[4] if len(parts) > 4 else None})
    return gpus, ""


def find_unity_editor(explicit: str | None = None) -> Optional[str]:
    candidates = [explicit, os.environ.get("FORGE_UNITY_EDITOR")]
    candidates += sorted(glob.glob(os.path.expanduser("~/Unity/Hub/Editor/*/Editor/Unity")), reverse=True)
    candidates += sorted(glob.glob("/opt/unity/Editor/Unity")) + sorted(glob.glob("/opt/Unity/Hub/Editor/*/Editor/Unity"))
    for c in candidates:
        if c and Path(c).exists() and os.access(c, os.X_OK):
            return c
    return None


def run_preflight(data_dir: str | Path, *, unity_editor: str | None = None, blender: str = "blender",
                  provider_credentials: dict[str, bool] | None = None) -> ToolchainManifest:
    tools: dict[str, ToolStatus] = {}
    for name, argv, pat in [
        ("git", ["git", "--version"], r"git version (\S+)"),
        ("dotnet", ["dotnet", "--version"], r"(\d+\.\d+\.\d+\S*)"),
        ("adb", ["adb", "version"], r"Android Debug Bridge version (\S+)"),
        ("blender", [blender, "--version"], r"Blender (\S+)"),
        ("python", ["python3", "--version"], r"Python (\S+)"),
    ]:
        ok, ver, path = _version(argv, pat)
        tools[name] = ToolStatus(name=name, found=bool(path), path=path or None, version=ver or None,
                                 verified=ok, note="" if path else "not installed / not on PATH")
    editor = find_unity_editor(unity_editor)
    unity_ver = None
    if editor:
        m = re.search(r"Editor/(\d{4}\.\d+\.\d+\w*|\d+\.\d+\.\d+\w*)/Editor", editor)
        unity_ver = m.group(1) if m else None
    tools["unity"] = ToolStatus(
        name="unity", found=bool(editor), path=editor, version=unity_ver, verified=False,
        note="editor binary present; activation/licence and Android modules not verified" if editor
        else "Unity editor not found (set FORGE_UNITY_EDITOR or install via Unity Hub)")
    gpus, gpu_note = _gpus()
    devices = []
    svc = AdbDeviceService()
    if svc.available()[0]:
        for d in svc.list_devices():
            devices.append({"serial": d.serial, "state": d.state, "model": d.model, "emulator": d.is_emulator})
    try:
        disk = shutil.disk_usage(Path(data_dir).resolve() if Path(data_dir).exists() else Path.cwd())
        disk_free = round(disk.free / 1024 ** 3, 2)
    except OSError:
        disk_free = None
    osd = _os()
    not_verified = [
        "free VRAM under real workload (preflight snapshot only)" if gpus else f"GPU: {gpu_note}",
        "Unity licence activation and Android build modules",
        "provider API entitlement, billing owner and quota (credentials are never called during preflight)",
        "promotional credits (eligibility, expiry, covered services)",
        "GPU rental region/quota",
        "a real job succeeding on any remote worker",
    ]
    if not any(d["state"] == "device" and not d["emulator"] for d in devices):
        not_verified.append("physical Android test device (none connected and authorised)")
    for t in tools.values():
        if not t.found:
            not_verified.append(f"{t.name}: not installed")
    for prov, has in (provider_credentials or {}).items():
        if not has:
            not_verified.append(f"{prov}: no credential configured")
    notes = []
    linux_x64 = osd.get("system") == "Linux" and platform.machine() in ("x86_64", "AMD64")
    if not linux_x64:
        notes.append("not the supported Linux x86-64 profile; other OSes need their own validation")
    if osd.get("distribution_id") != "ubuntu":
        notes.append(f"distribution {osd.get('distribution') or 'unknown'} is not the verified Ubuntu LTS profile")
    if not tools["git"].verified:
        notes.append("git missing: workspace isolation unavailable")
    if not tools["dotnet"].verified:
        notes.append("dotnet SDK missing: C# rules checks unavailable")
    if not editor:
        notes.append("Unity missing: Android build checks will be BLOCKED")
    if not tools["adb"].found:
        notes.append("adb missing: device evidence will be INCOMPLETE")
    code_loop_ok = tools["git"].verified and tools["dotnet"].verified
    return ToolchainManifest(
        generated_at=SystemClock().now(), profile=SUPPORTED_PROFILE, os=osd, cpu=_cpu(), ram_gb=_ram_gb(),
        disk_free_gb=disk_free, gpus=gpus, tools=tools, devices=devices, unity_editor_path=editor,
        not_verified=not_verified, compatible=bool(linux_x64 and code_loop_ok and editor and tools["adb"].found),
        compatibility_notes=notes + ([] if not code_loop_ok else ["R1 code/rules loop: available"]),
    )


def write_manifest(m: ToolchainManifest, data_dir: str | Path) -> Path:
    d = Path(data_dir)
    d.mkdir(parents=True, exist_ok=True)
    p = d / "toolchain-manifest.json"
    p.write_text(m.model_dump_json(indent=2))
    return p
