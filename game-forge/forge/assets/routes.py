"""Model routes, hardware qualification and first-use licence gates.

The table below is the plan's routing shortlist ("Model routes hardware qualification and first use
licence gates"). Published requirements were checked by the plan on 5 October 2026; the gates and
exclusions are Forge policy. Nothing here claims the owner's machine can run a route: a local route
is selectable only on a worker whose *single* GPU meets the published stage requirement (VRAM is
never pooled across GPUs), or on which a benchmark of that exact configuration succeeded.

Route selection refuses a route when:

* its licence excludes a territory the project distributes to (Hunyuan3D 2.1, HY-Motion 1.0: EU, UK,
  South Korea), unless separately appropriate rights are documented;
* it is excluded entirely (GVHMR) and no documented permission and dependency clearance exist;
* no qualified worker exists (published VRAM, OS, or a recorded successful benchmark);
* the first-use licence gate has not been completed by the owner;
* it is a manual browser step (Mixamo) or an uncertified adapter and the project runs unattended;
* the vendor, data classes or region are not allowed by the project policy.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Optional

from .territory import excluded_overlap


class AssetLaneKind(str, Enum):
    CONCEPT_IMAGE = "concept_image"
    MESH = "mesh"
    RIG = "rig"
    MOTION = "motion"
    AUDIO = "audio"


@dataclass(frozen=True)
class StageRequirement:
    stage: str  # e.g. "shape", "texture", "shape+texture", "lite", "full", "default"
    vram_gb: Optional[float]  # published minimum; None = not published (benchmark required)
    source: str = ""  # plan source reference, e.g. "S07"


@dataclass(frozen=True)
class LicenceInfo:
    name: str
    url: str = ""
    commercial_use: str = "verify"  # permitted | permission_required | verify
    excluded_territories: tuple[str, ...] = ()
    excluded_entirely: bool = False  # e.g. GVHMR: needs documented permission
    output_terms: str = ""
    dependency_note: str = ""
    source: str = ""


@dataclass(frozen=True)
class RouteDescriptor:
    id: str
    title: str
    lanes: tuple[AssetLaneKind, ...]
    kind: str  # local_model | api | manual | library
    vendor: str
    licence: LicenceInfo
    stages: tuple[StageRequirement, ...] = ()
    os_support: tuple[str, ...] = ("linux",)
    linux_tested_only: bool = False
    data_destinations: tuple[str, ...] = ()
    data_classes: tuple[str, ...] = ()
    billing: str = ""
    region: str = "global"
    manual_step: bool = False
    benchmark_required: bool = False  # a recorded benchmark is needed even when VRAM is published
    requires_certification: bool = False  # adapter checks must pass before unattended use
    extra_stages: tuple[str, ...] = ()  # Forge stages the route needs (e.g. HY-Motion loop cleanup)
    plan_decision: str = ""

    def stage(self, name: str | None) -> Optional[StageRequirement]:
        if not self.stages:
            return None
        if name is None:
            return self.stages[0]
        return next((s for s in self.stages if s.stage == name), None)

    def to_dict(self) -> dict:
        return {
            "id": self.id, "title": self.title, "lanes": [lane.value for lane in self.lanes], "kind": self.kind,
            "vendor": self.vendor, "licence": self.licence.__dict__ | {"excluded_territories":
                                                                        list(self.licence.excluded_territories)},
            "stages": [s.__dict__ for s in self.stages], "os_support": list(self.os_support),
            "data_destinations": list(self.data_destinations), "data_classes": list(self.data_classes),
            "billing": self.billing, "manual_step": self.manual_step, "benchmark_required": self.benchmark_required,
            "requires_certification": self.requires_certification, "extra_stages": list(self.extra_stages),
            "plan_decision": self.plan_decision,
        }


L = AssetLaneKind
LOCAL_DEST = ("the owner's GPU worker (local or rented: the GPU provider receives inputs/results)",)

ROUTES: dict[str, RouteDescriptor] = {r.id: r for r in (
    RouteDescriptor(
        "flux-schnell-local", "FLUX.1 [schnell] concept images (local)", (L.CONCEPT_IMAGE,), "local_model", "local",
        LicenceInfo("FLUX.1 [schnell] model licence (record the exact checkpoint)", commercial_use="verify",
                    output_terms="Record the exact checkpoint, dependency and service terms at the first-use gate."),
        stages=(StageRequirement("default", None),), data_destinations=LOCAL_DEST,
        data_classes=("prompt",), billing="owner GPU time", benchmark_required=True,
        plan_decision="Benchmark the selected configuration; do not market an untested compressed 8-12 GB profile "
                      "as guaranteed"),
    RouteDescriptor(
        "api-image-service", "Approved API image service (concept images)", (L.CONCEPT_IMAGE,), "api", "configured",
        LicenceInfo("Service terms of the approved image API", commercial_use="verify",
                    output_terms="Record output-rights terms at the first-use gate"),
        data_destinations=("the configured image API",), data_classes=("prompt", "reference_image"),
        billing="vendor account (owner)", requires_certification=True,
        plan_decision="Exact checkpoint, dependency and service terms need recording"),
    RouteDescriptor(
        "hunyuan3d-2.1", "Hunyuan3D 2.1 (image/text to 3D)", (L.MESH,), "local_model", "local",
        LicenceInfo("Tencent Hunyuan 3D 2.1 Community License", commercial_use="verify",
                    excluded_territories=("EU", "UK", "KR"),
                    output_terms="Use and distribution of outputs restricted outside the licence territory "
                                 "(excludes the EU, UK and South Korea)", source="S13"),
        stages=(StageRequirement("shape", 10, "S07"), StageRequirement("texture", 21, "S07"),
                StageRequirement("shape+texture", 29, "S07")),
        data_destinations=LOCAL_DEST, data_classes=("prompt", "reference_image"), billing="owner GPU time",
        plan_decision="Separate stage profiles; optional, territory-restricted route. Low-memory/community variants "
                      "require independent benchmarks"),
    RouteDescriptor(
        "trellis", "TRELLIS (original)", (L.MESH,), "local_model", "local",
        LicenceInfo("TRELLIS repository licence (MIT); verify dependency licences", commercial_use="verify",
                    dependency_note="Check dependency licences of the pinned environment"),
        stages=(StageRequirement("default", 16, "S08"),), linux_tested_only=True, data_destinations=LOCAL_DEST,
        data_classes=("prompt", "reference_image"), billing="owner GPU time", benchmark_required=True,
        plan_decision="Separate adapter and benchmark; a T4-sized memory capacity alone does not establish runtime "
                      "compatibility"),
    RouteDescriptor(
        "trellis2", "TRELLIS.2", (L.MESH,), "local_model", "local",
        LicenceInfo("TRELLIS.2 repository licence; separate dependency licences noted", commercial_use="verify",
                    dependency_note="Separate dependency licences are noted by the repository", source="S09"),
        stages=(StageRequirement("default", 24, "S09"),), os_support=("linux",), linux_tested_only=True,
        data_destinations=LOCAL_DEST, data_classes=("prompt", "reference_image"), billing="owner GPU time",
        benchmark_required=True,
        plan_decision="Linux route only initially; validate actual GPU architecture, dependencies, memory and export "
                      "quality"),
    RouteDescriptor(
        "hy-motion-1.0", "HY-Motion 1.0 (text to motion)", (L.MOTION,), "local_model", "local",
        LicenceInfo("Tencent HY-Motion 1.0 licence", commercial_use="verify", excluded_territories=("EU", "UK", "KR"),
                    output_terms="Use and distribution of outputs restricted outside the licence territory "
                                 "(excludes the EU, UK and South Korea)", source="S14"),
        stages=(StageRequirement("lite", 24, "S10"), StageRequirement("full", 26, "S10")),
        data_destinations=LOCAL_DEST, data_classes=("prompt",), billing="owner GPU time",
        extra_stages=("loop_cleanup", "root_motion_conversion"),
        plan_decision="Remove the unverified '6 GB Lite' promise; short clips and any offload mode are benchmarked "
                      "configurations, not guaranteed support; prompt-engineering memory is additional"),
    RouteDescriptor(
        "unirig", "UniRig (automatic rigging)", (L.RIG,), "local_model", "local",
        LicenceInfo("UniRig release/model licence (verify exact release)", commercial_use="verify"),
        stages=(StageRequirement("default", None),), data_destinations=LOCAL_DEST, data_classes=("mesh",),
        billing="owner GPU time", benchmark_required=True,
        plan_decision="Gate by tested skeleton contract and deformation quality; no fixed unverified 8 GB guarantee"),
    RouteDescriptor(
        "commercial-rigging", "Approved commercial rigging service", (L.RIG,), "api", "configured",
        LicenceInfo("Service terms of the approved rigging vendor", commercial_use="verify"),
        data_destinations=("the configured rigging service",), data_classes=("mesh",), billing="vendor account (owner)",
        requires_certification=True,
        plan_decision="Gate by tested skeleton contract and deformation quality"),
    RouteDescriptor(
        "gvhmr", "GVHMR (video to motion)", (L.MOTION,), "local_model", "local",
        LicenceInfo("GVHMR default licence: educational, research and nonprofit use only",
                    commercial_use="permission_required", excluded_entirely=True, source="S11",
                    output_terms="Commercial users are directed to seek permission"),
        stages=(StageRequirement("default", None),), data_destinations=LOCAL_DEST, data_classes=("video",),
        billing="owner GPU time", benchmark_required=True,
        plan_decision="Excluded from both the internal commercial game pilot and the customer product unless "
                      "documented permission and dependency rights are cleared"),
    RouteDescriptor(
        "meshy", "Meshy API (text/image to 3D, rigging)", (L.MESH, L.RIG), "api", "meshy",
        LicenceInfo("Meshy terms of service (output rights depend on plan tier)", "https://www.meshy.ai/terms",
                    commercial_use="verify", output_terms="Record plan tier and output-rights terms at first use"),
        data_destinations=("api.meshy.ai (Meshy, United States)",), data_classes=("prompt", "reference_image", "mesh"),
        billing="Meshy credits on the owner's account", region="us", requires_certification=True,
        plan_decision="Certify the adapter; include regeneration and rejected outputs in usage"),
    RouteDescriptor(
        "tripo", "Tripo API (text/image to 3D, rig, retarget)", (L.MESH, L.RIG, L.MOTION), "api", "tripo",
        LicenceInfo("Tripo terms of service (output rights depend on plan tier)", "https://www.tripo3d.ai/terms",
                    commercial_use="verify"),
        data_destinations=("api.tripo3d.ai (VAST)",), data_classes=("prompt", "reference_image", "mesh"),
        billing="Tripo credits on the owner's account", requires_certification=True,
        plan_decision="Certify the adapter; include regeneration and rejected outputs in usage"),
    RouteDescriptor(
        "deepmotion", "DeepMotion Animate 3D API (video to motion)", (L.MOTION,), "api", "deepmotion",
        LicenceInfo("DeepMotion terms of service", "https://www.deepmotion.com/terms", commercial_use="verify"),
        data_destinations=("DeepMotion API",), data_classes=("video",), billing="DeepMotion credits",
        requires_certification=True,
        plan_decision="Certify the adapter; include regeneration and rejected outputs in usage"),
    RouteDescriptor(
        "mixamo", "Mixamo (manual browser step: auto-rig and animations)", (L.RIG, L.MOTION), "manual", "adobe",
        LicenceInfo("Adobe Mixamo terms (royalty-free use in projects; no redistribution of raw files)",
                    "https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html", commercial_use="verify"),
        data_destinations=("mixamo.com (Adobe) via the owner's browser",), data_classes=("mesh",),
        billing="Adobe account (owner)", manual_step=True,
        plan_decision="Show manual steps explicitly"),
    RouteDescriptor(
        "stock-animation", "Licensed stock animation pack", (L.MOTION,), "library", "configured",
        LicenceInfo("Licence of the purchased animation pack (record seat/receipt)", commercial_use="verify"),
        data_destinations=("none (local library)",), data_classes=(), billing="one-off purchase",
        plan_decision="Certify each adapter; show manual steps explicitly"),
    RouteDescriptor(
        "licensed-sound-library", "Licensed sound library", (L.AUDIO,), "library", "configured",
        LicenceInfo("Licence of the documented sound library (record licence id)", commercial_use="verify"),
        data_destinations=("none (local library)",), data_classes=(), billing="library licence",
        plan_decision="Start with a documented licensed library or a specifically approved model/service and "
                      "output-rights record"),
)}


# --------------------------------------------------------------------------- selection context


@dataclass
class PermissionRecord:
    """Documented rights that lift a licence restriction (territory or entire exclusion)."""

    route: str
    scope: str  # "territory" | "commercial_use"
    document: str  # reference to the signed permission / agreement
    dependency_rights_cleared: bool = False
    by: str = ""
    at: float = 0.0


@dataclass
class LicenceGate:
    """The owner's first-use decision for a route (terms recorded before the first generation)."""

    route: str
    terms_name: str
    terms_version: str
    output_rights: str
    territory_decision: str
    public_demo_allowed: bool
    accepted_by: str
    accepted_at: float
    checkpoint: str = ""  # exact model checkpoint / service plan


@dataclass
class GpuSlot:
    worker_id: str
    gpu_uuid: str
    gpu_model: str
    vram_total_gb: float
    vram_free_gb: Optional[float]
    os: str = "linux"
    remote_provider: str = ""  # e.g. "azure" (data then reaches that provider)


@dataclass
class Benchmark:
    route: str
    stage: str
    worker_id: str
    gpu_model: str
    vram_total_gb: float
    peak_vram_gb: Optional[float]
    duration_s: float
    success: bool
    config: str = ""
    at: float = 0.0


@dataclass
class RouteContext:
    distribution_countries: list[str]
    public_demo: bool = False
    gpus: list[GpuSlot] = field(default_factory=list)
    permissions: list[PermissionRecord] = field(default_factory=list)
    licence_gates: dict[str, LicenceGate] = field(default_factory=dict)
    benchmarks: list[Benchmark] = field(default_factory=list)
    certified: set[str] = field(default_factory=set)
    configured: set[str] = field(default_factory=set)  # routes with an adapter configured on this install
    unattended: bool = True
    allowed_vendors: list[str] = field(default_factory=list)
    allowed_data_classes: list[str] = field(default_factory=list)
    allowed_regions: list[str] = field(default_factory=lambda: ["global"])


@dataclass
class RouteDecision:
    route: str
    allowed: bool
    reasons: list[str] = field(default_factory=list)
    gpu: Optional[GpuSlot] = None
    stage: Optional[str] = None
    qualification: str = ""  # "published requirement" | "benchmarked configuration" | "no GPU needed"

    def to_dict(self) -> dict:
        return {"route": self.route, "allowed": self.allowed, "reasons": self.reasons, "stage": self.stage,
                "gpu": self.gpu.__dict__ if self.gpu else None, "qualification": self.qualification}


class RouteRefused(Exception):
    def __init__(self, lane: AssetLaneKind, decisions: list[RouteDecision]):
        self.lane, self.decisions = lane, decisions
        why = "; ".join(f"{d.route}: {', '.join(d.reasons)}" for d in decisions) or "no route listed"
        super().__init__(f"no permitted {lane.value} route: {why}")


def _has_permission(ctx: RouteContext, route: str, scope: str, *, need_dependencies: bool = False) -> bool:
    return any(p.route == route and p.scope == scope and p.document and
               (p.dependency_rights_cleared or not need_dependencies) for p in ctx.permissions)


def qualify_hardware(route: RouteDescriptor, stage: str | None, ctx: RouteContext) -> tuple[Optional[GpuSlot], str, str]:
    """Pick one GPU that qualifies (never pools VRAM). Returns (gpu, qualification, reason-if-none)."""
    if route.kind != "local_model":
        return None, "no GPU needed", ""
    req = route.stage(stage)
    if req is None:
        return None, "", f"unknown stage {stage!r} (published stages: {[s.stage for s in route.stages]})"
    reasons = []
    for g in ctx.gpus:
        if g.os not in route.os_support:
            reasons.append(f"{g.worker_id}/{g.gpu_model}: OS {g.os} not supported")
            continue
        bench = [b for b in ctx.benchmarks if b.route == route.id and b.stage == req.stage and b.success and
                 b.gpu_model == g.gpu_model and (b.peak_vram_gb is None or b.peak_vram_gb <= g.vram_total_gb)]
        meets = req.vram_gb is not None and g.vram_total_gb >= req.vram_gb
        if meets and (not route.benchmark_required or bench):
            return g, "published requirement" + (" + benchmark" if bench else ""), ""
        if req.vram_gb is None and bench:
            return g, "benchmarked configuration", ""
        if not meets and bench:
            # a lighter benchmarked configuration on this exact GPU model (e.g. a low-memory variant)
            return g, "benchmarked configuration (below the published requirement)", ""
        if meets and route.benchmark_required:
            reasons.append(f"{g.worker_id}/{g.gpu_model}: meets {req.vram_gb} GB but no successful benchmark "
                           f"of {route.id}/{req.stage} recorded")
        elif req.vram_gb is None:
            reasons.append(f"{g.worker_id}/{g.gpu_model}: requirement not published; benchmark required")
        else:
            reasons.append(f"{g.worker_id}/{g.gpu_model} has {g.vram_total_gb} GB < {req.vram_gb} GB "
                           f"({req.stage}, {req.source or 'published'})")
    if not ctx.gpus:
        reasons.append("no GPU worker with a capability record (run `forge assets worker-preflight`)")
    return None, "", "; ".join(reasons)


def evaluate_route(route_id: str, lane: AssetLaneKind, ctx: RouteContext, *, stage: str | None = None) -> RouteDecision:
    route = ROUTES.get(route_id)
    if route is None:
        return RouteDecision(route_id, False, ["unknown route"])
    d = RouteDecision(route_id, True, stage=stage)
    r = d.reasons
    if lane not in route.lanes:
        r.append(f"route does not serve the {lane.value} lane")
    if route.licence.excluded_entirely and not _has_permission(ctx, route.id, "commercial_use", need_dependencies=True):
        r.append("excluded by its licence unless documented permission and dependency rights are recorded")
    if not ctx.distribution_countries:
        r.append("project has not declared its intended distribution countries")
    hits = excluded_overlap(ctx.distribution_countries, route.licence.excluded_territories)
    if hits and not _has_permission(ctx, route.id, "territory"):
        r.append(f"licence excludes {', '.join(hits)} where the project distributes")
    if route.manual_step and ctx.unattended:
        r.append("manual browser step: not available in unattended mode")
    if route.requires_certification and route.id not in ctx.certified and ctx.unattended:
        r.append("adapter not certified (run its adapter checks before unattended use)")
    if route.kind in ("api", "local_model", "library") and ctx.configured and route.id not in ctx.configured:
        r.append("route not configured on this install")
    if route.kind == "api":
        if route.vendor not in ctx.allowed_vendors:
            r.append(f"vendor {route.vendor} is not in the project's allowed vendors")
        if route.region not in ctx.allowed_regions and "global" not in ctx.allowed_regions:
            r.append(f"region {route.region} is not permitted")
    extra = set(route.data_classes) - set(ctx.allowed_data_classes)
    if route.kind in ("api", "manual") and extra:
        r.append(f"would send data classes not permitted: {sorted(extra)}")
    gate = ctx.licence_gates.get(route.id)
    if gate is None:
        r.append("first-use licence gate not completed (record terms, output rights and territory decision)")
    elif ctx.public_demo and not gate.public_demo_allowed:
        r.append("licence gate does not allow public demos/marketing use")
    gpu, qual, why = qualify_hardware(route, stage, ctx)
    if route.kind == "local_model":
        if gpu is None:
            r.append(why)
        else:
            d.gpu, d.qualification = gpu, qual
            if gpu.remote_provider:
                extra_remote = set(route.data_classes) - set(ctx.allowed_data_classes)
                if extra_remote:
                    r.append(f"remote GPU at {gpu.remote_provider} would receive {sorted(extra_remote)}")
                if gpu.remote_provider not in ctx.allowed_vendors:
                    r.append(f"GPU provider {gpu.remote_provider} is not an allowed vendor")
    else:
        d.qualification = qual
    d.allowed = not r
    return d


def select_route(lane: AssetLaneKind, preferences: list[str], ctx: RouteContext, *,
                 stages: dict[str, str] | None = None) -> RouteDecision:
    """The first route in the owner's preference order that passes every gate; raises :class:`RouteRefused`."""
    decisions = []
    for rid in preferences:
        route = ROUTES.get(rid)
        if route is not None and lane not in route.lanes:
            continue
        d = evaluate_route(rid, lane, ctx, stage=(stages or {}).get(rid))
        if d.allowed:
            return d
        decisions.append(d)
    raise RouteRefused(lane, decisions)
