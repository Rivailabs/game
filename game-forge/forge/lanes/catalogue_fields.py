"""Every external field name, path and constant the catalogue lane relies on, in one place.

If a field turns out to be named differently, correct it HERE; nothing else in Forge
spells these names. Each entry carries a confidence tag and the evidence behind it:

* CONFIRMED   - read in the upstream source code or README.
* THIRD-PARTY - seen in third-party code or printed real records, not in upstream docs.
* UNCONFIRMED - inferred (lookup tables, README ordering); verify on first real run.

huggingface.co was not reachable from the machine that wrote this module, so nothing
here was checked against a live download. ``docs/catalogue_fields.md`` holds the same
table for review.
"""

from __future__ import annotations

# =============================================================================== objaverse package
# Source: allenai/objaverse-xl, package `objaverse` 0.1.7, file objaverse/__init__.py.

#: CONFIRMED (objaverse/__init__.py): BASE_PATH = os.path.join(os.path.expanduser("~"), ".objaverse"),
#: hard-coded; there is no parameter or environment variable to move it.
OBJAVERSE_PACKAGE_VERSION = "0.1.7"
OBJAVERSE_BASE_PATH = "~/.objaverse"
#: CONFIRMED: Objaverse 1.0 cache dir; holds object-paths.json.gz, metadata/*.json.gz, glbs/*/*.glb.
OBJAVERSE_CACHE_DIR = "~/.objaverse/hf-objaverse-v1"
#: CONFIRMED: sub-directory of the cache dir that holds downloaded GLBs as glbs/<shard>/<uid>.glb.
OBJAVERSE_GLB_SUBDIR = "glbs"
#: CONFIRMED: sub-directory with the 160 metadata shards 000-000 .. 000-159 (json.gz).
OBJAVERSE_METADATA_SUBDIR = "metadata"

#: CONFIRMED: load_uids() -> List[str].
OBJAVERSE_FN_LOAD_UIDS = "load_uids"
#: CONFIRMED: load_annotations(uids: Optional[List[str]] = None) -> Dict[str, Any] (uid -> metadata dict).
#: Downloads all 160 metadata shards from huggingface plus object-paths.json.gz.
OBJAVERSE_FN_LOAD_ANNOTATIONS = "load_annotations"
#: CONFIRMED: load_objects(uids: List[str], download_processes: int = 1) -> Dict[str, str]
#: (uid -> local .glb path). Skips files already present. Downloads from
#: https://huggingface.co/datasets/allenai/objaverse/resolve/main/{object_path}.
OBJAVERSE_FN_LOAD_OBJECTS = "load_objects"
#: CONFIRMED: load_lvis_annotations() exists (not used by this lane).
OBJAVERSE_FN_LOAD_LVIS = "load_lvis_annotations"
OBJAVERSE_DATASET_URL = "https://huggingface.co/datasets/allenai/objaverse"
OBJAVERSE_CODE_URL = "https://github.com/allenai/objaverse-xl"

# =============================================================================== Objaverse 1.0 annotation keys
# THIRD-PARTY for every key below: printed real records in lihzha/diffusion-policy and
# makramchahine/flex, plus TCXX/ObjaversePlusPlus download_objaverse.py:31.

ANN_UID = "uid"  # THIRD-PARTY
ANN_NAME = "name"  # THIRD-PARTY
ANN_DESCRIPTION = "description"  # THIRD-PARTY
ANN_TAGS = "tags"  # THIRD-PARTY: list of dicts {"name", "slug", "uri"}
ANN_TAG_TEXT = "name"  # THIRD-PARTY: the tag text inside each tag dict
ANN_TAG_SLUG = "slug"  # THIRD-PARTY: fallback when "name" is absent
ANN_LICENSE = "license"  # THIRD-PARTY: string such as "by"; some code also handles a dict
ANN_LICENSE_DICT_LABEL = "label"  # THIRD-PARTY: label key when "license" is a dict
ANN_USER = "user"  # THIRD-PARTY: dict
ANN_USER_DISPLAY_NAME = "displayName"  # THIRD-PARTY: artist (preferred)
ANN_USER_USERNAME = "username"  # THIRD-PARTY: artist fallback
ANN_USER_PROFILE_URL = "profileUrl"  # THIRD-PARTY
ANN_VIEWER_URL = "viewerUrl"  # THIRD-PARTY: https://sketchfab.com/3d-models/<uid>
ANN_FACE_COUNT = "faceCount"  # THIRD-PARTY
ANN_VERTEX_COUNT = "vertexCount"  # THIRD-PARTY
ANN_ARCHIVES = "archives"  # THIRD-PARTY: {"glb","gltf","source","usdz"} -> dict
ANN_ARCHIVE_GLB = "glb"  # THIRD-PARTY
ANN_ARCHIVE_SIZE = "size"  # THIRD-PARTY: bytes; used for the per-brief download cap
ANN_ARCHIVE_TEXTURE_COUNT = "textureCount"  # THIRD-PARTY: used for the "textured" proxy
ANN_ARCHIVE_TEXTURE_MAX_RES = "textureMaxResolution"  # THIRD-PARTY
ANN_ARCHIVE_FACE_COUNT = "faceCount"  # THIRD-PARTY
ANN_CATEGORIES = "categories"  # THIRD-PARTY (not used for filtering)
ANN_AGE_RESTRICTED = "isAgeRestricted"  # THIRD-PARTY: entries with True are dropped

#: Fallback viewer URL pattern when viewerUrl is missing (THIRD-PARTY: matches printed records).
VIEWER_URL_PATTERN = "https://sketchfab.com/3d-models/{uid}"

# =============================================================================== licences
# Only "by" was seen in a real printed record (THIRD-PARTY). The full set
# by, by-sa, by-nd, by-nc, by-nc-sa, by-nc-nd, cc0 comes from third-party lookup tables
# (UNCONFIRMED). Anything not explicitly listed in LICENCE_ACCEPT is rejected, including
# missing or empty values, so an unexpected spelling fails closed.

#: slug -> canonical licence name written to credits.json. Owner rule: CC-BY and CC0 only.
LICENCE_ACCEPT: dict[str, str] = {
    "by": "CC-BY",  # THIRD-PARTY (seen in a real record)
    "cc0": "CC0",  # UNCONFIRMED spelling (third-party lookup table)
}
#: Label forms (Sketchfab display labels) mapped explicitly; UNCONFIRMED. Compared case-insensitively
#: after trimming. Every other label, e.g. "CC Attribution-NonCommercial", is rejected.
LICENCE_LABEL_ACCEPT: dict[str, str] = {
    "cc attribution": "by",
    "cc0 public domain": "cc0",
}
#: Known slugs that are explicitly rejected (informational; rejection does not depend on this list).
LICENCE_KNOWN_REJECTED = ("by-sa", "by-nd", "by-nc", "by-nc-sa", "by-nc-nd")

# =============================================================================== Objaverse++
# Dataset: https://huggingface.co/datasets/cindyxl/ObjaversePlusPlus
# Code:    https://github.com/TCXX/ObjaversePlusPlus

OPP_DATASET_ID = "cindyxl/ObjaversePlusPlus"  # CONFIRMED (dataset URL)
OPP_SPLIT = "train"  # THIRD-PARTY: load_dataset("cindyxl/ObjaversePlusPlus", split="train")
OPP_UID = "UID"  # THIRD-PARTY: capital letters
OPP_SCORE = "score"  # THIRD-PARTY: int 0-3
OPP_IS_MULTI_OBJECT = "is_multi_object"  # THIRD-PARTY
OPP_IS_SCENE = "is_scene"  # THIRD-PARTY
OPP_IS_SINGLE_COLOR = "is_single_color"  # THIRD-PARTY
OPP_IS_TRANSPARENT = "is_transparent"  # THIRD-PARTY (stored, not filtered)
#: The README (CONFIRMED) lists quality levels Low, Medium, High, Superior in that order and
#: the flags Transparency, Scene, Single Color, Not a Single Object, Figure. The released
#: columns above are THIRD-PARTY; a "Figure" column was not seen in released data.
#: score -> name mapping is UNCONFIRMED (implied by the README order).
OPP_SCORE_NAMES: dict[int, str] = {0: "Low", 1: "Medium", 2: "High", 3: "Superior"}
#: Owner rule: keep only High or Superior.
OPP_KEEP_SCORES = frozenset({2, 3})
#: Local file fallback when `datasets` is not installed: CSV or parquet with the columns above.
#: The file name and format of the upstream release are UNCONFIRMED.
OPP_LOCAL_FILE_SUFFIXES = (".csv", ".parquet", ".jsonl", ".json")

#: "Textured" has no direct Objaverse++ flag. Proxy used by this lane (documented, not upstream):
#: textured = is_single_color is False AND archives.glb.textureCount > 0.
TEXTURED_PROXY = "is_single_color == false AND archives.glb.textureCount > 0"
