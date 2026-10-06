# Astra Kingdoms match service runbook

This runbook covers the authoritative online match service (`server/AstraKingdoms.Server`, V1
tickets 49-56). It follows the plan chapters "Release and rollback procedure", "Incidents and
support" and "Data and artifact retention defaults". Steps that need infrastructure this repository
does not have (a host, a Firebase project, a store listing) are marked **[needs owner setup]**.

## 1. What the service does

- Clients connect to `/v1/ws` (WebSocket). The upgrade must carry `Authorization: Bearer <token>`.
  The token is a Firebase ID token in production. It is verified with RS256 against Google's
  published keys, with audience and issuer set from the project ID. The first frame must be
  `hello`. The message schema is the Unity client's protocol assembly
  (`unity/Assets/Scripts/Online/Protocol`).
- Each match runs the shared rules assembly (`MatchEngine`) with a pinned config and rules hash.
  The server alone advances phases at the online deadlines: 2 s terrain, 12 s concurrent choice,
  2.5 s replay, 12 s card and cut. Proposed policy: a 60 s private loadout window, and its expiry
  is a technical void.
- Each player receives only their own `PlayerView` and public events. Locks are immutable. A
  duplicate returns the original receipt. Receive time is authoritative.
- State is stored in SQLite (`AstraServer:Storage:SqlitePath`):
  - `matches`: config, seed and command log for every match. This makes matches exactly
    replayable. **It contains secrets until a match settles.**
  - `reward_grants`: primary key (result_id, player). A grant can never be applied twice.
  - `audit_log`: append-only, enforced by database triggers. It never holds an unrevealed choice.
  - `grievances`: the Rule 20 grievance intake.
- Logs are structured JSON on stdout (non-Development environments). Players appear only as
  `p_<hash>` references. Tokens and message payloads are never logged.

## 2. Configuration

Settings live in `appsettings.json` under `AstraServer`. Environment variables override them with
`__` as the separator, for example `AstraServer__Auth__FirebaseProjectId=astra-prod`.

| Setting | Default | Notes |
| --- | --- | --- |
| `Auth:Mode` | `Firebase` | `Dev` accepts `dev:<name>` tokens. It is refused outside the Development environment unless `Auth:AllowDevOutsideDevelopment=true` (private load hosts only). |
| `Auth:FirebaseProjectId` | empty | **Required** in Firebase mode. Startup fails without it. **[needs owner setup]** |
| `Timings:*Ms` | 2000 / 12000 / 2500 / 12000 / 60000 | These are the plan's online values. Change them only for tests or load hosts. |
| `Timings:BotThinkMs` | 1200 | The visible delay before a server bot acts. |
| `Rooms:ExpirySeconds`, `Rooms:HostGraceSeconds` | 600, 30 | Proposed values. |
| `Queue:BotOfferAfterSeconds` | 20 | Plan value (proposed). |
| `RateLimits:*` | 40 burst, 20/s per identity; 10 per IP-minute anonymous | Persistent flooding closes the socket with `policy_violation`. |
| `Storage:SqlitePath`, `Storage:MatchRecordRetentionDays` | `data/astra-server.db`, 30 | Plan default: diagnostic records are kept 30 days. |
| `Lifecycle:ShutdownPolicy` | `Preserve` | `Preserve` suspends matches and resumes them after restart. `Void` settles them as technical voids. |
| `Lifecycle:MaxSuspendMinutes` | 10 | A match suspended for longer than this is voided at startup instead of resumed. |
| `Lifecycle:NewMatchesEnabled`, `Lifecycle:DisabledCatalogs` | `true`, `[]` | **Incident switches.** They are read live from `appsettings.json`. Active matches are not touched. |
| `Clients:MinimumVersion` | `0.0.0` | Controlled minimum-version policy. Older clients get `CLIENT_TOO_OLD`. |
| `Grievance:*` | placeholders | Operator name, email, URL and response times. **[needs owner setup]** |

## 3. Build, deploy, health

```bash
cd astra-kingdoms
dotnet test tests/AstraKingdoms.Server.Tests          # must be green
docker build -f server/Dockerfile -t astra-match:$(git rev-parse --short HEAD) .
docker run -p 8080:8080 -v astra-data:/data \
  -e AstraServer__Auth__FirebaseProjectId=<project> astra-match:<commit>
```

- **Liveness:** `GET /healthz` returns 200 while the process serves requests.
- **Readiness:** `GET /readyz` returns 200 `ready` with counts and the rules hash. It returns 503
  while draining or when storage is unavailable. Route traffic only to ready instances.
- **Shutdown:** SIGTERM drains the service:
  1. readiness drops;
  2. rooms and queue entries close with `server_shutdown`;
  3. active matches are suspended (Preserve) or technically voided (Void);
  4. clients get `server.draining` and close code 1001, then reconnect.

  Give the container at least 20 s to stop (`HostOptions.ShutdownTimeout`).
- **Single instance.** V1 runs **one instance per database**. Matches live in that process's
  memory, and SQLite has one writer. A blue/green switch must drain the old instance (Preserve)
  before the new one opens the same volume. Scaling out needs sticky routing by match plus a shared
  store. That is not built.

## 4. Declared initial capacity and the load check

**Declared scenario (proposed V1 numbers, to validate on the real host):**
- one instance (2 vCPU, 2 GB RAM, local SSD);
- 200 concurrent matches (400 connected players) at production timers;
- 10% of players lose their link once mid-match.

Pass conditions:
- every match settles;
- no command from a well-behaved client is rejected;
- command receipt p95 ≤ 250 ms and p99 ≤ 500 ms;
- every player gets exactly one `match.end`;
- every dropped player reconnects.

```bash
# Load host only (Dev auth on a private host):
ASPNETCORE_ENVIRONMENT=Production AstraServer__Auth__Mode=Dev AstraServer__Auth__AllowDevOutsideDevelopment=true \
  dotnet AstraKingdoms.Server.dll --urls http://0.0.0.0:8080
dotnet run -c Release --project tools/AstraKingdoms.LoadTest -- --url ws://<host>:8080 --matches 200 --drop-percent 10 --report load.md
```

The tool exits 0 on PASS. Record `load.md` with the release candidate. A run on a developer machine
(client and server on one box) checks the tool and the service, **not** the deployment target.
The deployment check is **[needs owner setup]**.

The default `--policy fast` makes cheap legal choices, so the client machine spends its CPU on
traffic. Use `--policy bot` to run the full rules bot.

**Last measured (developer container, 4 cores shared by the server and all 400 clients):**
- Settings: production timers, 200 matches started within about 2 s, so every volley across all
  matches resolves at the same instant, and 10% link drops.
- Correctness passed: 200/200 matches settled, 0 rejected commands, 0 service errors, every player
  got exactly one `match.end`, and 20/20 dropped players recovered.
- Latency **failed** the declared limits:
  - client-observed receipt p50 6 ms, p95 294 ms, p99 494 ms;
  - service-side handling p50 0.4 ms, p95 16 ms, p99 152 ms.

  The tail comes from CPU contention during the synchronized bursts on a shared box (the clients run
  alongside the server). It must be re-measured on the target host before the gate is claimed.
- Two fixes came out of these runs:
  - per-round background checkpoints instead of a synchronous write per command;
  - re-arming timers that fire a few milliseconds early.

**Persistence policy.** These rows are written durably before the service continues:
- creation, resume, suspension and settlement (including grants);
- a checkpoint each round, written in the background.

SQLite runs in WAL mode with `synchronous=NORMAL`. A power loss can drop the last commits. A match
interrupted that way, or by any crash, is a technical void at startup. This is the plan's rule:
a service failure that prevents authoritative resolution gives no reward and no loss.

## 5. Release procedure (plan: "Release and rollback procedure")

1. A release candidate is one immutable combination of:
   - the source commit;
   - the rules hash (`/readyz` shows it);
   - the container image digest;
   - the configuration;
   - the test and load evidence.

   Any change makes a new candidate.
2. Check compatibility before rollout:
   - The **rules hash must match the shipped clients' rules hash.** A mismatch rejects every
     command with `RULES_HASH`.
   - Raise `Clients:MinimumVersion` only after the store rollout of the compatible client.
3. Roll out to the smallest audience that answers the release question. Watch the following
   against the previous release:
   - match completion (`match_settled` audit entries by outcome);
   - technical voids;
   - `command_rejected` rates;
   - receipt latency;
   - `/readyz`.
4. Expand only after the observation window and its stop conditions.

## 6. Rollback

- **Service or configuration rollback** (for new work): deploy the previous image or configuration
  with the Preserve drain. Settled results are kept, because the database is append-only for
  grants and audit.
- **Suspended matches across a rollback.** Matches suspended by a build with a **different rules
  hash** cannot be replayed by the older build. At startup they are settled as technical voids
  (`unrecoverable_record`): no reward and no loss. Prefer rolling back between matches when
  possible: set `NewMatchesEnabled=false`, wait for `active_matches` to reach 0, then switch.
- **Database schema.** The store records `schema_version`. A server refuses to start against a
  **newer** schema than it knows. Roll forward, or restore the matching backup (section 7).
- **Client rollback** needs a store release and cannot assume that devices revert. Keep the
  service compatible with every client version at or above `Clients:MinimumVersion`.
- **Disabling a failing feature in new matches.** Use `Lifecycle:DisabledCatalogs` (for example
  `["Full"]`) or `NewMatchesEnabled=false` in `appsettings.json`. It is read live. An active
  match's snapshot is never altered.

## 7. Backup and restore

- **Backup.** Use the SQLite online backup on the running service:
  `sqlite3 /data/astra-server.db ".backup '/backup/astra-$(date +%F-%H%M).db'"`. Take one at least
  hourly **[needs owner setup: schedule and off-host copy]**. The file holds seeds and choices of
  unfinished matches: encrypt it at rest and restrict access.
- **Restore drill:**
  1. Stop the service with Preserve.
  2. Copy the backup into place.
  3. Start the service.
  4. Check `/readyz`.
  5. Compare `SELECT COUNT(*) FROM reward_grants` with the pre-restore value.

  Record the drill date with the release.
- **Never lose or duplicate grants.** Grants are keyed by `(result_id, player)`. A restore that is
  older than the newest grants loses them. Before restoring, export the newer `reward_grants` rows
  and `INSERT OR IGNORE` them back afterwards. Re-inserting is safe because of the primary key.
  Paid entitlements are not stored here: purchases are a separate ledger (V1 progression tickets).

## 8. Incidents (plan: "Incidents and support")

| Incident | Immediate action in this service | Closure evidence |
| --- | --- | --- |
| Wrong match result or land corruption | Set `NewMatchesEnabled=false`, or disable the affected catalog. Preserve the inputs: copy `matches.record_json` for the affected IDs. `Replayer.Verify` reproduces each match exactly from the record. | Reproduced case, corrected resolver, a replay regression test, and a controlled compensation decision recorded as an audit entry. |
| Repeated crash or freeze (service) | Readiness is false, so stop routing. Check the logs for the last `match_settled` and `technical_void` details. On restart, interrupted matches become technical voids by design. | Root cause, a regression test, and a restart drill. |
| Duplicate progression reward | Not possible through `reward_grants` (primary key). If a downstream consumer duplicated grants, disable that consumer. Reconciliation: compare downstream totals with `SELECT player, SUM(xp) FROM reward_grants GROUP BY player`. | Idempotent repair and reconciliation. |
| Secret exposure or unauthorised access | Revoke the affected Firebase sessions or keys **[needs owner setup]**. Rotate the database volume credentials. Restart the service to drop all sockets: players reconnect with fresh tokens. Audit actors are pseudonymous: map `p_<hash>` back to an account only inside the incident. | Scope assessment, remediation, applicable notifications, and verified access controls. |
| Provider outage (Firebase or Google keys) | Cached signing keys keep working until their max-age, then new sign-ins fail and existing sockets continue. Announce it in the client status line. Nothing to reconcile. | Recovery confirmed with a fresh sign-in. |
| Flooding or abuse | Per-identity token buckets limit it automatically. Persistent violators are closed with `policy_violation`. Lower `RateLimits` live if needed (restart). | Abuse impact assessment. |

The owner reviews new incidents and grievances daily while the public service is active.

## 9. Grievances (India Online Gaming Rules 2026, Rule 20)

- **Contact route:** `GET /v1/grievances/contact` returns the operator name, email, URL and
  response windows from `Grievance:*`. **[needs owner setup: real operator and contact]**
- **Intake:** `POST /v1/grievances` with `{category, description, match_id?, contact?}`. It returns
  `201 {grievance_id: "GRV-YYYYMMDD-XXXXXXXX"}`. Anonymous intake is allowed and rate limited per
  IP. An authenticated reporter is stored as a pseudonymous reference. Each intake writes a
  `grievance_received` audit entry.
- **Status:** `GET /v1/grievances/{id}`.
- **Outcome records.** For now the operator updates `grievances.status` (`received`, then
  `acknowledged`, then `resolved` or `rejected`) and appends an audit entry with the decision. A
  staff tool is not built.

## 10. Retention

Settled match records are deleted hourly once `retain_until` passes. The plan default is 30 days:
`Storage:MatchRecordRetentionDays`. Extend it for a support case by updating that match's
`retain_until`. Audit, grant and grievance rows are kept. Their retention period needs a
legal/accounting decision **[needs owner decision]**.
