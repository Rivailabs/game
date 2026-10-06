"""Initial asset budgets for the Astra template (plan: "Initial asset budgets for the Astra template").

These are the plan's proposed production ceilings, linked to the reference phone. They make a brief
concrete; the physical-phone frame-time and memory gates remain the acceptance authority, so an
asset under its triangle ceiling can still fail on transparency, materials, animation or textures.

| Asset or resource            | Proposed starting ceiling                                                         |
|------------------------------|-----------------------------------------------------------------------------------|
| Archer                       | 8,000 triangles, 50 deforming bones, four weights per vertex, at most two materials |
| Bow and arrow                | 1,500 triangles per bow and 200 per arrow                                         |
| Visible arena                | 40,000 triangles; at most 16 opaque material batches                              |
| Total visible geometry       | 70,000 triangles in the initial worst-case scene                                  |
| Draw calls                   | 60 in the declared representative combat scene                                    |
| Character and arena textures | Normally at most 1,024 px per dimension; larger shared UI atlases need measured approval |
| Effects                      | At most 12 active emitters and 64 live particles per emitter                      |
| Audio                        | Short mono effects where appropriate and streamed music when qualified            |
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional

DEFAULT_MAX_TEXTURE_PX = 1024


@dataclass(frozen=True)
class AssetBudget:
    name: str
    max_triangles: Optional[int] = None
    max_deforming_bones: Optional[int] = None
    max_weights_per_vertex: Optional[int] = None
    max_materials: Optional[int] = None
    max_opaque_material_batches: Optional[int] = None
    max_texture_px: int = DEFAULT_MAX_TEXTURE_PX
    acceptance: str = ""


BUDGETS: dict[str, AssetBudget] = {
    "archer": AssetBudget("archer", max_triangles=8_000, max_deforming_bones=50, max_weights_per_vertex=4,
                          max_materials=2,
                          acceptance="Two equipped characters deform and animate correctly in the representative "
                                     "combat scene"),
    "bow": AssetBudget("bow", max_triangles=1_500,
                       acceptance="Grip, projectile silhouette and string attachment remain clear at the gameplay "
                                  "camera"),
    "arrow": AssetBudget("arrow", max_triangles=200,
                         acceptance="Projectile silhouette remains clear at the gameplay camera"),
    "arena": AssetBudget("arena", max_triangles=40_000, max_opaque_material_batches=16,
                         acceptance="Baked/simple lighting baseline; no automatic addition of realtime "
                                    "shadow-casting lights"),
}


@dataclass(frozen=True)
class SceneBudget:
    total_visible_triangles: int = 70_000
    draw_calls: int = 60
    max_active_emitters: int = 12
    max_particles_per_emitter: int = 64


SCENE_BUDGET = SceneBudget()


@dataclass
class BudgetLine:
    metric: str
    value: Optional[float]
    ceiling: Optional[float]
    ok: bool
    note: str = ""

    def to_dict(self) -> dict:
        return dict(self.__dict__)


def budget_for(key: str | None, *, max_triangles: int | None = None, max_texture_px: int | None = None,
               max_materials: int | None = None) -> AssetBudget:
    """A plan budget by key, or a brief-specific one (props are not in the plan table)."""
    base = BUDGETS.get(key or "") or AssetBudget(key or "custom")
    return AssetBudget(base.name, max_triangles if max_triangles is not None else base.max_triangles,
                       base.max_deforming_bones, base.max_weights_per_vertex,
                       max_materials if max_materials is not None else base.max_materials,
                       base.max_opaque_material_batches,
                       max_texture_px if max_texture_px is not None else base.max_texture_px, base.acceptance)


def check_asset(report: dict, budget: AssetBudget) -> list[BudgetLine]:
    """Compare a Blender technical report against an asset budget."""
    lines: list[BudgetLine] = []

    def add(metric: str, value, ceiling, note: str = "") -> None:
        if ceiling is None:
            return
        ok = value is not None and value <= ceiling
        lines.append(BudgetLine(metric, value, ceiling, ok, note if ok or value is not None else "not reported"))

    add("triangles", report.get("triangles"), budget.max_triangles)
    add("deforming bones", report.get("deforming_bones"), budget.max_deforming_bones)
    add("weights per vertex", report.get("max_weights_per_vertex"), budget.max_weights_per_vertex)
    add("materials", len(report.get("materials") or []), budget.max_materials)
    add("opaque material batches", report.get("opaque_material_batches",
                                              sum(1 for m in report.get("materials") or []
                                                  if m.get("surface", "opaque") == "opaque")),
        budget.max_opaque_material_batches)
    for t in report.get("textures") or []:
        edge = max(int(t.get("width", 0)), int(t.get("height", 0)))
        approved = bool(t.get("measured_approval"))
        lines.append(BudgetLine(f"texture {t.get('name')}", edge, budget.max_texture_px,
                                edge <= budget.max_texture_px or approved,
                                "larger than default; measured approval recorded" if approved and
                                edge > budget.max_texture_px else ""))
    return lines


@dataclass
class SceneMeasurement:
    """Counters from the running build (the integration report), named exactly as the engine reports them."""

    visible_triangles: int
    draw_calls: int
    draw_call_counter: str  # e.g. "Unity FrameTimingManager / ProfilerRecorder 'Draw Calls Count'"
    active_emitters: int = 0
    max_live_particles_per_emitter: int = 0
    frame_time_ms_p95: Optional[float] = None
    notes: list[str] = field(default_factory=list)


def check_scene(m: SceneMeasurement, budget: SceneBudget = SCENE_BUDGET) -> list[BudgetLine]:
    if not m.draw_call_counter:
        raise ValueError("name the draw-call counter; draw calls are not batches")
    return [
        BudgetLine("total visible triangles", m.visible_triangles, budget.total_visible_triangles,
                   m.visible_triangles <= budget.total_visible_triangles),
        BudgetLine("draw calls", m.draw_calls, budget.draw_calls, m.draw_calls <= budget.draw_calls,
                   f"counter: {m.draw_call_counter}"),
        BudgetLine("active emitters", m.active_emitters, budget.max_active_emitters,
                   m.active_emitters <= budget.max_active_emitters),
        BudgetLine("live particles per emitter", m.max_live_particles_per_emitter, budget.max_particles_per_emitter,
                   m.max_live_particles_per_emitter <= budget.max_particles_per_emitter),
    ]


def all_ok(lines: list[BudgetLine]) -> bool:
    return all(line.ok for line in lines)
