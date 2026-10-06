# Astra Kingdoms art and sound (V1 tickets 65-72)

**No production art, music or store capture exists yet.** This folder holds what can be built
before the playtest gate and the vendors: the briefs, the cultural review, the placeholder asset
generators and the placeholder ledger.

| Path | Contents |
| --- | --- |
| `briefs/` | One brief per asset or family in the plan's format (use, silhouette, scale, camera distance, palette, allowed references, device budget, licence provenance), tickets 65-72, plus `cultural-review-checklist.md`. |
| `tools/check_briefs.py` | Fails if a brief misses a plan field, ticket 67 does not list 20 weapons or ticket 69 does not list 40 icons. |
| `placeholders/` | Offline generators (Python, numpy + Pillow): 40 icons (vector ops -> `svg/` + PNG), 19 sound effects, 2 seamless music loops. Output goes to `unity/Assets/Art/Placeholder/`. |
| `ledger/placeholder-assets.ledger.json` | `AK-ASSET-LEDGER/1` rows for every generated file: status `Placeholder`, original in-house work, SHA-256 of the committed bytes. |
| `tests/` | pytest: brief completeness, icon count/contrast/element silhouette distinctness, audio format/clipping/clicks/durations/spectral distinctness, loop seams, ledger hashes. |

```bash
pip install -r astra-kingdoms/art/requirements.txt
python astra-kingdoms/art/placeholders/generate_all.py          # regenerate assets + ledger
python astra-kingdoms/art/placeholders/generate_all.py --check  # regenerate to temp, compare hashes
python astra-kingdoms/art/tools/check_briefs.py
python -m pytest astra-kingdoms/art/tests -q
dotnet run --project astra-kingdoms/tools/AstraKingdoms.Release -- ledger-validate \
  --unity astra-kingdoms/unity \
  --ledger astra-kingdoms/unity/Assets/Resources/Ledger/asset-ledger.json \
  --ledger astra-kingdoms/art/ledger/placeholder-assets.ledger.json
```

Import settings for everything under `unity/Assets/Art/` are applied by
`unity/Assets/Editor/Release/ArtImportPolicy.cs` (ASTC textures <= 1,024 px, sprites for icons;
mono effects decompressed or compressed in memory; streamed Vorbis music).

**Why a separate ledger file.** The game's runtime ledger
(`unity/Assets/Resources/Ledger/asset-ledger.json`) lists what the client references. The generated
placeholders are not yet referenced by client code, so they live in their own ledger with the same
format; the release tool validates both together (unique IDs across files, hashes against the
files). When the client starts loading an asset from `Assets/Art/`, move its row into the runtime
ledger.

**Byte reproducibility.** PNG and WAV bytes depend on the Pillow/zlib and numpy versions.
`--check` reports differences rather than hiding them; the committed files and their ledger hashes
are the reference.
