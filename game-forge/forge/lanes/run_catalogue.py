"""Run the catalogue lane for one brief.

search -> plan downloads under the cap -> fetch -> Blender cleanup + budget check + four
renders per candidate -> visual judge picks one or NONE -> write the pick, report, renders
and credit into ``<target>/assets/source/<id>/`` -> delete unused downloads and clear the
package cache.

Exit codes (also used by the orchestrator):

* 0 ``PICKED``  - a candidate was written; it still needs owner approval on the review page.
* 2 ``NONE``    - nothing suitable; fall through to the generation lane.
* 3 ``BLOCKED`` - a precondition is missing (disk < 10 GB, Blender, packages, index, judge policy).

Usable as ``python -m forge.lanes.run_catalogue <brief.json> --target <repo>`` or
``forge catalogue run``.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import shutil
import sys
import tempfile
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

from ..providers.base import JudgeCandidate, JudgeRequest, JudgeResult
from .asset_tools import VIEWS, BlenderAdapter, CleanupFailed, CleanupReport, ToolMissing, budget_check, judge_instructions
from .catalogue import (
    MAX_DOWNLOAD_BYTES,
    MIN_FREE_BYTES,
    Brief,
    CatalogueBlocked,
    Candidate,
    DiskGuardError,
    check_disk,
    clear_cache,
    fetch,
    index_exists,
    load_reject_list,
    objaverse_cache_dir,
    plan_downloads,
    record_credit,
    search,
)

EXIT_PICKED = 0
EXIT_NONE = 2
EXIT_BLOCKED = 3
STATUS = {EXIT_PICKED: "PICKED", EXIT_NONE: "NONE", EXIT_BLOCKED: "BLOCKED"}
PICK_STATUS = "AWAITING_OWNER_APPROVAL"
LICENCE_WARNING = ("Licence labels come from catalogue metadata and can be wrong. The owner must check the source "
                   "page and approve this pick on the review page before it ships.")


@dataclass
class LaneConfig:
    index_path: Path
    cache_dir: Path = field(default_factory=objaverse_cache_dir)
    work_root: Optional[Path] = None  # temporary downloads (default: system temp dir)
    reject_list: Optional[list[str]] = None  # None = packaged default list
    reject_list_path: Optional[Path] = None
    min_free_bytes: int = MIN_FREE_BYTES
    max_download_bytes: int = MAX_DOWNLOAD_BYTES
    downloader: Optional[Callable[[list[str]], dict[str, str]]] = None  # None = objaverse.load_objects
    disk_usage: Callable[[str], Any] = shutil.disk_usage
    blender: Optional[BlenderAdapter] = None
    today: Optional[Callable[[], str]] = None

    def rejects(self) -> list[str]:
        if self.reject_list is not None:
            return list(self.reject_list)
        return load_reject_list(self.reject_list_path)

    def date(self) -> str:
        return self.today() if self.today else _dt.datetime.now(_dt.timezone.utc).date().isoformat()


@dataclass
class LaneResult:
    exit_code: int
    brief_id: str
    reason: str
    pick: Optional[str] = None
    out_dir: Optional[str] = None
    files: list[str] = field(default_factory=list)
    candidates: list[dict] = field(default_factory=list)
    excluded: list[dict] = field(default_factory=list)
    judge: dict = field(default_factory=dict)
    cost_micros: int = 0
    downloaded_bytes: int = 0
    deleted: list[str] = field(default_factory=list)
    cache_cleared: list[str] = field(default_factory=list)
    requires_human_approval: bool = True
    pick_record: dict = field(default_factory=dict)

    @property
    def status(self) -> str:
        return STATUS[self.exit_code]

    def to_dict(self) -> dict:
        d = asdict(self)
        d["status"] = self.status
        return d


def preflight(brief: Brief, target: Path, cfg: LaneConfig, *, judge: Any = None, need_judge: bool = True
              ) -> Optional[str]:
    """Reason the lane cannot run (exit 3), or None. Nothing is downloaded or sent."""
    try:
        check_disk([target, cfg.work_root or Path(tempfile.gettempdir()), cfg.cache_dir],
                   min_free_bytes=cfg.min_free_bytes, disk_usage=cfg.disk_usage)
    except DiskGuardError as e:
        return f"disk guard: {e}"
    if cfg.blender is None:
        return "Blender adapter not configured"
    ok, why = cfg.blender.available()
    if not ok:
        return why
    if not index_exists(cfg.index_path):
        return f"catalogue index {cfg.index_path} not built: run `forge catalogue build-index`"
    if cfg.downloader is None:
        try:
            import objaverse  # type: ignore  # noqa: F401
        except ImportError:
            return "the 'objaverse' package is not installed: pip install 'game-forge[catalogue]'"
    if need_judge and judge is None:
        return "no visual judge route configured"
    return None


def _blocked(brief: Brief, reason: str, **kw) -> LaneResult:
    return LaneResult(EXIT_BLOCKED, brief.id, reason, **kw)


def run_brief(brief: Brief, target: str | Path, cfg: LaneConfig, judge: Any, *,
              on_cost: Callable[[JudgeResult], None] | None = None, idempotency_key: str | None = None) -> LaneResult:
    target = Path(target)
    why = preflight(brief, target, cfg, judge=judge)
    if why:
        return _blocked(brief, why)
    try:
        ranked = search(brief, cfg.index_path, reject_list=cfg.rejects())
    except CatalogueBlocked as e:
        return _blocked(brief, str(e))
    cands = [c.to_dict() for c in ranked]
    if not ranked:
        return LaneResult(EXIT_NONE, brief.id, "no catalogue entry matches the search terms", candidates=cands)
    planned, dropped = plan_downloads(ranked, cfg.max_download_bytes)
    excluded = list(dropped)
    if not planned:
        return LaneResult(EXIT_NONE, brief.id, "no candidate fits the download cap", candidates=cands,
                          excluded=excluded)
    if cfg.work_root:
        cfg.work_root.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix=f"catalogue-{brief.id}-", dir=str(cfg.work_root) if cfg.work_root else None))
    fetched_uids: list[str] = [c.uid for c in planned]
    result: LaneResult | None = None
    cache_paths: list[Path] = []
    try:
        try:
            fr = fetch([c.uid for c in planned], work / "downloads", downloader=cfg.downloader,
                       max_total_bytes=cfg.max_download_bytes)
        except CatalogueBlocked as e:
            result = _blocked(brief, str(e), candidates=cands, excluded=excluded)
            return result
        cache_paths = fr.cache_paths
        if fr.stopped:
            excluded.append({"uid": "-", "reason": fr.stopped})
        survivors: list[tuple[Candidate, CleanupReport]] = []
        for c in planned:
            src = fr.files.get(c.uid)
            if src is None:
                if not fr.stopped:
                    excluded.append({"uid": c.uid, "reason": "download failed"})
                continue
            try:
                rep = cfg.blender.cleanup(c.uid, src, work / "clean", brief)
            except ToolMissing as e:
                result = _blocked(brief, str(e), candidates=cands, excluded=excluded,
                                  downloaded_bytes=fr.total_bytes)
                return result
            except CleanupFailed as e:
                excluded.append({"uid": c.uid, "reason": str(e)})
                continue
            problems = budget_check(rep, brief)
            if problems:
                excluded.append({"uid": c.uid, "reason": "budget: " + "; ".join(problems)})
                continue
            survivors.append((c, rep))
        if not survivors:
            result = LaneResult(EXIT_NONE, brief.id, "no candidate survived cleanup and the budget check",
                                candidates=cands, excluded=excluded, downloaded_bytes=fr.total_bytes)
            return result
        req = JudgeRequest(
            brief_id=brief.id,
            brief_text=json.dumps(brief.model_dump(), indent=1),
            candidates=[JudgeCandidate(uid=c.uid, name=c.name, images={v: rep.renders[v] for v in VIEWS},
                                       facts={"triangles": rep.triangles, "quality": c.quality_name,
                                              "textures": len(rep.data.get("textures") or [])})
                        for c, rep in survivors],
            instructions=judge_instructions(brief),
            idempotency_key=idempotency_key or f"catalogue:{brief.id}",
        )
        jr: JudgeResult = judge.judge(req)
        if on_cost is not None:
            on_cost(jr)
        judge_info = {"pick": jr.pick, "reason": jr.reason, "cost_micros": jr.cost_micros,
                      "usage": jr.usage.to_dict(), "candidates": [c.uid for c, _ in survivors]}
        chosen = next(((c, rep) for c, rep in survivors if c.uid == jr.pick), None)
        if chosen is None:
            result = LaneResult(EXIT_NONE, brief.id, f"visual judge returned NONE: {jr.reason}"[:500],
                                candidates=cands, excluded=excluded, judge=judge_info, cost_micros=jr.cost_micros,
                                downloaded_bytes=fr.total_bytes)
            return result
        c, rep = chosen
        out_dir, files, record = _write_pick(brief, target, c, rep, jr, cfg, cands, excluded)
        result = LaneResult(EXIT_PICKED, brief.id, f"picked {c.uid} ({c.name}); awaiting owner approval",
                            pick=c.uid, out_dir=str(out_dir), files=files, candidates=cands, excluded=excluded,
                            judge=judge_info, cost_micros=jr.cost_micros, downloaded_bytes=fr.total_bytes,
                            pick_record=record)
        return result
    finally:
        deleted = sorted(str(p.relative_to(work)) for p in work.rglob("*") if p.is_file()) if work.exists() else []
        shutil.rmtree(work, ignore_errors=True)
        cleared = clear_cache(fetched_uids, cfg.cache_dir, extra_paths=cache_paths)
        if result is not None:
            result.deleted = deleted
            result.cache_cleared = cleared


def _write_pick(brief: Brief, target: Path, c: Candidate, rep: CleanupReport, jr: JudgeResult, cfg: LaneConfig,
                cands: list[dict], excluded: list[dict]) -> tuple[Path, list[str], dict]:
    out = target / "assets" / "source" / brief.id
    out.mkdir(parents=True, exist_ok=True)
    files = []
    shutil.copyfile(rep.glb, out / f"{c.uid}.glb")
    files.append(f"{c.uid}.glb")
    for v in VIEWS:
        shutil.copyfile(rep.renders[v], out / f"{v}.png")
        files.append(f"{v}.png")
    date = cfg.date()
    record = {
        "brief_id": brief.id,
        "kind": brief.kind,
        "role": brief.role,
        "uid": c.uid,
        "name": c.name,
        "artist": c.artist,
        "licence": c.licence_name,
        "source_url": c.viewer_url,
        "status": PICK_STATUS,
        "requires_human_approval": True,
        "mandatory_human_approval": brief.kind == "character_base" or brief.requires_human_approval,
        "licence_warning": LICENCE_WARNING,
        "judge_reason": jr.reason,
        "picked_on": date,
    }
    report = {
        "pick": record,
        "cleanup": rep.data,
        "budget": {"max_tris": brief.max_tris, "max_texture": brief.max_texture, "size_m": brief.size_m,
                   "problems": []},
        "candidates": cands,
        "excluded": excluded,
    }
    (out / "report.json").write_text(json.dumps(report, indent=2, default=str) + "\n")
    files.append("report.json")
    (out / "pick.json").write_text(json.dumps(record, indent=2) + "\n")
    files.append("pick.json")
    record_credit(c.uid, out, index_path=cfg.index_path, date=date)
    files.append("credits.json")
    return out, files, record


# =============================================================================== CLI


def add_run_arguments(p: argparse.ArgumentParser) -> None:
    p.add_argument("brief", help="brief JSON file (briefs/<set>/<id>.json)")
    p.add_argument("--target", required=True, help="target repository/project directory (assets/source/<id>/ is "
                                                   "written inside it)")
    p.add_argument("--task", help="asset task (ticket or root id) whose budget pays for the judge call")
    p.add_argument("--judge", help="provider route for the visual judge (default: [catalogue] judge_route), or "
                                   "fake:first / fake:NONE / fake:<uid> for an offline dry run")
    p.add_argument("--index", help="catalogue index path (default: <data_dir>/catalogue/index.sqlite)")
    p.add_argument("--blender", help="Blender binary")
    p.add_argument("--reject-list", help="JSON reject list (default: forge/lanes/reject_list.json)")


def cli_run(args: argparse.Namespace) -> int:
    from ..budget import BudgetError, BudgetLedger
    from ..config import build_providers, lane_config, load_manifest, load_project_config
    from ..providers.base import PolicyViolation, check_judge_policy
    from ..providers.fake import FakeJudge
    from ..store import Store
    from .catalogue import load_brief

    brief = load_brief(args.brief)
    pcfg = load_project_config(args.project_file)
    cfg = lane_config(pcfg, manifest=load_manifest(pcfg.data_dir), index=args.index, blender=args.blender,
                      reject_list=args.reject_list)
    judge_spec = args.judge or pcfg.catalogue.get("judge_route")
    on_cost = None
    settle: Callable[[], None] = lambda: None
    if not judge_spec:
        res = _blocked(brief, "no visual judge route: pass --judge or set [catalogue] judge_route")
        print(json.dumps(res.to_dict(), indent=2, default=str))
        return res.exit_code
    if judge_spec.startswith("fake:"):
        judge = FakeJudge(judge_spec.split(":", 1)[1] or "first")
    else:
        providers = build_providers(pcfg)
        judge = providers.get(judge_spec)
        reason = None
        if judge is None:
            reason = f"judge route {judge_spec!r} is not configured"
        else:
            try:
                check_judge_policy(judge.descriptor, pcfg.project.policy, unattended=False)
            except PolicyViolation as e:
                reason = f"project policy refuses the visual judge: {e}"
        if reason is None and not args.task:
            reason = "a paid judge call must be charged to an approved asset task: pass --task (or use `forge run`)"
        if reason is None:
            why = preflight(brief, Path(args.target), cfg, judge=judge)
            reason = why
        if reason:
            res = _blocked(brief, reason)
            print(json.dumps(res.to_dict(), indent=2, default=str))
            return res.exit_code
        store = Store(pcfg.data_dir / "forge.db")
        root = store.find_root_by_ticket(pcfg.project.id, args.task)
        if root is None:
            try:
                root = store.get_root(args.task)
            except Exception:
                res = _blocked(brief, f"no task {args.task!r} (run `forge init` and import the asset task first)")
                print(json.dumps(res.to_dict(), indent=2, default=str))
                return res.exit_code
        ledger = BudgetLedger(store)
        try:
            res_id = ledger.reserve(project_id=root.project_id, milestone_id=root.milestone_id, root_id=root.id,
                                    amount_micros=judge.judge_ceiling_micros(brief.candidates),
                                    provider=judge_spec, purpose=f"catalogue judge {brief.id} (manual run)",
                                    unattended=False)
        except BudgetError as e:
            res = _blocked(brief, f"budget: {e}")
            print(json.dumps(res.to_dict(), indent=2, default=str))
            return res.exit_code

        def on_cost(jr: JudgeResult) -> None:  # noqa: F811
            if jr.cost_micros:
                ledger.record_usage(res_id, jr.cost_micros, model=jr.usage.model, usage=jr.usage.to_dict())

        settle = lambda: ledger.settle(res_id)  # noqa: E731
    try:
        res = run_brief(brief, Path(args.target), cfg, judge, on_cost=on_cost)
    finally:
        settle()
    print(json.dumps(res.to_dict(), indent=2, default=str))
    return res.exit_code


def main(argv: list[str] | None = None) -> int:
    import os

    from ..cli import DEFAULT_PROJECT

    p = argparse.ArgumentParser(prog="python -m forge.lanes.run_catalogue", description=__doc__.split("\n")[0])
    p.add_argument("--project-file", default=os.environ.get("FORGE_PROJECT", str(DEFAULT_PROJECT)))
    add_run_arguments(p)
    return cli_run(p.parse_args(argv))


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
