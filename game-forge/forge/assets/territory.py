"""Distribution territories (ISO 3166-1 alpha-2) and licence exclusion groups.

The Hunyuan3D 2.1 and HY-Motion 1.0 licences restrict use and distribution of outputs outside their
defined territory, which excludes the European Union, the United Kingdom and South Korea (plan
[S13, S14]). Routes declare exclusions as groups (``EU``, ``UK``, ``KR``); projects declare
distribution countries as ISO codes, or ``WW`` for worldwide distribution.
"""

from __future__ import annotations

from typing import Iterable

EU_MEMBERS = frozenset({
    "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU",
    "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE",
})
GROUPS: dict[str, frozenset[str]] = {
    "EU": EU_MEMBERS,
    "UK": frozenset({"GB"}),
    "KR": frozenset({"KR"}),
}
WORLDWIDE = {"WW", "GLOBAL", "WORLDWIDE", "*"}


def expand(codes: Iterable[str]) -> tuple[set[str], bool]:
    """(ISO codes, worldwide?) for a list mixing ISO codes, groups and ``WW``."""
    out: set[str] = set()
    worldwide = False
    for c in codes:
        c = c.strip().upper()
        if c in WORLDWIDE:
            worldwide = True
        elif c in GROUPS:
            out |= GROUPS[c]
        elif c == "GB":
            out.add("GB")
        elif c:
            out.add(c)
    return out, worldwide


def excluded_overlap(distribution: Iterable[str], excluded_groups: Iterable[str]) -> list[str]:
    """Exclusion groups that the declared distribution touches (worldwide touches every group)."""
    dist, ww = expand(distribution)
    hits = []
    for g in excluded_groups:
        members, _ = expand([g])
        if ww or dist & members:
            hits.append(g.upper())
    return hits
