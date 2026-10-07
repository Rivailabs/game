"""Route table, territory/licence gates and hardware qualification (no GPU, no network)."""

from __future__ import annotations

import pytest

from forge.assets.routes import (
    ROUTES,
    AssetLaneKind as L,
    Benchmark,
    GpuSlot,
    LicenceGate,
    PermissionRecord,
    RouteContext,
    RouteRefused,
    evaluate_route,
    select_route,
)
from forge.assets.territory import EU_MEMBERS, excluded_overlap, expand


def gate(route: str, public_demo: bool = True) -> LicenceGate:
    return LicenceGate(route, f"{route} terms", "2026-10", "owner may use outputs commercially", "IN only",
                       public_demo, "owner", 1.0)


def gpu(name="RTX 4090", vram=24.0, worker="w1", uuid=None, remote="") -> GpuSlot:
    return GpuSlot(worker, uuid or f"GPU-{name}-{worker}", name, vram, vram - 1, remote_provider=remote)


def ctx(**kw) -> RouteContext:
    base = dict(distribution_countries=["IN"], gpus=[gpu()], licence_gates={r: gate(r) for r in ROUTES},
                certified={"meshy", "tripo", "deepmotion"}, allowed_vendors=["meshy", "tripo", "deepmotion"],
                allowed_data_classes=["prompt", "reference_image", "mesh", "video"])
    base.update(kw)
    return RouteContext(**base)


def test_route_table_matches_plan_requirements():
    h = ROUTES["hunyuan3d-2.1"]
    assert {(s.stage, s.vram_gb) for s in h.stages} == {("shape", 10), ("texture", 21), ("shape+texture", 29)}
    assert set(h.licence.excluded_territories) == {"EU", "UK", "KR"}
    assert ROUTES["trellis"].stage(None).vram_gb == 16
    assert ROUTES["trellis2"].stage(None).vram_gb == 24 and ROUTES["trellis2"].os_support == ("linux",)
    hy = ROUTES["hy-motion-1.0"]
    assert {(s.stage, s.vram_gb) for s in hy.stages} == {("lite", 24), ("full", 26)}
    assert set(hy.licence.excluded_territories) == {"EU", "UK", "KR"}
    assert hy.extra_stages == ("loop_cleanup", "root_motion_conversion")
    assert ROUTES["gvhmr"].licence.excluded_entirely
    assert ROUTES["mixamo"].manual_step and ROUTES["mixamo"].kind == "manual"
    assert ROUTES["unirig"].stage(None).vram_gb is None and ROUTES["unirig"].benchmark_required
    assert ROUTES["flux-schnell-local"].benchmark_required
    for r in ROUTES.values():
        assert r.data_destinations, r.id
        assert r.billing, r.id
        assert r.plan_decision, r.id


def test_territory_expansion():
    assert expand(["EU"])[0] == set(EU_MEMBERS)
    assert expand(["WW"])[1] is True
    assert excluded_overlap(["IN"], ["EU", "UK", "KR"]) == []
    assert excluded_overlap(["IN", "DE"], ["EU", "UK", "KR"]) == ["EU"]
    assert excluded_overlap(["GB"], ["EU", "UK", "KR"]) == ["UK"]
    assert excluded_overlap(["WW"], ["EU", "UK", "KR"]) == ["EU", "UK", "KR"]


@pytest.mark.parametrize("countries", [["DE"], ["GB"], ["KR"], ["WW"], ["IN", "FR"]])
def test_hunyuan_and_hy_motion_refused_for_eu_uk_kr_distribution(countries):
    c = ctx(distribution_countries=countries, gpus=[gpu("A100", 40.0)])
    for rid, lane, stage in (("hunyuan3d-2.1", L.MESH, "shape+texture"), ("hy-motion-1.0", L.MOTION, "full")):
        d = evaluate_route(rid, lane, c, stage=stage)
        assert not d.allowed and any("licence excludes" in r for r in d.reasons), d.reasons


def test_territory_permission_lifts_restriction_and_india_only_is_allowed():
    c = ctx(distribution_countries=["IN"], gpus=[gpu("A100", 40.0)])
    assert evaluate_route("hunyuan3d-2.1", L.MESH, c, stage="shape+texture").allowed
    c = ctx(distribution_countries=["DE"], gpus=[gpu("A100", 40.0)],
            permissions=[PermissionRecord("hunyuan3d-2.1", "territory", "signed agreement #42")])
    assert evaluate_route("hunyuan3d-2.1", L.MESH, c, stage="shape+texture").allowed


def test_gvhmr_excluded_unless_permission_and_dependencies_cleared():
    c = ctx(benchmarks=[Benchmark("gvhmr", "default", "w1", "RTX 4090", 24, 10, 60, True)])
    d = evaluate_route("gvhmr", L.MOTION, c)
    assert not d.allowed and any("excluded by its licence" in r for r in d.reasons)
    c.permissions = [PermissionRecord("gvhmr", "commercial_use", "email from authors", dependency_rights_cleared=False)]
    assert not evaluate_route("gvhmr", L.MOTION, c).allowed
    c.permissions = [PermissionRecord("gvhmr", "commercial_use", "signed licence", dependency_rights_cleared=True)]
    assert evaluate_route("gvhmr", L.MOTION, c).allowed


def test_vram_is_never_pooled_and_published_requirements_apply():
    two_small = ctx(gpus=[gpu("RTX 3060", 12.0, uuid="a"), gpu("RTX 3060", 12.0, uuid="b")])
    d = evaluate_route("trellis2", L.MESH, two_small)
    assert not d.allowed and any("12.0 GB < 24" in r for r in d.reasons)
    t4 = ctx(gpus=[gpu("Tesla T4", 16.0)])
    # TRELLIS: 16 GB is published, but a T4-sized memory alone does not establish compatibility
    d = evaluate_route("trellis", L.MESH, t4)
    assert not d.allowed and any("no successful benchmark" in r for r in d.reasons)
    t4.benchmarks = [Benchmark("trellis", "default", "w1", "Tesla T4", 16.0, 15.1, 300, True)]
    d = evaluate_route("trellis", L.MESH, t4)
    assert d.allowed and d.gpu.gpu_model == "Tesla T4" and "benchmark" in d.qualification
    # a 24 GB card does not meet the 26 GB full HY-Motion profile or the 29 GB combined Hunyuan profile
    c = ctx(gpus=[gpu("RTX 4090", 24.0)])
    assert evaluate_route("hy-motion-1.0", L.MOTION, c, stage="lite").allowed
    assert not evaluate_route("hy-motion-1.0", L.MOTION, c, stage="full").allowed
    assert not evaluate_route("hunyuan3d-2.1", L.MESH, c, stage="shape+texture").allowed
    assert evaluate_route("hunyuan3d-2.1", L.MESH, c, stage="shape").allowed


def test_unpublished_requirement_needs_benchmark():
    c = ctx(gpus=[gpu("RTX 4070", 12.0)])
    d = evaluate_route("unirig", L.RIG, c)
    assert not d.allowed and any("benchmark required" in r for r in d.reasons)
    c.benchmarks = [Benchmark("unirig", "default", "w1", "RTX 4070", 12.0, 9.5, 120, True)]
    assert evaluate_route("unirig", L.RIG, c).qualification == "benchmarked configuration"
    c.benchmarks = [Benchmark("unirig", "default", "w1", "RTX 4070", 12.0, 9.5, 120, False)]
    assert not evaluate_route("unirig", L.RIG, c).allowed


def test_first_use_licence_gate_manual_and_certification():
    c = ctx(licence_gates={})
    d = evaluate_route("meshy", L.MESH, c)
    assert not d.allowed and any("first-use licence gate" in r for r in d.reasons)
    c = ctx()
    assert not evaluate_route("mixamo", L.RIG, c).allowed  # manual in unattended mode
    c.unattended = False
    c.allowed_vendors.append("adobe")
    assert evaluate_route("mixamo", L.RIG, c).allowed
    c = ctx(certified=set())
    d = evaluate_route("tripo", L.MESH, c)
    assert not d.allowed and any("not certified" in r for r in d.reasons)
    c = ctx(allowed_vendors=["tripo"])
    assert any("vendor meshy" in r for r in evaluate_route("meshy", L.MESH, c).reasons)
    c = ctx(public_demo=True, licence_gates={"meshy": gate("meshy", public_demo=False)})
    assert any("public demos" in r for r in evaluate_route("meshy", L.MESH, c).reasons)
    assert any("distribution countries" in r for r in evaluate_route("meshy", L.MESH,
                                                                     ctx(distribution_countries=[])).reasons)


def test_remote_gpu_provider_is_a_data_destination():
    c = ctx(gpus=[gpu("A100", 80.0, remote="azure")],
            benchmarks=[Benchmark("trellis2", "default", "w1", "A100", 80.0, 30.0, 100, True)])
    d = evaluate_route("trellis2", L.MESH, c, stage=None)
    assert any("GPU provider azure" in r for r in d.reasons)


def test_select_route_follows_preferences_and_reports_all_refusals():
    c = ctx(distribution_countries=["WW"], gpus=[gpu("RTX 4090", 24.0)])
    c.benchmarks = [Benchmark("trellis2", "default", "w1", "RTX 4090", 24.0, 22.0, 200, True)]
    d = select_route(L.MESH, ["hunyuan3d-2.1", "trellis2", "meshy"], c)
    assert d.route == "trellis2"
    with pytest.raises(RouteRefused) as ei:
        select_route(L.MOTION, ["hy-motion-1.0", "gvhmr"], c)
    msg = str(ei.value)
    assert "hy-motion-1.0" in msg and "gvhmr" in msg
