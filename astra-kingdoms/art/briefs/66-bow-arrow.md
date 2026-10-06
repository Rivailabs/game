# Ticket 66: bow and arrow asset set

Acceptance (plan): *Attachments, scale and silhouette pass actual combat-view review.*

Code contract: `BowstringSolver` keeps grip, nock and arrow collinear at every aim; the string is a
procedural `LineRenderer` driven from draw state, so the bow mesh **has no string geometry**. The
release marker (`OnReleaseArrow`) starts the recorded projectile flight.

### AK-ART-066-A Bow family (Plain, Carved, Starlit, Sunrise)

| Field | Brief |
| --- | --- |
| Use | Held by both archers through selection, draw, release and dodge; locker bow skins (`bow.plain`, `bow.carved`, `bow.starlit`, `bow.sunrise`). |
| Silhouette | Recurve bow with a clear grip block and two limb tips; all four skins share one mesh outline (skins change material and small tip ornaments only) so draw strength never appears to differ. |
| Scale | 1.40 m tip to tip; grip 0.12 m; about 150 px on screen. |
| Camera distance | Gameplay camera about 10 m; the limb tips must stay visible against both arena backgrounds. |
| Palette | Plain 0x7A5230/0x3B2A1A; Carved 0x8E6A3E/0xD8C08A; Starlit 0x2C2F6B/0xF2E6A0; Sunrise 0xE07A2E/0xFFD27A. |
| Allowed references | Generic composite and wooden recurve bows; no replicas of museum pieces or named legendary bows. |
| Device budget | <= 1,500 triangles per bow, one material, texture <= 512 px (shared atlas preferred). |
| Licence provenance | Commissioned or rights-cleared generation; ledger `model.bow.<skin>` with FBX hash; paid skin rights cover sale. |
| Contract extras | Transforms `limb_top`, `limb_bottom` (string anchors) and `grip` aligned to the archer's `bow_grip`; pivot at grip centre; +Y along the limb axis. |
| Acceptance | Grip sits in the hand without penetration through draw/hold/release; string anchors follow limb tips; silhouette approved in a gameplay-camera screenshot on the reference phone. |

### AK-ART-066-B Arrow (base projectile mesh)

| Field | Brief |
| --- | --- |
| Use | Nocked arrow during draw/hold, and the base projectile for single-arrow weapons before element effects are added (ticket 67). |
| Silhouette | Oversized head and fletching (about 1.5x realistic) so direction reads at 5 px shaft width; head shape is neutral (element identity comes from the effect layer). |
| Scale | 0.80 m long; collider radius is defined by the rules (0.03-0.12 m per weapon), never by the mesh. |
| Camera distance | Gameplay camera about 10 m; flight crosses 8 m of screen in 0.3-1.5 s. |
| Palette | Light shaft, dark head, contrasting fletching; readable on sky and ground. |
| Allowed references | Generic arrows; no real-world military markings. |
| Device budget | <= 200 triangles, shares the bow atlas; pooled (no runtime instantiation per volley). |
| Licence provenance | As AK-ART-066-A; ledger `model.arrow.base`. |
| Contract extras | Pivot at the nock; +Z towards the head; `trail_anchor` transform at the head for effects. |
| Acceptance | Projectile silhouette remains clear at the gameplay camera, including during clashes. |
