# AstraKingdoms.Meta: V1 progression and monetization (tickets 57-64)

This is the account meta-game: XP and unlocks, daily cosmetic tasks, cosmetics, the shop, Play
Billing entitlements, ad policy and rewarded-ad verification, analytics and decision metrics,
retention and deletion. The library is netstandard2.1 / C# 9 with no UnityEngine references. It
is also the local Unity package `com.astrakingdoms.meta`. `Server/` has its own asmdef, whose
define constraint (`ASTRA_KINGDOMS_SERVER`) keeps the Google Play and AdMob verification code out
of the Unity client. dotnet compiles it here so the server can host it.

Source of truth: `docs/PLAN.md`. The sections used are "Modes and fair progression", "Make game
monetization measurable…", "Revenue and contribution model", "Metric definitions", "Audience
children photos and data", "Store deletion and purchase readiness" and "Data and artifact
retention defaults". The privacy data map and the Data safety draft are in
[`../../docs/privacy-data-map.md`](../../docs/privacy-data-map.md).

## Layout

| Folder | Contents |
|---|---|
| `Common/` | `IClock` / `ManualClock`, audience (age group, parental consent), the append-only idempotent **reward ledger** (`IRewardLedgerStore`), lenient JSON readers |
| `Progression/` | XP rules, levels, unlock table, `WeaponAccess` (symmetric room catalogues), `ProgressionService`, mastery, `GuestMigrationService`, `MatchReportBuilder` (rules record → report/summary/events) |
| `Economy/` | Daily tasks (`DailyTaskService`, India-midnight day) |
| `Cosmetics/` | Catalogue (data), ownership, equipment, coin shop |
| `Shop/` | Paid store catalogue, shelf presentation, audience purchase rules |
| `Billing/` | Play purchase model, `IPurchaseVerifier`, `PurchaseService`, entitlement ledger, `FakePurchaseVerifier` |
| `Ads/` | `AdPolicy` and catalogue validation, `IRewardedAdProvider`, `RewardedAdService` (offer tickets, reward once) |
| `Analytics/` | Event schema, consent gate, `AnalyticsClient`, event builders |
| `Reporting/` | Wilson intervals, D1/D7/D30 retention, completion, crash-free sessions, matches per active day, event/record reconciliation, revenue/contribution model |
| `Privacy/` | Retention defaults and sweeper, `AccountDeletionService`, erasers, privacy links |
| `Client/` | `IMetaBackend`, `LocalMetaBackend` (offline guest profile), `IStoreBridge` + `FakeStoreBridge`, `ClientPurchaseFlow`, `RewardedAdFlow` |
| `Server/` | `GooglePlayPurchaseVerifier`, `GoogleServiceAccountTokenProvider` (RS256 JWT → OAuth), `SsvSignatureVerifier` + `HttpAdVerifierKeyProvider` (ECDSA P-256), `IHttpTransport` |

## Ticket status

"Done in code" means written and covered by `tests/AstraKingdoms.Meta.Tests` (178 tests). Nothing
here has run against Google Play, an ad network, an analytics vendor, Unity or a phone.

| # | Deliverable | Status | Remaining / blocked |
|---|---|---|---|
| 57 | Levels, mastery/practice unlocks, saved progression | Done in code. 100/+25/+10 XP, practice 50, 300 per level to 20. No XP for automation, developer tests, invalid matches or matches that did not complete normally. One grant per (match result id, player), also under 64-way concurrency. Unlocks come from `WeaponCatalog.UnlockLevel`; levels 17-20 give cosmetics. Room catalogues take no account data. Mastery badges. Capped one-time guest migration. Offline guest profile persisted on the device. | Server DB stores; the online grant call in the match service (server team). Curve tuning from observation. |
| 58 | Daily cosmetic tasks | Done in code. 10 coins per eligible match (+5 for a human win). Three 20-coin tasks. Claims are idempotent, unclaimed tasks expire at India midnight, and a retry after expiry still reports the original claim. | Server endpoints. |
| 59 | Cosmetic catalogue and equipment | Done in code. Three outfits plus bow skins, banners, trails and titles, as data. Reflection, assembly-reference and real-match tests show no combat input is touched. | Art (asset keys only); the arena does not render cosmetics yet. |
| 60 | Shop presentation and eligibility | Done in code. Store-localised prices only (never guessed), listed contents, terms. Child: verified parental consent required; unknown age: age check first. Enforced server-side before the billing flow. | Verifiable parental-consent mechanism (**BLOCKED**). Final coin prices (provisional). Legal wording review. |
| 61 | Play Billing and entitlement verification | Done in code. Products.get / acknowledge / voidedpurchases.list over a service-account JWT. Handles pending, duplicate callbacks, restore/reinstall, voided purchases (paged), the 3-day acknowledgement retry queue with an at-risk alert, test purchases, account binding through `obfuscatedAccountId`, an append-only ledger with corrective transactions, and keeps paid entitlements apart from coins. Unity IAP 4.x adapter (stub-compiled). | **BLOCKED / unverified:** live Play API and licence-tester purchases on a closed track, Real-time Developer Notifications endpoint (server), Unity IAP package install and an editor check of `SetObfuscatedAccountId` / `IsPurchasedProductDeferred`. |
| 62 | Ad policy and conditional remove-ads | Done in code. The validator rejects any remove-ads SKU while ads are rewarded-only, and requires a description of what it removes and whether rewarded offers remain. The entitlement suppresses non-rewarded ads from the server ledger, so it survives reinstalls. | Only matters if non-rewarded ads are ever approved (owner decision). |
| 63 | Optional rewarded ads outside matches | Done in code. Single-use server tickets are never issued in a match. SSV signature verification (DER/P1363, key rotation, replay window). One reward per ticket, also under concurrency. Daily cap. Decline or failure is safe. Child/unknown age: non-personalised, tagged, and only once the SDK stack is confirmed Families-certified. | **BLOCKED:** no ad network or SDK chosen; no live callbacks verified. |
| 64 | Analytics and crash reporting | Done in code. Fixed event schema (the plan's set plus crash). Consent gate (off by default; children off unless assessed). Bot, automation, internal and test flags. Bounded queue. Retention windows exactly as planned, Wilson intervals (match the plan's 6/50, 60/500, 120/1000 figures), completion and crash-free denominators, reconciliation with match records. Unity client instrumentation. | **BLOCKED:** analytics and crash vendors and the collection endpoint. Events stay queued on the device. |

## Server integration points

The server project (`astra-kingdoms/server/`, built separately) can host these services directly.
Everything that touches storage is an interface with an in-memory reference implementation.

**Storage contracts** (implement over the database):

| Interface | Contract |
|---|---|
| `IRewardLedgerStore` | One table, `UNIQUE(idempotency_key)`. `TryAppend` runs in a transaction that locks the player's rows, so the precondition sees the committed totals. A unique violation means `Duplicate`. Never update or delete rows, except `DeletePlayer` during account deletion. |
| `IEntitlementLedgerStore` | `UNIQUE(idempotency_key)`, index on `purchase_token`. Append-only. `Pseudonymise` on account deletion. |
| `IDailyTaskProgressStore` | Row per (player, day). Atomic update (transaction or optimistic version column). |
| `IEquipmentStore`, `IAdTicketStore`, `IAcknowledgementQueue` | Plain keyed tables. |

**Authenticated player endpoints.** The player id always comes from the session, never from the
request body. The list mirrors `Client/IMetaBackend`, so an HTTP client can replace
`LocalMetaBackend` in `unity/Assets/Scripts/Meta/MetaServices.cs`:
`GET profile`, `POST age`, `GET daily-tasks`, `POST daily-tasks/{id}/claim {day}`, `GET locker`,
`POST equip {cosmeticId}`, `GET shop`, `POST coin-purchase {cosmeticId}`,
`POST purchases/authorize {sku}` → `ShopPresenter.AuthorizePaid` (returns the obfuscated account id),
`POST purchases/verify {sku, token}` → `PurchaseService.HandlePurchaseAsync`,
`POST purchases/restore [{sku, token}]` → `RestoreAsync`, `POST ads/offer {context}` →
`RewardedAdService.IssueOffer` (with an `IMatchPresence` backed by the match service),
`GET ads/ticket/{id}`, `POST guest-migration {guestProfileId, summaries}` → `GuestMigrationService.Migrate`,
`POST account/deletion` → `AccountDeletionService.Request`.

**Unauthenticated endpoints.** `GET /ads/ssv?…` runs `SsvSignatureVerifier.VerifyAsync(rawQuery)`.
Only verified callbacks are passed to `RewardedAdService.HandleVerifiedCallback`, and the endpoint
answers 200 to verified duplicates. The web deletion form calls `AccountDeletionService.Request`
with `DeletionChannel.Web`; `ConfirmVerification` is reached through the e-mailed or sign-in link.
The Play RTDN push endpoint calls `HandlePurchaseAsync` (player resolved from the obfuscated id) or
`Revoke`.

**Match service hook.** On every terminal result, for each human seat:
`MatchReportBuilder.FromRecord(record, playerId, side, MatchKind.OnlineHuman | OnlineBot, now, isValid: <server validation>)`,
then `ProgressionService.GrantForMatch` and `DailyTaskService.RecordMatch`. Store
`MatchReportBuilder.Summary(...)` for completion reporting and `EventReconciler`. Repeated
calls are harmless.

**Scheduled jobs.** `PurchaseService.RetryAcknowledgementsAsync` every few minutes (alert on the
returned at-risk list). `ProcessVoidedPurchasesAsync(since)` at least daily.
`AccountDeletionService.ProcessPendingAsync` and `Overdue()` hourly. `RetentionSweeper` nightly per
`DataCategory`. The reporting module runs over exported events and records.

**Configuration and secrets.** The service-account JSON (`ServiceAccountKey.Parse`) and
`BillingOptions.AccountIdSalt` come from the secret store, never from the repo or the client. Also
configure `GooglePlayPurchaseVerifier(packageName)`, `PrivacyLinks` (release validation refuses
placeholders), `AdPolicy`, `AudiencePolicy`, `CollectionPolicy`, `ShopPurchasePolicy`, and
`GuestMigrationPolicy` (caps).

## Decisions made here (proposed; owner may change)

- Only **normal terminal results** (90% early victory or round 8) earn XP or coins. Forfeits and
  aborts earn nothing for either player, which matches the plan's completion definition. Practice
  and labelled-bot matches give a flat 50 XP and 10 coins with no outcome bonus. A shared-phone
  match gives the device profile the human base (100 XP, 10 coins) without a win/draw bonus, because
  the profile owner's seat is unknown.
- Guest migration caps: **1,200 XP (level 5) and 150 coins**, from at most 200 local match summaries
  younger than 90 days, recomputed by the server. Each guest profile can seed only one account, and
  each account receives only one migration.
- Daily-task days reset at **midnight India Standard Time**. Unclaimed tasks lapse at the day's end.
- Coin prices (80-150) are **provisional**. Rewarded ads give 10 coins, capped at 5 per day.
- Development builds (and the editor) count as developer tests and earn nothing, unless
  `MetaServices.GrantInDevelopmentBuilds` is set. Their analytics are flagged *Internal*.
- Child threshold 18 (DPDP once applicable). Unknown age is treated as a child for ads, analytics and
  purchases.
