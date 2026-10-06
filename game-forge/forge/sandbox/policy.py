"""Containment policy: worker roles, network policy, mounts and the owner-owned sandbox config.

Plan ("Workspace isolation Unity integration and the cloud link", "Secrets egress and signed
releases"): generated-code execution runs in a container/VM or equivalent enforced environment
with only the required project mounts and approved network access. No privileged Docker
socket, normal home directory, unrelated repositories or signing keystore is mounted. Network
policy is chosen per worker role and the enforcement configuration is owned *outside* the
worker's writable scope - a policy file inside the writable repository is not accepted.

This module is pure data + validation; ``backends.py`` turns a validated request into an argv
for podman / docker (rootless) / bubblewrap and ``runner.py`` executes it.
"""

from __future__ import annotations

import os
import stat
import tomllib
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
from typing import Iterable, Optional


class SandboxError(Exception):
    """Base class for containment errors."""


class SandboxRefused(SandboxError):
    """The request would break the containment policy; nothing was executed."""


class SandboxConfigError(SandboxError):
    """The sandbox configuration file is unusable (location, permissions or content)."""


class WorkerRole(str, Enum):
    """What a sandboxed process is for. Each role has its own network policy."""

    GENERATED_CODE_CHECK = "generated_code_check"  # build/test of candidate code: no network
    DEPENDENCY_RESTORE = "dependency_restore"  # package restore: allow-listed registries only
    ASSET_GENERATION = "asset_generation"  # local model inference on a leased GPU: no network
    ASSET_NORMALIZATION = "asset_normalization"  # Blender batch scripts: no network
    INTEGRATION_BUILD = "integration_build"  # Unity batch build: no network by default


class NetworkMode(str, Enum):
    DENY = "deny"
    ALLOWLIST = "allowlist"


#: Registries the dependency-restore role may reach by default (NuGet and PyPI). The owner may
#: replace the list in the sandbox config; generated code cannot.
DEFAULT_RESTORE_HOSTS = (
    "api.nuget.org",
    "www.nuget.org",
    "globalcdn.nuget.org",
    "pypi.org",
    "files.pythonhosted.org",
)


@dataclass(frozen=True)
class NetworkPolicy:
    mode: NetworkMode = NetworkMode.DENY
    hosts: tuple[str, ...] = ()

    def describe(self) -> str:
        if self.mode == NetworkMode.DENY:
            return "denied"
        return "allow-list: " + ", ".join(self.hosts)


#: Deny by default; only dependency restore gets an allow-list.
DEFAULT_ROLE_NETWORK: dict[WorkerRole, NetworkPolicy] = {
    WorkerRole.GENERATED_CODE_CHECK: NetworkPolicy(),
    WorkerRole.DEPENDENCY_RESTORE: NetworkPolicy(NetworkMode.ALLOWLIST, DEFAULT_RESTORE_HOSTS),
    WorkerRole.ASSET_GENERATION: NetworkPolicy(),
    WorkerRole.ASSET_NORMALIZATION: NetworkPolicy(),
    WorkerRole.INTEGRATION_BUILD: NetworkPolicy(),
}


@dataclass(frozen=True)
class Mount:
    source: Path
    target: str  # absolute path inside the sandbox (Forge mounts the worktree at its own path)
    read_only: bool = True

    def to_dict(self) -> dict:
        return {"source": str(self.source), "target": self.target, "read_only": self.read_only}


#: File names that look like signing material; a mount that contains one is refused.
SIGNING_KEY_PATTERNS = ("*.jks", "*.keystore", "*.p12", "*.pfx", "*.pepk", "*.snk", "upload-key*", "*.gpg")


@dataclass
class SandboxConfig:
    """Owner-owned enforcement configuration (``~/.config/game-forge/sandbox.toml`` by default).

    ``source`` records where it came from: ``"defaults"`` (compiled-in, also outside the worker's
    reach) or the validated file path.
    """

    backend: str = "auto"  # auto | podman | docker | bwrap | none
    image: Optional[str] = None  # pinned worker image (digest) for container backends
    memory_mb: int = 8192
    cpus: float = 4.0
    pids_limit: int = 1024
    tmpfs_mb: int = 2048
    #: Allow-list enforcement for container backends: an owner-run filtering proxy reachable only
    #: through an *internal* container network. Without both, allow-list roles get no network.
    egress_proxy: Optional[str] = None
    restore_network: Optional[str] = None
    #: Extra read-only mounts (toolchains such as a dotnet SDK outside /usr for bubblewrap).
    extra_ro_mounts: list[str] = field(default_factory=list)
    #: Paths that must never be mounted in addition to the built-in list (keystores etc.).
    forbidden_paths: list[str] = field(default_factory=list)
    #: True: no backend -> refuse to run (check BLOCKED). False: run supervised and say so.
    require_containment: bool = False
    role_network: dict[WorkerRole, NetworkPolicy] = field(default_factory=lambda: dict(DEFAULT_ROLE_NETWORK))
    source: str = "defaults"

    def network_for(self, role: WorkerRole) -> NetworkPolicy:
        return self.role_network.get(role, NetworkPolicy())


def _is_within(child: Path, parent: Path) -> bool:
    return child == parent or parent in child.parents


def validate_config_location(path: Path, writable_roots: Iterable[Path]) -> None:
    """The enforcement config must be outside every worker-writable root and not writable by others."""
    real = path.resolve()
    for root in writable_roots:
        r = Path(root).resolve()
        if _is_within(real, r):
            raise SandboxConfigError(
                f"sandbox config {real} is inside worker-writable scope {r}; it must be owned outside it")
    st = real.stat()
    if st.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
        raise SandboxConfigError(f"sandbox config {real} is group/world writable (chmod 600 or 644)")
    if hasattr(os, "getuid") and st.st_uid not in (os.getuid(), 0):
        raise SandboxConfigError(f"sandbox config {real} is not owned by the Forge owner account or root")


def load_sandbox_config(path: str | os.PathLike | None, *, writable_roots: Iterable[Path] = ()) -> SandboxConfig:
    """Load and validate the owner's sandbox config. A missing file gives the compiled-in defaults."""
    if path is None:
        return SandboxConfig()
    p = Path(path).expanduser()
    if not p.exists():
        return SandboxConfig()
    validate_config_location(p, list(writable_roots))
    try:
        raw = tomllib.loads(p.read_text())
    except tomllib.TOMLDecodeError as e:
        raise SandboxConfigError(f"{p}: {e}") from e
    s = raw.get("sandbox") or {}
    known = {"backend", "image", "memory_mb", "cpus", "pids_limit", "tmpfs_mb", "egress_proxy", "restore_network",
             "extra_ro_mounts", "forbidden_paths", "require_containment", "roles"}
    unknown = set(s) - known
    if unknown:
        raise SandboxConfigError(f"{p}: unknown [sandbox] keys {sorted(unknown)}")
    if s.get("backend", "auto") not in ("auto", "podman", "docker", "bwrap", "none"):
        raise SandboxConfigError(f"{p}: backend must be auto, podman, docker, bwrap or none")
    roles = dict(DEFAULT_ROLE_NETWORK)
    for name, spec in (s.get("roles") or {}).items():
        try:
            role = WorkerRole(name)
        except ValueError as e:
            raise SandboxConfigError(f"{p}: unknown worker role {name!r}") from e
        mode = NetworkMode(spec.get("network", "deny"))
        hosts = tuple(spec.get("hosts", DEFAULT_RESTORE_HOSTS if mode == NetworkMode.ALLOWLIST else ()))
        if mode == NetworkMode.ALLOWLIST and not hosts:
            raise SandboxConfigError(f"{p}: role {name} uses an allow-list with no hosts")
        roles[role] = NetworkPolicy(mode, hosts)
    return SandboxConfig(
        backend=s.get("backend", "auto"), image=s.get("image"), memory_mb=int(s.get("memory_mb", 8192)),
        cpus=float(s.get("cpus", 4.0)), pids_limit=int(s.get("pids_limit", 1024)),
        tmpfs_mb=int(s.get("tmpfs_mb", 2048)), egress_proxy=s.get("egress_proxy"),
        restore_network=s.get("restore_network"), extra_ro_mounts=list(s.get("extra_ro_mounts", [])),
        forbidden_paths=list(s.get("forbidden_paths", [])), require_containment=bool(s.get("require_containment")),
        role_network=roles, source=str(p.resolve()))


def default_config_path() -> Path:
    env = os.environ.get("FORGE_SANDBOX_CONFIG")
    return Path(env).expanduser() if env else Path("~/.config/game-forge/sandbox.toml").expanduser()


def builtin_forbidden_paths(home: Path | None = None) -> list[Path]:
    """Paths that are never mounted into generated-code execution (or exposed via an ancestor mount)."""
    home = (home or Path.home()).resolve()
    out = [
        Path("/"), home,
        home / ".ssh", home / ".gnupg", home / ".android", home / ".config" / "game-forge",
        home / ".docker", home / ".aws", home / ".kube", home / ".nuget" / "NuGet",
        Path("/var/run/docker.sock"), Path("/run/docker.sock"), Path("/run/podman"), Path("/var/run"),
        Path("/run"), Path("/etc/shadow"), Path("/root"),
    ]
    xdg = os.environ.get("XDG_RUNTIME_DIR")
    if xdg:
        out += [Path(xdg), Path(xdg) / "docker.sock", Path(xdg) / "podman"]
    return out


def validate_mounts(mounts: list[Mount], *, forbidden: list[Path], home: Path | None = None,
                    scan_limit: int = 200_000) -> None:
    """Refuse mounts that expose forbidden paths or contain signing keys. Raises :class:`SandboxRefused`.

    ``home`` is the owner's home directory: mounting it (or an ancestor) is refused, a project
    directory below it is allowed, and hidden directories below it (``~/.ssh`` ...) are refused.
    """
    home = (home or Path.home()).resolve()
    for m in mounts:
        src = Path(m.source).resolve()
        for f in forbidden:
            f = Path(f).resolve() if Path(f).exists() else Path(f)
            if src == f:
                raise SandboxRefused(f"mount {src} is a forbidden path")
            if src in f.parents:
                raise SandboxRefused(f"mount {src} would expose forbidden path {f}")
            if f in src.parents and f not in (Path("/"),) and not _home_project_ok(src, f, home):
                raise SandboxRefused(f"mount {src} is inside forbidden path {f}")
        if src.is_dir():
            key = find_signing_material(src, limit=scan_limit)
            if key:
                raise SandboxRefused(f"mount {src} contains signing material {key}; keystores are never mounted")
        elif src.exists() and _matches_key(src.name):
            raise SandboxRefused(f"mount {src} is signing material")


def _home_project_ok(src: Path, forbidden: Path, home: Path) -> bool:
    """A project directory *under* the home directory is allowed; the home directory itself is not.

    Everything else inside a forbidden path (``~/.ssh/x``, ``/run/...``) stays refused.
    """
    if forbidden != home:
        return False
    rel = src.relative_to(home)
    return not rel.parts[0].startswith(".")  # ~/game is fine, ~/.ssh/... is not


def _matches_key(name: str) -> bool:
    from fnmatch import fnmatch

    return any(fnmatch(name.lower(), pat) for pat in SIGNING_KEY_PATTERNS)


def find_signing_material(root: Path, *, limit: int = 200_000) -> Optional[str]:
    """First file under ``root`` that looks like a signing keystore (bounded walk, skips .git)."""
    seen = 0
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in (".git", "node_modules", "Library", ".build")]
        for fn in filenames:
            seen += 1
            if _matches_key(fn):
                return str(Path(dirpath) / fn)
            if seen >= limit:
                return None
    return None
