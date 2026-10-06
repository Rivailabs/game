"""Catalogue lane: filters, index, search, fetch, credits, guards, briefs. Fake index and downloader only."""

from __future__ import annotations

import json
import socket
import sys
from pathlib import Path

import pytest
from pydantic import ValidationError

from forge.lanes import catalogue as cat
from forge.lanes import catalogue_fields as F
from forge.lanes.asset_tools import BlenderAdapter, budget_check, find_blender
from forge.lanes.run_catalogue import EXIT_BLOCKED, EXIT_NONE, EXIT_PICKED, run_brief
from forge.providers import FakeJudge

from .catalogue_fakes import (
    FakeBlenderRunner,
    FakeDownloader,
    ann,
    bow_brief,
    build_index,
    lane_config,
    q,
)

ROOT = Path(__file__).resolve().parent.parent
BRIEFS = ROOT / "briefs" / "astra_v1"


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    def refuse(*a, **k):
        raise AssertionError("network access attempted in a catalogue test")

    monkeypatch.setattr(socket.socket, "connect", refuse)
    monkeypatch.setattr(socket, "create_connection", refuse)
    monkeypatch.setitem(sys.modules, "objaverse", None)  # never the real package
    monkeypatch.setitem(sys.modules, "datasets", None)


@pytest.fixture
def index(tmp_path):
    p = tmp_path / "index.sqlite"
    build_index(p)
    return p


# ------------------------------------------------------------------ licence filter

@pytest.mark.parametrize("value,expected", [
    ("by", "by"), ("BY", "by"), (" by ", "by"), ("cc0", "cc0"), ("CC0", "cc0"),
    ({"label": "CC Attribution"}, "by"), ({"label": "CC0 Public Domain"}, "cc0"), ("CC Attribution", "by"),
    ("by-nc", None), ("by-nc-sa", None), ("by-sa", None), ("by-nd", None), ("by-nc-nd", None),
    (None, None), ("", None), ("   ", None), ({}, None), ({"label": ""}, None),
    ({"label": "CC Attribution-NonCommercial"}, None), ({"label": "CC Attribution-ShareAlike"}, None),
    ({"slug": "by"}, None), (["by"], None), (1, None), ("cc-by", None), ("public domain", None),
])
def test_licence_filter_accepts_only_cc_by_and_cc0(value, expected):
    assert cat.normalise_licence(value) == expected


def test_index_drops_rejected_licences_and_records_reasons(tmp_path, index):
    stats = cat.load_index(index)  # cached
    assert stats.cached
    uids = _uids(index)
    assert {"bow001", "bow003"} <= uids
    assert not {"bow004", "bow005"} & uids  # by-nc, by-sa
    assert stats.rejected["licence"] == 2
    # missing licence
    p = tmp_path / "i2.sqlite"
    s = build_index(p, {"x1": ann("x1", "Bow", licence=None), "x2": ann("x2", "Bow", licence="")}, [q("x1"), q("x2")])
    assert s.objects == 0 and s.rejected["licence"] == 2
    entry = cat.get_entry(index, "bow003")
    assert entry["licence"] == "cc0" and entry["licence_name"] == "CC0"


def _uids(index):
    import sqlite3

    with sqlite3.connect(index) as c:
        return {r[0] for r in c.execute("SELECT uid FROM objects")}


# ------------------------------------------------------------------ quality filter

@pytest.mark.parametrize("row,kept", [
    (q("a", score=2), True), (q("a", score=3), True), (q("a", score="3"), True), (q("a", score="High"), True),
    (q("a", score=1), False), (q("a", score=0), False), (q("a", score=None), False), (q("a", score=True), False),
    (q("a", score="x"), False), (q("a", score=7), False),
    (q("a", multi=True), False), (q("a", multi="true"), False), (q("a", multi=1), False), (q("a", multi="1"), False),
    (q("a", multi=False), True), (q("a", multi=0), True), (q("a", multi="False"), True), (q("a", multi=0.0), True),
    (q("a", scene="true"), False), (q("a", scene=1), False), (q("a", scene=True), False),
    (q("a", single="true"), False), (q("a", single=True), False), (q("a", single=1), False),
    (q("a", multi="maybe"), False), (q("a", scene=None), False), (q("a", single=""), False),
    ({"score": 3, "is_multi_object": False, "is_scene": False, "is_single_color": False}, False),  # no UID
])
def test_quality_filter(row, kept):
    res, why = cat.quality_verdict(row)
    assert (res is not None) is kept, why


def test_index_quality_and_texture_proxy(tmp_path):
    anns = {
        "hi": ann("hi", "Bow"), "sup": ann("sup", "Bow"), "med": ann("med", "Bow"), "multi": ann("multi", "Bow"),
        "scene": ann("scene", "Bow"), "mono": ann("mono", "Bow"), "notex": ann("notex", "Bow", textures=0),
        "untagged": ann("untagged", "Bow"), "age": ann("age", "Bow", age=True),
        "nocount": ann("nocount", "Bow", archives={"glb": {"size": 10}}),
    }
    quality = [q("hi", 2), q("sup", "3"), q("med", 1), q("multi", multi="true"), q("scene", scene=1),
               q("mono", single=True), q("notex"), q("age"), q("nocount")]
    s = build_index(tmp_path / "i.sqlite", anns, quality)
    assert _uids(tmp_path / "i.sqlite") == {"hi", "sup"}
    assert s.rejected["quality: Medium"] == 1
    assert s.rejected["quality: not a single object"] == 1
    assert s.rejected["quality: scene"] == 1
    assert s.rejected["quality: single colour (untextured)"] == 1
    assert s.rejected["texture: glb has no textures"] == 1
    assert s.rejected["texture: unknown glb textureCount"] == 1
    assert s.rejected["age restricted"] == 1
    assert s.rejected["quality: no High/Superior Objaverse++ entry"] >= 2  # untagged + med etc.
    assert cat.get_entry(tmp_path / "i.sqlite", "sup")["quality_name"] == "Superior"


def test_index_accepts_iterables_callables_and_quality_files(tmp_path):
    rows = [ann("a1", "Clay pot", ["pottery"]), ann("a2", "Bronze bell")]
    qfile = tmp_path / "opp.csv"
    qfile.write_text("UID,score,is_multi_object,is_scene,is_single_color,is_transparent\n"
                     "a1,3,False,False,False,False\na2,2,0,0,1,0\n")
    s = cat.load_index(tmp_path / "i.sqlite", annotations=lambda: rows, quality_file=qfile)
    assert s.objects == 1 and _uids(tmp_path / "i.sqlite") == {"a1"}
    jl = tmp_path / "opp.jsonl"
    jl.write_text(json.dumps(q("a2")) + "\n")
    s = cat.load_index(tmp_path / "j.sqlite", annotations=iter(rows), quality=lambda: cat._read_quality_file(jl))
    assert _uids(tmp_path / "j.sqlite") == {"a2"}


def test_missing_packages_block_clearly(tmp_path):
    with pytest.raises(cat.CatalogueBlocked, match="datasets"):
        cat.load_index(tmp_path / "i.sqlite", annotations={})
    with pytest.raises(cat.CatalogueBlocked, match="objaverse"):
        cat.load_index(tmp_path / "i.sqlite", quality=[])
    assert not (tmp_path / "i.sqlite").exists() and not (tmp_path / "i.sqlite.tmp").exists()
    with pytest.raises(cat.CatalogueBlocked, match="objaverse"):
        cat.default_downloader(["x"])


def test_field_mapping_module_is_the_single_source():
    assert F.OPP_UID == "UID" and F.ANN_LICENSE == "license" and F.ANN_VIEWER_URL == "viewerUrl"
    assert set(F.LICENCE_ACCEPT) == {"by", "cc0"}
    assert F.OPP_KEEP_SCORES == {2, 3} and F.OPP_SCORE_NAMES[3] == "Superior"
    src = (ROOT / "forge" / "lanes" / "catalogue.py").read_text()
    for literal in ('"viewerUrl"', '"isAgeRestricted"', '"textureCount"', '"is_multi_object"', '"UID"'):
        assert literal not in src, f"{literal} must only be spelled in catalogue_fields.py"


# ------------------------------------------------------------------ reject list and search

def test_reject_list_matches_names_and_tags_whole_words_case_insensitive():
    terms = cat.load_reject_list()
    assert "pokemon" in terms and "star wars" in terms and "coca cola" in terms
    assert cat.matched_terms("POKEMON Longbow", [], ["pokemon"]) == ["pokemon"]
    assert cat.matched_terms("Longbow", ["Star-Wars"], ["star wars"]) == ["star wars"]
    assert cat.matched_terms("Longbow", ["star_wars"], ["Star Wars"]) == ["Star Wars"]
    assert cat.matched_terms("Leather backpack", ["backpacks"], ["pack"]) == []  # word boundary
    assert cat.matched_terms("Sunset temple", [], ["set"]) == []
    assert cat.matched_terms("Asset pack v2", [], ["pack"]) == ["pack"]
    assert cat.matched_terms("Marvelous bow", [], ["marvel"]) == []


def test_search_skips_reject_list_and_brief_rejects(index):
    res = cat.search(bow_brief(candidates=10), index, reject_list=cat.load_reject_list())
    uids = [c.uid for c in res]
    assert "bow006" not in uids  # name "Pokemon longbow"
    assert "bow007" not in uids  # tag "Star-Wars"
    assert "bow009" not in uids  # brief reject term "pack"
    assert "bow008" not in uids  # Low quality never indexed
    assert "bag001" not in uids and "pot001" not in uids  # no match
    assert uids == ["bow001", "bow002", "bow003"]  # in-budget exact name match first


def test_search_ranking_is_deterministic_and_prefers_in_budget(tmp_path):
    anns = {u: ann(u, "Longbow", ["bow"]) for u in ("c3", "a1", "b2")}
    anns["z9"] = ann("z9", "Longbow", ["bow"], faces=50_000)  # over budget
    anns["y8"] = ann("y8", "Bow", ["weapon"])  # weaker match
    build_index(tmp_path / "i.sqlite", anns, [q(u) for u in anns])
    b = bow_brief(candidates=8, reject_terms=[])
    r1 = [c.uid for c in cat.search(b, tmp_path / "i.sqlite", reject_list=[])]
    r2 = [c.uid for c in cat.search(b, tmp_path / "i.sqlite", reject_list=[])]
    assert r1 == r2 == ["a1", "b2", "c3", "z9", "y8"]
    assert r1[:3] == ["a1", "b2", "c3"]  # equal scores tie-break by uid
    assert r1.index("z9") > r1.index("c3")
    # LIKE fallback gives the same order
    build_index(tmp_path / "l.sqlite", anns, [q(u) for u in anns], use_fts=False)
    assert [c.uid for c in cat.search(b, tmp_path / "l.sqlite", reject_list=[])] == r1
    assert len(cat.search(bow_brief(candidates=2, reject_terms=[]), tmp_path / "i.sqlite", reject_list=[])) == 2


def test_search_without_index_is_blocked(tmp_path):
    with pytest.raises(cat.CatalogueBlocked, match="build-index"):
        cat.search(bow_brief(), tmp_path / "missing.sqlite")


# ------------------------------------------------------------------ brief format

def test_brief_schema_validation(tmp_path):
    b = bow_brief()
    assert b.candidates == 4 and b.requires_human_approval
    assert cat.Brief(id="x", kind="prop", search_terms=["a"], size_m=1, max_tris=10, max_texture=512).candidates == 8
    bad = [
        dict(kind="vehicle"), dict(search_terms=[]), dict(search_terms=[" "]), dict(size_m=0), dict(max_tris=0),
        dict(max_texture=500), dict(candidates=0), dict(candidates=99), dict(id="Bad Id"), dict(colour="red"),
        dict(kind="character_base", requires_human_approval=False), dict(reject_terms=[""]),
    ]
    for kw in bad:
        with pytest.raises(ValidationError):
            bow_brief(**kw)
    p = tmp_path / "other_name.json"
    p.write_text(json.dumps(bow_brief().model_dump()))
    with pytest.raises(cat.CatalogueError, match="must be named"):
        cat.load_brief(p)
    p = tmp_path / "wooden_longbow.json"
    p.write_text("{not json")
    with pytest.raises(cat.CatalogueError):
        cat.load_brief(p)


REQUIRED = {
    "prop": ["wooden_longbow", "recurve_bow", "arrow", "quiver", "round_bronze_shield", "war_banner", "war_drum",
             "torch_stand", "stone_pillar", "broken_pillar", "wooden_barricade", "weapon_rack", "clay_pot",
             "bronze_bell", "chariot_wheel"],
    "arena": ["stone_fort_wall_segment", "fort_gate", "watchtower", "temple_ruin", "stone_steps",
              "river_rock_cluster", "forest_tree", "bush", "armoury_tent", "boulder"],
    "cover": ["stone_slab_cover", "wooden_palisade", "iron_wall_panel"],
    "character": ["humanoid_archer_base"],
}


def test_astra_v1_briefs_exist_validate_and_match_the_owner_list():
    files = sorted(BRIEFS.glob("*.json"))
    ids = {p.stem for p in files}
    expected = {i for group in REQUIRED.values() for i in group}
    assert len(expected) == 29 and ids == expected and len(files) == 29
    briefs = {p.stem: cat.load_brief(p) for p in files}
    for bid in REQUIRED["prop"]:
        b = briefs[bid]
        assert b.kind in ("prop", "weapon") and (b.max_tris, b.max_texture) == (3000, 512) and b.role is None
    assert {b for b in REQUIRED["prop"] if briefs[b].kind == "weapon"} == {
        "wooden_longbow", "recurve_bow", "arrow", "quiver", "round_bronze_shield"}
    for bid in REQUIRED["arena"]:
        assert briefs[bid].kind == "arena" and (briefs[bid].max_tris, briefs[bid].max_texture) == (5000, 1024)
    for bid in REQUIRED["cover"]:
        b = briefs[bid]
        assert b.kind == "prop" and b.role == "cover" and b.max_tris == 3000 and b.max_texture == 512
    ch = briefs["humanoid_archer_base"]
    assert ch.kind == "character_base" and ch.max_tris == 15000 and ch.requires_human_approval
    for b in briefs.values():
        assert b.candidates == 8 and b.requires_human_approval
        for t in ("pack", "diorama", "collection", "low poly scene", "gun", "rifle", "car", "robot"):
            if b.id == "river_rock_cluster" and t == "collection":
                continue  # a rock cluster is one composed object; "rock collection" uploads are fine
            assert t in b.reject_terms, (b.id, t)
        # no search term is itself rejected by the brief or the default reject list
        for s in b.search_terms:
            assert not cat.matched_terms(s, [], b.reject_terms + cat.load_reject_list()), (b.id, s)
    text = " ".join(p.read_text().lower() for p in files)
    for banned in ("weapon effect", "card", "land map", "icon\"", "sound"):
        assert f'"title": "{banned}' not in text


# ------------------------------------------------------------------ guards and fetch

def test_disk_guard_refuses_under_10gb(tmp_path):
    with pytest.raises(cat.DiskGuardError, match="10 GB"):
        cat.check_disk([tmp_path], disk_usage=lambda p: type("U", (), {"free": 9 * cat.GB})())
    assert cat.check_disk([tmp_path / "not" / "yet"], disk_usage=lambda p: type("U", (), {"free": 11 * cat.GB})())


def test_disk_guard_exit_3_before_any_download(tmp_path, index):
    cfg = lane_config(tmp_path, index, free_gb=9.5)
    res = run_brief(bow_brief(), tmp_path / "target", cfg, FakeJudge())
    assert res.exit_code == EXIT_BLOCKED == 3 and "disk guard" in res.reason
    assert cfg.downloader.calls == [] and not (tmp_path / "target").exists()


def test_download_cap_plans_by_archive_size_and_stops_on_actual_size(tmp_path):
    mk = lambda uid, size: cat.Candidate(uid, uid, [], "by", "CC-BY", "a", "u", 2, "High", 100, size)  # noqa: E731
    keep, dropped = cat.plan_downloads([mk("a", 300 * cat.MB), mk("b", 250 * cat.MB), mk("c", 150 * cat.MB),
                                        mk("d", None)])
    assert [c.uid for c in keep] == ["a", "c"]
    assert {d["uid"] for d in dropped} == {"b", "d"}
    dl = FakeDownloader(tmp_path / "cache", sizes={"a": 600, "b": 600})
    fr = cat.fetch(["a", "b", "c"], tmp_path / "dest", downloader=dl, max_total_bytes=1000)
    assert list(fr.files) == ["a"] and fr.stopped and "b" in fr.stopped
    assert dl.downloaded == ["a", "b"]  # stopped before c
    assert not (tmp_path / "dest" / "b.glb").exists()


def test_download_cap_in_lane(tmp_path, index):
    dl = FakeDownloader(tmp_path / "objaverse-cache", default_size=600)
    cfg = lane_config(tmp_path, index, downloader=dl, max_download_bytes=1000)
    # every planned archive is 2 MB (> 1000 bytes): nothing fits the cap -> NONE, nothing downloaded
    res = run_brief(bow_brief(), tmp_path / "t", cfg, FakeJudge())
    assert res.exit_code == EXIT_NONE and "download cap" in res.reason and dl.calls == []


def test_blender_missing_exit_3(tmp_path, index, monkeypatch):
    monkeypatch.delenv("FORGE_BLENDER", raising=False)
    monkeypatch.setenv("PATH", str(tmp_path / "empty"))
    assert find_blender() is None
    cfg = lane_config(tmp_path, index, blender=None)
    res = run_brief(bow_brief(), tmp_path / "t", cfg, FakeJudge())
    assert res.exit_code == EXIT_BLOCKED and "Blender not found" in res.reason
    assert cfg.downloader.calls == []


def test_blender_that_cannot_start_is_blocked(tmp_path, index):
    from forge.checks.base import ProcResult

    runner = lambda argv, cwd, t: ProcResult(None, "", "executable not found", 0, missing_executable=True)  # noqa
    cfg = lane_config(tmp_path, index, runner=runner)
    res = run_brief(bow_brief(), tmp_path / "t", cfg, FakeJudge())
    assert res.exit_code == EXIT_BLOCKED and "could not be started" in res.reason
    assert not any((tmp_path / "objaverse-cache").rglob("*.glb")) and not any((tmp_path / "work").iterdir())


def test_missing_objaverse_package_is_blocked(tmp_path, index):
    cfg = lane_config(tmp_path, index)
    cfg.downloader = None  # real default -> objaverse import (blocked by fixture)
    res = run_brief(bow_brief(), tmp_path / "t", cfg, FakeJudge())
    assert res.exit_code == EXIT_BLOCKED and "objaverse" in res.reason


def test_budget_check():
    from forge.lanes.asset_tools import CleanupReport

    b = bow_brief()
    rep = CleanupReport("u", Path("x"), Path("r"), {}, {"triangles": 4000, "textures": [
        {"name": "t", "width": 1024, "height": 512}], "bounds": {"size": [0.1, 0.1, 1.0]}})
    problems = budget_check(rep, b)
    assert any("4000 triangles" in p for p in problems)
    assert any("1024px" in p for p in problems)
    assert any("largest dimension" in p for p in problems)
    assert any("missing renders" in p for p in problems)


# ------------------------------------------------------------------ the lane end to end

def test_lane_picks_writes_credit_and_cleans_up(tmp_path, index):
    cfg = lane_config(tmp_path, index)
    judge = FakeJudge("bow003", cost_micros=4_000)
    costs = []
    target = tmp_path / "repo"
    res = run_brief(bow_brief(), target, cfg, judge, on_cost=costs.append)
    assert res.exit_code == EXIT_PICKED == 0, res.reason
    assert res.pick == "bow003" and costs and costs[0].cost_micros == 4_000 and res.cost_micros == 4_000
    out = target / "assets" / "source" / "wooden_longbow"
    names = sorted(p.name for p in out.iterdir())
    assert names == sorted(["bow003.glb", "front.png", "side.png", "back.png", "three_quarter.png", "report.json",
                            "pick.json", "credits.json"])
    assert (out / "bow003.glb").read_bytes() == b"glTF-clean-bow003"  # the cleaned file
    credits = json.loads((out / "credits.json").read_text())
    assert credits == [{
        "uid": "bow003", "name": "Recurve bow", "artist": "Artist", "licence": "CC0", "licence_raw": "cc0",
        "source_url": "https://sketchfab.com/3d-models/bow003", "download_date": "2026-10-06",
        "source": "Objaverse 1.0 (Sketchfab upload)",
        "note": "licence label taken from Objaverse metadata; labels can be wrong - owner must verify"}]
    pick = json.loads((out / "pick.json").read_text())
    assert pick["status"] == "AWAITING_OWNER_APPROVAL" and pick["requires_human_approval"]
    assert "can be wrong" in pick["licence_warning"]
    # the judge saw four views of every surviving candidate plus the NONE/brand instruction
    req = judge.judgements[0]
    assert all(set(c.images) == {"front", "side", "back", "three_quarter"} for c in req.candidates)
    assert "NONE" in req.instructions and "recognisable game, film" in req.instructions
    # unused downloads deleted, package cache cleared
    downloaded = set(cfg.downloader.downloaded)
    assert len(downloaded) >= 2 and "bow003" in downloaded
    assert not list((tmp_path / "objaverse-cache").rglob("*.glb"))
    assert not any((tmp_path / "work").iterdir())
    assert any(d.endswith("bow001.glb") for d in res.deleted)
    assert len(res.cache_cleared) == len(downloaded)
    for uid in downloaded - {"bow003"}:
        assert not list(target.rglob(f"{uid}*"))


def test_credit_record_appends_without_duplicates(tmp_path, index):
    d = tmp_path / "out"
    cat.record_credit("bow001", d, index_path=index, date="2026-10-01")
    cat.record_credit("bow001", d, index_path=index, date="2026-10-02")
    cat.record_credit("bow003", d, index_path=index, date="2026-10-03")
    credits = json.loads((d / "credits.json").read_text())
    assert [c["uid"] for c in credits] == ["bow001", "bow003"]
    assert credits[0]["download_date"] == "2026-10-01" and credits[0]["licence"] == "CC-BY"
    assert set(credits[0]) >= {"uid", "name", "artist", "licence", "source_url", "download_date"}
    with pytest.raises(cat.CatalogueError):
        cat.record_credit("nope", d, index_path=index)


def test_lane_none_from_judge_is_exit_2_and_leaves_nothing(tmp_path, index):
    cfg = lane_config(tmp_path, index)
    res = run_brief(bow_brief(), tmp_path / "repo", cfg, FakeJudge("NONE"))
    assert res.exit_code == EXIT_NONE == 2 and "NONE" in res.reason
    assert not (tmp_path / "repo").exists()
    assert not list((tmp_path / "objaverse-cache").rglob("*.glb")) and not any((tmp_path / "work").iterdir())


def test_lane_none_when_nothing_matches_or_survives(tmp_path, index):
    cfg = lane_config(tmp_path, index)
    judge = FakeJudge()
    res = run_brief(bow_brief(search_terms=["chariot wheel"]), tmp_path / "t", cfg, judge)
    assert res.exit_code == EXIT_NONE and "no catalogue entry" in res.reason and judge.judgements == []
    cfg = lane_config(tmp_path, index, runner=FakeBlenderRunner(triangles={"bow001": 9999, "bow002": 9999},
                                                                fail={"bow003"}))
    res = run_brief(bow_brief(), tmp_path / "t", cfg, judge)
    assert res.exit_code == EXIT_NONE and "survived" in res.reason and judge.judgements == []
    reasons = {e["uid"]: e["reason"] for e in res.excluded}
    assert "9999 triangles" in reasons["bow001"] and "cleanup failed" in reasons["bow003"].lower()


def test_judge_unknown_uid_is_none(tmp_path, index):
    cfg = lane_config(tmp_path, index)
    res = run_brief(bow_brief(), tmp_path / "t", cfg, FakeJudge("not-a-candidate"))
    assert res.exit_code == EXIT_NONE


def test_blender_adapter_builds_background_command(tmp_path):
    runner = FakeBlenderRunner()
    src = tmp_path / "u1.glb"
    src.write_bytes(b"glTF")
    rep = BlenderAdapter("/opt/blender", runner=runner).cleanup("u1", src, tmp_path / "o", bow_brief())
    argv = runner.calls[0]
    assert argv[:4] == ["/opt/blender", "--background", "--factory-startup", "--python"]
    assert argv[4].endswith("blender_cleanup.py") and Path(argv[4]).exists()
    assert "--max-tris" in argv and "3000" in argv and "--max-texture" in argv and "512" in argv
    assert rep.triangles == 1200 and set(rep.renders) == {"front", "side", "back", "three_quarter"}
    assert budget_check(rep, bow_brief()) == []
