"""Bounded parallelism planner.

Shows the owner how a backlog can run concurrently *within declared limits* (plan: "Budget
reservations recovery and resource scheduling"; "Workspace isolation"):

* at most ``max_parallel`` active candidate tasks;
* resource counts per kind (build workspaces, Unity editors, test phones, GPUs). Begin with one
  of each scarce resource; raise only after measurements justify it;
* tasks whose permitted paths overlap never run at the same time (shared scenes, prefab roots
  and settings have one owner at a time);
* a task starts only when all its dependencies have finished;
* integration stays serial (one integration writer) and is not modelled as parallel work.

The result is a proposed timeline using the *high* estimate of each task, plus the reason each
waiting task waited. It is a planning aid, not a promise of wall-clock time.
"""

from __future__ import annotations

import heapq
from dataclasses import dataclass, field

from .planner import Backlog


@dataclass
class SchedTask:
    id: str
    hours: float
    depends_on: list[str] = field(default_factory=list)
    resources: dict[str, int] = field(default_factory=dict)
    paths: list[str] = field(default_factory=list)


@dataclass
class Capacity:
    max_parallel: int = 2
    resources: dict[str, int] = field(default_factory=lambda: {"workspace": 2, "unity": 1, "device": 1, "gpu": 1})


@dataclass
class Slot:
    task: str
    start: float
    end: float


@dataclass
class Schedule:
    slots: list[Slot]
    makespan: float
    peak_parallel: int
    waits: dict[str, list[str]]
    serial_hours: float

    def slot(self, task: str) -> Slot:
        return next(s for s in self.slots if s.task == task)

    def markdown(self) -> str:
        out = [f"Makespan {self.makespan:g} h (serial {self.serial_hours:g} h), peak parallel {self.peak_parallel}.",
               "", "| Task | Start h | End h | Waited for |", "|---|---:|---:|---|"]
        for s in sorted(self.slots, key=lambda s: (s.start, s.task)):
            out.append(f"| {s.task} | {s.start:g} | {s.end:g} | {'; '.join(self.waits.get(s.task, [])) or '-'} |")
        return "\n".join(out) + "\n"


class ScheduleError(Exception):
    pass


def _prefix(glob: str) -> str:
    cut = len(glob)
    for ch in "*?[":
        i = glob.find(ch)
        if i != -1:
            cut = min(cut, i)
    return glob[:cut]


def paths_overlap(a: list[str], b: list[str]) -> bool:
    for x in a:
        for y in b:
            px, py = _prefix(x), _prefix(y)
            if px.startswith(py) or py.startswith(px):
                return True
    return False


def from_backlog(backlog: Backlog) -> list[SchedTask]:
    out = []
    for t in backlog.tasks:
        res = {"workspace": 1}
        if t.needs_unity:
            res["unity"] = 1
        if t.needs_device:
            res["device"] = 1
        out.append(SchedTask(t.ticket, t.estimate_hours[1], list(t.depends_on), res, list(t.permitted_paths)))
    return out


def schedule(tasks: list[SchedTask], capacity: Capacity) -> Schedule:
    if capacity.max_parallel < 1:
        raise ScheduleError("max_parallel must be at least 1")
    by_id = {t.id: t for t in tasks}
    for t in tasks:
        for d in t.depends_on:
            if d not in by_id:
                raise ScheduleError(f"{t.id} depends on unknown task {d}")
        for k, n in t.resources.items():
            if n > capacity.resources.get(k, 0):
                raise ScheduleError(f"{t.id} needs {n} {k} but capacity has {capacity.resources.get(k, 0)}")

    # Priority: longest remaining path first (classic list scheduling), then id for determinism.
    succ: dict[str, list[str]] = {t.id: [] for t in tasks}
    for t in tasks:
        for d in t.depends_on:
            succ[d].append(t.id)
    rank: dict[str, float] = {}

    def tail(tid: str, stack: frozenset = frozenset()) -> float:
        if tid in stack:
            raise ScheduleError(f"dependency cycle through {tid}")
        if tid not in rank:
            rank[tid] = by_id[tid].hours + max((tail(s, stack | {tid}) for s in succ[tid]), default=0.0)
        return rank[tid]

    for t in tasks:
        tail(t.id)

    now = 0.0
    done: set[str] = set()
    running: list[tuple[float, str]] = []  # heap of (end, id)
    in_use: dict[str, int] = {}
    slots: dict[str, Slot] = {}
    waits: dict[str, list[str]] = {}
    peak = 0
    pending = set(by_id)
    while pending or running:
        ready = sorted((tid for tid in pending if all(d in done for d in by_id[tid].depends_on)),
                       key=lambda x: (-rank[x], x))
        for tid in ready:
            t = by_id[tid]
            reason = None
            if len(running) >= capacity.max_parallel:
                reason = f"parallel limit {capacity.max_parallel}"
            else:
                for k, n in t.resources.items():
                    if in_use.get(k, 0) + n > capacity.resources.get(k, 0):
                        reason = f"{k} busy"
                        break
            if reason is None:
                clash = next((rid for _, rid in running if paths_overlap(t.paths, by_id[rid].paths)), None)
                if clash:
                    reason = f"path overlap with {clash}"
            if reason:
                w = waits.setdefault(tid, [])
                if reason not in w:
                    w.append(reason)
                continue
            for k, n in t.resources.items():
                in_use[k] = in_use.get(k, 0) + n
            heapq.heappush(running, (now + t.hours, tid))
            slots[tid] = Slot(tid, round(now, 3), round(now + t.hours, 3))
            pending.discard(tid)
            peak = max(peak, len(running))
        if not running:
            if pending:
                raise ScheduleError(f"cannot schedule {sorted(pending)}")
            break
        end, tid = heapq.heappop(running)
        now = end
        done.add(tid)
        for k, n in by_id[tid].resources.items():
            in_use[k] -= n
        while running and running[0][0] <= now:  # finish everything ending at the same moment
            _, other = heapq.heappop(running)
            done.add(other)
            for k, n in by_id[other].resources.items():
                in_use[k] -= n
    return Schedule(slots=[slots[t.id] for t in tasks], makespan=round(now, 3), peak_parallel=peak, waits=waits,
                    serial_hours=round(sum(t.hours for t in tasks), 3))
