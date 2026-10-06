# Closed-test feedback intake (ticket 79)

One record per report. Store as rows in the team's tracker (no unnecessary identifiers). The
questions mirror the pilot observation goals (plan): comprehension, touch controls, fairness of
the reveal, the land cut, losses players cannot explain, and voluntary rematches.

## Form (what the tester fills in)

| # | Question | Answer type |
| --- | --- | --- |
| 1 | Tester ID (T__) | text |
| 2 | Build version (Settings > About) | text |
| 3 | How did you play? (shared phone / vs computer / online with a friend) | choice |
| 4 | Did you understand why you won or lost the last duel? | yes / partly / no + text |
| 5 | Was anything about aiming, power or dodging hard to do by touch? | text |
| 6 | Did the reveal feel fair (both shots hidden until locked)? | yes / no + text |
| 7 | Could you draw the land cut you wanted within the time? | yes / sometimes / no + text |
| 8 | Did you choose to play another match straight away? Why or why not? | text |
| 9 | Anything that broke, froze or looked wrong? When? | text (+ optional screenshot) |
| 10 | Was any text unclear, cut off or in the wrong language? | text |
| 11 | Anything you would change first? | text |

## Triage record (filled by the team)

| Field | Values |
| --- | --- |
| Intake ID | FB-### |
| Received (UTC) | |
| Tester ID / cohort | from the tracker |
| Category | crash / freeze / wrong result / controls / comprehension / fairness / land cut / text-language / performance / store-purchase / other |
| Severity | release-blocker / major / minor / suggestion |
| Reproduced? | yes (steps, build, device) / no / not attempted |
| Linked evidence | match record id, logcat file, screenshot hash |
| Action | ticket ID / known issue KI-### / won't fix (reason) / needs more data |
| Owner | |
| Closed (UTC) and outcome | |

Crash, wrong-result or purchase reports also follow the incident table in
`release/operations/support-runbook.md`. Release blockers are copied into
`release/operations/known-issues.md`.
