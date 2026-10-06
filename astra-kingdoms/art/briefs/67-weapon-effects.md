# Ticket 67: twenty weapon-effect assets

Acceptance (plan): *Readable visual identities fit the runtime effects contract.*

**Runtime effects contract** (already in code, `Core/Presentation/Effects.cs`): every effect is a
pooled slot of one `EffectKind` (ProjectileTrail, Impact, Clash, ShieldBlock, Miss, DodgeStreak,
CoverDust, StatusAura) styled by its element's `ElementEffectStyle` = particle **shape** + **motion
pattern** + trail spacing + impact spokes. At most **12 active emitters x 64 live particles**;
outcome effects (impact, clash, shield) outrank decoration when the pool is full. Effects are
presentation only: the authoritative record decides hits, clashes and damage, and nothing a
player must know is carried by an effect alone (captions with glyphs, HP bar and damage numbers
carry the outcome).

Identity order, applied everywhere (plan: "icon shape, label and effect pattern as well as colour"):
**1 shape -> 2 motion -> 3 rhythm/spacing -> 4 colour.** A greyscale capture must still let a tester
name the element and tell the weapons of one element apart.

## Element languages (shared by all four weapons of an element)

### AK-ART-067-A Agni language: flame / flicker

| Field | Brief |
| --- | --- |
| Use | Trails, impacts and Burn status aura of Ember Arrow, Fire Fan, Ash Shield and Sun Lance. |
| Silhouette | Teardrop flame particles (same outline as the Agni icon, `element-agni`); impact = 8-spoke star burst. |
| Scale | Trail particles 0.10-0.18 m (11-20 px); impact burst radius 0.6 m. |
| Camera distance | Gameplay camera about 10 m. |
| Palette | Red-orange core, yellow tips; dark outline so flames read on the sandstone courtyard. |
| Allowed references | Generic fire studies, own sketches. No sacred-fire, ritual (havan) or deity iconography. |
| Device budget | Trail 16 particles, impact 32 (<= 64); one shared additive material on a 256 px element atlas; overdraw checked on the reference phone. |
| Licence provenance | In-house or commissioned VFX with assignment; flipbook textures in the ledger (`texture.vfx.agni`). |
| Contract extras | MotionPattern.Flicker (rising, flickering), trail spacing 0.15 m (dense). |

### AK-ART-067-B Vayu language: spiral / swirl

| Field | Brief |
| --- | --- |
| Use | Gale Arrow, Twin Gust, Cyclone, Sky Dive. |
| Silhouette | Spiral wisps orbiting the flight path; impact = 3-arm vortex. |
| Scale | Orbit radius 0.15 m; particles 0.08-0.12 m. |
| Camera distance | Gameplay camera about 10 m. |
| Palette | Pale teal-white, translucent; outline kept for sky contrast. |
| Allowed references | Generic wind/vortex studies. |
| Device budget | As Agni; alpha-blended, short lifetime to limit overdraw. |
| Licence provenance | As Agni (`texture.vfx.vayu`). |
| Contract extras | MotionPattern.Swirl, trail spacing 0.25 m. |

### AK-ART-067-C Prithvi language: stone / tumble

| Field | Brief |
| --- | --- |
| Use | Stone Arrow, Boulder Shot, Iron Wall, Quake Arrow. |
| Silhouette | Faceted stone chunks (Prithvi icon outline) that tumble and fall; impact = 5-spoke ground crack. |
| Scale | Chunks 0.08-0.2 m; Boulder head 0.24 m (matches its 0.12 m radius x2). |
| Camera distance | Gameplay camera about 10 m. |
| Palette | Ochre and brown with light edges; must separate from the brown ground by outline and drop shadow. |
| Allowed references | Generic rock and dust studies. |
| Device budget | Opaque/alpha-tested chunks preferred (less overdraw); 16/32 particles. |
| Licence provenance | As Agni (`texture.vfx.prithvi`). |
| Contract extras | MotionPattern.Tumble, trail spacing 0.35 m (sparse, heavy). |

### AK-ART-067-D Vidyut language: bolt / zigzag

| Field | Brief |
| --- | --- |
| Use | Spark Arrow, Chain Bolt, Storm Net, Thunder Crown; Shock status aura. |
| Silhouette | Short jagged bolt segments (Vidyut icon outline) that jump sideways; impact = 4-spoke crackle. |
| Scale | Segments 0.12-0.2 m. |
| Camera distance | Gameplay camera about 10 m. |
| Palette | Violet with white core. Avoid full-screen flashes (photosensitivity): no more than 3 flashes per second, none larger than 25 % of the screen. |
| Allowed references | Generic electricity studies. |
| Device budget | Additive, 16/32 particles; flash limited by the rule above. |
| Licence provenance | As Agni (`texture.vfx.vidyut`). |
| Contract extras | MotionPattern.Zigzag, trail spacing 0.20 m. |

### AK-ART-067-E Varuna language: wave / ripple

| Field | Brief |
| --- | --- |
| Use | Tide Arrow, Mist Veil, Flood Arrow, Ocean Call. |
| Silhouette | Smooth wave crests rippling along the path; impact = ring splash with no spokes. |
| Scale | Crest 0.12-0.18 m; splash ring radius 0.5 m. |
| Camera distance | Gameplay camera about 10 m; must remain distinct from the Riverside water backdrop (crest outline, motion, not colour). |
| Palette | Blue with white foam edge. |
| Allowed references | Generic water studies. No river-goddess or sacred-river imagery. |
| Device budget | Alpha-blended; 16/32 particles; short lifetime. |
| Licence provenance | As Agni (`texture.vfx.varuna`). |
| Contract extras | MotionPattern.Ripple, trail spacing 0.30 m. |

## The twenty weapon identities

Each weapon = its element language + one weapon-specific **head/trail signature** and, where the
weapon has an ability, an **ability cue** (status aura or marker) shown with an icon and caption.
Mechanics in brackets come from the rules tables and must not be contradicted by the visuals
(e.g. no homing look for Sky Dive, no clash spark for Boulder Shot).

| # | Weapon | Element | Head / trail signature | Motion note | Impact / ability cue |
| --- | --- | --- | --- | --- | --- |
| 1 | Ember Arrow | Agni | Single flame head, dense ember trail | Normal arc | Burn aura (`status-burn`) on hit for next volley |
| 2 | Gale Arrow | Vayu | Thin arrow wrapped by one spiral | Fast, flat | Sideways push streak to target's right (0.25 m) |
| 3 | Stone Arrow | Prithvi | Stone-tipped head, sparse chunks | Slow, high | Jump-pierce marker when the target jumps |
| 4 | Spark Arrow | Vidyut | Single bolt head, short zigzag trail | Fast | Shock aura (`status-shock`) next volley |
| 5 | Tide Arrow | Varuna | Wave-crest head, ripple trail | Normal | Cleanse ripple removing Burn/Shock icons |
| 6 | Fire Fan | Agni | Three small flames in a fan (3 projectiles, +-3 deg / +-1.5 deg) | Normal, spread | Three small bursts, each can hit/miss independently |
| 7 | Twin Gust | Vayu | Two spirals; the second visibly curves (yaw +4 deg, lateral pull) | Fast, flat, curve | Two small vortices |
| 8 | Boulder Shot | Prithvi | Large tumbling boulder (0.24 m) with no trail glow | Very slow, very high | Ground-burst crater ring (0.55 m full / 0.80 m graze); **never a clash spark** |
| 9 | Chain Bolt | Vidyut | Bolt head with a trailing chain of links | Fast | Extra neutral link-snap marker when the target is covered |
| 10 | Mist Veil | Varuna | Mist cloud around a faint head | Normal, high | Concealment veil (`status-concealed`) over the next volley's inputs |
| 11 | Ash Shield | Agni | Ash-grey ember trail | Normal | Round shield ring in front of the shooter (`status-shield`), flashes once when it blocks |
| 12 | Cyclone | Vayu | Wide spiral (0.08 m radius) | Normal, high | Reversal arrows over the opponent's dodge choice |
| 13 | Iron Wall | Prithvi | Flat slab projectile | Slow, flat | Low wall (`status-cover`) for this and next volley |
| 14 | Storm Net | Vidyut | Mesh net of bolts (0.12 m radius) | Normal | Net overlay on the target: dodge blocked next volley |
| 15 | Flood Arrow | Varuna | Heavy wave head with spray | Slow, high | Cover-wash effect that removes the target's wall before its hit |
| 16 | Sun Lance | Agni | Straight beam-like lance; no arc | Direct | Narrow linear flash, no spokes beyond the Agni 8 |
| 17 | Sky Dive | Vayu | Feathered head that climbs then dives | Very high ballistic (not homing) | Cover-ignore marker on body hit |
| 18 | Quake Arrow | Prithvi | Stone head with ground-shake ring on landing | Slow | Pitch-shift marker (+5 deg) on the target's next aim |
| 19 | Thunder Crown | Vidyut | Crown-shaped bolt cluster | Normal | Advantage x2 badge on an advantaged hit |
| 20 | Ocean Call | Varuna | Large swirling crest with droplets | Slow, high | Heal sparkle (+15) on the shooter once on hit |

Per-weapon budget: reuse the element atlas and materials; a weapon may add at most one signature
texture (<= 256 px) and stays inside the trail 16 / impact 32 particle defaults. Ledger rows:
`texture.vfx.weapon.<id>` for any added texture.
