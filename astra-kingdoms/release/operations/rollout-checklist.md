# Rollout checklist (tickets 81, 82)

Candidate / version code: ______  Release authority: ______  Date: ______

## Before the first percent

- [ ] Release record status PASS; candidate hash ______ equals the approval and signing records.
- [ ] Signed artifact hash ______ equals the file uploaded (Forge submission log).
- [ ] Server compatibility: the server accepts this version **and** every version >= current minimum.
- [ ] `minimum-version-policy.json` unchanged, or its change passed `version-policy-check` with a grace period.
- [ ] Kill switches available for: each new weapon, each ad placement, each optional feature.
- [ ] Support rota covers the observation window (support runbook "Coverage").
- [ ] Rollback method written in the record (config rollback / previous build ready to re-release).
- [ ] Device lifecycle checks on the reference phone with **this** build: background/foreground
      during selection, lock screen during resolution, incoming call/notification, low-battery
      mode, app killed and restarted mid-match (offline: local match recovers or ends cleanly;
      online: reconnect/result recovery), rotation lock, text scale 130 %.

## Stage table

| Stage | Audience (users, not just %) | Window | Observe vs previous release | Expand if | Halt if |
| --- | --- | --- | --- | --- | --- |
| 1 | smallest that yields >= 200 sessions | >= 48 h | crash-free sessions, match completion, purchase errors, server errors, support reports | no stop condition; metrics within noise of previous | any stop condition |
| 2 | ______ | >= 48 h | same | same | same |
| 3 | 100 % | - | daily checks continue | - | - |

Stop conditions (halt, then follow the incident table): crash-free sessions < 99 % in a device
cohort with >= 50 sessions; any wrong result, land corruption, duplicate grant or purchase grant
failure; match completion < 70 % human-started; repeated freeze on a reference phone; analytics or
purchase verification broken.

## After 100 %

- [ ] Previous build kept available for re-release (artifact + record archived by the signer).
- [ ] Known issues updated; support macros updated for the new version.
- [ ] Decision recorded (`release-decision-record-template.md`).
