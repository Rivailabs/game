# Privacy implementation and consent-flow checklist (ticket 77)

Acceptance (plan): *Actual SDK/network behaviour agrees with disclosures and consent choices.*
**Needs legal review.** Run on the **release build** of the candidate, on the reference phone,
with a traffic capture (e.g. mitmproxy with a user CA on a test device, or the vendor's debug
view). Record every capture file in `sdk-inventory.json` (`traffic_capture`) and regenerate the
Data safety draft (`AstraKingdoms.Release data-safety`).

Candidate: ________  Tester: ________  Date: ________  Capture files: ________

## A. Fresh install, no choices made

- [ ] No network request before the first screen except what the data map allows (game server
      session for online play; nothing for offline guest play).
- [ ] Analytics queue stays on the device (consent default **off**); no analytics or crash
      endpoint contacted.
- [ ] No advertising-ID read before an ad is requested (check the ad SDK init settings).

## B. Age screen

- [ ] Neutral age screen offers "Prefer not to say"; only the age group is stored (inspect
      `guest-profile.json`).
- [ ] Unknown age behaves as a child for ads and analytics.
- [ ] Child: no paid purchase without verified parental consent (today BLOCKED: no mechanism, so
      purchases are refused for children - confirm the shop shows that).

## C. Consent switches (Privacy and account screen)

- [ ] Analytics on (adult): events are sent; captured payload contains only schema tokens and
      integers (no names, free text, aim inputs, contacts, location).
- [ ] Analytics off: queue cleared on the device; no further requests.
- [ ] Crash reporting switch independent of analytics.
- [ ] Children / unknown age: analytics stays off even with the switch on (unless an assessment
      approved it).

## D. Ads (if the ad SDK ships)

- [ ] No ad during a match, ever.
- [ ] Adult: request flags as configured; child/unknown: non-personalised, under-age tagged,
      G-rated, and offers appear only when `AdPolicy.FamiliesCertifiedSdkConfirmed` is true.
- [ ] Mediation adapters in the build are each Families self-certified (list them here).
- [ ] Reward granted once via server-side verification (SSV); offline replay of the callback refused.

## E. Purchases (if Play Billing ships)

- [ ] `obfuscatedAccountId` is the salted hash, never the raw account id (capture the Play flow).
- [ ] Pending, cancelled, restored and refunded purchases behave as in `BillingTests`.

## F. Deletion

- [ ] In-app path: Home > Profile and shop > Privacy and account > Delete my data works in two taps.
- [ ] Web deletion URL reachable from a phone outside the office network; ownership is verified
      before deletion; the player is told what is retained (purchase records, pseudonymised) and
      when backups expire.
- [ ] Provider deletions (analytics, crash, ads) issued and recorded; partial completion retried.

## G. Disclosures match

- [ ] Every host contacted in A-F belongs to an SDK in `sdk-inventory.json`.
- [ ] Every data type observed is declared in `privacy-data-map.json` and in the Data safety draft.
- [ ] Privacy policy text describes this build (not a planned one).
