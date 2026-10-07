"""Isolation backends: rootless podman, rootless docker and bubblewrap.

Detection is honest: a backend counts only when its probe succeeds on this machine.

* **podman** must report ``host.security.rootless = true``.
* **docker** must be a *rootless* daemon (``SecurityOptions`` contains ``name=rootless``). A rootful
  daemon is refused: a container escape there is root on the host, and talking to it needs the
  privileged socket the plan forbids handing to workers.
* **bubblewrap** must be able to create user/network namespaces (a probe run of ``true``).

Each backend turns a :class:`SandboxRequest` into an argv. Network deny is enforced by the
kernel (``--network none`` / ``--unshare-net``). An allow-list needs an owner-run filtering proxy
on an *internal* container network; bubblewrap cannot enforce host allow-lists, so allow-list
roles never run under bubblewrap.
"""

from __future__ import annotations

import json
import shutil
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional

from ..checks.base import ProcResult, run_proc_direct
from .policy import Mount, NetworkMode, NetworkPolicy, SandboxConfig

Runner = Callable[[list[str], Path, float, Optional[dict]], ProcResult]


def _default_runner(argv: list[str], cwd: Path, timeout: float, env: Optional[dict] = None) -> ProcResult:
    return run_proc_direct(argv, cwd, timeout, env)


@dataclass
class BackendInfo:
    name: str  # podman | docker | bwrap
    path: Optional[str]
    available: bool
    reason: str = ""
    rootless: bool = False
    version: str = ""
    #: True when the backend can enforce a host allow-list (container + internal network + proxy).
    allowlist_capable: bool = False

    def to_dict(self) -> dict:
        return dict(self.__dict__)


@dataclass
class SandboxRequest:
    argv: list[str]
    cwd: Path
    mounts: list[Mount]
    network: NetworkPolicy
    env: dict[str, str] = field(default_factory=dict)
    name: str = "forge-sbx"
    gpu: Optional[str] = None  # GPU UUID (containers) or index (bubblewrap)


class Backend:
    name = "backend"

    def __init__(self, info: BackendInfo, config: SandboxConfig):
        self.info, self.config = info, config

    def can_enforce(self, net: NetworkPolicy) -> tuple[bool, str]:
        return True, ""

    def argv(self, req: SandboxRequest) -> list[str]:  # pragma: no cover - interface
        raise NotImplementedError

    def kill_argv(self, name: str) -> Optional[list[str]]:
        return None

    def network_description(self, net: NetworkPolicy) -> str:
        return net.describe()


class ContainerBackend(Backend):
    """podman / docker in rootless mode. The worktree is mounted at its own path."""

    def can_enforce(self, net: NetworkPolicy) -> tuple[bool, str]:
        if not self.config.image:
            return False, "no pinned worker image configured ([sandbox] image)"
        if net.mode == NetworkMode.ALLOWLIST:
            if not (self.config.egress_proxy and self.config.restore_network):
                return False, ("allow-list needs [sandbox] egress_proxy and restore_network "
                               "(an internal network that reaches only the filtering proxy)")
        return True, ""

    def argv(self, req: SandboxRequest) -> list[str]:
        c = self.config
        net = "none" if req.network.mode == NetworkMode.DENY else str(c.restore_network)
        out = [self.info.path or self.name, "run", "--rm", "--init", "--name", req.name, "--network", net,
               "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
               "--pids-limit", str(c.pids_limit), "--memory", f"{c.memory_mb}m", "--cpus", f"{c.cpus:g}",
               "--tmpfs", f"/tmp:rw,size={c.tmpfs_mb}m", "--workdir", str(req.cwd),
               "--label", "forge.sandbox=1"]
        out += self._user_args()
        for m in req.mounts:
            out += ["--volume", f"{m.source}:{m.target}:{'ro' if m.read_only else 'rw'}"]
        env = {"HOME": "/tmp/home", **req.env}
        if req.network.mode == NetworkMode.ALLOWLIST:
            env.update({"HTTP_PROXY": str(c.egress_proxy), "HTTPS_PROXY": str(c.egress_proxy),
                        "http_proxy": str(c.egress_proxy), "https_proxy": str(c.egress_proxy),
                        "FORGE_EGRESS_ALLOWLIST": ",".join(req.network.hosts)})
        for k in sorted(env):
            out += ["--env", f"{k}={env[k]}"]
        if req.gpu:
            out += self._gpu_args(req.gpu)
        out += [str(c.image), *req.argv]
        return out

    def _user_args(self) -> list[str]:
        return []

    def _gpu_args(self, gpu: str) -> list[str]:
        return []

    def kill_argv(self, name: str) -> Optional[list[str]]:
        return [self.info.path or self.name, "rm", "--force", name]

    def network_description(self, net: NetworkPolicy) -> str:
        if net.mode == NetworkMode.DENY:
            return "denied (--network none)"
        return (f"allow-list {', '.join(net.hosts)} via proxy {self.config.egress_proxy} on internal network "
                f"{self.config.restore_network}")


class PodmanBackend(ContainerBackend):
    name = "podman"

    def _user_args(self) -> list[str]:
        return ["--userns", "keep-id"]

    def _gpu_args(self, gpu: str) -> list[str]:
        return ["--device", f"nvidia.com/gpu={gpu}"]  # CDI device name for exactly one GPU


class DockerBackend(ContainerBackend):
    name = "docker"

    def _user_args(self) -> list[str]:
        # Rootless docker maps container root to the unprivileged host user, so files written to the
        # worktree stay owned by that user; passing --user would map to a subordinate uid instead.
        return []

    def _gpu_args(self, gpu: str) -> list[str]:
        return ["--gpus", f"device={gpu}"]


#: System directories bubblewrap exposes read-only so ordinary toolchains (python, dotnet in /usr) run.
BWRAP_RO_SYSTEM = ("/usr", "/bin", "/sbin", "/lib", "/lib64", "/lib32", "/opt",
                   "/etc/alternatives", "/etc/ssl", "/etc/ca-certificates", "/etc/ld.so.cache",
                   "/etc/ld.so.conf", "/etc/ld.so.conf.d", "/etc/localtime", "/etc/fonts")


class BwrapBackend(Backend):
    name = "bwrap"

    def can_enforce(self, net: NetworkPolicy) -> tuple[bool, str]:
        if net.mode == NetworkMode.ALLOWLIST:
            return False, "bubblewrap cannot enforce a host allow-list (network is all-or-nothing)"
        return True, ""

    def argv(self, req: SandboxRequest) -> list[str]:
        out = [self.info.path or "bwrap", "--die-with-parent", "--new-session", "--unshare-all", "--clearenv",
               "--cap-drop", "ALL"]
        for d in BWRAP_RO_SYSTEM:
            out += ["--ro-bind-try", d, d]
        out += ["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--dir", "/tmp/home"]
        for extra in self.config.extra_ro_mounts:
            out += ["--ro-bind-try", extra, extra]
        for m in req.mounts:
            out += ["--ro-bind" if m.read_only else "--bind", str(m.source), m.target]
        if req.gpu is not None:
            for dev in ("/dev/nvidiactl", "/dev/nvidia-uvm", "/dev/nvidia-uvm-tools", f"/dev/nvidia{req.gpu}"):
                out += ["--dev-bind-try", dev, dev]
        env = {"HOME": "/tmp/home", "PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
               "LANG": "C.UTF-8", **req.env}
        for k in sorted(env):
            out += ["--setenv", k, env[k]]
        out += ["--chdir", str(req.cwd), "--", *req.argv]
        return out

    def network_description(self, net: NetworkPolicy) -> str:
        return "denied (--unshare-net)" if net.mode == NetworkMode.DENY else "unsupported"


BACKEND_CLASSES = {"podman": PodmanBackend, "docker": DockerBackend, "bwrap": BwrapBackend}
DETECTION_ORDER = ("podman", "docker", "bwrap")


def _probe_podman(path: str, run: Runner) -> BackendInfo:
    r = run([path, "info", "--format", "json"], Path.cwd(), 30, None)
    if r.returncode != 0:
        return BackendInfo("podman", path, False, f"podman info failed: {(r.stderr or r.stdout).strip()[:200]}")
    try:
        data = json.loads(r.stdout)
    except json.JSONDecodeError:
        return BackendInfo("podman", path, False, "podman info returned unreadable output")
    host = data.get("host") or {}
    rootless = bool((host.get("security") or {}).get("rootless"))
    ver = str((data.get("version") or {}).get("Version", ""))
    if not rootless:
        return BackendInfo("podman", path, False, "podman is running rootful; only rootless podman is accepted",
                           rootless=False, version=ver)
    return BackendInfo("podman", path, True, "", rootless=True, version=ver, allowlist_capable=True)


def _probe_docker(path: str, run: Runner) -> BackendInfo:
    r = run([path, "info", "--format", "{{json .SecurityOptions}}|{{.ServerVersion}}"], Path.cwd(), 30, None)
    if r.returncode != 0:
        return BackendInfo("docker", path, False,
                           f"docker daemon not reachable: {(r.stderr or r.stdout).strip()[:200]}")
    opts, _, ver = r.stdout.strip().partition("|")
    try:
        sec = json.loads(opts or "[]") or []
    except json.JSONDecodeError:
        sec = []
    rootless = any("rootless" in str(o) for o in sec)
    if not rootless:
        return BackendInfo("docker", path, False,
                           "docker daemon is rootful; only a rootless daemon is accepted as containment",
                           version=ver)
    return BackendInfo("docker", path, True, "", rootless=True, version=ver, allowlist_capable=True)


def _probe_bwrap(path: str, run: Runner) -> BackendInfo:
    v = run([path, "--version"], Path.cwd(), 15, None)
    ver = (v.stdout or "").strip().split()[-1] if v.returncode == 0 and v.stdout.strip() else ""
    r = run([path, "--unshare-all", "--die-with-parent", "--ro-bind", "/usr", "/usr", "--ro-bind-try", "/lib", "/lib",
             "--ro-bind-try", "/lib64", "/lib64", "--ro-bind-try", "/bin", "/bin", "--proc", "/proc", "--dev", "/dev",
             "--", "/usr/bin/env", "true"], Path.cwd(), 15, None)
    if r.returncode != 0:
        return BackendInfo("bwrap", path, False,
                           f"bubblewrap cannot create namespaces here: {(r.stderr or r.stdout).strip()[:200]}",
                           version=ver)
    return BackendInfo("bwrap", path, True, "", rootless=True, version=ver)


PROBES = {"podman": _probe_podman, "docker": _probe_docker, "bwrap": _probe_bwrap}


def detect_backends(*, which: Callable[[str], Optional[str]] = shutil.which, run: Runner | None = None
                    ) -> list[BackendInfo]:
    """Probe every supported backend. Missing binaries are reported, not hidden."""
    run = run or _default_runner
    out = []
    for name in DETECTION_ORDER:
        path = which(name)
        if not path:
            out.append(BackendInfo(name, None, False, f"{name} is not installed"))
            continue
        out.append(PROBES[name](path, run))
    return out


def make_backend(info: BackendInfo, config: SandboxConfig) -> Backend:
    return BACKEND_CLASSES[info.name](info, config)
