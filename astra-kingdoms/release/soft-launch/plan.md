# Controlled soft launch and cohort observation (ticket 80)

Acceptance (plan): *Four-week observation is scheduled; sufficient mature cohorts exist before
judging goals.* Source: plan "Measurement and stage decisions", "Release and rollback procedure",
"Live operations cadence".

## Scope

- Market: India only. Track: production with a staged rollout sized to answer the question, not a
  fixed percentage (plan: "ten percent of a very small audience is not an informative test").
- No paid user acquisition until measurement is stable, technical completion is acceptable and a
  cash ceiling is approved (plan "Launch communication and growth").
- Arrivals: friend-room invitations, observed local play, developer clips, small communities.
  Every install is tagged with a `CohortSource` (Meta analytics) so friends/colleagues/contributors
  stay separable from organic players.

## Schedule (4 weeks of observation after the rollout reaches its planned audience)

| Week | Activity |
| --- | --- |
| 0 | Release candidate approved (release record PASS), staged rollout starts; daily checks begin |
| 1 | Daily: crashes, failed matches, server health, purchase reconciliation, critical reports. First D1 cohorts mature (players whose first session is >= 48 h old). |
| 2 | Weekly review: engagement, support themes, balance report (diagnosis only). First D7 cohorts mature (>= 192 h). |
| 3 | Weekly review; decide whether arrivals are large enough for a D7 read (see sample sizes). |
| 4 | Decision meeting with the template in `decision-template.md`. D30 cannot be complete for most cohorts; report it as **not yet observable**, never as a failure. |

## Metric definitions (implemented in `src/AstraKingdoms.Meta/Reporting/Metrics.cs`)

| Metric | Definition | Calculator | Proposed working gate |
| --- | --- | --- | --- |
| D1 / D7 / D30 retention | A real foreground game session in hours 24-48 / 168-192 / 720-744 after the player's first valid session; only players whose window has fully elapsed are counted | `RetentionCalculator.Compute(events, RetentionWindow.D1/D7/D30, asOf, ...)` | D1 35 %, D7 12 %, D30 5 % (V2 target) |
| Uncertainty | Approximate 95 % Wilson interval for every proportion | `Wilson.Interval(successes, total)` | Report always; 6/50 -> 5.6-23.8 % |
| Match completion | Human-started matches that reach a normal terminal result (incl. a valid early victory), split by mode and reason; forfeits, lock-timeout forfeits and technical aborts reported separately | `CompletionCalculator.Compute(records)` | 90 % initial target; 70 % is only a warning level |
| Crash-free sessions | Denominator = every instrumented real-user session in the cohort, incl. sessions that crash before a match | `CrashFreeCalculator.Compute(events, cohort)` | 99 % provisional minimum; critical crashes reviewed separately |
| Matches per active player | Distribution per active day, bots excluded | `MatchesPerActivePlayer.Compute(events, dayOffset)` | 3/day is a hypothesis |
| Purchase / ad funnels | State the denominator (eligible active users vs users shown the offer) | Meta `Reporting` | 2 % pass conversion is a V2 hypothesis |

Exclusions: bots, simulator accounts, automated test clients and internal sessions never count
as retained or paying users (analytics traffic flags). Human-vs-human and human-vs-bot completion
are reported separately. Shared-phone sessions are not converted into extra unique players.

## Sample size and decision rules

- Planning range for an informative initial retention read: **500-1,000 new users** in mature
  cohorts (not an industry standard). At 60/500 D7 the interval is about 9.4-15.1 %.
- If arrivals or mature cohorts are too small, the outcome is **"insufficient evidence"**, not a
  retention failure.
- No automatic "stop after two patches": at most **two bounded experiments**, each with one
  hypothesis, one principal change, a recruiting/observation plan and a cash/hour limit,
  compared on consistent mature cohorts with regression checks.

## Stop conditions during the rollout (pause expansion immediately)

- Crash-free sessions below 99 % in any device cohort with >= 50 sessions, or any freeze report
  reproduced on a reference phone.
- Any wrong match result, land corruption, duplicate grant or purchase grant failure
  (incident table, `release/operations/support-runbook.md`).
- Match completion below 70 % for human-started matches.
- Analytics or purchase verification broken (results uninterpretable).
