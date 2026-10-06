"""Supported-template manifest (``templates/<id>/template.toml``).

A template is *data*: the modules it is built from, their dependencies, milestones, human
checkpoints, estimate ranges, the rule topics a brief must cover and the features the template
does not support. Intake, the planner and the scaffolding generator all read the same manifest,
so a template change is one reviewed file, not code scattered across services.
"""

from __future__ import annotations

import re
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

TEMPLATES_DIR = Path(__file__).resolve().parent.parent.parent / "templates"


class TemplateError(Exception):
    pass


@dataclass(frozen=True)
class Module:
    id: str
    name: str
    milestone: str
    depends_on: tuple[str, ...]
    kinds: tuple[str, ...]
    keywords: tuple[str, ...]
    paths: tuple[str, ...]
    evidence: tuple[str, ...]
    checks: tuple[str, ...]
    estimate_hours: tuple[float, float]
    cost_usd: tuple[float, float]
    per_requirement_hours: tuple[float, float]
    per_requirement_cost_usd: tuple[float, float]
    visual_review: bool
    needs_unity: bool
    scaffold: tuple[str, ...]


@dataclass(frozen=True)
class RequiredTopic:
    id: str
    question: str
    any: tuple[str, ...]


@dataclass(frozen=True)
class Unsupported:
    id: str
    pattern: re.Pattern
    reason: str


@dataclass
class Template:
    id: str
    version: str
    name: str
    description: str
    root: Path
    info: dict
    modules: list[Module]
    milestones: dict[str, dict]
    checkpoints: dict[str, str]
    required_topics: list[RequiredTopic]
    unsupported: list[Unsupported]
    module_index: dict[str, Module] = field(default_factory=dict)

    def module(self, mid: str) -> Module:
        try:
            return self.module_index[mid]
        except KeyError:
            raise TemplateError(f"template {self.id} has no module {mid!r}") from None

    def module_order(self) -> list[Module]:
        """Modules in dependency order (stable: manifest order breaks ties)."""
        order: list[Module] = []
        seen: set[str] = set()
        visiting: set[str] = set()

        def visit(m: Module) -> None:
            if m.id in seen:
                return
            if m.id in visiting:
                raise TemplateError(f"dependency cycle through module {m.id}")
            visiting.add(m.id)
            for d in m.depends_on:
                visit(self.module(d))
            visiting.discard(m.id)
            seen.add(m.id)
            order.append(m)

        for m in self.modules:
            visit(m)
        return order


def _pair(v, name: str) -> tuple[float, float]:
    if not (isinstance(v, list) and len(v) == 2 and v[0] <= v[1]):
        raise TemplateError(f"{name} must be [low, high] with low <= high, got {v!r}")
    return float(v[0]), float(v[1])


def load_template(ref: str | Path) -> Template:
    """Load a template by id (``turn-duel-2p``) or by path to its directory / ``template.toml``."""
    p = Path(ref)
    if not p.exists():
        p = TEMPLATES_DIR / str(ref)
    if p.is_dir():
        p = p / "template.toml"
    if not p.exists():
        raise TemplateError(f"no template manifest at {p}")
    raw = tomllib.loads(p.read_text())
    t = raw.get("template") or {}
    for k in ("id", "version", "name"):
        if k not in t:
            raise TemplateError(f"{p}: [template] {k} is required")
    modules = []
    for m in raw.get("modules") or []:
        modules.append(Module(
            id=m["id"], name=m.get("name", m["id"]), milestone=m.get("milestone", "M1"),
            depends_on=tuple(m.get("depends_on", [])), kinds=tuple(m.get("kinds", [])),
            keywords=tuple(k.lower() for k in m.get("keywords", [])), paths=tuple(m.get("paths", [])),
            evidence=tuple(m.get("evidence", ["rules"])), checks=tuple(m.get("checks", [])),
            estimate_hours=_pair(m.get("estimate_hours", [1, 2]), f"{m['id']}.estimate_hours"),
            cost_usd=_pair(m.get("cost_usd", [0, 1]), f"{m['id']}.cost_usd"),
            per_requirement_hours=_pair(m.get("per_requirement_hours", [0, 0]), f"{m['id']}.per_requirement_hours"),
            per_requirement_cost_usd=_pair(m.get("per_requirement_cost_usd", [0, 0]),
                                           f"{m['id']}.per_requirement_cost_usd"),
            visual_review=bool(m.get("visual_review", False)), needs_unity=bool(m.get("needs_unity", False)),
            scaffold=tuple(m.get("scaffold", [])),
        ))
    ids = [m.id for m in modules]
    if len(ids) != len(set(ids)):
        raise TemplateError(f"{p}: duplicate module ids")
    tpl = Template(
        id=t["id"], version=str(t["version"]), name=t["name"], description=t.get("description", ""),
        root=p.parent, info=t, modules=modules, milestones=dict(raw.get("milestones") or {}),
        checkpoints=dict(raw.get("checkpoints") or {}),
        required_topics=[RequiredTopic(r["id"], r["question"], tuple(w.lower() for w in r.get("any", [])))
                         for r in raw.get("required_topics") or []],
        unsupported=[Unsupported(u["id"], re.compile(u["pattern"], re.I), u["reason"])
                     for u in raw.get("unsupported") or []],
        module_index={m.id: m for m in modules},
    )
    for m in modules:
        for d in m.depends_on:
            if d not in tpl.module_index:
                raise TemplateError(f"module {m.id} depends on unknown module {d}")
        if m.milestone not in tpl.milestones:
            raise TemplateError(f"module {m.id} names unknown milestone {m.milestone}")
    tpl.module_order()  # raises on cycles
    return tpl
