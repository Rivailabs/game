#!/usr/bin/env python3
"""Validate the art briefs in art/briefs (format described in art/briefs/README.md).

Checks:
* every ``### AK-ART-<ticket>-<letter>`` section has all eight plan fields with non-empty text;
* tickets 65-72 each have at least one brief section;
* ticket 67 lists exactly twenty weapon identities (rows numbered 1-20);
* ticket 69 lists exactly forty backticked icon IDs.

Exit 0 when clean, 1 with one line per problem otherwise.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REQUIRED = ("Use", "Silhouette", "Scale", "Camera distance", "Palette", "Allowed references", "Device budget",
            "Licence provenance")
BRIEFS = Path(__file__).resolve().parents[1] / "briefs"
SECTION = re.compile(r"^### (AK-ART-(\d{3})-[A-Z])\b(.*)$")
ROW = re.compile(r"^\|\s*([^|]+?)\s*\|\s*(.*?)\s*\|\s*$")


def sections(text: str) -> list[tuple[str, int, dict[str, str]]]:
    out, cur = [], None
    for line in text.splitlines():
        m = SECTION.match(line)
        if m:
            cur = (m.group(1), int(m.group(2)), {})
            out.append(cur)
            continue
        if line.startswith("## ") or line.startswith("### "):
            cur = None
            continue
        if cur is not None:
            r = ROW.match(line)
            if r and r.group(1) not in ("Field", "---"):
                cur[2][r.group(1)] = r.group(2)
    return out


def check(briefs: Path = BRIEFS) -> list[str]:
    problems: list[str] = []
    seen_tickets: set[int] = set()
    ids: set[str] = set()
    for f in sorted(briefs.glob("*.md")):
        text = f.read_text(encoding="utf-8")
        for sid, ticket, fields in sections(text):
            if sid in ids:
                problems.append(f"{f.name}: duplicate brief id {sid}")
            ids.add(sid)
            seen_tickets.add(ticket)
            for field in REQUIRED:
                if not fields.get(field, "").strip():
                    problems.append(f"{f.name}: {sid} is missing '{field}'")
        if f.name.startswith("67-"):
            nums = [int(n) for n in re.findall(r"^\|\s*(\d{1,2})\s*\|", text, flags=re.M)]
            if sorted(nums) != list(range(1, 21)):
                problems.append(f"{f.name}: expected weapon identities 1-20, found {sorted(nums)}")
        if f.name.startswith("69-"):
            icons = set(re.findall(r"`((?:element|terrain|card|status|dodge|ui)-[a-z-]+)`", text))
            if len(icons) != 40:
                problems.append(f"{f.name}: expected 40 icon ids, found {len(icons)}")
    for t in range(65, 73):
        if t not in seen_tickets:
            problems.append(f"ticket {t}: no brief section")
    return problems


def main() -> int:
    problems = check()
    for p in problems:
        print(f"FAIL: {p}")
    if not problems:
        print("PASS: briefs complete")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
