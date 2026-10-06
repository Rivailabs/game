# Ticket 70: sound-effect library

Acceptance (plan): *Required cues are distinct, licensed and mixed appropriately.*

Cue registry: `AudioCue` / `AudioCueRegistry` in `Core/Audio/AudioCues.cs`. Every cue already has a
**visual counterpart** so timing and combat stay understandable with sound off. Placeholders for
every cue (plus five element impacts) are in `unity/Assets/Art/Placeholder/Audio/Sfx/` and the
placeholder ledger; `art/tests` checks mono, no clipping, click-free edges, durations and that no
two cues have near-identical spectra.

### AK-ART-070-A Effects library (licensed)

| Field | Brief |
| --- | --- |
| Use | One clip (or a small random set of 2-3 variants) per cue below; played by `AudioService` on the Effects or UI channel. |
| Silhouette | Each cue has a distinct envelope and band (sound "silhouette"): UI = short and high, combat = mid/low with transients, jingles = pitched. No two cues confusable in a blind A/B test with 3 testers. |
| Scale | Durations: UI <= 0.15 s; combat 0.2-0.5 s; jingles <= 1.5 s. |
| Camera distance | Mono, centred (shared phone held between players); no positional audio needed in V1. |
| Palette | Organic wood/string/stone/water textures plus soft metal; avoid harsh 2-4 kHz peaks on phone speakers. |
| Allowed references | A licensed sound library or rights-qualified provider (plan). No sounds ripped from films, games or devotional recordings (bells, conch, temple chants are excluded). |
| Device budget | Mono, 22.05-44.1 kHz; short clips Decompress On Load (ADPCM), longer ones Compressed In Memory (Vorbis q 0.5); total effects memory <= 6 MB decompressed. Import preset: `ArtImportPolicy` (Editor/Release). |
| Licence provenance | Library licence permitting use in a distributed game, worldwide, no per-unit royalty; licence document stored outside the repo with its reference in the ledger (`audio.sfx.<cue>`, kind Sound, Approved, hash of the imported file). |
| Contract extras | Audio contract fields: source rights, clip purpose, format, sample rate, loudness target (effects peak <= -1 dBFS, integrated about -16 LUFS on the mix bus), loop boundaries (none for effects), import/compression policy. |
| Acceptance | No clipping at the approved mix; independent music and effects controls work (settings test); with sound off, every cue's visual counterpart is present. |

### Cue list

| Cue (AudioCue) | Placeholder file | Distinctness note | Visual counterpart (from the registry) |
| --- | --- | --- | --- |
| UiClick | sfx-ui-click.wav | very short high tick | Button pressed state |
| CardSelect | sfx-card-select.wav | two-step rising chirp | Card check mark and outline |
| Lock | sfx-lock.wav | low falling clunk | "Locked" / "Ready" |
| Draw | sfx-draw.wav | rising creak | Draw pose, string pulls back |
| Release | sfx-release.wav | plucked twang + snap | String snaps, arrow appears |
| Clash | sfx-clash.wav | bright inharmonic metal | Starburst clash marker |
| Hit | sfx-hit.wav | low thud + dust | Impact shape, damage number, HP bar |
| Miss | sfx-miss.wav | airy whoosh | Dust ring and "Miss" tag |
| Dodge | sfx-dodge.wav | quick swish | Dodge pose and direction arrow |
| ShieldBlock | sfx-shield-block.wav | dull metal + thud | Shield ring flash, "Blocked" |
| LandTransfer | sfx-land-transfer.wav | three rising plucks | Cells sweep, totals count |
| Victory | sfx-victory.wav | four rising plucks | Result title and crown |
| Defeat | sfx-defeat.wav | three falling plucks | Result title and reason |
| TimerWarning | sfx-timer-warning.wav | two beeps | Timer "!" badge (pulse off with reduced motion) |
| Hit (Agni) | sfx-impact-agni.wav | crackle | Flame impact burst |
| Hit (Vayu) | sfx-impact-vayu.wav | swirling whoosh | Vortex impact |
| Hit (Prithvi) | sfx-impact-prithvi.wav | heavy thud and rumble | Stone crack |
| Hit (Vidyut) | sfx-impact-vidyut.wav | electric zap | Bolt crackle |
| Hit (Varuna) | sfx-impact-varuna.wav | splash and bubbles | Ring splash |
