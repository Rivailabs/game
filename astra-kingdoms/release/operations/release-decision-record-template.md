# Release decision record (ticket 82)

Plan: "Technical pass, visual approval, integrated acceptance and release approval remain distinct
fields." "The owner approves that combination. A later source or asset change creates a new
candidate and invalidates approvals affected by the change." Copy once per candidate.

## Candidate

| Field | Value |
| --- | --- |
| Candidate hash (release-record / Forge package) | |
| Source commit | |
| Version name / code | |
| Rules version / hash | |
| Economy / balance bundle | |
| Artifact sha256 (unsigned) / signed sha256 / certificate fingerprint | |
| Release record file + sha256 | |
| Track and audience | internal / closed / production staged (users: ___) |
| Scope (features in this build) | |
| Excluded features (and store text checked) | |

## Gate decisions (each field separately)

| Gate | Result | Evidence file + sha256 | Decider | Date |
| --- | --- | --- | --- | --- |
| Technical pass (tests, compile, editor gate) | | | | |
| Correctness (golden replays, area conservation, no duplicate grants) | | | | |
| Device: frame pacing (both reference phones) | | | | |
| Device: memory / sustained play | | | | |
| Download size (<= 80 MB, device spec named) | | | | |
| Connectivity (lock, timeout, reconnect, result recovery) | | | | |
| Accessibility (colour-independent, text scale, audio/reduced motion) | | | | |
| Reproducible build / signing | | | | |
| Asset provenance (ledger, exceptions) | | | | |
| Visual approval (art at gameplay camera) | | | | |
| Cultural review | | | | |
| Store page / content rating / Data safety | | | | |
| Closed-test eligibility (12 x 14 days) | | | | |
| Restore rehearsal (last drill date) | | | | |
| Support coverage and grievance route live | | | | |
| India Online Gaming Rules check (notifications, features, no prizes) | | | | |
| DPDP position refreshed for the release date (legal) | | | | |

## Known issues accepted for this release

| KI | Why acceptable now | Mitigation | Revisit by |
| --- | --- | --- | --- |

## Decision

- [ ] APPROVED for signing and the stated track/audience.
- [ ] NOT APPROVED. Reasons:
- Owner: ______ (approval key fingerprint ______) Date (UTC): ______
- Release authority (track/rollout): ______ Date: ______
- Correction reason (if this record corrects an earlier one): ______
