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
  collects. The `visual_judge` role is configured but not wired to a lane.
- Performance budgets (frame time, PSS, download size) have no dedicated check type yet. Use a
  command check.

Later releases (by design not in R1):
- **R2** controlled assets: asset contracts, Blender normalization, rig/motion, provenance, GPU
  capability scheduler and model-route benchmarks. `TaskType.ASSET` and `gpu_vram_gb` leases
  exist only as schema.
- **R3** document-to-spec intake, ambiguity handling, release packaging and the separate signer.
  `release_approval` exists as a field but has no signing flow.
- **R4** installer, update channels, rollback, backup/restore, diagnostic export, licence inventory.
- **R5** team roles, hosted review, tenant isolation, billing/refunds, managed credits.
