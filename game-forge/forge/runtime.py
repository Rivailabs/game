"""Per-project runtime wiring: paths, checks, provider routes, worker identity."""

from __future__ import annotations

import os
import socket
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

from .checks import Check
from .models import Project, ToolchainManifest, WorkerCapability
from .providers.base import ProviderAdapter


def default_worker_id() -> str:
    return f"{socket.gethostname()}|{os.getpid()}"


@dataclass
class ProjectRuntime:
    project: Project
    data_dir: Path
    checks: dict[str, Check] = field(default_factory=dict)
    providers: dict[str, ProviderAdapter] = field(default_factory=dict)
    reviewer_route: Optional[str] = None
    manifest: Optional[ToolchainManifest] = None
    capability: Optional[WorkerCapability] = None
    worker_id: str = field(default_factory=default_worker_id)
    transport_retries: int = 2
    backoff_s: float = 1.0
    lease_ttl_s: float = 900.0
    max_restage: int = 3
    unattended: bool = True
    #: Catalogue lane configuration (``forge.lanes.run_catalogue.LaneConfig``); None = asset tasks BLOCKED.
    catalogue: Optional[Any] = None
    #: Generation lane for asset tasks, run only when the catalogue returns NONE (exit 2).
    #: Signature ``(root, target_dir, brief) -> LaneResult``. None: no generation lane exists yet.
    generation_lane: Optional[Callable[..., Any]] = None

    @property
    def db_path(self) -> Path:
        return self.data_dir / "forge.db"

    @property
    def artifacts_dir(self) -> Path:
        return self.data_dir / "artifacts"

    @property
    def worktrees_dir(self) -> Path:
        return self.data_dir / "worktrees" / self.project.id

    @property
    def staging_dir(self) -> Path:
        return self.data_dir / "staging" / self.project.id

    @property
    def repo_path(self) -> Path:
        return Path(self.project.repo_path).resolve()
