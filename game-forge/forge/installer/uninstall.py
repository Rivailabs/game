"""Uninstall: remove the application, preserve projects.

Plan: "An uninstall removes the application by default while preserving projects and clearly
identifying any optional removal of caches/models."

The plan is computed and shown first (``plan_uninstall``); ``execute`` removes only what the plan
lists. Project directories, Forge data (database, specifications, evidence, backups) and anything
inside a project are always kept. Caches and downloaded models are removed only when explicitly
requested, and are listed with their size so the owner can decide.
"""

from __future__ import annotations

import shutil
from dataclasses import dataclass, field
from pathlib import Path


class UninstallError(Exception):
    pass


@dataclass
class UninstallPlan:
    remove: list[Path] = field(default_factory=list)
    keep: list[tuple[Path, str]] = field(default_factory=list)
    optional: list[tuple[Path, str, int]] = field(default_factory=list)  # not selected: path, what, bytes

    def markdown(self) -> str:
        out = ["Will remove:"] + [f"  - {p}" for p in self.remove] + ["Will keep:"]
        out += [f"  - {p} ({why})" for p, why in self.keep]
        if self.optional:
            out += ["Not removed unless you ask (--remove-caches / --remove-models):"]
            out += [f"  - {p} ({what}, {size / 1024 ** 2:.1f} MB)" for p, what, size in self.optional]
        return "\n".join(out) + "\n"


def _size(p: Path) -> int:
    if p.is_file():
        return p.stat().st_size
    return sum(f.stat().st_size for f in p.rglob("*") if f.is_file()) if p.exists() else 0


def _overlaps(a: Path, b: Path) -> bool:
    a, b = a.resolve(), b.resolve()
    return a == b or a in b.parents or b in a.parents


def plan_uninstall(install_root: str | Path, *, projects: list[str | Path], data_dirs: list[str | Path],
                   caches: list[str | Path] = (), models: list[str | Path] = (), remove_caches: bool = False,
                   remove_models: bool = False) -> UninstallPlan:
    root = Path(install_root)
    plan = UninstallPlan()
    protected = [(Path(p), "project") for p in projects] + [(Path(d), "Forge data: database, specs, evidence, backups")
                                                             for d in data_dirs]
    for p, why in protected:
        plan.keep.append((p, why))
    app_parts = [root / "versions", root / "current", root / "previous", root / "history.jsonl"]
    candidates = [(p, "application") for p in app_parts if p.exists()]
    for c in caches:
        (candidates if remove_caches else plan.optional).append(
            (Path(c), "cache") if remove_caches else (Path(c), "cache", _size(Path(c))))
    for m in models:
        (candidates if remove_models else plan.optional).append(
            (Path(m), "downloaded models") if remove_models else (Path(m), "downloaded models", _size(Path(m))))
    for path, what in candidates:
        clash = next((k for k, _ in protected if _overlaps(path, k)), None)
        if clash is not None:
            plan.keep.append((path, f"overlaps protected {clash}; never removed"))
            continue
        plan.remove.append(path)
    if (root / "backups").exists():
        plan.keep.append((root / "backups", "pre-update database backups"))
    return plan


def execute(plan: UninstallPlan, *, confirm: bool) -> list[Path]:
    if not confirm:
        raise UninstallError("uninstall not confirmed; review the plan and pass confirm=True")
    removed = []
    for p in plan.remove:
        if p.is_dir() and not p.is_symlink():
            shutil.rmtree(p)
        elif p.exists() or p.is_symlink():
            p.unlink()
        removed.append(p)
    return removed
