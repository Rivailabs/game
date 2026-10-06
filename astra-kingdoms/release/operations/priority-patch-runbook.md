# Priority patch and rollout runbook (ticket 81)

Acceptance (plan): *Fixes have reproducible evidence; rollout/rollback avoids incompatible active
matches.* Source: plan "Release and rollback procedure", "Incidents and support".

## 0. Decide which lever fixes it (fastest safe first)

| Lever | Use when | Effect on active matches | How |
| --- | --- | --- | --- |
| Kill switch (config) | One weapon, ad placement or optional feature is failing | None: new matches only | `kill_switches` in `minimum-version-policy.json` (served by the server); validate with `version-policy-check` |
| Balance rollback | A published balance bundle misbehaves | None: matches keep their pinned bundle | `BalanceChannel` rollback (ticket 24): new matches only; IDs never reused |
| Server/config rollback | Backend regression | Committed results preserved | Restore the previous compatible service/config; never restore a DB over committed results (section 5) |
| Client patch (store release) | Client code or asset defect | Old clients keep working inside the compatibility window | Sections 1-4 |
| Minimum-version raise | Old clients are unsafe (security, wrong results) | Enforced after the grace period, new matches only | Section 4 |

## 1. Reproduce and fix with evidence

1. Open an incident (support runbook) and a known-issues row (`KI-###`).
2. Reproduce on the reported build: match record (`records/<match-id>.json`) replayed with
   `Replayer.Verify`, device and build ID, logcat. A rules defect gets a failing rules test or
   golden replay **before** the fix (plan: "Reproduced case, corrected resolver, replay regression").
3. Fix on a branch from the **released commit** (not from main if main carries unreleased scope).
4. Required evidence for the patch candidate:
   - [ ] the new regression test fails on the old commit and passes on the fix;
   - [ ] `ci/build-and-test.sh` green (0 warnings);
   - [ ] device scenario `pilot-smoke` + `frame-pacing` on a registered phone (`perf-compare` PASS, no regression);
   - [ ] `ci/release-gates.sh --store-release` and the editor gate PASS;
   - [ ] if the rules changed: new rules version/hash, migration note, compatibility decision.

## 2. Build, record, approve, sign

Follow `release/packaging/README.md` with a **new version code**. A patch is a new candidate:
earlier approvals do not carry over (plan). The release record lists the incident, the fix
evidence and the rollback method.

## 3. Staged rollout

Use `rollout-checklist.md`. Start with the smallest audience that answers "is the fix effective
and harmless?"; observe crash-free sessions, match completion, purchase errors and server health
against the previous release for the stated window; expand only when the stop conditions stay
clear. Halt the release in Play Console (`forge release authorise-submission --status halted`) on
any stop condition.

## 4. Minimum-version change (only if old clients must stop)

1. Edit `minimum-version-policy.json`: raise `minimum_version_code`, set `changed_utc` = now and
   `enforce_after_utc` >= now + `grace_minutes` (>= 60).
2. `version-policy-check --policy new.json --previous old.json` must PASS (it fails a transition
   that would strand active matches).
3. Publish the config; old clients see `update.recommended` until enforcement, then
   `update.required` before starting a **new** match. Active matches finish on their pinned version.

## 5. Rollback

- **Config/backend rollback** restores the previous compatible service/configuration for new work
  while preserving committed results. Economy and purchase ledgers are append-only: correct with
  compensating transactions, never by restoring a backup over them.
- **Client rollback** = another store release with a higher version code built from the previous
  good commit. Devices do not revert automatically; keep the server compatible with both.
- Record the rollback in the release decision record and close the incident only with the closure
  evidence from the incident table.
