# Astra Kingdoms V1: privacy data map, Data safety draft and deletion process

> **DRAFT. Needs legal review before any store submission or public release.**
> This document describes what the code in `src/AstraKingdoms.Meta` and `unity/Assets/Scripts/Meta`
> does on 2026-10-06. It is not legal advice and it is not a privacy policy. Several vendors (analytics,
> crash reporting, ad network) and the online server are not chosen or not finished, so every row
> marked **(vendor TBD)** or **(server)** must be re-checked against the shipped build. The plan says
> published statements must describe the implementation actually shipped (plan: "Audience children
> photos and data"; "Store deletion and purchase readiness"; "Data and artifact retention defaults").

## 1. Data map

Retention periods are the plan's *proposed operational defaults*, not statutory periods. They are
also encoded in `Privacy/Retention.cs` (`RetentionDefaults`), so the server's nightly sweep and this
table stay in step.

| # | Data | Fields (and what is deliberately not collected) | Purpose | Where it goes | Retention (proposed) | Deletion |
|---|------|------|---------|---------------|----------------------|----------|
| 1 | Guest profile on the device | Random guest id; XP/coin ledger lines (keys contain match ids); daily-task progress; equipped cosmetics; age group (adult/child/unknown, **not** the age or birth date); parental-consent state; consent switches; last ≤ 200 local match summaries (id, mode, result, end reason, time) | Offline progression, cosmetics, settings | Device only: `persistentDataPath/meta/guest-profile.json` | Until the player deletes it or uninstalls | In-app "Delete my data" (two taps) removes the file and starts a new guest id. Uninstall removes it. |
| 2 | Account id **(server)** | Account identifier from authentication (ticket 49) | Sign-in, ownership of progress and purchases | Game server | Life of the account | Account deletion (section 3) |
| 3 | Progression ledger **(server)** | Account id, idempotency key (match result id / task / ad ticket), XP and coin deltas, cosmetic id, weapons used (for mastery), time | Grant XP and earned coins once per match; daily tasks; coin purchases; corrections | Game server database | Life of the account | Erased on account deletion (`MetaErasers.RewardLedger`) |
| 4 | Guest migration marker **(server)** | Guest profile id → account id, time | Enforce the one-time capped migration | Game server | Life of the account | Erased with the ledger |
| 5 | Daily-task progress **(server)** | Account id, day, counted match ids, elements used, practice events | Daily tasks | Game server | 30 days | Swept; erased on account deletion |
| 6 | Cosmetic equipment **(server)** | Account id, slot → cosmetic id | Appearance | Game server | Life of the account | Erased on account deletion |
| 7 | Purchase / entitlement records | Account id (pseudonymised after deletion), SKU, Play purchase token, order id, grant/revoke action, reason, time, test-purchase flag. **No** card or payment details: Google Play handles payment. The billing flow sends Play a salted SHA-256 of the account id (`obfuscatedAccountId`), never the raw id. | Verify purchases before granting, restore on reinstall, process refunds/chargebacks, accounting, tax and fraud | Game server; Google Play (Play Developer API reads) | **Decision needed** with accounting/tax/fraud advice; keep only justified fields | On account deletion the lines stay but the account id is replaced by a pseudonym (`MetaErasers.Entitlements`). Disclose this. |
| 8 | Rewarded-ad offer tickets and callbacks **(server)** | Ticket id, account id, issue/expiry time, ad-network transaction id, request flags | Grant a verified reward once; disputes | Game server | 30 days (support hold allowed) | Swept; erased on account deletion |
| 9 | Ad requests **(vendor TBD)** | Whatever the chosen SDK collects (typically advertising ID, device and app info, IP). We send: non-personalised flag, child-directed / under-age tags, max rating, the ad-offer ticket id and the account id as SSV data. | Show an optional rewarded ad outside matches | Ad network and its mediation adapters | Vendor's policy | Vendor process; must be listed in the Data safety form. **No SDK is integrated yet.** |
| 10 | Product analytics **(vendor TBD)** | Random analytics id (not the account id, resettable), random session id, event type, time, short tokens and integers from the fixed schema (`Analytics/AnalyticsSchema.cs`), traffic flags (bot/automation/internal/test), cohort source. **No** names, free text, chat, aim inputs, weapon choices before reveal, locations or contacts: the schema validator rejects anything that is not a short token or integer. | The plan's decision metrics: retention, completion, crash-free sessions, matches per active day, purchase and ad-reward funnels | Collection endpoint **not chosen**: events stay in a bounded on-device queue (≤ 500) and are discarded on consent withdrawal | Raw events 90 days; aggregate reports longer | Consent off → queue cleared; deletion resets the analytics id; provider-side deletion via the provider's API (an `IDataEraser`) |
| 11 | Crash reports **(vendor TBD)** | Crash flag, critical flag, stage (startup/menu/match/background), session id; vendor stack traces | Stability, crash-free-session metric | Crash vendor **not chosen** | 90 days | Consent off stops collection; vendor deletion via `IDataEraser` |
| 12 | Match diagnostic records **(server)** | Seed, command log, results (no personal data beyond account ids of the seats) | Replay verification and support | Game server, access restricted | 30 days, support-case hold allowed | Swept; erased on account deletion |
| 13 | Support and grievance records **(server/ops)** | Contact route, request, outcome | Grievance mechanism (India Online Gaming Rules 2026, rule 20) | Operator | Proposed 365 days; **needs legal review** | Per legal advice |
| 14 | Photos / avatars | **Not collected in V1.** Stock choices only. | - | - | - | Photo avatars need their own privacy and moderation gate (plan). |

## 2. Google Play Data safety form: draft answers

Draft for the V1 build with online accounts, Play Billing, optional analytics and crash reports, and a
Families-certified rewarded-ad SDK. **Re-answer after every SDK change and verify against the release
build's real network traffic** (plan: "Data safety declarations reflect actual SDKs and data flows").

| Play category → type | Collected? | Shared? | Optional? | Purposes | Notes |
|---|---|---|---|---|---|
| Personal info → User IDs | Yes (account id) | No | Required for online play | App functionality, account management | Guest play needs no account. |
| Personal info → Other info (age group) | Yes (adult/child/unknown only) | No | Optional (can skip: "prefer not to say") | App functionality, compliance | No birth date is stored. |
| Financial info → Purchase history | Yes | No (Google is the payment processor) | Only if the player buys | App functionality, fraud prevention, compliance | Payment details are never received. |
| App activity → In-app interactions / other actions | Yes (schema events) | **Vendor TBD**: "shared" if the analytics vendor is not a service provider | Optional (consent switch, default off; off for children unless an assessment approves) | Analytics | Pseudonymous analytics id. |
| App info and performance → Crash logs / diagnostics | Yes | **Vendor TBD** | Optional (separate consent switch) | Analytics (stability) | |
| Device or other IDs | Analytics id (ours); advertising ID **if the ad SDK collects it** | Ad network (if any) | Ads are optional | Advertising, analytics | Children/unknown age: non-personalised requests only. |
| Location, contacts, photos, audio, messages, health, web history | **No** | - | - | - | Keep it that way unless a new feature passes review. |

Security practices: data is encrypted in transit (HTTPS) **to be verified on the release build**; users
can request deletion in the app and on the web; Families policy commitment applies if children are in
the target audience.

## 3. Account and data deletion process

Play requires, when accounts exist, a **discoverable in-app path** and an **external web resource**,
and that associated data is deleted (clearing local saves is not enough).

**Entry points.**
- In-app: Home → Profile and shop → Privacy and account → Delete my data (`PrivacyLinks.InAppPath`). Today, with
  only the offline guest profile, this deletes the device profile immediately.
- Web: `PrivacyLinks.AccountDeletionUrl`. Still a placeholder (`example.invalid`):
  `PrivacyLinks.ValidateForRelease()` fails until the operator sets real HTTPS URLs for deletion,
  privacy policy and grievances.

**Server flow** (`Privacy/AccountDeletionService`, hosted by the server):

1. **Request.** An in-app request with a valid session starts at once. A web request, or an in-app
   request with an expired session, waits in *AwaitingVerification* until the requester proves
   ownership (a sign-in or e-mailed link). A stranger cannot delete someone else's account. There is
   only one open request per account.
2. **Erase.** Every registered `IDataEraser` runs: progression ledger, daily tasks, equipment, ad
   tickets, purchase records (pseudonymised, *RetainedJustified*), and the server's own stores
   (sessions, friend rooms, match diagnostic records, which the server team registers), plus
   provider deletion APIs (analytics, crash, ad vendor where applicable).
3. **Partial completion.** If a provider is unreachable, the request becomes *PartiallyCompleted*.
   `ProcessPendingAsync` retries only the unfinished steps. `Overdue()` lists requests past the
   published window (proposed 30 days) for the operator.
4. **Complete.** The request is *Completed* when every step is *Erased* or *RetainedJustified*.
   The player is told what was retained and why (purchase records) and when backups expire.

Cases the plan requires, with their tests in `AstraKingdoms.Meta.Tests/PrivacyAndClientTests.cs`:
an absent app (web channel), an expired session, a partially completed request, a provider outage,
and an account with no data.

**Backups (open).** Active data is deleted at once. Backup copies age out with the backup rotation
(**period to be chosen and published**). Provider-held copies follow each provider's deletion
process. A successful local deletion does not prove that every external copy is gone (plan).

## 4. Children and audience notes

- **Decide the audience before choosing SDKs** (plan). The code assumes children may be present:
  `AudiencePolicy.ChildrenInTargetAudience = true` and a child threshold of 18
  (`ChildBelowAge`), which matches DPDP's definition once its child obligations apply. That is
  phased to **May 2027** per the plan's reading; refresh before launch. Families uses its own age
  definitions. **Legal review must confirm the threshold(s) and which flows apply on the release date.**
- **Neutral age screen.** The Privacy screen asks for an age with − / + steps and "Prefer not to say".
  Only the age group is stored. Unknown age is treated like a child for ads and analytics, and must
  answer before any paid purchase.
- **Purchases.** Children need *verified* parental consent before any paid purchase
  (`ShopPurchasePolicy`); refused consent blocks paid offers. **BLOCKED:** no verifiable
  parental-consent mechanism exists yet, so in practice children cannot make paid purchases. Earned
  coins are not restricted (no money involved).
- **Ads.** Children and unknown age receive offers only after the owner confirms that the ad SDK
  *and every mediation adapter* are Families self-certified (`AdPolicy.FamiliesCertifiedSdkConfirmed`,
  default false). Their requests are non-personalised, tagged under-age (child-directed for children)
  and limited to G-rated ads. No ads ever appear during a match. There is no remove-ads product while
  ads are rewarded-only.
- **Analytics.** Off until the player opts in. For children and unknown age it stays off even with
  consent, until an applicable-law assessment approves it (`CollectionPolicy`). The plan warns that
  consent does not automatically permit behavioural tracking of children.
- **Photos.** None in V1.

## 5. Open decisions blocking release

1. Analytics, crash and ad vendors. Each must be added to rows 9-11 and the Data safety form.
2. Purchase-record retention period and the exact retained fields (accounting/tax advice).
3. Backup rotation period and the published completion window.
4. Real HTTPS URLs for deletion, privacy policy and grievances.
5. Verifiable parental-consent mechanism (or a decision that children cannot buy).
6. Child threshold(s) and target-audience declaration in Play Console.
7. Whether licence-tester purchases are accepted in production (`BillingOptions.AcceptTestPurchases`).
