**Planning baseline 1.0**  
5 October 2026

A corrected end-to-end plan for the Astra Kingdoms game, from the first shared-phone pilot through a conditional conquest world, and for Game Forge, from internal automation through an independently validated commercial product.

The immediate sequence is to freeze the two-player rules, qualify a minimal code/test/physical-phone loop, and complete the pilot. Add production assets and online play after the game gate. Validate Forge with outside users before expanding paid workflow services or managed inference credits.

This document includes the full pilot and V1 rule contract, the complete 82-ticket game register, future-version design boundaries, Forge architecture and release gates, cost and engagement models, operating procedures, current-source references and a traceable register of all 36 review corrections.

**Reading note.** Gameplay numbers, targets, commercial allowances and delivery ranges are explicit planning assumptions. They require the tests and decisions stated in the plan. They are not claims of achieved performance or guaranteed delivery.

# Contents

Use the linked chapter titles to navigate. Detailed subheadings also appear in the document outline and PDF bookmarks.

[Plan purpose and operating decisions](#plan-purpose-and-operating-decisions) 3

[Astra Kingdoms product and player experience](#astra-kingdoms-product-and-player-experience) 6

[Game rules and acceptance cases](#game-rules-and-acceptance-cases) 10

[Game architecture and quality](#game-architecture-and-quality) 25

[Pilot and V1 delivery register](#pilot-and-v1-delivery-register) 28

[Expansion roadmap and joint schedule](#expansion-roadmap-and-joint-schedule) 34

[Future game design and operating boundaries](#future-game-design-and-operating-boundaries) 38

[Game Forge architecture and workflow](#game-forge-architecture-and-workflow) 44

[Forge providers assets and safety](#forge-providers-assets-and-safety) 50

[Forge releases and commercial offering](#forge-releases-and-commercial-offering) 57

[Finance and commercial economics](#finance-and-commercial-economics) 60

[Measurement and stage decisions](#measurement-and-stage-decisions) 66

[Governance and release requirements](#governance-and-release-requirements) 68

[Operating procedures and decision records](#operating-procedures-and-decision-records) 70

[Correction register](#correction-register) 76

[Sources and verification basis](#sources-and-verification-basis) 79

# Plan purpose and operating decisions

This is the complete corrected planning baseline for Astra Kingdoms and Game Forge. Astra Kingdoms is the first game; Game Forge is the system used to build it and, if it proves useful to other makers, a later software product. The immediate investment is a small, playable two-player game and a reliable code, test and phone-build loop. Further game versions and the commercial Forge service each require their own evidence and funding decision.

The plan incorporates all 36 review points and the subsequent decisions to retain replay sharing in V2 and to offer remove-ads only if non-rewarded advertising is actually introduced. It defines the game mechanics that were missing, replaces emulator-dependent release gates with physical-phone gates, corrects hardware and licensing assumptions, and separates cloud-assisted local tooling from a future fully offline edition.

**Document status.** This is a design and execution plan dated 5 October 2026. It is not a claim that software has been implemented, gameplay has been balanced, hardware has been benchmarked, vendor credits are available, or legal clearance has been obtained. Numeric gameplay settings are explicit starting values for implementation and testing. Business figures are planning estimates or stated targets, not quotes, forecasts or achieved results.

**How to use the plan.** The game rules chapter is the source of truth for the pilot and V1 mechanics. The delivery register converts those rules into testable work. The Forge chapter defines how that work can be automated and reviewed. The operating and financial chapters determine whether another stage is justified. The final correction register shows where every review point was incorporated. When two implementation documents disagree, resolve the conflict against the approved rules version before continuing a dependent ticket.

## Decisions adopted in this revision

| **Decision**       | **Adopted baseline**                                                             | **Consequence**                                                                     |
|--------------------|----------------------------------------------------------------------------------|-------------------------------------------------------------------------------------|
| First game scope   | Two players, shared phone first, then online                                     | Four-player rules and networking belong to a later independent gate                 |
| Pilot content      | Five elemental starter weapons, Plain terrain, one arena, Chakra and Suchi cards | Tests the duel and land cut without requiring an asset factory                      |
| Duel result        | Up to three volleys, with a precise winner and draw rule                         | The resolver, replay and simulator have the same definition of a result             |
| Land percentages   | A fraction of total board area, adjusted by final HP margin                      | Quota, actual transfer and remaining land are separate values                       |
| Match fairness     | Equal starting land and a symmetric eligible weapon catalog                      | Progression or purchases cannot create an unequal online weapon pool                |
| V1 progression     | Twenty levels and weapon familiarisation, with five starters                     | Advanced online catalogs loan eligible weapons to both players                      |
| V1 revenue         | Cosmetics and optional rewarded ads outside matches                              | Remove-ads is absent until there are non-rewarded ads to remove                     |
| Replay sharing     | V2 export feature                                                                | V1 growth uses friend rooms, invitations and developer-made clips                   |
| Automation promise | Bounded, supervised automation for a supported template                          | Failures can require a specialist; no promise that owners never need technical help |
| Testing            | Pure rules tests, Unity tests and physical Android phone smoke tests             | Emulator experiments cannot satisfy a release gate                                  |
| Asset rollout      | One qualified asset route at a time                                              | Hardware, output rights and measured cleanup cost decide expansion                  |
| Forge product      | Internal proof, then outside-user proof, then paid workflow features             | Managed inference credits and offline enterprise support remain separate projects   |
| Spending           | Root-task caps and a shared cash ledger                                          | Retries, parallel jobs and both products draw against visible limits                |

## Scope commitments and planning assumptions

The owner retains the original creative intent: Indian-epic fantasy, hidden astra choices, an understandable elemental counter system, a bow duel and a finger-drawn land cut. The implementation defaults below make that intent testable. They do not establish that the design is fun. The pilot can change damage, timers, silhouettes, quotas and tutorials through a recorded rules revision; it must not change definitions silently in code.

Use a provisional planning capacity of 15 to 20 owner hours per week across both products. Those hours include decisions, reviewing outputs, device testing, operations and customer conversations. AI activity does not create more owner review capacity. Contractor hours, specialist reviews and platform lead times must be recorded separately. Replace this capacity assumption with actual available hours in the first planning session.

The first supported deployment is Android. The first Forge host is the owner's actual laptop after a compatibility check. A dedicated, isolated Linux worker may run code agents or GPU jobs where required. Do not promise a particular operating system combination, CUDA stack, rented GPU, region, Unity patch or cloud credit until that combination has completed qualification. A working configuration is captured in a version manifest, not described as simply “latest.”

The immediate pilot has no account system, advertising, in-app purchases, photo uploads, public chat or persistent world. It still uses original or properly licensed assets and obtains clear permission from testers for any feedback, video or identifiable data collected. Any route that sends project files to a provider is disclosed before use.

## Authority and change control

The owner approves the product scope, spending envelope, visual direction, release candidates and expansion decisions. A technical reviewer is responsible for accepting the implementation approach to networking, deterministic geometry, security and billing before those features reach users. This can be the owner if suitably qualified, or a contracted specialist with a defined review deliverable. An agent reviewer is useful evidence, but is not a substitute for assigning accountability.

Every gameplay change has a rules version, effective date, migration impact, acceptance examples and rollout decision. Running matches retain their initial rules and catalog snapshots. Economy and purchase changes include an entitlement migration and rollback plan. A document correction that only clarifies existing behavior can be editorial; a change that alters an outcome must become a tested rules revision.

Use four decision states: **adopted baseline**, **experiment**, **blocked pending evidence**, and **deferred**. A backlog ticket cannot treat an experiment as a production dependency without an exit gate. A deferred feature has a destination version and trigger; it does not quietly enter the current sprint. Blocked items name the missing evidence and the person responsible for obtaining it.

## Ownership and decision cadence

| **Area**                            | **Accountable role**                        | **Required evidence**                                             |
|-------------------------------------|---------------------------------------------|-------------------------------------------------------------------|
| Game rules and player experience    | Product owner                               | Rules version, playable build, observed tester feedback           |
| Core implementation and integration | Technical lead or appointed reviewer        | Source diff, independent acceptance cases, integration result     |
| Art and motion                      | Owner with artist when needed               | In-game preview, license provenance, performance budget           |
| Backend and release security        | Qualified engineer                          | Threat review, adversarial cases, restore and rollback drill      |
| User data and store obligations     | Owner with legal adviser for affected scope | Data map, notices, applicable policy checklist, unresolved issues |
| Spending and commercial terms       | Owner                                       | Cost ledger, provider contracts, entitlement and refund design    |
| Forge support and customer promises | Product owner                               | Supported matrix, reproducible onboarding, support capacity       |

Review tasks, cost, human time and defects weekly. Each phase ends with a written go, hold, narrow or stop decision.

# Astra Kingdoms product and player experience

## Product promise

Astra Kingdoms is a short competitive strategy game in which both players secretly prepare a bow shot, watch the simultaneous result, and compete for territory through a visible land-cut decision. Players should understand why a shot succeeded, why they won or lost the duel, how much land they may take and why the match ended. The goal is a satisfying rematch, followed later by a persistent kingdom that provides long-term identity.

In the pilot and V1, two equal kingdoms share a circular board. The attacker alternates each round; the rules select a legal frontier challenge location from the versioned match seed. Both players fight under the same elemental rules. The duel winner chooses a formation card, draws a cut within the allowed area and gains the land actually transferred. After at most eight rounds, the player with more land wins, unless an earlier victory condition is reached. A drawn duel transfers no land. A tied match stays a draw.

The four-kingdom image remains part of the longer-term product direction. It must not appear as a promise of four-player V1 support in a store listing, onboarding screen, trailer or Forge template. Persistent kingdoms start in V2; V1 account progression is not a live world or an army economy.

## Release scope

| **Release** | **Included experience**                                                                                                                                                                                   | **Excluded or separately gated**                                                                                    |
|-------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------|
| Pilot       | Shared phone, full eight-round loop, five starters, two cards, one Plain arena, deterministic replay for debugging                                                                                        | Online services, paid products, progression, generated character pipeline                                           |
| V1          | Two-player shared phone and online, practice bots, 20-weapon full catalog, six cards, four special terrain types plus Plain, friend rooms, tutorial, three languages, cosmetics and optional rewarded ads | Persistent kingdom, social replay export, clans, photos, ranked seasons, four-player matches                        |
| V2          | Persistent kingdom and protected homeland, ranked 18-day seasons, cosmetic pass, friends and clans, replay export, wider language support, content up to 40 weapons                                       | Photo avatars require a distinct privacy and moderation gate; they can ship later without blocking the kingdom loop |
| V3          | Separately validated real-time duel mode, four-player competition, spectating and tournaments, content up to 70 weapons, iPhone after platform qualification                                              | Each subsystem has its own go decision; no combined promise that one launch must include all of them                |
| V4          | Shared conquest world, offline defence, earned-resource army choices, alliance wars, content up to 100 weapons                                                                                            | Requires economy simulation, staffing, sustainable infrastructure and new-player protection                         |

Weapon and content counts in later versions are ceilings for the roadmap, not a requirement to ship weak or redundant additions. Content qualifies through a distinct role, understandable counterplay, an approved asset and sufficient test coverage. If 30 well-understood weapons are better than 40 overlapping ones, the owner records that change and revises the promise before release.

## Player journey

The first launch asks for language and basic settings, then immediately offers the guided starter duel. The tutorial introduces one choice at a time: select a weapon, aim and power, select a dodge, lock, watch the reveal, read the outcome and draw a land cut. The player completes a short guided sequence before seeing advanced status effects. Tutorial help never hides the actual rules or awards an unexplained win.

The home screen prioritises Play, Practice and Play with a Friend. Local play includes a handover screen between secret choices. Online play shows the selected catalog, terrain rules, connection status and opponent type before the match starts. A bot is identified as a bot; matchmaking never uses one to impersonate a human. Friend rooms show the same mode settings to both players before confirmation.

During a duel, the screen shows the current round and volley, both HP values, visible statuses, time remaining and the player's own locked or editable choices. The opponent's private values are not displayed or transmitted to the other client before the common reveal. After resolution, a concise explanation identifies hit or miss, element multiplier, dodge or cover effect and remaining HP. More detailed numerical breakdowns remain available in the replay or help view.

The land screen shows total ownership, the maximum allowed transfer, the selected card shape, legal placement feedback and the actual area that would move. Invalid gestures receive a specific reason and can be corrected within the cut window. On confirmation or a defined timeout, the result is committed once and the next round begins. Match results show both final areas, the result and a clear rematch action.

## Modes and fair progression

V1 has two symmetric catalog presets: Starter uses the five base elemental weapons, and Full uses the 20-weapon catalog. Both players receive the same eligible set for that match. Full-catalog play loans weapons that an account has not permanently unlocked; equip limits and slot rules still apply. Queue or room settings cannot change after one player's selections become known. V2 ranked play also uses a normalised catalog.

Permanent progression teaches weapons and provides account identity. Five weapons are available from the start; the remaining 15 are introduced through levels 2 to 16. Levels 17 to 20 focus on cosmetics and mastery recognition. No paid purchase increases HP, damage, land quota, equipment slots, elemental multiplier, matchmaking advantage or world protection. A future earned-resource army system also requires equal-budget competitive constraints, rather than assuming that the absence of cash purchases makes a power imbalance fair.

As an initial economy experiment, award 100 experience for a completed human match, 25 more for a win and 10 more for a draw; a practice match awards 50 experience. Require 300 experience per level through level 20. These values are proposed learning-pace settings, not retention evidence. Disable experience in automation, developer tests and invalid matches. Online grants use a unique match result ID; repeated requests cannot duplicate rewards. Tune the curve from observed learning, not the need to create artificial grind.

Start with one earned cosmetic currency. A completed eligible match grants 10 cosmetic coins and a win grants 5 more. Offer three optional daily tasks worth 20 coins each, such as finish two matches, use two different elements, or complete a practice exercise. These are pilot values for a progression test after the core game gate. Tasks must not require ad viewing or purchases. Set starter cosmetic prices only after inventory and expected earning pace are measured; do not create premium currency or a second wallet merely to make the store look larger.

V1 paid cosmetics can be direct purchases with clearly described content. Keep paid entitlements separate from earned coins and weapon familiarisation. Guest or offline rewards cannot become an unlimited source of server currency: choose either a capped migration grant with a published rule or keep local progress distinct. The default is a one-time capped migration determined during the progression milestone, never automatic trust in client-provided totals.

## Art and sound direction

Use original Indian-epic fantasy architecture, textiles, materials and formation motifs with clear silhouettes. The characters are original mortal or fictional fantasy archers. Gods, revered figures and recognisable copyrighted characters are not fighters or targets. Review the working game and Forge names before public branding. A cultural review covers names, symbols, costumes and marketing language at the first public-art gate.

The pilot uses simple shapes and flat colours. V1 art starts only after the duel and cut pass the playtest gate. Create one readable base archer with three cosmetic outfits, one bow family, elemental projectile and impact effects, terrain props and two arena treatments. Prefer shared rigs, materials and animation clips where they improve consistency and performance. A second arena may reuse the same rules and navigation; it should not double the gameplay validation burden.

The art brief for each item defines use, silhouette, scale, camera distance, palette, allowed references, target device budget and licence provenance. A source generation that looks good in a turntable is not accepted until it works at the game's actual camera scale. Small-screen readability outranks decorative detail. The Forge asset contract supplies the exact import and budget checks.

Element recognition uses icon shape, label and effect pattern as well as colour. Agni can use a flame, Vayu a spiral, Prithvi a stone, Vidyut a bolt and Varuna a wave. Counter explanations use the same symbols everywhere. Include reduced camera shake, independent music and effects controls, haptic control, readable text scaling and persistent settings. Important timing and combat information must remain understandable with sound disabled.

Use a licensed sound library or a rights-qualified audio provider for draw, release, clash, hit, dodge, card selection, land transfer, victory and UI feedback. Keep an asset ledger for every sound, music track, font, model, texture and reference input. Two music tracks are a V1 content target, not a reason to build an audio-generation lane before the game is playable.

## Language and accessibility acceptance

English, Hindi and Kannada are the V1 scope. All player-facing strings use keys and parameters; avoid concatenating fragments into grammatical sentences. Store proper-name spelling and transliteration decisions in a glossary. Translation review includes native-language feedback, font coverage, line wrapping, button widths, number formatting and complete tutorial playthroughs on the reference phone.

V2 adds Telugu, Tamil, Marathi and Bengali only after the translation workflow and support capacity are established. A missing translated string must have a safe fallback; it cannot display a raw key. Images should not contain essential text that cannot be localised. Release screenshots and store descriptions must accurately represent the available languages and current feature set.

## Launch communication and growth

Before V1 soft launch, prepare an accurate store listing, a short gameplay video, clear support details, privacy and deletion pages, age and audience declarations, and a concise explanation of optional purchases. The store video demonstrates an actual build. Do not advertise automation as evidence that the game is stable, or show V2 kingdoms and V3 four-player modes as current gameplay.

V1 discovery relies on friend-room invitations, observed local play, developer-created short clips, and small communities where the owner can gather useful feedback. Replay export remains in V2. Closed-test recruitment and qualitative feedback can proceed without paid user acquisition. A paid growth experiment requires stable measurement, acceptable technical completion and an explicit cash ceiling; a retention target alone does not demonstrate profitable acquisition.

For later growth, test one audience and creative at a time. Record impressions, qualified installs, first completed matches, retained players and net attributable revenue. Stop campaigns that cannot be interpreted because attribution, purchase verification or event logging is broken. Expansion beyond India requires a release checklist for that market, including asset-output rights, audience rules, language and support capability.

# Game rules and acceptance cases

## Status and scope of this chapter

This chapter replaces the ambiguous game rules in the 5 October plan. It defines an implementable first ruleset, AK-TR-1, for a two-player game. The numeric values below are **proposed, unvalidated design constants**: they are starting settings for the simulator and human pilot, not measured evidence of balance, retention or commercial success. Any later tuning produces a new versioned rules bundle; an ongoing match continues using the version with which it started.

The core remains hidden weapon selection, bow aiming, simultaneous arrows, dodging, clashes, a finger-drawn land cut and eight alternating challenges. The pilot uses five starter weapons, plain terrain and two fixed card choices: Chakra and Suchi. V1 adds the complete twenty-weapon catalog, six cards, five terrain categories, online two-player rooms and bots. Neither release contains four-player rooms, real-time shooting, armies, persistent competitive land or a world conquest economy. Those modes require separate specifications and gates.

An in-match kingdom is temporary territory on the duel board. A later personal kingdom is account progression. Its protected homeland does not reserve invulnerable cells on the V1 match board. Both players always begin a new match with exactly equal territory.

## Match structure and fair equipment

| **Rule**         | **Frozen implementation default**                                                                                    |
|------------------|----------------------------------------------------------------------------------------------------------------------|
| Players          | Exactly two; either two people or a clearly identified bot and a person                                              |
| Territory        | 51,040 equal-area board cells; 25,520 initially owned by each player                                                 |
| Rounds           | A maximum of eight duels; every completed duel consumes one round                                                    |
| Initiative       | First attacker chosen from the match seed; attacker alternates after every round, including draws and zero-area cuts |
| Duel health      | Each player starts each duel at 100.00 HP                                                                            |
| Duel length      | Up to three simultaneous volleys; health persists between those volleys                                              |
| Duel winner      | A sole survivor after a volley; otherwise higher HP after volley three                                               |
| Duel draw        | Equal HP after volley three, or both reaching zero in the same health update; no land cut                            |
| Match end        | A player reaches at least 45,936 cells, or round eight finishes                                                      |
| Final comparison | Higher exact cell count wins; equal counts produce a match draw                                                      |
| Normal equipment | One to six different equipped weapons; Starter allows one to five; equipped weapons are reusable every volley        |
| Seventh weapon   | A unique reserve selected before the match; eligible only in the Full catalog when defending an Armoury duel         |
| Purchases        | No purchase changes weapons, health, land allowance, timers or combat strength                                       |

This is an **up-to-three-volley duel**, not best-of-three scoring. There are no independent volley points. A player can lose more health in an early volley and still win the duel by finishing with higher HP.

Rooms select a symmetric catalog before either loadout is submitted. **Starter** offers the same five basic weapons to both players and requires one to five equipped slots. It uses plain terrain by default. **Full** loans all twenty weapons for the match to both players, regardless of account level, and requires one to six normal slots. A seventh distinct reserve is optional only when all six normal slots are equipped. Reject an empty normal loadout. A friend-room host chooses the catalog and both players see it before joining. Public V1 play is unranked; any public Full match uses the same loan rule. V2 ranked play must retain a normalized catalog.

Account unlocks provide persistent access in practice and progression presentation, not an asymmetric online combat advantage. Five weapons are available at level 1; one additional weapon unlocks at each level from 2 through 16. Levels 17-20 grant cosmetics and mastery rewards only. Match equipment cannot be edited midway through the match. Loadout identities are private; normal post-lock reveals gradually disclose weapons actually used.

## Commit reveal and clock behaviour

Each volley has one input per player: equipped weapon ID, pitch, yaw, power and dodge. Dodge is Left, Right, Jump or None. The authoritative receiver validates eligibility and ranges, accepts one immutable lock, and returns a receipt containing the match, round, volley, input revision and rules hash. A duplicate of that accepted input returns the same receipt. A different second input cannot replace it.

Both choices remain secret until both have locked or the deadline resolves. A lock notification may say that the opponent is ready; it cannot reveal their weapon, ultimate use, element, aim, power or dodge. The server supplies only the appropriate private view to each client. Replay data that would reveal remaining private information is withheld until match completion.

| **Phase**                 | **Online default**                                                     | **Shared-phone default**                                                  |
|---------------------------|------------------------------------------------------------------------|---------------------------------------------------------------------------|
| Terrain announcement      | 2 seconds before the first volley of a duel                            | Same, with an optional untimed tutorial                                   |
| Choice deadline           | 12 seconds for both players concurrently                               | 12 seconds per player, entered privately in sequence                      |
| Handover                  | Not applicable                                                         | One opaque handover per volley, at most 6 seconds between the two entries |
| Resolution replay         | At most 2.5 seconds; longer simulation time is consistently compressed | Same                                                                      |
| Card choice, pose and cut | One combined 12-second window                                          | 20 seconds                                                                |
| Pause                     | No pause of an online opponent's clock                                 | Available in local practice; excluded from duration benchmarks            |

An unlocked player at the deadline receives a server-created **Pass**: no projectile, no dodge and a neutral defensive element. Existing damage-over-time and terrain healing still resolve. Invalid or out-of-range submissions are rejected rather than silently converted into a different shot; the player may submit a valid input before the deadline. Two consecutive selection timeouts by one player forfeit the match. This streak is match-level and can cross a duel boundary; a valid intervening lock resets it. If both reach the forfeiture condition in the same deadline transaction, the match is void, with no winner reward.

A disconnect preserves accepted choices and does not extend the deadline. Reconnection can recover the current state; it cannot reopen a resolved volley. A server-declared forfeit settles the match without inventing a land transfer and is not counted as a normally completed match. A service failure that prevents authoritative resolution produces a technical void, with no competitive reward or loss. A cut timeout transfers zero cells and advances the round; it does not count as a combat-selection timeout.

The shared-phone privacy screen conceals previous controls, logs and previews. Players still need to hand over the phone privately. Entry order alternates by volley so the same person is not always waiting. Five-to-seven-minute online matches remain a measurement target. At every scheduled maximum, online time is 24×12 + 24×2.5 + 8×2 + 8×12 = 460 seconds, or 7 minutes 40 seconds. Shared-phone time is 24×24 + 24×6 + 24×2.5 + 8×2 + 8×20 = 956 seconds, or 15 minutes 56 seconds. These totals exclude pre-match setup and local pauses. Instrument median and upper-percentile duration rather than promising the shorter target for every session.

### Command fields and units

Both command types carry schema_version=1, a 32-byte rules_hash, canonical UUID match_id and request_id, one-based round_index, and unsigned 64-bit expected_state_revision. This is the published phase snapshot revision, fixed until that phase closes; accepting the first player's lock does not invalidate the second player's input. Event-log sequence numbers are separate. The authenticated connection determines the player; a client-supplied player ID grants no authority. Receive time is authoritative. Duplicate request IDs return the original result only when their canonical payload is identical.

| **Command** | **Required typed payload**                                                                                                                                                                                                                                                         |
|-------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| LockInput   | volley_index 1-3; weapon_id 1-20, or experimental 1000; signed integer pitch_qdeg and yaw_qdeg, where one unit is 0.25°; integer power_percent 70-100; dodge enum 0=None, 1=Left, 2=Right, 3=Jump                                                                                  |
| SubmitCut   | expected_map_revision unsigned 64-bit; card_id 1=Chakra, 2=Garuda, 3=Suchi, 4=Makara, 5=Padma, 6=Vajra; anchor_cell_id=y×256+x; integer envelope centre x/y 0-255; rotation_index 0-15; scale_quarters 1-1024; mode Manual or Auto; Manual vertex array of integer x/y coordinates |

Pass ID 0 is server-created only. Brahmastra requires zero pitch/yaw, power 100 and dodge None as canonical placeholders. Auto mode has no stroke vertices. The server checks these fields against the current phase, catalog, offered cards and pinned state. The first accepted lock closes that player's selection; the first accepted cut atomically closes the entire cut phase. A different later cut cannot spend a second allowance. Client preview and accepted canonical geometry must agree.

## Numerical combat contract

The rules resolver owns hits and health. Rendering interpolates its event log and cannot decide damage from an animation frame or an engine collision callback.

| **Quantity**         | **Proposed value or representation**                                                                                                |
|----------------------|-------------------------------------------------------------------------------------------------------------------------------------|
| Coordinates          | Three dimensions, in metres; authoritative signed fixed-point Q32.32 values                                                         |
| Physics clock        | 120 ticks per simulated second; maximum 360 ticks per volley                                                                        |
| Integration          | Semi-implicit update: v' = v + round(a/120); p' = p + round(v'/120)                                                                 |
| Fixed-point rounding | Nearest representable value, ties to even; checked wide intermediates                                                               |
| Ground               | Projectile-centre crossing of y = 0; gravity −9.8 m/s² except explicitly direct trajectories; radius does not move the ground plane |
| Fighters             | A at x=0, B at x=8; baseline lateral coordinate z=0                                                                                 |
| Launch point         | 0.35 m forward of the shooter, 1.35 m high, at their current baseline lateral coordinate                                            |
| Body test            | Capsule silhouette at the defender's fixed x-plane; vertical axis from 0.35 to 1.45 m, core radius 0.25 m                           |
| Power                | Integer 70-100%; multiplies launch speed only, not damage                                                                           |
| Aim quantization     | Pitch and yaw in 0.25° increments; yaw between −8° and +8°                                                                          |
| Trigonometry         | Versioned integer sine/cosine lookup table; no platform-specific runtime trigonometric calculation                                  |
| Flight termination   | Ground contact, resolved target contact, cancellation, leaving x∈\[−2,10\], y∈\[0,12\], z∈\[−3,3\], or tick 360                     |

For A, forward is world +x and local Right is world +z; for B, forward is −x and local Right is −z. Aim, dodge labels and lateral movement use these local axes. Speed, pitch and yaw define the launch vector using the same lookup table on every executor. The exact table bytes, integration convention and collision tie rules are included in the rules hash.

| **Launch profile** | **Base speed**    | **Allowed pitch** | **Additional rule**       |
|--------------------|-------------------|-------------------|---------------------------|
| Normal             | 12 m/s            | 0°-65°            | Standard gravity          |
| Fast               | 15 m/s            | 0°-65°            | Standard gravity          |
| Slow               | 11 m/s            | 25°-75°           | Standard gravity          |
| Very slow          | 10.5 m/s          | 45°-80°           | Standard gravity          |
| Flat               | Uses weapon speed | −5°-20°           | Still affected by gravity |
| High               | Uses weapon speed | 25°-75°           | Standard gravity          |
| Very high          | Uses weapon speed | 45°-80°           | Standard gravity          |
| Direct             | 16 m/s            | −10°-10°          | Zero gravity              |

For mixed labels, the speed label selects speed and the trajectory label selects pitch/gravity. All projectiles in a weapon's initial pattern launch together from the snapshot launch point. A dodge then sets the defender's collision pose for the resolution window; it does not retroactively move the already launched arrows. The replay shows the shot and evasive action from these events.

### Dodging and body contacts

Left and Right move the target capsule laterally by 0.45 m; Jump raises its axis by 0.50 m; None leaves it at the baseline. A side dodge is relative to the defender's own facing. Jump intentionally helps against low shots and does not automatically evade a torso-height arrow. Leading the target's chosen side can still produce a full hit.

At a swept crossing of the defender plane, compute the projectile centre's distance to the displaced capsule axis. A distance within 0.25 m + projectile radius is a core hit. During a legal dodge, a core miss within 0.50 m + projectile radius is a graze and takes half direct damage. Outside both is a miss. Without a dodge, the outer graze region does not apply. Thus an effective dodge can cause either a graze or a complete miss; it is not an unconditional 50% discount on every shot. For Stone Arrow's unsuppressed Jump Pierce, a Jumping target is tested against its standing silhouette with no jump graze, while side dodges remain available.

Swept collision tests cover the complete segment between ticks. Fast arrows must not tunnel through a body plane, ground or another projectile. Apply each tick's acceleration impulse once, before testing linear motion within that tick. Quantize a computed event time upward to the first of 65,536 sub-tick positions at or after contact. After a mid-tick clash, recompute pending contacts and advance the remainder with the new velocity without applying gravity or curvature a second time. Canonical interpolation, integer square-root and tie handling are tested using stored conformance vectors. Coordinates alone do not establish determinism.

### Projectile clashes

Only opposing projectiles with clash enabled interact. The collision distance is the sum of their radii. Equal remaining masses cancel. For unequal masses, the heavier projectile survives with remaining mass equal to the difference and its entire velocity vector halved. Its damage and element stay unchanged. Gravity continues normally from the collision point, so the trajectory is recalculated. Remaining mass affects later clashes.

Events are resolved by collision time. At an identical time, build connected components of contacting opposing projectiles. Sum remaining mass by side in each component. Equal totals remove the entire component. Otherwise retain one projectile from the higher-mass side: greatest individual remaining mass, then smallest projectile index within that weapon. Give it the side-total mass difference and halve its velocity once; remove the others. This explicitly handles a fan contacting a heavy arrow without dependence on iteration order. Friendly arrows alone do not collide or pool mass.

At an exact tie, process clashes, then body contacts, then ground contacts, then expiry. Boulder never creates a body-plane contact. Equal-time incoming contacts consume a shield in ascending projectile-index order. Independent contacts for the two players are collected before health is changed. Projectile identity uses round, volley, owner and projectile index; individual events add type, tick/sub-tick and an authoritative sequence number. Log sorting never grants one player an earlier health update.

## Elements damage and the twenty weapons

Each damaging projectile compares its element with the opponent's committed weapon element, even if the opposing arrow misses or is cancelled. A Pass or enabled Brahmastra uses Neutral, which receives and deals no elemental advantage. Equal elements use 100%; an advantage uses 150%; a disadvantage uses 50%.

| **Element** | **Beats**       | **Loses to**    |
|-------------|-----------------|-----------------|
| Agni        | Vayu, Prithvi   | Vidyut, Varuna  |
| Vayu        | Prithvi, Vidyut | Agni, Varuna    |
| Prithvi     | Vidyut, Varuna  | Agni, Vayu      |
| Vidyut      | Varuna, Agni    | Vayu, Prithvi   |
| Varuna      | Agni, Vayu      | Prithvi, Vidyut |

The cycle is complete and symmetric. Its mean multiplier is 1.0 against a uniform selection of all five elements; that arithmetic does not prove the full game balanced.

Health is stored in hundredths of one HP: 100 HP equals 10,000 units. For each unblocked contact, multiply base damage by the elemental factor, dodge factor and applicable cover factor as exact rational values, then round once to the nearest 0.01 HP, with half-cent ties rounded upward. Sum these rounded contact amounts. Do not round at each multiplier or round displayed bars back into game state.

### Weapon combat values

| **ID** | **Weapon**    | **Element** | **Damage** | **Speed / trajectory** | **Mass per projectile; radius** |
|--------|---------------|-------------|------------|------------------------|---------------------------------|
| 1      | Ember Arrow   | Agni        | 30         | Normal / Normal        | 2; 0.04 m                       |
| 2      | Gale Arrow    | Vayu        | 25         | Fast / Flat            | 1; 0.04 m                       |
| 3      | Stone Arrow   | Prithvi     | 35         | Slow / High            | 3; 0.06 m                       |
| 4      | Spark Arrow   | Vidyut      | 28         | Fast / Normal          | 1; 0.04 m                       |
| 5      | Tide Arrow    | Varuna      | 30         | Normal / Normal        | 2; 0.04 m                       |
| 6      | Fire Fan      | Agni        | 3 × 12     | Normal / Normal        | 1 each; 0.03 m                  |
| 7      | Twin Gust     | Vayu        | 2 × 15     | Fast / Flat            | 1 each; 0.03 m                  |
| 8      | Boulder Shot  | Prithvi     | 45         | Very slow / Very high  | 4; 0.12 m                       |
| 9      | Chain Bolt    | Vidyut      | 26         | Fast / Normal          | 1; 0.04 m                       |
| 10     | Mist Veil     | Varuna      | 20         | Normal / High          | 2; 0.04 m                       |
| 11     | Ash Shield    | Agni        | 15         | Normal / Normal        | 2; 0.04 m                       |
| 12     | Cyclone       | Vayu        | 22         | Normal / High          | 2; 0.08 m                       |
| 13     | Iron Wall     | Prithvi     | 10         | Slow / Flat            | 3; 0.06 m                       |
| 14     | Storm Net     | Vidyut      | 24         | Normal / Normal        | 1; 0.12 m                       |
| 15     | Flood Arrow   | Varuna      | 32         | Slow / High            | 2; 0.06 m                       |
| 16     | Sun Lance     | Agni        | 40         | Direct / Direct        | 1; 0.03 m                       |
| 17     | Sky Dive      | Vayu        | 35         | Normal / Very high     | 2; 0.05 m                       |
| 18     | Quake Arrow   | Prithvi     | 30         | Slow / Normal          | 3; 0.05 m                       |
| 19     | Thunder Crown | Vidyut      | 38         | Normal / Normal        | 2; 0.05 m                       |
| 20     | Ocean Call    | Varuna      | 36         | Slow / High            | 2; 0.06 m                       |

### Weapon abilities and account introduction

| **ID** | **Weapon**    | **Ability**                                                | **Account unlock** |
|--------|---------------|------------------------------------------------------------|--------------------|
| 1      | Ember Arrow   | Burn 5 next volley on hit                                  | Level 1            |
| 2      | Gale Arrow    | Push next-volley baseline 0.25 m to target's local Right   | Level 1            |
| 3      | Stone Arrow   | Jump Pierce                                                | Level 1            |
| 4      | Spark Arrow   | Shock: suppress eligible weapon ability next volley        | Level 1            |
| 5      | Tide Arrow    | Cleanse due Burn and Shock before suppression is evaluated | Level 1            |
| 6      | Fire Fan      | Three-shot intrinsic spread                                | Level 2            |
| 7      | Twin Gust     | Two-shot intrinsic pattern; second curves                  | Level 3            |
| 8      | Boulder Shot  | Ground burst; cannot clash                                 | Level 4            |
| 9      | Chain Bolt    | Additional 10 neutral damage against covered target        | Level 5            |
| 10     | Mist Veil     | Conceal next-volley identity and exact input values        | Level 6            |
| 11     | Ash Shield    | Block first incoming contact this volley                   | Level 7            |
| 12     | Cyclone       | Reverse opponent's Left/Right choice this volley           | Level 8            |
| 13     | Iron Wall     | Cover for this and next volley                             | Level 9            |
| 14     | Storm Net     | Prevent target dodge next volley on hit                    | Level 10           |
| 15     | Flood Arrow   | Remove target cover before its own unblocked hit           | Level 11           |
| 16     | Sun Lance     | Intrinsic straight trajectory                              | Level 12           |
| 17     | Sky Dive      | Ignore cover on body hit                                   | Level 13           |
| 18     | Quake Arrow   | Add 5° to target's next submitted pitch                    | Level 14           |
| 19     | Thunder Crown | Advantage multiplier becomes 200%, replacing 150%          | Level 15           |
| 20     | Ocean Call    | Heal 15 once this volley on hit                            | Level 16           |

Fire Fan uses pitch offsets −3°, 0°, +3° and yaw offsets −1.5°, 0°, +1.5° for projectile indices 0, 1 and 2. Validate the player's central angle against ranges narrowed by those offsets, so no spawned arrow needs silent angle clamping. Twin Gust's second projectile begins at yaw +4° relative to the submitted yaw and receives lateral acceleration −2 m/s² in the shooter's local Right axis for ticks 1 through 29 inclusive, then zero lateral acceleration. Its permitted central yaw is narrowed so both launches remain within the overall yaw bounds. Each arrow can independently hit, graze, miss, clash or consume a shield.

Boulder ignores the target plane and bursts only at its first ground contact. Compare that ground point with the target's displaced capsule axis in three dimensions: distance at most 0.55 m gives a full burst hit; during a dodge, distance over 0.55 m and at most 0.80 m gives a half-damage graze. Otherwise it misses. It generates at most one contact and can be blocked by Ash Shield or reduced by cover. Sky Dive is a high ballistic shot that ignores cover; it is not an automatic homing hit.

Intrinsic properties-element, base damage, mass, radius, projectile count, launch profile, curve and Boulder ground-burst/no-clash behaviour-remain under Shock. Shock disables optional triggered/activated abilities. Fire Fan and Sun Lance therefore have no optional bonus for Shock to remove. Tide's cleanse deliberately occurs before Shock and can remove it; this is explicit counterplay, not an accidental ordering exception.

## One authoritative volley resolution order

| **Step**                 | **Required operation**                                                                                                          |
|--------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| 1\. Snapshot             | Freeze inputs, HP, terrain, cover, baseline offsets, statuses and remaining charges for both players                            |
| 2\. Cleanse              | A valid Tide selection removes its owner's due Burn and Shock; Pass cannot cleanse                                              |
| 3\. Due statuses         | Identify remaining Burn damage; evaluate Shock; apply Quake pitch shift and clamp to the weapon's permitted central pitch range |
| 4\. Defensive activation | If unsuppressed, create Ash Shield and Iron Wall; activate terrain effects already assigned to this duel                        |
| 5\. Dodge resolution     | Storm Net forces None; otherwise Cyclone reverses Left/Right; then apply legal dodge and possible Forest charge                 |
| 6\. Launch and simulate  | Emit canonical projectiles; process swept clashes, ground and target contacts; collect effects without changing HP              |
| 7\. Contact defence      | On a qualifying contact, consume Ash Shield first; otherwise apply Flood cover removal, determine cover and calculate damage    |
| 8\. Hit effects          | Collect eligible Burn, Push, Shock, Veil, Net, Quake and Ocean effects; compute Chain Bolt's covered-target bonus               |
| 9\. Simultaneous health  | Update both players once from the snapshot: clamp(old HP − direct damage − due Burn + Ocean healing + River healing, 0, 100 HP) |
| 10\. Settle              | Check simultaneous KO/draw/winner, otherwise expire current statuses and advance to the next volley                             |

A hit means a core or graze contact with positive resolved direct damage. Misses, clashes and fully shielded contacts do not trigger on-hit abilities. Defensive abilities can activate even when their own arrow misses. Every once-on-hit ability triggers at most once per owner per volley, even if later content adds more projectiles. Queue future statuses only if another volley of this duel will occur. Nothing scheduled for a fourth volley carries into the next duel.

Burn deals exactly 5.00 HP in the next health batch and ignores element, dodge and cover. It does not stack; a fresh application refreshes one next-volley instance. Shock, Net, Quake and Veil likewise hold at most one due instance each. Each instance has a creation volley and activation volley: expiry removes the due instance, never a newly queued replacement. Iron Wall activated in volley n lasts through volley n+1, unless destroyed or the duel ends. Quake adds exactly +5° after commitment, with clipping to the valid central pitch bounds. It changes pitch only. The UI announces that the player is affected before selection. Gale shifts the baseline by 0.25 m to the target's local Right before the next volley's controls open, capped at a baseline displacement of ±0.50 m; this displacement persists for the rest of that duel, but does not change the current flight's target.

Cyclone operates on this volley's committed choice, rather than announcing a reversible next-volley control swap. Net has priority: a netted target remains at None. Forest is consumed only by a legal side dodge after these transformations. Current Shock cannot retroactively remove a status already applied by an earlier weapon.

Cover reduces normal direct damage by 25%; multiple cover sources do not stack. Chain Bolt checks whether cover exists after shield processing. If covered, its ordinary 26-damage contact receives ordinary multipliers and cover reduction, plus 10.00 neutral damage that bypasses cover, element and dodge. The bonus requires the underlying unshielded body hit. Flood removes Fort cover for the rest of that duel and clears temporary Iron Wall before its own damage is calculated. A later Iron Wall can create fresh temporary cover. Sky Dive ignores cover but still respects shields and geometry.

Ocean healing is 15.00 HP once for a qualifying hit. River adds 10.00 HP once in each volley of that duel. They can combine. The single final clamp means same-volley healing can rescue an otherwise lethal hit and can offset damage even when the player started at full HP. Both committed attacks still resolve when one would appear lethal earlier in the animation. No intermediate death check or pre-damage healing cap is allowed.

Mist Veil has a deliberately limited information role. A successful Mist hit masks the owner's next-volley weapon label, exact controls and element-specific projectile cosmetic for the opponent until the match ends. Damage, movement, terrain effects and decision-relevant status indicators remain truthful. The owner sees their own controls. The opponent may infer the weapon from the trajectory or effect; perfect secrecy is not promised. This cannot change an already committed response, but it can conceal information used in later volleys and rounds. Its value must be tested; hiding a reveal alone is not evidence that a 20-damage weapon is competitive.

## Five terrain categories

V1 has **five categories: Plain plus four special terrains**. Approved Full-map templates store a terrain ID per active cell and use exact counts: 30,624 Plain cells and 5,104 each of Fort, River, Forest and Armoury. The starting halves mirror one another, with 15,312 Plain and 2,552 of each special category per player. Ship verified authored templates rather than assuming independent random placement will produce equal maps.

Before each duel, the rules select a defender-owned frontier cell from cells sharing an edge with attacker-owned land. Selection is deterministic from the match seed and round ID, over a canonical sorted list. Its terrain becomes that duel's terrain and is announced before either lock. Ownership changes can therefore alter future frontier options. Terrain stays attached to cells during transfers; cuts neither create nor delete terrain.

Random selection uses no runtime System.Random behaviour. Its four ASCII labels are exactly AK-TR-1/initiative, AK-TR-1/map, AK-TR-1/terrain and AK-TR-1/cards. A candidate is SHA-256 of label \|\| 0x00 \|\| seed\[32\] \|\| round_u32_be \|\| counter_u32_be; use its first eight bytes as an unsigned big-endian integer. Round is zero for initiative/map and 1-8 for terrain/cards. Counters begin at zero for each stream/round and increment after every digest, including rejection. For list length k, reject values at or above floor(2^64/k)×k, then use remainder modulo k. Lists are sorted by stable ID; card draws remove the chosen card and continue the same counter. The match commits to SHA-256(ASCII("AK-TR-1/seed") \|\| 0x00 \|\| rules_hash\[32\] \|\| seed\[32\] \|\| canonical_match_UUID_ASCII\[36\]) before play and discloses the seed after settlement.

Golden vectors with 32 zero seed bytes and counter zero: initiative round 0 gives first-eight-byte hex 79e4c771f74e35fb, index 1 for k=2; terrain round 1 gives fe50068810271381, index 89 for k=100; cards round 1 gives c4baaed295bda319, index 2 for k=5. These values pin encoding and selection independently of the implementation language. Completed replays include stream counters and state hashes.

| **Terrain** | **Benefit to the defender**                                                                   | **Scope and reset**                                                                            |
|-------------|-----------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------|
| Plain       | None                                                                                          | Pilot and default Starter terrain                                                              |
| Fort        | 25% cover reduction on normal direct damage                                                   | Present at duel start; Flood can remove it for the remaining duel                              |
| River       | 10 HP in the simultaneous health batch each volley                                            | At most three heals; HP still capped at 100                                                    |
| Forest      | First legal Left/Right dodge grants 50% mitigation of ordinary direct projectile/burst damage | One charge per duel; a miss stays a miss; graze and Forest do not multiply into quarter damage |
| Armoury     | Activates the defender's preselected seventh reserve                                          | Full catalog only; no mid-match loadout editing; inactive in Starter                           |

The Forest damage factor is the smaller of ordinary dodge mitigation and 0.5 for that charged volley. It does not reduce Burn, Chain Bolt's separate neutral bonus or experimental Brahmastra. Choosing a side dodge spends the charge even if all arrows miss. A Net-forced None spends no charge. Armoury requires six regular weapons and a seventh distinct reserve selected at match setup; if the player chose fewer, it adds nothing. Both players have the same reserve opportunity. A weapon used from the reserve is reusable while eligible, but becomes unavailable in another terrain duel.

At each new duel, reset HP, cover, Forest charge, baseline displacement and all temporary statuses. Account level, territory size and previous duel health do not add hidden combat statistics. Equal starting land and catalog access are fairness constraints; terrain and initiative balance still need measurement.

## Exact territory allowance and card geometry

### Board and units

Use the 256×256 integer grid with active cells satisfying (2x−255)² + (2y−255)² ≤ 255², for integer x,y from 0 through 255. It contains exactly 51,040 cells. Initially A owns active cells with x \< 128; B owns the remainder. Each cell has one owner and one terrain ID. Area means cell count, never a screen-pixel estimate or an independently rounded percentage. Visual outlines can be smoothed without changing this mask.

Let N=51,040, L be the loser's current cell count, D the winner's final HP units minus the loser's final HP units, and C the selected card's integer percentage cap. First handle a draw: if D=0, no card is offered and no transfer occurs. Otherwise:

$$Q\  = \ min(L,\ floor(N\  \times \ max(30,000,\ C\  \times \ D)\ /\ 1,000,000))$$

This is the integer form of min(loser area, total board area × max(0.03, card cap × HP difference/100)). It uses whole-board percentage points. **Q is the maximum legal cutting allowance, not a minimum award.** The smallest positive-margin allowance is 1,531 cells; the actual transfer can be smaller or zero. That discontinuity between an exact draw and a very close win is an intentional initial rule requiring playtesting.

### Cards as practical clipping envelopes

V1 offers three different eligible cards, sampled deterministically without replacement after the duel. Vajra is eligible only when D \> 6,000, equivalent to a margin strictly greater than 60 HP. The other five cards are always eligible after a win. The pilot always offers Chakra and Suchi as two fixed choices.

Cards provide movable geometric envelopes for the player's finger-drawn cut. They do not demand that an irregular enemy territory contain a perfect decorative silhouette, and they do not award the envelope's nominal area. The player positions/rotates the envelope along a shared boundary and draws a closed cut within it. The filled preview is the exact set of cells that would transfer. An explicit **Auto Cut** button supplies an accessible legal alternative.

The following shapes use local dimensionless coordinates (u,v), then a uniform scale in quarter-cell increments and one of sixteen rotations at 22.5° increments. Rotation zero maps u to increasing board x and v to increasing board y; successive indices rotate clockwise in the displayed x-right/y-down map. Envelope centres are quantized to cell centres. Store the canonical polygons or integer membership masks in the rules bundle.

| **Card** | **Cap of total board** | **Canonical envelope**                                                 | **Placement character**            |
|----------|------------------------|------------------------------------------------------------------------|------------------------------------|
| Chakra   | 12%                    | Disk u²+v²≤1                                                           | Rounded claim                      |
| Garuda   | 15%                    | Diamond abs(u)/2 + abs(v)≤1                                            | Broad wings                        |
| Suchi    | 18%                    | Triangle with vertices (−0.5,−0.5), (−0.5,0.5), (2,0)                  | Narrow forward reach               |
| Makara   | 14%                    | L polygon: (−1,−0.5),(1.5,−0.5),(1.5,1.5),(0.5,1.5),(0.5,0.5),(−1,0.5) | Hook around an edge                |
| Padma    | 10%                    | Union of five radius-0.55 disks centred at (0,0),(±0.45,0),(0,±0.45)   | Rounded petals                     |
| Vajra    | 20%                    | Union of abs(u)+abs(v)≤1 with rectangle abs(u)≤1.8, abs(v)≤0.25        | Central diamond with extended ends |

Scale ranges from 0.25 to 256 cells, subject to the canonical rasterized envelope at its selected rotation containing no more than 2Q cells before board clipping. Its centre need not be the border anchor. A tiny envelope centred on the anchor permits a one-cell claim when that is all that remains. This gives room to trace a partial claim while keeping the card's reach meaningful; the actual claim still cannot exceed Q. Membership and rotation use the same fixed-point lookup conventions as the rules bundle, not a client's texture alpha.

### Cut validation and transfer

A border anchor is an **opponent-owned cell sharing a full grid edge with a winner-owned cell**. Diagonal contact is insufficient. The chosen envelope must contain it. Snap pointer coordinates to the nearest cell-centre index, with exact half ties choosing the lower integer. The client may simplify a long stroke only while displaying the resulting preview; the submitted array must contain at most 128 vertices. The server rejects longer arrays and never silently resamples them. Remove consecutive duplicates and an optional repeated closing vertex, close once, and require at least three distinct vertices. Reject nonadjacent edge crossings/touches, overlapping edges and zero-area paths. Use even-odd polygon inclusion, with cell centres exactly on an edge included. Integer/fixed-point envelope membership includes exact boundary points and uses the submitted pose. The preview shows these exact snapped cells while drawing.

Compute the candidate as the intersection of active board cells, loser-owned cells, card envelope and filled cut polygon. It must include the anchor. Retain only its four-connected component containing that anchor; show discarded fragments as unclaimed in the preview. Reject an empty component or one larger than Q. A legal transfer changes exactly those cell ownership IDs atomically. The claim connects to an existing winner-owned component through the anchor; it does not promise to reconnect islands that the winner already had. The loser may retain disconnected islands. Islands do not automatically become captured or invulnerable.

Auto Cut performs a breadth-first traversal from the chosen anchor within eligible loser-owned envelope cells, using the fixed neighbour order North, East, South, West. It selects at most Q reachable cells. The resulting exact set is previewed and accepted by the user's Auto Cut action; it does not promise to fill Q when less fits. An invalid hand-drawn cut remains editable within the time window. If it is never replaced by an accepted legal cut, the award is zero. There is no automatic three-percent reward for an illegal stroke or expired timer.

After each transfer, enforce disjoint ownership, union equal to the active board, unchanged terrain IDs, nonnegative counts and area(A)+area(B)=51,040. Compare the exact winner count with 45,936 immediately. After round eight-including a draw or zero cut-settle using exact territory. The 90% shortcut is reachable because the cap uses total-board area, but only when actual accepted cuts use sufficient allowance; a displayed quota alone never changes ownership.

## Brahmastra gated private experiment

Brahmastra is outside the twenty-weapon progression table. It is disabled in the pilot, default V1 public rules and normalized ranked rules. A Full private room may explicitly enable the experimental flag for both players. It becomes a separate once-per-match action, replacing that volley's normal weapon choice and consuming its charge on an accepted lock, even when blocked.

The experimental contract is a guaranteed neutral 60.00-damage homing strike scheduled at simulated tick 60. Aim and power are ignored; it does not clash, cannot graze and bypasses ordinary dodge and cover. Ash Shield blocks it completely. Shock does not disable it. The user of Brahmastra has Neutral as the defensive element that volley. Burn, River and other already active health effects still enter the ordinary simultaneous batch. Its identity stays secret until both choices lock. No other effects or on-hit weapon abilities accompany it.

This exception is intentionally narrow and potentially strong. Keep it behind the flag until mirrored tests and human matches demonstrate useful counterplay and acceptable early-ending frequency. Symmetric access alone does not prove it improves the game.

## Worked acceptance cases and balance evidence

| **Case**               | **Inputs**                                                          | **Required result**                                                                |
|------------------------|---------------------------------------------------------------------|------------------------------------------------------------------------------------|
| Exact board            | Generate canonical mask                                             | 51,040 cells; 25,520 each; no unowned active cell                                  |
| Counter arithmetic     | Ember hits a Vayu choice; no defences                               | 45.00 current damage; queue 5.00 Burn only if another volley follows               |
| Thunder interpretation | Thunder hits Agni with ability active                               | 38×2=76.00; never 38×1.5×2=114                                                     |
| Suppressed Thunder     | Same, but Shock remains after cleanse                               | Ordinary advantage: 38×1.5=57.00                                                   |
| Rounding               | Stone disadvantage, graze and 25% cover                             | 35×0.5×0.5×0.75=6.5625, rounded once to 6.56 HP                                    |
| Simultaneous healing   | Player starts at 20; receives 25; River heals 10                    | Ends at 5 HP, regardless of event-list iteration order                             |
| Full-health healing    | Player starts at 100; receives 60; lands Ocean                      | Ends at 55 HP; healing is not discarded before damage                              |
| Mutual knockout        | Both start at 30; each receives 40; no healing                      | Both zero in one update; duel draw; zero transfer                                  |
| Due-status cleanse     | Tide selected with due Burn and Shock                               | Both removed before suppression; neither burn damage nor suppression applies       |
| Last-volley status     | Ember lands in volley three                                         | Apply current damage; no fourth-volley Burn or cross-duel carry                    |
| Multi-arrow shield     | Three Fire Fan contacts; one Ash Shield                             | First qualifying contact blocked; remaining contacts resolve separately            |
| Heavy clash            | Mass 3 contacts mass 1                                              | Mass-3 projectile survives with mass 2 and half velocity; unchanged base damage    |
| Forest graze           | Charged side dodge already creates half-damage graze                | Final dodge factor 0.5, not 0.25                                                   |
| Low-shot Jump          | Normal-radius arrow at standing y=0.40,z=0; Jump                    | Shifted-axis distance 0.45 m: graze; Stone Jump Pierce instead tests standing core |
| Torso-shot Jump        | Arrow at y=1.00,z=0; Jump                                           | Still intersects shifted core; Jump is not a universal dodge                       |
| Close win              | HP difference 0.01; any eligible normal card                        | Allowance 1,531 cells; no automatic award                                          |
| Ordinary margin        | Winner 80 HP, loser 20; Chakra                                      | Q = 3,674 cells (floor(51,040×0.12×0.60))                                          |
| Actual cut             | Previous case; accepted shape contains 2,100 eligible cells         | Transfer exactly 2,100, not 3,674 and not the envelope's area                      |
| Limited remaining land | Loser has 900 cells; calculated allowance exceeds 900               | Q clamps to 900; actual transfer cannot exceed remaining ownership                 |
| Strict Vajra gate      | HP difference 60.00 versus 60.01                                    | Ineligible at 60.00; eligible at 60.01                                             |
| Reachable shortcut     | Start 25,520; two valid 10,208-cell cuts at maximum Vajra allowance | 45,936 cells; match ends at exactly 90% after the second cut                       |
| Invalid or expired cut | Self-crossing stroke, missing anchor, or no accepted cut            | Zero transfer; round advances when its window ends                                 |
| Concurrent duplicate   | Two copies of the same accepted cut revision                        | One ownership mutation and one settlement; second call returns the existing result |
| Concurrent locks       | Both players submit against the same published volley revision      | Both valid locks can succeed; accepting one does not stale the other               |
| Stale command          | An earlier volley lock or map revision arrives late                 | Reject without altering current inputs, ownership or rewards                       |
| Eight-round tie        | Equal exact territory after round eight                             | Match draw; no invented overtime or random winner                                  |

Balance experiments must identify rules version, seed, policy, catalog, terrain, first-attacker role and equipment. A weapon's usage-conditioned volley or duel result is separate from the win rate of a match whose loadout merely contained it. Publish sample counts and uncertainty, use mirrored starts, and test several policies; ten thousand simulated matches is an initial workload rather than proof of full coverage.

Treat weapon 45-55%, element 47-53%, round-eight frequency and comeback bands as provisional targets to calibrate. Define a comeback cohort at a fixed checkpoint, for example players below 35% land after duel four. Measure conditional matchup outcomes and novice/full-catalog experience; a global 50% average can conceal a broken counter matchup or terrain advantage. Reject structural violations immediately, but change a game constant because of reproducible evidence rather than a single aggregate percentage. Freeze the next rules version only after its exact acceptance cases, simulation scenarios and human explanation/rematch checks pass.

# Game architecture and quality

## Architecture that both the game and Forge must respect

### Rules and presentation

The engine-independent C# rules assembly executes the approved rules contract: weapons, elements, loadouts, volleys, victory/ties, cards, terrain, land and progression. Validated state/actions produce the next state plus typed events. Unity objects, frame timing, animations, Firebase and advertisements remain outside it.

Specify units and rounding. The authoritative board is a **256 × 256 logical grid with 51,040 active circle cells**, initially 25,520 per player. Cuts transfer eligible cells using the approved whole-board quota; rendered contours visualize ownership without deciding it. Identical inputs, state, seed and rules version reproduce results. Animate resolved events rather than assuming independent Unity physics simulations agree.

Unity presents events, previews aiming and collects input. It cannot award online land, progression, purchases or victories. Visible collisions must agree with the resolved event record.

### Online ownership and hidden choices

Firebase Authentication establishes identity. Firebase can support lobby presence and room metadata, but the **C# match service owns online match state and resolution**. Clients must not write authoritative health, land, locks, rewards or results directly. Public lobby documents contain only information that may be visible to both players. Neither opponent actions nor unrevealed aim, power, weapon or dodge values belong there.

Each match stores its participants, immutable rules/balance version, approved starting state, seed, round and volley identifiers, phase, deadline and event sequence. Requests include the match identifier, the published phase snapshot revision and a unique action identifier. The selection-phase revision stays fixed until both locks or defaults resolve; accepting one private lock does not invalidate the opponent's input against that revision. Lock receipts and event-log sequences are tracked separately. Authentication, participant membership, phase, legal action, deadline and duplicate handling are checked before a state transition. Granting rewards and finalizing a match must be idempotent.

Before lock, a player may edit only their own provisional choice. The first accepted lock is immutable under the rules contract. The server privately retains both choices, reveals only permitted information at the specified phase, resolves once, and publishes the resulting events. Debug logs and analytics must not leak secret choices before reveal. A trusted authoritative server can receive the choices directly over authenticated transport; a cryptographic commit/reveal protocol is optional future work, not an automatic requirement for V1.

Timeouts have one approved outcome for each phase: an explicit default action, loss, forfeit or other rule from the contract. A retry must not restart a deadline. A disconnected player receives a canonical snapshot plus events after their acknowledged sequence number. Repeating a lock, reconnect or reward request cannot create another action or grant. Test disconnects immediately before and after locking, resolution and match finalization.

### Bots records and versioning

Bots receive only legally observable information. Difficulty changes policy, accuracy or planning; it does not inspect private choices or invent damage bonuses. After the proposed 20-second queue wait, offer an explicitly labelled bot match that requires player acceptance; allow continued waiting or cancellation. These V1 matches are unranked and tagged separately in analytics.

Online room catalogues are symmetric: **Starter loans the same five weapons to both players; Full loans all 20 regardless of account level**. Equip one to five unique weapons in Starter or one to six normal weapons in Full. With six Full slots equipped, a seventh distinct reserve may be selected; it is usable only while defending an Armoury duel. Progression records mastery and practice unlocks without creating unequal competitive catalogues. Brahmastra is disabled in the pilot and public default; qualify it behind an advanced private-room feature flag before considering broader use.

Match records contain inputs, rules version, seed, events and terminal reason, subject to a retention policy. V1 uses records for reproduction/support; **clip export is V2**. Test supported old records. Balance changes affect new matches only; V2 seasons pin their snapshot, with a documented emergency-change process.

## Physical device quality and release gates

Use rules tests, Unity EditMode/PlayMode tests, reproducible Android builds and physical-phone checks. Unity's Android compatibility requirements \[S15\] state that Android emulators are unsupported, so emulator experiments cannot authorize releases. Pin the actual Unity patch and its matching Android dependencies using the official dependency matrix \[S30\].

Before detailed art, record an actual **2 GB reference phone** and second target phone: model, chipset, OS, ABI, graphics API, resolution and storage. Verify support against the selected minimum platform. Budget acquisition/borrowing during initial setup; RAM alone does not define performance.

The following are **proposed acceptance budgets to validate during the pilot**, not achieved benchmarks. Any revision needs an explicit scope or quality decision.

| **Gate**       | **Measurement and proposed pass condition**                                                                                                                                                        |
|----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Frame pacing   | Target 30 fps at the chosen resolution; provisional steady-state p95 frame time at most 35 ms. Record p99 and long stalls separately. Exclude loading only where the UI clearly indicates loading. |
| Sustained play | Repeat representative matches for a 20-minute run, including maximum expected effects and screen transitions; report thermal degradation and crashes.                                              |
| Memory         | Peak Android total process PSS at most 400 MB on the reference device. Also report managed/native allocations and graphics estimates without incorrectly adding overlapping counters.              |
| Download       | At most 80 MB for the defined device-specific compressed initial download. Record AAB, installed size and optional downloaded content separately.                                                  |
| Correctness    | Accepted golden replays match expected results; no area conservation failure, duplicate grant, invalid transition or disagreement between server result and display.                               |
| Connectivity   | Lock, timeout, reconnect and result recovery pass scripted interruption cases; no secret-action exposure or repeated rewards.                                                                      |
| Accessibility  | Element identity works without colour alone; text scales, contrast is readable, controls remain usable, and audio/reduced-motion options work.                                                     |
| Release        | Signed artifact, dependency/licence records, store declarations, tested migration, rollback route and known issues all refer to the same candidate.                                                |

# Pilot and V1 delivery register

## Pilot ten actionable tickets

Use the approved pilot subset, placeholders/stock assets, one arena, agreed weapons and one land-cut operation. Dependencies determine order.

Diagnose with development builds and repeat acceptance measurements with release builds; follow Unity's device-profiling guidance \[S31\]. Run cheap relevant checks per change and device scenarios on integration candidates/milestones.

| **ID** | **Deliverable**                                                      | **Acceptance evidence**                                                                                    |
|--------|----------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| 1      | Pure C# rules/configuration skeleton and validated pilot data        | Invalid data fails clearly; all five element counter relationships pass tests.                             |
| 2      | Duel state machine, legal locks, resolution, ties and margin         | Approved hit, miss, clash, dodge, timeout and tie examples reproduce expected results.                     |
| 3      | Authoritative arrow trajectory and collision calculations            | Known inputs reproduce landing/collision events independently of rendering frame rate.                     |
| 4      | Script-created grey-box duel scene and Android build                 | Both fighters display on the named physical phone; commit and build configuration are recorded.            |
| 5      | Touch input, aim/power controls and private shared-phone handoff     | Two players can submit legal choices without the normal UI revealing the first choice.                     |
| 6      | Result presentation from resolved events                             | Video and event record agree on arrows, damage, winner and explanation.                                    |
| 7      | Logical-cell ownership, cut rasterization and area accounting        | Ownership totals exactly 51,040 cells; invalid gestures fail safely and transfers obey whole-board quotas. |
| 8      | Card-constrained cut input with visible legal boundary               | Finger input produces the permitted region and previews the actual transfer.                               |
| 9      | Complete shared-phone match and rematch                              | Two people finish the full loop; reset removes previous secret inputs and match state.                     |
| 10     | Replay regression, physical-phone autoplay and baseline measurements | Multiple approved seeds finish; logs, video, frame pacing, memory and download size are attached.          |

Observe five initial testers: can they use controls, finish a cut, explain a loss and voluntarily request a rematch? Record observations, not a claimed retention rate. Revise a failing loop before full art, monetization or online scope.

## V1 register exactly 72 tickets IDs 11 82

V1 includes two-player online/shared-phone play, 20 regular weapons, symmetric room catalogues, six cards, terrain, bots, mastery progression, cosmetics, optional rewarded ads and English/Hindi/Kannada. Brahmastra remains an advanced private-room experiment. The count is **A10 + B5 + C11 + D6 + E6 + F8 + G8 + H8 + I10 = 72**. Split implementation subtasks as needed while preserving traceability.

### Rules 10 tickets

| **ID** | **Deliverable**                                        | **Acceptance**                                                                         |
|--------|--------------------------------------------------------|----------------------------------------------------------------------------------------|
| 11     | Complete weapon registry and schema                    | All 20 records validate; units, identifiers and unlock references are complete.        |
| 12     | Damage, counter, hit/miss and dodge ordering           | Approved interaction fixtures produce exact expected results.                          |
| 13     | Multi-arrow, clash, cover and trajectory interactions  | Each affected weapon has positive, boundary and incompatible-action cases.             |
| 14     | Status-effect lifecycle and special-effect handlers    | Application, duration, cleansing, expiry and combination rules are tested.             |
| 15     | Symmetric catalogues, loadouts and special-astra flags | Starter/Full loans are equal; illegal selections fail identically offline/server-side. |
| 16     | Duel lifecycle with up to three volleys                | Early victory, ties, simultaneous outcomes and timeout cases terminate correctly.      |
| 17     | Card eligibility, margin and cell-transfer calculation | Every card follows the whole-board quota formula and minimum/maximum constraints.      |
| 18     | Terrain definitions and combat modifiers               | Each terrain triggers only in its documented place and phase.                          |
| 19     | Eight-round match progression and scoring              | Alternation, early finish, final ties and terminal rewards are unambiguous.            |
| 20     | Deterministic records and rules snapshots              | Golden matches replay consistently and reject unavailable/incompatible versions.       |

### Bots and balance 5 tickets

| **ID** | **Deliverable**                              | **Acceptance**                                                                         |
|--------|----------------------------------------------|----------------------------------------------------------------------------------------|
| 21     | Bot observation/action interface             | Tests prove unrevealed opponent choices are inaccessible to policy inputs.             |
| 22     | Three bounded bot difficulties               | Each produces legal play; difficulty differences are documented and observable.        |
| 23     | Configurable 10,000-match simulation harness | Runs report seeds, policy pairings, uncertainty and stratified outcomes.               |
| 24     | Versioned balance publication and rollback   | Only new matches adopt settings; invalid releases fail validation.                     |
| 25     | Human/bot balance review report              | Matchups, first-player effects, unlock cohorts and comeback behaviour are inspectable. |

Simulation thresholds are screening hypotheses, not proof that every weapon must have an identical aggregate win rate. Compare like-for-like skill/loadout conditions and inspect dominant combinations. Human tests still decide clarity and enjoyment.

### Duel presentation 11 tickets

| **ID** | **Deliverable**                               | **Acceptance**                                                                   |
|--------|-----------------------------------------------|----------------------------------------------------------------------------------|
| 26     | Runtime archer prefab and rig import          | Approved mesh deforms correctly within the asset budget.                         |
| 27     | Archer animation controller                   | Idle, draw, release, hit and terminal transitions have no visible discontinuity. |
| 28     | Bow, arrow and procedural string attachment   | Grip and release align across approved poses and aiming ranges.                  |
| 29     | Aim and power preview                         | Preview uses compatible trajectory parameters and clear touch feedback.          |
| 30     | Selection, lock and reveal presentation       | Secret information appears only in the permitted phase.                          |
| 31     | Projectile playback from authoritative events | Playback agrees with hit, miss and collision records.                            |
| 32     | Pooled weapon/element effects                 | Every effect is identifiable and respects the measured concurrent-effect budget. |
| 33     | Clash, impact and damage feedback             | Cancelled/surviving arrows and damage remain understandable without sound.       |
| 34     | Dodge and cover presentation                  | Timing and visible protection match resolved rules.                              |
| 35     | Health, timer and combat status HUD           | States remain readable at reference-device size and supported text scaling.      |
| 36     | Camera and two-arena integration              | Controls remain stable; both arenas pass representative frame-time checks.       |

### Land presentation 6 tickets

| **ID** | **Deliverable**                                      | **Acceptance**                                                                        |
|--------|------------------------------------------------------|---------------------------------------------------------------------------------------|
| 37     | Cell-circle map and rendered ownership contours      | Displayed ownership agrees with logical cells; contour rendering cannot alter totals. |
| 38     | Legal card choices and explanation                   | Eligible choices and their limits match the rule record.                              |
| 39     | Finger-cut capture, quantization and cell validation | Bad gestures cannot exceed whole-board quotas or corrupt logical ownership.           |
| 40     | Land transfer and warrior-jump presentation          | Animation finishes at the actual updated ownership state.                             |
| 41     | Totals, boundaries and terrain labels                | Areas reconcile; ownership works without colour alone.                                |
| 42     | Match result and rematch flow                        | Winner/reason is correct; rematch creates fresh state once.                           |

### Screens and usability 6 tickets

| **ID** | **Deliverable**                           | **Acceptance**                                                                                 |
|--------|-------------------------------------------|------------------------------------------------------------------------------------------------|
| 43     | Bootstrap, menu and mode selection        | Shared-phone mode works under its documented offline conditions.                               |
| 44     | Loadout and mastery/practice screens      | Room loans are clear; account progression never restricts the opponent's equivalent catalogue. |
| 45     | Playable tutorial and loss explanation    | New testers complete the core loop without continuous coaching.                                |
| 46     | Settings, accessibility and data controls | Audio, motion, input and permitted deletion flows work.                                        |
| 47     | Pause, background and connection-state UI | Returning users see authoritative phase and appropriate recovery choices.                      |
| 48     | English, Hindi and Kannada localization   | All strings and glyphs render; key screens receive fluent-speaker review.                      |

### Online systems 8 tickets

| **ID** | **Deliverable**                                | **Acceptance**                                                                                          |
|--------|------------------------------------------------|---------------------------------------------------------------------------------------------------------|
| 49     | Authentication and participant authorization   | Unauthenticated/nonparticipant requests cannot read or mutate private match state.                      |
| 50     | Authoritative C# match service                 | Accepted intents resolve through the shared versioned rules assembly.                                   |
| 51     | Private locks, deadlines and reveal protocol   | Late/duplicate inputs and disconnects cannot alter an accepted outcome.                                 |
| 52     | Two-player friend rooms                        | Codes expire; capacity, joining, leaving and host disappearance work.                                   |
| 53     | Match queue and optional labelled bot match    | After 20 seconds, player consent starts an unranked bot match; waiting/cancelling creates no duplicate. |
| 54     | Reconnection and idempotent action recovery    | Snapshots/events restore correct state through scripted interruption cases.                             |
| 55     | Server validation, rate limits and audit trail | Tampered actions fail; useful diagnostics do not expose secrets.                                        |
| 56     | Deployment, health and failure handling        | Load/fault checks meet a declared initial capacity; rollback and recovery are rehearsed.                |

### Progression and monetization 8 tickets

| **ID** | **Deliverable**                                        | **Acceptance**                                                                                                           |
|--------|--------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------|
| 57     | Levels, mastery/practice unlocks and saved progression | One match grants once; competitive room catalogues remain symmetric.                                                     |
| 58     | Daily cosmetic tasks                                   | Claim, expiry and retry cannot duplicate rewards.                                                                        |
| 59     | Cosmetic catalogue and equipment                       | Cosmetics change appearance without combat-stat effects.                                                                 |
| 60     | Shop presentation and eligibility                      | Prices/terms are clear; audience-dependent purchase restrictions are enforced.                                           |
| 61     | Play Billing and entitlement verification              | Test purchases, pending outcomes, restores and revoked entitlements are handled.                                         |
| 62     | Ad policy and conditional remove-ads entitlement       | No remove-ads SKU with rewarded-only ads; if non-rewarded ads are approved, entitlement removes those across reinstalls. |
| 63     | Optional rewarded ads outside matches                  | Decline/failure is safe; verified rewards are granted once.                                                              |
| 64     | Analytics and crash reporting                          | Permitted events reconcile with match records and cohort definitions.                                                    |

### Art and sound 8 tickets

| **ID** | **Deliverable**                     | **Acceptance**                                                           |
|--------|-------------------------------------|--------------------------------------------------------------------------|
| 65     | Archer asset, rig and three outfits | Visual/technical brief, deformation and licence provenance are approved. |
| 66     | Bow and arrow asset set             | Attachments, scale and silhouette pass actual combat-view review.        |
| 67     | Twenty weapon-effect assets         | Readable visual identities fit the runtime effects contract.             |
| 68     | Two arena sets and terrain props    | Assets meet budgets and avoid misleading collision/cover cues.           |
| 69     | Forty UI/store icons                | Sizes, contrast, consistency and usage rights are verified.              |
| 70     | Sound-effect library                | Required cues are distinct, licensed and mixed appropriately.            |
| 71     | Two music tracks                    | Rights, looping, volume controls and mobile import settings pass review. |
| 72     | Store art and screenshots           | Captures match the shipped game and supported languages.                 |

### Performance and release 10 tickets

| **ID** | **Deliverable**                                    | **Acceptance**                                                                            |
|--------|----------------------------------------------------|-------------------------------------------------------------------------------------------|
| 73     | Device performance baseline and regression checks  | Both reference phones complete the defined frame-pacing scenarios.                        |
| 74     | Download/build-size control                        | Defined compressed initial download meets the approved 80 MB budget.                      |
| 75     | Memory and sustained-play checks                   | Peak PSS, lifecycle recovery and leak/thermal evidence are recorded.                      |
| 76     | Reproducible signed Android packaging              | Artifact, source, dependencies and signing authority are traceable.                       |
| 77     | Privacy implementation and store data declarations | Actual SDK/network behaviour agrees with disclosures and consent choices.                 |
| 78     | Content rating and store-page review               | Audience, permissions, wording and regional availability match the product.               |
| 79     | Closed-test operations                             | Current account-specific store requirements and tester feedback actions are satisfied.    |
| 80     | Controlled soft launch and cohort observation      | Four-week observation is scheduled; sufficient mature cohorts exist before judging goals. |
| 81     | Priority patch and rollout procedure               | Fixes have reproducible evidence; rollout/rollback avoids incompatible active matches.    |
| 82     | Operational handover and release decision          | Restore rehearsal, support runbook, known issues and gate decision are complete.          |

IDs 73-75 start during the pilot and recur throughout development. H begins after the pilot gate and supplies C/D assets. E/F/G can overlap only where interfaces are stable and reviewer capacity exists. No individual build needs every later ticket; each milestone declares exactly which features and tests it contains.

# Expansion roadmap and joint schedule

## V2 V4 bounded work packages and decision gates

| **Version/work package**     | **Deliverable and prerequisite**                                                                                                                                                 | **Evidence required before expansion**                                                                                        |
|------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------|
| V2 kingdom                   | Persistent cosmetic kingdom growth, protected homeland and explicitly bounded seasonal grants; requires reliable V1 rewards.                                                     | Property tests, migration/recovery checks and duplicate-grant tests.                                                          |
| V2 ranked seasons            | Leagues, 18-day seasons and cosmetic pass; requires balance snapshot/versioning.                                                                                                 | Full season rehearsal, reward reconciliation, rank/reset rules and purchase entitlement tests.                                |
| V2 social                    | Friends, clans, preset chat, report/block tools; requires a reachable support owner.                                                                                             | Abuse flow rehearsal, permission tests and documented response process.                                                       |
| V2 avatars                   | Optional photo-to-cartoon flow only after audience/legal decisions and a tested local implementation.                                                                            | Consent/deletion/data-flow review; otherwise ship approved non-photo avatars and defer photos.                                |
| V2 content / localization    | Up to 40 weapons, ten cards, six total terrain categories including Plain, and Telugu/Tamil/Marathi/Bengali.                                                                     | Readability, balance and device budgets maintained; fluent-speaker review.                                                    |
| V2 replay export             | User-controlled clip export and sharing, separate from internal V1 records.                                                                                                      | Phone encoding, file size, interruptions, permissions and privacy tests.                                                      |
| V3 real-time discovery       | Separate bot-only prototype for flying/shooting combat; existing turn-resolved mode remains supported.                                                                           | Human feel approval, device budget and explicit architecture estimate from a network specialist.                              |
| V3 networking                | Regional authoritative servers, prediction/reconciliation, disconnect handling and cheat response.                                                                               | Measured packet-loss/latency scenarios, reproducible state recovery and operating-cost model.                                 |
| V3 multiplayer / competition | Four-player discovery, spectating and bounded tournaments with their own networking qualification; paired turn-resolved discovery can proceed independently of real-time combat. | Pairing/waiting behaviour, four-client state recovery, spectator privacy, fairness, tournament recovery and support coverage. |
| V3 iPhone/content            | Separate Apple build/device qualification and content expansion toward 70 weapons.                                                                                               | Current store/toolchain requirements, real iPhone tests and maintenance capacity.                                             |
| V4 economy discovery         | Simulator for earned coins, armies, land and seasonal sinks before world implementation.                                                                                         | No repeatable currency exploit, bounded loss/recovery, newcomer progression and documented economy assumptions.               |
| V4 conquest world            | Sharded/partitioned world state as justified by measured load, challenge deadlines and offline defence.                                                                          | Tested ownership consistency, challenge idempotency, restore and a declared load scenario.                                    |
| V4 armies/alliances          | Earned tactical choices, alliance wars and bounded border losses.                                                                                                                | Explicit combat budget and sidegrade rules; money never buys competitive power.                                               |

Separate kingdom appearance from conquest ownership. V4 must bound absent-player losses, protection, recovery and opponent rank. Unlimited army spending cannot coexist with a “no power” promise: define fair tactical budgets and insulate V1 duel statistics from the world economy.

V2 expansion requires mature D30 cohorts and completed seasonal observations; two 18-day seasons already require 36 calendar days. V3 requires a named network owner and evidence that revenue can cover the forecast operating workload. V4 requires a funded team and an economy discovery pass. “Map loads for 10,000 kingdoms” becomes a specified database/render/load scenario, not a promise of 10,000 simultaneous players.

## One joint schedule and one gate ledger

The original game estimates add to **17-25 months for V1-V4, plus a 4-6-week pilot**, approximately 18-26.5 months. They are conditional planning estimates, not a validated two-year part-time commitment. The original Forge modules add to **14-19 weeks for R1-R3** and **26-35 weeks for R1-R5**, before deciding whether the game pilot is included.

For the first commitment, use a **9-13-week planning range** for a minimal Forge loop (5-7 weeks) followed by the game pilot (4-6 weeks). The previous first-11-week schedule was one scenario: seven weeks of tooling plus four weeks of pilot. Include environment setup, rules clarification and physical-phone qualification inside the agreed scope; do not hide them as unestimated pre-work. Re-estimate after the first working build and after pilot acceptance.

| **Joint stage**            | **Dependencies and owner focus**                                                                    | **Gate and evidence**                                                                                       |
|----------------------------|-----------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------|
| Minimal Forge              | Approved pilot contract; pin platform; build/test/device loop; limited task queue, review and caps. | One deliberately broken task is diagnosed/recovered; one accepted task produces a reproducible phone build. |
| Astra pilot                | Qualified loop and ten pilot tickets; founder concentrates on play observations.                    | Complete match evidence, usable controls, explained outcomes and rematch interest.                          |
| V1 plus needed Forge R2/R3 | Approved pilot; develop only automation that unblocks named game tickets.                           | Milestone builds show measurable game progress and actual human-time savings.                               |
| V1 observation             | Instrumentation, store access and support readiness; development slows enough to respond.           | Mature cohorts, operational stability and documented go/hold/stop decision.                                 |
| Forge R4 productization    | Repeated internal success and outside-user discovery; allocate its own human capacity.              | A stranger completes the supported grey-box template with recorded intervention and failure cases.          |
| V2 or Forge R5             | Choose primary focus from evidence; quote required specialists and support work.                    | Funded capacity plan; no silent assumption that one founder delivers both in parallel.                      |
| V3/V4                      | Prior game gates, bounded prototypes and sustainable operations.                                    | New estimate and specialist/team commitment before production scope is accepted.                            |

Assign each shared ticket and expense once, with links to both products where appropriate. Keep calendars for store testing, cohort maturation and contractor availability alongside effort estimates. Do not add both headline schedules to calculate completion, and do not remove shared effort merely because agents can run simultaneously.

| **Decision point** | **Go**                                                                                                                                                                                   | **Hold/rework**                                                                       | **Stop or narrow**                                                                                                                            |
|--------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------|
| Pilot              | Players understand outcomes and choose to replay; builds/replays are reproducible.                                                                                                       | Specific control/rule confusion or an unreliable loop has a bounded repair.           | Repeated pilot revisions fail to establish enjoyable core play.                                                                               |
| V1                 | Provisional goals: D1 35%, D7 12%, three completed matches/active user/day; proposed normal match completion at least 90%, 95% stretch; 99% crash-free sessions and no blocking exploit. | Cohorts are too small or weak; separate voluntary abandonment from technical failure. | After two bounded, hypothesis-led improvement experiments and sufficient observation, evidence and remaining budget do not justify expansion. |
| V2                 | D30/season participation improve against comparable V1 cohorts; original hypotheses remain D30 5%, 30% season completion and 2% pass purchase.                                           | Season/reset issues or uncertain cohort comparisons need correction.                  | No durable retention improvement: retain V1/V2, defer V3.                                                                                     |
| V3                 | Real-time mode passes device/network qualification and its contribution can sustain servers/support.                                                                                     | Narrow regions, concurrency or mode scope until operations are reliable.              | Preserve turn-resolved play and defer conquest if real-time economics fail.                                                                   |
| Forge product      | Outside projects succeed within the documented scope and support/cost are understood.                                                                                                    | Narrow templates and improve onboarding before billing.                               | Keep Forge internal if outside adoption does not justify a product.                                                                           |

Use the elapsed-time D1, D7 and D30 windows defined in the measurement chapter, with the same exclusions. Define completion and crash-free denominators; report samples and uncertainty. Keep the old 70% overall completion figure as behavioural diagnosis, not a release-quality completion gate. Targets are hypotheses, not benchmarks or revenue promises. Unresolved audience, photo, monetization or territory decisions hold dependent features while independent work continues.

# Future game design and operating boundaries

This chapter supplies planning decisions for the future versions; the delivery register supplies their work packages. **Every new numerical setting below is PROPOSED and unvalidated.** Before production, the owner must approve a version-specific design containing worked outcomes, economy simulations, interface flows and acceptance cases; the technical owner must approve its implementation and operating estimate. Content ceilings are limits, not promises to fill a catalog.

The established two-player game remains available throughout. Its symmetric weapon access, hidden choices, authoritative resolution, land accounting and cosmetic-only purchases are unchanged. Future modes use separate rules identifiers and queues. They cannot silently alter existing matches or turn persistent ownership into stronger V1 weapons.

## V2 a home worth returning to

### Persistent kingdom and protected homeland

The return journey becomes **visit home → choose a duel or social activity → receive the recorded result → personalise home**. The kingdom provides identity and visible accomplishments without replacing the quick Play action. Skipping a day causes no damage or lost buildings.

**PROPOSED baseline:** each account has twelve permanent homeland plots, with three available initially and the remainder opened through published play milestones. Plots hold decorative buildings, gardens, banners and trophies. There are no construction timers, repair bills or paid accelerators. All twelve plots are protected: other players cannot attack, occupy or confiscate them. V4 introduces a separate contestable border; it does not remove this protection.

Use the existing earned cosmetic currency for decorations. Milestones are non-spendable achievements, avoiding another currency. Match rewards and milestone grants use unique entitlement records. A retry, reinstall or restored backup cannot duplicate them. Visits are read-only and friends-only by default; owners can disable visits. The home layout has versioned placements and a safe default if an asset is retired. Previously purchased usable cosmetics remain owned when seasons change.

### Ranked seasons and cosmetic pass

Retain the accepted **18-day season**. Ranked play uses the normalised eligible catalog and unchanged equipment limits; every participant can select the same eligible weapons regardless of account progression. Keep casual, practice and friend-room results outside ranked rating. Display the rules/catalog snapshot before entry.

**PROPOSED baseline:** five placement matches, followed by Bronze, Silver, Gold and Diamond leagues. Match results update a server-owned skill rating; damage dealt, money spent and ad viewing do not. The exact expected-score formula, league thresholds and per-match bounds require balance approval. There is no absence decay within a season. A soft reset moves the visible league down by at most one league while retaining skill information for matchmaking; test whether this produces fair early-season matches.

Close matchmaking before the boundary using the measured maximum match duration. Reconcile every old-season match before issuing final rewards; unresolved technical incidents follow the published cancellation policy. Match and season identifiers prevent results or prizes being counted twice. Freeze the approved balance snapshot for the season; an emergency exploit fix needs an incident decision and player explanation.

The pass is **PROPOSED as a one-time purchase per season**, with twenty cosmetic reward tiers and a free track. Purchasing it grants already-earned paid-track rewards and access to the remaining paid track; it buys no rating, weapons, progress multiplier or protection. Show the closing date, earned tiers and remaining requirements before checkout. Stop new pass sales in the final 24 hours. Automatically deliver earned, unclaimed rewards at settlement; unearned rewards are not implied entitlements. Do not require daily attendance, ads or purchases to complete ordinary tasks.

### Friends clans avatars and replay sharing

Use friend codes and explicit acceptance rather than public contact discovery. Invitations expire; block suppresses invitations, presence and social interaction from that account. **PROPOSED clans hold twenty members:** one leader, up to two officers and members. Officers can invite and organise events; only the leader changes officer roles. No role can transfer another member's assets. After thirty days of leader inactivity, support can review succession with notice and an audit record; there is no silent automated takeover.

Start with translated preset messages and emotes. Clan names and profiles still need reporting and moderation. Clans provide identity and cooperative cosmetic milestones, with capped individual contribution so a few highly active members cannot control all progress. Matchmaking does not mix a coordinated group into an unsuspecting solo competition. A reachable operator owns reports, appeals and temporary feature suspension.

Stock illustrated avatars are the default. Photo conversion is an optional later V2 gate: approve audience eligibility, consent, processing destinations, retention, deletion and abuse handling before enabling uploads. Declining photos must leave the full kingdom and social experience usable.

Replay export starts in V2, after a match is terminal. **PROPOSED default:** a selected thirty-second, 720p highlight rendered on the phone, with a lower-quality fallback after device testing. Use player aliases by default and exclude account identifiers, chat, diagnostic logs and unpublished records. Preview the clip before the user invokes the operating-system share sheet; nothing posts automatically. Interrupted export preserves the game result and cleans temporary files. Test old replay versions, storage limits, deletion and rendering cost.

### Content and the V2 release decision

V2 has ceilings of **40 weapons, ten formation cards and six total terrain categories**. V1 already has five terrain categories including Plain, so this means one additional category. New weapons must introduce readable alternatives with counterplay, rather than stronger replacements; four additional cards need distinct, validated placement choices. Specify their final effects and statistics in reviewed content records rather than inventing forty complete records in this roadmap.

The seven-language destination is English, Hindi, Kannada, Telugu, Tamil, Marathi and Bengali. Add native-language tutorial, purchase and support review for each language. Validate V2 with two rehearsed season settlements, migration/recovery tests and comparable mature engagement cohorts. Kingdom visits and pass purchases alone do not establish that the duel remains enjoyable or that ongoing content is affordable.

## V3 broader competition through independent gates

### Real time combat discovery

Prototype flying/moving, aiming and shooting **against a bot on one phone first**. Keep this mode outside ranked progression. Measure control comprehension, motion comfort, readable counterplay, sustained performance and preference against the existing duel. A failed prototype can be removed while the established game continues.

Only after that feel gate should a qualified network engineer scope authoritative movement/combat, prediction, reconciliation, regional placement, disconnects, cheating and observed service costs. Decide tick rate, transport and lag handling from measurements. The current turn-resolution server is not a ready-made real-time solution, and an AI-generated network layer does not waive engineering review.

### Proposed four player territory mode

Four-player discovery can initially use paired turn-resolved duels independently of the real-time experiment. The following is a **candidate gameplay specification requiring preproduction approval**, not an implementation-ready network design.

| **Situation**   | **PROPOSED rule**                                                                                                                                                                                                                                  |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Start           | Four players receive equal sectors of one board and the same eligible catalog. Assign labels A-D using a seed committed before private choices.                                                                                                    |
| Pairing         | Initial waves are A-B/C-D, A-C/B-D, A-D/B-C; repeat while four remain, up to six waves total. Each pair runs the existing up-to-three-volley duel.                                                                                                 |
| Land changes    | Freeze the board at wave start. Each winner cuts only the defeated opponent's eligible cells, capped at 5% of total board area or that opponent's available area, whichever is smaller. Apply disjoint transfers together after both duels finish. |
| Geometry        | This candidate allows challenge pairs without a shared border and permits disconnected captured pockets. Card legality, cut placement and four-owner rendering require their own approved geometry fixtures; V1 rules do not change.               |
| Draw            | A drawn duel transfers no land. It still completes that pair's participation for the wave.                                                                                                                                                         |
| Three survivors | Pair players with the fewest completed duels, breaking equal counts by a fixed rotating order; the third receives a bye with no land/reward grant. Avoid consecutive byes when alternatives exist.                                                 |
| Two survivors   | They duel each remaining wave under the same candidate transfer cap.                                                                                                                                                                               |
| Idle or absent  | A bye player sees only permitted spectator information. A participant missing inputs follows the approved timeout/forfeit rules; no undisclosed bot replaces them.                                                                                 |
| Elimination     | Zero land or match forfeit eliminates the player. Forfeit land becomes locked neutral territory for that match, preventing an automatic windfall to another player.                                                                                |
| Finish          | One survivor wins immediately. Otherwise, after the sixth completed wave, most owned land wins; identical areas share placement. No arbitrary coin flip resolves an exact tie.                                                                     |

Test seat advantage, bye frequency, collusion, last-place incentives and neutral-area effects before opening ranked four-player play. Prototype disconnected cut readability and concurrent land updates before promising the four-kingdom visual. If six waves or the transfer cap produces weak outcomes, approve revised constants before production. Capacity and secret-state filtering must be tested with four clients; doubling a two-player room is insufficient.

### Spectators tournaments and platform expansion

Spectator messages contain **only explicitly public states and resolved events**. Unrevealed locks never enter the spectator stream. **PROPOSED default:** show at least one completed wave behind active play; delay supplements filtering and cannot make leaked secrets safe. Player-only diagnostics and future-action hints stay absent from broadcasts and exported views.

Begin with free-entry community tournaments and non-transferable cosmetic recognition. **PROPOSED first format:** four or eight entrants in a scheduled round-robin using established two-player matches, scoring three points for a win and one for a draw. Exact final ties share placement. Publish catalog, schedule, absence rules, technical-cancellation policy and prize descriptions before registration. A named operator manages check-in, reports and result disputes; verified tournament-result IDs award cosmetics once. Paid competitive entry, transferable rewards and monetary prizes require a distinct product/compliance decision.

iPhone release has its own toolchain, devices, purchases, accessibility, support and current store qualification. Android success does not satisfy it. The **70-weapon ceiling** includes earlier weapons; expansion depends on balance/readability coverage and maintenance capacity. Keep whichever V3 subsystems pass their gates, and defer the rest without disabling two-player play.

## V4 bounded conquest with a recoverable kingdom

### World structure and the player s day

V4 adds **choose a border challenge → play or review a defence → receive one verified world outcome → adjust tactical loadout**, followed by optional alliance activity. Permanent homeland remains decorative and protected. Conquest belongs to seasonal border ownership, with no effect on V1 HP, damage, weapon access or purchases.

**PROPOSED baseline:** an 18-day world season aligned with ranked-season operations; twelve contestable border tiles per established account at season start. A shard is an authoritative seasonal world partition with a declared capacity, region and rules version. Assign one active shard per account; allow migration only at a reconciled season boundary. Friends may request compatible placement, but admission cannot exceed measured capacity. A map showing thousands of kingdoms is not a claim that thousands fight simultaneously.

### Challenges and offline defence

The server creates a unique challenge, validates participants, reserves an eligible target tile and remaining loss allowance, then snapshots rules and the defender's chosen loadout. Challenges have a **PROPOSED ten-minute expiry**, to be validated against actual match duration. A timed-out request is reconciled by identifier before retry; it cannot create another challenge or reserve a tile indefinitely.

Defenders publish a legal defensive loadout and bot policy; defaults are available. Select live or automatic defence before the encounter snapshot, with no mid-duel takeover. An offline defence bot receives only legal observations and equal catalog/budget access. It cannot inspect the attacker's hidden selection. The authoritative result commits tile ownership and rewards once, or cancels the reservation without transfer. Concurrent attacks cannot consume the same tile or exceed the defender's remaining loss budget.

### Protection armies and newcomer fairness

| **System**         | **PROPOSED initial constraint**                                                                                                                                                                     |
|--------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Loss ceiling       | At most two border tiles lost per account per UTC day and six over one world season, across online and offline losses combined. The twelve protected homeland plots never enter these calculations. |
| Repeated targeting | No attacker can challenge the same account twice within 24 hours; at most one successful attack per alliance against that account per day. Global loss limits still apply.                          |
| Starter protection | Seven days without incoming conquest; protected accounts cannot attack. After five training encounters, a player may explicitly end protection early.                                               |
| Fair opponents     | Match within an approved experience/rating range; do not expose a beginner to veteran accounts merely because both own twelve tiles.                                                                |
| Tactical armies    | Ten total deployment points per encounter, with role-specific costs and slot limits. Unlocks provide choices; every eligible competitor receives the same available choices through loans.          |
| Reset/recovery     | Restore the standard border allocation at the next season; retain homeland, purchases and earned cosmetics. Offer a safe practice/re-entry route immediately on return.                             |

Army roles might support scouting, guarding or formation choices, but their effects need separate simulations and rule approval. They do not add unlimited units, raw account-age power, private-input visibility or passive duel-stat bonuses. Earned resources cannot bypass the deployment budget. Border ownership earns bounded cosmetic recognition rather than compounding combat production; conquest should not make each subsequent conquest automatically easier.

There is no market or transfer system for troops, currency, territory or cosmetics. Do not sell raid tickets, damage upgrades, resource multipliers, additional daily attacks or protection. Cosmetic spending cannot bypass loss limits, match eligibility or newcomer safeguards. These constraints address both spending advantage and advantage from unlimited grinding.

### Alliances seasonal operations and the production gate

**PROPOSED alliances reuse the twenty-member clan structure** and its audited permissions. Officers schedule optional objectives; members retain control over participation. Shared objectives reward capped contributions, with no mandatory attendance penalties, resource confiscation or officer-controlled payout wallet. Publish reporting, suspension and appeal routes; coordinated harassment and repeated targeting require active moderation.

At season close, stop new challenges, settle or cancel outstanding reservations, snapshot final ownership, grant rewards once and rebuild the next world's borders. Announce precisely what resets. Run dry settlements and restore drills before the first public world; compensation for an incident uses recorded affected accounts rather than arbitrary currency creation.

Proceed only after economy simulations, human fairness tests, bounded-loss proofs, fault/concurrency tests and a funded operations team. The **100-weapon ceiling** is conditional on maintaining understandable matchups, suitable device performance and sustainable content review. If conquest cannot meet those conditions, retain the protected kingdom and proven duel modes without launching the shared world.

# Game Forge architecture and workflow

## Product scope and operating assumptions

Game Forge will first automate a supported Unity mobile-game template and use Astra Kingdoms as its initial proving project. It will turn approved, bounded development tasks into reviewable code, assets, test evidence and Android builds. The owner remains responsible for the design decisions, visual direction, gameplay approval and release decision. Successful automation reduces routine development work; this plan does not guarantee that an arbitrary game can be delivered without engineering intervention.

The initial supported template is a small, turn-resolved, two-player or player-versus-bot game with a menu, shared rules library, match state, replay, results and rematch. Astra adds its approved duel and land-cut systems through explicit modules. Real-time networking, arbitrary engines, iOS, open-world generation and general enterprise simulations are separate expansions. Features enter the template only after they work in an integrated game and have reusable acceptance checks.

Use one supported development profile initially: a Linux x86-64 worker, one verified Ubuntu LTS release, one pinned Unity 6 LTS patch, the Android build modules supplied for that editor, Blender and Git. Freeze the exact versions during preflight and record them in a machine-readable toolchain manifest. The initial rendering target is Unity URP with a mobile quality profile; the game chapter determines the tested device floor and performance budgets. Windows and macOS installers require their own validation before receiving a supported badge. A remote Linux GPU worker may support a laptop with another operating system without establishing native compatibility for every model.

**Not verified yet:** the owner's actual GPU and free VRAM, host RAM, available disk space, OS, connected Android device, Unity activation, provider credentials, GPU rental quota, promotional credits, API access and commercial permissions. Preflight records these facts. The plan must not count a proposed Azure or Lightning machine as available capacity until a real job succeeds there. Costs and calendar commitments belong to the combined delivery plan; the gates below determine whether work advances.

## The owner s end to end workflow

| **Step**                      | **Owner experience**                                                                           | **Forge output and boundary**                                                                                        |
|-------------------------------|------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------|
| 1\. Prepare                   | Open the setup assistant and connect a development device and approved providers               | Compatibility report; verified build toolchain; no game-data upload before the project policy is set                 |
| 2\. Describe                  | Select the supported template and provide the game brief                                       | Structured requirements, unresolved questions and a proposed scope; unspecified game rules are not silently invented |
| 3\. Approve the specification | Confirm rules, supported features, art references, intended release countries and spend limits | Versioned specification and immutable initial acceptance cases                                                       |
| 4\. Approve a milestone       | Review the tasks, dependencies, estimate range and required human checkpoints                  | An approved backlog of root tasks, each with a bounded output and completion condition                               |
| 5\. Run                       | Start the milestone or individual ready tasks                                                  | Workers generate candidates, run checks and display progress, actual usage and blocked reasons                       |
| 6\. Review                    | Inspect before/after views, videos, test findings and the playable build                       | Approve a specific candidate; request a bounded repair; hold; or change the requirement explicitly                   |
| 7\. Play                      | Test the milestone on the physical Android device                                              | Owner feedback and device measurements join the same evidence bundle                                                 |
| 8\. Package                   | Select an accepted release candidate                                                           | Reproducible source/export package, asset provenance, test report and unsigned release artifact                      |
| 9\. Release                   | Approve the exact candidate for signing and the intended destination                           | A separate signer produces the signed artifact; store submission remains a separately authorized operation           |

Release 1 starts with an authored Astra specification and its approved pilot tickets. Automated document intake belongs to Release 3. Until then, the interface accepts a structured task file and presents it for review. After document intake is added, Forge highlights contradictions and missing rules, maps each requirement to tasks and tests, and produces a change report whenever the source document changes. A changed requirement never silently rewrites already approved behaviour.

The normal review page needs five panels: task purpose and acceptance cases; proposed changes; technical evidence; visual/play evidence; and cost with remaining limits. The owner sees actionable reasons such as “phone disconnected,” “hand does not grip bow,” or “weapon rule is undefined.” Raw stack traces and implementation settings remain expandable. Rejection requires a specific correction or a selected failure category so the next attempt has a defined target.

## Deployment modes and truthful privacy

“Local” must describe where each part runs. Running open model weights on a rented GPU does not keep assets inside the owner's building. Locally installed cloud coding tools also send inference data to their providers; Claude documents this distinction explicitly. \[S01\]

| **Mode**                        | **What stays under the user's control**                                                       | **What can leave the machine**                                                                                      | **Availability**                |
|---------------------------------|-----------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------|---------------------------------|
| Local assets with cloud agents  | Repository, queue, builds, review page and configured local asset jobs                        | Selected prompts, relevant code, test excerpts and approved screenshots/video sent to selected AI providers         | Initial supported mode          |
| Remote assets with cloud agents | Same project controls; assets may execute on the user's rented GPU                            | Approved asset inputs/results and job metadata reach that GPU provider; agent inference still reaches its providers | Available per verified worker   |
| API assets with cloud agents    | Local orchestration and build option                                                          | Inputs required by each selected asset or coding provider; optional hosted-review copies                            | Available per certified adapter |
| Fully local deployment          | Execution, inference, artifacts, review, logs and credentials inside the approved environment | No inference or project-content egress during operation                                                             | Separate future acceptance gate |

Every project has a policy listing allowed vendors, regions, data classes, hosted-review permission and retention. Each job shows its destinations before first use. Code snippets, logs and video receive the same classification as the project they reveal. Debug logs are not assumed harmless. Hosted review is a separate upload destination, not an automatic consequence of selecting an API model.

The fully local edition requires local replacements for coding, independent review and image/video judgment, local model files and dependencies, offline-capable tool licensing where applicable, and an egress-denied operational test. Enterprise sales may offer a private worker with approved external inference before that gate, but must describe that deployment accurately. “DPDP-compliant by design” is replaced by documented data flows, retention/deletion controls and a release-specific legal assessment in the compliance chapter.

## Core services and ownership

Use a modular single-host application first: a Python orchestrator and API, a local web review interface, SQLite for durable state, a content-addressed artifact directory, and independent worker processes. Keep the domain schemas and provider interfaces separate from the UI. A hosted multi-user deployment can move durable state to PostgreSQL and artifacts to private object storage without changing task semantics. Avoid building a distributed platform before the local workflow is reliable.

| **Component**                | **Owns**                                                                  | **Must not own**                                                         |
|------------------------------|---------------------------------------------------------------------------|--------------------------------------------------------------------------|
| Specification service        | Approved rules, requirements, acceptance cases and revisions              | Provider credentials or release authority                                |
| Orchestrator                 | Dependency readiness, reservations, leases, retries and state transitions | Arbitrary execution inside the owner's normal account                    |
| Provider adapters            | Versioned requests, provider job IDs, normalized results and usage        | Permission to select a more expensive or different-region route silently |
| Coding workers               | Candidate edits in isolated project workspaces                            | Protected acceptance gates, billing policy or signing material           |
| Asset workers                | Generation, cleanup, normalization and technical asset reports            | Automatic permission to distribute the output                            |
| Integration worker           | Serial candidate integration and Unity builds                             | Changes to approved game requirements                                    |
| Device service               | Installation, scripted play, logs and recordings on approved test devices | General access to unrelated phone data                                   |
| Review service               | Evidence presentation and recorded owner decisions                        | Secret values or unscreened public artifact links                        |
| Credential broker and signer | Authenticated provider calls and approved signing operations respectively | Free-form agent instruction following                                    |

All components use the same project ID, specification version, root task ID, candidate ID and artifact hashes. Logs carry correlation IDs and machine-readable event types. Provider prose is stored as evidence rather than interpreted as an authoritative state transition. A worker saying “done” does not mark a task accepted.

## Task contract and exact state machine

A root task is one owner-approved outcome. It contains: project and milestone IDs; immutable root ID; task type; specification version; dependencies and their artifact hashes; permitted paths and tools; input/output contracts; acceptance cases; visual-review requirement; resource profile; permitted provider routes; maximum attempts; active-work timeout; reservation ceiling; and the latest evidence references. Candidate attempts and repair jobs are children of that root. Splitting work into children never creates fresh spending authority.

| **State**                     | **Entry condition**                                                                | **Permitted next states**                                                                     |
|-------------------------------|------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------|
| DRAFT                         | Specification/task proposed                                                        | NEEDS_INPUT, APPROVED, CANCELLED                                                              |
| NEEDS_INPUT                   | Missing rule, incompatible route or owner decision                                 | DRAFT, CANCELLED                                                                              |
| APPROVED                      | Scope and limits accepted                                                          | BLOCKED, READY, PAUSED, CANCELLED                                                             |
| BLOCKED                       | A required accepted dependency, device or resource is unavailable                  | READY, PAUSED, CANCELLED                                                                      |
| READY                         | Dependencies match, policy passes, capacity and budget can be reserved             | RUNNING, BLOCKED, PAUSED, CANCELLED                                                           |
| RUNNING                       | A worker holds the current lease and an attempt is active                          | VERIFYING, RETRY_PENDING, PAUSED, FAILED, CANCEL_REQUESTED                                    |
| VERIFYING                     | Candidate artifacts complete; protected checks running                             | AWAITING_APPROVAL, INTEGRATION_READY, RETRY_PENDING, FAILED                                   |
| AWAITING_APPROVAL             | Technical checks pass; owner decision required for the recorded candidate hash     | INTEGRATION_READY, ACCEPTED after verified integration, RETRY_PENDING, NEEDS_INPUT, CANCELLED |
| INTEGRATION_READY             | Candidate has all required approvals                                               | INTEGRATING, BLOCKED, CANCELLED                                                               |
| INTEGRATING                   | Single writer tests a staged integration candidate against current accepted main   | ACCEPTED, AWAITING_APPROVAL, RETRY_PENDING, FAILED                                            |
| RETRY_PENDING                 | Defined repair exists and root limits allow another attempt                        | READY, PAUSED, FAILED                                                                         |
| PAUSED                        | Owner hold, credentials expired, budget exhausted or recoverable operation problem | READY, NEEDS_INPUT, CANCELLED                                                                 |
| CANCEL_REQUESTED              | Cancellation sent to the executing worker/provider                                 | CANCELLED, FAILED                                                                             |
| ACCEPTED / FAILED / CANCELLED | Terminal result for this root execution                                            | New specification revision requires an explicitly linked new root                             |

Acceptance means the integrated result passed the applicable gates, not merely that a branch produced a valid file. Accepted artifacts are immutable. Replacing a dependency marks affected consumers stale; it does not edit an old acceptance record. The scheduler revalidates dependencies immediately before integration to catch intervening changes.

Integration tests a staged candidate before promoting it to accepted main. If integration materially changes an artifact that required visual approval, route it to AWAITING_APPROVAL against the final integrated hash. That approval can accept the already-verified candidate without repeating integration, provided its recorded inputs and base commit remain current. Promotion checks that base atomically. If accepted main advanced while approval was pending, return to INTEGRATION_READY and revalidate the new integrated result. A prior approval never silently covers changed output bytes.

The default is **three candidate attempts total: the initial candidate and at most two repairs**. A human visual rejection that requests another candidate consumes the same allowance. A root includes a maximum active duration and cumulative budget; parent milestone/project limits apply as well. The actual monetary limits come from the budget configuration. If the owner materially changes the specification, Forge records a new linked root and displays the previous cost. It never labels the change a free retry.

Distinguish quality attempts from transport handling. Polling an existing provider job after a dropped connection does not generate a fresh asset. Before resubmitting after a timeout, reconcile the provider job ID and idempotency key. Allow a small bounded transport retry policy with backoff, but count any actual provider charges toward the same root. An uncertain cancellation displays “provider completion/charge pending”; it does not promise that all external work has stopped.

## Budget reservations recovery and resource scheduling

Before dispatch, the orchestrator atomically reserves an upper-bound allowance for the planned call/job, including configured retries where applicable. The ledger distinguishes available funds, reserved funds and settled usage. Reconcile actual usage after completion and release the unused balance. A call with an unknown billable ceiling is unavailable for unattended mode until the adapter provides a bounded request or a credible reservation policy. Provider limits and the local ledger supplement each other.

Do not treat a prepaid balance as guaranteed profitability. Taxes, payment processing, refunds, storage, video delivery, GPU idle time and support are separate costs. Concurrent dispatch checks aggregate reservations; two workers cannot each spend the same remaining allowance. The owner can stop new dispatch immediately, while already committed external jobs remain visible until settled. The system never raises a limit automatically to finish a milestone.

Persist state and events before and after external operations. A worker lease has an expiry and heartbeat; a replacement worker reconciles old provider jobs before taking over. On restart, jobs enter a recovery check rather than restarting blindly. A laptop sleep event, device disconnect or GPU loss pauses affected work without losing task lineage. Checkpoint expensive accepted assets and evidence immediately. Run a restore drill from the database, repository and artifact backup before calling the workflow recoverable.

Scheduling uses a capability record per worker and **a lease per actual GPU/device/build workspace**. Record GPU model, available VRAM, compute support, host RAM, disk, driver/runtime versions and successful route benchmarks. A job requests a compatible profile. A laptop GPU job does not unnecessarily block a separate rented GPU. Conversely, multiple processes on one physical GPU cannot each assume its full memory. Begin with one generation job per GPU and one Unity build per workspace; raise concurrency only after measurements justify it. These are scheduling rules, not a mechanism for pooling small GPUs into one larger VRAM allocation.

# Forge providers assets and safety

## Agent provider adapters and commercial boundaries

Forge supports two distinct coding connections. **User-owned official CLI connections** launch the provider's unmodified tool with its own authentication flow; the end user owns the subscription or API relationship. **Forge-managed API agents** use Forge's own tool runner and commercially appropriate API accounts. They can share task schemas and evidence formats, but they do not share subscription entitlements or assume identical tool behaviour.

Current Claude Code terms permit an unmodified binary in products under specified conditions, including direct end-user authentication and billing, and prohibit intermediating its usage on users' behalf without another agreement. The same page says product developers should use API authentication and must not collect Claude account tokens. Therefore Maker/Studio must not bundle pooled Claude Code usage by default. A commercial Claude API application is a separate arrangement; the Code-binary restriction must not be misrepresented as a blanket prohibition on API products. \[S02\]

Each adapter declares supported operations, authentication method, direct billing party, data destinations, quotas, timeouts, cancellation behaviour, usage reporting, structured-output support and tested versions. A subscription connector can pause on exhausted quota or authentication expiry. An owner-configured fallback may move a future job to an approved API route within its separate budget; it cannot extract tokens, bypass quotas or silently create paid usage. Manual browser-only services are labelled manual steps rather than presented as unattended API adapters.

For the first unattended implementation, prefer the API runner because its tool and credential boundaries can be controlled explicitly. Official CLI connections remain useful for the owner's directly authenticated workflow, but their unattended status depends on verified isolation and reliable machine-readable results. If a connector cannot prevent generated tools from reading its credential material, keep that route supervised instead of claiming a protection it cannot enforce.

Model selection remains a configurable role mapping: builder, independent reviewer, visual judge and optional video judge. Use a second review pass where it changes confidence, while deterministic tests remain the authority for rule correctness. A judge must cite the specific acceptance case and frame/log evidence behind a rejection. Two models agreeing is not proof that the game is correct.

## Workspace isolation Unity integration and the cloud link

Every active code task receives a separate worktree or clone rooted at a pinned accepted commit. Git documents worktrees as distinct checkouts supporting simultaneous branches. \[S03\] Each Unity checkout has its own generated caches, temporary files and build directory. Preserve the full source set: Assets, Packages, ProjectSettings and asset metadata. Unity's .meta files contain IDs and import settings; dropping them can break scene, texture and script references. \[S04\]

Use a single integration writer. Workers propose changes; they do not push directly to accepted main. Shared scenes, prefab roots, package manifests and project settings have declared ownership or locks. The integration worker applies one candidate, resolves its base, imports, compiles, runs relevant protected checks and builds the merged result. A merge conflict is a repair under the original root. Tests that passed on an older branch do not authorize an untested integration commit.

Keep the cloud link outbound from the worker. An authenticated queue message names a permitted workflow, project, commit, artifacts, lease and expiry. It is not an arbitrary shell command. Authenticate artifacts by hash, reject replayed jobs and use short-lived project-scoped identities. Never publish raw Unity MCP or the owner's shell as an Internet endpoint. A hosted review service cannot grant itself code-execution or signing authority.

The proposed containment boundary includes the agent process, file tools, MCP servers, hooks, build scripts and generated editor code. Claude's documentation explicitly notes that its Bash sandbox alone does not contain all those surfaces. \[S05\] Use a dedicated worker account plus an appropriate container/VM or equivalent enforced environment, with only required project mounts and approved network access. No privileged Docker socket, normal home directory, unrelated repositories or signing keystore is mounted into generated-code execution. A restricted policy file in the same writable repository is insufficient; enforcement configuration is owned outside the worker's writable scope.

## Secrets egress and signed releases

API keys stay in a trusted credential broker or server secret manager, outside model context and generated subprocess environments. The broker validates tenant/project, provider, model, request class and remaining reservation before authenticating a request. It returns normalized data and usage, not secret values. Redact logs and diagnostic exports. Avoid exposing arbitrary destination URLs through the broker, because a permitted request mechanism must not become a general credential-forwarding proxy.

The BYOK local mode uses the user's secret store and direct provider billing. Managed mode uses server-side secrets, tenant-scoped gateway credentials, per-project authorization and revocation. A hosted user cannot read another user's objects by changing an identifier. Review downloads use short-lived authenticated access. Support personnel need explicit, logged access to any shared diagnostic material. Official CLI tokens stay in the vendor-controlled sign-in arrangement; Forge does not collect them into its gateway.

Network policy distinguishes build dependency downloads, provider inference, Git synchronization, artifact uploads and telemetry. Only the necessary destinations are enabled for each worker role. Generated code cannot choose a new provider or upload to a hosted review service merely by writing a configuration file. Trust requirements extend to pinned model scripts, dependencies, imported editor packages and installer updates.

Builds are unsigned or development-signed until the release gate. The owner approves a candidate hash with its tests, device evidence, provenance report, package ID and version. A separate signing service receives that exact artifact and approved metadata; no build script gets private signing keys. For Google Play, use the distinction between the upload key and the app-signing key in Play App Signing. \[S06\] Archive the signed artifact hash, certificate fingerprint and approval record. Store submission is a separate action with its own destination/track and approval; a normal code merge cannot publish a game.

## Model routes hardware qualification and first use licence gates

The following is a routing shortlist, not a claim that the owner's machine can run every entry. Published requirements are facts checked on 5 October 2026; benchmark gates and exclusions are proposed Forge policy.

| **Lane / candidate**                                       | **Published requirement or restriction**                                                                                           | **Forge decision**                                                                                                                            |
|------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------|
| Concept images: FLUX schnell or approved API image service | Exact checkpoint, dependency and service terms need recording                                                                      | Benchmark the selected configuration; do not market an untested compressed 8-12 GB profile as guaranteed                                      |
| Hunyuan3D 2.1                                              | Repository lists 10 GB shape, 21 GB texture and 29 GB for combined shape/texture generation \[S07\]                                | Separate stage profiles; optional, territory-restricted route. Low-memory/community variants require independent benchmarks                   |
| Original TRELLIS                                           | Repository specifies at least 16 GB and primarily Linux-tested code \[S08\]                                                        | Separate adapter and benchmark; a T4-sized memory capacity alone does not establish runtime compatibility                                     |
| TRELLIS.2                                                  | At least 24 GB; currently tested on Linux, with separate dependency licences noted \[S09\]                                         | Linux route only initially; validate actual GPU architecture, dependencies, memory and export quality                                         |
| HY-Motion 1.0                                              | Repository lists Lite at 24 GB and full model at 26 GB; optional prompt-engineering memory is additional \[S10\]                   | Remove the unverified “6 GB Lite” promise; short clips and any offload mode are benchmarked configurations, not guaranteed support            |
| UniRig or approved commercial rigging                      | Exact release/model licence and measured resource use must be verified                                                             | Gate by tested skeleton contract and deformation quality; no fixed unverified 8 GB guarantee                                                  |
| GVHMR                                                      | Default licence limits use to educational, research and nonprofit purposes and directs commercial users to seek permission \[S11\] | Excluded from both the internal commercial game pilot and the customer product unless documented permission and dependency rights are cleared |
| Meshy, Tripo, DeepMotion, Mixamo or stock animation        | Capabilities, API entitlement, pricing and output terms differ                                                                     | Certify each adapter; show manual steps explicitly; include regeneration and rejected outputs in usage                                        |
| Sound/music                                                | Unspecified “open audio models” is not a licence decision                                                                          | Start with a documented licensed library or a specifically approved model/service and output-rights record                                    |

A 12 GB GPU is not a complete local pipeline. A 24 GB card does not meet every published requirement in the table. NVIDIA's T4 has 16 GB of device memory \[S12\]; an Azure T4 therefore does not satisfy the 21/24/26/29 GB profiles by specification alone. Resolve insufficient memory by selecting a permitted lighter route, splitting genuinely independent stages, or renting a suitable worker. Do not promise that sequential jobs solve the peak memory requirement inside one model.

Before first generation, the project declares intended distribution countries and whether the assets may appear in publicly accessible demos or marketing. Hunyuan3D 2.1 and HY-Motion licences restrict use and distribution of outputs outside their defined territory, which excludes the EU, UK and South Korea. \[S13, S14\] These routes are disabled for a globally distributable template unless separately appropriate rights are documented. Downloading weights after an acceptance checkbox does not remove output restrictions. Prefer a permitted route at the beginning rather than creating an expensive set of assets that later needs replacement.

## Asset contract and production workflow

Every asset starts with a brief containing intended screen size, art reference, silhouette, scale, materials, attachment points, animation requirements, target budgets and licence requirements. For the archer, keep character, bow, arrow and procedural bowstring as separate assets. Approve a neutral pose and proportions before spending on rigging and motion. A character that looks good in one render is not yet a production-ready animated model.

| **Contract** | **Required normalized information and acceptance**                                                                                                                |
|--------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Geometry     | Units, axes, pivot, bounds, topology/triangle count, LODs where needed, normals, UVs, named attachments and collider policy                                       |
| Materials    | Explicit Unity shader mapping, base/normal/roughness/metallic/opacity interpretation, channel packing, texture size, colour space and mobile compression settings |
| Skeleton     | Stable joint names/hierarchy, rest pose, orientation, root, skin weights and the approved retarget mapping                                                        |
| Motion       | Frame rate, duration, clip name, root-motion/in-place declaration, contact timing, loop policy, transition expectations and gameplay event markers                |
| Audio        | Source rights, clip purpose, format, sample rate, loudness target, loop boundaries and import/compression policy                                                  |
| Provenance   | Input references and rights, model/service/version, generation date, applicable terms, transformation history, attribution, territory flags and final hashes      |

The standard lane is brief → concept approval → generation → Blender normalization → technical report and turntable → visual approval → rig → deformation review → motion/retargeting → contact/transition review → Unity prefab → physical-device evidence. Unnecessary stages may be skipped for a prop, but the asset contract remains. The bowstring is driven from draw state and attachment points; the release marker triggers the projectile at the approved moment. Check hands, shoulders, elbows, feet, grip, bow penetration, root drift and recovery transitions in motion, not only in a still image.

The initial delivery package uses a normalized FBX mesh/animation export, explicit texture files, a manifest and the assembled Unity prefab; retain editable Blender sources where available and permitted. Provider GLB output is an intermediate until its tested conversion/import adapter succeeds. HY-Motion documents seamless loops and in-place modes as unsupported; loop cleanup and root-motion conversion therefore need their own implementation and review. \[S10\]

A new mesh topology invalidates skinning and usually dependent animation validation. A changed skeleton invalidates retarget evidence. A texture-only revision still requires material/import and visual checks. Route switching produces a proposed replacement asset; Forge does not overwrite an approved asset or assume that matching file extensions mean compatibility. Adapters must normalize and validate their outputs against the same contract. If the new route cannot satisfy it, the task pauses with a clear incompatibility report.

The initial budgets are per asset and per scene, linked to the reference phone. Geometry limits alone cannot determine performance: materials, transparency, lights, textures, audio and scripts contribute. The integration report therefore checks the full scene and running build. Licensed manual cleanup remains a planned escape path when automatic assets fail the approved visual or deformation standard.

### Initial asset budgets for the Astra template

These are proposed production ceilings for the first mobile art pass. They make an asset brief concrete; the physical-phone frame-time and memory gates remain the acceptance authority. An asset under its triangle ceiling can still fail because of transparency, materials, animation or texture cost.

| **Asset or resource**        | **Proposed starting ceiling**                                                                   | **Acceptance condition**                                                                                             |
|------------------------------|-------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------|
| Archer                       | 8,000 triangles, 50 deforming bones, four weights per vertex, at most two materials             | Two equipped characters deform and animate correctly in the representative combat scene                              |
| Bow and arrow                | 1,500 triangles per bow and 200 per arrow                                                       | Grip, projectile silhouette and string attachment remain clear at the gameplay camera                                |
| Visible arena                | 40,000 triangles; at most 16 opaque material batches                                            | Baked/simple lighting baseline; no automatic addition of realtime shadow-casting lights                              |
| Total visible geometry       | 70,000 triangles in the initial worst-case scene                                                | Include characters, projectiles, terrain and effects in the actual measured scene                                    |
| Draw calls                   | 60 in the declared representative combat scene                                                  | Report actual engine counters alongside frame time; do not equate draw calls with batches without naming the counter |
| Character and arena textures | Normally at most 1,024 pixels per dimension; larger shared UI atlases require measured approval | Correct compression and colour space; only currently needed outfits loaded                                           |
| Effects                      | At most 12 active emitters and 64 live particles per emitter                                    | Pool reused effects; examine transparency overdraw and visual clutter on the phone                                   |
| Audio                        | Short mono effects where appropriate and streamed music when qualified                          | No clipping at the approved mix; independent volume controls and tested loop boundaries                              |

The required archer clip set is idle, draw, hold, release, recover, hit, left dodge, right dodge, jump, defeat and victory. Cover presentation may use a compatible pose rather than another full animation system. Idle and hold need tested loops; attacks need explicit event markers and transitions. HY-Motion does not claim native seamless-loop or in-place modes, so those clips require a separate cleanup and validation step when that route is used. \[S10\] The pilot can demonstrate every gameplay state with simple poses and procedural movement before this production clip set exists.

## Tests real device evidence and review decisions

The physical Android device gate exists in **Release 1**. Unity explicitly states that Android emulators are unsupported. \[S15\] An emulator may remain an experimental convenience check, but it never substitutes for an ARM64 device run or decides that a milestone is release-ready. Unity Editor/device simulation can help inspect layout and input; it does not establish phone performance.

Run cheap checks at every candidate: pure C# rule tests, schema validation, static checks and relevant Unity EditMode/PlayMode tests. Build and device checks run on integrated milestones and changes affecting interaction, rendering, platform behaviour or performance. Maintain protected acceptance cases independent of the builder's proposed unit tests. A change that deletes a failing test, disables a gate or changes a threshold requires separate review and cannot earn a normal automatic pass.

The device service installs to a dedicated test device selected by serial number, launches a deterministic scenario, captures logs/screenshots/video, and records result files. A development-only replay/test interface drives game actions by semantic identifiers. Remove or disable that interface in distribution builds. Add actual touch-input scenarios because directly invoking game actions alone does not test the UI path. Device disconnect, permission dialogs and interrupted runs are reported as incomplete evidence rather than converted into a pass.

| **Evidence class** | **Minimum purpose**                                                                                |
|--------------------|----------------------------------------------------------------------------------------------------|
| Rules              | Approved examples, edge cases and invariants; expected result independent of presentation          |
| Replay             | Specification/rules version, seed, initial state, ordered commands, event log and final state hash |
| Integration        | Exact merge commit, toolchain manifest, asset hashes, import/compile results and tests             |
| Device             | Phone identity/profile, OS, build ID, scenario, logs, screenshots and video                        |
| Performance        | Agreed frame-time/memory/loading budgets on reference devices, with capture conditions             |
| Human              | Decision, reviewer, timestamp, candidate hash and correction reason where relevant                 |

Use several fixed regression seeds plus generated valid cases. Investigate replay/state mismatches and rule invariants instead of relying on a video judge to notice a subtle scoring error. Judge findings identify the relevant frame, log or acceptance case and can be uncertain. Owner judgment remains necessary for readability, visual fit and fun. For performance measurements, disable video capture or run a separate capture condition when recording distorts the result.

Technical pass, visual approval, integrated acceptance and release approval remain distinct fields. Code within its approved scope may proceed automatically after required tests/review. New scenes, screens, character looks, rigs and motion require owner approval. Deterministic derivatives can be automatically accepted only under an explicitly approved template policy. Any material artifact change invalidates its prior visual/release approval and requests fresh evidence.

# Forge releases and commercial offering

## Installer support updates backups and telemetry

The installer verifies rather than conceals prerequisites. It reports OS/toolchain compatibility, editor activation, build modules, disk capacity, GPU profile, provider authentication and a real-phone connection. Dependencies and model downloads are versioned, checksum-verified and accompanied by their applicable terms. Forge-owned code can use Apache-2.0; that label must not imply that Unity, every model, third-party binary or generated output shares the same licence.

Provide a setup diagnostic and a minimal sample build before importing a customer's full project. Document which steps need a user account or a manual licence decision. An uninstall removes the application by default while preserving projects and clearly identifying any optional removal of caches/models. A broken provider connection offers re-authentication, another approved route or pause; it does not silently downgrade to an unsupported path.

Updates use versioned channels, signed/checksummed distributions and a compatibility report. Back up project state before database/schema or template migrations. Do not update a model, editor, provider adapter or acceptance rubric halfway through an active task. Keep the previous working version available for rollback. A changed provider API/terms entry can disable new jobs for that adapter while leaving accepted artifacts and evidence accessible.

Back up source, specifications, durable queue/ledger state, accepted artifact bytes and approval/provenance records. Regenerable caches are lower priority. Encrypt backups, separate credentials from project archives, and support export without ongoing subscription dependence. Provide configurable retention for failed candidates, full videos and logs. The user can delete a project and review what copies may remain with selected external providers under their policies.

Proposed operational defaults are daily backups, failed-candidate retention for 14 days and raw diagnostic/video retention for 30 days. Keep accepted source, release evidence and provenance for the project's lifetime unless the owner deletes them; allow a stricter project policy to override the defaults. Budget backup storage explicitly and verify a restore before an update or machine migration is considered safe.

Product telemetry is off by default. Optional metrics are limited to operational counts and timings with no prompts, code, images, audio or video. Diagnostic sharing is separate, previewable and redacted. Hosted services maintain minimum security/billing logs with stated retention; they do not describe required service records as optional telemetry. Publish support boundaries and response expectations actually staffed by the business. Unresolved arbitrary-game debugging is not hidden inside a promise of unlimited priority support.

## Releases R1 R5 and exit gates

The releases describe capability gates. The combined timeline must distinguish building the automation, using it to build Astra, and preparing it for external customers. Work completed once should not appear as free duplicate capacity in both plans.

| **Release**                                | **Deliverables**                                                                                                                                                                                                                                | **Exit gate**                                                                                                                                                                                                                  |
|--------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| R1: reliable code/build/device loop        | Approved task schema, durable queue, one coding connector, independent reviewer, isolated workspace, protected checks, serial Unity integration, physical-phone runner, local evidence page, root retry/spending limits and restart recovery    | A bounded set of approved pilot tasks produces an integrated playable Android milestone; injected rule/compile/UI failures are detected; a cap stops further dispatch; restart and device-disconnect recovery are demonstrated |
| R2: controlled assets                      | One proven route per required lane, asset contracts, Blender normalization, visual approval, rig/motion retargeting, provenance and GPU capability scheduler; alternate routes added only after adapter checks                                  | A prop and an archer with required clips enter the existing pilot, pass contract/deformation/contact checks and are approved in the running phone build; actual cost and failed attempts are recorded                          |
| R3: complete supported-template production | Document-to-specification intake, ambiguity handling, dependency planning, additional UI/audio/localization lanes, balance/replay reports, bounded parallelism, release packaging and separate signing                                          | A second game brief within the declared template produces an exportable, tested build; requirements trace to checks; all release assets have provenance; owner approves and signs the exact candidate                          |
| R4: externally usable product              | Supported installer, versioning/rollback, examples, template documentation, diagnostic export, backup/restore, privacy controls, licence inventory and support process                                                                          | Independent users on the declared profile reach a grey-box phone build from the guide; failures and human assistance are recorded; backups restore a project; exported projects work without a Forge subscription              |
| R5: paid offering                          | Team identity/roles, private hosted review, usage ledger, approved BYOK adapters, billing/refunds, tenant isolation, service monitoring and staffed support; managed inference is optional under its separate commercial, billing and cost gate | A real paying customer completes an allowed BYOK workflow; billing reconciles, limits hold under concurrency, cross-tenant access is blocked and the delivered service meets its written support promises                      |

R1 does not require a finished generated archer, arbitrary document intake or commercial online multiplayer. Its first infrastructure milestone and the subsequent Astra pilot execution are separate tasks. Early external design-partner feedback can examine the supported workflow once R1 is usable; there is no reason to postpone all product discovery until the entire game is finished. Paid and broad public promises wait for the relevant gates.

## Offering measurement and scope decisions

Keep the open core useful: local orchestration, documented adapters, supported template, review UI, source export and BYOK. Suggested Maker value is hosted private review, backup convenience, managed project history, higher artifact retention and tested template updates. Suggested Studio value is shared roles/approvals, team review, policy controls, multiple workers, private templates and an audit/export workflow. Sell these services separately from direct user-owned CLI subscriptions.

Managed model usage can be a separate metered/prepaid service for approved API products when the provider arrangement supports it. Publish included allowances explicitly; a monthly price without a defined usage entitlement is incomplete. Do not use Forge credits to pool Claude Code usage under the ordinary embedded-binary arrangement. Enterprise value may include private deployment and onboarding; fully local inference is sold only after its separate qualification gate.

Measure accepted **root outcomes**, not raw merged ticket counts. Report completed roots divided by approved roots attempted, including failures and manual fixes; cost per accepted root/build; human minutes per root; time to first playable build; first-pass visual acceptance; escaped defects; recovery success; and external projects retained. State sample size and scope. A 70% completion target is a hypothesis for a defined workload, not proof of general autonomy.

When repeated tasks exceed the limits, narrow the template or repair the failing stage before expanding. If customers need substantial engineering support, price and describe a guided service instead of claiming effortless self-service. If the game succeeds but external Forge demand does not, keep the tool internal. An asset-only pivot requires independent proof of asset quality, licensing feasibility and demand. The business decision follows observed outcomes rather than assuming every unbuilt lane can become a profitable fallback.

# Finance and commercial economics

**Planning basis: 5 October 2026.** This section applies to Astra Kingdoms and Game Forge together. Amounts from the supplied plan are retained as planning estimates, not supplier quotations or guaranteed expenditure. Product thresholds below are proposed decisions to test. Legal and platform requirements are supported by the official sources at the end; they must be refreshed before the first affected release or commercial use.

## Fund the two projects through one controlled budget

The game and the tool have separate outcomes but share development work, model calls, GPU jobs and test infrastructure. Maintain one cash ledger with project allocations. A model job used to create the Astra archer is one expense, even when it also proves Forge's asset pipeline. Allocating it to both reports must not double the combined budget.

### Original cash estimates and their limits

| **Project and stage** | **Supplied cash estimate** | **Funding decision**                                                                                                |
|-----------------------|----------------------------|---------------------------------------------------------------------------------------------------------------------|
| Astra pilot           | ₹10,000-30,000             | Release only the pilot budget; prove the duel, land cut and rematch experience.                                     |
| Astra version 1       | ₹1-3 lakh                  | Commit in milestones after the pilot; obtain actual artist and service estimates before engaging them.              |
| Astra version 2       | ₹1-2.5 lakh                | Fund after version 1 provides sufficient retention, operating-cost and audience evidence.                           |
| Astra version 3       | ₹5-12 lakh                 | A separate investment decision, including a scoped network-engineering engagement and ongoing operations.           |
| Astra version 4       | ₹10 lakh or more           | A lower bound, with no validated upper limit; authorize only through a new funded project budget.                   |
| Forge releases 1-3    | ₹30,000-90,000             | Model usage and GPU planning allowance; allocate shared Astra work once.                                            |
| Forge release 4       | ₹20,000-50,000             | Product packaging and independent-user validation after the internal workflow succeeds.                             |
| Forge release 5       | ₹50,000-1.5 lakh           | Gateway, billing, hosting and professional-review allowance, conditional on paid demand and measured delivery cost. |

The Astra stages imply a **minimum of ₹17.1 lakh through version 4**, excluding Forge, founder time and any omitted ongoing costs. The version-4 figure is open ended, so the plan has no defensible maximum total. The pilot plus version 1 imply ₹1.1-3.3 lakh before allocating shared Forge expenditure. Forge's listed stages total **₹1-2.9 lakh**. These totals are arithmetic on the supplied estimates, not an independent validation that the scope can be delivered for those amounts.

For each expenditure, record date, vendor, job or invoice identifier, cash paid, taxes/fees, promotional credit consumed, the benefiting milestone and an allocation rule. Keep founder hours in a separate capacity ledger. They may not require an immediate cash payment, but they constrain the schedule and determine whether the product is economical to maintain.

| **Cost category**                                  | **Budget treatment**                                                                                       |
|----------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| Shared AI, GPU and build work                      | Charge once; allocate between the game and tool using recorded purpose or usage.                           |
| Game-only art, audio and engineering               | Charge to the relevant game version, including revision allowances.                                        |
| Tool installer, documentation and customer support | Charge to Forge, including support for free users where material.                                          |
| Hosting, storage, logs, bandwidth and monitoring   | Show monthly operating costs separately from development costs.                                            |
| Store, payment, tax and refund costs               | Use applicable contracts and settlement reports; do not treat customer receipts as spendable profit.       |
| Devices and specialist contractors                 | Obtain a specification and estimate before committing; avoid assuming existing devices cover every target. |

Promotional balances, including the mentioned cloud credits, are **contingent resources rather than cash**. Record eligibility, covered services, expiry, rate limits and whether paid overflow is enabled. Do not subtract an unverified credit from a funding requirement. Maintain both cash actually paid and equivalent usage at the normal applicable rate so the prototype does not appear artificially cheap.

### Funding gates and stop conditions

Before a milestone begins, set its cash ceiling, founder-hour allowance, review date and minimum deliverable. Reserve enough cash for already committed jobs, recurring services and foreseeable refund obligations. Automated jobs may consume only an authorized allocation; crossing it stops new work.

If a milestone exceeds its allowance, reduce optional scope or create a new explicit budget decision. “Twice the estimate” is an escalation trigger, not permission to spend twice the budget. When a game experiment fails, preserve the reusable rules, assets with valid rights, test fixtures and tool work, then stop further optional game expenditure.

Version 3 and version 4 require an operating forecast that includes the current game's maintenance while the next version is built. “Funded by game income” means available cash after operating commitments and reserves, not lifetime gross sales. A small positive month does not establish that several months of engineering can be funded.

## Make game monetization measurable and consistent with fairness

Version 1 defaults to optional rewarded ads outside active matches and direct purchases of clearly described cosmetics. Ad rewards use cosmetic currency or cosmetic benefits, not ranked combat advantages. Purchase availability must never change attack damage, loadout strength, defensive protection or competitive eligibility.

**Do not offer a remove-ads purchase when the only advertising is optional rewarded advertising.** There is then no compulsory advertising for that purchase to remove. If a later release introduces non-rewarded advertising outside matches, consider a remove-ads product at that time, explain exactly what it removes, and state whether optional rewarded offers remain available.

Version 2 introduces replay sharing and a cosmetic season pass. Define the season's start/end, purchasable rewards, progress required, treatment of late purchases, already earned rewards and unclaimed rewards. An 18-day season is a content schedule; it does not automatically imply an 18-day recurring billing product. Implement and disclose the selected one-time purchase or subscription arrangement explicitly.

Tournament passes remain outside the initial monetization implementation. The later specification must distinguish cosmetic or spectator benefits from paid competitive entry and identify whether any prize has monetary or transferable value. Free competitive participation with non-transferable cosmetic recognition is the proposed initial tournament design. Any different financial design requires a separate classification and store-policy assessment before implementation.

### Revenue and contribution model

Use settled or supportable net amounts, with one consistent period, region and player population:

| **Measure**              | **Definition**                                                                                                                                                  |
|--------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Net IAP revenue          | Customer purchase charges minus applicable indirect taxes, store/payment deductions and refunds or chargeback adjustments, without double counting adjustments. |
| Net ad revenue           | Publisher receivable after network deductions and invalid-traffic adjustments; not advertiser spending.                                                         |
| Game net revenue         | Net IAP revenue plus net ad revenue.                                                                                                                            |
| Variable delivery cost   | Match/profile services, database operations, bandwidth, storage and other usage costs, plus attributable variable support/moderation.                           |
| Contribution             | Game net revenue minus variable delivery cost.                                                                                                                  |
| Operating result         | Contribution minus fixed operations and the development/content costs recognized in the chosen management view.                                                 |
| Acquisition contribution | Cohort contribution after deducting attributable acquisition spending.                                                                                          |

For ads, calculate revenue from **paid impressions × realized publisher eCPM ÷ 1,000**. Record ad opportunity, opt-in, fill, impression and reward completion separately. Do not assume every player watches every offered ad. Revenue forecasting requires observed impressions and realized rates for the actual audience; this document supplies no invented India eCPM or earnings forecast.

Compare backend cost per monthly active user with net revenue per monthly active user for the same month. Also track cost per completed match and per player-hour, which explain infrastructure changes when match duration or real-time play changes. Monthly average concurrency is total player-hours divided by hours in the month; capacity planning must additionally measure peaks.

Passing D1 or D7 retention targets does not establish financial viability. Paid acquisition begins only as a bounded experiment with measurable cohorts. Compare cumulative net contribution per acquired player against acquisition cost and specify an acceptable payback period. Organic and paid cohorts remain separate because friend invitations may behave differently from advertisements.

The version-3 continuation gate is **sustainable contribution after all material variable costs**, together with enough funding for fixed operations and development. Merely covering server invoices is insufficient. Replay storage, abuse handling, regional infrastructure and specialist support enter the forecast when their features are introduced.

## Launch Forge with prices that have defined obligations

The original prices can remain **test prices**, but the initial paid product uses customers' own approved provider accounts or API keys. It includes **no unspecified hosted AI allowance**. Customers see the software charge separately from their provider charges. State whether displayed regional prices include applicable taxes before accepting orders.

| **Offering** | **Proposed test price**               | **Initial commercial scope**                                                                                                     |
|--------------|---------------------------------------|----------------------------------------------------------------------------------------------------------------------------------|
| Open core    | Free                                  | Documented core workflow, approved local routes and BYOK integrations; community support.                                        |
| Maker        | ₹999/month in India; \$19 elsewhere   | Individual workflow features and a defined hosted-review allowance; model use billed through the customer's provider account.    |
| Studio       | ₹4,999/month in India; \$99 elsewhere | Five seats, team permissions and pooled project/review limits; BYOK model use; explicitly defined support response expectations. |
| Enterprise   | Quoted after scoping                  | Deployment, support and security requirements that have actually been implemented and verified.                                  |

Five India Maker subscriptions cost ₹4,995. Studio is only ₹4 higher, or ₹999.80 per seat. That is not automatically unprofitable, but it leaves almost no extra revenue per seat for priority support if compute and service entitlements are identical. Use pooled limits and measured support costs; do not promise unlimited projects, storage, concurrency or human assistance.

The current local-asset route still sends coding or judging work to cloud providers. Describe it as **local asset generation with cloud coding/review**, including the data sent by each lane. Fully offline enterprise deployment requires validated local alternatives for every required lane and an installation tested with external access blocked. A local orchestrator alone cannot support a “data never leaves the building” promise.

### Managed credits are a later separately metered service

Before offering one gateway, verify each provider's terms for the intended customer-facing integration and define the billing unit. Record provider, model/version, job parameters, estimated maximum charge and customer authorization. Credits must map to a published charging schedule; similarly named credits from different vendors are not interchangeable units.

| **Job outcome**                                                   | **Customer ledger treatment**                                                                                                                                                         |
|-------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Job not submitted or rejected before acceptance                   | Release the reservation; no usage charge.                                                                                                                                             |
| Successful output delivered within the authorized scope           | Settle the disclosed actual usage charge and release unused reservation.                                                                                                              |
| Platform fault prevents delivery                                  | Restore the customer's affected usage credits; any unrecoverable upstream charge is Forge's cost.                                                                                     |
| Provider fails without a usable output                            | Default to restoring affected customer credits; price the unrecovered vendor-cost risk into the service. Any alternative needs explicit pre-purchase terms and applicable-law review. |
| Technically valid output rejected for taste or creative direction | The disclosed completed-generation charge remains; regeneration is a new metered attempt within the remaining authorized budget.                                                      |
| Provider status is unknown after a timeout                        | Reconcile the existing job identifier before resubmitting; avoid duplicate jobs and duplicate charges.                                                                                |

Reserve credits atomically before dispatching parallel jobs, and settle against actual provider outcomes. Account-wide and daily limits must include running reservations, retries and fallback routes. Switching providers or increasing an estimated maximum must stay within the customer's authorized settings. An exhausted budget pauses work with the evidence and remaining options visible.

Publish credit expiry, cancellation and refund terms, any minimum top-up, export access and what happens to unused balances. Maintain a balance ledger and outstanding service obligations. Prepayment reduces collection risk; it does not make provider spending, refunds or support free.

Use this internal equation:

**Customer contribution = revenue excluding tax − provider usage − payment fees − refunds/chargebacks − variable hosting/storage − variable support.**

Measure average and expensive-case project costs, including failed attempts, before choosing any included allowance. Temporary free vendor credits must not be the basis for a sustainable price. As published when checked, Razorpay's standard rate is 2% plus GST on its fee, while Paddle advertises 5% plus \$0.50 per checkout transaction. Actual contracts and tax treatment control the forecast. \[S16-S17\]

Fifty Maker subscribers at ₹999 produce **₹49,950 gross monthly billings**, equivalent to **₹5,99,400 annualized run-rate** if that customer count and price persist. At \$19, fifty subscribers produce \$950/month or \$11,400 annualized. These are arithmetic scenarios, not first-year revenue forecasts or profit. Track retained paying customers and renewals, not just first payments.

# Measurement and stage decisions

## Validate engagement with defined cohorts and enough observation

The five-player pilot evaluates comprehension, touch controls, fairness of the reveal, enjoyment of the land cut and voluntary rematches. Observe what players do before explaining the intended strategy. Record confusion, losses they cannot explain, rounds abandoned and whether they independently choose another match. This is qualitative evidence; five enthusiastic people cannot establish market retention.

The closed test with 20-50 recruited players is primarily for reliability, onboarding and device coverage. Keep feedback from friends, colleagues and development contributors identifiable in analysis so it is not silently treated as representative acquisition data.

### Metric definitions

Store event time consistently and define retention using elapsed windows from a player's first valid game session. D1 is a return in hours 24-48; D7 in hours 168-192; D30 in hours 720-744. Report only players whose complete observation window has elapsed. “Return” means a real foreground game session, not a background notification or automated launch. If a different calendar-day definition is adopted, label it and do not mix definitions in comparisons.

| **Metric**                | **Proposed working gate and interpretation**                                                                                                                              |
|---------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| D1 retention              | 35% proposed target; report cohort size, count and uncertainty.                                                                                                           |
| D7 retention              | 12% proposed target; diagnose onboarding and repeat-play barriers before adding large content scope.                                                                      |
| D30 retention             | 5% proposed version-2 target; report mature cohorts and compare with the previous experience.                                                                             |
| Matches per active player | Three per active day is a hypothesis; report distribution as well as average, excluding bots.                                                                             |
| Match completion          | **90% proposed initial target** for human-started matches, split by mode and reason. The old 70% is a diagnostic warning level rather than an acceptable commercial gate. |
| Crash-free sessions       | 99% remains a provisional minimum, with the denominator defined and critical failures reviewed separately.                                                                |
| Season completion         | 30% of the specified season-eligible player cohort; define eligibility before measurement.                                                                                |
| Pass conversion           | 2% is a hypothesis; state whether the denominator is eligible active users or users shown the offer.                                                                      |

Bots, simulator accounts, automated test clients and internal development sessions never count as retained or paying users. Report human-versus-human and human-versus-bot match completion separately. A real player using bot mode can count as a retained person; the bot itself cannot. Shared-phone sessions cannot be converted into multiple unique accounts without evidence.

For completion, count a match that reaches a normal terminal result under the rules, including a valid early victory. Report voluntary forfeits, lock-timeout forfeits and technical aborts separately; awarding a server-side winner after a disconnect does not make that a cleanly completed player experience. The crash-free-session denominator includes every instrumented real-user session in the stated cohort, including sessions that crash before a match starts.

Instrument the small set of events necessary for these decisions: valid session, tutorial completion, match start, lock, resolution, land-cut completion, match end with reason, reconnect outcome, purchase state and ad reward state. Keep analytics identifiers and event collection within the approved privacy design. Child-user measurement requires its own applicable-law assessment; do not assume consent permits every form of behavioral tracking.

### Sample size and decision rules

If six of fifty users return on D7, observed retention is 12%, but its approximate 95% Wilson interval is **5.6-23.8%**. At 60/500 the interval is approximately 9.4-15.1%; at 120/1,000 it is 10.1-14.2%. These are calculations assuming independent observations. Larger samples do not remove recruitment bias or compensate for unreliable instrumentation.

Use 500-1,000 new users as a planning range for a more informative initial retention read, adjusted to the actual decision and attainable traffic. It is not a mandatory industry standard. A four-week soft launch cannot complete D30 observation, and later recruits require additional time. Record an “insufficient evidence” outcome when arrivals or mature cohorts are too small.

Replace the automatic “stop after two patches” rule with two bounded experiments. Each has a stated hypothesis, one principal change, a recruiting/observation plan and a cash/hour limit. Compare consistent mature cohorts and check unintended regressions. Stop further expansion if the evidence remains weak after those experiments and the budget is exhausted; do not confuse lack of traffic with a proven retention failure.

For Forge, ten outside projects must identify distinct target users and how much assistance each required. Measure installation success, time to first playable build, manual interventions, accepted-output cost, defects discovered after merging, support minutes and repeat projects. Report unaided-ticket percentages by ticket complexity so splitting easy work does not inflate success. Managed billing follows repeat usage and observed willingness to pay, not a raw project count alone.

# Governance and release requirements

## Governance and compliance before the first affected use

Assign an owner and completion evidence to each requirement. A policy document or “legal review passed” checkbox is insufficient if the corresponding product flow does not work. Conversely, do not burden the offline pilot with services it does not use. Obtain the permissions needed for test participation and any recording, minimize tester data, and retain asset/software rights from the first prototype onward.

### India online gaming and tournament design

The Promotion and Regulation of Online Gaming Act, 2025 and final Rules, 2026 are effective from **1 May 2026**. Rule 20 requires a functional grievance mechanism for an online social-game or e-sport provider. Put the contact route, intake process, responsible operator and outcome records in version 1 rather than waiting for clans or photo avatars. \[S18-S19\]

The rules do not impose automatic blanket registration on every free social game. Rules 8 and 12 specify determination and registration triggers, including intended e-sport status, notified categories and authority action. Before each public release, check applicable notifications and the game's actual features and financial model. Do not market it as officially determined or registered unless that is true. \[S19\]

The Act distinguishes social-game access fees from money or other stakes associated with expected winnings, and its e-sport definition has recognition and registration conditions. Google Play separately restricts ordinary apps taking money or purchased items for an opportunity to win a prize of real-world value. A tournament label or virtual currency does not by itself resolve either assessment. Retain free entry and non-transferable cosmetic recognition until any alternative design receives the relevant review. This is a design constraint, not a conclusion that all tournament passes are unlawful. \[S20-S21\]

### Audience children photos and data

Decide the intended age groups and supported child experience before selecting authentication, advertising and analytics SDKs. The plan does not assume an adults-only audience. Store declarations must match the actual content and marketing; an age label cannot substitute for the required implementation.

DPDP implementation is phased. The November 2025 commencement notification places substantive obligations, including sections 3-17 and child-data obligations, in an 18-month phase: **May 2027**. Corresponding rules on notice, security, rights and verifiable parental consent are similarly phased. As of 5 October 2026, it is inaccurate to claim every substantive requirement is already operative. Design against the applicable release-date requirements and refresh the position before launch. Other currently applicable obligations are not suspended by this transition. \[S22-S23\]

When applicable, the Act's child threshold is under 18, and parental-consent obligations concern covered personal data generally, not only photo avatars. It also restricts tracking/behavioral monitoring and targeted advertising directed at children, subject to applicable exemptions. Implement the necessary age/parental flows before affected data processing; do not defer them automatically to version 2. \[S24\]

Where children are part of the Google Play target audience, Families requirements apply to the child experience. Ads shown to children or users of unknown age require appropriate self-certified SDKs, non-interest-based advertising and compliant content/formats. Check mediation adapters and other SDK behavior, not only the primary provider's name. Rewarded ads outside matches still need those checks. \[S25\]

For photo avatars, default to stock choices. If enabled, document the raw photo, conversion intermediates, avatar and thumbnail separately: purpose, device/server/vendor destinations, retention and deletion. On-device conversion reduces exposure, but a recognizable cartoon avatar may remain personal data. Test consent withdrawal, deletion, reporting and misuse handling across caches, profiles and shared surfaces before release.

Forge needs the same accurate data map for designs, code, screenshots, videos, logs, telemetry and support uploads. Telemetry remains off by default. Explain provider transmission separately from optional product analytics. Published privacy statements must describe the implementation actually shipped.

### Store deletion and purchase readiness

| **Requirement**              | **Completion evidence**                                                                                                                                                                                                                                                    |
|------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Play closed-test eligibility | If the developer uses a personal account created after 13 November 2023, at least 12 testers remain opted in continuously for 14 days, followed by the production-access application. Twenty invitations or fourteen calendar days alone do not prove eligibility. \[S26\] |
| Account/data deletion        | If account creation is offered, provide a discoverable in-app path and an external web resource for deletion requests. Verify associated-data deletion and explain justified retained records; clearing local saves is insufficient. \[S27\]                               |
| Purchases                    | Verify completed purchases before granting benefits; handle pending states, duplicate callbacks, restore/reinstall and revocations. Acknowledge ordinary one-time purchases within three days to avoid automatic refunds and entitlement revocation. \[S28\]               |
| Digital products             | Use the selected compliant store-billing arrangement for cosmetics, passes and any valid remove-ads product; reassess before using an alternative billing program. \[S29\]                                                                                                 |
| Privacy and support          | Store Data safety declarations reflect actual SDKs and data flows; support, reporting and deletion links work on the release build and website.                                                                                                                            |

Recheck store rules, SDK behavior, vendor terms and legal applicability before the first affected use, not only before Forge begins charging. Record the reviewed version/date and the product evidence. Later additions-photos, clans, replay sharing, tournaments, new countries and offline enterprise promises-each reopen only the requirements they actually change.

# Operating procedures and decision records

## Release and rollback procedure

A release candidate is one immutable combination of source commit, rules and economy versions, provider-independent accepted assets, dependency manifest, package identifier, build version and test evidence. The owner approves that combination. A later source or asset change creates a new candidate and invalidates approvals affected by the change. Signing or uploading a different file because it has the same filename is not permitted.

Before signing, the release record contains the current scope, known issues, device results, purchase/ad test results when relevant, data declarations, provenance exceptions, support contact and rollback method. The integration worker produces an unsigned or development-signed artifact. The trusted signing step receives only the approved artifact and release metadata. The release authority chooses the store track and rollout; a routine Forge task cannot publish by itself.

Start with the smallest audience that can answer the release question. Observe crash and match-completion signals, purchase errors and server health against the previous release. Expand only after the observation window and stop conditions defined for that rollout. Do not prescribe a universal rollout percentage without considering the number of actual users; ten percent of a very small audience is not an informative test.

Rollback has two distinct meanings. A backend or configuration rollback restores a compatible previous service/configuration for new work while preserving committed results. A client rollback may require another store release and cannot assume all devices immediately revert. Maintain a compatible server window for supported client versions and a controlled minimum-version policy. A reversible configuration can disable a failing weapon, ad placement or optional feature in new matches without altering an active match snapshot.

Economy and purchase ledgers are append-only records with corrective transactions. Never restore a database backup in a way that silently loses paid entitlements or duplicates a previously granted reward. Backups, provider records and settlement reconciliation must be part of the recovery procedure. Keep migration scripts, rollback constraints and the last successful restore drill with the release record.

## Incidents and support

| **Incident**                           | **Immediate action**                                                                     | **Closure evidence**                                                                        |
|----------------------------------------|------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------|
| Wrong match result or land corruption  | Stop affected mode or rules version for new matches; preserve inputs and hashes          | Reproduced case, corrected resolver, replay regression and controlled compensation decision |
| Repeated crash or phone freeze         | Stop rollout; identify device/build cohort; disable the affected optional path when safe | Reproduction, physical-device fix and recovery in affected cohort                           |
| Purchase grant failure                 | Preserve purchase token securely; reconcile provider state and entitlement ledger        | Correct grant or explained refund path, duplicate protection and customer response          |
| Duplicate ad or progression reward     | Disable the faulty grant path; retain audit evidence                                     | Idempotent repair, reconciliation and abuse impact assessment                               |
| Secret exposure or unauthorised access | Revoke affected credentials/sessions and contain the exposed service                     | Scope assessment, remediation, applicable notifications and verified access controls        |
| Provider outage or unknown job status  | Pause new submissions to that route; reconcile existing job identifiers                  | Settled ledger, recovered artifacts or defined credit adjustment                            |
| Unexpected spending                    | Stop new dispatch through the independent limit control                                  | Reservation/usage reconciliation, cause and revised adapter or job policy                   |
| Photo or clan misuse in later versions | Restrict reported content through the documented moderation process                      | Reviewed evidence, action record, appeal route and deletion/retention handling              |

The owner reviews new incidents daily while a public service is active. A support promise requires coverage during absence and a clear route for severe issues. A part-time operator must either cap the audience and service scope, arrange backup coverage, or avoid promising immediate support. Written policies identify response expectations and escalation roles without claiming a response time that the business cannot staff.

## Live operations cadence

Check game crashes, failed matches, server health, purchase reconciliation, provider spending and critical reports each day. Review balance, engagement, support themes and accepted Forge outputs weekly. Separate balance diagnosis from balance deployment: a weekly report does not require a weekly rule change.

V2 uses an 18-day season only after the full season lifecycle works in a test environment. Freeze competitive balance for the season, publish dates and rewards, and validate reset, late purchase, claim and rollback cases. Emergency corrections are documented and communicated. Do not change a rule or card every season merely to satisfy a calendar.

New weapons, arenas and cosmetic sets ship when they pass their content gate. The original monthly-content cadence becomes a capacity planning target. Existing players should receive stable, comprehensible matches even if a new item takes longer. Track support and content maintenance hours alongside new development; an 18-day season plus monthly content is work that must fit within the same operating budget.

## Data and artifact retention defaults

The following are proposed operational defaults for implementation. They require review against actual product data, provider retention, deletion obligations and any applicable legal retention requirements. They are not statutory retention periods.

| **Record**                             | **Proposed default**                                                                                                | **Required handling**                                                                          |
|----------------------------------------|---------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------|
| Local pilot feedback                   | Notes without unnecessary identifiers; retain through the pilot decision and one follow-up cycle                    | Tester recording is optional and separately consented; delete when no longer needed            |
| Game diagnostic match records          | 30 days, with a specific support-case hold where justified                                                          | Access restricted; user-facing replay sharing is a separate V2 purpose                         |
| Raw optional product analytics         | 90 days when permitted; retain aggregate decision reports longer                                                    | Respect audience/consent rules; do not collect a raw event just because the schema supports it |
| Forge failed candidate videos and logs | 14 days locally by default                                                                                          | Owner can export before deletion; preserve evidence attached to an unresolved defect           |
| Forge accepted source and provenance   | Retain with the project until owner deletion                                                                        | Export complete source and rights metadata; backups follow the published deletion process      |
| Purchase and financial records         | Period chosen with applicable accounting, tax and fraud requirements                                                | Keep only justified fields and disclose any retention after account deletion                   |
| Photo conversion inputs                | Stock avatars by default; if photos are enabled, discard raw/intermediate data after the documented conversion need | Verify caches, failed conversion paths and downstream deletion                                 |

Deletion distinguishes active data, copies in backups and provider-held copies. The product describes the real process and completion window. A successful local deletion command does not prove that every external copy has gone. Deletion test cases cover an absent app, an expired session, a partially completed request and a provider outage.

## Initial paid Forge entitlement experiment

Maker and Studio subscriptions buy defined workflow services. The table below is a proposed initial test configuration, not a validated cost envelope. Activate it only after the measured delivery cost, customer feedback and provider arrangements justify the price. Neither tier includes pooled Claude Code usage or an unspecified model credit allowance.

| **Entitlement**               | **Maker candidate**                                       | **Studio candidate**                                                                                                              |
|-------------------------------|-----------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------|
| Seats                         | 1                                                         | 5                                                                                                                                 |
| Active hosted projects        | 3                                                         | 10 pooled                                                                                                                         |
| Hosted artifact storage       | 5 GB total                                                | 25 GB pooled                                                                                                                      |
| Uploaded build-review bundles | 10 per billing month, maximum 250 MB each                 | 50 pooled per billing month, maximum 250 MB each                                                                                  |
| Hosted review retention       | 30 days within storage limit                              | 90 days within storage limit                                                                                                      |
| Customer worker concurrency   | 1 active workflow                                         | Up to 3 active workflows, subject to actual workers and budgets                                                                   |
| Inference usage               | Customer's own approved account or API billing            | Customer's own approved account or API billing                                                                                    |
| Managed model credits         | None included; separately metered when qualified          | None included; separately metered when qualified                                                                                  |
| Human support                 | Asynchronous setup/bug intake; no guaranteed response SLA | Proposed two-business-day initial response for up to two supported-workflow incidents per month, only after staffing is confirmed |
| Export                        | Complete project and accepted artifacts remain exportable | Same, with team permissions and audit history                                                                                     |

An upload can fail the bundle limit without deleting the local result. The interface shows storage use and expiry before upload, offers pruning or export, and never automatically adds a charge. Renewals reset monthly upload counts according to disclosed billing rules, while retained bytes continue to count against storage. Limits remain visible to customers; raising them is a product and cost decision.

Run at least one cost exercise using an ordinary customer project and one expensive valid project before charging. Include onboarding minutes, failed-provider costs, hosted video traffic, refunds and support. If the Studio service cannot cover its extra support burden at the proposed price, change its limits, price or promise before selling it. Do not rely on a future managed-credit margin to subsidise an underpriced current subscription.

## Risk and dependency register

| **Risk or dependency**                                 | **First affected stage**          | **Response and decision owner**                                                                         |
|--------------------------------------------------------|-----------------------------------|---------------------------------------------------------------------------------------------------------|
| Duel or land cut is hard to understand                 | Pilot                             | Owner observes play and revises the specific confusing interaction before more content                  |
| Complex status combinations dominate                   | V1 rules                          | Technical reviewer requires interaction fixtures and stratified balance review                          |
| Finger cuts often yield zero land                      | Pilot                             | Measure failed gestures and completion time; improve preview/snapping or simplify card shapes           |
| Match takes too long                                   | Pilot                             | Measure selection, handoff, animation and cut time separately; tune within a new rules version          |
| Lowest target phone misses budgets                     | Pilot and art gate                | Reduce scene cost or revise supported-device promise explicitly                                         |
| Model does not fit available GPU                       | First model use                   | Capability preflight selects a verified allowed route or suitable rental; no blind retries              |
| Asset output cannot be distributed in a target country | First generation/public demo      | Owner uses a permitted route or obtains documented rights before production assets accumulate           |
| Agents can change their own gates or read secrets      | Forge R1                          | Separate policy, credential and execution boundaries; retain supervised mode until verified             |
| Provider timeout causes duplicate work or charges      | Forge R1                          | Stable job IDs, reservation reconciliation and bounded transport retries                                |
| Shared scenes or settings conflict                     | Forge R1 and parallel work        | Single integration writer and path ownership; recheck the merged candidate                              |
| Insufficient mature retention data                     | Soft launch                       | Extend a bounded observation/recruiting window or record inconclusive evidence                          |
| Game operations consume development time               | V1 onwards                        | Deduct actual support and incident hours from the next milestone's capacity                             |
| Forge demand differs from the owner's needs            | Before R4 expansion               | Observe distinct outside users and a second supported-template brief                                    |
| Provider terms or prices change                        | First use and ongoing adapters    | Version/date record; pause affected new jobs; requalify permitted alternative                           |
| Financial ledger does not match entitlements           | V1 paid products or Forge billing | Reconcile before growth; preserve append-only corrections and customer remedies                         |
| V3 or V4 requires more team than revenue supports      | Before those stages               | Separate funded scope with named specialists; retain the viable earlier game if funding is insufficient |

## Preflight evidence still to collect

The plan is complete enough to start bounded setup work. These external facts remain unverified and must be collected as the first relevant task, rather than filled with invented answers.

Record the actual laptop OS, CPU, RAM, free disk, GPU and free VRAM; a compatible physical Android test device; the Unity licence and selected editor/toolchain versions; provider API/CLI entitlement and billing owner; any eligible promotional credits with their expiry and permitted services; a usable GPU region and quota if renting; intended first-release audience and countries; and the cash/hour allowance for the initial milestone. Each entry has a responsible owner and evidence link.

An unavailable GPU does not block a placeholder pilot. An absent phone does block a phone acceptance gate. An unresolved photo workflow does not block a stock-avatar V1. An unverified managed-credit agreement does not block a customer-owned API integration. This dependency-based approach keeps optional expansion work from delaying a valid smaller milestone.

# Correction register

All 36 review points are incorporated below. “Incorporated” means the planning text and required work were corrected; implementation and validation remain the tasks and gates specified in the plan.

| **Point** | **Correction adopted**                                                                                                 | **Where it is implemented in this plan**       |
|-----------|------------------------------------------------------------------------------------------------------------------------|------------------------------------------------|
| 1         | Retain the valid five-element counter cycle, pure C# core and correct 72-ticket V1 count                               | Game rules and V1 delivery register            |
| 2         | Two-player pilot/V1; kingdoms in V2; no four-player V1 promise; replay sharing stays V2                                | Product scope and player journey               |
| 3         | Define up to three volleys, HP-based winner, simultaneous outcomes and draws                                           | Duel lifecycle and resolution order            |
| 4         | Five starters; up to six unique equipped weapons; symmetric online catalog; explicit reserve                           | Loadout, catalog and progression rules         |
| 5         | Numeric projectile, mass, damage and counter definitions; Thunder Crown is an explicit total multiplier                | Weapon registry and projectile contract        |
| 6         | Ordered shield, cover, clash, hit, dodge, damage, heal and status handling                                             | Resolution sequence and acceptance cases       |
| 7         | Common hidden-choice reveal; Brahmastra separately specified and private-experiment gated                              | Information visibility and special astra rules |
| 8         | Total-board denominator and final HP margin; quota is maximum, actual transfer is measured                             | Land quota formula and examples                |
| 9         | Area accounting, legal connected capture and timeout behavior; corrected 90 percent reasoning                          | Logical board and land-cut algorithm           |
| 10        | Five terrain categories including Plain, with defined trigger and reset behavior                                       | Terrain allocation and effects                 |
| 11        | Separate online and sequential shared-phone durations; measure full-loop time                                          | Timing contract and pilot observations         |
| 12        | Authoritative match service, private locks, deadlines, reconnect and idempotent rewards                                | Game architecture and online tickets           |
| 13        | Stratified simulations, confidence and combination coverage; no aggregate win-rate guarantee                           | Simulator plan and balance gates               |
| 14        | Persistent/world and army rules are gated; earned resources do not automatically establish fairness                    | V2 to V4 roadmap and live operations           |
| 15        | Physical phone gate from Forge R1 and pilot; emulator remains optional                                                 | Device gates and Forge test lane               |
| 16        | Defined frame-time, PSS memory, download and completion metrics                                                        | Performance budgets and release evidence       |
| 17        | Defined retention cohorts, sample limitations and bounded hypothesis-led experiments                                   | Measurement and funding decisions              |
| 18        | Rewarded-only default; conditional remove-ads; net revenue and contribution accounting                                 | Monetisation and financial model               |
| 19        | Supported-template automation with documented intervention and support boundaries                                      | Forge product scope and workflow               |
| 20        | Separate local assets, rented workers, cloud inference and future fully local deployment                               | Forge modes and data flow policy               |
| 21        | Correct Hunyuan, HY-Motion and TRELLIS requirements; measured configurations                                           | Model routing and capability checks            |
| 22        | Per-GPU scheduling, 16 GB T4 limitations and unverified credits/quotas                                                 | Forge scheduling and preflight                 |
| 23        | Exclude default GVHMR commercial use; assess Tencent output territory and dependencies before first use                | Asset rights gates and provenance              |
| 24        | Separate user-owned unmodified CLI access from Forge-managed commercial API agents                                     | Provider adapters and commercial boundaries    |
| 25        | Immutable root task lineage, three total candidates, cumulative reservations and timeout reconciliation                | Forge task state machine and ledger            |
| 26        | Technical, visual, integration and release approvals have distinct evidence                                            | Review workflow and release procedure          |
| 27        | Enforced worker boundary, secret broker and separate signer                                                            | Forge security and incident handling           |
| 28        | Pinned toolchain, complete Unity metadata, separate checkouts and single integration writer                            | Workspace and build reproducibility            |
| 29        | Complete geometry/material/rig/motion/audio/provenance contracts; manual work labelled                                 | Asset pipeline and qualification               |
| 30        | BYOK test prices, explicit trial entitlements, measured support cost and separate managed credits                      | Forge commercial scope and unit economics      |
| 31        | Correct duration sums; shared capacity and dependencies; no automatic two-year commitment                              | Joint delivery roadmap                         |
| 32        | Correct cash totals and open-ended V4 cost; shared-cost allocation and recurring costs                                 | Finance and funding gates                      |
| 33        | Current Indian gaming-law stage, grievance process and conditional tournament/registration review                      | Governance before public online release        |
| 34        | Phased DPDP timing, audience decisions and child-data handling before affected use                                     | Privacy, audience and later photo gates        |
| 35        | Account-specific Play test requirement, external deletion route and verified purchase lifecycle                        | Store and release readiness                    |
| 36        | Minimal loop then pilot, qualified asset/online expansion, outside-user proof and paid features before managed credits | Adopted sequence and stage decisions           |

The next authorised planning step is the initial preflight and rules-baseline milestone. It creates a concrete compatibility report, accepted pilot task set and first bounded spending configuration. Each subsequent stage begins only when its dependency evidence exists and its scope fits the available capacity.

# Sources and verification basis

Official sources were checked on 5 October 2026. References support the platform, model, pricing and legal facts specifically attributed to them. Proposed gameplay values, workflow controls, staffing assumptions, commercial limits and release targets are decisions for this plan; they are not vendor guarantees. Arithmetic uses the supplied plan and stated assumptions. Recheck changing terms at first use and before an affected release.

**\[S01\] Claude Code data flows.**

<https://code.claude.com/docs/en/data-usage>

**\[S02\] Claude Code product embedding and authentication terms.**

<https://code.claude.com/docs/en/legal-and-compliance>

**\[S03\] Git worktrees.**

<https://git-scm.com/docs/git-worktree>

**\[S04\] Unity asset metadata.**

<https://docs.unity3d.com/6000.0/Documentation/Manual/AssetMetadata.html>

**\[S05\] Claude Code sandbox scope.**

<https://code.claude.com/docs/en/sandboxing>

**\[S06\] Android app signing and upload keys.**

<https://developer.android.com/studio/publish/app-signing>

**\[S07\] Hunyuan3D 2 point 1 hardware requirements.**

<https://raw.githubusercontent.com/Tencent-Hunyuan/Hunyuan3D-2.1/main/README.md>

**\[S08\] Original TRELLIS requirements.**

<https://github.com/microsoft/TRELLIS>

**\[S09\] TRELLIS 2 requirements and licences.**

<https://huggingface.co/microsoft/TRELLIS.2-4B>

**\[S10\] HY Motion 1 point 0 requirements and limitations.**

<https://huggingface.co/tencent/HY-Motion-1.0/raw/main/README.md>

**\[S11\] GVHMR licence.**

<https://github.com/zju3dv/GVHMR/blob/main/LICENSE>

**\[S12\] NVIDIA T4 specifications.**

<https://www.nvidia.com/en-us/data-center/tesla-t4/>

**\[S13\] Hunyuan3D output and territory restrictions.**

<https://raw.githubusercontent.com/Tencent-Hunyuan/Hunyuan3D-2.1/main/LICENSE>

**\[S14\] HY Motion output and territory restrictions.**

<https://huggingface.co/tencent/HY-Motion-1.0/blob/main/LICENSE.txt>

**\[S15\] Unity Android emulator compatibility.**

<https://docs.unity3d.com/6000.3/Documentation/Manual/android-requirements-and-compatibility.html>

**\[S16\] Razorpay published processing pricing.**

<https://razorpay.com/pricing/>

**\[S17\] Paddle published merchant of record pricing.**

<https://www.paddle.com/pricing>

**\[S18\] Online Gaming Act commencement notification.**

<https://www.meity.gov.in/static/uploads/2026/04/089ca9904b13f019b41a391584ab10ea.pdf>

**\[S19\] Final Online Gaming Rules 2026.**

<https://www.meity.gov.in/static/uploads/2026/04/7e0b02d37fd07f81fa48578a9996aa85.pdf>

**\[S20\] Online Gaming Act definitions and conditions.**

<https://www.meity.gov.in/static/uploads/2025/10/8a7f103cefc68ed8aaa2ebc9a2ed7c13.pdf>

**\[S21\] Google Play real money games and tournament policy.**

<https://support.google.com/googleplay/android-developer/answer/9877032>

**\[S22\] DPDP Act phased commencement.**

<https://www.meity.gov.in/static/uploads/2025/11/c56ceae6c383460ca69577428d36828b.pdf>

**\[S23\] Final DPDP Rules and commencement phases.**

<https://www.meity.gov.in/static/uploads/2025/11/53450e6e5dc0bfa85ebd78686cadad39.pdf>

**\[S24\] DPDP Act and child data provisions.**

<https://www.meity.gov.in/static/uploads/2024/06/2bf1f0e9f04e6fb4f8fef35e82c42aa5.pdf>

**\[S25\] Google Play Families policies.**

<https://support.google.com/googleplay/android-developer/answer/9893335?hl=en>

**\[S26\] Google Play testing requirements for new personal accounts.**

<https://support.google.com/googleplay/android-developer/answer/14151465?hl=en>

**\[S27\] Google Play account deletion requirements.**

<https://support.google.com/googleplay/android-developer/answer/13327111?hl=en>

**\[S28\] Android purchase processing and acknowledgment.**

<https://developer.android.com/google/play/billing/integrate>

**\[S29\] Google Play Payments policy.**

<https://support.google.com/googleplay/android-developer/answer/9858738?hl=en>

**\[S30\] Unity Android dependency versions.**

<https://docs.unity3d.com/6000.3/Documentation/Manual/android-supported-dependency-versions.html>

**\[S31\] Unity target device profiling.**

<https://docs.unity3d.com/6000.0/Documentation/Manual/profiling-target-device.html>

**\[S32\] Azure NCasT4 v3 specifications.**

<https://learn.microsoft.com/en-us/azure/virtual-machines/sizes/gpu-accelerated/ncast4v3-series>

**\[S33\] Android purchase verification and abuse prevention.**

<https://developer.android.com/google/play/billing/security>

**Source plan.** The owner supplied the revised Astra Kingdoms and Game Forge complete plans dated 5 October 2026 and the accepted 36-point review. This document consolidates the corrections and adds explicit implementation defaults. No game build, GPU benchmark, user-retention experiment or vendor-account verification was performed as part of preparing the document.
