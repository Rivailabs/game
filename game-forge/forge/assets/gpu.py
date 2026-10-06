"""GPU capability records, preflight and the GPU scheduler.

Plan ("Budget reservations recovery and resource scheduling"): scheduling uses a capability record
per worker and a lease per actual GPU. Record GPU model, available VRAM, compute support, host RAM,
disk, driver/runtime versions and successful route benchmarks. Begin with **one generation job per
GPU**; multiple processes on one physical GPU cannot each assume its full memory, and small GPUs are
never pooled into one larger VRAM allocation.

* :func:`parse_nvidia_smi` reads ``nvidia-smi --query-gpu=... --format=csv,noheader,nounits``.
* :func:`capability_preflight` builds a :class:`WorkerRecord` for this machine (runner injectable).
* :class:`GpuScheduler` leases exactly one GPU (``gpu:<worker>:<uuid>``) whose *own* total VRAM meets
  the requirement, re-checking live free VRAM before the job starts.
"""

from __future__ import annotations

import os
import platform
import shutil
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Callable, Optional

from ..checks.base import ProcResult, run_proc_direct
from ..leases import LeaseManager
from .routes import Benchmark, GpuSlot

NVIDIA_SMI_QUERY = "index,uuid,name,memory.total,memory.free,driver_version,compute_cap"
NVIDIA_SMI_ARGV = ["nvidia-smi", f"--query-gpu={NVIDIA_SMI_QUERY}", "--format=csv,noheader,nounits"]


class GpuUnavailable(Exception):
    pass


@dataclass
class GpuInfo:
    index: int
    uuid: str
    name: str
    memory_total_gb: float
    memory_free_gb: float
    driver: str
    compute_capability: str

    def to_dict(self) -> dict:
        return asdict(self)


def parse_nvidia_smi(text: str) -> list[GpuInfo]:
    """Parse the CSV query output (MiB values). Malformed lines are skipped, never guessed."""
    out = []
    for line in text.strip().splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) < 7:
            continue
        try:
            out.append(GpuInfo(int(parts[0]), parts[1], parts[2], round(float(parts[3]) / 1024, 2),
                               round(float(parts[4]) / 1024, 2), parts[5], parts[6]))
        except ValueError:
            continue
    return out


@dataclass
class WorkerRecord:
    worker_id: str
    host: str
    os: str
    gpus: list[GpuInfo] = field(default_factory=list)
    ram_gb: Optional[float] = None
    disk_free_gb: Optional[float] = None
    driver_versions: dict[str, str] = field(default_factory=dict)
    remote_provider: str = ""  # "" = the owner's machine; e.g. "azure" for a rented worker
    verified_at: float = 0.0
    notes: list[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        d = asdict(self)
        return d

    @classmethod
    def from_dict(cls, d: dict) -> "WorkerRecord":
        d = dict(d)
        d["gpus"] = [GpuInfo(**g) for g in d.get("gpus", [])]
        return cls(**d)

    def slots(self) -> list[GpuSlot]:
        return [GpuSlot(self.worker_id, g.uuid, g.name, g.memory_total_gb, g.memory_free_gb,
                        os=self.os.lower(), remote_provider=self.remote_provider) for g in self.gpus]


Runner = Callable[[list[str], Path, float, Optional[dict]], ProcResult]


def _default_run(argv, cwd, timeout, env=None):
    return run_proc_direct(argv, cwd, timeout, env)


def query_gpus(*, run: Runner | None = None, which: Callable[[str], Optional[str]] = shutil.which
               ) -> tuple[list[GpuInfo], str]:
    run = run or _default_run
    if not which("nvidia-smi"):
        return [], "nvidia-smi not found (no NVIDIA GPU/driver detected)"
    r = run(NVIDIA_SMI_ARGV, Path.cwd(), 30, None)
    if r.returncode != 0:
        return [], f"nvidia-smi failed: {(r.stderr or r.stdout).strip()[:200]}"
    gpus = parse_nvidia_smi(r.stdout)
    return gpus, "" if gpus else "nvidia-smi returned no GPUs"


def capability_preflight(worker_id: str, *, run: Runner | None = None, which=shutil.which, now: float = 0.0,
                         disk_path: str | Path = ".", remote_provider: str = "") -> WorkerRecord:
    gpus, note = query_gpus(run=run, which=which)
    ram = None
    try:
        for line in Path("/proc/meminfo").read_text().splitlines():
            if line.startswith("MemTotal:"):
                ram = round(int(line.split()[1]) / 1024 / 1024, 2)
    except OSError:
        pass
    try:
        disk = round(shutil.disk_usage(str(disk_path)).free / 1024 ** 3, 2)
    except OSError:
        disk = None
    drivers = {"nvidia_driver": gpus[0].driver} if gpus else {}
    rec = WorkerRecord(worker_id=worker_id, host=platform.node(), os=platform.system().lower(), gpus=gpus, ram_gb=ram,
                       disk_free_gb=disk, driver_versions=drivers, remote_provider=remote_provider, verified_at=now,
                       notes=[note] if note else [])
    if os.environ.get("CUDA_VISIBLE_DEVICES") is not None:
        rec.notes.append(f"CUDA_VISIBLE_DEVICES={os.environ['CUDA_VISIBLE_DEVICES']} restricts visible GPUs")
    return rec


@dataclass
class GpuLease:
    key: str
    worker_id: str
    gpu: GpuInfo
    holder: str
    job: str

    @property
    def cuda_visible_devices(self) -> str:
        return self.gpu.uuid


class GpuScheduler:
    """One job per physical GPU. A requirement is met by a single GPU's own memory, never a sum."""

    def __init__(self, leases: LeaseManager, workers: Callable[[], list[WorkerRecord]], *,
                 live_query: Callable[[str], list[GpuInfo]] | None = None, ttl_s: float = 3600):
        self.leases = leases
        self.workers = workers
        self.live_query = live_query  # worker_id -> current GPUs (None: trust the recorded snapshot)
        self.ttl_s = ttl_s

    @staticmethod
    def key(worker_id: str, gpu_uuid: str) -> str:
        return f"gpu:{worker_id}:{gpu_uuid}"

    def candidates(self, need_gb: Optional[float], *, gpu_model: Optional[str] = None,
                   worker_id: Optional[str] = None) -> list[tuple[WorkerRecord, GpuInfo]]:
        out = []
        for w in self.workers():
            if worker_id and w.worker_id != worker_id:
                continue
            for g in w.gpus:
                if gpu_model and g.name != gpu_model:
                    continue
                if need_gb is not None and g.memory_total_gb < need_gb:
                    continue  # this GPU alone cannot hold the job; other GPUs never add up
                out.append((w, g))
        # smallest sufficient GPU first, so a large card stays free for jobs that need it
        return sorted(out, key=lambda wg: (wg[1].memory_total_gb, wg[0].worker_id, wg[1].index))

    def acquire(self, job: str, holder: str, need_gb: Optional[float], *, gpu_model: Optional[str] = None,
                worker_id: Optional[str] = None, root_id: Optional[str] = None) -> GpuLease:
        cands = self.candidates(need_gb, gpu_model=gpu_model, worker_id=worker_id)
        if not cands:
            raise GpuUnavailable(f"no single GPU with >= {need_gb} GB" + (f" ({gpu_model})" if gpu_model else ""))
        busy = []
        for w, g in cands:
            k = self.key(w.worker_id, g.uuid)
            if not self.leases.acquire(k, holder, ttl=self.ttl_s, root_id=root_id):
                busy.append(f"{w.worker_id}/{g.name}: leased by another job")
                continue
            if self.live_query is not None and need_gb is not None:
                live = {x.uuid: x for x in self.live_query(w.worker_id)}
                cur = live.get(g.uuid)
                if cur is None or cur.memory_free_gb < need_gb:
                    self.leases.release(k, holder)
                    free = "unknown" if cur is None else f"{cur.memory_free_gb} GB"
                    busy.append(f"{w.worker_id}/{g.name}: only {free} free now (another process uses the GPU)")
                    continue
            return GpuLease(k, w.worker_id, g, holder, job)
        raise GpuUnavailable("; ".join(busy))

    def release(self, lease: GpuLease) -> None:
        self.leases.release(lease.key, lease.holder)


def benchmark_from_run(route: str, stage: str, worker: WorkerRecord, gpu: GpuInfo, *, peak_vram_gb: Optional[float],
                       duration_s: float, success: bool, config: str, at: float) -> Benchmark:
    return Benchmark(route, stage, worker.worker_id, gpu.name, gpu.memory_total_gb, peak_vram_gb, duration_s, success,
                     config, at)
