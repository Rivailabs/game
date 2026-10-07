"""Turn document blocks into requirements, and keep requirement IDs stable across versions.

Rules of extraction (deliberately mechanical, so the owner can predict them):

* Every bullet and table row under a section heading is one requirement.
* A paragraph under a section heading is split into sentences; each sentence is one requirement.
* Prose before the first section heading is *context* (kind ``info``): it is kept for the
  record but never planned or turned into an acceptance case.
* ``- [RD-7] text`` keeps the explicit ID ``RD-7``.
* The kind (rule, ui, audio, ...) comes from the section heading; the normative level from the
  modal verb (must/shall/will -> must, should -> should, may/could -> may; a plain statement in
  a rules section is a rule).
"""

from __future__ import annotations

import difflib
import re

from .document import SourceDocument
from .spec import Requirement, RequirementStatus, normalise, text_hash

_SENTENCE_SPLIT = re.compile(r"(?<=[.!?])\s+(?=[A-Z\"'(])")
_EXPLICIT_ID = re.compile(r"^\[([A-Za-z][A-Za-z0-9_.-]{0,30})\]\s*")

#: (kind, heading words). First match wins, so specific kinds come before ``rule``.
SECTION_KINDS: list[tuple[str, tuple[str, ...]]] = [
    ("ui", ("screen", "screens", "ui", "interface", "hud", "layout", "menu", "menus", "accessibility")),
    ("audio", ("audio", "sound", "sounds", "music", "sfx")),
    ("localization", ("language", "languages", "localization", "localisation", "translation", "translations",
                      "text", "strings")),
    ("balance", ("balance", "balancing", "fairness", "tuning")),
    ("release", ("release", "releases", "store", "distribution", "countries", "launch", "publishing")),
    ("art", ("art", "visual", "visuals", "style", "look", "characters", "animation")),
    ("rule", ("player", "players", "match", "matches", "round", "rounds", "turn", "turns", "rule", "rules",
              "action", "actions", "winning", "win", "draw", "draws", "timer", "time", "randomness", "random",
              "start", "starting", "state", "setup", "scoring", "score", "replay", "rematch", "bot", "bots",
              "card", "cards", "board", "pieces", "rune", "runes", "weapons", "weapon", "combat", "moves",
              "resources", "economy", "end", "ending", "victory", "opponent", "ai")),
]

_MUST = re.compile(r"\b(must|shall|will|always|never|exactly|required|cannot|can not|may not|must not)\b", re.I)
_SHOULD = re.compile(r"\b(should|ideally|preferably|recommended)\b", re.I)
_MAY = re.compile(r"\b(may|could|optional|optionally|might)\b", re.I)


def classify_section(section: tuple[str, ...]) -> str:
    if not section:
        return "info"
    words = set(normalise(" ".join(section)).split())
    for kind, keys in SECTION_KINDS:
        if words & set(keys):
            return kind
    return "feature"


def normative_level(text: str, kind: str) -> str:
    if kind == "info":
        return "info"
    if _MUST.search(text):
        return "must"
    if _SHOULD.search(text):
        return "should"
    if _MAY.search(text):
        return "may"
    return "must"  # a plain statement in a brief section is a statement of required behaviour


def extract_requirements(doc: SourceDocument) -> list[Requirement]:
    """Requirements with *provisional* IDs (``X<n>``); :func:`assign_ids` makes them stable."""
    out: list[Requirement] = []
    n = 0
    for b in doc.blocks:
        kind = classify_section(b.section)
        pieces = [b.text] if b.kind != "paragraph" else [s.strip() for s in _SENTENCE_SPLIT.split(b.text)]
        for piece in pieces:
            if not piece:
                continue
            explicit = None
            m = _EXPLICIT_ID.match(piece)
            if m:
                explicit, piece = m.group(1), piece[m.end():]
            n += 1
            out.append(Requirement(
                id=explicit or f"X{n}", section=" / ".join(b.section) or "(overview)", text=piece, kind=kind,
                normative=normative_level(piece, kind), source_line=b.line,
                status=RequirementStatus.INFO if kind == "info" else RequirementStatus.ACTIVE,
            ))
    return out


def _next_id(used: set[str]) -> str:
    n = 1
    nums = [int(u[1:]) for u in used if re.fullmatch(r"R\d+", u)]
    if nums:
        n = max(nums) + 1
    return f"R{n}"


def assign_ids(new: list[Requirement], previous: list[Requirement] | None,
               issued: set[str] | None = None) -> list[Requirement]:
    """Give each new requirement a stable ID.

    Order of matching against the previous version: explicit ID; identical normalised text; the
    most similar requirement in the same section (ratio >= 0.6); the most similar anywhere
    (ratio >= 0.85, a moved requirement). Anything else gets a fresh ``R<n>`` that is never
    reused, even if an older requirement with that number was removed.
    """
    previous = previous or []
    used: set[str] = {r.id for r in previous} | set(issued or ())
    taken: set[str] = set()
    explicit_ids = {r.id for r in new if not re.fullmatch(r"X\d+", r.id)}
    prev_by_hash: dict[str, list[Requirement]] = {}
    for p in previous:
        prev_by_hash.setdefault(text_hash(p.text), []).append(p)

    pending: list[Requirement] = []
    for r in new:
        if r.id in explicit_ids:
            taken.add(r.id)
            continue
        cands = [p for p in prev_by_hash.get(text_hash(r.text), []) if p.id not in taken and p.id not in explicit_ids]
        if cands:
            r.id = cands[0].id
            taken.add(r.id)
        else:
            pending.append(r)

    def best(r: Requirement, pool: list[Requirement]) -> tuple[float, Requirement | None]:
        top, hit = 0.0, None
        a = normalise(r.text)
        for p in pool:
            ratio = difflib.SequenceMatcher(None, a, normalise(p.text)).ratio()
            if ratio > top:
                top, hit = ratio, p
        return top, hit

    still: list[Requirement] = []
    for r in pending:
        free = [p for p in previous if p.id not in taken and p.id not in explicit_ids and p.source == "document"]
        ratio, hit = best(r, [p for p in free if p.section == r.section])
        if hit is None or ratio < 0.6:
            ratio, hit = best(r, free)
            if ratio < 0.85:
                hit = None
        if hit is not None:
            r.id = hit.id
            taken.add(r.id)
        else:
            still.append(r)
    for r in still:
        r.id = _next_id(used | taken)
        taken.add(r.id)
    return new
