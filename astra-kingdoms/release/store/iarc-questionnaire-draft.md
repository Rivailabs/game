# IARC content rating questionnaire: draft answers (ticket 78)

> **DRAFT for owner and legal review.** Play Console's IARC questionnaire wording changes; answer
> the live form, using these drafts as the reasoning record. Re-answer whenever content changes
> (new weapon effects, blood, chat, user-generated content, purchases, ads, location).
> Category to choose: **Game**. The rating is issued by IARC per region (ESRB, PEGI, USK,
> ClassInd, ACB, Generic for India and others); record each issued rating in the release
> decision record.

| Topic | Draft answer | Basis in the build | Evidence to attach |
| --- | --- | --- | --- |
| Violence | Yes: **fantasy violence** against fictional humanoid characters; no blood, gore, dismemberment or death animation beyond a "defeat" pose. | Archers shoot elemental arrows; HP bars; defeat and victory poses (brief 65, 67). | Gameplay video of the candidate. |
| Realistic violence / human targets | Characters are stylised fantasy archers, not realistic humans; no injury detail. | Art briefs forbid gore. | Screenshots. |
| Blood | No. | Effects are element particles only. | Effect sheet (brief 67). |
| Sexual content / nudity | No. | Outfits are full costumes. | Outfit sheet. |
| Crude humour / language | No. | All text in localisation tables; no profanity. | `en.txt`. |
| Fear / horror | No. | | |
| Drugs, alcohol, tobacco | No. | | |
| Gambling: simulated | **No.** No casino games, betting, loot boxes or random paid rewards. Card draws are free, deterministic from the match seed and grant no purchasable value. | Shop sells named cosmetics only (`StoreCatalog`). | Shop screenshots. |
| Gambling: real money | **No.** No stakes, no prizes of real-world value. | Plan "India online gaming"; store lint `money.prizes`. | |
| Users interact / communicate | V1 has **no chat** and no user-generated content. Online friend rooms (if in the candidate) let two players play together via a room code. Answer "users can interact" = Yes only if online play ships. | Online layer (tickets 49-56). | Friend-room screenshots. |
| Shares location | No. | Data map: location not collected. | Data safety draft. |
| Digital purchases | **Yes** (if Play Billing ships): optional cosmetic purchases. | `StoreCatalog`. | |
| Ads | Yes (if the rewarded-ad SDK ships): optional rewarded ads outside matches. | `AdPolicy`. | |
| Unrestricted internet / web view | No. | | |
| Religious or cultural content | Fantasy setting inspired by Indian epic art; no worship, no deities as fighters. | Cultural review checklist. | Signed checklist. |

## Target audience and content (Play "Target audience" section)

Decided **before** choosing SDKs (plan). Current code assumes children may be present
(`AudiencePolicy.ChildrenInTargetAudience = true`). If the target audience includes under-13s,
Families policy applies: Families self-certified ad SDK and mediation adapters, non-personalised
ads, teacher-approved programme not required. **Owner decision needed:** target age groups
(e.g. 13-15, 16-17, 18+ only, or including under-13). Record it in
`release/operations/release-decision-record-template.md` and keep the store declaration identical.
