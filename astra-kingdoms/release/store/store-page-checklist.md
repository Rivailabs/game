# Store page review checklist (ticket 78)

Acceptance (plan): *Audience, permissions, wording and regional availability match the product.*
Complete one copy per release candidate; attach it to the release decision record.

Candidate ID / build: ________  Reviewer: ________  Date: ________

## Wording

- [ ] `store-text-lint` on every listing file: PASS (no FAIL, no INCOMPLETE). Output file hash: ____
- [ ] Every feature line exists in this candidate (shared phone, online friend rooms, bots,
      tutorial, cosmetics, ads, languages). Remove lines for features not in the build.
- [ ] No four-player, kingdom/world, ranked, clan, replay-sharing, tournament or prize wording.
- [ ] Purchases described as optional and cosmetic; ads as optional, rewarded, outside matches.
- [ ] Cultural review checklist (`art/briefs/cultural-review-checklist.md`) signed for all text.

## Graphics (brief 72)

- [ ] Icon, feature graphic and every screenshot come from this candidate; build IDs recorded in
      `screenshot-manifest.md`.
- [ ] Screenshot UI language equals the listing language.
- [ ] Video (if any) is a real capture of this candidate.

## Audience and rating

- [ ] Target age groups in Play Console = the decision recorded in the release record.
- [ ] IARC questionnaire answered from `iarc-questionnaire-draft.md`; issued ratings recorded.
- [ ] If children are in the audience: ad SDK + every adapter Families self-certified;
      analytics off for children unless assessed; parental-consent purchase flow or "children
      cannot buy" decision implemented.

## Permissions

- [ ] Merged AndroidManifest of the candidate reviewed (`aapt2 dump permissions` or
      `bundletool dump manifest`). Expected: INTERNET, ACCESS_NETWORK_STATE, BILLING (if Play
      Billing), AD_ID (only if the ad SDK needs it; must match the Data safety advertising-ID answer),
      VIBRATE (haptics). Any other permission needs a written reason. Output hash: ____
- [ ] No location, contacts, camera, microphone or storage permissions.

## Data safety and policy links

- [ ] Data safety answers = `data-safety` output for this candidate (gate PASS).
- [ ] Privacy policy URL, account-deletion URL and support/grievance contact work from a phone
      outside the office network (`PrivacyLinks.ValidateForRelease()` passes on the release build).

## Regional availability

- [ ] Countries: **India only** for V1 soft launch unless a market checklist exists (plan:
      "Expansion beyond India requires a release checklist for that market").
- [ ] India: Online Gaming Rules grievance mechanism live (`release/operations/grievance-process.md`);
      no registration/determination claims.
- [ ] Prices set in INR for every paid product; tax settings reviewed.
