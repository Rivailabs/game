"""The R2 generation lane: runs the asset production lane one owner gate at a time.

One orchestrator dispatch of an asset root runs one *segment*: from the asset's next unsatisfied stage
up to the next owner gate (concept approval, visual approval, deformation review, contact/transition
review, device evidence). The segment's outputs become a normal Forge candidate commit under
``<workdir>/assets/source/<id>/`` (and the Unity art folder for the prefab stage), so the owner approves
the exact hash on the review page and the integration writer checkpoints it into accepted main. The
next root task for the same brief continues after that gate; a visual rejection ("repair" with a
correction) re-runs the stages that produced what was rejected, with the correction in the prompt.

Every external step is behind an interface: generation adapters (per route), the GPU scheduler,
Blender (normalization/rig/motion scripts), the Unity prefab step and the existing device checks. A
missing tool or credential gives BLOCKED/PAUSED with the reason; nothing is pretended.

Exit codes follow the catalogue lane so the orchestrator treats both alike: 0 = candidate written and
awaiting the owner, 2 = no permitted route or the owner must decide (NEEDS_INPUT), 3 = blocked/paused
(tool, GPU, credential, budget, manual step, contract incompatibility).
"""

from __future__ import annotations

import json
import shutil
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING, Any, Optional

from ..budget import BudgetExceeded, DispatchStopped, NoCapConfigured, UnknownCeiling
from ..credentials import redact
from ..lanes.asset_tools import ToolMissing
from ..models import (
    AttemptStatus,
    CandidateAttempt,
    Decision,
    Evidence,
    EvidenceClass,
    EvidenceStatus,
    FailureCategory,
    RootTask,
    TaskState,
)
from ..util import new_id
from .adapters import (
    AdapterError,
    AdapterUnavailable,
    GenerationAdapter,
    GenerationJob,
    JobTimeout,
    ManualStepPending,
    UncertainSubmission,
    run_job,
)
from .adapters.base import sha256_file
from .blender_tools import (
    BlenderAssetTools,
    BlenderFailed,
    geometry_from_report,
    materials_from_report,
    motion_from_report,
    skeleton_from_report,
)
from .brief import AssetBrief, brief_path_for, load_asset_brief
from .budgets import check_asset
from .clips import ARCHER_CLIPS, check_clip, needs_cleanup
from .contracts import (
    InputReference,
    ProvenanceRecord,
    TerritoryFlags,
    TermsRef,
    Transformation,
    validate_geometry,
    validate_materials,
    validate_provenance,
    validate_skeleton,
)
from .gpu import GpuScheduler, GpuUnavailable
from .lane import GATES, AssetLane, ChangeKind, LaneError, Stage, StageStatus, segment
from .provenance import write_asset_provenance
from .registry import AssetRegistry
from .routes import ROUTES, AssetLaneKind, RouteContext, RouteDecision, RouteRefused, select_route
from .skeleton import ARCHER_JOINTS, builtin_mapping, mapping_sha256, validate_mapping
from .unity_prefab import ClipImport, PrefabManifest, TextureImport, UnityPrefabStep

if TYPE_CHECKING:  # pragma: no cover
    from ..orchestrator import Orchestrator

EXIT_PICKED, EXIT_NONE, EXIT_BLOCKED = 0, 2, 3
STATUS = {EXIT_PICKED: "AWAITING_OWNER", EXIT_NONE: "NEEDS_OWNER_DECISION", EXIT_BLOCKED: "BLOCKED"}
VIEWS = ("front", "side", "back", "three_quarter")
CONTRACT_EVIDENCE = "asset-contract-check"
#: Default mapping per motion/rig source skeleton (owner can override per route in config).
DEFAULT_MAPPINGS = {"mixamo": "mixamo", "stock-animation": "mixamo", "hy-motion-1.0": "smpl"}


@dataclass
class AssetsConfig:
    preferences: dict[str, list[str]] = field(default_factory=dict)  # lane kind -> route ids (owner order)
    routes: dict[str, dict[str, Any]] = field(default_factory=dict)  # [assets.routes.<id>]
    unity_project_dir: str = "unity"
    unity_art_root: str = "Assets/Forge/Art"
    device_checks: list[str] = field(default_factory=list)
    public_demo: bool = False
    poll_interval_s: float = 10.0
    fps: float = 30.0
    briefs_dir: Optional[Path] = None


@dataclass
class SegmentResult:
    exit_code: int
    brief_id: str
    reason: str
    pick: Optional[str] = None
    out_dir: Optional[str] = None
    stages: list[str] = field(default_factory=list)
    gate: Optional[str] = None
    asset_stage: dict = field(default_factory=dict)
    cost_micros: int = 0
    failed_jobs: int = 0
    pick_record: dict = field(default_factory=dict)
    requires_human_approval: bool = True

    @property
    def status(self) -> str:
        return STATUS[self.exit_code]

    def to_dict(self) -> dict:
        d = asdict(self)
        d["status"] = self.status
        return d


class StageBlocked(Exception):
    """Pause (exit 3): a tool, GPU, credential, budget, manual step or contract incompatibility."""


class NeedsOwner(Exception):
    """Owner decision required (exit 2): no permitted route, policy, brief."""


class GenerationLane:
    def __init__(self, cfg: AssetsConfig, adapters: dict[str, GenerationAdapter], blender: BlenderAssetTools,
                 unity: Optional[UnityPrefabStep] = None):
        self.cfg = cfg
        self.adapters = adapters
        self.blender = blender
        self.unity = unity
        self.orch: Optional["Orchestrator"] = None

    # ================================================================ wiring
    def bind(self, orch: "Orchestrator") -> None:
        self.orch = orch
        self.store = orch.store
        self.registry = AssetRegistry(orch.store)
        self.gpu = GpuScheduler(orch.leases, self.registry.workers)
        if self.unity is None:
            self.unity = UnityPrefabStep(orch.rt.manifest)

    @property
    def data_dir(self) -> Path:
        return self.orch.rt.data_dir

    def _brief(self, root: RootTask) -> tuple[AssetBrief, str]:
        p = Path(root.asset_brief or "")
        p = p if p.is_absolute() else self.orch.rt.repo_path / p
        cand = brief_path_for(p) if not p.name.endswith(".asset.json") else p
        if not cand.exists() and self.cfg.briefs_dir is not None:
            cand = self.cfg.briefs_dir / f"{p.stem.removesuffix('.asset')}.asset.json"
        if not cand.exists():
            raise NeedsOwner(f"no production brief {cand.name} (screen size, silhouette, scale, materials, "
                             "attachments, animation, budgets and licence requirements are required)")
        try:
            return load_asset_brief(cand)
        except ValueError as e:
            raise NeedsOwner(f"production brief invalid: {e}"[:500]) from e

    def lane_for(self, brief: AssetBrief, brief_sha: str) -> AssetLane:
        data = self.registry.load_lane(brief.id)
        if data:
            return AssetLane.model_validate(data)
        lane = AssetLane.new(brief.id, brief.kind, skip=list(brief.skip_stages), brief_sha256=brief_sha)
        self.save(lane)
        return lane

    def save(self, lane: AssetLane) -> None:
        self.registry.save_lane(lane.asset_id, lane.kind, json.loads(lane.model_dump_json()))

    def asset_rel(self, asset_id: str) -> str:
        wd = (self.orch.project.workdir or ".").strip("/")
        rel = f"assets/source/{asset_id}"
        return rel if wd in ("", ".") else f"{wd}/{rel}"

    def unity_rel(self, asset_id: str) -> str:
        wd = (self.orch.project.workdir or ".").strip("/")
        rel = f"{self.cfg.unity_project_dir}/{self.cfg.unity_art_root}/{asset_id}"
        return rel if wd in ("", ".") else f"{wd}/{rel}"

    def work(self, lane: AssetLane) -> Path:
        return self.data_dir / "assets" / "work" / lane.asset_id / f"v{lane.version}"

    def mirror(self, lane: AssetLane) -> Path:
        """Lane-owned copy of the asset's repository folder (survives abandoned attempts)."""
        return self.work(lane) / "repo"

    # ================================================================ orchestrator hooks
    def handles(self, root: RootTask) -> bool:
        """True when this root continues an asset lane, or its brief skips the catalogue."""
        if root.task_type.value != "asset" or not root.asset_brief or self.orch is None:
            return False
        try:
            brief, sha = self._brief(root)
        except NeedsOwner:
            return False
        if not brief.catalogue_first:
            return True
        data = self.registry.load_lane(brief.id)
        if not data:
            return False
        lane = AssetLane.model_validate(data)
        return lane.rec(Stage.GENERATION).status != StageStatus.PENDING

    def preflight(self, root: RootTask) -> str:
        """Reason this asset root cannot be dispatched now ("" = ready). Nothing is sent anywhere."""
        from ..pathglob import match_path

        try:
            brief, sha = self._brief(root)
        except NeedsOwner as e:
            return str(e)
        lane = self.lane_for(brief, sha)
        self.sync_gates(lane)
        if lane.done():
            return f"asset {brief.id} v{lane.version} has passed every stage"
        if not self.orch.project.policy.intended_release_countries:
            return "declare [policy] intended_release_countries before the first generation"
        probe = f"{self.asset_rel(brief.id)}/provenance.json"
        if not match_path(probe, root.permitted_paths):
            return f"permitted_paths must include {self.asset_rel(brief.id)}/**"
        seg = segment(lane)
        if Stage.UNITY_PREFAB in seg and not match_path(f"{self.unity_rel(brief.id)}/x.prefab", root.permitted_paths):
            return f"permitted_paths must include {self.unity_rel(brief.id)}/** for the prefab stage"
        if lane.rec(seg[-1]).status == StageStatus.AWAITING_APPROVAL and seg[-1] in GATES:
            return f"{seg[-1].value} is awaiting the owner's decision on the review page"
        if any(st in seg for st in (Stage.NORMALIZATION, Stage.RIG, Stage.MOTION)):
            ok, why = self.blender.available()
            if not ok:
                return why
        return ""

    def approval_reason(self, root: RootTask) -> str:
        try:
            brief, sha = self._brief(root)
            lane = self.lane_for(brief, sha)
        except NeedsOwner:
            return "owner approval of the asset required"
        gate = next((s for s in GATES if lane.rec(s).status == StageStatus.AWAITING_APPROVAL), None)
        return (f"owner {gate.value.replace('_', ' ')} of {brief.id} v{lane.version} required (check renders, "
                "budgets and provenance)") if gate else "owner approval of the asset required"

    # ================================================================ gates <- owner decisions
    def sync_gates(self, lane: AssetLane) -> None:
        """Carry owner decisions from the review page (approval records) into the lane's gates.

        An approval counts only when the approved commit contains exactly the bytes the gate recorded.
        """
        import hashlib

        changed = False
        for st in GATES:
            r = lane.rec(st)
            if r.status != StageStatus.AWAITING_APPROVAL or not r.root_id:
                continue
            attempt_id = (r.report or {}).get("attempt_id")
            for apr in self.store.list_approvals(r.root_id):
                if apr.attempt_id != attempt_id:
                    continue
                if apr.visual_approval.decision == Decision.REJECTED:
                    corr = apr.visual_approval.reason or "owner rejected"
                    lane.reject(st, by=apr.visual_approval.by or "owner", correction=corr,
                                at=apr.visual_approval.at or self.store.now())
                    changed = True
                    break
                if apr.visual_approval.decision != Decision.APPROVED:
                    continue
                ok = True
                for path, digest in r.report.get("repo_files", {}).items():
                    raw = _git_bytes(self.orch.rt.repo_path, apr.candidate_hash, path)
                    if raw is None or hashlib.sha256(raw).hexdigest() != digest:
                        ok = False  # the approved commit does not hold the bytes this gate showed
                        self.store.append_event("asset_gate_hash_mismatch", root_id=r.root_id,
                                                asset_id=lane.asset_id, gate=st.value, path=path)
                        break
                if ok:
                    lane.approve(st, gate_hash=r.gate_hash, by=apr.visual_approval.by or "owner",
                                 at=apr.visual_approval.at or self.store.now(), candidate_hash=apr.candidate_hash,
                                 root_id=r.root_id)
                    self.store.append_event("asset_gate_approved", root_id=r.root_id, asset_id=lane.asset_id,
                                            gate=st.value, gate_hash=r.gate_hash, candidate=apr.candidate_hash)
                    changed = True
                    break
        if changed:
            self.save(lane)

    # ================================================================ catalogue fallthrough
    def __call__(self, root: RootTask, target: Path, brief_unused: Any) -> SegmentResult:
        """Called by the orchestrator when the catalogue returned NONE (inside the attempt's workspace)."""
        attempt = self.store.latest_attempt(root.id)
        return self.run_segment(root, attempt, Path(target))

    # ================================================================ own dispatch (later segments)
    def dispatch(self, root: RootTask) -> RootTask:
        o = self.orch
        S = TaskState
        why = self.preflight(root)
        if why:
            return o._to(root, S.BLOCKED, why)
        brief, _ = self._brief(root)
        base = o.accepted_head()
        number = o.next_attempt_number(root.id)
        attempt = CandidateAttempt(id=new_id("att"), root_id=root.id, number=number, base_commit=base,
                                   provider="asset-lane", started_at=self.store.now())
        if not o.leases.acquire_all([f"attempt:{attempt.id}"], o.rt.worker_id, root_id=root.id, attempt_id=attempt.id):
            return root
        attempt.lease_holder = o.rt.worker_id
        attempt.lease_expires_at = self.store.now() + o.rt.lease_ttl_s
        attempt.heartbeat_at = self.store.now()
        self.store.create_attempt(attempt)
        root = o._to(root, S.RUNNING, f"asset lane for {brief.id} (attempt {number})", attempt_id=attempt.id)
        try:
            path, branch = o.ws.create(attempt.id, base)
        except Exception as e:
            return o._end_asset_attempt(root, attempt, S.PAUSED, f"workspace error: {e}")
        attempt.workspace_path, attempt.branch = str(path), branch
        self.store.save_attempt(attempt)
        target = Path(path) / (o.project.workdir or ".")
        try:
            res = self.run_segment(root, attempt, target)
        except DispatchStopped:
            return o._end_asset_attempt(root, attempt, S.PAUSED, "owner stop-dispatch switch is on")
        o._lane_evidence(root, attempt, res, "generation-lane")
        if res.exit_code == EXIT_PICKED:
            return self._candidate(root, attempt, res)
        if res.exit_code == EXIT_BLOCKED:
            return o._end_asset_attempt(root, attempt, S.PAUSED, f"asset lane paused: {res.reason}")
        root = o._end_asset_attempt(root, attempt, S.PAUSED, res.reason)
        return o._to(root, S.NEEDS_INPUT, res.reason)

    def _candidate(self, root: RootTask, attempt: CandidateAttempt, res: SegmentResult) -> RootTask:
        o = self.orch
        try:
            cand = o.ws.commit_candidate(attempt.workspace_path,
                                         f"Forge asset candidate {root.ticket or root.id}: {res.pick} "
                                         f"({res.gate})\n\nroot={root.id} attempt={attempt.id}")
        except Exception as e:
            cand = None
            res.reason += f" (commit failed: {e})"
        if cand is None:
            attempt.repair_instructions = f"asset lane produced no change: {res.reason}"
            return o._fail_attempt(root, attempt, FailureCategory.OTHER, "no candidate produced: " + res.reason)
        attempt.candidate_hash = cand
        attempt.status = AttemptStatus.CANDIDATE
        self.store.save_attempt(attempt)
        self.store.add_evidence(contract_evidence(root, attempt, res))
        root = o._to(root, TaskState.VERIFYING, f"asset candidate {cand[:12]} ({res.pick})", candidate_hash=cand)
        return o.verify(root, attempt)

    def recover(self, root: RootTask, attempt: CandidateAttempt) -> None:
        """Restart during a segment: jobs are durable (idempotency keys), so the segment simply pauses;
        the next dispatch re-polls recorded provider jobs instead of resubmitting."""
        pending = [j for j in self.registry.jobs(root_id=root.id) if j["status"] in ("SUBMITTING", "UNKNOWN")]
        note = "; provider completion/charge pending" if pending else ""
        self.orch._end_asset_attempt(root, attempt, TaskState.PAUSED,
                                     "recovered: asset lane was interrupted; resume to re-poll recorded jobs" + note)

    # ================================================================ the segment
    def run_segment(self, root: RootTask, attempt: CandidateAttempt, target: Path) -> SegmentResult:
        try:
            brief, sha = self._brief(root)
        except NeedsOwner as e:
            return SegmentResult(EXIT_NONE, Path(root.asset_brief or "?").stem, str(e))
        lane = self.lane_for(brief, sha)
        self.sync_gates(lane)
        if lane.brief_sha256 and lane.brief_sha256 != sha:
            return SegmentResult(EXIT_NONE, brief.id, "the production brief changed after work started: revise the "
                                                      "task (a changed requirement never silently rewrites approved "
                                                      "work)")
        stages = segment(lane)
        if not stages:
            return SegmentResult(EXIT_NONE, brief.id, f"asset {brief.id} v{lane.version} has no stage left to run")
        ctx = _Ctx(self, root, attempt, brief, lane, target)
        done: list[str] = []
        try:
            for st in stages:
                getattr(self, f"_stage_{st.value}")(ctx, st)
                done.append(st.value)
                self.save(lane)
        except NeedsOwner as e:
            self.save(lane)
            return ctx.result(EXIT_NONE, str(e), done)
        except StageBlocked as e:
            self.save(lane)
            return ctx.result(EXIT_BLOCKED, str(e), done)
        except LaneError as e:
            self.save(lane)
            return ctx.result(EXIT_BLOCKED, f"lane error: {e}", done)
        gate = stages[-1] if stages and stages[-1] in GATES else None
        ctx.publish()
        reason = (f"{brief.id} v{lane.version}: {gate.value.replace('_', ' ')} awaiting the owner" if gate
                  else f"{brief.id}: stages {', '.join(done)} complete")
        return ctx.result(EXIT_PICKED, reason, done, gate=gate)

    # ---------------------------------------------------------------- helpers
    def route_context(self, brief: AssetBrief) -> RouteContext:
        pol = self.orch.project.policy
        return RouteContext(
            distribution_countries=list(pol.intended_release_countries),
            public_demo=self.cfg.public_demo or brief.licence.public_demo,
            gpus=[s for w in self.registry.workers() for s in w.slots()], permissions=self.registry.permissions(),
            licence_gates=self.registry.licence_gates(), benchmarks=self.registry.benchmarks(),
            certified=self.registry.certified(), configured=set(self.adapters), unattended=self.orch.rt.unattended,
            allowed_vendors=list(pol.allowed_vendors), allowed_data_classes=list(pol.allowed_data_classes),
            allowed_regions=list(pol.allowed_regions))

    def preferences(self, root: RootTask, kind: AssetLaneKind) -> list[str]:
        mine = [r for r in root.permitted_routes if r in ROUTES and kind in ROUTES[r].lanes]
        return mine or list(self.cfg.preferences.get(kind.value, []))

    def choose(self, ctx: "_Ctx", kind: AssetLaneKind) -> tuple[RouteDecision, GenerationAdapter]:
        prefs = self.preferences(ctx.root, kind)
        chosen = ctx.lane.routes.get(kind.value)
        if chosen:  # keep the route of this asset version (switching creates a replacement version)
            prefs = [chosen]
        stages = {r: self.cfg.routes.get(r, {}).get("stage") for r in prefs}
        try:
            d = select_route(kind, prefs, self.route_context(ctx.brief), stages=stages)
        except RouteRefused as e:
            raise NeedsOwner(redact(str(e))[:900]) from e
        adapter = self.adapters.get(d.route)
        if adapter is None:
            raise NeedsOwner(f"route {d.route} has no configured adapter")
        ok, why = adapter.available()
        if not ok:
            raise StageBlocked(why)
        ctx.lane.routes[kind.value] = d.route
        return d, adapter

    def run_provider_job(self, ctx: "_Ctx", decision: RouteDecision, adapter: GenerationAdapter,
                         job: GenerationJob) -> Any:
        o = self.orch
        res_id = None
        route = ROUTES[decision.route]
        ceiling = adapter.estimate_ceiling_micros(job)
        if ceiling is None or ceiling > 0:
            try:
                res_id = o.budget.reserve(project_id=ctx.root.project_id, milestone_id=ctx.root.milestone_id,
                                          root_id=ctx.root.id, amount_micros=ceiling, attempt_id=ctx.attempt.id,
                                          provider=decision.route, purpose=f"{ctx.brief.id} {job.stage} via "
                                                                           f"{decision.route}",
                                          unattended=o.rt.unattended)
            except UnknownCeiling as e:
                raise StageBlocked(f"{decision.route}: {e} (configure credit prices for this route)") from e
            except (BudgetExceeded, NoCapConfigured) as e:
                raise StageBlocked(f"budget: {e}") from e
        lease = None
        if route.kind == "local_model" and decision.gpu is not None:
            req = route.stage(job.stage if route.stage(job.stage) else None)
            need = req.vram_gb if req and req.vram_gb is not None else None
            try:
                lease = self.gpu.acquire(job.idempotency_key, o.rt.worker_id, need, gpu_model=decision.gpu.gpu_model,
                                         worker_id=decision.gpu.worker_id, root_id=ctx.root.id)
            except GpuUnavailable as e:
                if res_id:
                    o.budget.release(res_id)
                raise StageBlocked(f"waiting for a GPU: {e}") from e
            job.gpu_uuid = lease.gpu.uuid

        def on_cost(cost: int, usage: dict) -> None:
            if res_id and cost:
                o.budget.record_usage(res_id, cost, model=decision.route, usage=usage)
            ctx.cost += cost or 0

        deadline = ctx.attempt.started_at + ctx.root.active_work_timeout_s
        prior = self.registry.job(job.idempotency_key)
        already_paid = bool(prior and prior["status"] == "SUCCEEDED")  # charged when it first completed
        try:
            result = run_job(adapter, job, registry=self.registry, root_id=ctx.root.id, reservation_id=res_id,
                             sleep=self.store.clock.sleep, now=self.store.now, deadline=deadline,
                             poll_interval_s=self.cfg.poll_interval_s, on_cost=on_cost)
        except ManualStepPending as e:
            if res_id:
                o.budget.release(res_id)
            raise StageBlocked(f"manual step pending: {e}") from e
        except (UncertainSubmission, JobTimeout) as e:
            if res_id:
                o.budget.mark_charge_pending(res_id, str(e))
            raise StageBlocked(f"{e}; provider completion/charge pending") from e
        except AdapterUnavailable as e:
            if res_id:
                o.budget.release(res_id)
            raise StageBlocked(str(e)) from e
        except AdapterError as e:
            ctx.failed_jobs += 1
            if res_id:
                o.budget.settle(res_id)
            raise StageBlocked(redact(f"{decision.route} job failed: {e}")) from e
        except Exception as e:  # transport problems etc.: the outcome is unknown
            if res_id:
                o.budget.mark_charge_pending(res_id, redact(str(e)))
            raise StageBlocked(redact(f"{decision.route}: {type(e).__name__}: {e}; provider completion/charge "
                                      "pending")) from e
        finally:
            if lease is not None:
                self.gpu.release(lease)
        if result.status.cost_micros is None and ceiling and not already_paid:
            # the provider reports no per-job cost: charge the reserved ceiling (flagged as an estimate) rather
            # than recording a paid job as free; the cash ledger reconciles against the invoice later
            on_cost(ceiling, {"estimated_from_ceiling": True})
        if res_id:
            o.budget.settle(res_id)
        for p in result.files.values():
            o.artifacts.put_file(p)  # raw provider outputs are kept (content-addressed), not committed
        return result

    def job_key(self, lane: AssetLane, stage: Stage, route: str, op: str, extra: str = "") -> str:
        r = lane.rec(stage)
        n = sum(1 for h in r.history if h.get("event") in ("failed",)) + len(lane.corrections(_gate_for(stage)))
        return f"{lane.asset_id}:v{lane.version}:{stage.value}:{route}:{op}{':' + extra if extra else ''}:{n}"

    def provenance(self, ctx: "_Ctx", stage: Stage, route: str, *, inputs: list[InputReference],
                   transformations: list[Transformation], files: dict[str, str], job_ids: list[str],
                   model: str = "", model_version: str = "", attribution: str = "") -> ProvenanceRecord:
        r = ROUTES.get(route)
        gate = self.registry.licence_gates().get(route)
        terms = [TermsRef(name=gate.terms_name, version=gate.terms_version, accepted_by=gate.accepted_by,
                          accepted_at=gate.accepted_at)] if gate else []
        failed = sum(1 for j in self.registry.jobs(asset_id=ctx.lane.asset_id) if j["stage"] == stage.value and
                     j["status"] in ("FAILED", "CANCELLED", "UNKNOWN"))
        rec = ProvenanceRecord(
            asset_id=ctx.lane.asset_id, version=ctx.lane.version, stage=stage.value, inputs=inputs, route=route,
            model=model or route, model_version=model_version,
            service=(r.data_destinations[0] if r else route), generated_at=self.store.now(), terms=terms,
            transformations=transformations,
            attribution=attribution,
            territory=TerritoryFlags(
                distribution_countries=list(self.orch.project.policy.intended_release_countries),
                excluded_territories=list(r.licence.excluded_territories) if r else [],
                public_demo_allowed=bool(gate.public_demo_allowed) if gate else False,
                restricted=bool(r and r.licence.excluded_territories)),
            final_hashes=files, provider_job_ids=job_ids, cost_micros=ctx.cost, failed_attempts=failed)
        self.registry.put_provenance(rec)
        ctx.provenance_problems += validate_provenance(rec, third_party=bool(r and r.kind != "library") or
                                                       bool(terms))
        return rec

    # ================================================================ stages
    def _stage_brief(self, ctx: "_Ctx", st: Stage) -> None:
        dest = ctx.mirror / "brief.json"
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(ctx.brief.model_dump_json(indent=2) + "\n")
        ctx.lane.complete(st, {ctx.rel("brief.json"): sha256_file(dest)}, at=self.store.now(),
                          report={"budget": asdict(ctx.brief.asset_budget()),
                                  "separate_assets": ctx.brief.separate_assets,
                                  "procedural_parts": ctx.brief.procedural_parts})

    def _prompt(self, ctx: "_Ctx", base: str, gate: Stage) -> str:
        corr = ctx.lane.corrections(gate)
        extra = f"\nOwner corrections: {'; '.join(corr)}" if corr else ""
        return base + extra

    def _stage_concept(self, ctx: "_Ctx", st: Stage) -> None:
        d, adapter = self.choose(ctx, AssetLaneKind.CONCEPT_IMAGE)
        job = GenerationJob(self.job_key(ctx.lane, st, d.route, "concept_image"), ctx.lane.asset_id, d.route,
                            self.cfg.routes.get(d.route, {}).get("stage", "default"), "concept_image",
                            inputs={"prompt": self._prompt(ctx, ctx.brief.prompt, Stage.CONCEPT_APPROVAL)},
                            params={"width": 1024, "height": 1024}, out_dir=ctx.work / "concept")
        res = self.run_provider_job(ctx, d, adapter, job)
        out = {}
        for name, p in sorted(res.files.items()):
            dest = ctx.mirror / "concept" / name
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(p, dest)
            out[ctx.rel(f"concept/{name}")] = sha256_file(dest)
        self.provenance(ctx, st, d.route, inputs=[InputReference(kind="prompt", description=ctx.brief.prompt[:200],
                                                                 rights="owner-authored brief")] + ctx.art_refs(),
                        transformations=[Transformation(step="concept_image", tool=d.route, at=self.store.now(),
                                                        output_sha256=list(out.values()))],
                        files=out, job_ids=[res.provider_job_id], model=res.model, model_version=res.model_version)
        ctx.lane.complete(st, out, route=d.route, at=self.store.now())

    def _gate(self, ctx: "_Ctx", st: Stage, payload: dict) -> None:
        upstream = {}
        for s in list(Stage)[:list(Stage).index(st)]:
            upstream.update(ctx.lane.rec(s).outputs)
        repo_files = {k: v for k, v in upstream.items() if k.startswith(ctx.asset_rel + "/")
                      or k.startswith(self.unity_rel(ctx.lane.asset_id) + "/")}
        review = ctx.mirror / "review" / f"{st.value}.json"
        review.parent.mkdir(parents=True, exist_ok=True)
        review.write_text(json.dumps(payload, indent=2, sort_keys=True, default=str) + "\n")
        out = {ctx.rel(f"review/{st.value}.json"): sha256_file(review)}
        repo_files.update(out)
        ctx.lane.complete(st, out, at=self.store.now(),
                          report={"repo_files": repo_files, "attempt_id": ctx.attempt.id, **payload})
        rec = ctx.lane.rec(st)
        rec.root_id = ctx.root.id
        ctx.gate_payload = payload

    def _stage_concept_approval(self, ctx: "_Ctx", st: Stage) -> None:
        imgs = [k for k in ctx.lane.rec(Stage.CONCEPT).outputs if k.endswith(".png")]
        ctx.review_images(after={Path(k).stem: ctx.repo_path_to_mirror(k) for k in imgs})
        self._gate(ctx, st, {"gate": st.value, "concepts": imgs, "brief": ctx.brief.model_dump(mode="json"),
                             "provenance": ctx.prov_summary(Stage.CONCEPT)})

    def _stage_generation(self, ctx: "_Ctx", st: Stage) -> None:
        d, adapter = self.choose(ctx, AssetLaneKind.MESH)
        concept = sorted(k for k in ctx.lane.rec(Stage.CONCEPT).outputs if k.endswith(".png"))
        budget = ctx.brief.asset_budget()
        stage_profile = self.cfg.routes.get(d.route, {}).get("stage") or (ROUTES[d.route].stages[0].stage
                                                                         if ROUTES[d.route].stages else "default")
        op = "image_to_3d" if concept else "text_to_3d"
        inputs: dict[str, Any] = {"prompt": self._prompt(ctx, ctx.brief.prompt, Stage.VISUAL_APPROVAL)}
        if concept:
            inputs["image"] = ctx.repo_path_to_mirror(concept[0])
        job = GenerationJob(self.job_key(ctx.lane, st, d.route, op), ctx.lane.asset_id, d.route, stage_profile, op,
                            inputs=inputs, params={"target_polycount": budget.max_triangles or 10_000,
                                                   "negative_prompt": ctx.brief.negative_prompt},
                            out_dir=ctx.work / "generation")
        res = self.run_provider_job(ctx, d, adapter, job)
        raw = {f"raw/{n}": h for n, h in res.hashes.items()}
        model = next((res.files[n] for n in sorted(res.files) if n.endswith(".glb")), None) or \
            next((res.files[n] for n in sorted(res.files) if n.endswith((".fbx", ".obj"))), None)
        if model is None:
            ctx.lane.fail(st, "provider returned no mesh", at=self.store.now())
            raise StageBlocked(f"{d.route} returned no GLB/FBX/OBJ (got {sorted(res.files)})")
        ctx.state["raw_model"] = str(model)
        ins = [InputReference(kind="prompt", description=inputs["prompt"][:200], rights="owner-authored brief")]
        if concept:
            ins.append(InputReference(kind="concept_image", description=concept[0], sha256=ctx.lane.rec(
                Stage.CONCEPT).outputs[concept[0]], rights="generated for this project; approved at concept gate"))
        ins += ctx.art_refs()
        self.provenance(ctx, st, d.route, inputs=ins,
                        transformations=[Transformation(step="generation", tool=d.route, at=self.store.now(),
                                                        params={"operation": op, "stage_profile": stage_profile,
                                                                "qualification": d.qualification},
                                                        output_sha256=list(res.hashes.values()))],
                        files=raw, job_ids=[res.provider_job_id], model=res.model, model_version=res.model_version)
        prev_topology = (ctx.lane.rec(Stage.NORMALIZATION).report or {}).get("topology_hash")
        ctx.lane.complete(st, raw, route=d.route, at=self.store.now(),
                          report={"raw_model": str(model), "provider_job_id": res.provider_job_id,
                                  "qualification": d.qualification, "gpu": d.gpu.__dict__ if d.gpu else None,
                                  "previous_topology": prev_topology})

    def _raw_model(self, ctx: "_Ctx") -> Path:
        p = ctx.state.get("raw_model") or (ctx.lane.rec(Stage.GENERATION).report or {}).get("raw_model")
        if not p or not Path(p).exists():
            ctx.lane.rec(Stage.GENERATION).status = StageStatus.PENDING
            raise StageBlocked("raw provider output is missing from the lane work directory; generation re-runs "
                               "on resume")
        return Path(p)

    def _stage_normalization(self, ctx: "_Ctx", st: Stage) -> None:
        src = self._raw_model(ctx)
        b = ctx.brief
        budget = b.asset_budget()
        try:
            run = self.blender.normalize(src, ctx.mirror, asset_id=b.id, size_m=b.scale_m, scale_axis=b.scale_axis,
                                         pivot=b.pivot, max_tris=budget.max_triangles or 10_000,
                                         max_texture=budget.max_texture_px, attachments=b.attachment_points,
                                         auto_attachments=[] if b.rigged else b.attachment_points)
        except ToolMissing as e:
            raise StageBlocked(str(e)) from e
        except BlenderFailed as e:
            ctx.lane.fail(st, str(e), at=self.store.now())
            raise StageBlocked(str(e)) from e
        try:
            before = self.blender.render_source(src, ctx.work / "before")
            ctx.state["before"] = {k: str(v) for k, v in before.renders.items()}
        except (ToolMissing, BlenderFailed) as e:
            ctx.state["before"] = {}
            ctx.notes.append(f"source preview renders unavailable: {e}")
        out = ctx.collect(["*.fbx", "textures/*", "renders/*", "technical_report.json"])
        rep = run.report
        prev = (ctx.lane.rec(st).report or {}).get("topology_hash")
        if prev and rep.get("topology_hash") and prev != rep["topology_hash"]:
            hit = ctx.lane.record_change(ChangeKind.TOPOLOGY, at=self.store.now(), reason="regenerated mesh")
            ctx.notes.append(f"topology changed: invalidated {[s.value for s in hit]}")
        self.provenance(ctx, st, "blender", inputs=[InputReference(kind="base_mesh", description=src.name,
                                                                   sha256=sha256_file(src),
                                                                   rights="output of the generation stage")],
                        transformations=[Transformation(step="blender_normalize", tool="blender",
                                                        tool_version=rep.get("blender_version", ""),
                                                        at=self.store.now(),
                                                        params={"size_m": b.scale_m, "pivot": b.pivot,
                                                                "max_tris": budget.max_triangles,
                                                                "decimations": rep.get("decimations", 0)},
                                                        input_sha256=[sha256_file(src)],
                                                        output_sha256=list(out.values()))],
                        files=out, job_ids=[])
        ctx.lane.complete(st, out, route="blender", at=self.store.now(), report=rep)

    def _stage_technical_report(self, ctx: "_Ctx", st: Stage) -> None:
        rep = ctx.lane.rec(Stage.NORMALIZATION).report
        b = ctx.brief
        budget = b.asset_budget()
        geo = geometry_from_report(rep, collider=b.collider)
        mats = materials_from_report(rep)
        # rigged characters get their sockets as skeleton joints at the rig stage (checked there)
        problems = validate_geometry(geo, size_m=b.scale_m if b.scale_axis == "largest" else None,
                                     max_triangles=budget.max_triangles,
                                     required_attachments=[] if b.rigged else b.attachment_points,
                                     textured=bool(rep.get("textures")))
        if b.scale_axis == "height":
            h = geo.bounds_size_m[1] if len(geo.bounds_size_m) == 3 else 0
            if abs(h - b.scale_m) > b.scale_m * 0.05:
                problems.append(f"height {h:.3f} m is not {b.scale_m} m (+/-5%)")
        problems += validate_materials(mats, max_materials=budget.max_materials, max_texture_px=budget.max_texture_px)
        lines = [line.to_dict() for line in check_asset(rep, budget)]
        problems += [f"budget: {line['metric']} {line['value']} > {line['ceiling']}" for line in lines
                     if not line["ok"]]
        for p in b.procedural_parts:
            if any(p in (m.get("name") or "").lower() for m in rep.get("materials") or []):
                problems.append(f"{p} must stay procedural (found in the normalized asset)")
        contract = {"geometry": json.loads(geo.model_dump_json()), "materials": json.loads(mats.model_dump_json()),
                    "budget": lines, "problems": problems, "notes": rep.get("notes", []) + ctx.notes}
        dest = ctx.mirror / "contract_report.json"
        dest.write_text(json.dumps(contract, indent=2, sort_keys=True) + "\n")
        out = {ctx.rel("contract_report.json"): sha256_file(dest)}
        ctx.state["budget_lines"] = lines
        if problems:
            ctx.lane.fail(st, "; ".join(problems)[:500], at=self.store.now(), report=contract)
            raise StageBlocked("asset contract not met (incompatibility report in contract_report.json): "
                               + "; ".join(problems)[:600])
        ctx.lane.complete(st, out, at=self.store.now(), report=contract)

    def _stage_visual_approval(self, ctx: "_Ctx", st: Stage) -> None:
        renders = {Path(k).stem: ctx.repo_path_to_mirror(k) for k in ctx.lane.rec(Stage.NORMALIZATION).outputs
                   if "/renders/" in k and k.endswith(".png")}
        after = {v: renders[v] for v in VIEWS if v in renders}
        ctx.review_images(after=after, before={k: Path(v) for k, v in (ctx.state.get("before") or {}).items()})
        tr = ctx.lane.rec(Stage.TECHNICAL_REPORT).report
        self._gate(ctx, st, {"gate": st.value, "budget": tr.get("budget", ctx.state.get("budget_lines", [])),
                             "contract_problems": tr.get("problems", []),
                             "turntable": sorted(k for k in renders if "turntable" in k),
                             "provenance": ctx.prov_summary(Stage.GENERATION),
                             "route": ctx.lane.routes.get(AssetLaneKind.MESH.value),
                             "check": ["silhouette at the gameplay camera", "proportions / neutral pose",
                                       "materials and texture readability", "no recognisable franchise/brand look"]})

    def _mapping_for(self, route: str, ctx: "_Ctx") -> tuple[dict, Path]:
        spec = self.cfg.routes.get(route, {})
        if spec.get("mapping"):
            p = Path(spec["mapping"]).expanduser()
            if not p.is_absolute():
                p = self.orch.rt.repo_path / p
            mapping = json.loads(p.read_text())
        elif route in DEFAULT_MAPPINGS:
            mapping = builtin_mapping(DEFAULT_MAPPINGS[route])
        else:
            raise NeedsOwner(f"no retarget mapping for route {route}: set [assets.routes.{route}] mapping to a "
                             "forge-retarget/1 file")
        problems = validate_mapping(mapping, joints=ARCHER_JOINTS)
        if problems:
            raise NeedsOwner(f"retarget mapping for {route} invalid: {'; '.join(problems)[:400]}")
        dest = ctx.work / f"mapping_{route}.json"
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(json.dumps(mapping, indent=2, sort_keys=True))
        return mapping, dest

    def _stage_rig(self, ctx: "_Ctx", st: Stage) -> None:
        d, adapter = self.choose(ctx, AssetLaneKind.RIG)
        fbx = next((k for k in ctx.lane.rec(Stage.NORMALIZATION).outputs if k.endswith(".fbx")), None)
        if fbx is None:
            raise StageBlocked("no normalized FBX to rig")
        mesh = ctx.repo_path_to_mirror(fbx)
        job = GenerationJob(self.job_key(ctx.lane, st, d.route, "rig"), ctx.lane.asset_id, d.route,
                            self.cfg.routes.get(d.route, {}).get("stage", "default"), "rig",
                            inputs={"model": mesh}, params={"height_m": ctx.brief.scale_m,
                                                            "expected_files": [f"{ctx.lane.asset_id}_rigged.fbx"]},
                            out_dir=ctx.work / "rig_raw")
        res = self.run_provider_job(ctx, d, adapter, job)
        rigged = next((p for n, p in sorted(res.files.items()) if n.endswith(".fbx")), None) or \
            next((p for n, p in sorted(res.files.items()) if n.endswith(".glb")), None)
        if rigged is None:
            raise StageBlocked(f"{d.route} returned no rigged FBX/GLB")
        mapping, mpath = self._mapping_for(d.route, ctx)
        budget = ctx.brief.asset_budget()
        try:
            run = self.blender.rig(rigged, mpath, ctx.mirror / "rig", asset_id=ctx.lane.asset_id,
                                   max_weights=budget.max_weights_per_vertex or 4)
        except ToolMissing as e:
            raise StageBlocked(str(e)) from e
        except BlenderFailed as e:
            ctx.lane.fail(st, str(e), at=self.store.now())
            raise StageBlocked(str(e)) from e
        shutil.copy2(mpath, ctx.mirror / "rig" / "retarget_mapping.json")
        msha = mapping_sha256(mapping)
        sk = skeleton_from_report(run.report, mapping_sha256=msha)
        problems = validate_skeleton(sk, reference=ARCHER_JOINTS if ctx.brief.budget == "archer" else None,
                                     max_deforming=budget.max_deforming_bones,
                                     max_weights=budget.max_weights_per_vertex, require_mapping=True)
        out = ctx.collect(["rig/*.fbx", "rig/deform/*", "rig/rig_report.json", "rig/retarget_mapping.json"])
        prev = (ctx.lane.rec(st).report or {}).get("skeleton_hash")
        if prev and prev != sk.skeleton_hash:
            hit = ctx.lane.record_change(ChangeKind.SKELETON, at=self.store.now(), reason="new rig")
            ctx.notes.append(f"skeleton changed: invalidated {[s.value for s in hit]}")
        report = {**run.report, "skeleton_hash": sk.skeleton_hash, "mapping_sha256": msha,
                  "skeleton_problems": problems}
        self.provenance(ctx, st, d.route, inputs=[InputReference(kind="base_mesh", description=fbx,
                                                                 sha256=sha256_file(mesh),
                                                                 rights="approved at visual approval")],
                        transformations=[Transformation(step="rig", tool=d.route, at=self.store.now(),
                                                        manual=ROUTES[d.route].manual_step),
                                         Transformation(step="rig_normalize", tool="blender",
                                                        tool_version=run.report.get("blender_version", ""),
                                                        at=self.store.now(), params={"mapping_sha256": msha})],
                        files=out, job_ids=[res.provider_job_id], model=res.model)
        if problems:
            ctx.lane.fail(st, "; ".join(problems)[:500], at=self.store.now(), report=report)
            raise StageBlocked("skeleton contract not met: " + "; ".join(problems)[:600])
        ctx.lane.complete(st, out, route=d.route, at=self.store.now(), report=report)

    def _stage_deformation_review(self, ctx: "_Ctx", st: Stage) -> None:
        rig = ctx.lane.rec(Stage.RIG)
        deform = {Path(k).stem: ctx.repo_path_to_mirror(k) for k in rig.outputs if "/deform/" in k and
                  k.endswith(".png")}
        ctx.review_images(after=deform)
        self._gate(ctx, st, {"gate": st.value, "skeleton_hash": rig.report.get("skeleton_hash"),
                             "mapping_sha256": rig.report.get("mapping_sha256"),
                             "deforming_bones": rig.report.get("deforming_bones"),
                             "max_weights_per_vertex": rig.report.get("max_weights_per_vertex"),
                             "notes": rig.report.get("notes", []),
                             "check": ["hands and grip", "shoulders and elbows in full draw", "feet and knees in "
                                       "crouch", "bow penetration", "retarget mapping (approved by this gate)"],
                             "provenance": ctx.prov_summary(Stage.RIG)})

    def _stage_motion_retarget(self, ctx: "_Ctx", st: Stage) -> None:
        clips = ctx.brief.animation_requirements
        if not clips:
            raise NeedsOwner("brief lists no animation requirements for the motion stage")
        d, adapter = self.choose(ctx, AssetLaneKind.MOTION)
        mapping, mpath = self._mapping_for(d.route, ctx)
        rig_fbx = next((k for k in ctx.lane.rec(Stage.RIG).outputs if k.endswith("_rigged.fbx")), None)
        if rig_fbx is None:
            raise StageBlocked("no rigged FBX from the rig stage")
        sk_hash = ctx.lane.rec(Stage.RIG).report.get("skeleton_hash", "")
        checks, job_ids = {}, []
        for clip in clips:
            spec = ARCHER_CLIPS.get(clip)
            prompt = ctx.brief.clip_prompts.get(clip) or f"{ctx.brief.title}: {clip.replace('_', ' ')}"
            job = GenerationJob(self.job_key(ctx.lane, st, d.route, "motion", clip), ctx.lane.asset_id, d.route,
                                self.cfg.routes.get(d.route, {}).get("stage", "default"),
                                "library_import" if ROUTES[d.route].kind == "library" else "text_to_motion",
                                inputs={"prompt": self._prompt(ctx, prompt, Stage.CONTACT_REVIEW), "clip": clip},
                                params={"purpose": clip, "tags": [clip], "fps": self.cfg.fps,
                                        "duration_s": ((spec.min_s + spec.max_s) / 2) if spec else 1.0,
                                        "expected_files": [f"{clip}.fbx"]},
                                out_dir=ctx.work / "motion_raw" / clip)
            res = self.run_provider_job(ctx, d, adapter, job)
            job_ids.append(res.provider_job_id)
            src = next((p for n, p in sorted(res.files.items()) if n.endswith((".fbx", ".bvh"))), None)
            if src is None:
                raise StageBlocked(f"{d.route} returned no clip file for {clip}")
            cleanup = needs_cleanup(d.route, clip)
            try:
                run = self.blender.motion(src, ctx.repo_path_to_mirror(rig_fbx), mpath, ctx.mirror / "clips",
                                          clip=clip, fps=self.cfg.fps, loop_cleanup="loop_cleanup" in cleanup,
                                          root_motion_conversion="root_motion_conversion" in cleanup)
            except ToolMissing as e:
                raise StageBlocked(str(e)) from e
            except BlenderFailed as e:
                ctx.lane.fail(st, f"{clip}: {e}", at=self.store.now())
                raise StageBlocked(f"{clip}: {e}") from e
            mc = motion_from_report(run.report, route=d.route, skeleton_hash_=sk_hash)
            chk = check_clip(mc)
            checks[clip] = {"ok": chk.ok, "problems": chk.problems, "markers_proposed":
                            run.report.get("markers_proposed", False), "contract": json.loads(mc.model_dump_json())}
        out = ctx.collect(["clips/*.fbx", "clips/*.json", "clips/*_sheet/*"])
        summary = ctx.mirror / "clips" / "clip_checks.json"
        summary.write_text(json.dumps(checks, indent=2, sort_keys=True) + "\n")
        out[ctx.rel("clips/clip_checks.json")] = sha256_file(summary)
        self.provenance(ctx, st, d.route, inputs=[InputReference(kind="prompt", description=f"clips {clips}",
                                                                 rights="owner-authored brief"),
                                                  InputReference(kind="retarget_mapping", description=mpath.name,
                                                                 sha256=mapping_sha256(mapping),
                                                                 rights="Forge mapping file")],
                        transformations=[Transformation(step="motion_generation", tool=d.route, at=self.store.now(),
                                                        manual=ROUTES[d.route].manual_step),
                                         Transformation(step="retarget+cleanup", tool="blender", at=self.store.now(),
                                                        params={"cleanup": sorted({c for clip in clips for c in
                                                                                   needs_cleanup(d.route, clip)})})],
                        files=out, job_ids=job_ids)
        failing = {c: v["problems"] for c, v in checks.items() if not v["ok"]}
        if failing:
            ctx.lane.fail(st, json.dumps(failing)[:500], at=self.store.now(), report={"clips": checks})
            raise StageBlocked("motion clip checks failed: " + "; ".join(f"{c}: {', '.join(p)}"
                                                                       for c, p in failing.items())[:700])
        ctx.lane.complete(st, out, route=d.route, at=self.store.now(), report={"clips": checks,
                                                                                "skeleton_hash": sk_hash})

    def _stage_contact_transition_review(self, ctx: "_Ctx", st: Stage) -> None:
        mot = ctx.lane.rec(Stage.MOTION)
        sheets = {Path(k).parent.name.replace("_sheet", "") + "_" + Path(k).stem: ctx.repo_path_to_mirror(k)
                  for k in mot.outputs if "_sheet/" in k and k.endswith(".png")}
        ctx.review_images(after=dict(sorted(sheets.items())[:24]))
        self._gate(ctx, st, {"gate": st.value, "clips": {c: {"problems": v["problems"],
                                                             "markers_proposed": v["markers_proposed"],
                                                             "events": v["contract"]["events"],
                                                             "contacts": v["contract"]["contacts"],
                                                             "transitions": v["contract"]["transitions"]}
                                                         for c, v in (mot.report.get("clips") or {}).items()},
                             "check": ["hands, shoulders, elbows, feet in motion", "grip and bow penetration",
                                       "root drift", "recovery transitions", "release marker timing"],
                             "provenance": ctx.prov_summary(Stage.MOTION)})

    def _stage_unity_prefab(self, ctx: "_Ctx", st: Stage) -> None:
        aid = ctx.lane.asset_id
        unity_root = ctx.target / self.cfg.unity_project_dir
        art = unity_root / self.cfg.unity_art_root / aid
        art.mkdir(parents=True, exist_ok=True)
        ctx.publish()  # Unity imports from the workspace; the mirror keeps the result across attempts
        norm = ctx.lane.rec(Stage.NORMALIZATION)
        rig = ctx.lane.rec(Stage.RIG)
        fbx_key = next((k for k in rig.outputs if k.endswith("_rigged.fbx")), None) or \
            next((k for k in norm.outputs if k.endswith(".fbx")), None)
        if fbx_key is None:
            raise StageBlocked("no FBX to assemble")
        rel_art = f"{self.cfg.unity_art_root}/{aid}"
        shutil.copy2(ctx.repo_path_to_mirror(fbx_key), art / Path(fbx_key).name)
        textures, clips = [], []
        for t in norm.report.get("textures") or []:
            src = ctx.mirror / t["file"]
            if src.exists():
                shutil.copy2(src, art / src.name)
                textures.append(TextureImport(f"{rel_art}/{src.name}", t.get("role", "base_color"),
                                              (t.get("colour_space") or "sRGB") == "sRGB",
                                              int(max(t.get("width", 0), t.get("height", 0))) or 1024,
                                              t.get("compression") or "ASTC_6x6"))
        for clip, v in ((ctx.lane.rec(Stage.MOTION).report or {}).get("clips") or {}).items():
            src = ctx.mirror / "clips" / f"{clip}.fbx"
            if src.exists():
                shutil.copy2(src, art / src.name)
                c = v["contract"]
                clips.append(ClipImport(clip, f"{rel_art}/{src.name}", c["loop_policy"] == "loop",
                                        c["root_motion"] == "root_motion",
                                        [{"name": e["name"], "time_s": e["time_s"], "function": "OnForgeEvent"}
                                         for e in c["events"]]))
        materials = [{"name": m["name"], "shader": m.get("unity_shader", "Universal Render Pipeline/Lit"),
                      "surface": m.get("surface", "opaque"), "maps": m.get("textures", [])}
                     for m in norm.report.get("materials") or []]
        manifest = PrefabManifest(aid, ctx.lane.version, ctx.brief.kind, f"{rel_art}/{Path(fbx_key).name}",
                                  f"{rel_art}/{aid}.prefab", textures, materials,
                                  {"animation_type": "Generic", "root": "root"} if rig.outputs else None, clips,
                                  norm.report.get("attachments") or [], ctx.brief.collider)
        mpath = art / "forge_prefab_manifest.json"
        mpath.write_text(manifest.to_json() + "\n")
        result = self.unity.run(unity_root, mpath, ctx.work / "prefab_result.json")
        if result.status != "PASS":
            ctx.lane.fail(st, result.reason, at=self.store.now(), report={"manifest": json.loads(manifest.to_json())})
            raise StageBlocked(result.reason)
        out = {}
        if ctx.unity_mirror.exists():
            shutil.rmtree(ctx.unity_mirror)
        shutil.copytree(art, ctx.unity_mirror)  # prefab and Unity .meta files included
        for p in sorted(art.rglob("*")):
            if p.is_file():
                out[f"{self.unity_rel(aid)}/{p.relative_to(art).as_posix()}"] = sha256_file(p)
        ctx.lane.complete(st, out, route="unity", at=self.store.now(),
                          report={"manifest": json.loads(manifest.to_json()), "result": result.details})

    def _stage_device_evidence(self, ctx: "_Ctx", st: Stage) -> None:
        names = self.cfg.device_checks
        if not names:
            raise NeedsOwner("no device evidence checks configured ([assets] device_checks)")
        o = self.orch
        ctx.publish()  # the build must contain the asset and its prefab
        workdir = Path(ctx.attempt.workspace_path or ctx.target)
        results = o._run_checks(ctx.root, ctx.attempt, names, workdir, None)
        bad = [oc for oc, _ in results if oc.status != EvidenceStatus.PASS]
        if bad:
            raise StageBlocked("device evidence incomplete: " + "; ".join(f"{oc.name}: {oc.status.value} - "
                                                                          f"{oc.summary}" for oc in bad)[:600])
        self._gate(ctx, st, {"gate": st.value, "device_evidence": [ev.id for _, ev in results],
                             "check": ["approve in the running phone build: two equipped characters deform and "
                                       "animate correctly in the representative combat scene"]})


def _gate_for(stage: Stage) -> Stage:
    from .lane import PRODUCERS

    for gate, producers in PRODUCERS.items():
        if stage in producers or stage == gate:
            return gate
    return Stage.VISUAL_APPROVAL


def _git_bytes(repo: Path, commit: str, path: str) -> Optional[bytes]:
    import subprocess

    r = subprocess.run(["git", "show", f"{commit}:{path}"], cwd=str(repo), capture_output=True)
    return r.stdout if r.returncode == 0 else None


class _Ctx:
    """Per-segment working state."""

    def __init__(self, gl: GenerationLane, root: RootTask, attempt: CandidateAttempt, brief: AssetBrief,
                 lane: AssetLane, target: Path):
        self.gl, self.root, self.attempt, self.brief, self.lane, self.target = gl, root, attempt, brief, lane, target
        self.work = gl.work(lane)
        self.mirror = gl.mirror(lane)
        self.unity_mirror = self.work / "unity_art"
        self.mirror.mkdir(parents=True, exist_ok=True)
        self.asset_rel = gl.asset_rel(lane.asset_id)
        self.cost = 0
        self.failed_jobs = 0
        self.notes: list[str] = []
        self.state: dict[str, Any] = {}
        self.provenance_problems: list[str] = []
        self.images: dict[str, dict[str, str]] = {"before": {}, "after": {}}
        self.gate_payload: dict = {}

    def rel(self, name: str) -> str:
        return f"{self.asset_rel}/{name}"

    def repo_path_to_mirror(self, repo_path: str) -> Path:
        return self.mirror / repo_path[len(self.asset_rel) + 1:]

    def collect(self, patterns: list[str]) -> dict[str, str]:
        out = {}
        for pat in patterns:
            for p in sorted(self.mirror.glob(pat)):
                if p.is_file():
                    out[self.rel(p.relative_to(self.mirror).as_posix())] = sha256_file(p)
        return out

    def art_refs(self) -> list[InputReference]:
        return [InputReference(kind="reference_image", description=a.description, uri=a.uri or a.path,
                               rights=a.rights) for a in self.brief.art_reference]

    def prov_summary(self, stage: Stage) -> dict:
        recs = [r for r in self.gl.registry.provenance(self.lane.asset_id) if r.version == self.lane.version and
                r.stage == stage.value]
        if not recs:
            return {}
        r = recs[-1]
        return {"route": r.route, "model": r.model, "model_version": r.model_version, "service": r.service,
                "terms": [t.name + (f" ({t.version})" if t.version else "") for t in r.terms],
                "excluded_territories": r.territory.excluded_territories,
                "distribution": r.territory.distribution_countries, "inputs": [i.description for i in r.inputs],
                "failed_attempts": r.failed_attempts, "cost_micros": r.cost_micros,
                "problems": validate_provenance(r, third_party=bool(r.terms))}

    def review_images(self, *, after: dict[str, Path], before: dict[str, Path] | None = None) -> None:
        arts = self.gl.orch.artifacts
        for key, src in (("after", after), ("before", before or {})):
            for name, p in src.items():
                p = Path(p)
                if p.exists() and p.read_bytes()[:8] == b"\x89PNG\r\n\x1a\n":
                    self.images[key][name] = arts.put_file(p)
        review = self.mirror / "review"
        review.mkdir(parents=True, exist_ok=True)
        for v in VIEWS:  # the orchestrator's evidence shows these four views from out_dir
            if v in after and Path(after[v]).exists():
                shutil.copy2(after[v], review / f"{v}.png")

    def publish(self) -> None:
        """Copy the lane's mirror into the candidate workspace and record provenance next to the asset."""
        recs = [r for r in self.gl.registry.provenance(self.lane.asset_id) if r.version == self.lane.version]
        write_asset_provenance(self.mirror, recs)
        targets = [(self.mirror, self.target / "assets" / "source" / self.lane.asset_id)]
        if self.unity_mirror.exists():
            targets.append((self.unity_mirror, self.target / self.gl.cfg.unity_project_dir /
                            self.gl.cfg.unity_art_root / self.lane.asset_id))
        for src, dest in targets:
            dest.mkdir(parents=True, exist_ok=True)
            for p in sorted(src.rglob("*")):
                if p.is_file():
                    d = dest / p.relative_to(src)
                    d.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(p, d)

    def result(self, code: int, reason: str, done: list[str], *, gate: Optional[Stage] = None) -> SegmentResult:
        payload = {"asset_id": self.lane.asset_id, "version": self.lane.version, "gate": gate.value if gate else None,
                   "stages_run": done, "lane": self.lane.summary(), "renders": self.images,
                   "review": self.gate_payload, "notes": self.notes,
                   "provenance_problems": self.provenance_problems, "cost_micros": self.cost,
                   "failed_jobs": self.failed_jobs}
        return SegmentResult(code, self.lane.asset_id, reason, pick=f"{self.lane.asset_id}@v{self.lane.version}",
                             out_dir=str(self.target / "assets" / "source" / self.lane.asset_id / "review")
                             if code == EXIT_PICKED else None,
                             stages=done, gate=gate.value if gate else None, asset_stage=payload, cost_micros=self.cost,
                             failed_jobs=self.failed_jobs)


def contract_evidence(root: RootTask, attempt: CandidateAttempt, res: SegmentResult) -> Evidence:
    """Evidence the orchestrator's verification accepts as the lane's technical check for this candidate."""
    return Evidence(id=new_id("ev"), root_id=root.id, attempt_id=attempt.id, evidence_class=EvidenceClass.STATIC,
                    status=EvidenceStatus.PASS, name=CONTRACT_EVIDENCE,
                    summary=f"{res.pick}: stages {', '.join(res.stages)} passed the asset contract",
                    details={"gate": res.gate, "stages": res.stages})
