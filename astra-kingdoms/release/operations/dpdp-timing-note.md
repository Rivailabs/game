# DPDP Act phased timing: release planning note

> **NEEDS LEGAL REVIEW. Not legal advice.** Restates the plan's reading as of 5-6 October 2026
> ("Audience children photos and data" [S22-S24]); refresh before every public release.

## Plan's position

- DPDP implementation is **phased**. The November 2025 commencement notification places the
  substantive obligations, including sections 3-17 and the child-data obligations, in an
  **18-month phase: May 2027**. The corresponding rules on notice, security, rights and verifiable
  parental consent are similarly phased.
- As of 5 October 2026 it is **inaccurate to claim every substantive requirement is already
  operative**. Other currently applicable obligations (other laws, Play policy, contracts) are
  **not suspended** by this transition.
- When applicable, the child threshold is **under 18**; parental-consent obligations concern
  covered personal data generally (not only photos); tracking/behavioural monitoring and targeted
  advertising directed at children are restricted, subject to exemptions.

## What the build does now (design against the release-date requirements)

| Topic | Implementation | File |
| --- | --- | --- |
| Child threshold | 18 (`AudiencePolicy.ChildBelowAge`) | `src/AstraKingdoms.Meta/Common/Audience.cs` |
| Unknown age | Treated as a child for ads and analytics | `AudiencePolicy` |
| Analytics for children | Off even with consent until an applicable-law assessment approves | `CollectionPolicy` (`Analytics/AnalyticsClient.cs`) |
| Paid purchases by children | Require verified parental consent; none exists, so refused | `ShopPurchasePolicy` (`Shop/ShopPresenter.cs`) |
| Ads for children | Non-personalised, under-age tagged, G-rated; only with Families-certified SDK and adapters | `AdPolicy` (`Ads/AdPolicy.cs`) |
| Deletion and rights | In-app + web deletion, provider erasers, partial-completion retries | `Privacy/AccountDeletion.cs` |

## Decisions for counsel before each release

1. Which obligations are in force on the planned release date (Act sections, Rules, any amended
   notification after October 2026)?
2. Is the age-gate + "children cannot buy" approach sufficient until verifiable parental consent
   exists, for the declared target audience?
3. Notice content and languages (English, Hindi, Kannada) for the release date.
4. Retention periods for purchase, support and grievance records.
5. Whether the closed test (adults only) needs a different notice than the public release.

Record the answers, reviewer and date in the release decision record.
