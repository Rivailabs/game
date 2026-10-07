"""Audio lane: select clips from a documented licensed library and write a rights record.

Plan ("Model routes..." table, Sound/music row): "Start with a documented licensed library or a
specifically approved model/service and output-rights record." This lane does the library half:

* the library is a JSON catalogue the owner provides (clip id, file, sha256, licence, attribution,
  source URL, tags, duration, sample rate, channels) plus the library's licence document;
* only licences on the brief's allow-list are eligible; an unknown or missing licence is rejected
  (fails closed), and CC-BY clips must carry an attribution string;
* when the audio file is present it is measured (WAV PCM: format, duration, peak, RMS level,
  clipping) and its sha256 must match the catalogue;
* each pick becomes a rights record (the plan's Audio and Provenance contract fields) with status
  ``AWAITING_OWNER_APPROVAL``: the owner still listens to it and confirms the rights.

``verify_rights`` re-checks a rights file (hash, licence, attribution) and is the protected check
named ``audio-lane`` in the template's project configuration.

Level is reported as RMS dBFS. That is **not** a LUFS (ITU-R BS.1770) loudness measurement; the
plan's loudness target therefore remains an owner listening check until a LUFS meter is added.
"""

from __future__ import annotations

import array
import hashlib
import json
import math
import sys
import time
import wave
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Optional

from .base import LaneReport

ATTRIBUTION_REQUIRED = ("CC-BY",)


@dataclass
class WavFacts:
    sample_rate: int
    channels: int
    sample_width_bits: int
    duration_s: float
    peak_dbfs: float
    rms_dbfs: float
    clipped_samples: int


def analyse_wav(path: str | Path) -> WavFacts:
    with wave.open(str(path), "rb") as w:
        rate, ch, width, frames = w.getframerate(), w.getnchannels(), w.getsampwidth(), w.getnframes()
        data = w.readframes(frames)
    if width != 2:
        raise ValueError(f"{path}: only 16-bit PCM WAV is analysed (got {width * 8}-bit)")
    samples = array.array("h")
    samples.frombytes(data)
    if sys.byteorder == "big":  # pragma: no cover - WAV is little-endian
        samples.byteswap()
    full = 32767
    peak = max((abs(s) for s in samples), default=0)
    rms = math.sqrt(sum(s * s for s in samples) / len(samples)) if samples else 0.0
    to_db = (lambda v: round(20 * math.log10(v / full), 2) if v > 0 else -120.0)
    return WavFacts(sample_rate=rate, channels=ch, sample_width_bits=width * 8,
                    duration_s=round(frames / rate, 3) if rate else 0.0, peak_dbfs=to_db(peak),
                    rms_dbfs=to_db(rms), clipped_samples=sum(1 for s in samples if s >= full or s <= -full))


def sha256_file(path: str | Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


@dataclass
class RightsRecord:
    brief_id: str
    purpose: str
    clip_id: str
    title: str
    library: str
    library_licence_doc: str
    licence: str
    attribution: str
    source_url: str
    file: str
    sha256: str
    format: dict = field(default_factory=dict)
    loudness: dict = field(default_factory=dict)
    loop_policy: str = "one-shot"
    import_policy: str = ""
    territory_flags: list[str] = field(default_factory=list)
    selected_at: float = 0.0
    status: str = "AWAITING_OWNER_APPROVAL"


def _licence_allowed(licence: str, allowed: list[str]) -> bool:
    return bool(licence) and licence in allowed


def select(briefs_doc: dict, library: dict, *, library_dir: str | Path = ".", clock=time.time
           ) -> tuple[LaneReport, list[RightsRecord]]:
    rep = LaneReport(lane="audio", evidence_class="static",
                     human_checkpoints=["owner listens to each pick and confirms its rights record",
                                        "loudness/mix check on the reference phone (no clipping at the approved mix)"])
    allowed = list(briefs_doc.get("allowed_licences", []))
    if not allowed:
        rep.add("error", "no_licence_policy", "the audio briefs declare no allowed licences")
        return rep.finalise(), []
    if not library.get("licence_doc"):
        rep.add("error", "library_undocumented", "the library has no licence document reference")
    target = briefs_doc.get("loudness_target_dbfs")
    root = Path(library_dir)
    picks: list[RightsRecord] = []
    rejected: dict[str, str] = {}
    for brief in briefs_doc.get("briefs", []):
        bid = brief["id"]
        want = {t.lower() for t in brief.get("tags", [])}
        ranked = []
        for clip in library.get("clips", []):
            cid = clip.get("id", "?")
            lic = clip.get("licence", "")
            if not _licence_allowed(lic, allowed):
                rejected[cid] = f"licence {lic or 'missing'} not allowed"
                continue
            if lic.startswith(ATTRIBUTION_REQUIRED) and not clip.get("attribution"):
                rejected[cid] = "CC-BY clip without attribution"
                continue
            overlap = len(want & {t.lower() for t in clip.get("tags", [])})
            if overlap == 0:
                continue
            dur = clip.get("duration_s")
            if dur is not None and brief.get("max_seconds") is not None and dur > brief["max_seconds"]:
                continue
            ranked.append((-overlap, dur or 0, cid, clip))
        if not ranked:
            rep.add("error", "no_candidate", f"no licensed clip matches brief {bid!r}; the owner chooses another "
                    "source (never an unlicensed one)", bid)
            continue
        clip = sorted(ranked, key=lambda x: x[:3])[0][3]
        fmt: dict = {"declared_sample_rate": clip.get("sample_rate"), "declared_channels": clip.get("channels"),
                     "declared_duration_s": clip.get("duration_s")}
        loud: dict = {"target_dbfs": target, "method": "RMS dBFS (not LUFS)"}
        f = root / clip.get("file", "")
        if clip.get("file") and f.is_file():
            actual = sha256_file(f)
            if clip.get("sha256") and actual != clip["sha256"]:
                rep.add("error", "hash_mismatch", f"{clip['id']}: file hash differs from the catalogue", bid)
            if f.suffix.lower() == ".wav":
                try:
                    facts = analyse_wav(f)
                    fmt.update(asdict(facts))
                    loud.update({"rms_dbfs": facts.rms_dbfs, "peak_dbfs": facts.peak_dbfs})
                    if facts.clipped_samples:
                        rep.add("warning", "clipping", f"{clip['id']}: {facts.clipped_samples} clipped sample(s)", bid)
                    if brief.get("mono") and facts.channels != 1:
                        rep.add("warning", "not_mono", f"{clip['id']}: {facts.channels} channels; import as mono", bid)
                except (ValueError, wave.Error) as e:
                    rep.add("warning", "unanalysed", f"{clip['id']}: {e}", bid)
            sha = actual
        else:
            rep.add("warning", "file_absent", f"{clip['id']}: audio file not present; format and hash unverified", bid)
            sha = clip.get("sha256", "")
        picks.append(RightsRecord(
            brief_id=bid, purpose=brief.get("purpose", ""), clip_id=clip["id"], title=clip.get("title", ""),
            library=library.get("name", ""), library_licence_doc=library.get("licence_doc", ""),
            licence=clip["licence"], attribution=clip.get("attribution", ""), source_url=clip.get("source_url", ""),
            file=clip.get("file", ""), sha256=sha, format=fmt, loudness=loud,
            loop_policy="loop (boundaries need a listening check)" if brief.get("loop") else "one-shot",
            import_policy="mono, Decompress On Load, Vorbis" if brief.get("mono", True) else "stereo, Streaming",
            selected_at=clock()))
    rep.details = {"picks": [p.clip_id for p in picks], "rejected_for_rights": rejected,
                   "allowed_licences": allowed}
    return rep.finalise(), picks


def write_rights(picks: list[RightsRecord], path: str | Path) -> Path:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps({"records": [asdict(r) for r in picks]}, indent=2, sort_keys=True) + "\n")
    return p


def verify_rights(path: str | Path, *, library_dir: Optional[str | Path] = None,
                  allowed: Optional[list[str]] = None) -> LaneReport:
    rep = LaneReport(lane="audio", evidence_class="static",
                     human_checkpoints=["owner approval of each rights record"])
    p = Path(path)
    if not p.exists():
        rep.add("error", "no_rights_file", f"{p} does not exist")
        return rep.finalise()
    data = json.loads(p.read_text())
    root = Path(library_dir) if library_dir else p.parent
    for r in data.get("records", []):
        where = r.get("brief_id", "?")
        lic = r.get("licence", "")
        if not lic or (allowed is not None and lic not in allowed):
            rep.add("error", "licence", f"{r.get('clip_id')}: licence {lic or 'missing'} not allowed", where)
        if lic.startswith(ATTRIBUTION_REQUIRED) and not r.get("attribution"):
            rep.add("error", "attribution", f"{r.get('clip_id')}: CC-BY without attribution", where)
        f = root / r.get("file", "")
        if not r.get("file") or not f.is_file():
            rep.add("error", "file_missing", f"{r.get('clip_id')}: file {r.get('file')!r} not found", where)
        elif sha256_file(f) != r.get("sha256"):
            rep.add("error", "hash_mismatch", f"{r.get('clip_id')}: file bytes differ from the rights record", where)
        if r.get("status") != "APPROVED":
            rep.add("info", "awaiting_owner", f"{r.get('clip_id')}: {r.get('status')}", where)
    rep.details = {"records": len(data.get("records", []))}
    return rep.finalise()
