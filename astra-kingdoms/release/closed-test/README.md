# Closed-test operations (ticket 79)

Acceptance (plan): *Current account-specific store requirements and tester feedback actions are
satisfied.* The closed test (20-50 recruited players) is primarily for reliability, onboarding
and device coverage, not market retention (plan "Measurement and stage decisions").

## Play requirement (re-check in Play Console before starting; requirements change)

If the developer account is a **personal account created after 13 November 2023**: at least
**12 testers opted in continuously for 14 days** on a closed track, then apply for production
access. Twenty invitations or fourteen calendar days alone do not prove eligibility (plan [S26]).
Recruit about 20 so that 12 remain opted in. Record the account type and the requirement text
shown in Play Console (with the date read) in the release decision record.

## Flow

1. **Recruit** with `recruitment.md`. Tag every tester's cohort (friend / colleague /
   contributor / community / recruited) so their feedback is never silently treated as
   representative.
2. **Consent** with `tester-consent.md` before adding the tester's Google account to the
   closed-track list. Recording (screen/voice) is a separate, optional consent. Adults only for
   the closed test unless the child-audience flows and parental consent are implemented.
3. **Track opt-in daily** in `tester-tracker.csv` (pseudonymous tester IDs only; the mapping to
   e-mail addresses lives in the Play Console tester list, not in the repository). Mark `Y` for
   each day the tester is still opted in (Play Console > Testing > Closed testing > Testers).
   ```bash
   dotnet run --project tools/AstraKingdoms.Release -- closed-test-check \
     --tracker release/closed-test/tester-tracker.csv --out out/evidence/closed-test.json
   ```
   PASS only when 12 testers have 14 continuous days; otherwise INCOMPLETE with the gaps listed.
4. **Collect feedback** with `feedback-intake-template.md` (in-app link or form). Crash and
   technical reports go through the incident table (`release/operations/support-runbook.md`).
5. **Triage weekly**: each item gets a category, severity and an action (fix in ticket, known
   issue, won't fix with reason). An item is closed only with an action record. Rules or balance
   changes follow the balance publication process (ticket 24), not ad-hoc edits.
6. **Apply for production access** only after `closed-test-check` passes and the open
   release-blocker issues are closed; attach the tracker hash and the feedback action log.

## Data handling

Tracker and feedback notes hold no unnecessary identifiers. Retention follows the plan's
proposed defaults ("Local pilot feedback ... retain through the pilot decision and one follow-up
cycle"; recordings deleted when no longer needed). Deletion requests from testers are handled
like any player deletion request.

Status: templates and the eligibility check are done; **no testers recruited** (needs the Play
Console app, a closed-track build and people).
