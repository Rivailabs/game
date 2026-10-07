# Ticket 65: archer asset, rig and three outfits

Acceptance (plan): *Visual/technical brief, deformation and licence provenance are approved.*

Process (plan, "Asset contract"): brief -> concept approval -> generation or commission -> Blender
normalisation -> technical report and turntable -> visual approval **at the gameplay camera** ->
rig -> deformation review -> motion/retargeting -> contact/transition review -> Unity prefab ->
physical-device evidence. Approve a neutral pose and proportions **before** paying for rigging and
motion. Character, bow, arrow and bowstring stay separate assets.

The code contract already exists: `ArcherAttachments` (`hand_l`, `hand_r`, `bow_grip`,
`string_nock`, `arrow_spawn`), `ArcherPoseLibrary` / `ArcherAnimatorBuilder` (clip set and the
`OnReleaseArrow` event), `AssetBudgetValidator` (8,000 triangles, 50 deforming bones, 4 weights,
2 materials). The editor release gate (`Astra Kingdoms/Release/Run Release Gate`) runs those
checks on the imported prefab.

### AK-ART-065-A Base archer (neutral body, shared rig)

| Field | Brief |
| --- | --- |
| Use | Both fighters in every duel (two instances on screen), result screen, locker preview. The player reads stance, draw, dodge direction, hit and defeat from it. |
| Silhouette | An original mortal archer, not a deity, saint, historical ruler or known character. Clear head, shoulders and bow arm; wide, planted stance; draw arm and elbow visible against the sky band; dodge left/right and jump must read as different outlines in one frame. No capes or loose cloth that hide the bow arm. |
| Scale | 1.70 m standing height (capsule axis 0.35-1.45 m, radius 0.25 m in the rules). About 190 px tall on a 720 px landscape screen. |
| Camera distance | Side gameplay camera about 10 m away, 38 deg FOV; the two archers face each other 8 m apart. Locker preview at 3 m is secondary and must not drive detail. |
| Palette | Neutral skin and base cloth; outfit colour is applied by material swap. Player A and B are distinguished by side, name plate and outline pattern as well as tint. |
| Allowed references | Original Indian-epic-inspired fantasy: textile weaves, wrapped garments, leather bracers, sandstone-era jewellery shapes in generic form. Forbidden: images of gods or revered figures, temple idols, film/TV/game characters, real people, copyrighted costume designs, actual regimental or religious insignia. Every reference image is recorded with its rights in the ledger (`kind: Reference`). |
| Device budget | <= 8,000 triangles, <= 50 deforming bones, <= 4 weights per vertex, <= 2 materials, textures <= 1,024 px (ASTC 6x6 on Android), only equipped outfit loaded. Two archers + bows within the 70,000-triangle scene and 60 draw-call budgets. |
| Licence provenance | Commissioned with written assignment or exclusive licence covering games, store marketing and derivatives, worldwide; or a generation route whose terms allow commercial use, with model/service/version/date recorded. Ledger row `model.archer.base` (kind Model) moves to Approved only with source, licence, rights holder, territory, provenance chain (inputs -> generation -> Blender cleanup) and the SHA-256 of the imported FBX. |
| Contract extras | Units metres, +Y up, +Z forward, pivot between the feet. Named transforms: `hand_l`, `hand_r`, `bow_grip`, `string_nock`, `arrow_spawn`. Humanoid-compatible skeleton with stable joint names; rest pose A-pose. Clips: idle (loop), draw, hold (loop), release (event `OnReleaseArrow`), recover, hit, dodge_left, dodge_right, jump, defeat, victory; in-place, 30 fps. |
| Acceptance | Neutral pose approved at gameplay camera; deformation review of shoulders, elbows, hands, grip, feet, bow penetration and root drift in motion; two equipped archers animate in the representative combat scene on the reference phone within frame/memory gates. |

### AK-ART-065-B Outfit "Wanderer" (default)

| Field | Brief |
| --- | --- |
| Use | Default outfit for every new player (`outfit.wanderer`, CosmeticSource.Default). |
| Silhouette | Short wrapped tunic and trousers, simple shoulder wrap; keeps the base silhouette unchanged (cosmetics never alter hitbox or readability). |
| Scale | Same rig and proportions as AK-ART-065-A; no added volume over 3 cm from the body. |
| Camera distance | As AK-ART-065-A. |
| Palette | Earth brown 0x6B5B45 with sand accents 0xC9B38A (from `CosmeticCatalog`). |
| Allowed references | Generic travelling clothing of no specific community; no sacred thread, no religious marks. |
| Device budget | Shares the base mesh; outfit is a material + optional <= 1,500-triangle overlay; one 1,024 px texture set. |
| Licence provenance | As AK-ART-065-A; ledger `texture.outfit.wanderer` + overlay model row. |
| Acceptance | Reads as a different person from the two paid/earned outfits in greyscale at gameplay size. |

### AK-ART-065-C Outfit "Ember Guard" (coin shop)

| Field | Brief |
| --- | --- |
| Use | Earned-coin cosmetic (`outfit.ember-guard`, 150 coins). Cosmetic only. |
| Silhouette | Layered guard tunic with a stiff collar and arm guards; no weapon-like shapes that could be mistaken for an ability. |
| Scale | Same rig; overlay volume <= 5 cm. |
| Camera distance | As AK-ART-065-A. |
| Palette | Deep red 0x9C2F1F and amber 0xE3A33B. Must not suggest the Agni element advantage: element identity stays in effects and icons, never in outfits. |
| Allowed references | Original fantasy guard costume; no real regimental insignia, no flags. |
| Device budget | Overlay <= 1,500 triangles; one 1,024 px texture set. |
| Licence provenance | As AK-ART-065-A; ledger `texture.outfit.ember-guard`. |
| Acceptance | Store/locker text accurately states "cosmetic only". |

### AK-ART-065-D Outfit "Tide Warden" (paid)

| Field | Brief |
| --- | --- |
| Use | Paid cosmetic (`ak.cosmetic.outfit_tide_warden`). Store screenshot candidate. |
| Silhouette | Long sash and wrapped headcloth kept above the shoulders so the draw arm stays visible. |
| Scale | Same rig; overlay volume <= 5 cm; sash animated by bones already in the 50-bone budget (no cloth simulation). |
| Camera distance | As AK-ART-065-A. |
| Palette | Blue 0x1F4E8C and sea-glass 0x8FD3E8. Must not imply Varuna advantage (same rule as above). |
| Allowed references | Original coastal-guard fantasy costume; no religious garments. |
| Device budget | Overlay <= 1,500 triangles; one 1,024 px texture set. |
| Licence provenance | As AK-ART-065-A; ledger `texture.outfit.tide-warden`. Paid item: rights must cover sale of the item. |
| Acceptance | Purchase description and screenshot match the in-game item exactly. |
