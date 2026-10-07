"""Catalogue asset lane: find an existing free 3D model before anything is generated.

Sources (owner rules):

* Objaverse 1.0 via the ``objaverse`` package. Annotations are used only for search;
  only the chosen uids are downloaded. Objaverse-XL sources are never used.
* Objaverse++ quality tags. Only High/Superior, single-object, non-scene, textured
  entries are kept.
* Licences: CC-BY and CC0 only. Everything else, including a missing licence, is rejected.

Every external field name lives in :mod:`forge.lanes.catalogue_fields`.

Public functions: :func:`load_index`, :func:`search`, :func:`fetch`, :func:`record_credit`
plus the guards (:func:`check_disk`, :func:`plan_downloads`) used by ``run_catalogue``.
Every source is injectable so tests run on a fake index and a fake downloader.
"""

from __future__ import annotations

import datetime as _dt
import json
import os
import re
import shutil
import sqlite3
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable, Literal, Mapping, Optional

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

from . import catalogue_fields as F

GB = 1024 ** 3
MB = 1024 ** 2
MIN_FREE_BYTES = 10 * GB
MAX_DOWNLOAD_BYTES = 500 * MB
DEFAULT_REJECT_LIST = Path(__file__).with_name("reject_list.json")
INDEX_SCHEMA_VERSION = "1"


class CatalogueError(Exception):
    pass


class CatalogueBlocked(CatalogueError):
    """A precondition is missing (disk, tool, dependency, index): exit code 3."""


class DiskGuardError(CatalogueBlocked):
    pass


class DownloadCapExceeded(CatalogueError):
    pass


# =============================================================================== brief format

Kind = Literal["prop", "arena", "weapon", "character_base"]
_ID_RE = re.compile(r"^[a-z0-9][a-z0-9_]{0,63}$")
_TEXTURE_SIZES = (128, 256, 512, 1024, 2048, 4096)


class Brief(BaseModel):
    """One catalogue request. JSON on disk: ``briefs/<set>/<id>.json``."""

    model_config = ConfigDict(extra="forbid")

    id: str
    kind: Kind
    title: str = ""
    search_terms: list[str]
    reject_terms: list[str] = Field(default_factory=list)
    size_m: float  # largest dimension of the cleaned asset, metres
    max_tris: int
    max_texture: int  # largest texture edge in pixels
    candidates: int = 8
    role: Optional[str] = None  # e.g. "cover" for cover props
    requires_human_approval: bool = True  # every catalogue pick needs owner approval
    notes: str = ""

    @field_validator("id")
    @classmethod
    def _id(cls, v: str) -> str:
        if not _ID_RE.match(v):
            raise ValueError("id must be lower-case letters, digits and underscores (max 64)")
        return v

    @field_validator("search_terms")
    @classmethod
    def _terms(cls, v: list[str]) -> list[str]:
        v = [t.strip() for t in v]
        if not v or any(not t for t in v):
            raise ValueError("search_terms must be a non-empty list of non-empty strings")
        return v

    @field_validator("reject_terms")
    @classmethod
    def _rejects(cls, v: list[str]) -> list[str]:
        v = [t.strip() for t in v]
        if any(not t for t in v):
            raise ValueError("reject_terms must not contain empty strings")
        return v

    @field_validator("size_m")
    @classmethod
    def _size(cls, v: float) -> float:
        if not (0 < v <= 100):
            raise ValueError("size_m must be in (0, 100]")
        return v

    @field_validator("max_tris")
    @classmethod
    def _tris(cls, v: int) -> int:
        if not (1 <= v <= 200_000):
            raise ValueError("max_tris must be in [1, 200000]")
        return v

    @field_validator("max_texture")
    @classmethod
    def _tex(cls, v: int) -> int:
        if v not in _TEXTURE_SIZES:
            raise ValueError(f"max_texture must be one of {_TEXTURE_SIZES}")
        return v

    @field_validator("candidates")
    @classmethod
    def _cands(cls, v: int) -> int:
        if not (1 <= v <= 32):
            raise ValueError("candidates must be in [1, 32]")
        return v

    @model_validator(mode="after")
    def _character_needs_human(self) -> "Brief":
        if self.kind == "character_base" and not self.requires_human_approval:
            raise ValueError("character_base briefs always require human approval")
        return self


def load_brief(path: str | os.PathLike) -> Brief:
    p = Path(path)
    try:
        data = json.loads(p.read_text())
    except (OSError, json.JSONDecodeError) as e:
        raise CatalogueError(f"cannot read brief {p}: {e}") from e
    brief = Brief.model_validate(data)
    if p.stem != brief.id:
        raise CatalogueError(f"brief file {p.name} must be named {brief.id}.json")
    return brief


# =============================================================================== text matching


def normalise(text: str) -> str:
    """Lower-case, every non-alphanumeric run becomes one space ("Low-Poly_Bow" -> "low poly bow")."""
    return " ".join(re.sub(r"[^0-9a-z]+", " ", (text or "").lower().replace("'", "")).split())


def contains_term(text_norm: str, term: str) -> bool:
    """Whole-word phrase match on normalised text ("pack" does not match "backpack")."""
    t = normalise(term)
    if not t or not text_norm:
        return False
    return f" {t} " in f" {text_norm} "


def load_reject_list(path: str | os.PathLike | None = None) -> list[str]:
    p = Path(path) if path else DEFAULT_REJECT_LIST
    data = json.loads(p.read_text())
    terms: list[str] = []
    if isinstance(data, list):
        terms = [str(t) for t in data]
    else:
        for k, v in data.items():
            if k.startswith("_"):
                continue
            terms += [str(t) for t in v]
    return sorted({t.strip() for t in terms if t and t.strip()})


def matched_terms(name: str, tags: Iterable[str], terms: Iterable[str]) -> list[str]:
    hay = [normalise(name)] + [normalise(t) for t in tags]
    return sorted({t for t in terms if any(contains_term(h, t) for h in hay)})


# =============================================================================== licence and quality rules


def normalise_licence(value: Any) -> Optional[str]:
    """Return the accepted licence slug ('by' or 'cc0') or None (rejected). Fails closed."""
    if isinstance(value, Mapping):
        value = value.get(F.ANN_LICENSE_DICT_LABEL)
        if not isinstance(value, str):
            return None
        label = " ".join(value.strip().lower().split())
        return F.LICENCE_LABEL_ACCEPT.get(label)
    if not isinstance(value, str):
        return None
    v = value.strip().lower()
    if not v:
        return None
    if v in F.LICENCE_ACCEPT:
        return v
    return F.LICENCE_LABEL_ACCEPT.get(" ".join(v.split()))


def parse_bool(v: Any) -> Optional[bool]:
    """Parse Objaverse++ booleans stored as bools, 0/1 numbers or strings. None = unparseable."""
    if isinstance(v, bool):
        return v
    if isinstance(v, int):
        return {0: False, 1: True}.get(v)
    if isinstance(v, float):
        return {0.0: False, 1.0: True}.get(v)
    if isinstance(v, str):
        return {"true": True, "false": False, "1": True, "0": False, "yes": True, "no": False,
                "t": True, "f": False, "1.0": True, "0.0": False}.get(v.strip().lower())
    return None


def parse_score(v: Any) -> Optional[int]:
    if isinstance(v, bool) or v is None:
        return None
    if isinstance(v, int):
        return v if v in F.OPP_SCORE_NAMES else None
    if isinstance(v, float) and v.is_integer():
        return parse_score(int(v))
    if isinstance(v, str):
        s = v.strip()
        if re.fullmatch(r"\d+(\.0+)?", s):
            return parse_score(int(float(s)))
        names = {n.lower(): k for k, n in F.OPP_SCORE_NAMES.items()}
        return names.get(s.lower())
    return None


@dataclass
class QualityRow:
    uid: str
    score: int
    is_single_color: bool
    is_transparent: Optional[bool]


def quality_verdict(row: Mapping[str, Any]) -> tuple[Optional[QualityRow], str]:
    """Apply the Objaverse++ rules. Returns (row, "") when kept, else (None, reason)."""
    uid = row.get(F.OPP_UID)
    if not isinstance(uid, str) or not uid.strip():
        return None, "quality: missing uid"
    score = parse_score(row.get(F.OPP_SCORE))
    if score is None:
        return None, "quality: unparseable score"
    if score not in F.OPP_KEEP_SCORES:
        return None, f"quality: {F.OPP_SCORE_NAMES[score]}"
    multi = parse_bool(row.get(F.OPP_IS_MULTI_OBJECT))
    scene = parse_bool(row.get(F.OPP_IS_SCENE))
    single = parse_bool(row.get(F.OPP_IS_SINGLE_COLOR))
    if multi is None or scene is None or single is None:
        return None, "quality: unparseable flag"
    if multi:
        return None, "quality: not a single object"
    if scene:
        return None, "quality: scene"
    if single:
        return None, "quality: single colour (untextured)"
    return QualityRow(uid.strip(), score, single, parse_bool(row.get(F.OPP_IS_TRANSPARENT))), ""


def _get(d: Any, *keys: str) -> Any:
    for k in keys:
        if not isinstance(d, Mapping):
            return None
        d = d.get(k)
    return d


def _int(v: Any) -> Optional[int]:
    if isinstance(v, bool):
        return None
    if isinstance(v, (int, float)):
        return int(v)
    if isinstance(v, str) and v.strip().isdigit():
        return int(v.strip())
    return None


def tag_texts(ann: Mapping[str, Any]) -> list[str]:
    out = []
    for t in ann.get(F.ANN_TAGS) or []:
        if isinstance(t, Mapping):
            s = t.get(F.ANN_TAG_TEXT) or t.get(F.ANN_TAG_SLUG)
        else:
            s = t if isinstance(t, str) else None
        if isinstance(s, str) and s.strip():
            out.append(s.strip())
    return out


def artist_of(ann: Mapping[str, Any]) -> str:
    u = ann.get(F.ANN_USER)
    if isinstance(u, Mapping):
        return str(u.get(F.ANN_USER_DISPLAY_NAME) or u.get(F.ANN_USER_USERNAME) or "").strip()
    return ""


def annotation_verdict(uid: str, ann: Mapping[str, Any]) -> tuple[Optional[dict], str]:
    """Licence / age / texture rules on one Objaverse 1.0 annotation."""
    lic = normalise_licence(ann.get(F.ANN_LICENSE))
    if lic is None:
        raw = ann.get(F.ANN_LICENSE)
        return None, f"licence: {raw!r}"[:80]
    if ann.get(F.ANN_AGE_RESTRICTED) is True or parse_bool(ann.get(F.ANN_AGE_RESTRICTED)) is True:
        return None, "age restricted"
    tex = _int(_get(ann, F.ANN_ARCHIVES, F.ANN_ARCHIVE_GLB, F.ANN_ARCHIVE_TEXTURE_COUNT))
    if tex is None:
        return None, "texture: unknown glb textureCount"
    if tex <= 0:
        return None, "texture: glb has no textures"
    user = ann.get(F.ANN_USER) if isinstance(ann.get(F.ANN_USER), Mapping) else {}
    raw_lic = ann.get(F.ANN_LICENSE)
    return {
        "uid": uid,
        "name": str(ann.get(F.ANN_NAME) or "").strip(),
        "description": str(ann.get(F.ANN_DESCRIPTION) or "")[:2000],
        "tags": tag_texts(ann),
        "licence": lic,
        "licence_name": F.LICENCE_ACCEPT[lic],
        "licence_raw": raw_lic if isinstance(raw_lic, str) else json.dumps(raw_lic, sort_keys=True, default=str),
        "artist": artist_of(ann),
        "artist_url": str(user.get(F.ANN_USER_PROFILE_URL) or ""),
        "viewer_url": str(ann.get(F.ANN_VIEWER_URL) or F.VIEWER_URL_PATTERN.format(uid=uid)),
        "face_count": _int(ann.get(F.ANN_FACE_COUNT)) or _int(_get(ann, F.ANN_ARCHIVES, F.ANN_ARCHIVE_GLB,
                                                                      F.ANN_ARCHIVE_FACE_COUNT)),
        "glb_size": _int(_get(ann, F.ANN_ARCHIVES, F.ANN_ARCHIVE_GLB, F.ANN_ARCHIVE_SIZE)),
        "texture_count": tex,
        "texture_max_res": _int(_get(ann, F.ANN_ARCHIVES, F.ANN_ARCHIVE_GLB, F.ANN_ARCHIVE_TEXTURE_MAX_RES)),
    }, ""


# =============================================================================== default sources (lazy imports)


def objaverse_cache_dir() -> Path:
    return Path(os.path.expanduser(F.OBJAVERSE_CACHE_DIR))


def _objaverse():
    try:
        import objaverse  # type: ignore
    except ImportError as e:
        raise CatalogueBlocked("the 'objaverse' package is not installed: pip install 'game-forge[catalogue]' "
                               f"(tested against objaverse {F.OBJAVERSE_PACKAGE_VERSION})") from e
    return objaverse


def default_annotations() -> Mapping[str, Mapping[str, Any]]:
    """All Objaverse 1.0 annotations (downloads the 160 metadata shards on first use)."""
    return getattr(_objaverse(), F.OBJAVERSE_FN_LOAD_ANNOTATIONS)()


def default_downloader(uids: list[str]) -> dict[str, str]:
    return getattr(_objaverse(), F.OBJAVERSE_FN_LOAD_OBJECTS)(uids=list(uids), download_processes=1)


def default_quality(quality_file: str | os.PathLike | None = None) -> Iterable[Mapping[str, Any]]:
    if quality_file:
        return _read_quality_file(Path(quality_file))
    try:
        from datasets import load_dataset  # type: ignore
    except ImportError as e:
        raise CatalogueBlocked("Objaverse++ tags need the 'datasets' package (pip install datasets) or a local "
                               "file passed with --quality-file (CSV, parquet or JSON lines)") from e
    return load_dataset(F.OPP_DATASET_ID, split=F.OPP_SPLIT)


def _read_quality_file(p: Path) -> Iterable[Mapping[str, Any]]:
    if not p.exists():
        raise CatalogueBlocked(f"quality file {p} does not exist")
    suf = p.suffix.lower()
    if suf == ".csv":
        import csv

        with open(p, newline="") as fh:
            yield from csv.DictReader(fh)
    elif suf in (".jsonl", ".json"):
        text = p.read_text()
        if suf == ".json" and text.lstrip().startswith("["):
            yield from json.loads(text)
        else:
            for line in text.splitlines():
                if line.strip():
                    yield json.loads(line)
    elif suf == ".parquet":
        try:
            import pyarrow.parquet as pq  # type: ignore
        except ImportError as e:
            raise CatalogueBlocked("reading a parquet quality file needs pyarrow (pip install pyarrow)") from e
        yield from pq.read_table(p).to_pylist()
    else:
        raise CatalogueBlocked(f"unsupported quality file type {suf}; use one of {F.OPP_LOCAL_FILE_SUFFIXES}")


# =============================================================================== index

_SCHEMA = """
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS objects(
  uid TEXT PRIMARY KEY, name TEXT NOT NULL, name_norm TEXT NOT NULL, tags_json TEXT NOT NULL,
  tags_norm TEXT NOT NULL, description TEXT NOT NULL, licence TEXT NOT NULL, licence_name TEXT NOT NULL,
  licence_raw TEXT NOT NULL, artist TEXT NOT NULL, artist_url TEXT NOT NULL, viewer_url TEXT NOT NULL,
  quality_score INTEGER NOT NULL, quality_name TEXT NOT NULL, face_count INTEGER, glb_size INTEGER,
  texture_count INTEGER, texture_max_res INTEGER, is_transparent INTEGER
);
"""


@dataclass
class IndexStats:
    path: str
    objects: int
    built_at: str
    fts: bool
    rejected: dict[str, int] = field(default_factory=dict)
    cached: bool = False


def _fts_available(conn: sqlite3.Connection) -> bool:
    try:
        conn.execute("CREATE VIRTUAL TABLE IF NOT EXISTS temp._fts_probe USING fts5(x)")
        conn.execute("DROP TABLE IF EXISTS temp._fts_probe")
        return True
    except sqlite3.Error:
        return False


def _iter_annotations(src: Any) -> Iterable[tuple[str, Mapping[str, Any]]]:
    if callable(src):
        src = src()
    if isinstance(src, Mapping):
        for uid, ann in src.items():
            if isinstance(ann, Mapping):
                yield str(uid), ann
        return
    for ann in src:
        if isinstance(ann, Mapping) and ann.get(F.ANN_UID):
            yield str(ann[F.ANN_UID]), ann


def index_exists(index_path: str | os.PathLike) -> bool:
    p = Path(index_path)
    if not p.exists():
        return False
    try:
        with sqlite3.connect(p) as c:
            r = c.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone()
            return bool(r and r[0] == INDEX_SCHEMA_VERSION)
    except sqlite3.Error:
        return False


def _stats(index_path: Path, cached: bool) -> IndexStats:
    with sqlite3.connect(index_path) as c:
        meta = dict(c.execute("SELECT key, value FROM meta").fetchall())
        n = c.execute("SELECT COUNT(*) FROM objects").fetchone()[0]
    return IndexStats(str(index_path), n, meta.get("built_at", ""), meta.get("fts") == "1",
                      json.loads(meta.get("rejected", "{}")), cached)


def load_index(index_path: str | os.PathLike, *, annotations: Any = None, quality: Any = None,
               quality_file: str | os.PathLike | None = None, rebuild: bool = False,
               use_fts: Optional[bool] = None, now: Callable[[], _dt.datetime] | None = None) -> IndexStats:
    """Build (or reuse) the local SQLite index of acceptable catalogue objects.

    ``annotations``: uid -> annotation mapping, iterable of annotation dicts, or a callable
    returning either (default: ``objaverse.load_annotations()``). ``quality``: iterable of
    Objaverse++ rows or a callable (default: Hugging Face ``datasets`` or ``quality_file``).
    Licence, quality, single-object, textured and age rules are applied at build time;
    franchise/brand rejects and brief reject terms are applied at search time so the
    configurable list can change without a rebuild.
    """
    index_path = Path(index_path)
    if not rebuild and index_exists(index_path):
        return _stats(index_path, cached=True)
    if quality is None:
        quality = default_quality(quality_file)
    if annotations is None:
        annotations = default_annotations
    rejected: dict[str, int] = {}

    def bump(reason: str) -> None:
        rejected[reason] = rejected.get(reason, 0) + 1

    keep: dict[str, QualityRow] = {}
    for row in (quality() if callable(quality) else quality):
        q, why = quality_verdict(row)
        if q is None:
            bump(why)
        else:
            keep[q.uid] = q
    index_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = index_path.with_suffix(index_path.suffix + ".tmp")
    if tmp.exists():
        tmp.unlink()
    conn = sqlite3.connect(tmp)
    try:
        conn.executescript(_SCHEMA)
        fts = _fts_available(conn) if use_fts is None else (use_fts and _fts_available(conn))
        if fts:
            conn.execute("CREATE VIRTUAL TABLE objects_fts USING fts5(uid UNINDEXED, name_norm, tags_norm)")
        seen = set()
        for uid, ann in _iter_annotations(annotations):
            q = keep.get(uid)
            if q is None:
                bump("quality: no High/Superior Objaverse++ entry")
                continue
            rec, why = annotation_verdict(uid, ann)
            if rec is None:
                bump(why.split(":")[0] if why.startswith("licence") else why)
                continue
            seen.add(uid)
            tags_norm = " | ".join(normalise(t) for t in rec["tags"])
            conn.execute(
                "INSERT OR REPLACE INTO objects VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (uid, rec["name"], normalise(rec["name"]), json.dumps(rec["tags"]), tags_norm, rec["description"],
                 rec["licence"], rec["licence_name"], rec["licence_raw"], rec["artist"], rec["artist_url"],
                 rec["viewer_url"], q.score, F.OPP_SCORE_NAMES[q.score], rec["face_count"], rec["glb_size"],
                 rec["texture_count"], rec["texture_max_res"],
                 None if q.is_transparent is None else int(q.is_transparent)))
            if fts:
                conn.execute("INSERT INTO objects_fts(uid, name_norm, tags_norm) VALUES(?,?,?)",
                             (uid, normalise(rec["name"]), tags_norm))
        built = (now or (lambda: _dt.datetime.now(_dt.timezone.utc)))().isoformat(timespec="seconds")
        meta = {"schema_version": INDEX_SCHEMA_VERSION, "built_at": built, "fts": "1" if fts else "0",
                "rejected": json.dumps(dict(sorted(rejected.items()))), "objects": str(len(seen)),
                "sources": json.dumps({"objaverse": F.OBJAVERSE_DATASET_URL, "quality": F.OPP_DATASET_ID})}
        conn.executemany("INSERT OR REPLACE INTO meta(key, value) VALUES(?,?)", list(meta.items()))
        conn.commit()
    except BaseException:
        conn.close()
        tmp.unlink(missing_ok=True)
        raise
    conn.close()
    os.replace(tmp, index_path)
    return _stats(index_path, cached=False)


# =============================================================================== search


@dataclass
class Candidate:
    uid: str
    name: str
    tags: list[str]
    licence: str
    licence_name: str
    artist: str
    viewer_url: str
    quality_score: int
    quality_name: str
    face_count: Optional[int]
    glb_size: Optional[int]
    score: int = 0
    matched: list[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        return asdict(self)


_STOP = {"a", "an", "the", "of", "and", "or", "with", "in", "on", "for", "to", "3d", "model"}


def _tokens(terms: Iterable[str]) -> list[str]:
    out = []
    for t in terms:
        for w in normalise(t).split():
            if w not in _STOP and len(w) > 1 and w not in out:
                out.append(w)
    return out


def _score(brief: Brief, name_norm: str, tags_norm: list[str], face_count: Optional[int],
           quality_score: int) -> tuple[int, list[str]]:
    """Integer relevance score (ties broken by uid). 0 = not a match."""
    s, why = 0, []
    for term in brief.search_terms:
        if contains_term(name_norm, term):
            s += 30
            why.append(f"name:{term}")
        elif any(contains_term(t, term) for t in tags_norm):
            s += 20
            why.append(f"tag:{term}")
    for tok in _tokens(brief.search_terms):
        if contains_term(name_norm, tok):
            s += 6
        if any(contains_term(t, tok) for t in tags_norm):
            s += 4
    if s == 0:
        return 0, []
    if face_count is not None and face_count <= brief.max_tris:
        s += 10
        why.append("within triangle budget")
    elif face_count is not None and face_count > brief.max_tris * 20:
        s -= 10  # decimating 20x rarely keeps a model readable
    s += 5 if quality_score == 3 else 0
    return max(s, 1), why


def search(brief: Brief, index_path: str | os.PathLike, *, reject_list: Iterable[str] | None = None,
           limit: Optional[int] = None, pool: int = 5000) -> list[Candidate]:
    """Top ``brief.candidates`` uids by tag and name match, skipping reject-list and brief rejects.

    Deterministic: score descending, then uid ascending.
    """
    if not index_exists(index_path):
        raise CatalogueBlocked(f"catalogue index {index_path} not built: run `forge catalogue build-index`")
    rejects = list(reject_list) if reject_list is not None else load_reject_list()
    rejects += brief.reject_terms
    toks = _tokens(brief.search_terms)
    if not toks:
        return []
    conn = sqlite3.connect(index_path)
    conn.row_factory = sqlite3.Row
    try:
        fts = conn.execute("SELECT value FROM meta WHERE key='fts'").fetchone()[0] == "1"
        if fts:
            q = " OR ".join(f'"{t}"' for t in toks)
            rows = conn.execute(
                "SELECT o.* FROM objects_fts f JOIN objects o ON o.uid = f.uid WHERE objects_fts MATCH ? "
                "ORDER BY o.uid LIMIT ?", (q, pool)).fetchall()
        else:
            where = " OR ".join("(o.name_norm LIKE ? OR o.tags_norm LIKE ?)" for _ in toks)
            args: list[Any] = []
            for t in toks:
                args += [f"%{t}%", f"%{t}%"]
            rows = conn.execute(f"SELECT o.* FROM objects o WHERE {where} ORDER BY o.uid LIMIT ?",
                                (*args, pool)).fetchall()
    finally:
        conn.close()
    out: list[Candidate] = []
    for r in rows:
        tags = json.loads(r["tags_json"])
        if matched_terms(r["name"], tags, rejects):
            continue
        tags_norm = [normalise(t) for t in tags]
        sc, why = _score(brief, r["name_norm"], tags_norm, r["face_count"], r["quality_score"])
        if sc <= 0:
            continue
        out.append(Candidate(uid=r["uid"], name=r["name"], tags=tags, licence=r["licence"],
                             licence_name=r["licence_name"], artist=r["artist"], viewer_url=r["viewer_url"],
                             quality_score=r["quality_score"], quality_name=r["quality_name"],
                             face_count=r["face_count"], glb_size=r["glb_size"], score=sc, matched=why))
    out.sort(key=lambda c: (-c.score, c.uid))
    return out[: (limit or brief.candidates)]


def get_entry(index_path: str | os.PathLike, uid: str) -> Optional[dict]:
    conn = sqlite3.connect(index_path)
    conn.row_factory = sqlite3.Row
    try:
        r = conn.execute("SELECT * FROM objects WHERE uid=?", (uid,)).fetchone()
        return dict(r) if r else None
    finally:
        conn.close()


# =============================================================================== guards


def _existing(path: Path) -> Path:
    p = path.resolve() if path.is_absolute() else Path.cwd() / path
    while not p.exists() and p != p.parent:
        p = p.parent
    return p


def check_disk(paths: Iterable[str | os.PathLike], *, min_free_bytes: int = MIN_FREE_BYTES,
               disk_usage: Callable[[str], Any] = shutil.disk_usage) -> dict[str, int]:
    """Refuse to run when any of ``paths`` (or its nearest existing parent) has < min free bytes."""
    free: dict[str, int] = {}
    for raw in paths:
        p = _existing(Path(os.path.expanduser(str(raw))))
        f = int(disk_usage(str(p)).free)
        free[str(raw)] = f
        if f < min_free_bytes:
            raise DiskGuardError(f"only {f / GB:.1f} GB free at {p}; the catalogue lane needs at least "
                                 f"{min_free_bytes / GB:.0f} GB")
    return free


def plan_downloads(cands: list[Candidate], max_total_bytes: int = MAX_DOWNLOAD_BYTES
                   ) -> tuple[list[Candidate], list[dict]]:
    """Keep candidates in rank order while the summed archive size fits the cap.

    Lower-ranked candidates are dropped first; unknown sizes are dropped (the cap cannot be
    guaranteed for them).
    """
    keep, dropped, total = [], [], 0
    for c in cands:
        if not c.glb_size or c.glb_size <= 0:
            dropped.append({"uid": c.uid, "reason": "unknown glb size"})
            continue
        if total + c.glb_size > max_total_bytes:
            dropped.append({"uid": c.uid, "reason": f"would exceed the {max_total_bytes // MB} MB download cap"})
            continue
        keep.append(c)
        total += c.glb_size
    return keep, dropped


# =============================================================================== fetch / credits / cache


@dataclass
class FetchResult:
    files: dict[str, Path]
    total_bytes: int
    stopped: Optional[str] = None  # reason the fetch stopped early
    cache_paths: list[Path] = field(default_factory=list)


def fetch(uids: list[str], dest: str | os.PathLike, *, downloader: Callable[[list[str]], Mapping[str, str]] | None = None,
          max_total_bytes: int = MAX_DOWNLOAD_BYTES) -> FetchResult:
    """Download only these uids' GLBs (one at a time) and copy them to ``dest/<uid>.glb``.

    Actual file sizes are checked after each download; when the running total exceeds the
    cap the offending file is deleted and fetching stops.
    """
    dl = downloader or default_downloader
    dest = Path(dest)
    dest.mkdir(parents=True, exist_ok=True)
    out: dict[str, Path] = {}
    cache_paths: list[Path] = []
    total = 0
    for uid in uids:
        got = dl([uid])
        src = got.get(uid) if got else None
        if not src or not Path(src).exists():
            continue
        src = Path(src)
        cache_paths.append(src)
        size = src.stat().st_size
        if total + size > max_total_bytes:
            return FetchResult(out, total, f"download cap {max_total_bytes // MB} MB reached at {uid}", cache_paths)
        target = dest / f"{uid}.glb"
        shutil.copyfile(src, target)
        out[uid] = target
        total += size
    return FetchResult(out, total, None, cache_paths)


def clear_cache(uids: Iterable[str], cache_dir: str | os.PathLike | None = None,
                extra_paths: Iterable[Path] = ()) -> list[str]:
    """Remove this run's GLBs from the objaverse package cache (metadata is kept for rebuilds)."""
    base = (Path(cache_dir) if cache_dir else objaverse_cache_dir()).resolve()
    removed: list[str] = []
    targets = {Path(p).resolve() for p in extra_paths}
    glbs = base / F.OBJAVERSE_GLB_SUBDIR
    if glbs.exists():
        for uid in uids:
            targets.update(p.resolve() for p in glbs.glob(f"*/{uid}.glb"))
    for p in sorted(targets):
        # only ever delete inside the package cache directory
        if base not in p.parents:
            continue
        try:
            if p.is_file():
                p.unlink()
                removed.append(str(p))
        except OSError:
            pass
    if glbs.exists():
        for d in glbs.iterdir():
            if d.is_dir() and not any(d.iterdir()):
                d.rmdir()
    return removed


def record_credit(uid: str, dest: str | os.PathLike, *, index_path: str | os.PathLike | None = None,
                  entry: Mapping[str, Any] | None = None, date: str | None = None) -> Path:
    """Append uid, name, artist, licence, source URL and download date to ``dest/credits.json``.

    The file is a JSON list; an existing record for the same uid is not duplicated.
    """
    if entry is None:
        if index_path is None:
            raise CatalogueError("record_credit needs index_path or entry")
        entry = get_entry(index_path, uid)
        if entry is None:
            raise CatalogueError(f"uid {uid} is not in the catalogue index")
    dest = Path(dest)
    dest.mkdir(parents=True, exist_ok=True)
    path = dest / "credits.json"
    credits: list[dict] = []
    if path.exists():
        credits = json.loads(path.read_text() or "[]")
        if not isinstance(credits, list):
            raise CatalogueError(f"{path} is not a JSON list")
    if any(c.get("uid") == uid for c in credits):
        return path
    credits.append({
        "uid": uid,
        "name": entry.get("name", ""),
        "artist": entry.get("artist", ""),
        "licence": entry.get("licence_name") or F.LICENCE_ACCEPT.get(str(entry.get("licence")), ""),
        "licence_raw": entry.get("licence_raw", entry.get("licence", "")),
        "source_url": entry.get("viewer_url", ""),
        "download_date": date or _dt.datetime.now(_dt.timezone.utc).date().isoformat(),
        "source": "Objaverse 1.0 (Sketchfab upload)",
        "note": "licence label taken from Objaverse metadata; labels can be wrong - owner must verify",
    })
    tmp = path.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(credits, indent=2) + "\n")
    os.replace(tmp, path)
    return path
