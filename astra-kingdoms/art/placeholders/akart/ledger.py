"""Asset-ledger rows (format ``AK-ASSET-LEDGER/1``) for the generated placeholders.

The format and its validation rules are defined by
``unity/Assets/Scripts/Core/Assets/AssetLedger.cs``. Placeholders must use a ``generated:`` path;
we put the Unity asset path after the prefix (``generated:Assets/Art/...``) so the release tool
(``AstraKingdoms.Release ledger-validate --root unity``) can re-hash the committed file and prove
that the ledger describes exactly these bytes.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from . import GENERATOR_VERSION

FORMAT = "AK-ASSET-LEDGER/1"
LICENCE = "Original in-house work generated from code; no third-party input (placeholder, owner approval needed before public use)"
RIGHTS = "Astra Kingdoms owner (in-house; confirm the legal entity before release)"
SOURCE = "in-house generator astra-kingdoms/art/placeholders"


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def entry(id_: str, kind: str, unity_path: str, purpose: str, provenance: str, digest: str) -> dict:
    # Key order follows the existing ledger file.
    return {
        "id": id_, "kind": kind, "status": "Placeholder", "path": "generated:" + unity_path, "purpose": purpose,
        "source": SOURCE, "licence": LICENCE, "rights_holder": RIGHTS, "attribution": None,
        "provenance": provenance, "territory": "worldwide", "sha256": digest,
    }


def unity_rel(path: Path, unity_root: Path) -> str:
    return path.resolve().relative_to(unity_root.resolve()).as_posix()


def build(icons, sfx, music, unity_root: Path) -> dict:
    rows = []
    for icon, png, svg in icons:
        rows.append(entry(
            f"texture.icon.{icon.id}", "Texture", unity_rel(png, unity_root), f"{icon.purpose} (placeholder icon, ticket 69)",
            f"{GENERATOR_VERSION} icons.py: vector ops -> Pillow raster 4x supersampled; SVG source art/placeholders/svg/{svg.name} "
            f"sha256 {sha256(svg)}; no external input", sha256(png)))
    for spec, wav in sfx:
        rows.append(entry(
            f"audio.generated.{spec.id}", "Sound", unity_rel(wav, unity_root), f"{spec.purpose} (cue {spec.cue}; placeholder, ticket 70)",
            f"{GENERATOR_VERSION} sfx.py:{spec.make.__name__}: numpy synthesis, fixed PCG64 seeds, 22050 Hz mono 16-bit, "
            f"peak -1 dBFS; no samples or external input", sha256(wav)))
    for spec, wav in music:
        rows.append(entry(
            f"audio.generated.{spec.id}", "Music", unity_rel(wav, unity_root), f"{spec.purpose} (cue {spec.cue}; placeholder, ticket 71)",
            f"{GENERATOR_VERSION} music.py: drone + Karplus-Strong plucks, {spec.bpm} bpm x {spec.bars} bars, seed {spec.seed}, "
            f"circular render for a seamless loop; no samples or external input", sha256(wav)))
    return {"format": FORMAT, "entries": rows}


def dump(ledger: dict) -> str:
    text = json.dumps(ledger, indent=2, ensure_ascii=True) + "\n"
    text.encode("ascii")  # the ledger is ASCII by contract
    return text
