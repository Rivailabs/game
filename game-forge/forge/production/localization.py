"""Localization lane: string extraction, key checks and the translation review workflow.

Table format: the Astra/template ``key = value`` text files (``Assets/Resources/Localization/<code>.txt``;
``#`` comment lines; positional parameters ``{0}``; a duplicate key is an error, never an override).

Checks (``check_project``):

* **extraction**: keys referenced from C# (``.Get("key")`` / ``.Format("key", ...)``); a prefix such as
  ``Get("difficulty." + level)`` marks every ``difficulty.*`` key as used;
* a referenced key missing from the reference (English) table is an **error**;
* a parameter mismatch between the reference and a translation (``{0}`` vs ``{1}``) is an **error**;
* empty values, malformed lines and duplicate keys are **errors**;
* keys missing from a translation are **warnings** (the game falls back to English, but the
  language is not release-ready); keys only in a translation are warnings; unused keys are info;
* hard-coded player-facing text passed straight to UI calls is a **warning** with its location.

Review workflow (``ReviewLedger``): every (language, key) moves DRAFT -> IN_REVIEW -> APPROVED or
REJECTED. An approval is bound to the hash of the English source *and* of the translation, so
changing either makes it STALE. A language is release-ready only when every reference key has a
current APPROVED translation. Forge never marks its own (machine) translation as reviewed.
"""

from __future__ import annotations

import hashlib
import json
import re
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from .base import LaneReport

_PARAM = re.compile(r"\{(\d+)\}")
_KEY_CALL = re.compile(r"(?<![A-Za-z0-9_])(?:Get|Format|T|TF)\(\s*\"([A-Za-z0-9_\-]*\.[A-Za-z0-9_.\-]*)\"\s*(\+)?")
_HARDCODED = (
    re.compile(r"\.text\s*=\s*\"([^\"]*[A-Za-z]{2,}[^\"]*)\""),
    re.compile(r"\b(?:ui|Ui|UI)\.(?:Button|Label|Text|Title)\(\s*[^,()\n]+,\s*\"([^\"]*[A-Za-z]{2,}[^\"]*)\""),
)


@dataclass
class Table:
    code: str
    entries: dict[str, str] = field(default_factory=dict)
    order: list[str] = field(default_factory=list)
    header: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)


def parse_table(text: str, code: str = "") -> Table:
    t = Table(code)
    in_header = True
    for n, raw in enumerate(text.replace("\r\n", "\n").split("\n"), 1):
        line = raw.strip().lstrip("﻿")
        if not line:
            continue
        if line.startswith("#"):
            if in_header:
                t.header.append(line[1:].strip())
            continue
        in_header = False
        if "=" not in line or line.index("=") == 0:
            t.errors.append(f"line {n}: expected 'key = value'")
            continue
        key, value = (s.strip() for s in line.split("=", 1))
        if key in t.entries:
            t.errors.append(f"line {n}: duplicate key '{key}'")
            continue
        t.entries[key] = value.replace("\\n", "\n")
        t.order.append(key)
    return t


def params(value: str) -> set[str]:
    return set(_PARAM.findall(value))


def extract_keys(sources: dict[str, str]) -> tuple[dict[str, list[str]], set[str]]:
    """(key -> files using it, prefixes used dynamically)."""
    used: dict[str, list[str]] = {}
    prefixes: set[str] = set()
    for path, text in sources.items():
        for m in _KEY_CALL.finditer(text):
            key, concat = m.group(1), m.group(2)
            if concat or key.endswith("."):
                prefixes.add(key)
            else:
                used.setdefault(key, []).append(path)
    return used, prefixes


def find_hardcoded(sources: dict[str, str]) -> list[tuple[str, int, str]]:
    out = []
    for path, text in sources.items():
        for pat in _HARDCODED:
            for m in pat.finditer(text):
                line = text.count("\n", 0, m.start()) + 1
                out.append((path, line, m.group(1)))
    return sorted(out)


# String tables are named by locale code (en.txt, hi.txt, pt-BR.txt); other .txt files such as
# glossary.txt live beside them but are not tables.
_LOCALE_CODE = re.compile(r"[a-z]{2,3}(?:[-_][A-Za-z]{2,4})?")


def load_project(assets_dir: str | Path, *, reference: str = "en") -> tuple[dict[str, Table], dict[str, str]]:
    root = Path(assets_dir)
    loc_dirs = [p for p in root.rglob("Localization") if p.is_dir() and any(p.glob("*.txt"))]
    tables: dict[str, Table] = {}
    for d in loc_dirs:
        for f in sorted(d.glob("*.txt")):
            if not _LOCALE_CODE.fullmatch(f.stem):
                continue  # not a string table (e.g. glossary.txt)
            tables[f.stem] = parse_table(f.read_text(encoding="utf-8-sig"), f.stem)
    sources = {p.relative_to(root).as_posix(): p.read_text(encoding="utf-8", errors="replace")
               for p in sorted(root.rglob("*.cs"))}
    return tables, sources


def check_project(tables: dict[str, Table], sources: dict[str, str], *, reference: str = "en",
                  ledger: Optional["ReviewLedger"] = None) -> LaneReport:
    rep = LaneReport(lane="localization", evidence_class="static",
                     human_checkpoints=["fluent-speaker review of each translation (APPROVED in the review ledger)",
                                        "font coverage, wrapping and button widths checked on the reference phone"])
    if reference not in tables:
        rep.add("error", "no_reference", f"reference table {reference}.txt not found")
        return rep.finalise()
    ref = tables[reference]
    for code, t in tables.items():
        for e in t.errors:
            rep.add("error", "table_error", e, f"{code}.txt")
        for k, v in t.entries.items():
            if not v.strip():
                rep.add("error", "empty_value", f"empty value for {k}", f"{code}.txt")
    used, prefixes = extract_keys(sources)
    for key, files in sorted(used.items()):
        if key not in ref.entries:
            rep.add("error", "missing_key", f"key {key!r} used in code but missing from {reference}.txt",
                    ", ".join(sorted(set(files))))
    for key in ref.order:
        if key not in used and not any(key.startswith(p) for p in prefixes):
            rep.add("info", "unused_key", f"{key} is not referenced from C# (it may be used from data)",
                    f"{reference}.txt")
    langs = {}
    for code, t in sorted(tables.items()):
        if code == reference:
            continue
        missing = [k for k in ref.order if k not in t.entries]
        extra = [k for k in t.order if k not in ref.entries]
        for k in missing:
            rep.add("warning", "missing_translation", f"{code} has no {k} (falls back to {reference})", f"{code}.txt")
        for k in extra:
            rep.add("warning", "extra_key", f"{code} has {k}, which {reference} does not", f"{code}.txt")
        for k in ref.order:
            if k in t.entries and params(ref.entries[k]) != params(t.entries[k]):
                rep.add("error", "param_mismatch", f"{k}: {reference} uses {sorted(params(ref.entries[k]))}, "
                        f"{code} uses {sorted(params(t.entries[k]))}", f"{code}.txt")
        review = ledger.language_status(code, ref, t) if ledger else None
        flagged = any("review" in h.lower() for h in t.header)
        langs[code] = {"keys": len(t.entries), "missing": len(missing), "extra": len(extra),
                       "header_says_needs_review": flagged,
                       "review": review, "release_ready": bool(review and review["release_ready"]) and not missing}
    for path, line, text in find_hardcoded(sources):
        rep.add("warning", "hardcoded_text", f"player-facing literal {text!r}; use a localization key",
                f"{path}:{line}")
    rep.details = {"reference": reference, "reference_keys": len(ref.entries), "keys_used_in_code": len(used),
                   "dynamic_prefixes": sorted(prefixes), "languages": langs}
    return rep.finalise()


def check_assets(assets_dir: str | Path, *, reference: str = "en", ledger_path: str | Path | None = None) -> LaneReport:
    tables, sources = load_project(assets_dir, reference=reference)
    ledger = ReviewLedger(ledger_path) if ledger_path else None
    return check_project(tables, sources, reference=reference, ledger=ledger)


# --------------------------------------------------------------------------- review workflow

REVIEW_STATES = ("DRAFT", "IN_REVIEW", "APPROVED", "REJECTED")


def _h(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


class ReviewError(Exception):
    pass


class ReviewLedger:
    """Append-only JSON-lines ledger of translation review actions; current state is derived."""

    def __init__(self, path: str | Path, clock=time.time):
        self.path = Path(path)
        self.clock = clock

    def _entries(self) -> list[dict]:
        if not self.path.exists():
            return []
        return [json.loads(x) for x in self.path.read_text().splitlines() if x.strip()]

    def _append(self, entry: dict) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with open(self.path, "a", encoding="utf-8") as fh:
            fh.write(json.dumps({**entry, "at": self.clock()}, sort_keys=True, ensure_ascii=False) + "\n")

    def state(self, lang: str, key: str) -> Optional[dict]:
        cur = None
        for e in self._entries():
            if e["lang"] == lang and e["key"] == key:
                cur = e
        return cur

    def submit(self, lang: str, key: str, source: str, translation: str, *, by: str, machine: bool = False) -> None:
        self._append({"lang": lang, "key": key, "state": "IN_REVIEW", "source_hash": _h(source),
                      "translation_hash": _h(translation), "by": by, "machine": machine})

    def decide(self, lang: str, key: str, source: str, translation: str, *, reviewer: str, approve: bool,
               reason: str = "") -> None:
        cur = self.state(lang, key)
        if cur is None or cur["state"] != "IN_REVIEW":
            raise ReviewError(f"{lang}:{key} is not in review")
        if cur["source_hash"] != _h(source) or cur["translation_hash"] != _h(translation):
            raise ReviewError(f"{lang}:{key} changed since it was submitted; resubmit it")
        if cur["by"] == reviewer:
            raise ReviewError("the translator cannot review their own translation")
        if not approve and not reason.strip():
            raise ReviewError("a rejection needs a reason")
        self._append({"lang": lang, "key": key, "state": "APPROVED" if approve else "REJECTED",
                      "source_hash": cur["source_hash"], "translation_hash": cur["translation_hash"],
                      "by": reviewer, "reason": reason, "machine": cur.get("machine", False)})

    def status_of(self, lang: str, key: str, source: str, translation: Optional[str]) -> str:
        cur = self.state(lang, key)
        if translation is None:
            return "MISSING"
        if cur is None:
            return "DRAFT"
        if cur["source_hash"] != _h(source) or cur["translation_hash"] != _h(translation):
            return "STALE"
        return cur["state"]

    def language_status(self, lang: str, ref: Table, table: Table) -> dict:
        counts: dict[str, int] = {}
        for k in ref.order:
            s = self.status_of(lang, k, ref.entries[k], table.entries.get(k))
            counts[s] = counts.get(s, 0) + 1
        return {"counts": counts, "release_ready": counts.get("APPROVED", 0) == len(ref.order)}
