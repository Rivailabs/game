"""The Astra archer skeleton and the retarget mapping file format.

The skeleton contract needs *stable* joint names and hierarchy so every rigging route (UniRig, a
commercial rigger, Meshy/Tripo auto-rig, Mixamo) is normalized onto the same skeleton and every
motion route is retargeted through an owner-approved mapping.

``astra_archer_v1``: 38 deforming joints (budget 50) plus non-deforming sockets for the bow grip,
string hand, arrow nock and quiver. The bowstring is NOT part of the mesh or skeleton: it is driven
procedurally at runtime from the draw state and the ``bow_string_top``/``bow_string_bottom``
attachment points of the bow asset.

Retarget mapping file (JSON, ``forge-retarget/1``)::

    {
      "format": "forge-retarget/1",
      "target_skeleton": "astra_archer_v1",
      "target_skeleton_hash": "<sha256 of the canonical joint list>",
      "source_skeleton": "mixamo",
      "source_up_axis": "+Y", "source_forward_axis": "+Z", "source_scale_to_m": 0.01,
      "root_motion_source": "mixamorig:Hips",
      "bones": {"hips": "mixamorig:Hips", "spine_01": "mixamorig:Spine", ...},
      "unmapped_ok": ["thumb_02_l", ...],
      "rest_pose_offsets": {"upperarm_l": [x, y, z, w]}
    }

The owner approves a mapping by its exact sha256 (canonical JSON); a changed skeleton changes the
target hash and so invalidates every mapping and retarget evidence built on the old one.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Optional

from .contracts import Joint

SKELETON_ID = "astra_archer_v1"
MAPPING_FORMAT = "forge-retarget/1"


def _side(side: str) -> list[Joint]:
    s = side
    return [
        Joint(name=f"clavicle_{s}", parent="spine_03"),
        Joint(name=f"upperarm_{s}", parent=f"clavicle_{s}"),
        Joint(name=f"lowerarm_{s}", parent=f"upperarm_{s}"),
        Joint(name=f"hand_{s}", parent=f"lowerarm_{s}"),
        Joint(name=f"thumb_01_{s}", parent=f"hand_{s}"),
        Joint(name=f"thumb_02_{s}", parent=f"thumb_01_{s}"),
        Joint(name=f"index_01_{s}", parent=f"hand_{s}"),
        Joint(name=f"index_02_{s}", parent=f"index_01_{s}"),
        Joint(name=f"middle_01_{s}", parent=f"hand_{s}"),
        Joint(name=f"middle_02_{s}", parent=f"middle_01_{s}"),
        Joint(name=f"ring_01_{s}", parent=f"hand_{s}"),
        Joint(name=f"pinky_01_{s}", parent=f"hand_{s}"),
        Joint(name=f"thigh_{s}", parent="hips"),
        Joint(name=f"calf_{s}", parent=f"thigh_{s}"),
        Joint(name=f"foot_{s}", parent=f"calf_{s}"),
        Joint(name=f"toe_{s}", parent=f"foot_{s}"),
    ]


ARCHER_JOINTS: list[Joint] = [
    Joint(name="root", parent=None, deforming=False),
    Joint(name="hips", parent="root"),
    Joint(name="spine_01", parent="hips"),
    Joint(name="spine_02", parent="spine_01"),
    Joint(name="spine_03", parent="spine_02"),
    Joint(name="neck", parent="spine_03"),
    Joint(name="head", parent="neck"),
    *_side("l"),
    *_side("r"),
    # non-deforming sockets (named attachments)
    Joint(name="bow_grip_socket", parent="hand_l", deforming=False),
    Joint(name="string_hand_socket", parent="hand_r", deforming=False),
    Joint(name="arrow_nock_socket", parent="hand_r", deforming=False),
    Joint(name="quiver_socket", parent="spine_03", deforming=False),
]

ARCHER_ATTACHMENTS = ["bow_grip_socket", "string_hand_socket", "arrow_nock_socket", "quiver_socket"]
FINGER_PREFIXES = ("thumb_", "index_", "middle_", "ring_", "pinky_")
#: Body joints every motion mapping must cover; finger joints may stay at rest (e.g. SMPL body models).
REQUIRED_MAPPED = [j.name for j in ARCHER_JOINTS if j.deforming and not j.name.startswith(FINGER_PREFIXES)]


def skeleton_hash(joints: list[Joint]) -> str:
    canon = json.dumps([[j.name, j.parent, j.deforming] for j in joints], separators=(",", ":"))
    return hashlib.sha256(canon.encode()).hexdigest()


ARCHER_SKELETON_HASH = skeleton_hash(ARCHER_JOINTS)


def deforming_count(joints: list[Joint]) -> int:
    return sum(1 for j in joints if j.deforming)


def mapping_sha256(mapping: dict) -> str:
    """Canonical hash of a mapping (what the owner approves)."""
    return hashlib.sha256(json.dumps(mapping, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def load_mapping(path: str | Path) -> dict:
    return json.loads(Path(path).read_text())


def validate_mapping(mapping: dict, *, joints: list[Joint] | None = None,
                     source_bones: Optional[list[str]] = None) -> list[str]:
    """Problems with a retarget mapping against the target skeleton (and the source rig, if known)."""
    joints = joints or ARCHER_JOINTS
    p: list[str] = []
    if mapping.get("format") != MAPPING_FORMAT:
        p.append(f"format must be {MAPPING_FORMAT}")
    if mapping.get("target_skeleton") != SKELETON_ID and joints is ARCHER_JOINTS:
        p.append(f"target_skeleton must be {SKELETON_ID}")
    if mapping.get("target_skeleton_hash") != skeleton_hash(joints):
        p.append("target_skeleton_hash does not match the current skeleton (skeleton changed: re-approve)")
    for k in ("source_skeleton", "source_up_axis", "source_forward_axis", "root_motion_source"):
        if not mapping.get(k):
            p.append(f"missing {k}")
    scale = mapping.get("source_scale_to_m")
    if not isinstance(scale, (int, float)) or scale <= 0:
        p.append("source_scale_to_m must be a positive number")
    bones = mapping.get("bones") or {}
    names = {j.name for j in joints}
    unknown = sorted(set(bones) - names)
    if unknown:
        p.append(f"unknown target joints {unknown}")
    unmapped_ok = set(mapping.get("unmapped_ok") or [])
    for req in (j.name for j in joints if j.deforming):
        if req not in bones and req not in unmapped_ok:
            p.append(f"target joint {req} is not mapped")
    for req in REQUIRED_MAPPED:
        if req in unmapped_ok:
            p.append(f"body joint {req} cannot be left unmapped")
    srcs = [v for v in bones.values()]
    dup = sorted({s for s in srcs if srcs.count(s) > 1})
    if dup:
        p.append(f"source bones used twice {dup}")
    if source_bones is not None:
        missing = sorted(set(srcs) - set(source_bones))
        if missing:
            p.append(f"source rig lacks bones {missing}")
        if mapping.get("root_motion_source") not in source_bones:
            p.append("root_motion_source is not a bone of the source rig")
    return p


DATA_DIR = Path(__file__).with_name("data")


def builtin_mapping(name: str) -> dict:
    """Committed mappings: ``mixamo`` and ``smpl`` (HY-Motion body joints; verify against the release)."""
    return load_mapping(DATA_DIR / f"retarget_{name}_archer.json")
