# Store screenshot manifest (ticket 72)

Every store image must be reproducible on the release candidate it advertises (plan: "Captures
match the shipped game and supported languages"). Fill one row per image; add the files to the
asset ledger as `texture.store.screenshot.<lang>.<n>` with their hashes.

| # | Shot (brief AK-ART-072-C) | Language | Candidate build ID | Device | Capture method | File | sha256 | Reviewer |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Duel selection with aim preview | en | | | `adb exec-out screencap -p` | | | |
| 2 | Simultaneous reveal and clash | en | | | | | | |
| 3 | Element counter explanation | en | | | | | | |
| 4 | Card choice and finger cut | en | | | | | | |
| 5 | Territory result | en | | | | | | |
| 6 | Tutorial step | en | | | | | | |
| 7 | Locker (cosmetic only) | en | | | | | | |
| 8 | Friend room (only if online ships) | en | | | | | | |

Status: **no captures yet** (needs the approved art, a release candidate and the reference phone).
