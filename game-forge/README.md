# Game Forge: Release 1 (reliable code/build/device loop)

Game Forge turns owner-approved, bounded development tasks into reviewable candidates.
It runs protected checks, integrates candidates one at a time, and records evidence, spend and
decisions in a durable store. This folder holds **Release 1 (R1)** as specified in
`docs/PLAN.md` ("Game Forge architecture and workflow", "Forge providers assets and safety",
"Forge releases and commercial offering"). Its first proving project is `../astra-kingdoms/`.

R1 does not make Forge autonomous. The owner approves every task, every visual change and
every gate change. A provider saying "done" never marks anything accepted. Only protected
checks, the integration writer's atomic promotion and recorded owner approvals do that.

## What R1 does

| Capability | Status in this code |
|---|---|
| Task contract (root task with every field the plan lists), candidate attempts, evidence, approvals with **distinct** technical / visual / integrated / release fields | Done (`forge/models.py`) |
| The plan's exact 16-state machine. Illegal transitions raise. Terminal states are immutable. A spec change creates a linked new root that shows the previous cost | Done (`forge/statemachine.py`, `Store.revise_root`) |
| Durable SQLite store (WAL), append-only event log enforced by triggers, correlation IDs, content-addressed sha256 artifact store | Done (`forge/store.py`, `forge/artifacts.py`) |
| Budget ledger: available / reserved / settled; atomic reservation (`BEGIN IMMEDIATE`) at root, milestone and project scope; unknown ceilings refused in unattended mode; "provider completion/charge pending"; owner kill switch; caps change only through explicit owner commands | Done (`forge/budget.py`) |
| Cash ledger (date, vendor, invoice, cash, fees, promo credit, normal-rate equivalent, milestone, allocation rule) that refuses double counting, and a founder-hours capacity ledger | Done |
| Scheduler: dependency readiness against pinned artifact hashes, stale consumers when a dependency is replaced, worker and resource leases (expiry + heartbeat, one per GPU/device/workspace), restart recovery that reconciles by idempotency key before any resubmission, bounded transport retries with backoff charged to the same root, attempt limit (3 = initial + 2 repairs; visual rejections count) | Done (`forge/orchestrator.py`, `forge/leases.py`) |
| A `git worktree` per attempt, rooted at the pinned accepted commit. A single integration writer stages on current accepted main, re-runs protected checks, then promotes with an atomic compare-and-swap (`git update-ref new old`) | Done (`forge/gitops.py`) |
| Protected checks live outside the worker's scope. Protected paths are restored to the accepted bytes before checks run. The diff guard flags deleted, skipped or weakened tests, threshold edits and disabled gates, and those candidates cannot auto-pass | Done (`forge/diffguard.py`) |
| Generic command check; `dotnet build` + `dotnet test` preset with TRX parsing (zero tests = INCOMPLETE, never PASS) | Done and exercised against the real `astra-kingdoms/` |
| Device service (adb by serial). Missing adb, no device, unauthorised device, emulator, missing build or no scenario report all give **INCOMPLETE** | Implemented. Tested with a scripted fake `adb` only: no phone here |
| Unity batch-mode Android build adapter. Editor path comes from the toolchain manifest; an absent editor gives **BLOCKED** | Implemented. Tested with a stub editor only: no Unity here |
| Provider interface that declares operations, auth, billing party, data destinations, data classes, quotas, timeouts, cancellation, usage reporting and tested versions. Includes a deterministic `FakeProvider` | Done |
| Claude API builder / independent reviewer adapter (`claude-opus-5-5` by default, role mapping configurable). Path-confined file tools, credential broker, redaction, price table → ledger | Implemented. Checked against the real `anthropic` 1.11 SDK and a local fake Messages endpoint. **Never run against the live API** (no key in this environment) |
| Official CLI connector (for example Claude Code) | **Supervised route only.** It prints the command for the owner to run, is refused in unattended mode and never touches tokens |
| Preflight → `toolchain-manifest.json` (OS, CPU, RAM, disk, GPU/VRAM via nvidia-smi, git/dotnet/Unity/adb/Blender, devices) with an honest `not_verified` list | Done |
| Local review UI on 127.0.0.1: dashboard, tasks by state, five-panel review page, approve / bounded repair / hold / cancel bound to an exact hash, budget page, kill switch. CSRF-protected, no external assets | Done (`forge/web/app.py`) |
| CLI `forge` | Done |
| Catalogue asset lane: Objaverse 1.0 + Objaverse++ index, CC-BY/CC0-only licence filter, reject list, Blender cleanup and renders, visual judge, credits, disk guard and download cap. Asset tasks run it before any generation | Implemented and tested with fakes only. See [Catalogue asset lane](#catalogue-asset-lane-find-a-free-model-before-generating-one) |
| Astra seed: `projects/astra-kingdoms/project.toml` and `tasks/pilot.toml` (pilot 1-10 and V1 11-82, text copied verbatim from the plan) | Done. Pilot dependencies are **inferred** (the plan does not list them per ticket), so confirm them before approving |

## Architecture

```
             owner (CLI `forge` / review UI on 127.0.0.1)
                 |  approve task / approve exact hash / repair / hold / cancel / kill switch
                 v
 +--------------------------- Orchestrator (forge/orchestrator.py) ----------------------------+
 |  readiness: deps@hash, policy, resources ──► READY ──► reserve budget (BEGIN IMMEDIATE)    |
 |  leases: attempt / device / gpu / workspace / integration      recovery check on restart   |
 +-----|--------------------------|---------------------------|-------------------------------+
       | CodingRequest            | candidate commit          | verified + approved candidate
       v                          v                           v
 Provider adapters          Verification                 Integration writer (single)
 (forge/providers)          - diff guard (scope,         - stage on CURRENT accepted main
 - FakeProvider               gate weakening)            - re-run protected checks
 - ClaudeAPIProvider        - restore protected paths    - Unity build / device (if any)
   (path-confined tools,    - protected checks           - CAS promote: update-ref new old
    broker-held key)          (dotnet, command, ...)       (BaseMoved -> restage / re-approve)
 - OfficialCLI (supervised) - independent reviewer
       |                          |                           |
       v                          v                           v
 git worktree per attempt   Evidence (rules, replay, integration, device, performance, human,
 (forge/gitops.py)          static, model_review) + logs in sha256 artifact store
                                  |
                                  v
            SQLite (WAL): roots, attempts, evidence, approvals, provider_jobs, leases,
            reservations, usage, cash ledger, founder hours, append-only events
```

Life of a task: `DRAFT → APPROVED → READY → RUNNING → VERIFYING → (AWAITING_APPROVAL →)
INTEGRATION_READY → INTEGRATING → ACCEPTED`. A failed check goes to `RETRY_PENDING`. When the
attempt limit is reached it goes to `FAILED`. A missing phone, Unity or a stale dependency gives
`BLOCKED`. An exhausted budget, an owner hold or a provider problem gives `PAUSED`.

## Quickstart

```bash
cd game-forge
pip install -e '.[test]'           # optional: '.[claude]' adds the anthropic SDK
python -m pytest                   # full test suite (the dotnet e2e test skips if dotnet is absent)

forge preflight                    # writes .forge/toolchain-manifest.json and prints what is NOT verified
forge init                         # db, project, milestones, configured caps, branch forge/accepted
forge import projects/astra-kingdoms/tasks/pilot.toml     # 82 tasks, all DRAFT
forge status
forge approve 1                    # approve ticket 1's scope and limits
export ANTHROPIC_API_KEY=...       # or ~/.config/game-forge/anthropic.key (chmod 600, outside the repo)
forge run                          # scheduler pass until idle (or: forge run --loop --interval 10)
forge review-server                # http://127.0.0.1:8765/
forge approve 1 --candidate <sha>  # approve the exact hash shown on the review page
forge review 1 repair --candidate <sha> --category visual_mismatch --correction "hand does not grip bow"
forge stop-dispatch / forge resume-dispatch
forge budget show / forge budget set pilot 100
forge ledger add-expense --date 2026-10-06 --vendor Anthropic --invoice INV-1 --cash 1500 --fees 270 \
      --allocation '{"astra": 0.7, "forge": 0.3}'
forge ledger hours --date 2026-10-06 --hours 3 --milestone pilot
forge events 1
```

`forge init` creates the branch `forge/accepted` in the monorepo. Only the integration writer
advances it, and your normal checkout is never touched. Runtime data lives in `game-forge/.forge/`,
which git ignores.

What to expect on this machine: dotnet is present, but Unity, adb and a phone are not. So
ticket 1 (pure C#) can run. Unity/phone tickets (4, 5, 6, 8, 9, 10) stay **BLOCKED** with the
reason shown, and nothing is spent on them. While `astra-kingdoms` has no tests, `dotnet test`
evidence is **INCOMPLETE**, not a pass. The owner sees this and must explicitly override it or
request tests.

## Security boundaries (R1)

- **Credentials.** API keys are read only by `forge/credentials.py`, from an env var or a
  key file that must be outside every repository and `chmod 600`. The key goes into the SDK
  client object only. Every subprocess Forge starts (git, dotnet, Unity, adb, checks) gets a
  scrubbed environment with no `*API_KEY*`, `*TOKEN*`, `*SECRET*`, `ANTHROPIC_*`, `AWS_*`
  or SSH agent variables. Event payloads, logs and evidence text pass through `redact()`.
- **Builder agent.** The Claude builder has only `list_files` / `read_file` / `write_file` /
  `delete_file` / `finish`. Reads are confined to the worktree (no `..`, no symlink escape,
  no `.git`). Writes are confined to the task's permitted paths. There is no shell tool, so
  generated code never runs inside the agent loop.
- **Gates.** Check definitions live in the owner's `project.toml`, which is a protected path.
  Protected acceptance files are restored to their accepted bytes before checks run. The diff
  guard sends gate-weakening changes to a separate, explicit owner acknowledgement.
- **Integration.** Workers never write the accepted branch. Promotion is an atomic CAS.
  Approvals are bound to exact hashes, and changed bytes need fresh approval.
- **Network/UI.** The review server binds only to localhost, uses CSRF tokens and a strict
  CSP, and loads no external assets. Project policy (vendors, regions, data classes,
  hosted review, retention) is checked before every dispatch.
- **Not yet enforced (be aware).** R1 does **not** run generated code inside a container or
  VM, or under a dedicated OS account. Checks such as `dotnet test` run as your user, with a
  scrubbed environment but full filesystem and network access. The plan requires a dedicated
  worker account plus container/VM for unattended use, so treat R1 as supervised until that
  exists.

## Catalogue asset lane (find a free model before generating one)

An asset task (`task_type = "asset"`, `asset_brief = "game-forge/briefs/astra_v1/<id>.json"`) runs the
catalogue lane first. The lane searches Objaverse 1.0 for an existing model that uses only annotations
and Objaverse++ quality tags. It downloads only the chosen uids, cleans each one in Blender, checks its
budget, renders four views and asks the visual judge to pick one or return NONE. The generation lane
runs only when the result is NONE (exit 2). Code: `forge/lanes/`. Field names: `forge/lanes/catalogue_fields.py`.
Confidence table: `docs/catalogue_fields.md`.

Rules:
- **Licence:** CC-BY and CC0 only. CC-BY-NC, CC-BY-NC-SA, CC-BY-SA, CC-BY-ND and a missing or unknown
  licence are rejected (fails closed).
- **Quality:** Objaverse++ High or Superior only. The object must not be multi-object or a scene. It must
  be textured, which the lane defines as `is_single_color` false and the GLB having at least one texture.
  Age-restricted entries are dropped.
- **Reject list:** names or tags matching a known franchise, brand or character are skipped
  (`forge/lanes/reject_list.json`, plus each brief's `reject_terms`; whole words, case-insensitive). The
  judge is also told to return NONE for anything that resembles a recognisable game, film or brand asset.
- **Disk guard:** the lane refuses to run (exit 3) when the target, work or cache disk has less than 10 GB
  free. Downloads are capped at 500 MB per brief. The cap is planned from `archives.glb.size` and checked
  again against real file sizes.
- **Objaverse-XL sources are never used.**

Exit codes: `0` picked (awaiting owner approval), `2` NONE (fall through to generation), `3` blocked (disk,
Blender, packages, index, or judge policy).

```bash
pip install -e '.[catalogue]'                       # objaverse 0.1.7 + datasets (Objaverse++ tags)
forge catalogue build-index                         # downloads the Objaverse metadata shards once (~GBs)
forge catalogue build-index --quality-file opp.csv  # if `datasets` is unavailable: local Objaverse++ CSV/parquet/JSONL
forge catalogue search briefs/astra_v1/wooden_longbow.json          # ranked candidates, no download
forge catalogue run briefs/astra_v1/wooden_longbow.json --target ../astra-kingdoms --judge fake:first  # offline dry run
forge catalogue run briefs/astra_v1/wooden_longbow.json --target ../astra-kingdoms --task A1  # paid judge, charged to task A1
python -m forge.lanes.run_catalogue <brief.json> --target <repo>   # same as `forge catalogue run`
```

The index is a SQLite file at `.forge/catalogue/index.sqlite`. It stores uid, name, tags, licence, artist,
viewer URL, quality score, face count and GLB size. A later run reuses it; pass `--rebuild` to rebuild it.
Blender comes from `--blender`, `FORGE_BLENDER`, the toolchain manifest (`forge preflight`) or `PATH`. The
committed script `forge/lanes/blender_cleanup.py` imports the model, applies transforms, scales it to
`size_m`, decimates it to `max_tris`, downsizes textures to `max_texture`, exports a GLB and renders front,
side, back and three-quarter views. It has **not yet run on a real Blender** here (no Blender on this
machine), so the first real run checks it.

**Where things go.** A pick is written to `<target>/assets/source/<id>/`:
- the cleaned `<uid>.glb`;
- `front.png`, `side.png`, `back.png` and `three_quarter.png`;
- `report.json`, with the cleanup report, budgets, all candidates and the reason each excluded one was dropped;
- `pick.json`, with status `AWAITING_OWNER_APPROVAL`;
- **`credits.json`**, a JSON list with uid, name, artist, licence, source URL and download date. New picks
  are appended, never duplicated.

Downloads that were not picked are deleted, and this run's GLBs are removed from the objaverse package
cache (`~/.objaverse/hf-objaverse-v1/glbs`). The metadata stays so the index can be rebuilt.

**Licence labels can be wrong.** They come from uploader metadata. Every pick still needs owner approval on
the review page: panel 4 shows the renders, artist, licence and source link. Open the source page and
check the licence before approving. Character bases (`kind = "character_base"`) are always marked for
mandatory human approval.

Orchestrator mapping. The plan's state machine has no RUNNING → BLOCKED or RUNNING → NEEDS_INPUT edge, so:
- Disk, Blender, index, package, brief and judge-policy problems are checked during readiness. They give
  **BLOCKED** before anything is reserved or downloaded.
- A NONE result moves the task RUNNING → PAUSED → **NEEDS_INPUT** with the reason "no catalogue match;
  generation lane not available". No generation lane exists yet; `ProjectRuntime.generation_lane` is the hook.
- An exit 3 found mid-run gives **PAUSED** with "catalogue lane blocked: …".
- A pick becomes a normal candidate commit. It is verified (diff guard plus the lane's budget check), then
  goes to **AWAITING_APPROVAL** and is integrated only after the owner approves the exact hash.
- Judge cost is reserved and settled in the budget ledger like any provider call.

The judge sends PNG renders, which is a new data class (`render_image`). Astra's `project.toml` policy does
not allow it yet, so Astra asset tasks report BLOCKED with that reason until the owner adds it.

Briefs: `briefs/astra_v1/` holds 29 briefs:
- **Props** (3000 triangles, 512 px). Bows, arrow, quiver and shield use `kind = "weapon"`, meaning
  equipment a character holds or wears. They keep the props budget.
- **Arena pieces** (`arena`, 5000 triangles, 1024 px).
- **Cover objects** (`prop` with `role = "cover"`, 3000 triangles). 512 px was chosen because the list
  gave no texture budget.
- **One archer `character_base`** (15000 triangles). 1024 px was chosen because the list gave no texture
  budget.

There are no briefs for weapon effects, cards, the land map, icons or sound.

## Releases 3-5: template production, external use, paid offering

| Release item | Code | Status here |
|---|---|---|
| R3 document intake: Markdown / plain text / DOCX brief -> versioned spec; contradictions, missing rules, vague wording and out-of-template features become NEEDS_INPUT questions; owner answers recorded in the owner's words; change report per source change; approved requirements only *proposed* for change; acceptance cases frozen at approval; optional model pass through the provider `review` operation | `forge/intake/`, `forge spec ...` | Done, tested with FakeProvider. Heuristic checks can miss or over-report; dismissals are recorded |
| R3 traceability matrix, dependency planner (milestones, estimate ranges, human checkpoints, task file for `forge import`), bounded parallelism planner | `forge/planning/`, `forge plan`, `forge trace` | Done |
| R3 supported template `turn-duel-2p` (manifest, scaffold: C# rules / match state / replay / bot / rematch + NUnit checks, UI/audio/localization inputs, Forge project file) and the second sample brief (Rune Duel) | `templates/turn-duel-2p/`, `forge scaffold` | Done; the scaffold's sample build passes with the real .NET SDK. Unity-side screens are stubs (no Unity here) |
| R3 lanes: UI checks, licensed-library audio with rights records, localization extraction/key checks/translation review, balance/replay report running the Astra simulator twice per seed | `forge/production/`, `python -m forge.production ...` | Done. Localization lane run on the real Astra tables; balance lane run on the real simulator (small n) |
| R3 release packaging, signed owner approval, separate signer (apksigner/jarsigner, BLOCKED when absent), store submission behind an interface | `forge/release/`, `forge release ...`, `python -m forge.release.signer` | Done. Real `jarsigner` signing verified; `apksigner` and Google Play only against stand-ins. See `docs/release-and-signing.md` |
| R4 setup assistant (prerequisites reported, manual steps and terms, sample minimal build first), checksum-verified downloads, signed update channels, compatibility report, rollback, no update mid-task, uninstall keeping projects | `forge/installer/`, `forge setup / update / uninstall` | Done. No automatic downloads ship until real checksums are recorded |
| R4 diagnostic export (redacted, previewed, digest-confirmed), telemetry off by default (operational counts only), licence inventory, project export without a subscription, docs | `forge diag / telemetry / licences / export`, `docs/getting-started.md`, `docs/support-boundaries.md` | Done. Independent-user trials not run |
| R5 hosted service (tenants, roles, entitlements, hosted review links, BYOK vault + gateway, Razorpay/Paddle billing, managed credits, monitoring, support), unit economics, offering metrics | `forge/hosted/`, `forge hosted / economics / metrics` | Implemented and **disabled by default**. See `docs/hosted-offering.md` |

Backups/restore, asset generation lanes and the sandbox are separate work (`forge/backup/`, `forge/assets/`,
`forge/sandbox/`).

## What is NOT implemented yet (honest list)

R1 gaps:
- No Unity project exists in `astra-kingdoms/` yet, and the Unity adapter has never run a real
  editor. The device runner has never touched a real phone. Restart and device-disconnect
  recovery are demonstrated in tests with simulated crashes and a scripted adb, not on hardware.
- The Claude API adapter has never made a live call from here. Live behaviour (refusals, rate
  limits, prompt quality) is unverified. Refusal fallbacks are off by default because they may
  route to a pricier model.
- The scheduler is a synchronous, single-process loop (`forge run`). Leases, heartbeats and
  recovery are real, but there is no multi-process worker pool.
- `active_work_timeout_s` is recorded. Enforcement relies on lease expiry and per-request
  timeouts, not a watchdog that interrupts a running call.
- Nothing enforces retention yet (14/30-day policies are stored only), and there is no backup
  or restore drill command.
- Visual/play evidence capture (screenshots/video judging) is limited to what the device check
  collects. The `visual_judge` role is wired only to the catalogue lane. It has never made a live call.
- The catalogue lane has run only against a fake index, a fake downloader and a fake Blender. The real
  `objaverse` package, Hugging Face data and Blender script have not been exercised here, and several field
  names are third-party or unconfirmed (see `docs/catalogue_fields.md`). No generation lane exists yet.
- Performance budgets (frame time, PSS, download size) have no dedicated check type yet. Use a
  command check.

Later releases (by design not in R1):
- **R2** controlled assets: asset contracts, Blender normalization, rig/motion, provenance, GPU
  capability scheduler and model-route benchmarks. `TaskType.ASSET` runs only the catalogue lane
  (above); `gpu_vram_gb` leases exist only as schema.
- **R3, R4, R5**: implemented in code (see [Releases 3-5](#releases-3-5-template-production-external-use-paid-offering));
  their exit gates still need a real phone, Unity, independent users and paying customers.
