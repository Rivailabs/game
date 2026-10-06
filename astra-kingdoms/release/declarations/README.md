# Privacy and store declarations (ticket 77)

> **Needs legal review.** Nothing here is legal advice. Declarations must describe the build that
> ships, verified with a traffic capture of that build.

| File | Purpose |
| --- | --- |
| `privacy-data-map.json` | Machine-readable form of `docs/privacy-data-map.md` section 1 (`AK-PRIVACY-DATA-MAP/1`). Keep the two in step. |
| `sdk-inventory.json` | Every SDK/service in the Android build with the Play data types it sends and its traffic capture (`AK-SDK-INVENTORY/1`). |
| `data-safety-draft.md` | Generated: `AstraKingdoms.Release data-safety --data-map ... --sdk-inventory ... --md-out ...`. Do not edit by hand. |
| `consent-flow-checklist.md` | Device checklist proving actual SDK/network behaviour matches the disclosures and consent choices. |

Current gate result: **INCOMPLETE** (ad, analytics and crash vendors not chosen; billing and the
online server not integrated; no release-build traffic capture). The generator refuses to call the
draft complete until those exist.
