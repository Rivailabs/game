# Ticket 69: forty UI/store icons

Acceptance (plan): *Sizes, contrast, consistency and usage rights are verified.*

Placeholders for all forty exist (`unity/Assets/Art/Placeholder/Icons/*.png`, sources in
`art/placeholders/svg/`), generated from one vector definition per icon. Element outlines are the
same polygons as `ElementGlyphs.cs`, so explanations, HUD, effects and icons share symbols.
`art/tests` verifies: 40 unique icons, 128 px RGBA, glyph/tile contrast >= 4.5:1, pairwise element
silhouette difference without colour, ledger hashes.

### AK-ART-069-A Icon set (40 icons)

| Field | Brief |
| --- | --- |
| Use | HUD statuses, weapon and element choice, dodge buttons, card choices, terrain announcement, settings, shop, result. Every icon appears **with a text label** or caption somewhere on the same screen; icons never replace text for a decision. |
| Silhouette | Single flat glyph on a rounded tile; one idea per icon; minimum stroke 8 % of the glyph box so it survives 48 dp. Element icons: flame, spiral, stone, bolt, wave (plan); neutral = ring. |
| Scale | Authored at 128 px; shown at 48-96 dp (about 72-144 px on xhdpi). Store icon (512 px) is ticket 72. |
| Camera distance | Screen-space UI, 25-40 cm viewing distance, landscape. |
| Palette | White glyph on category tiles (element tiles tinted per element). Contrast >= 4.5:1 glyph vs tile; colour is a secondary cue only. |
| Allowed references | Original drawings only. No icon fonts or icon packs unless their licence is recorded (e.g. an OFL/Apache set) and the ledger lists it. No religious symbols (no Om, swastika, trishul, lotus-throne, tilak marks); the Padma card uses a geometric petal envelope only. |
| Device budget | One UI atlas (<= 2,048 px requires a measured approval per `AssetBudgets`; target 1,024 px), ASTC 6x6, no mipmaps, sprite import. |
| Licence provenance | Final icons: in-house or commissioned with assignment; ledger rows `texture.icon.<id>` move from Placeholder to Approved with hash. |
| Acceptance | Size, contrast and consistency checks pass (automated); usage rights recorded; owner reviews the sheet on the reference phone at 48 dp. |

### Icon list

| Category | Icons |
| --- | --- |
| Elements (6) | `element-agni`, `element-vayu`, `element-prithvi`, `element-vidyut`, `element-varuna`, `element-neutral` |
| Terrain (5) | `terrain-plain`, `terrain-fort`, `terrain-river`, `terrain-forest`, `terrain-armoury` |
| Formation cards (6) | `card-chakra`, `card-garuda`, `card-suchi`, `card-makara`, `card-padma`, `card-vajra` |
| Statuses (5) | `status-burn`, `status-shock`, `status-cover`, `status-shield`, `status-concealed` |
| Dodge (4) | `dodge-none`, `dodge-left`, `dodge-right`, `dodge-jump` |
| Interface (14) | `ui-lock`, `ui-timer`, `ui-settings`, `ui-home`, `ui-replay`, `ui-shop`, `ui-coin`, `ui-back`, `ui-close`, `ui-check`, `ui-sound`, `ui-music`, `ui-haptics`, `ui-crown` |
