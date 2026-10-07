"""The required archer clip set and motion clip checks.

Plan: "The required archer clip set is idle, draw, hold, release, recover, hit, left dodge, right
dodge, jump, defeat and victory. [...] Idle and hold need tested loops; attacks need explicit event
markers and transitions. HY-Motion does not claim native seamless-loop or in-place modes, so those
clips require a separate cleanup and validation step when that route is used."

Each clip is checked against :class:`~forge.assets.contracts.MotionContract`: frame rate, duration,
clip name, root-motion/in-place declaration, contact timing, loop policy, transitions and markers.
The release marker is what triggers the projectile at the approved moment.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from .contracts import MotionContract

REQUIRED_ARCHER_CLIPS = ("idle", "draw", "hold", "release", "recover", "hit", "dodge_left", "dodge_right", "jump",
                         "defeat", "victory")
LOOP_CLIPS = frozenset({"idle", "hold"})


@dataclass(frozen=True)
class ClipSpec:
    name: str
    loop: bool
    root_motion: str  # "in_place" | "root_motion"
    min_s: float
    max_s: float
    markers: tuple[str, ...] = ()
    transitions: tuple[str, ...] = ()  # clips it must be able to transition to
    contacts: tuple[str, ...] = ()


#: Durations are proposed starting ranges for readability at the gameplay camera (owner may tune).
ARCHER_CLIPS: dict[str, ClipSpec] = {c.name: c for c in (
    ClipSpec("idle", True, "in_place", 1.0, 6.0, (), ("draw", "hit", "dodge_left", "dodge_right", "jump", "defeat",
                                                       "victory"), ("left_foot", "right_foot")),
    ClipSpec("draw", False, "in_place", 0.3, 2.0, ("draw_start", "draw_full"), ("hold", "hit"),
             ("left_foot", "right_foot", "bow_hand_grip")),
    ClipSpec("hold", True, "in_place", 0.5, 4.0, (), ("release", "hit"), ("left_foot", "right_foot", "bow_hand_grip")),
    ClipSpec("release", False, "in_place", 0.1, 1.0, ("release",), ("recover",),
             ("left_foot", "right_foot", "bow_hand_grip")),
    ClipSpec("recover", False, "in_place", 0.2, 1.5, ("recover_end",), ("idle", "draw")),
    ClipSpec("hit", False, "in_place", 0.2, 1.5, ("hit_react",), ("idle", "defeat")),
    ClipSpec("dodge_left", False, "root_motion", 0.3, 1.5, ("dodge_start", "dodge_end"), ("idle",),
             ("left_foot", "right_foot")),
    ClipSpec("dodge_right", False, "root_motion", 0.3, 1.5, ("dodge_start", "dodge_end"), ("idle",),
             ("left_foot", "right_foot")),
    ClipSpec("jump", False, "in_place", 0.4, 2.0, ("takeoff", "land"), ("idle",), ("left_foot", "right_foot")),
    ClipSpec("defeat", False, "in_place", 0.5, 4.0, ("defeat_settle",), ()),
    ClipSpec("victory", False, "in_place", 0.5, 5.0, (), ("idle",)),
)}

ALLOWED_FRAME_RATES = (24.0, 30.0, 60.0)
LOOP_SEAM_TOLERANCE_RAD = 0.02
IN_PLACE_TOLERANCE_M = 0.01

#: Routes whose clips need Forge's own loop cleanup / root-motion conversion (no native support).
NO_NATIVE_LOOP_OR_IN_PLACE = frozenset({"hy-motion-1.0"})


@dataclass
class ClipCheck:
    clip: str
    problems: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return not self.problems


def check_clip(c: MotionContract, spec: ClipSpec | None = None) -> ClipCheck:
    spec = spec or ARCHER_CLIPS.get(c.clip_name)
    out = ClipCheck(c.clip_name)
    p = out.problems
    if spec is None:
        p.append(f"{c.clip_name} is not part of the archer clip set")
        return out
    if c.frame_rate not in ALLOWED_FRAME_RATES:
        p.append(f"frame rate {c.frame_rate} not in {ALLOWED_FRAME_RATES}")
    if not (spec.min_s <= c.duration_s <= spec.max_s):
        p.append(f"duration {c.duration_s:.2f}s outside {spec.min_s}-{spec.max_s}s")
    if spec.loop:
        if c.loop_policy != "loop":
            p.append("clip must loop")
        if c.loop_seam_error is None:
            p.append("loop not tested (no seam measurement)")
        elif c.loop_seam_error > LOOP_SEAM_TOLERANCE_RAD:
            p.append(f"loop seam error {c.loop_seam_error:.3f} rad > {LOOP_SEAM_TOLERANCE_RAD}")
    elif c.loop_policy == "loop":
        p.append("one-shot clip is declared as looping")
    if c.root_motion != spec.root_motion:
        p.append(f"declared {c.root_motion}, expected {spec.root_motion}")
    if c.root_motion == "in_place" and c.root_horizontal_travel_m > IN_PLACE_TOLERANCE_M:
        p.append(f"in-place clip drifts {c.root_horizontal_travel_m:.3f} m (root drift)")
    names = [e.name for e in c.events]
    for m in spec.markers:
        if m not in names:
            p.append(f"missing event marker {m}")
    for e in c.events:
        if not (0.0 <= e.time_s <= c.duration_s):
            p.append(f"marker {e.name} at {e.time_s}s is outside the clip")
    if spec.name == "draw" and {"draw_start", "draw_full"} <= set(names):
        t = {e.name: e.time_s for e in c.events}
        if t["draw_start"] >= t["draw_full"]:
            p.append("draw_full must come after draw_start")
    targets = {t.to_clip for t in c.transitions}
    for t in spec.transitions:
        if t not in targets:
            p.append(f"no transition to {t}")
    contacts = {w.name for w in c.contacts}
    for w in spec.contacts:
        if w not in contacts:
            p.append(f"no contact timing for {w}")
    for w in c.contacts:
        if not (0 <= w.start_s <= w.end_s <= c.duration_s):
            p.append(f"contact {w.name} window outside the clip")
    if c.source_route in NO_NATIVE_LOOP_OR_IN_PLACE:
        if spec.loop and "loop_cleanup" not in c.cleanup_applied:
            p.append(f"{c.source_route} has no native seamless loops: loop_cleanup stage required")
        if spec.root_motion == "in_place" and "root_motion_conversion" not in c.cleanup_applied:
            p.append(f"{c.source_route} has no native in-place mode: root_motion_conversion stage required")
    return out


def check_clip_set(clips: list[MotionContract], *, skeleton_hash: str | None = None) -> list[ClipCheck]:
    """Check every required archer clip is present and passes; extra clips are reported."""
    by = {c.clip_name: c for c in clips}
    out = []
    for name in REQUIRED_ARCHER_CLIPS:
        c = by.get(name)
        if c is None:
            out.append(ClipCheck(name, ["required clip missing"]))
            continue
        chk = check_clip(c)
        if skeleton_hash and c.skeleton_hash and c.skeleton_hash != skeleton_hash:
            chk.problems.append("clip was retargeted to a different skeleton (retarget evidence invalid)")
        out.append(chk)
    for name in sorted(set(by) - set(REQUIRED_ARCHER_CLIPS)):
        out.append(ClipCheck(name, [f"{name} is not part of the archer clip set"]))
    return out


def needs_cleanup(route_id: str, clip: str) -> list[str]:
    """Stages Forge must add after generation for this route and clip."""
    spec = ARCHER_CLIPS.get(clip)
    if spec is None or route_id not in NO_NATIVE_LOOP_OR_IN_PLACE:
        return []
    out = []
    if spec.loop:
        out.append("loop_cleanup")
    if spec.root_motion == "in_place":
        out.append("root_motion_conversion")
    return out
