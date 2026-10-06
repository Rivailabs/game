# Astra Kingdoms V1 art and sound briefs (tickets 65-72)

Status: **DRAFT briefs for owner approval.** No production art, audio or store capture exists yet.
The placeholders in `unity/Assets/Art/Placeholder/` (made by `art/placeholders/generate_all.py`)
only exercise the pipeline, import presets and gates.

Plan sources: "Art and sound direction", "Asset contract and production workflow", "Initial asset
budgets for the Astra template", "Physical device quality and release gates", "Launch communication
and growth".

## Brief format

Every asset (or asset family that shares one brief) is a `###` section whose heading starts with a
stable brief ID `AK-ART-<ticket>-<letter>` and that contains a two-column table with **all eight**
plan fields:

| Field | Meaning |
| --- | --- |
| Use | Where and when the player sees or hears it, and what decision it supports |
| Silhouette | The readable outline / sound envelope that identifies it at gameplay size |
| Scale | World size (metres) or pixel size, and its share of the screen at the gameplay camera |
| Camera distance | The real gameplay camera: (4, 2.6, -9.5) m, 38 deg vertical FOV, landscape; about 10 m to each archer |
| Palette | Colours/materials, always paired with a non-colour cue |
| Allowed references | What a vendor or generator may look at; what is forbidden |
| Device budget | Ceilings from "Initial asset budgets" plus memory/download share |
| Licence provenance | Required rights, ledger kind and what the ledger row must contain on approval |

Optional rows: `Contract extras` (attachments, skeleton, motion, audio contract fields from the
Forge asset contract) and `Acceptance` (the ticket's acceptance text made concrete).

`python art/tools/check_briefs.py` (also run by `art/tests`) fails if a brief section misses a field,
if ticket 67 does not list exactly twenty weapon identities, or if ticket 69 does not list forty icons.

## Gameplay-camera arithmetic used in every brief

- Visible height at the fighters' plane: 2 x 9.5 m x tan(19 deg) = **6.5 m**.
- Archer (1.70 m tall incl. bow arm) = **26 % of screen height**: about 190 px on a 720 px-tall
  landscape screen (typical 2 GB reference-class phone, to be confirmed in the device record).
- One metre is about 110 px at the fighters; a 0.04 m arrow radius is **under 5 px**, so projectile
  identity must come from trail shape, head silhouette and motion, never from shaft detail.
- The board and land-cut screens are 2D overlays; icons are authored at 128 px and shown at
  48-96 dp.

## Index

| Ticket | File | Deliverable |
| --- | --- | --- |
| 65 | [65-archer.md](65-archer.md) | Base archer, rig and three outfits |
| 66 | [66-bow-arrow.md](66-bow-arrow.md) | Bow family, arrow and procedural string |
| 67 | [67-weapon-effects.md](67-weapon-effects.md) | Five element languages and twenty weapon-effect identities |
| 68 | [68-arenas-terrain.md](68-arenas-terrain.md) | Courtyard and Riverside arenas, terrain markers and props |
| 69 | [69-ui-store-icons.md](69-ui-store-icons.md) | Forty UI/store icons |
| 70 | [70-sound-effects.md](70-sound-effects.md) | Sound-effect cue list and mix |
| 71 | [71-music.md](71-music.md) | Two music tracks |
| 72 | [72-store-art.md](72-store-art.md) | Store icon, feature graphic, screenshots and video plan |
| all | [cultural-review-checklist.md](cultural-review-checklist.md) | Names, symbols, costumes, marketing language |
