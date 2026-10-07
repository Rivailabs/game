"""Balance and replay report lane: runs the Astra bot simulator and screens its results.

The simulator (``astra-kingdoms/tools/AstraKingdoms.Sim``) plays mirrored bot-policy matches through
the authoritative rules engine and writes ``<name>.csv`` / ``<name>.md``. This lane:

1. runs it (``dotnet run -c Release --project <sim> -- --matches N --seed S --out DIR --name NAME``);
   no dotnet -> **BLOCKED**, never a pass;
2. runs it a second time with the same seed and compares the CSV bytes: identical output is the
   replay/determinism evidence; a difference is a **FAIL**;
3. screens the CSV: statistically clear outliers (the Wilson 95% interval lies entirely outside the
   neutral band) are flagged for the owner; a requirement-derived limit (``max_share``, e.g. "no rune
   type wins more than 55%") is a **FAIL** only when the interval's lower bound is above the limit.

These are bot-policy screening results, not balance proof (the simulator's own report says so).
Thresholds are owner settings; nothing here changes a rule or a constant.
"""

from __future__ import annotations

import csv
import hashlib
import io
import shutil
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional, Protocol

from ..credentials import scrubbed_env
from .base import LaneReport

CAVEAT = "Bot-policy screening results, not balance proof: humans choose differently."


@dataclass
class SimRun:
    returncode: int
    csv_path: Optional[Path]
    md_path: Optional[Path]
    stdout: str = ""
    blocked: str = ""


class SimRunner(Protocol):
    def run(self, out_dir: Path, name: str, *, matches: int, seed: int, catalog: str) -> SimRun: ...


@dataclass
class DotnetSimRunner:
    sim_project: Path
    dotnet: str = "dotnet"
    configuration: str = "Release"
    timeout_s: float = 1800
    threads: Optional[int] = None

    def run(self, out_dir: Path, name: str, *, matches: int, seed: int, catalog: str) -> SimRun:
        exe = shutil.which(self.dotnet)
        if exe is None:
            return SimRun(127, None, None, blocked="dotnet SDK not found")
        if not Path(self.sim_project).exists():
            return SimRun(127, None, None, blocked=f"simulator project not found: {self.sim_project}")
        argv = [exe, "run", "-c", self.configuration, "--project", str(self.sim_project), "--",
                "--matches", str(matches), "--seed", str(seed), "--catalog", catalog,
                "--out", str(out_dir), "--name", name]
        if self.threads:
            argv += ["--threads", str(self.threads)]
        p = subprocess.run(argv, capture_output=True, text=True, timeout=self.timeout_s, env=scrubbed_env())
        csv_p, md_p = Path(out_dir) / f"{name}.csv", Path(out_dir) / f"{name}.md"
        return SimRun(p.returncode, csv_p if csv_p.exists() else None, md_p if md_p.exists() else None,
                      stdout=(p.stdout + p.stderr)[-4000:])


@dataclass
class Thresholds:
    neutral_low: float = 0.45
    neutral_high: float = 0.55
    #: Requirement-derived ceiling for any single item's decisive win share (None = not specified).
    max_share: Optional[float] = None
    max_share_metrics: tuple[str, ...] = ("weapon_duel", "element_total")
    requirement_id: Optional[str] = None


@dataclass
class Row:
    metric: str
    group: str
    key: str
    n: int
    wins: Optional[int]
    losses: Optional[int]
    share: Optional[float]
    lo: Optional[float]
    hi: Optional[float]


def parse_csv(text: str) -> list[Row]:
    def num(v, f=float):
        return f(v) if v not in ("", None) else None

    out = []
    for r in csv.DictReader(io.StringIO(text)):
        out.append(Row(r["metric"], r["group"], r["key"], int(r["n"] or 0), num(r.get("wins"), int),
                       num(r.get("losses"), int), num(r.get("win_share")), num(r.get("wilson_lo")),
                       num(r.get("wilson_hi"))))
    return out


@dataclass
class BalanceResult:
    report: LaneReport
    rows: list[Row] = field(default_factory=list)


def screen(rows: list[Row], th: Thresholds, rep: LaneReport) -> None:
    for r in rows:
        if r.lo is None or r.hi is None:
            continue
        label = f"{r.metric}/{r.group}/{r.key}"
        if r.metric in ("first_attacker", "weapon_duel", "weapon_volley", "element_total", "terrain_defender"):
            if r.lo > th.neutral_high or r.hi < th.neutral_low:
                rep.add("warning", "outlier", f"{label}: win share {r.share:.3f} (95% {r.lo:.3f}-{r.hi:.3f}, n={r.n}) "
                        f"is outside {th.neutral_low:.2f}-{th.neutral_high:.2f}", label)
        if r.metric == "policy_pairing" and r.hi < 0.5:
            rep.add("warning", "difficulty_order", f"{label}: the stronger policy does not win more often "
                    f"({r.share:.3f})", label)
        if th.max_share is not None and r.metric in th.max_share_metrics and r.lo > th.max_share:
            rep.add("error", "requirement_limit", f"{label}: lower bound {r.lo:.3f} exceeds the required maximum "
                    f"{th.max_share:.2f}" + (f" ({th.requirement_id})" if th.requirement_id else ""), label)


def run_balance(runner: SimRunner, out_dir: str | Path, *, matches: int = 600, seed: int = 20261006,
                catalog: str = "full", thresholds: Thresholds | None = None, name: str = "balance") -> BalanceResult:
    th = thresholds or Thresholds()
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    rep = LaneReport(lane="balance", evidence_class="replay",
                     human_checkpoints=["owner reviews flagged outliers and decides on human playtests; "
                                        "a balance change is a separate, approved rules task"])
    first = runner.run(out / "run1", name, matches=matches, seed=seed, catalog=catalog)
    if first.blocked:
        rep.status = "BLOCKED"
        rep.add("error", "blocked", first.blocked)
        return BalanceResult(rep.finalise(f"balance: BLOCKED ({first.blocked})"))
    if first.returncode != 0 or first.csv_path is None:
        rep.add("error", "sim_failed", f"simulator exited {first.returncode}: {first.stdout[-500:]}")
        return BalanceResult(rep.finalise())
    second = runner.run(out / "run2", name, matches=matches, seed=seed, catalog=catalog)
    csv1 = first.csv_path.read_bytes()
    h1 = hashlib.sha256(csv1).hexdigest()
    h2 = hashlib.sha256(second.csv_path.read_bytes()).hexdigest() if second.csv_path else None
    if second.returncode != 0 or h2 is None:
        rep.status = "INCOMPLETE"
        rep.add("error", "replay_incomplete", "second (replay) run did not produce a CSV")
    elif h1 != h2:
        rep.add("error", "nondeterministic", "same seed produced different results: replay is not deterministic")
    rows = parse_csv(csv1.decode("utf-8"))
    if not rows:
        rep.add("error", "empty_report", "the simulator CSV has no rows")
    screen(rows, th, rep)
    rep.details = {"caveat": CAVEAT, "matches": matches, "seed": seed, "catalog": catalog, "csv_sha256": h1,
                   "replay_csv_sha256": h2, "deterministic": h1 == h2, "csv": str(first.csv_path),
                   "markdown": str(first.md_path) if first.md_path else None,
                   "thresholds": {"neutral": [th.neutral_low, th.neutral_high], "max_share": th.max_share,
                                  "requirement": th.requirement_id}}
    return BalanceResult(rep.finalise(), rows)


def verify_report(path: str | Path) -> LaneReport:
    """Protected check: re-read a written balance report; PASS only for a deterministic, error-free run."""
    import json

    p = Path(path)
    rep = LaneReport(lane="balance", evidence_class="replay")
    if not p.exists():
        rep.add("error", "missing", f"{p} not found")
        return rep.finalise()
    d = json.loads(p.read_text())
    status = d.get("status")
    if status != "PASS":
        if status in ("BLOCKED", "INCOMPLETE"):
            rep.status = status
        rep.add("error", "report_status", f"balance report status is {status}")
    if not d.get("details", {}).get("deterministic"):
        rep.add("error", "not_deterministic", "the report does not show a deterministic replay")
    return rep.finalise()
