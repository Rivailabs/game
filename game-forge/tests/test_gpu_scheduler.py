"""GPU capability preflight (nvidia-smi parsing), one-job-per-GPU leases, benchmarks."""

from __future__ import annotations

import pytest

from forge.assets.gpu import (
    GpuScheduler,
    GpuUnavailable,
    WorkerRecord,
    benchmark_from_run,
    capability_preflight,
    parse_nvidia_smi,
)
from forge.assets.registry import AssetRegistry
from forge.checks.base import ProcResult
from forge.leases import LeaseManager
from forge.store import Store
from forge.util import FakeClock

from .asset_fakes import NVIDIA_SMI_4090, NVIDIA_SMI_TWO_GPUS, fake_smi


def test_parse_nvidia_smi():
    gpus = parse_nvidia_smi(NVIDIA_SMI_TWO_GPUS + "garbage line\n2, x, y, notanumber, 1, d, 8\n")
    assert [g.uuid for g in gpus] == ["GPU-aaaa-1111", "GPU-bbbb-2222"]
    g = gpus[0]
    assert g.name == "NVIDIA GeForce RTX 4070" and g.memory_total_gb == 11.99 and g.compute_capability == "8.9"
    assert g.driver == "550.54.14"


def test_capability_preflight_with_and_without_driver(tmp_path):
    rec = capability_preflight("w1", run=fake_smi(NVIDIA_SMI_4090), which=lambda n: "/usr/bin/" + n, now=5.0,
                               disk_path=tmp_path)
    assert rec.gpus[0].memory_total_gb == 23.99 and rec.driver_versions["nvidia_driver"] == "550.54.14"
    assert rec.disk_free_gb is not None and rec.verified_at == 5.0
    rec = capability_preflight("w2", which=lambda n: None, disk_path=tmp_path)
    assert rec.gpus == [] and "nvidia-smi not found" in rec.notes[0]
    failing = capability_preflight("w3", run=lambda *a: ProcResult(9, "", "NVIDIA-SMI has failed", 0),
                                   which=lambda n: "/x", disk_path=tmp_path)
    assert failing.gpus == [] and "failed" in failing.notes[0]


@pytest.fixture
def sched(tmp_path):
    store = Store(tmp_path / "forge.db", clock=FakeClock())
    reg = AssetRegistry(store)
    two = capability_preflight("laptop", run=fake_smi(NVIDIA_SMI_TWO_GPUS), which=lambda n: n, disk_path=tmp_path)
    big = capability_preflight("rented", run=fake_smi(NVIDIA_SMI_4090), which=lambda n: n, disk_path=tmp_path,
                               remote_provider="azure")
    reg.put_worker(two)
    reg.put_worker(big)
    return GpuScheduler(LeaseManager(store), reg.workers), reg, store


def test_scheduler_never_pools_vram_and_prefers_smallest_sufficient_gpu(sched):
    s, reg, _ = sched
    # 2 x 12 GB on the laptop do not make one 16 GB job: only the rented 24 GB card qualifies
    lease = s.acquire("trellis-job", "w", 16)
    assert lease.worker_id == "rented" and lease.gpu.memory_total_gb >= 16
    assert lease.cuda_visible_devices == "GPU-cccc-3333"
    # one job per GPU: a second 16 GB job must wait
    with pytest.raises(GpuUnavailable, match="leased by another job"):
        s.acquire("second", "w2", 16)
    s.release(lease)
    assert s.acquire("second", "w2", 16).worker_id == "rented"
    small = s.acquire("flux", "w3", 8)
    assert small.worker_id == "laptop"  # smallest sufficient card, the big one stays free
    other = s.acquire("flux2", "w4", 8)
    assert other.worker_id == "laptop" and other.gpu.uuid != small.gpu.uuid
    with pytest.raises(GpuUnavailable, match="no single GPU"):
        s.acquire("huge", "w5", 29)


def test_scheduler_rechecks_live_free_vram(sched):
    s, reg, store = sched
    busy = parse_nvidia_smi("0, GPU-cccc-3333, NVIDIA GeForce RTX 4090, 24564, 2048, 550.54.14, 8.9\n")
    s.live_query = lambda worker: busy if worker == "rented" else []
    with pytest.raises(GpuUnavailable, match="another process uses the GPU"):
        s.acquire("j", "w", 16)
    assert LeaseManager(store).holder("gpu:rented:GPU-cccc-3333") is None  # lease given back


def test_benchmarks_and_worker_records_persist(sched):
    s, reg, _ = sched
    w = next(x for x in reg.workers() if x.worker_id == "rented")
    assert isinstance(w, WorkerRecord) and w.remote_provider == "azure"
    assert w.slots()[0].remote_provider == "azure"
    b = benchmark_from_run("trellis2", "default", w, w.gpus[0], peak_vram_gb=21.5, duration_s=310, success=True,
                           config="trellis2@abc123 fp16", at=10.0)
    reg.record_benchmark(b)
    got = reg.benchmarks("trellis2")
    assert got[0].peak_vram_gb == 21.5 and got[0].gpu_model == "NVIDIA GeForce RTX 4090"
