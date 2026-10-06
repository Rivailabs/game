"""Generation adapters per route and the factory that builds them from ``[assets.routes.<id>]``."""

from __future__ import annotations

from pathlib import Path
from typing import Any, Optional

from ...credentials import CredentialBroker, ProviderCredentialSpec
from ...sandbox.runner import SandboxRunner
from ..routes import ROUTES
from .api import CreditPricing, DeepMotionAdapter, ImageAPIAdapter, MeshyAdapter, TripoAdapter
from .base import (
    AdapterError,
    AdapterUnavailable,
    GenerationAdapter,
    GenerationJob,
    JobResult,
    JobStatus,
    JobTimeout,
    ManualStepPending,
    UncertainSubmission,
    run_job,
)
from .local import LocalModelAdapter
from .manual import LicensedLibraryAdapter, ManualStepAdapter

__all__ = [
    "AdapterError", "AdapterUnavailable", "CreditPricing", "DeepMotionAdapter", "GenerationAdapter", "GenerationJob",
    "ImageAPIAdapter", "JobResult", "JobStatus", "JobTimeout", "LicensedLibraryAdapter", "LocalModelAdapter",
    "ManualStepAdapter", "ManualStepPending", "MeshyAdapter", "TripoAdapter", "UncertainSubmission",
    "build_adapters", "run_job",
]

API_CLASSES = {"meshy": MeshyAdapter, "tripo": TripoAdapter, "deepmotion": DeepMotionAdapter}
DEFAULT_KEY_ENV = {"meshy": "MESHY_API_KEY", "tripo": "TRIPO_API_KEY", "deepmotion": "DEEPMOTION_CREDENTIALS",
                   "api-image-service": "FORGE_IMAGE_API_KEY", "commercial-rigging": "FORGE_RIGGING_API_KEY"}


def build_adapters(route_specs: dict[str, dict[str, Any]], *, project_id: str, repo_roots: list[str],
                   data_dir: Path, runner: Optional[SandboxRunner] = None,
                   transports: dict[str, Any] | None = None) -> dict[str, GenerationAdapter]:
    """Adapters for the routes the owner configured. Unknown route ids are an error, not ignored."""
    broker = CredentialBroker(repo_roots=list(repo_roots))
    out: dict[str, GenerationAdapter] = {}
    for rid, spec in route_specs.items():
        route = ROUTES.get(rid)
        if route is None:
            raise ValueError(f"[assets.routes.{rid}]: unknown route (known: {sorted(ROUTES)})")
        transport = (transports or {}).get(rid)
        if route.kind == "api":
            broker.register(ProviderCredentialSpec(rid, spec.get("key_env", DEFAULT_KEY_ENV.get(rid, "")),
                                                   spec.get("key_file")))
            broker.allow(project_id, rid)

            def key(rid=rid) -> str:
                return broker.get(project_id, rid)

            pricing = CreditPricing(spec.get("credit_usd"), spec.get("credits"))
            if rid in API_CLASSES:
                out[rid] = API_CLASSES[rid](key, transport=transport, pricing=pricing)
            elif rid == "api-image-service":
                if not spec.get("endpoint"):
                    raise ValueError("[assets.routes.api-image-service] needs endpoint and vendor")
                out[rid] = ImageAPIAdapter(key, endpoint=spec["endpoint"], vendor=spec.get("vendor", ""),
                                           transport=transport, pricing=pricing)
            else:
                raise ValueError(f"{rid}: no API adapter implemented yet (configure a certified vendor adapter)")
        elif route.kind == "local_model":
            out[rid] = LocalModelAdapter(rid, argv=spec.get("argv"), install_dir=spec.get("install_dir"),
                                         outputs=spec.get("outputs"), runner=runner,
                                         timeout_s=float(spec.get("timeout_s", 7200)),
                                         model_version=str(spec.get("model_version", "")), env=spec.get("env"))
        elif route.kind == "manual":
            out[rid] = ManualStepAdapter(rid, Path(spec.get("inbox", data_dir / "assets" / "manual")))
        elif route.kind == "library":
            out[rid] = LicensedLibraryAdapter(rid, spec.get("path"))
    return out
