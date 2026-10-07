"""Local model routes (Hunyuan3D 2.1, TRELLIS, TRELLIS.2, HY-Motion 1.0, FLUX schnell, UniRig, GVHMR).

These run a *pinned* command on a leased GPU, inside the sandbox runner (role ``asset_generation``:
no network, only the job directory writable, the model install mounted read-only). The command is
owner configuration (``[assets.routes.<id>] argv = [...]``) because each repository's entry point and
flags change between releases; Forge ships no guessed default. Placeholders:

``{prompt}`` ``{image}`` ``{model}`` ``{out_dir}`` ``{seed}`` ``{stage}`` ``{target_polycount}`` ``{clip}``
``{duration_s}`` ``{fps}``

The command must write its outputs into ``{out_dir}``; ``outputs`` lists the expected file globs.
A job runs in a background thread so the orchestrator can poll it, enforce the active-work deadline
and cancel it (which kills the sandboxed process group: cancellation is "confirmed").
"""

from __future__ import annotations

import threading
from pathlib import Path
from typing import Optional

from ...sandbox.policy import Mount, WorkerRole
from ...sandbox.runner import SandboxResult, SandboxRunner
from ..routes import ROUTES
from .base import AdapterError, AdapterUnavailable, GenerationAdapter, GenerationJob, JobStatus

PLACEHOLDERS = ("prompt", "image", "model", "out_dir", "seed", "stage", "target_polycount", "clip", "duration_s", "fps")


class _LocalRun:
    def __init__(self) -> None:
        self.thread: Optional[threading.Thread] = None
        self.cancel = threading.Event()
        self.result: Optional[SandboxResult] = None
        self.error: str = ""
        self.out_dir: Path = Path(".")


class LocalModelAdapter(GenerationAdapter):
    def __init__(self, route_id: str, *, argv: list[str] | None, install_dir: str | None = None,
                 outputs: list[str] | None = None, runner: SandboxRunner | None = None, timeout_s: float = 7200,
                 model_version: str = "", env: dict[str, str] | None = None):
        if route_id not in ROUTES or ROUTES[route_id].kind != "local_model":
            raise ValueError(f"{route_id} is not a local model route")
        self.route = ROUTES[route_id]
        self.argv = list(argv) if argv else None
        self.install_dir = Path(install_dir).expanduser() if install_dir else None
        self.outputs = list(outputs or ["*.glb", "*.fbx", "*.obj", "*.png", "*.npz", "*.bvh", "*.json"])
        self.runner = runner or SandboxRunner()
        self.timeout_s = timeout_s
        self.model = route_id
        self.model_version = model_version
        self.env = dict(env or {})
        self._runs: dict[str, _LocalRun] = {}

    def available(self) -> tuple[bool, str]:
        if not self.argv:
            return False, (f"{self.route.id}: no pinned command configured ([assets.routes.{self.route.id}] argv); "
                           "Forge ships no guessed entry point")
        if self.install_dir is not None and not self.install_dir.exists():
            return False, f"{self.route.id}: install dir {self.install_dir} does not exist"
        return True, ""

    def estimate_ceiling_micros(self, job: GenerationJob) -> Optional[int]:
        return 0  # owner GPU time is not a provider charge; GPU rental is recorded in the cash ledger

    def _render_argv(self, job: GenerationJob) -> list[str]:
        values = {"out_dir": str(job.out_dir), "stage": job.stage}
        for k in PLACEHOLDERS:
            if k in job.inputs:
                values[k] = str(job.inputs[k])
            elif k in job.params:
                values[k] = str(job.params[k])
        out = []
        for a in self.argv or []:
            try:
                out.append(a.format(**values))
            except KeyError as e:
                raise AdapterError(f"{self.route.id}: command needs {e} which this job does not provide") from None
        return out

    def submit(self, job: GenerationJob) -> str:
        ok, why = self.available()
        if not ok:
            raise AdapterUnavailable(why)
        argv = self._render_argv(job)
        job.out_dir.mkdir(parents=True, exist_ok=True)
        run = _LocalRun()
        run.out_dir = job.out_dir
        mounts = [Mount(self.install_dir, str(self.install_dir), True)] if self.install_dir else []
        for k in ("image", "model", "video"):
            if k in job.inputs and Path(job.inputs[k]).exists():
                src = Path(job.inputs[k]).resolve()
                if not str(src).startswith(str(job.out_dir.resolve())):
                    mounts.append(Mount(src, str(src), True))
        env = dict(self.env)
        if job.gpu_uuid:
            env["CUDA_VISIBLE_DEVICES"] = job.gpu_uuid  # exactly the leased GPU

        def target() -> None:
            try:
                run.result = self.runner.run(argv, workdir=job.out_dir, role=WorkerRole.ASSET_GENERATION,
                                             timeout_s=self.timeout_s, env_extra=env, extra_mounts=mounts,
                                             gpu=job.gpu_uuid, cancel=run.cancel)
            except Exception as e:  # SandboxRefused etc.
                run.error = f"{type(e).__name__}: {e}"

        jid = f"local-{job.idempotency_key}"
        run.thread = threading.Thread(target=target, daemon=True, name=jid)
        self._runs[jid] = run
        run.thread.start()
        return jid

    def poll(self, provider_job_id: str) -> JobStatus:
        run = self._runs.get(provider_job_id)
        if run is None:
            return JobStatus("failed", provider_job_id, error="local job not running in this process (restart?)")
        if run.thread and run.thread.is_alive():
            return JobStatus("running", provider_job_id)
        if run.error:
            return JobStatus("failed", provider_job_id, error=run.error)
        r = run.result
        if r is None:
            return JobStatus("failed", provider_job_id, error="no result")
        info = {"containment": r.evidence(), "exit_code": r.proc.returncode, "duration_s": round(r.proc.duration_s, 1)}
        if run.cancel.is_set():
            return JobStatus("cancelled", provider_job_id, error="cancelled", raw=info)
        if r.proc.timed_out:
            return JobStatus("failed", provider_job_id, error=f"timed out after {self.timeout_s}s", raw=info)
        if r.proc.returncode != 0:
            tail = (r.proc.stderr or r.proc.stdout).strip().splitlines()[-5:]
            return JobStatus("failed", provider_job_id, error=f"exit {r.proc.returncode}: {' | '.join(tail)[:400]}",
                             raw=info)
        outs = {}
        for pat in self.outputs:
            for p in sorted(run.out_dir.glob(pat)):
                outs[p.name] = str(p)
        if not outs:
            return JobStatus("failed", provider_job_id, error="command succeeded but wrote no expected outputs",
                             raw=info)
        return JobStatus("succeeded", provider_job_id, 1.0, "", outs, cost_micros=0, raw=info)

    def fetch(self, status: JobStatus, out_dir: Path) -> dict[str, Path]:
        return {name: Path(p) for name, p in status.outputs.items()}

    def cancel(self, provider_job_id: str) -> str:
        run = self._runs.get(provider_job_id)
        if run is None:
            return "uncertain"
        run.cancel.set()  # kills the sandboxed process group
        if run.thread:
            run.thread.join(timeout=30)
        return "confirmed" if not (run.thread and run.thread.is_alive()) else "uncertain"
