"""The asset production lane: stages, approval gates and invalidation rules.

Plan: "The standard lane is brief -> concept approval -> generation -> Blender normalization ->
technical report and turntable -> visual approval -> rig -> deformation review -> motion/retargeting
-> contact/transition review -> Unity prefab -> physical-device evidence. Unnecessary stages may be
skipped for a prop, but the asset contract remains."

Each stage is a state with outputs bound to sha256 hashes. The five owner gates (concept approval,
visual approval, deformation review, contact/transition review, device evidence) are approved only
for the exact output hashes they were shown; a later change to any upstream output invalidates them.

Invalidation rules (plan: "A new mesh topology invalidates skinning and usually dependent animation
validation. A changed skeleton invalidates retarget evidence. A texture-only revision still requires
material/import and visual checks."):

==================  ===========================================================================
change              invalidated stages
==================  ===========================================================================
topology            technical report, visual approval, rig (skinning), deformation review,
                    motion/retarget, contact/transition review, Unity prefab, device evidence
skeleton            deformation review, motion/retarget (retarget evidence), contact/transition
                    review, Unity prefab, device evidence (the mesh and its visual approval stay)
texture / material  technical report (material/import checks), visual approval, Unity prefab,
                    device evidence (rig, deformation, motion and contact evidence stay)
motion clip         contact/transition review, Unity prefab, device evidence
==================  ===========================================================================

Route switching never overwrites an approved asset: it creates a *proposed replacement* version
that must pass the same contract and gates; incompatibility pauses with a report.
"""

from __future__ import annotations

import hashlib
import json
from enum import Enum
from typing import Optional

from pydantic import BaseModel, Field


class Stage(str, Enum):
    BRIEF = "brief"
    CONCEPT = "concept"
    CONCEPT_APPROVAL = "concept_approval"
    GENERATION = "generation"
    NORMALIZATION = "normalization"
    TECHNICAL_REPORT = "technical_report"  # technical report + turntable
    VISUAL_APPROVAL = "visual_approval"
    RIG = "rig"
    DEFORMATION_REVIEW = "deformation_review"
    MOTION = "motion_retarget"
    CONTACT_REVIEW = "contact_transition_review"
    UNITY_PREFAB = "unity_prefab"
    DEVICE_EVIDENCE = "device_evidence"


ORDER: list[Stage] = list(Stage)
GATES = frozenset({Stage.CONCEPT_APPROVAL, Stage.VISUAL_APPROVAL, Stage.DEFORMATION_REVIEW, Stage.CONTACT_REVIEW,
                   Stage.DEVICE_EVIDENCE})
#: Stages a prop may skip (the contract still applies to what remains).
PROP_SKIPPABLE = frozenset({Stage.CONCEPT, Stage.CONCEPT_APPROVAL, Stage.RIG, Stage.DEFORMATION_REVIEW, Stage.MOTION,
                            Stage.CONTACT_REVIEW})
#: Kinds that must run the full lane.
FULL_LANE_KINDS = frozenset({"character", "character_base"})


class ChangeKind(str, Enum):
    TOPOLOGY = "topology"
    SKELETON = "skeleton"
    TEXTURE = "texture"
    MATERIAL = "material"
    MOTION = "motion"


S = Stage
INVALIDATES: dict[ChangeKind, tuple[Stage, ...]] = {
    ChangeKind.TOPOLOGY: (S.TECHNICAL_REPORT, S.VISUAL_APPROVAL, S.RIG, S.DEFORMATION_REVIEW, S.MOTION,
                          S.CONTACT_REVIEW, S.UNITY_PREFAB, S.DEVICE_EVIDENCE),
    ChangeKind.SKELETON: (S.DEFORMATION_REVIEW, S.MOTION, S.CONTACT_REVIEW, S.UNITY_PREFAB, S.DEVICE_EVIDENCE),
    ChangeKind.TEXTURE: (S.TECHNICAL_REPORT, S.VISUAL_APPROVAL, S.UNITY_PREFAB, S.DEVICE_EVIDENCE),
    ChangeKind.MATERIAL: (S.TECHNICAL_REPORT, S.VISUAL_APPROVAL, S.UNITY_PREFAB, S.DEVICE_EVIDENCE),
    ChangeKind.MOTION: (S.CONTACT_REVIEW, S.UNITY_PREFAB, S.DEVICE_EVIDENCE),
}


class StageStatus(str, Enum):
    PENDING = "PENDING"
    DONE = "DONE"  # automatic stage produced outputs
    AWAITING_APPROVAL = "AWAITING_APPROVAL"  # gate shown to the owner
    APPROVED = "APPROVED"
    REJECTED = "REJECTED"
    SKIPPED = "SKIPPED"
    INVALIDATED = "INVALIDATED"
    FAILED = "FAILED"


SATISFIED = frozenset({StageStatus.DONE, StageStatus.APPROVED, StageStatus.SKIPPED})


class LaneError(Exception):
    pass


class StageRecord(BaseModel):
    stage: Stage
    status: StageStatus = StageStatus.PENDING
    outputs: dict[str, str] = Field(default_factory=dict)  # repository path / name -> sha256
    route: Optional[str] = None
    gate_hash: Optional[str] = None  # combined hash the owner was shown (gates)
    candidate_hash: Optional[str] = None  # Forge candidate commit that carried the gate's outputs
    root_id: Optional[str] = None
    decided_by: Optional[str] = None
    decided_at: Optional[float] = None
    reason: str = ""
    report: dict = Field(default_factory=dict)
    history: list[dict] = Field(default_factory=list)


def combined_hash(outputs: dict[str, str]) -> str:
    return hashlib.sha256(json.dumps(sorted(outputs.items()), separators=(",", ":")).encode()).hexdigest()


class AssetLane(BaseModel):
    asset_id: str
    kind: str
    version: int = 1
    brief_sha256: str = ""
    replaces_version: Optional[int] = None  # route switching: a proposed replacement of an approved version
    stages: dict[Stage, StageRecord] = Field(default_factory=dict)
    routes: dict[str, str] = Field(default_factory=dict)  # lane kind -> chosen route id
    skipped: list[Stage] = Field(default_factory=list)
    paused_reason: str = ""

    # ---------------------------------------------------------------- construction
    @classmethod
    def new(cls, asset_id: str, kind: str, *, skip: list[Stage] | None = None, brief_sha256: str = "") -> "AssetLane":
        skip = list(skip or [])
        if kind in FULL_LANE_KINDS and skip:
            raise LaneError(f"a {kind} must run the full lane; cannot skip {[s.value for s in skip]}")
        bad = [s for s in skip if s not in PROP_SKIPPABLE]
        if bad:
            raise LaneError(f"stages {[s.value for s in bad]} cannot be skipped (the asset contract remains)")
        lane = cls(asset_id=asset_id, kind=kind, brief_sha256=brief_sha256, skipped=skip)
        for st in ORDER:
            lane.stages[st] = StageRecord(stage=st, status=StageStatus.SKIPPED if st in skip else StageStatus.PENDING)
        return lane

    def rec(self, stage: Stage) -> StageRecord:
        return self.stages[stage]

    # ---------------------------------------------------------------- progress
    def next_stage(self) -> Optional[Stage]:
        for st in ORDER:
            if self.stages[st].status not in SATISFIED:
                return st
        return None

    def done(self) -> bool:
        return self.next_stage() is None

    def _require_upstream(self, stage: Stage) -> None:
        for st in ORDER[:ORDER.index(stage)]:
            if self.stages[st].status not in SATISFIED:
                raise LaneError(f"{stage.value} cannot run: {st.value} is {self.stages[st].status.value}")

    def complete(self, stage: Stage, outputs: dict[str, str], *, route: str | None = None, report: dict | None = None,
                 at: float = 0.0) -> StageRecord:
        """Record an automatic stage's outputs (or a gate's evidence, which then awaits the owner)."""
        self._require_upstream(stage)
        r = self.stages[stage]
        if r.status == StageStatus.SKIPPED:
            raise LaneError(f"{stage.value} was skipped for this asset")
        prev = dict(r.outputs)
        r.outputs = dict(outputs)
        r.route = route or r.route
        r.report = dict(report or {})
        r.reason = ""
        if stage in GATES:
            r.status = StageStatus.AWAITING_APPROVAL
            r.gate_hash = self.gate_hash(stage)
            r.decided_by, r.decided_at, r.candidate_hash = None, None, None
        else:
            r.status = StageStatus.DONE
        r.history.append({"event": "completed", "at": at, "outputs": outputs, "previous": prev})
        return r

    def gate_hash(self, stage: Stage) -> str:
        """What the owner approves: every output of this stage and of all stages before it."""
        allout: dict[str, str] = {}
        for st in ORDER[:ORDER.index(stage) + 1]:
            for k, v in self.stages[st].outputs.items():
                allout[f"{st.value}/{k}"] = v
        return combined_hash(allout)

    def fail(self, stage: Stage, reason: str, *, at: float = 0.0, report: dict | None = None) -> None:
        r = self.stages[stage]
        r.status = StageStatus.FAILED
        r.reason = reason
        if report is not None:
            r.report = dict(report)
        r.history.append({"event": "failed", "at": at, "reason": reason})

    # ---------------------------------------------------------------- gates
    def approve(self, stage: Stage, *, gate_hash: str, by: str, at: float = 0.0,
                candidate_hash: str | None = None, root_id: str | None = None) -> StageRecord:
        if stage not in GATES:
            raise LaneError(f"{stage.value} is not an owner gate")
        r = self.stages[stage]
        if r.status != StageStatus.AWAITING_APPROVAL:
            raise LaneError(f"{stage.value} is {r.status.value}, not awaiting approval")
        current = self.gate_hash(stage)
        if gate_hash != current or r.gate_hash != current:
            raise LaneError(f"{stage.value}: approval is for {gate_hash[:12]} but the current outputs are "
                            f"{current[:12]}; a prior approval never covers changed bytes")
        r.status = StageStatus.APPROVED
        r.decided_by, r.decided_at = by, at
        r.candidate_hash, r.root_id = candidate_hash, root_id
        r.history.append({"event": "approved", "at": at, "by": by, "gate_hash": gate_hash,
                          "candidate": candidate_hash})
        return r

    def reject(self, stage: Stage, *, by: str, correction: str, at: float = 0.0) -> StageRecord:
        if stage not in GATES:
            raise LaneError(f"{stage.value} is not an owner gate")
        if not correction.strip():
            raise LaneError("a rejection needs a specific correction")
        r = self.stages[stage]
        r.status = StageStatus.REJECTED
        r.decided_by, r.decided_at, r.reason = by, at, correction
        r.history.append({"event": "rejected", "at": at, "by": by, "correction": correction})
        # the stage(s) that produced the rejected outputs run again with the correction
        producers = PRODUCERS.get(stage, ())
        for st in producers:
            if self.stages[st].status in SATISFIED and self.stages[st].status != StageStatus.SKIPPED:
                self.stages[st].status = StageStatus.PENDING
                self.stages[st].reason = f"re-run requested at {stage.value}: {correction}"
        return r

    def corrections(self, stage: Stage) -> list[str]:
        return [h["correction"] for h in self.stages[stage].history if h.get("event") == "rejected"]

    # ---------------------------------------------------------------- changes
    def record_change(self, change: ChangeKind, *, at: float = 0.0, reason: str = "") -> list[Stage]:
        """Apply the plan's invalidation rule for ``change``; returns the stages invalidated."""
        hit = []
        for st in INVALIDATES[change]:
            r = self.stages[st]
            if r.status in (StageStatus.SKIPPED, StageStatus.PENDING):
                continue
            r.status = StageStatus.INVALIDATED
            r.reason = f"{change.value} change: {reason}".strip(": ")
            r.history.append({"event": "invalidated", "at": at, "change": change.value, "reason": reason})
            hit.append(st)
        return hit

    def propose_replacement(self, *, route_kind: str, route: str) -> "AssetLane":
        """Route switching: a new version that must pass the same contract; the approved one is untouched."""
        new = AssetLane.new(self.asset_id, self.kind, skip=list(self.skipped), brief_sha256=self.brief_sha256)
        new.version = self.version + 1
        new.replaces_version = self.version
        new.routes = {**self.routes, route_kind: route}
        # the brief and (if approved) the concept carry over; everything produced by the old route reruns
        for st in (S.BRIEF, S.CONCEPT, S.CONCEPT_APPROVAL):
            new.stages[st] = self.stages[st].model_copy(deep=True)
        return new

    def summary(self) -> dict:
        return {"asset_id": self.asset_id, "version": self.version, "kind": self.kind,
                "next": (self.next_stage() or Stage.DEVICE_EVIDENCE).value if not self.done() else "done",
                "stages": {s.value: r.status.value for s, r in self.stages.items()}, "routes": self.routes,
                "replaces_version": self.replaces_version, "paused": self.paused_reason}


#: Which automatic stages produce what each gate shows (rerun on rejection).
PRODUCERS: dict[Stage, tuple[Stage, ...]] = {
    S.CONCEPT_APPROVAL: (S.CONCEPT,),
    S.VISUAL_APPROVAL: (S.GENERATION, S.NORMALIZATION, S.TECHNICAL_REPORT),
    S.DEFORMATION_REVIEW: (S.RIG,),
    S.CONTACT_REVIEW: (S.MOTION,),
    S.DEVICE_EVIDENCE: (S.UNITY_PREFAB,),
}


def segment(lane: AssetLane) -> list[Stage]:
    """Stages one orchestrator dispatch runs: from the next unsatisfied stage up to the next owner gate."""
    out = []
    started = False
    for st in ORDER:
        r = lane.stages[st]
        if not started:
            if r.status in SATISFIED:
                continue
            started = True
        if r.status == StageStatus.SKIPPED:
            continue
        out.append(st)
        if st in GATES:
            break
    return out
