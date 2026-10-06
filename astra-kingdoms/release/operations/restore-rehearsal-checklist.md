# Restore rehearsal checklist (ticket 82)

Plan: "Keep migration scripts, rollback constraints and the last successful restore drill with
the release record." "Never restore a database backup in a way that silently loses paid
entitlements or duplicates a previously granted reward." Run before the first public release and
after every schema or provider change. The date and result go into `release-record --restore-drill`.

Environment: **staging copy only** (never production). Operator: ______ Date: ______

## Preparation

- [ ] Backup set identified: database snapshot ID ______, taken at ______ (UTC); object storage
      version ______; configuration commit ______.
- [ ] Provider records exported for the same window: Play voided purchases / order list, ad-network
      SSV callbacks, analytics deletion log.
- [ ] Synthetic data in staging covers: guest migration, daily tasks, coin grants, a paid
      entitlement, a refunded purchase, an ad reward, an account deletion in progress.

## Restore

- [ ] Restore the snapshot into a fresh staging database; record wall-clock time to restore: ____
- [ ] Apply migrations forward to the current schema; record the scripts and their hashes.
- [ ] Start the server version of the candidate against it.

## Verify

- [ ] Entitlement ledger: every paid entitlement in the provider export exists exactly once after
      reconciliation (`Reconciliation` in Meta reporting); purchases after the snapshot are
      re-granted from provider records, not lost.
- [ ] Reward ledger: no reward granted twice (idempotency keys); ad rewards after the snapshot are
      replayed from SSV records once.
- [ ] Deletion: accounts deleted after the snapshot are deleted again (deletion log replay); a
      restored backup never resurrects a deleted account.
- [ ] Match diagnostic records: replays of restored records verify (`Replayer.Verify`).
- [ ] Balance channel: pinned bundle per match preserved; current bundle equals the expected one.
- [ ] Client compatibility: current and minimum supported clients can sign in and play.

## Result

| Item | Result | Evidence |
| --- | --- | --- |
| Time to restore (RTO) | | |
| Data loss window (RPO) | | |
| Entitlements reconciled | | |
| Rewards not duplicated | | |
| Deletions preserved | | |

Outcome: PASS / FAIL. Follow-ups: ______

Status: **not run** (the online server and its storage are not built in this repository).
