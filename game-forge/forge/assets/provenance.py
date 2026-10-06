"""Provenance records per asset and the licence inventory export.

Every stage that brings in or transforms content writes a :class:`ProvenanceRecord` (inputs and their
rights, model/service/version, date, applicable terms, transformation history, attribution, territory
flags and final hashes). The records live in the store and in ``provenance.json`` next to the asset in
the repository, so the rights metadata travels with exported source.

The licence inventory joins generated-asset provenance with the catalogue lane's ``credits.json``
files into one table (JSON and CSV) for release review: every model, texture, clip and sound with its
licence, terms, territory restrictions and attribution.
"""

from __future__ import annotations

import csv
import io
import json
from pathlib import Path
from typing import Iterable

from .contracts import ProvenanceRecord

INVENTORY_COLUMNS = ["asset_id", "version", "stage", "source", "route_or_licence", "model", "model_version",
                     "terms", "attribution", "excluded_territories", "distribution_countries", "public_demo_allowed",
                     "generated_or_downloaded", "files", "hashes", "manual_steps"]


def write_asset_provenance(asset_dir: Path, records: Iterable[ProvenanceRecord]) -> Path:
    recs = [json.loads(r.model_dump_json()) for r in records]
    p = asset_dir / "provenance.json"
    p.write_text(json.dumps(recs, indent=2, sort_keys=True) + "\n")
    return p


def inventory_rows(records: Iterable[ProvenanceRecord], credits_files: Iterable[Path] = ()) -> list[dict]:
    rows = []
    for r in records:
        rows.append({
            "asset_id": r.asset_id, "version": r.version, "stage": r.stage, "source": r.service or r.route,
            "route_or_licence": r.route, "model": r.model, "model_version": r.model_version,
            "terms": "; ".join(f"{t.name} {t.version}".strip() for t in r.terms),
            "attribution": r.attribution, "excluded_territories": ",".join(r.territory.excluded_territories),
            "distribution_countries": ",".join(r.territory.distribution_countries),
            "public_demo_allowed": r.territory.public_demo_allowed, "generated_or_downloaded": r.generated_at,
            "files": ";".join(sorted(r.final_hashes)), "hashes": ";".join(r.final_hashes[k] for k in
                                                                         sorted(r.final_hashes)),
            "manual_steps": ";".join(t.step for t in r.transformations if t.manual),
        })
    for cf in credits_files:
        try:
            credits = json.loads(Path(cf).read_text())
        except (OSError, json.JSONDecodeError):
            continue
        for c in credits if isinstance(credits, list) else []:
            rows.append({
                "asset_id": Path(cf).parent.name, "version": 1, "stage": "catalogue", "source": c.get("source_url", ""),
                "route_or_licence": c.get("licence", ""), "model": c.get("uid", ""), "model_version": "",
                "terms": c.get("licence", ""), "attribution": f"{c.get('name', '')} by {c.get('artist', '')}",
                "excluded_territories": "", "distribution_countries": "", "public_demo_allowed": "",
                "generated_or_downloaded": c.get("download_date", ""), "files": c.get("uid", ""), "hashes": "",
                "manual_steps": "owner licence check required",
            })
    return rows


def inventory_csv(rows: list[dict]) -> str:
    buf = io.StringIO()
    w = csv.DictWriter(buf, fieldnames=INVENTORY_COLUMNS)
    w.writeheader()
    for r in rows:
        w.writerow({k: r.get(k, "") for k in INVENTORY_COLUMNS})
    return buf.getvalue()


def find_credits(repo_workdir: Path) -> list[Path]:
    root = repo_workdir / "assets" / "source"
    return sorted(root.glob("*/credits.json")) if root.exists() else []
