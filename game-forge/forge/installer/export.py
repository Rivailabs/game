"""Project export that works without a Forge subscription (or Forge at all).

Plan: "support export without ongoing subscription dependence" and the R4 exit gate "exported
projects work without a Forge subscription".

The export is plain, open formats only:

* ``source.bundle``: a ``git bundle`` of the accepted branch (``git clone source.bundle`` restores
  the full history; no Forge needed);
* ``records/<table>.json``: every durable record (tasks, attempts, evidence, approvals, review
  decisions, budget and cash ledgers, events) as JSON rows;
* ``specs/``: the versioned specifications, change reports and acceptance cases as written;
* ``artifacts/<sha256>``: every artifact the records reference (evidence logs, reports), by hash;
* ``EXPORT.md`` and ``export-manifest.json`` (sha256 of every file).

Credentials are never part of a project and are never exported.
"""

from __future__ import annotations

import hashlib
import json
import shutil
import sqlite3
import subprocess
import tempfile
from pathlib import Path
from typing import Optional

from ..credentials import scrubbed_env

README = """# {project} export

This directory needs no Forge account, subscription or installation.

- `git clone source.bundle {project}` restores the accepted source with its full history
  (branch `{branch}`).
- `records/*.json` hold every task, attempt, evidence record, approval, review decision, ledger entry
  and event as plain JSON rows.
- `specs/` holds the versioned specifications, change reports and acceptance cases.
- `artifacts/<sha256>` are the evidence files the records reference, named by their sha256.
- `export-manifest.json` lists the sha256 of every file so you can check the copy is complete.

Credentials and API keys are never part of a project export.
"""


class ExportError(Exception):
    pass


def _sha(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def _dump_tables(db_path: Path, out: Path) -> tuple[list[str], set[str]]:
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    refs: set[str] = set()
    tables = []
    try:
        names = [r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table' "
                                           "AND name NOT LIKE 'sqlite_%' ORDER BY name")]
        for name in names:
            rows = [dict(r) for r in con.execute(f'SELECT * FROM "{name}"')]
            (out / f"{name}.json").write_text(json.dumps(rows, indent=1, sort_keys=True, default=str))
            tables.append(name)
            for r in rows:
                for v in r.values():
                    if isinstance(v, str):
                        refs.update(_hashes_in(v))
    finally:
        con.close()
    return tables, refs


def _hashes_in(text: str) -> set[str]:
    import re

    return set(re.findall(r"\b[0-9a-f]{64}\b", text))


def export_project(*, project_id: str, repo_path: str | Path, branch: str, db_path: str | Path, out_dir: str | Path,
                   spec_dir: Optional[str | Path] = None, artifacts_dir: Optional[str | Path] = None) -> dict:
    out = Path(out_dir)
    if out.exists() and any(out.iterdir()):
        raise ExportError(f"{out} is not empty")
    (out / "records").mkdir(parents=True)
    p = subprocess.run(["git", "-C", str(repo_path), "bundle", "create", str(out / "source.bundle"), branch],
                       capture_output=True, text=True, env=scrubbed_env())
    if p.returncode != 0:
        raise ExportError(f"git bundle failed: {p.stderr.strip()}")
    tables, refs = _dump_tables(Path(db_path), out / "records")
    if spec_dir and Path(spec_dir).exists():
        shutil.copytree(spec_dir, out / "specs")
    copied = []
    if artifacts_dir:  # any 64-hex value in a record that names a stored artifact is copied
        adir = Path(artifacts_dir)
        for digest in sorted(refs):
            src = adir / digest[:2] / digest
            if src.exists():
                (out / "artifacts").mkdir(exist_ok=True)
                shutil.copyfile(src, out / "artifacts" / digest)
                copied.append(digest)
    (out / "EXPORT.md").write_text(README.format(project=project_id, branch=branch))
    files = {f.relative_to(out).as_posix(): _sha(f) for f in sorted(out.rglob("*")) if f.is_file()}
    manifest = {"format": "game-forge-project-export/1", "project_id": project_id, "branch": branch,
                "tables": tables, "artifacts": copied, "files": files,
                "needs_forge": False, "contains_credentials": False}
    (out / "export-manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n")
    return manifest


def verify_export(out_dir: str | Path) -> list[str]:
    out = Path(out_dir)
    m = json.loads((out / "export-manifest.json").read_text())
    problems = [f"{name}: missing" for name in m["files"] if not (out / name).exists()]
    problems += [f"{name}: changed" for name, d in m["files"].items() if (out / name).exists() and _sha(out / name) != d]
    with tempfile.TemporaryDirectory() as tmp:  # the real test: a plain git clone, no Forge involved
        p = subprocess.run(["git", "clone", "-q", "--branch", m["branch"], str(out / "source.bundle"),
                            str(Path(tmp) / "src")], capture_output=True, text=True, env=scrubbed_env())
    if p.returncode != 0:
        problems.append(f"source.bundle does not clone: {p.stderr.strip()[:200]}")
    return problems
