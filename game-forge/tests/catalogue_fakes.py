"""Fake index sources, downloader, Blender runner and disk for catalogue-lane tests (no network)."""

from __future__ import annotations

import json
import struct
import zlib
from collections import namedtuple
from pathlib import Path

from forge.checks.base import ProcResult
from forge.lanes.asset_tools import BlenderAdapter
from forge.lanes.catalogue import GB, Brief, load_index
from forge.lanes.run_catalogue import LaneConfig

Usage = namedtuple("Usage", "total used free")


def png_bytes(w: int = 4, h: int = 4) -> bytes:
    raw = b"".join(b"\x00" + b"\x80\x80\x80" * w for _ in range(h))

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def ann(uid, name, tags=(), licence="by", faces=1500, size=2_000_000, textures=2, user="Artist", age=False,
        **extra):
    a = {
        "uid": uid, "name": name, "description": f"{name} model",
        "tags": [{"name": t, "slug": t.lower().replace(" ", "-"), "uri": f"https://api/{t}"} for t in tags],
        "license": licence, "user": {"username": user.lower(), "displayName": user,
                                      "profileUrl": f"https://sketchfab.com/{user.lower()}"},
        "viewerUrl": f"https://sketchfab.com/3d-models/{uid}", "faceCount": faces, "vertexCount": faces,
        "archives": {"glb": {"textureCount": textures, "size": size, "type": "glb", "textureMaxResolution": 2048,
                             "faceCount": faces, "vertexCount": faces}},
        "categories": [], "isAgeRestricted": age,
    }
    if licence is None:
        del a["license"]
    a.update(extra)
    return a


def q(uid, score=2, multi="false", scene="false", single="false", transparent="false"):
    return {"UID": uid, "score": score, "is_multi_object": multi, "is_scene": scene, "is_single_color": single,
            "is_transparent": transparent}


def standard_sources():
    anns = {
        "bow001": ann("bow001", "Wooden Longbow", ["bow", "medieval", "weapon"]),
        "bow002": ann("bow002", "Elven longbow", ["bow", "fantasy"], faces=9000),
        "bow003": ann("bow003", "Recurve bow", ["bow"], licence="cc0"),
        "bow004": ann("bow004", "Longbow NC", ["bow"], licence="by-nc"),
        "bow005": ann("bow005", "Longbow SA", ["bow"], licence="by-sa"),
        "bow006": ann("bow006", "Pokemon longbow", ["bow"]),
        "bow007": ann("bow007", "Longbow", ["bow", "Star-Wars"]),
        "bow008": ann("bow008", "Longbow low quality", ["bow"]),
        "bow009": ann("bow009", "Longbow pack", ["bow"]),
        "bag001": ann("bag001", "Leather backpack", ["bag"]),
        "pot001": ann("pot001", "Clay pot", ["pottery"]),
    }
    quality = [q(u) for u in anns if u != "bow008"] + [q("bow008", score=1)]
    return anns, quality


def build_index(path: Path, anns=None, quality=None, **kw):
    if anns is None:
        anns, quality = standard_sources()
    return load_index(path, annotations=anns, quality=quality, **kw)


class FakeDownloader:
    """Mimics objaverse.load_objects: writes <cache>/glbs/000-001/<uid>.glb and returns uid -> path."""

    def __init__(self, cache_dir: Path, sizes: dict[str, int] | None = None, default_size: int = 1000):
        self.cache_dir = Path(cache_dir)
        self.sizes = sizes or {}
        self.default_size = default_size
        self.calls: list[list[str]] = []

    def __call__(self, uids):
        self.calls.append(list(uids))
        out = {}
        for uid in uids:
            p = self.cache_dir / "glbs" / "000-001" / f"{uid}.glb"
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b"glTF" + b"\x00" * (self.sizes.get(uid, self.default_size) - 4))
            out[uid] = str(p)
        return out

    @property
    def downloaded(self) -> list[str]:
        return [u for c in self.calls for u in c]


class FakeBlenderRunner:
    """Stands in for `blender --background --python blender_cleanup.py -- ...`."""

    def __init__(self, triangles: dict[str, int] | None = None, fail: set[str] | None = None, tex: int = 512,
                 size_scale: float = 1.0):
        self.triangles = triangles or {}
        self.fail = fail or set()
        self.tex = tex
        self.size_scale = size_scale
        self.calls: list[list[str]] = []

    def __call__(self, argv, cwd, timeout):
        self.calls.append(list(argv))
        a = argv[argv.index("--") + 1:]
        opt = {a[i]: a[i + 1] for i in range(0, len(a), 2)}
        src = Path(opt["--input"])
        uid = src.stem
        if uid in self.fail:
            return ProcResult(1, "", "Error: cannot import", 0.1)
        out, rep, ren = Path(opt["--output"]), Path(opt["--report"]), Path(opt["--renders"])
        out.write_bytes(b"glTF-clean-" + uid.encode())
        ren.mkdir(parents=True, exist_ok=True)
        renders = {}
        for v in ("front", "side", "back", "three_quarter"):
            (ren / f"{v}.png").write_bytes(png_bytes())
            renders[v] = f"{v}.png"
        size = float(opt["--size-m"]) * self.size_scale
        rep.write_text(json.dumps({
            "triangles": self.triangles.get(uid, min(1200, int(opt["--max-tris"]))),
            "original_triangles": 5000, "decimated": True,
            "textures": [{"name": "base", "width": self.tex, "height": self.tex}],
            "bounds": {"min": [0, 0, 0], "max": [size / 4, size / 4, size], "size": [size / 4, size / 4, size]},
            "renders": renders,
        }))
        return ProcResult(0, "ok", "", 0.1)


def fake_disk(free_gb: float = 100.0):
    return lambda path: Usage(1000 * GB, 0, int(free_gb * GB))


def lane_config(tmp: Path, index: Path, *, downloader=None, runner=None, free_gb=100.0, blender="/fake/blender",
                reject_list=None, **kw) -> LaneConfig:
    cache = tmp / "objaverse-cache"
    return LaneConfig(
        index_path=index, cache_dir=cache, work_root=tmp / "work",
        reject_list=reject_list if reject_list is not None else ["pokemon", "star wars"],
        downloader=downloader or FakeDownloader(cache), disk_usage=fake_disk(free_gb),
        blender=BlenderAdapter(blender, runner=runner or FakeBlenderRunner()), today=lambda: "2026-10-06", **kw)


def bow_brief(**kw) -> Brief:
    data = dict(id="wooden_longbow", kind="weapon", search_terms=["longbow", "bow"], reject_terms=["pack"],
                size_m=1.8, max_tris=3000, max_texture=512, candidates=4)
    data.update(kw)
    return Brief(**data)
