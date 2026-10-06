# Known-issues register (ticket 82)

Parsed by `AstraKingdoms.Release release-record --known-issues`: rows whose first cell is
`KI-###`; columns ID | Title | Severity | Status | ... Severity: `release-blocker`, `major`,
`minor`. Status: `open`, `mitigated`, `closed`. An **open release-blocker** fails the release
record. Keep player-visible wording in the "Player note" column for store "What's new" and
support macros.

| ID | Title | Severity | Status | Affects | Workaround / player note | Owner | Opened | Closed |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| KI-001 | Hindi and Kannada text cannot render: legacy uGUI Text has no Devanagari/Kannada glyphs or shaping | release-blocker | open | hi, kn builds | Ship English only until the TextMesh Pro + Noto path is verified on the reference phone | client | 2026-10-06 | |
| KI-002 | All art, music and sound are generated placeholders; no licensed or approved asset exists | release-blocker | open | store release | Allowed for internal/closed tests only (`ledger-validate` WARN; `--require-approved` FAIL) | art | 2026-10-06 | |
| KI-003 | Package id mismatch with Forge project config (`com.rivailabs.astrakingdoms` vs `com.astrakingdoms.game`) | major | open | Forge device-smoke | Align `game-forge/projects/astra-kingdoms/project.toml` (owner-protected) | owner | 2026-10-06 | |
| KI-004 | No reference phone registered; no device performance, memory or sustained-play evidence | release-blocker | open | all releases | Acquire phones, fill `release/perf/devices.json`, run the scenarios | owner | 2026-10-06 | |
| KI-005 | Ad, analytics and crash vendors not chosen; Data safety gate INCOMPLETE | release-blocker | open | store release | Choose vendors, update `sdk-inventory.json`, capture release-build traffic | owner | 2026-10-06 | |
| KI-006 | Verifiable parental-consent mechanism missing; children cannot make paid purchases | major | open | child audience | Purchases refused for children (by design until implemented) | meta | 2026-10-06 | |
| KI-007 | Policy URLs (privacy, deletion, grievance) are placeholders (`example.invalid`) | release-blocker | open | store release | Set real HTTPS URLs; `PrivacyLinks.ValidateForRelease()` must pass | owner | 2026-10-06 | |
| KI-008 | Automation report aggregates frame times over the whole run; thermal degradation measured against a cold run instead of per segment | minor | open | ticket 75 evidence | Use `sustained-collate --cold-report`; per-match frame stats proposed for the client | client | 2026-10-06 | |
| KI-009 | Unity stubs for importer/build-report APIs are hand-written; first editor compile must confirm them | major | open | Editor/Release scripts | Open the project in the pinned editor and fix any signature differences | client | 2026-10-06 | |
