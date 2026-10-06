# Support and operations runbook (ticket 82)

Source: plan "Incidents and support", "Live operations cadence", "Data and artifact retention
defaults". Response expectations are written so that they can actually be staffed; do not
promise a response time the business cannot keep.

## Contacts and coverage

| Role | Person | Backup | Reachable via |
| --- | --- | --- | --- |
| Owner / release authority | ______ | ______ | ______ |
| On-call for severe issues (wrong results, purchases, security) | ______ | ______ | ______ |
| Support inbox | support@______ | | |
| Grievance officer (India, Rule 20) | see `grievance-process.md` | | |

If no backup exists for an absence, **cap the audience** (halt rollout, pause new paid
products) rather than promising cover (plan).

Published response expectations (proposed, adjust to staffing): acknowledgement within 2
business days; severe issues (wrong match result, purchase not granted, account access,
security) triaged within 1 business day.

## Incident table (from the plan)

| Incident | Immediate action | Closure evidence |
| --- | --- | --- |
| Wrong match result or land corruption | Stop the affected mode or rules version for new matches (kill switch / balance rollback); preserve inputs and hashes | Reproduced case, corrected resolver, replay regression and a controlled compensation decision |
| Repeated crash or phone freeze | Halt rollout; identify device/build cohort; disable the optional path when safe | Reproduction, physical-device fix and recovery in the affected cohort |
| Purchase grant failure | Preserve the purchase token securely; reconcile Play state and the entitlement ledger | Correct grant or explained refund path, duplicate protection and customer response |
| Duplicate ad or progression reward | Disable the faulty grant path; keep audit evidence | Idempotent repair, reconciliation and abuse impact assessment |
| Secret exposure or unauthorised access | Revoke credentials/sessions; contain the service | Scope assessment, remediation, applicable notifications, verified access controls |
| Provider outage or unknown job status | Pause new submissions to that route; reconcile job IDs | Settled ledger, recovered artifacts or defined credit adjustment |
| Unexpected spending | Stop new dispatch through the independent limit control | Reservation/usage reconciliation, cause, revised policy |
| Photo or clan misuse (later versions) | Restrict reported content through moderation | Reviewed evidence, action record, appeal route, deletion handling |

Every incident gets: ID, opened (UTC), severity, owner, affected versions/cohorts, actions with
timestamps, closure evidence, and a known-issues row if players can still hit it.

## Common requests (macros)

| Request | Steps |
| --- | --- |
| "I paid but did not get the item" | Ask for the order ID (GPA.xxxx); look it up in the entitlement ledger; if verified and missing, grant via the reconciliation path; never grant from a screenshot alone. |
| "Delete my data/account" | Point to in-app Delete my data or the web form; ownership is verified by the deletion service; reply with what is retained (pseudonymised purchase records) and the completion window. |
| "I lost a match I should have won" | Ask for the match time; fetch the diagnostic record (30-day retention); replay with `Replayer.Verify`; explain using the volley explanation; escalate as "wrong result" only if the replay disagrees with the display. |
| "Game crashes on my phone" | Collect model, Android version, build version; check the crash cohort; add to the known-issues register. |
| Complaint under the Online Gaming Rules | Route to the grievance process (acknowledge, record, resolve within the published time). |

## Live-operations cadence (plan)

- **Daily:** crashes, failed matches, server health, purchase reconciliation, provider spending,
  critical reports, open incidents (owner reviews new incidents daily while a public service is active).
- **Weekly:** balance report (diagnosis only; a weekly report does not require a weekly rule
  change), engagement, support themes, content maintenance hours.
- **Per release:** rollout checklist, release decision record.

## Retention defaults (proposed; need review against actual data and law)

| Record | Default |
| --- | --- |
| Local pilot/closed-test feedback | Through the pilot decision and one follow-up cycle; recordings deleted when no longer needed |
| Game diagnostic match records | 30 days, support-case hold where justified |
| Raw optional product analytics | 90 days when permitted; aggregates longer |
| Purchase and financial records | Period chosen with accounting/tax/fraud advice |
| Support and grievance records | Proposed 365 days, needs legal review |

Deletion distinguishes active data, backups and provider copies; the published process states
the real completion window.
