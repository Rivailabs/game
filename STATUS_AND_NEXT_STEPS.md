# Status and next steps

Where Astra Kingdoms and Game Forge stand against `docs/PLAN.md`, what is left, and what to do next, in order.

**Short version.** Most of the plan now exists as tested code. None of it has been opened in Unity, run on a phone, played by people, or connected to a real outside service. Those steps need you, your hardware and your accounts. The plan's gates require them before any stage counts as passed.

---

## 1. How to run what exists

| What | Command | Needs |
|---|---|---|
| All game tests | `cd astra-kingdoms && dotnet test` | .NET 8 SDK |
| Bot balance simulator | `cd astra-kingdoms && dotnet run --project tools/AstraKingdoms.Sim -- --help` | .NET 8 |
| Economy simulator (V4) | `dotnet run --project tools/AstraKingdoms.EconomySim` | .NET 8 |
| Online game server (dev) | `cd astra-kingdoms/server/AstraKingdoms.Server && ASPNETCORE_ENVIRONMENT=Development dotnet run` | .NET 8; see `server/RUNBOOK.md` |
| Server load test | `dotnet run --project tools/AstraKingdoms.LoadTest -- --help` | .NET 8 |
| Unity client | Open `astra-kingdoms/unity/` in Unity 6 LTS | Unity 6, Android module |
| Forge | `cd game-forge && pip install -e ".[claude,test]" && forge --help` | Python 3.11+ |
| Forge tests | `cd game-forge && python -m pytest -q` (add `-k "not dotnet"` to skip the slow end-to-end ones) | Python, pytest, .NET 8 for the e2e ones |
| Record your machine | `forge preflight` | — |

---

## 2. What is done

"Done" means written, merged on branch `claude/adoring-clarke-x42v5y` (PR #1), and passing its automated tests. It does **not** mean checked in Unity, on a phone, or with real users.

### Astra Kingdoms

| Plan item | State | Where |
|---|---|---|
| Rules AK-TR-1: board, seeded streams, land quota, card shapes, cuts, Auto Cut | Done, every plan acceptance case is a test | `src/AstraKingdoms.Rules/Land`, `Core` |
| Combat: fixed-point physics, dodges, clashes, statuses, terrain, 10-step volley order, Brahmastra flag | Done, every combat acceptance case is a test | `src/AstraKingdoms.Rules/Combat` |
| Match engine, private views, timeouts/forfeit/void, rules hash, replays | Done | `src/AstraKingdoms.Rules/Match`, `Replay` |
| Bots (3 levels), 10,000-match simulator, balance report (tickets 21-23, 25) | Done | `src/AstraKingdoms.Rules/Bots`, `tools/AstraKingdoms.Sim`, `reports/` |
| Balance bundles that go live for new matches only, rollback (ticket 24) | Done in the engine | `src/AstraKingdoms.Rules/Balance`, `Core/RulesParameters.cs` |
| Pilot tickets 4-10 and V1 tickets 26-48: Unity scene builder, Android build entry, screens, HUD, effects, arenas, land screens, tutorial, settings, localization, autoplay | Code done; compiled only against hand-written Unity stand-ins | `unity/` |
| V1 tickets 49-56: online server, Firebase token check, friend rooms, queue + labelled bot, reconnection, rate limits, audit, health, Dockerfile, runbook | Done; load test correct but p95 latency 294 ms vs 250 ms target on a shared 4-core box | `server/`, `tools/AstraKingdoms.LoadTest` |
| V1 tickets 57-64: XP/levels, daily tasks, cosmetics, shop, Google Play purchase verification, rewarded ads, analytics, retention maths | Done as a library | `src/AstraKingdoms.Meta`, `unity/Assets/Scripts/Meta` |
| V3: four-player mode, tournaments, spectator feed, real-time prototype | Game logic done | `src/AstraKingdoms.Modes` |
| V4: conquest world, loss limits, offline defence, alliances, season close, economy simulator | Game logic done | `src/AstraKingdoms.World`, `tools/AstraKingdoms.EconomySim` |
| Privacy data map | Draft, needs legal review | `docs/privacy-data-map.md` |

### Game Forge

| Release | State | Where |
|---|---|---|
| R1: task state machine, budgets, isolated worktrees, protected checks, review page, sandbox, watchdog, backups, retention | Done | `forge/` |
| R2: asset contracts, 15 generation routes with licence/territory rules, GPU scheduler, Blender scripts, provenance, catalogue lane + 29 Astra briefs | Done with fakes; Blender scripts never run | `forge/assets`, `forge/lanes`, `briefs/` |
| R3: document intake, planner, traceability, `turn-duel-2p` template, release packaging, separate signer | Done | `forge/intake`, `planning`, `release`, `templates/` |
| R4: setup assistant, signed updates, uninstall, diagnostics, export, guides | Done | `forge/installer`, `docs/` |
| R5: hosted teams, BYOK, Razorpay/Paddle, credits ledger, unit economics | Built, **off by default** | `forge/hosted` |

---

## 3. Started but not merged (saved on separate branches)

Work was stopped on request partway through. Each piece is pushed to its own branch so nothing is lost. See section 7 for whether each one builds and passes tests.

| Branch | What it contains | What is missing |
|---|---|---|
| `wip/v2-kingdom-seasons-social` | V2 library: homeland plots, ranked seasons and rating, cosmetic pass, friends, clans, photo-avatar gates, replay export, V2 content extension points, plus tests | Unity V2 screens, server hosting, te/ta/mr/bn locale files |
| `wip/art-and-release-65-82` | Art briefs, cultural review checklist, placeholder icon/sound/music generators (tickets 65-72); release tool and docs for tickets 73-82; CI scripts | Not finished or reviewed; GitHub CI workflow not added |
| `wip/server-integration` | Progression, shop, purchases, ads, analytics and account deletion hosted inside the server | Online drawn cut and arrow playback, reserve-weapon picker, Firebase sign-in adapter, Unity prefab assembler for Forge, latency fix |

---

## 4. Still to build (code)

1. **Finish and merge the three branches above.**
2. **V2 Unity screens and server endpoints:** kingdom view and editing, ranked entry and leagues, pass track, friends, clans, reports, avatar picker, replay export preview.
3. **Online client gaps:** finger-drawn cut online (needs a server preview message), arrow playback online (needs a volley-result message), reserve-weapon picker in Full rooms.
4. **Firebase sign-in on the phone:** an adapter for the Firebase Unity SDK. Without it, release builds cannot sign in.
5. **Unity reads tuned balance values:** the client still uses fixed timers, HP, rounds and card caps; the server does not yet store or send balance files. Bundles that change only damage or heal values are already safe.
6. **Forge's Unity prefab script** (`Forge.Assets.PrefabAssembler.AssembleFromManifest`): Forge's asset stage reports BLOCKED until it exists.
7. **Release tickets 73-82:** finish performance, size and reproducibility tools, Data-safety form, store text check, closed-test tracker, rollout and handover runbooks.
8. **GitHub CI workflow** running `dotnet test` and `pytest` on every push.
9. **Server load:** re-run the 200-match test on real server hardware; if p95 is still over 250 ms, resolve matches on a bounded worker pool.

---

## 5. What only you can do

### Decisions (in `game-forge/projects/astra-kingdoms/project.toml`, which only you edit)

- [ ] **Package id.** The game is `com.rivailabs.astrakingdoms`; Forge's device check launches `com.astrakingdoms.game`. Change line `package = ...` to match, and consider `wait_s = 60`.
- [ ] **Renders leaving your machine.** Add `"render_image"` to `allowed_data_classes` if Claude may judge asset renders. Asset tasks stay blocked until you do.
- [ ] **Budget caps.** Confirm or change $150 project / $100 pilot / $45 per task.
- [ ] **Pilot ticket order.** Confirm the proposed dependencies in `tasks/pilot.toml`.
- [ ] **Asset vendors.** Choose generation routes and credentials in the `[assets]` section.

### Hardware and software

- [ ] Run `forge preflight` on your laptop (OS, RAM, GPU, disk).
- [ ] Install Unity 6 LTS with the Android module; pin the exact version in `astra-kingdoms/unity/ProjectSettings/ProjectVersion.txt`.
- [ ] Open `astra-kingdoms/unity/`, let Unity generate `.meta` files, and **commit them**.
- [ ] Fix whatever the real Unity compiler reports. The client was only checked against hand-written stand-ins; first suspects are listed in `astra-kingdoms/README.md`.
- [ ] Choose a 2 GB reference Android phone and a second phone; record model, chipset, OS and GPU.

### Accounts and services

- [ ] Firebase project (sign-in) and the server's project id.
- [ ] Google Play developer account, app entry, service account for purchase checks.
- [ ] Ad network (Families-certified if children may play), analytics and crash vendor.
- [ ] Hosting for the game server (Docker image in `server/Dockerfile`).
- [ ] Optional: Anthropic API key for Forge, Meshy/Tripo accounts, Razorpay or Paddle (only for the Forge paid tier).

### People and legal

- [ ] Five-person pilot playtest (plan: can they use controls, finish a cut, explain a loss, ask for a rematch?).
- [ ] Native-speaker review of Hindi and Kannada.
- [ ] Legal review of the privacy map, deletion process and grievance contact; publish real URLs (release builds refuse placeholders).
- [ ] Real art, music and sound, or approve placeholders for the pilot.
- [ ] Decide the audience (children or not) before choosing ad and analytics SDKs.

---

## 6. Recommended next steps, in order

1. **Merge PR #1** into `main` once you have looked through it.
2. **Make the five `project.toml` decisions** above.
3. **Open the Unity project, fix compile errors, commit `.meta` files.**
4. **Build to your phone** with the grey-box scene and play a shared-phone match. Run the autoplay mode (`-autoplay <seed>`) and collect the report for frame time and memory.
5. **Run the five-tester pilot.** Fix confusion in the duel or land cut through a new balance bundle or rules version, not silent code edits.
6. Only after the pilot passes: **finish the code in section 4**, then art and audio, then the closed test with 12 testers for 14 days.
7. **Soft launch** with the measurement plan (D1/D7/D30 as defined in the plan), then decide go/hold/stop before V2.

The plan's own rule: do not add art, online scope or monetization before the pilot shows people understand and enjoy the duel and land cut.

---

## 7. Test results at the time of writing

See the end of this file (filled in after the final test run).
