# Ticket 71: two music tracks

Acceptance (plan): *Rights, looping, volume controls and mobile import settings pass review.*

Plan: "Two music tracks are a V1 content target, not a reason to build an audio-generation lane
before the game is playable." The two placeholder loops (`music-menu.wav`, `music-match.wav`) are
procedural stand-ins that let the music channel, streaming import, loop boundaries and the
independent volume control be tested now. They are not soundtrack candidates.

### AK-ART-071-A Menu track

| Field | Brief |
| --- | --- |
| Use | Home, loadout, shop, settings (`AudioCue.MusicMenu`). Loops indefinitely. |
| Silhouette | Calm, spacious, inviting; a drone foundation with a plucked melodic line; no vocals with words (localisation and cultural review). |
| Scale | 60-120 s loop; seamless boundary (no gap, click or reverb tail cut). |
| Camera distance | Stereo or mono; phone speaker first: check the low end does not disappear on a small speaker. |
| Palette | Original composition inspired by Indian classical instrumentation in general (plucked strings, bansuri-like flute, frame drums) without quoting a specific raga recording, film song or devotional piece. |
| Allowed references | Mood references by description only; no temp-track imitation of a commercial recording. Devotional music, bhajans, mantras and named compositions are excluded. |
| Device budget | Vorbis, quality 0.5, **Streaming** load type, mono unless stereo is approved; <= 1.5 MB per minute in the build. |
| Licence provenance | Commissioned with assignment, or a library track with a game-sync licence covering store trailers; composer, publisher, PRO/registration status recorded; ledger `audio.music.menu` (kind Music) Approved with hash. |
| Contract extras | Loop start/end sample points recorded; loudness about -18 LUFS integrated (sits under effects); fades handled by the mixer, not baked. |
| Acceptance | Loops 10 times without an audible seam on the reference phone; music volume slider at 0 silences it while effects continue; import settings verified by the release gate. |

### AK-ART-071-B Match track

| Field | Brief |
| --- | --- |
| Use | Selection and resolution (`AudioCue.MusicMatch`). Must sit **under** effects and leave room for timer warnings. |
| Silhouette | Steady, low-intensity pulse; no tempo changes tied to gameplay (timer information is visual and in effects, never only in music). |
| Scale | 60-120 s loop; seamless. |
| Camera distance | As AK-ART-071-A. |
| Palette | Same instrument family as the menu track for consistency, with soft percussion. |
| Allowed references | As AK-ART-071-A. |
| Device budget | As AK-ART-071-A (Streaming Vorbis). |
| Licence provenance | As AK-ART-071-A (`audio.music.match`). |
| Contract extras | As AK-ART-071-A; ducking of -6 dB under combat effects is a mixer setting. |
| Acceptance | As AK-ART-071-A; testers with music on can still hear the timer warning. |
