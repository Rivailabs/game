"""Project configuration (``project.toml``) loading and runtime construction.

The project file is owner-controlled and lives outside every worker's writable
scope (it is itself listed as a protected path), so generated code cannot change
providers, checks, thresholds or budgets by writing a configuration file.
"""

from __future__ import annotations

import json
import tomllib
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Optional

from .budget import BudgetLedger, milestone_scope, project_scope
from .checks import build_registry
from .models import Milestone, Project, ProjectPolicy, ToolchainManifest, WorkerCapability
from .providers.base import ProviderAdapter
from .providers.fake import FakeProvider
from .providers.pricing import PriceTable
from .runtime import ProjectRuntime
from .store import Store
from .util import usd_to_micros


class ConfigError(Exception):
    pass


@dataclass
class ProjectConfig:
    path: Path
    project: Project
    milestones: list[Milestone]
    project_cap_micros: Optional[int]
    default_root_ceiling_micros: Optional[int]
    check_specs: dict[str, dict[str, Any]]
    provider_specs: dict[str, dict[str, Any]]
    reviewer_route: Optional[str]
    pricing: dict[str, Any]
    data_dir: Path
    runtime: dict[str, Any] = field(default_factory=dict)
    catalogue: dict[str, Any] = field(default_factory=dict)
    assets: dict[str, Any] = field(default_factory=dict)  # R2 generation lane ([assets])


def load_project_config(path: str | Path) -> ProjectConfig:
    path = Path(path).resolve()
    with open(path, "rb") as fh:
        raw = tomllib.load(fh)
    base = path.parent
    p = raw.get("project") or {}
    if "id" not in p:
        raise ConfigError(f"{path}: [project] id is required")
    repo = (base / p.get("repo_path", ".")).resolve()
    project = Project(
        id=p["id"], name=p.get("name", p["id"]), repo_path=str(repo), workdir=p.get("workdir", "."),
        accepted_branch=p.get("accepted_branch", "forge/accepted"), spec_version=str(p.get("spec_version", "1")),
        policy=ProjectPolicy(**(raw.get("policy") or {})), protected_paths=list(p.get("protected_paths", [])),
        toolchain_profile=p.get("toolchain_profile", "linux-x86_64-ubuntu-lts"),
    )
    b = raw.get("budget") or {}
    milestones = []
    for mid, m in (raw.get("milestones") or {}).items():
        milestones.append(Milestone(
            id=mid, project_id=project.id, name=m.get("name", mid), description=m.get("description", ""),
            cap_micros=usd_to_micros(m["cap_usd"]) if m.get("cap_usd") is not None else None,
            founder_hours_allowance=m.get("founder_hours_allowance"), review_date=m.get("review_date"),
            minimum_deliverable=m.get("minimum_deliverable", "")))
    forge = raw.get("forge") or {}
    data_dir = (base / forge.get("data_dir", ".forge")).resolve()
    return ProjectConfig(
        path=path, project=project, milestones=milestones,
        project_cap_micros=usd_to_micros(b["project_cap_usd"]) if b.get("project_cap_usd") is not None else None,
        default_root_ceiling_micros=usd_to_micros(b["default_root_ceiling_usd"])
        if b.get("default_root_ceiling_usd") is not None else None,
        check_specs=raw.get("checks") or {}, provider_specs=raw.get("providers") or {},
        reviewer_route=(raw.get("reviewer") or {}).get("route"), pricing=raw.get("pricing") or {},
        data_dir=data_dir, runtime=forge, catalogue=dict(raw.get("catalogue") or {}),
        assets=dict(raw.get("assets") or {}),
    )


def lane_config(cfg: ProjectConfig, *, manifest: Optional[ToolchainManifest] = None, index: str | None = None,
                blender: str | None = None, reject_list: str | None = None):
    """Catalogue lane configuration from ``[catalogue]`` (no network, no optional imports)."""
    from .lanes.asset_tools import BlenderAdapter, find_blender
    from .lanes.catalogue import MAX_DOWNLOAD_BYTES, MB, objaverse_cache_dir
    from .lanes.run_catalogue import LaneConfig

    c = cfg.catalogue
    base = cfg.path.parent
    idx = index or c.get("index")
    rl = reject_list or c.get("reject_list")
    return LaneConfig(
        index_path=(base / idx).resolve() if idx else cfg.data_dir / "catalogue" / "index.sqlite",
        cache_dir=objaverse_cache_dir(),
        work_root=cfg.data_dir / "catalogue" / "work",
        reject_list_path=(base / rl).resolve() if rl else None,
        # may be lowered, never raised above the owner's 500 MB rule
        max_download_bytes=min(int(c["max_download_mb"]) * MB, MAX_DOWNLOAD_BYTES) if c.get("max_download_mb")
        else MAX_DOWNLOAD_BYTES,
        blender=BlenderAdapter(find_blender(blender or c.get("blender"), manifest)),
    )


def build_providers(cfg: ProjectConfig) -> dict[str, ProviderAdapter]:
    out: dict[str, ProviderAdapter] = {}
    prices = PriceTable.from_config(cfg.pricing)
    for name, spec in cfg.provider_specs.items():
        kind = spec.get("type")
        if kind == "fake":
            out[name] = FakeProvider(name=name, vendor=spec.get("vendor", "fake"),
                                     ceiling_micros=usd_to_micros(spec.get("ceiling_usd", 0.05)))
        elif kind == "claude_api":
            from .providers.claude_api import (ClaudeAdapterConfig, ClaudeAPIProvider, ClaudeRoleConfig,
                                               broker_from_env)

            roles = ClaudeRoleConfig(
                builder=spec.get("builder_model", "claude-opus-5-5"),
                reviewer=spec.get("reviewer_model", "claude-opus-5-5"),
                visual_judge=spec.get("visual_judge_model", "claude-opus-5-5"),
                effort_builder=spec.get("effort_builder", "high"),
                effort_reviewer=spec.get("effort_reviewer", "high"))
            acfg = ClaudeAdapterConfig(
                roles=roles, max_turns=int(spec.get("max_turns", 16)), max_tokens=int(spec.get("max_tokens", 16000)),
                max_context_bytes=int(spec.get("max_context_bytes", 250_000)),
                transport_retries=int(spec.get("transport_retries", 2)),
                refusal_fallback=bool(spec.get("refusal_fallback", False)),
                inference_geo=spec.get("inference_geo"))
            broker = broker_from_env(cfg.project.id, [cfg.project.repo_path],
                                     str(Path(spec["key_file"]).expanduser()) if spec.get("key_file") else None)
            out[name] = ClaudeAPIProvider(project_id=cfg.project.id, broker=broker, prices=prices, config=acfg)
        elif kind == "official_cli":
            from .providers.cli_connector import OfficialCLIConnector

            out[name] = OfficialCLIConnector(binary=spec.get("binary", "claude"), vendor=spec.get("vendor", "anthropic"))
        else:
            raise ConfigError(f"provider {name}: unknown type {kind!r}")
    return out


def load_manifest(data_dir: Path) -> Optional[ToolchainManifest]:
    p = data_dir / "toolchain-manifest.json"
    if p.exists():
        return ToolchainManifest.model_validate_json(p.read_text())
    return None


def build_runtime(cfg: ProjectConfig, providers: dict[str, ProviderAdapter] | None = None) -> ProjectRuntime:
    manifest = load_manifest(cfg.data_dir)
    cap = None
    if manifest:
        gpu = manifest.gpus[0] if manifest.gpus else {}
        cap = WorkerCapability(worker_id="local", host=str(manifest.os.get("node", "local")),
                               os=str(manifest.os.get("system", "")), gpu_model=gpu.get("name"),
                               vram_gb=gpu.get("memory_total_gb"), ram_gb=manifest.ram_gb,
                               disk_free_gb=manifest.disk_free_gb)
    rt = cfg.runtime
    runtime = ProjectRuntime(
        project=cfg.project, data_dir=cfg.data_dir, checks=build_registry(cfg.check_specs),
        providers=providers if providers is not None else build_providers(cfg),
        reviewer_route=cfg.reviewer_route, manifest=manifest, capability=cap,
        transport_retries=int(rt.get("transport_retries", 2)), backoff_s=float(rt.get("backoff_s", 2.0)),
        lease_ttl_s=float(rt.get("lease_ttl_s", 900)), unattended=bool(cfg.project.policy.unattended_mode),
    )
    runtime.catalogue = lane_config(cfg, manifest=manifest)
    if cfg.assets:
        from .assets.config import generation_lane_from_config

        runtime.generation_lane = generation_lane_from_config(
            cfg.assets, base=cfg.path.parent, project_id=cfg.project.id, repo_root=Path(cfg.project.repo_path),
            data_dir=cfg.data_dir, manifest=manifest)
    return runtime


def init_project(cfg: ProjectConfig, store: Store, *, by: str = "owner") -> None:
    """Persist the project, milestones and configured caps; create the accepted branch."""
    from .gitops import WorkspaceManager

    store.put_project(cfg.project)
    ledger = BudgetLedger(store)
    for m in cfg.milestones:
        store.put_milestone(m)
        if m.cap_micros is not None and ledger.get_cap(milestone_scope(m.id)) is None:
            ledger.set_cap(milestone_scope(m.id), m.cap_micros, by=f"{by} (project.toml)")
    if cfg.project_cap_micros is not None and ledger.get_cap(project_scope(cfg.project.id)) is None:
        ledger.set_cap(project_scope(cfg.project.id), cfg.project_cap_micros, by=f"{by} (project.toml)")
    ws = WorkspaceManager(cfg.project.repo_path, cfg.data_dir / "worktrees" / cfg.project.id)
    head = ws.ensure_branch(cfg.project.accepted_branch)
    store.append_event("project_initialised", project_id=cfg.project.id, accepted_branch=cfg.project.accepted_branch,
                       accepted_head=head, config=str(cfg.path))


def dump_json(obj: Any) -> str:
    return json.dumps(obj, indent=2, default=str, sort_keys=True)
