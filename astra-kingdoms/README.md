# Astra Kingdoms

A short competitive strategy game for Android phones. Two players secretly prepare a bow shot.
They watch both shots resolve at the same time, and the duel winner takes territory on a
circular board by drawing a cut. The source of truth is [`docs/PLAN.md`](../docs/PLAN.md),
especially the rules chapter. This folder holds:

- the engine-independent rules assembly,
- its tests,
- a bot simulator,
- the Unity 6 client for the **pilot**: shared phone, five starter weapons, plain terrain, the two
  cards Chakra and Suchi, deterministic replay.

> **Status in one line.** Everything that can be verified without Unity or a phone is verified:
> `dotnet build` (0 warnings) and `dotnet test` (rules + client core). The Unity client is written
> and compiles against API stubs. It has **not** been opened in a Unity editor, built for Android,
> run on a phone or played by people. Those gates are open (see the ticket table below).

## Ruleset AK-TR-1 at a glance

The numbers are *proposed, unvalidated design constants* (plan: "Game rules and acceptance
cases"). Any change produces a new rules version and a new rules hash.

| Area | Rule |
| --- | --- |
| Match | Two players, up to 8 rounds. The attacker alternates and the first attacker comes from the seed. Win by reaching ≥ 45,936 cells (90%) or by having more cells after round 8. Equal cells is a draw. |
| Duel | Up to three simultaneous volleys. Each player starts at 100.00 HP (stored as integer hundredths). A player who is the only one left above 0 HP wins; otherwise higher HP after volley 3 wins. Equal HP, or both reaching zero together, is a draw and no land moves. |
| Volley input | Weapon, pitch and yaw in 0.25° steps, power 70–100%, dodge (None/Left/Right/Jump). Each choice is locked once and kept secret until both players lock or the deadline passes. A player with no lock at the deadline gets a **Pass**. Two selection timeouts in a row forfeit the match. |
| Combat | Q32.32 fixed point, 120 ticks/s, at most 360 ticks. Fighters stand at x = 0 m and x = 8 m and are tested as capsules (axis 0.35–1.45 m, radius 0.25 m). Collisions are swept, so fast arrows cannot pass through targets. Clashes are mass-based. Elements: advantage ×1.5, disadvantage ×0.5. |
| Elements | Agni (flame), Vayu (spiral), Prithvi (stone), Vidyut (bolt), Varuna (wave). Each element beats two others. In the pilot the five starter weapons are one per element. |
| Board | 256×256 grid with 51,040 active cells in a circle, 25,520 per player at the start. Area is always an exact cell count. |
| Land | Allowance `Q = min(L, floor(N × max(30000, C × D) / 1e6))`, where C is the card's cap % and D is the HP difference in hundredths. The pilot offers Chakra (12%) and Suchi (18%). The cut must lie inside the posed card envelope and include a border anchor. Only the 4-connected component of the anchor is kept. A cut has at most 128 vertices. Auto Cut takes cells breadth-first. |
| Shared-phone clock | 2 s terrain announcement. 12 s per player, entered privately in sequence. One opaque handover of at most 6 s. 2.5 s replay. 20 s card and cut window. Pause is available only in local practice. |
| Determinism | Seeded SHA-256 streams and integer trigonometry tables, with no floats in authoritative paths. A record holds the seed and command log, and replays to identical state hashes. |

## Folder layout

| Path | Contents |
| --- | --- |
| `src/AstraKingdoms.Rules/` | Authoritative rules (netstandard2.1, C# 9): `Core/`, `Combat/`, `Land/`, `Match/` (MatchEngine, commands, private views), `Bots/`, `Replay/`. It is also a **local Unity package** (`package.json` = `com.astrakingdoms.rules`, `AstraKingdoms.Rules.asmdef` with `noEngineReferences`). |
| `src/AstraKingdoms.Meta/` | V1 progression and monetization (tickets 57-64): XP/unlocks, daily tasks, cosmetics, shop, Play Billing entitlements, ad policy, analytics and metrics, deletion. Also a local Unity package (`com.astrakingdoms.meta`); `Server/` is server-only. See its [README](src/AstraKingdoms.Meta/README.md) and [`docs/privacy-data-map.md`](docs/privacy-data-map.md) (draft, needs legal review). |
| `tests/AstraKingdoms.Meta.Tests/` | NUnit tests for the meta library and source checks of the Unity meta layer. |
| `tests/AstraKingdoms.Rules.Tests/` | 248 NUnit tests: acceptance cases, golden vectors, privacy, timeouts, replay. |
| `tests/AstraKingdoms.Client.Core.Tests/` | 51 NUnit tests that run the Unity client's engine-independent logic under dotnet. |
| `tools/AstraKingdoms.Sim/` | Bot-policy screening simulator (reports in `reports/`). |
| `tools/UnityStubs/`, `tools/UnityCompileCheck/` | Compile-only Unity API stubs and the projects that compile the Unity client against them (see "How the Unity code is verified here"). |
| `unity/` | The Unity 6 project (details below). |
| `reports/` | Simulator output. |

Unity project:

| Path | Contents |
| --- | --- |
| `unity/Packages/manifest.json` | URP, Input System, uGUI 2.0 (includes TextMesh Pro in Unity 6), Test Framework, and `"com.astrakingdoms.rules": "file:../../src/AstraKingdoms.Rules"`. |
| `unity/ProjectSettings/ProjectVersion.txt` | Editor pin (see Preflight). The other ProjectSettings assets are generated by the editor; they are not hand-written. |
| `unity/Assets/Scripts/Core/` | `AstraKingdoms.Client.Core` (no UnityEngine). Contains the host clock and shared-phone sequencing (`LocalMatchHost`), stroke simplification, board raster, cut helpers, playback timeline, aim preview, explanations, element glyph shapes, localization tables, settings model, automation report and replay stepper. |
| `unity/Assets/Scripts/Runtime/` | `AstraKingdoms.Client`. Contains GameBootstrap, GameFlow, MatchController, the arena view and resolution playback, and screens built in code with uGUI: Home, Settings, Loadout, Privacy/handover, Selection, HUD, Announcement, Resolution, Land, Result and Replay viewer. `Automation/` is development-only. |
| `unity/Assets/Scripts/Meta/` | `AstraKingdoms.Client.Meta`: profile, daily tasks, locker, shop, privacy/deletion screens, plugged in through `GameFlow.UiBuilt`. `Store/Iap/` is the Unity IAP adapter (compiled only when com.unity.purchasing is installed). |
| `unity/Assets/Editor/` | Grey-box scene builder, project and Android configuration, and `Forge.Build.BuildAndroid`. |
| `unity/Assets/Tests/` | Unity Edit Mode and Play Mode tests. |
| `unity/Assets/Resources/Localization/` | `en.txt` (complete reference), plus `hi.txt` and `kn.txt` (drafts, **need fluent-speaker review**). |

## Running the .NET side

```bash
cd astra-kingdoms
dotnet build            # rules, sim, client core, Unity compile checks: 0 warnings, 0 errors
dotnet test             # 248 rules tests + 51 client-core tests
dotnet run -c Release --project tools/AstraKingdoms.Sim -- --matches 2000   # bot screening report
```

Build outputs go to `astra-kingdoms/.build/` (see `Directory.Build.props`), so `src/AstraKingdoms.Rules`
stays clean for Unity.

## Opening the Unity project

1. Install the Unity 6 LTS editor recorded during preflight, with Android Build Support (SDK, NDK,
   OpenJDK) from the official dependency matrix.
2. Open `astra-kingdoms/unity` in Unity Hub. The rules arrive as the local package
   `com.astrakingdoms.rules`.
3. **The first open generates every `.meta` file**: under `unity/Assets` and also inside
   `src/AstraKingdoms.Rules/`, because Unity writes metas into local `file:` packages. Commit them
   all in one change straight away (plan [S04]: dropping .meta files breaks references). None were
   written by hand, because wrong GUIDs are worse than missing ones.
4. If the Input System package asks whether to enable the new input backends, either answer
   works. With the new system active, `GameFlow` uses `InputSystemUIInputModule`; otherwise it uses
   `StandaloneInputModule` (`#if ENABLE_INPUT_SYSTEM`). All game input goes through uGUI pointer
   events.
5. Run **Astra Kingdoms → Configure Project (Android)**. It sets the package id
   `com.rivailabs.astrakingdoms`, IL2CPP, ARM64 only, min SDK 23 (proposed), landscape, the Activity
   entry point and a URP asset. Then run **Astra Kingdoms → Build Grey-box Scene**.
6. Open the Test Runner and run the Edit Mode and Play Mode suites.

### Command line (what Game Forge runs)

```bash
UNITY=/path/to/Unity   # from the frozen toolchain manifest

# Ticket 4: rebuild the script-created grey-box scene (exit 0/1)
$UNITY -batchmode -nographics -quit -projectPath astra-kingdoms/unity \
  -executeMethod AstraKingdoms.EditorTools.GreyBoxSceneBuilder.BuildFromCommandLine -logFile -

# Android build (Forge's unity_build check): regenerates the scene, applies settings,
# writes Builds/Android/astra.apk and Builds/Android/build-manifest.json
$UNITY -batchmode -nographics -quit -projectPath astra-kingdoms/unity -buildTarget Android \
  -executeMethod Forge.Build.BuildAndroid -logFile -
#   add -releaseBuild (or AK_RELEASE_BUILD=1) for a release-configuration measurement build

# Unity tests
$UNITY -batchmode -nographics -projectPath astra-kingdoms/unity -runTests -testPlatform EditMode -testResults editmode.xml
$UNITY -batchmode -nographics -projectPath astra-kingdoms/unity -runTests -testPlatform PlayMode -testResults playmode.xml
```

`build-manifest.json` records:

- the commit (`git rev-parse HEAD` or `FORGE_COMMIT`) and whether the tree was dirty,
- the Unity version and a UTC timestamp,
- dev/release, package id, IL2CPP/ARM64, min SDK,
- the rules version and rules hash,
- the APK size and SHA-256.

### Automation, evidence and replay (development builds only)

`Assets/Scripts/Runtime/Automation/` compiles only with `DEVELOPMENT_BUILD || UNITY_EDITOR`. Release
builds do not contain it (the `RuntimeAndroidRelease` compile check confirms this). It plays full
matches with both seats as labelled bots. They go through the same host, screens and arena that
people use, and act through semantic commands. For each seed it writes the match record and
verifies it with `Replayer.Verify`. It records frame times (p50/p95/p99, stalls over 100 ms) and
Profiler memory peaks.

```bash
# Editor / desktop player
-autoplay 1,2,20261006 -autoplaySpeed 4 -quitAfterAutoplay

# Android (what the Forge device service does; extras are read from the launch intent)
adb shell am start -W -n com.rivailabs.astrakingdoms/com.unity3d.player.UnityPlayerActivity -e forge_scenario pilot-smoke
adb shell am start -W -n com.rivailabs.astrakingdoms/com.unity3d.player.UnityPlayerActivity -e autoplay 7 -e autoplaySpeed 2
adb logcat -d | grep FORGE_SCENARIO_RESULT          # PASS|FAIL plus p95/p99/stalls/peak memory
adb pull /sdcard/Android/data/com.rivailabs.astrakingdoms/files/automation/   # report JSON + match records
adb shell dumpsys meminfo com.rivailabs.astrakingdoms   # total PSS: the plan's memory gate (not a Profiler number)
```

- `pilot-smoke` plays the three approved seeds (1, 2, 20261006) with the match clock at 8×. Each
  bot match takes about 80 s of match-clock time, so the run lasts about 30 s. Every frame is still
  rendered and measured. The run prints `FORGE_SCENARIO_RESULT`, writes `automation/autoplay-report.json`
  and quits.
- Finished matches in development builds are also saved to `records/<match-id>.json` and
  `records/latest-record.json`.
- **Home → Replay viewer** (or `-replay <path>`) steps through a record using a fresh authoritative
  engine. It plays each volley from the engine's own tracks, redraws the board after each cut, and
  can run the full `Replayer.Verify` check.
- This interface drives semantic commands, not touches. The plan also requires real touch-input
  scenarios; those are not written yet.

## How the Unity code is verified here (and what that does not prove)

Unity is not installed in this environment. Verification has three layers:

1. **Real tests of the client's logic.** `unity/Assets/Scripts/Core` has no UnityEngine
   dependency. `tests/AstraKingdoms.Client.Core.Tests` runs it under `dotnet test`. Covered:
   shared-phone deadlines, handover, entry alternation, pause rules, timeout forfeits, full bot
   matches whose records replay, a human cut where the preview equals the accepted transfer,
   rematch isolation, the 128-vertex simplifier, rules snapping, raster pixels = cell counts,
   playback ≤ 2.5 s, the aim preview's launch point, explanations, distinct element glyphs,
   localization completeness (every literal key used in the client exists, and hi/kn have the same
   keys and parameters), and the automation report.
2. **Compile checks against stubs.** `tools/UnityStubs` contains hand-written signatures of the
   Unity 6 APIs used. They were written from knowledge of the API, not generated from Unity's
   assemblies. `tools/UnityCompileCheck` compiles Runtime in three define sets (Editor, Android
   development, Android release), plus Editor and the Unity test suites, all with warnings as
   errors. This catches C# errors and misuse of our own types. It **cannot** catch a stub signature
   that differs from the real Unity API, Unity-specific compile rules, serialization, rendering,
   layout or device behaviour.
3. **Not done: a real Unity compile, the Edit Mode and Play Mode runs, an Android build, a device
   run.** These need the pinned editor and the reference phone. APIs most worth checking first in
   the editor:
   - `PlayerSettings.Android.applicationEntry`
   - `UniversalRenderPipelineAsset.Create(ScriptableRendererData)`
   - `GraphicsSettings.defaultRenderPipeline`
   - `Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")`
   - `Object.FindFirstObjectByType`
   - the Input System UI module

## Pilot ticket status (plan: "Pilot ten actionable tickets")

Legend: **done in code** = written, and verified only as described above. **needs Unity editor** =
first compile, Unity tests, scene and build. **needs physical phone** = the reference 2 GB Android
device. **needs human testers** = the five-tester observation and two-person play.

| # | Deliverable | Status | What remains |
| --- | --- | --- | --- |
| 1 | Rules/configuration skeleton and pilot data | Done in code; rules tests pass | — |
| 2 | Duel state machine, locks, resolution, ties, margin | Done in code; acceptance tests pass | — |
| 3 | Authoritative trajectory and collision | Done in code; golden vectors pass | — |
| 4 | Script-created grey-box duel scene and Android build | Done in code: scene builder (menu + `-executeMethod`), Android configuration, `Forge.Build.BuildAndroid` with build manifest. Fighter positions are checked by an Edit Mode test (not yet run). | Needs Unity editor (first open, metas, build). Needs physical phone ("both fighters display on the named phone"). |
| 5 | Touch input, aim/power, private shared-phone handoff | Done in code: private sequential entry, opaque handover, view entitlement (dotnet-tested); touch aim pad, power slider, dodge, lock (compile-checked only) | Needs Unity editor, physical phone and human testers |
| 6 | Result presentation from resolved events | Done in code: playback interpolates engine tracks and is compressed to ≤ 2.5 s; HP is held until the arrows land; the explanation is built from `VolleyExplanation` (dotnet-tested) | Needs Unity editor and phone. The video-versus-event-record comparison is still to do. |
| 7 | Logical-cell ownership, rasterization, area accounting | Rules done earlier and tested. Client texture uses one pixel per cell (dotnet test; an Edit Mode texture test is written). | Needs Unity editor to run the texture test |
| 8 | Card-constrained cut input with visible legal boundary | Done in code: envelope overlay, drag and two-finger pose, finger cut snapped and simplified, live engine preview with specific reasons, Auto Cut, confirm (dotnet-tested at the host level) | Needs Unity editor, physical phone and human testers (usability of drawing within 20 s) |
| 9 | Complete shared-phone match and rematch | Done in code: full loop and Rematch, which builds a new seed, engine and clean screens (dotnet-tested with scripted players and bots) | Needs physical phone and human testers (two people finish the loop) |
| 10 | Replay regression, phone autoplay, baseline measurements | Done in code: automation runner, evidence report, replay verification, replay viewer (dotnet-tested; Play Mode test written) | Needs physical phone (autoplay run, logs, video, PSS, frame pacing). Download size from the build artifact. Owner decisions listed below. |

The pilot gate also requires five observed testers (controls, finishing a cut, explaining a loss,
a voluntary rematch). Nothing in code replaces that.

## Known issues and decisions needed

- **Package id mismatch with Forge.** The client uses `com.rivailabs.astrakingdoms`, as requested.
  `game-forge/projects/astra-kingdoms/project.toml` (owner-protected) still says
  `package = "com.astrakingdoms.game"` for `device-smoke`. One of them must change before the device
  check can launch the app. The APK path (`Builds/Android/astra.apk`) and activity
  (`com.unity3d.player.UnityPlayerActivity`, forced via `applicationEntry = Activity`) already match.
- **Device-check wait time.** Forge waits 45 s. `pilot-smoke` needs about 30 s plus app start;
  60 s would leave headroom.
- **Unity version pin.** `ProjectVersion.txt` holds `6000.0.23f1`, the first Unity 6 LTS release, as
  a baseline only. Preflight must install the current 6000.0 LTS patch, let the editor rewrite the
  file (with its revision line), and record the matching Android SDK/NDK/JDK. Package versions in
  `manifest.json` (URP 17.0.3, Input System 1.11.2, Test Framework 1.4.5, uGUI 2.0.0) will be
  updated by that editor.
- **Hindi and Kannada text will not render correctly yet.** The UI uses legacy uGUI `Text` with the
  built-in font. That font has no Devanagari or Kannada glyphs, and uGUI does no complex-script
  shaping (conjuncts, vowel signs). Shipping these languages needs a licensed font with coverage
  and a shaping-capable text path, plus native review of the draft strings. English is complete.
- **Minimum SDK 23** is Unity 6.0's floor, not a decision. Confirm it against the reference phone.
- **Audio** is generated placeholder tones or silence (nothing to license), routed through the
  `AudioCue` registry; the licensed library is still to come (see "V1 tickets 24-48" below).
- **Tutorial** exists in code (ticket 45) but has not been tried by new testers.
- **Aim preview** shows only the first 0.375 s of the arc, so it reads the angle without solving
  the shot. That is a tuning choice for playtests.

### Simulator balance flags (`reports/bot-screening-AK-TR-1-n2000.md`)

These are bot-policy screening results, **not balance proof**. They come from the V1 Full catalog
with five terrains, so the pilot's starter set and plain terrain avoid most of them. Use them to
choose what human tests to run.

- **Weapons outside the provisional 45–55% band.**
  - Ash Shield wins 90.5% of volleys (its duel win share is 58.0%).
  - Fire Fan 63.3% and Ember Arrow 62.3%.
  - Mist Veil 22.4%.
  - Stone Arrow 32.0% (a starter weapon), Sky Dive 32.6%, Cyclone 34.0%, Flood Arrow 35.5%.
- **Element totals outside the provisional 47–53% band.** Agni 65.4%, Vidyut 55.3%, Vayu 44.7%,
  Prithvi 42.6%, Varuna 38.9%. Agni beats Vayu 92.5% and Prithvi 89.9% of decisive volleys.
- **River terrain.** Defenders win 71.0% of duels on River.
- **First attacker.** About 50% overall, but 41.3% in Hard-versus-Hard play.
- **Match length.** 83.3% of matches reach round 8; 20.3% end by the 90% shortcut.
- **Comebacks.** Almost none: a player below 35% of the board after duel 4 wins 2.6% of mirror
  matches.

## V1 tickets 24-48: balance, duel and land presentation, screens

Status legend as above. "Done in code" means dotnet-tested logic (rules tests 279, client-core tests
119 after this work) plus compile-checked Unity code; nothing here has run in a Unity editor or on
a phone, and no person has played it.

| # | Deliverable | Status | What remains |
| --- | --- | --- | --- |
| 24 | Versioned balance publication and rollback | Done in code: `src/AstraKingdoms.Rules/Balance/` — `TunableSchema` (closed list; board, physics, trig, abilities stay frozen), `BalanceBundle` (`AK-BALANCE-BUNDLE/1` JSON, new ID per change, effective rules hash), `BalanceValidator`, `BalanceChannel` (append-only log, idempotent per-match pins, rollback for new matches only, IDs never reused) | **Blocked on a parameterized engine:** AK-TR-1 compiles its constants, so a tuned bundle is refused as `NOT_EXECUTABLE`; it can be validated, hashed and reviewed but not served. Server-side storage is the online agent's work. |
| 25 | Human/bot balance review report | Done in code: the simulator builds a source-independent `MatchObservation` and one stratified report (pairings, first attacker, unlock cohorts, comebacks by checkpoint and deficit, elements, terrain, weapons usage-conditioned vs loadout-contained, Wilson + Bonferroni flags, "insufficient" strata). Human records use `AK-PLAYTEST-RECORD/1` (match record + pseudonymous seats + consent, replay-verified on ingest). Reports: `reports/balance-review-AK-TR-1-n2000-cohorts.md`; format example in `tools/AstraKingdoms.Sim/examples/` (synthetic, skipped unless `--include-synthetic`). | No human playtest data exists yet. Bot cohorts use a stated familiarity assumption. |
| 26 | Archer prefab and rig import | Done in code: `ArcherAttachments` contract, placeholder `ArcherRig` from primitives, editor `AssetBudgetValidatorMenu` with the plan's ceilings (`AssetBudgets`) | Needs the approved archer (ticket 65), the editor and deformation review |
| 27 | Archer animation controller | Done in code: required clip set and transitions in `ArcherPoseLibrary`/`ArcherAnimator` (tested continuity and a single release marker); `ArcherAnimatorBuilder` writes the Animator controller with the `OnReleaseArrow` event | Controller never generated in an editor; production clips |
| 28 | Bow, arrow and procedural string | Done in code: `BowstringSolver` (grip, nock and arrow collinear at every aim), LineRenderer string, nocked arrow | Visual check at the gameplay camera |
| 29 | Aim and power preview | Done in code: `AimController` feeds both the preview and the lock; degree ticks, limit markers, timing dots | Touch feel on the phone; light haptics need an Android plugin (`Haptics.Tick` is a no-op seam) |
| 30 | Selection, lock and reveal | Done in code: `RevealPolicy` (tested over every stage), locked state, ready flags only | Human check |
| 31-34 | Playback, pooled effects, clash/impact/damage, dodge/cover | Done in code: `VolleyCueBuilder` derives every cue from the record (tested against 4 bot matches); `EffectBudgetPool` (12 x 64) + `EffectPoolView`; captions with glyphs; release marker starts the recorded flight | Visual and frame-time checks on the phone |
| 35 | HUD | Done in code: `HudModel` (bar + number, glyph statuses, urgency badge, compact layout at 130% text) | Reference-phone readability |
| 36 | Camera and two arenas | Done in code: `CameraShake` (off with reduced motion), `ArenaVariants` courtyard/riverside (no prop in the flight volume or in front of the camera), `ArenaVariantSceneBuilder` | Frame-time checks of both scenes on the phone |
| 37-41 | Contours, card choices, cut capture, transfer, totals/labels | Done in code: `OwnershipContours`, `OwnershipOverlay` patterns, `CardChoices`, `CutGesture`, `LandTransferPlan`, `LandTotals`, `BoardLabels` | Usability of the cut window with people |
| 42 | Result and rematch | Done in code: `ResultSummary`, `RematchGuard` | — |
| 43 | Bootstrap, menu and modes | Done in code: `ModeCatalog` (offline modes always available; online disabled with a reason), `LaunchFlow` (settings, then the tutorial offer) | Online entries depend on the online layer |
| 44 | Loadout, mastery, practice | Done in code: `LoadoutModel` (Owned/Loaned from the symmetric catalogue, reserve rules), `Mastery`, weapons screen | Account level comes from progression (default 1 here) |
| 45 | Tutorial and loss explanation | Done in code: `TutorialMachine` on a real practice match, `LossExplanation` from the record, untimed option | New testers must finish it without coaching |
| 46 | Settings, accessibility, data | Done in code: reduced motion, patterns, untimed tutorial, local data deletion (`LocalDataControls`) | Account deletion is an online flow |
| 47 | Pause, background, connection | Done in code: `SessionLifecycle` + `OnApplicationPause` (local clock stops, resume frame dropped, opaque cover), online reconnect/recovered states | Online states need the online layer's events |
| 48 | English, Hindi, Kannada | Done in code: `MessageFormat` (grouping, CLDR plurals), fallback chain, `glossary.txt`, `FontCoverage`; hi/kn drafts complete but **need fluent-speaker review** | Fonts and a shaping text path (below), native review on the reference phone |

**Text rendering path (ticket 48).** Legacy uGUI `Text` cannot shape Devanagari or Kannada. The
planned path is TextMesh Pro (bundled with uGUI 2.0 in Unity 6) with the Noto faces listed in the
asset ledger, font atlases generated from `FontCoverage.AtlasCharacters(...)`, and the editor's
complex-script shaping verified on the reference phone before Hindi or Kannada ship. `UiFactory`
is the single place that creates text, so the switch is local to it.

**Asset ledger.** `unity/Assets/Resources/Ledger/asset-ledger.json` (`AK-ASSET-LEDGER/1`) records
every sound, music track, font, model, texture and animation in use or planned; tests reject an
approved entry without licence, rights holder, provenance and hash.

```bash
dotnet run -c Release --project tools/AstraKingdoms.Sim -- --matches 10000 --cohorts      # bot review
dotnet run -c Release --project tools/AstraKingdoms.Sim -- --ingest path/to/playtests      # human review
```

## Online play (V1 tickets 49-56)

| Path | Contents |
| --- | --- |
| `server/AstraKingdoms.Server/` | Authoritative ASP.NET Core (net8.0) match service: Firebase ID-token or dev identity, friend rooms, queue with a consent-only labelled bot offer, server phase clock, per-player `PlayerView` delivery over WebSockets, SQLite persistence, append-only audit, rate limits, health, graceful drain, grievance intake. Runbook: `server/RUNBOOK.md`. Container: `server/Dockerfile`. |
| `unity/Assets/Scripts/Online/` | Unity online client. `Protocol/` (its own asmdef) is the message schema the server also compiles. The core (no UnityEngine) holds `OnlineConnection` (reconnect with backoff), `OnlineClient` and `OnlineMatchSession`, which implements `IMatchSession`, the view-driven surface `LocalMatchSession` also exposes. `UI/` holds the lobby and match screens and registers the Home "Play online" hook. |
| `src/AstraKingdoms.Rules/Match/PlayerViewCodec.cs` | Wire form of a private `PlayerView` (additive; no rules change). |
| `tests/AstraKingdoms.Server.Tests/` | The service in-process (TestServer, fake clock) driven by the real client library. |
| `tools/AstraKingdoms.LoadTest/` | Load and fault check of the declared initial capacity scenario. |

```bash
dotnet test tests/AstraKingdoms.Server.Tests
cd server/AstraKingdoms.Server && ASPNETCORE_ENVIRONMENT=Development dotnet run   # dev auth, data/astra-server-dev.db
```

**Not verified here:**
- a real Firebase project and the client-side Firebase sign-in adapter (`OnlineEntryPoint.IdentityTokenProvider`);
- a Docker build (no daemon available; the publish step was checked);
- the online UI in a Unity editor or on a phone;
- the capacity numbers on the target host.

The online cut window offers Auto Cut only: the drawn-cut `LandScreen` is tied to `LocalMatchHost`.
