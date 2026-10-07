# Catalogue lane: external field names and how sure we are

Every name below is defined once in `forge/lanes/catalogue_fields.py`. If a name is wrong, change it there.

huggingface.co could not be reached from the machine that wrote the lane, so nothing here has been checked
against a live download. Treat the first real `forge catalogue build-index` as the check. Its output lists
how many rows were rejected for each reason. If almost everything is rejected for one reason (for example
`texture: unknown glb textureCount`), a field name is probably wrong.

Confidence levels:

- **CONFIRMED**: read in the upstream source code or README.
- **THIRD-PARTY**: seen in third-party code or printed real records, but not in the upstream docs.
- **UNCONFIRMED**: inferred from lookup tables or README order. Not seen in real data.

## `objaverse` package 0.1.7 (`objaverse/__init__.py`, github.com/allenai/objaverse-xl)

| Name | Meaning | Confidence | Evidence |
|---|---|---|---|
| `BASE_PATH = ~/.objaverse` | Hard-coded cache root. No parameter or environment variable changes it | CONFIRMED | package source |
| `~/.objaverse/hf-objaverse-v1` | Objaverse 1.0 cache: `object-paths.json.gz`, `metadata/*.json.gz`, `glbs/*/*.glb` | CONFIRMED | package source |
| `load_uids() -> List[str]` | All uids | CONFIRMED | package source |
| `load_annotations(uids=None) -> Dict[str, Any]` | uid → metadata dict. Downloads 160 shards `000-000`..`000-159` and `object-paths.json.gz` | CONFIRMED | package source |
| `load_objects(uids, download_processes=1) -> Dict[str, str]` | uid → local `.glb` path. Skips files already present. Source: `https://huggingface.co/datasets/allenai/objaverse/resolve/main/{object_path}` | CONFIRMED | package source |
| `load_lvis_annotations()` | LVIS categories (not used) | CONFIRMED | package source |

## Objaverse 1.0 annotation keys

Evidence for every row: printed real records in lihzha/diffusion-policy and makramchahine/flex, plus
TCXX/ObjaversePlusPlus `download_objaverse.py:31`.

| Key | Used for | Confidence |
|---|---|---|
| `uid`, `name`, `description` | identity, search, credit | THIRD-PARTY |
| `tags` (list of `{name, slug, uri}`), tag text = `name` (`slug` as a fallback) | search, reject list | THIRD-PARTY |
| `license` (string such as `by`; a dict with `label` is also accepted) | licence filter | THIRD-PARTY |
| `user.displayName`, falling back to `user.username`; `user.profileUrl` | artist in credits.json | THIRD-PARTY |
| `viewerUrl` (`https://sketchfab.com/3d-models/<uid>`) | source URL in credits.json | THIRD-PARTY |
| `faceCount`, `vertexCount` | prefer within-budget objects in search | THIRD-PARTY |
| `archives.glb.size` (bytes) | 500 MB per-brief download cap (planning) | THIRD-PARTY |
| `archives.glb.textureCount` | "textured" proxy | THIRD-PARTY |
| `archives.glb.textureMaxResolution`, `archives.glb.faceCount` | stored; faceCount is the fallback | THIRD-PARTY |
| `categories` | not used | THIRD-PARTY |
| `isAgeRestricted` | entries with `true` are dropped | THIRD-PARTY |

## Licence values

| Value | Decision | Confidence |
|---|---|---|
| `by` | accept, credited as CC-BY | THIRD-PARTY (the only value seen in a real record) |
| `cc0` | accept, credited as CC0 | UNCONFIRMED (spelling taken from third-party lookup tables) |
| `CC Attribution`, `CC0 Public Domain` (label forms, case-insensitive) | accept | UNCONFIRMED |
| `by-sa`, `by-nd`, `by-nc`, `by-nc-sa`, `by-nc-nd` | reject | UNCONFIRMED spellings. They are rejected whatever their spelling, because only exact accept-list values pass |
| missing, empty, any other value or type | reject (fails closed) | - |

## Objaverse++ (huggingface.co/datasets/cindyxl/ObjaversePlusPlus, github.com/TCXX/ObjaversePlusPlus)

| Name | Meaning | Confidence | Evidence |
|---|---|---|---|
| Quality levels Low, Medium, High, Superior (in that order) | quality scale | CONFIRMED | README |
| Flags Transparency, Scene, Single Color, Not a Single Object, Figure | annotation flags | CONFIRMED | README (no released `Figure` column was seen) |
| `load_dataset("cindyxl/ObjaversePlusPlus", split="train")` | how the tags are loaded | THIRD-PARTY | code that calls it |
| `UID` (capital letters) | join key with Objaverse uid | THIRD-PARTY | same code |
| `score` (int 0-3) | quality | THIRD-PARTY | same code |
| `is_multi_object`, `is_scene`, `is_single_color`, `is_transparent` | flags. Parsed from bools, `"true"`/`"false"` strings or 0/1. Unparseable values are rejected | THIRD-PARTY | same code |
| `score` → name: 0 Low, 1 Medium, 2 High, 3 Superior | keep scores 2 and 3 | UNCONFIRMED | implied by README order |
| local fallback file (`--quality-file`: CSV, parquet, JSON lines) | used when `datasets` is not installed | UNCONFIRMED | the upstream file name and format are not known |

**"Textured" proxy.** Objaverse++ has no "textured" flag. The lane keeps an object only when
`is_single_color` is false **and** `archives.glb.textureCount > 0`. This rule is Forge's own, not upstream's.

## Rules applied at index time

Licence is CC-BY or CC0. Objaverse++ score is High or Superior. `is_multi_object` is false and `is_scene` is
false. The textured proxy holds. `isAgeRestricted` is not true. An object with no Objaverse++ row is
rejected. The lane calls only the package's Objaverse 1.0 functions listed above. It never calls the
Objaverse-XL API (`objaverse.xl`) or any XL source (GitHub, Thingiverse, Smithsonian).
