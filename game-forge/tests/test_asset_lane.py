"""The asset lane state machine: order, gates bound to exact hashes, skipping and invalidation rules."""

from __future__ import annotations

import pytest

from forge.assets.lane import (
    GATES,
    INVALIDATES,
    ORDER,
    AssetLane,
    ChangeKind,
    LaneError,
    Stage as S,
    StageStatus as T,
    segment,
)


def run_to(lane: AssetLane, upto: S) -> None:
    """Complete stages (approving gates) until ``upto`` has been completed."""
    for st in ORDER:
        if lane.rec(st).status == T.SKIPPED:
            continue
        lane.complete(st, {f"{st.value}.bin": f"h-{st.value}"})
        if st in GATES:
            lane.approve(st, gate_hash=lane.gate_hash(st), by="owner")
        if st == upto:
            return


def test_standard_order_and_gates_match_the_plan():
    assert [s.value for s in ORDER] == [
        "brief", "concept", "concept_approval", "generation", "normalization", "technical_report", "visual_approval",
        "rig", "deformation_review", "motion_retarget", "contact_transition_review", "unity_prefab", "device_evidence"]
    assert GATES == {S.CONCEPT_APPROVAL, S.VISUAL_APPROVAL, S.DEFORMATION_REVIEW, S.CONTACT_REVIEW,
                     S.DEVICE_EVIDENCE}


def test_segments_run_up_to_each_gate():
    lane = AssetLane.new("archer", "character")
    assert segment(lane) == [S.BRIEF, S.CONCEPT, S.CONCEPT_APPROVAL]
    run_to(lane, S.CONCEPT_APPROVAL)
    assert segment(lane) == [S.GENERATION, S.NORMALIZATION, S.TECHNICAL_REPORT, S.VISUAL_APPROVAL]
    run_to(lane, S.VISUAL_APPROVAL)
    assert segment(lane) == [S.RIG, S.DEFORMATION_REVIEW]
    run_to(lane, S.DEFORMATION_REVIEW)
    assert segment(lane) == [S.MOTION, S.CONTACT_REVIEW]
    run_to(lane, S.CONTACT_REVIEW)
    assert segment(lane) == [S.UNITY_PREFAB, S.DEVICE_EVIDENCE]
    run_to(lane, S.DEVICE_EVIDENCE)
    assert lane.done() and segment(lane) == []


def test_props_may_skip_stages_but_characters_and_contract_stages_may_not():
    prop = AssetLane.new("crate", "prop", skip=[S.CONCEPT, S.CONCEPT_APPROVAL, S.RIG, S.DEFORMATION_REVIEW, S.MOTION,
                                                S.CONTACT_REVIEW])
    assert segment(prop) == [S.BRIEF, S.GENERATION, S.NORMALIZATION, S.TECHNICAL_REPORT, S.VISUAL_APPROVAL]
    run_to(prop, S.VISUAL_APPROVAL)
    assert segment(prop) == [S.UNITY_PREFAB, S.DEVICE_EVIDENCE]
    with pytest.raises(LaneError, match="full lane"):
        AssetLane.new("archer", "character", skip=[S.RIG])
    with pytest.raises(LaneError, match="contract remains"):
        AssetLane.new("crate", "prop", skip=[S.TECHNICAL_REPORT])
    with pytest.raises(LaneError, match="skipped"):
        prop.complete(S.RIG, {"x": "y"})


def test_gate_approval_is_bound_to_the_exact_hash():
    lane = AssetLane.new("archer", "character")
    lane.complete(S.BRIEF, {"brief.json": "b1"})
    with pytest.raises(LaneError, match="cannot run"):
        lane.complete(S.CONCEPT_APPROVAL, {})
    lane.complete(S.CONCEPT, {"concept_0.png": "c1"})
    lane.complete(S.CONCEPT_APPROVAL, {"review.json": "r1"})
    assert lane.rec(S.CONCEPT_APPROVAL).status == T.AWAITING_APPROVAL
    shown = lane.gate_hash(S.CONCEPT_APPROVAL)
    lane.rec(S.CONCEPT).outputs["concept_0.png"] = "c2"  # bytes changed after the owner saw them
    with pytest.raises(LaneError, match="never covers changed bytes"):
        lane.approve(S.CONCEPT_APPROVAL, gate_hash=shown, by="owner")
    with pytest.raises(LaneError, match="not an owner gate"):
        lane.approve(S.CONCEPT, gate_hash=shown, by="owner")


def test_rejection_reruns_the_producing_stages_with_the_correction():
    lane = AssetLane.new("archer", "character")
    run_to(lane, S.CONCEPT_APPROVAL)
    for st in (S.GENERATION, S.NORMALIZATION, S.TECHNICAL_REPORT, S.VISUAL_APPROVAL):
        lane.complete(st, {f"{st.value}": "v1"})
    with pytest.raises(LaneError, match="specific correction"):
        lane.reject(S.VISUAL_APPROVAL, by="owner", correction=" ")
    lane.reject(S.VISUAL_APPROVAL, by="owner", correction="hand does not grip bow")
    assert [lane.rec(s).status for s in (S.GENERATION, S.NORMALIZATION, S.TECHNICAL_REPORT)] == [T.PENDING] * 3
    assert lane.rec(S.CONCEPT_APPROVAL).status == T.APPROVED  # earlier gates stand
    assert lane.corrections(S.VISUAL_APPROVAL) == ["hand does not grip bow"]
    assert segment(lane)[0] == S.GENERATION


@pytest.mark.parametrize("change,invalid,kept", [
    (ChangeKind.TOPOLOGY, {S.TECHNICAL_REPORT, S.VISUAL_APPROVAL, S.RIG, S.DEFORMATION_REVIEW, S.MOTION,
                           S.CONTACT_REVIEW, S.UNITY_PREFAB, S.DEVICE_EVIDENCE}, {S.CONCEPT_APPROVAL}),
    (ChangeKind.SKELETON, {S.DEFORMATION_REVIEW, S.MOTION, S.CONTACT_REVIEW, S.UNITY_PREFAB, S.DEVICE_EVIDENCE},
     {S.VISUAL_APPROVAL, S.RIG, S.TECHNICAL_REPORT}),
    (ChangeKind.TEXTURE, {S.TECHNICAL_REPORT, S.VISUAL_APPROVAL, S.UNITY_PREFAB, S.DEVICE_EVIDENCE},
     {S.RIG, S.DEFORMATION_REVIEW, S.MOTION, S.CONTACT_REVIEW}),
    (ChangeKind.MOTION, {S.CONTACT_REVIEW, S.UNITY_PREFAB, S.DEVICE_EVIDENCE},
     {S.MOTION, S.DEFORMATION_REVIEW, S.VISUAL_APPROVAL}),
])
def test_invalidation_rules(change, invalid, kept):
    assert set(INVALIDATES[change]) == invalid
    lane = AssetLane.new("archer", "character")
    run_to(lane, S.DEVICE_EVIDENCE)
    hit = set(lane.record_change(change, reason="test"))
    assert hit == invalid
    for st in invalid:
        assert lane.rec(st).status == T.INVALIDATED
    for st in kept:
        assert lane.rec(st).status in (T.APPROVED, T.DONE)
    assert not lane.done()


def test_route_switch_proposes_a_replacement_version():
    lane = AssetLane.new("archer", "character")
    run_to(lane, S.VISUAL_APPROVAL)
    lane.routes = {"mesh": "meshy"}
    new = lane.propose_replacement(route_kind="mesh", route="trellis2")
    assert new.version == 2 and new.replaces_version == 1 and new.routes["mesh"] == "trellis2"
    assert new.rec(S.CONCEPT_APPROVAL).status == T.APPROVED  # the approved concept carries over
    assert new.rec(S.GENERATION).status == T.PENDING
    assert lane.rec(S.VISUAL_APPROVAL).status == T.APPROVED  # the approved version is untouched
