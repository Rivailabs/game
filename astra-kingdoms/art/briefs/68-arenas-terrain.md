# Ticket 68: two arena sets and terrain props

Acceptance (plan): *Assets meet budgets and avoid misleading collision/cover cues.*

The duel has **no world collision**: projectiles collide only with fighter capsules and each other
(rules: swept capsule tests). Cover exists only as a rules status (Fort terrain, Iron Wall). Any
prop that looks like it could block an arrow therefore misleads the player. `ArenaVariants.Validate`
already rejects props inside the flight volume or in front of the camera; the release gate runs it
and the editor budget validator on both arenas.

### AK-ART-068-A Courtyard arena

| Field | Brief |
| --- | --- |
| Use | Default duel arena (`arena.courtyard`). Seen during every volley playback. |
| Silhouette | Sandstone floor, colonnade and back wall **behind** the fighters, side gates outside the 0-8 m fighting span; strong horizon band so the arrow arc reads against the sky. |
| Scale | Playable strip x = -2..10 m, back wall at z = 7.5 m, pillars 3.2 m tall. |
| Camera distance | Gameplay camera at (4, 2.6, -9.5), 38 deg FOV; nothing between camera and fighters. |
| Palette | Warm sandstone (0.78, 0.68, 0.5), pale stone, terracotta; low-contrast background so effects and archers dominate. |
| Allowed references | Generic sandstone courtyards and colonnades. Forbidden: recognisable temples, mosques, palaces, monuments or shrines; religious carvings; real place names. |
| Device budget | <= 40,000 triangles, <= 16 opaque material batches, baked or simple lighting, no realtime shadow-casting lights, textures <= 1,024 px; whole scene <= 70,000 triangles and <= 60 draw calls with two archers and effects. |
| Licence provenance | Commissioned/kit assets with commercial game licence; every kit listed in the ledger with its licence text and hash. |
| Acceptance | Budget validator passes on the imported scene; reference-phone frame pacing within the 30 fps / p95 <= 35 ms gate. |

### AK-ART-068-B Riverside ghat arena

| Field | Brief |
| --- | --- |
| Use | Second arena (`arena.riverside`) reusing the same rules and navigation (plan: it must not double the validation burden). |
| Silhouette | Stone steps down to water behind the fighters, lamp posts at the far sides, a small generic pavilion in the back. |
| Scale | As Courtyard; water plane at z >= 12 m. |
| Camera distance | As Courtyard. |
| Palette | Grey stone, deep water blue, warm lamp light. Water must not resemble Varuna effects (calm, low-contrast, no white crests). |
| Allowed references | Generic riverside steps. Forbidden: recognisable ghats of real cities, cremation or ritual scenes, deities. "Shrine" placeholder must become a neutral pavilion. |
| Device budget | As Courtyard; animated water by UV scroll only (no extra pass). |
| Licence provenance | As Courtyard. |
| Acceptance | As Courtyard; cultural review signs off the pavilion and steps. |

### AK-ART-068-C Terrain markers (Plain, Fort, River, Forest, Armoury)

| Field | Brief |
| --- | --- |
| Use | Announce the duel's terrain (2 s announcement) and show its defender benefit during the duel. |
| Silhouette | One ground decal + small prop group per terrain at the **defender's side only**: Fort = low parapet behind the defender (cover benefit, but visibly below the arrow path); River = shallow stream strip under the defender's feet; Forest = two saplings beside (not in front of) the defender; Armoury = weapon rack behind; Plain = nothing. Always paired with the terrain icon and label. |
| Scale | Props <= 1.0 m tall, outside the capsule volume and flight path. |
| Camera distance | Gameplay camera about 10 m. |
| Palette | Uses the arena palette; the icon carries the identity. |
| Allowed references | Generic. No flags or emblems. |
| Device budget | <= 3,000 triangles and 1 material per terrain set; counted inside the 40,000-triangle arena budget. |
| Licence provenance | As Courtyard (`model.terrain.<id>`). |
| Acceptance | No prop suggests that it blocks arrows; the Fort parapet is explained as "cover 25 %" in the HUD caption. |
