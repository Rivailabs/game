# Soft-launch decision record: GO / HOLD / STOP (ticket 80)

Copy per decision. Numbers come from the Meta calculators with the stated denominators; every
proportion carries its Wilson interval and cohort size.

| Field | Value |
| --- | --- |
| Decision date (UTC) | |
| Release candidate / version code | |
| Observation window | from ____ to ____ (weeks: __) |
| Arrivals (real users, bots/internal excluded) | total __; by cohort source: friend __ / colleague __ / contributor __ / community __ / organic __ |
| Mature cohorts available | D1: __ players; D7: __ players; D30: __ players (or "not yet observable") |

## Metrics

| Metric | Value | 95 % Wilson interval | n | Gate (proposed) | Read |
| --- | --- | --- | --- | --- | --- |
| D1 retention | | | | 35 % | above / within / below / insufficient |
| D7 retention | | | | 12 % | |
| D30 retention | | | | 5 % (V2) | not yet observable? |
| Match completion (human-started, H-v-H) | | | | 90 % (warn 70 %) | |
| Match completion (human-started, H-v-bot) | | | | 90 % | |
| Crash-free sessions | | | | 99 % | |
| Matches per active player per day (median, p75) | | - | | hypothesis 3 | |
| Purchase errors / unreconciled grants | | - | | 0 | |

## Qualitative evidence

- Top support/feedback themes (with counts):
- Losses players could not explain (examples, linked match records):
- Device cohorts with problems:

## Decision

- [ ] **GO**: expand the rollout to ____ (audience size) for ____ days; stop conditions unchanged.
- [ ] **HOLD**: keep the current audience; run experiment ____ (hypothesis, one principal change,
      recruiting plan, cash limit ____, hour limit ____, mature-cohort comparison date ____).
- [ ] **STOP**: stop expansion. Reason: evidence remains weak after the two bounded experiments and
      the budget is exhausted / a stop condition was hit / ____.
- [ ] **INSUFFICIENT EVIDENCE**: arrivals or mature cohorts too small to judge; next read on ____.

Rationale (must reference the numbers above and their intervals):

Approved by (owner): ________  Reviewer: ________
