"""UI lane: static checks of a screen specification before anything is built in Unity.

Input is ``design/ui/screens.json`` (see the turn-duel-2p scaffold). The lane checks what can be
checked without a phone: every interactive element meets the minimum touch target, every text
element names a localization key that exists in the reference table (no hard-coded text), every
navigation target exists, every screen is reachable from the start screen and can get back to it,
and the template's required screens are present.

It cannot judge layout, readability or visual fit: those stay with the owner's visual approval of
the exact candidate on the physical phone (listed as a human checkpoint on every report).
"""

from __future__ import annotations

import json
from collections import deque
from pathlib import Path
from typing import Optional

from .base import LaneReport

INTERACTIVE = {"button", "toggle", "slider", "tab", "card"}
TEXT_TYPES = {"button", "toggle", "label", "tab", "title"}
REQUIRED_SCREENS = ("menu", "match", "results")


def check_screens(spec: dict, strings: Optional[dict[str, str]] = None, *,
                  required_screens: tuple[str, ...] = REQUIRED_SCREENS) -> LaneReport:
    rep = LaneReport(lane="ui", evidence_class="static",
                     human_checkpoints=["visual_approval of each screen's exact candidate on the reference phone",
                                        "touch-input scenario on the physical device"])
    min_dp = int(spec.get("min_touch_dp", 48))
    screens = spec.get("screens")
    if not isinstance(screens, list) or not screens:
        rep.add("error", "schema", "`screens` must be a non-empty list")
        return rep.finalise()
    ids = [s.get("id") for s in screens]
    dupes = {i for i in ids if ids.count(i) > 1}
    for d in sorted(x for x in dupes if x):
        rep.add("error", "duplicate_screen", f"screen id {d!r} appears more than once")
    known = set(ids)
    start = spec.get("start", ids[0])
    if start not in known:
        rep.add("error", "start_missing", f"start screen {start!r} is not defined")
    for req in required_screens:
        if req not in known:
            rep.add("error", "required_screen", f"template screen {req!r} is missing")

    edges: dict[str, set[str]] = {i: set() for i in known if i}
    keys_used: set[str] = set()
    for s in screens:
        sid = s.get("id", "?")
        el_ids = [e.get("id") for e in s.get("elements", [])]
        for d in {i for i in el_ids if el_ids.count(i) > 1}:
            rep.add("error", "duplicate_element", f"element id {d!r} repeats", sid)
        for e in s.get("elements", []):
            where = f"{sid}.{e.get('id', '?')}"
            et = e.get("type", "")
            if "text" in e and "text_key" not in e:
                rep.add("error", "hardcoded_text", f"hard-coded text {e['text']!r}; use a localization key", where)
            if et in TEXT_TYPES:
                key = e.get("text_key")
                if not key:
                    rep.add("error", "missing_text_key", f"{et} has no text_key", where)
                else:
                    keys_used.add(key)
                    if strings is not None and key not in strings:
                        rep.add("error", "unknown_text_key", f"text_key {key!r} is not in the reference table", where)
            if et in INTERACTIVE:
                size = e.get("size_dp")
                if not (isinstance(size, list) and len(size) == 2):
                    rep.add("error", "touch_size_missing", f"{et} has no size_dp [w, h]", where)
                elif min(size) < min_dp:
                    rep.add("error", "touch_target", f"touch target {size[0]}x{size[1]} dp is below {min_dp} dp", where)
            tgt = e.get("goes_to")
            if tgt:
                if tgt not in known:
                    rep.add("error", "bad_navigation", f"goes_to {tgt!r} is not a screen", where)
                else:
                    edges.setdefault(sid, set()).add(tgt)

    def reach(frm: str, graph: dict[str, set[str]]) -> set[str]:
        seen, q = {frm}, deque([frm])
        while q:
            for n in graph.get(q.popleft(), ()):
                if n not in seen:
                    seen.add(n)
                    q.append(n)
        return seen

    if start in known:
        reachable = reach(start, edges)
        for sid in sorted(i for i in known if i and i not in reachable):
            rep.add("error", "unreachable", f"screen {sid!r} cannot be reached from {start!r}", sid)
        for sid in sorted(i for i in reachable if i != start):
            if start not in reach(sid, edges):
                rep.add("warning", "dead_end", f"screen {sid!r} has no path back to {start!r}", sid)
    rep.details = {"screens": len(screens), "min_touch_dp": min_dp, "text_keys_used": sorted(keys_used)}
    return rep.finalise()


def load_strings(path: str | Path) -> dict[str, str]:
    from .localization import parse_table

    return parse_table(Path(path).read_text(encoding="utf-8-sig")).entries


def check_screens_file(path: str | Path, strings_path: str | Path | None = None) -> LaneReport:
    spec = json.loads(Path(path).read_text())
    return check_screens(spec, load_strings(strings_path) if strings_path else None)
