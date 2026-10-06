# V4 economy discovery — AK-W4-0-proposed

> **Discovery evidence, not a balance or economy approval.** Every world number is PROPOSED (plan: "V4 bounded conquest with a recoverable kingdom"). Players, their activity and encounter outcomes are modelled; every rule decision (reservations, protection table, caps, season close, grants) is made by the real `AstraKingdoms.World` code.

- World rules hash: `8a4425ce31e99f17f6361e8662f244aae60caf2354f4c8e68c9bd0a6b2a16752`
- Run: 400 players, 3 seasons x 18 days, 2 shards, seed 20261006, 12 encounters played as real AK-TR-1 matches, collusion ring of 1 + 6 alts. Runtime 5.1 s.
- Reproduce: `dotnet run -c Release --project tools/AstraKingdoms.EconomySim -- --players 400 --seasons 3 --shards 2 --seed 20261006`

## Verdicts

| Plan evidence item | Result |
| --- | --- |
| No repeatable currency exploit | No probe exceeded the daily cap or repeated a grant (7 probes) |
| Bounded loss | Held: max 2 tiles/day, 6/season (caps 2 and 6) |
| Recovery | Every account back to 12 border tiles after each rebuild; homeland untouched by construction |
| Newcomer progression | 79 newcomers; 0 veteran attacks on newcomer-band accounts; worst first-season close 6 tiles |
| Invariants | No violations |

## Findings for the owner

- The most common reason a challenge attempt failed was `DEFENDER_SEASON_LIMIT` (65% of attempts). With this activity mix most defenders use up the six-loss season allowance well before day 18, so late-season targets become scarce; test with people whether that feels fair or empty before approving the constants.
- Season 1: 331 of 351 accounts ended at the season loss cap; the best honest account held 44 tiles. Losses are bounded but gains are limited only by other accounts' allowances, so tiles must stay cosmetic recognition, never production or power.
- Season 2: 358 of 375 accounts ended at the season loss cap; the best honest account held 39 tiles. Losses are bounded but gains are limited only by other accounts' allowances, so tiles must stay cosmetic recognition, never production or power.
- Season 3: 374 of 400 accounts ended at the season loss cap; the best honest account held 38 tiles. Losses are bounded but gains are limited only by other accounts' allowances, so tiles must stay cosmetic recognition, never production or power.
- A colluding ring (1 main + 6 alts that never defend) reached 30 tiles and the top recognition tier; its coins stayed within the daily cap. The experience-band rule eventually stops the main from farming newcomer-band alts, but this still needs alt detection and moderation.
- Repeatable world income always stopped at the daily cap; one-off grants (season participation) were issued once per key even when settlements were replayed.

## Per season

| Season | Players | Attempts | Accepted | A wins | D wins | Draws | Expired | Max loss/day | Max loss/season | Below 12 at close | Min tiles | Max tiles (honest) | Ring main tiles | Re-entry offers | Migrations |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 351 | 22088 | 4307 | 2009 | 2008 | 193 | 11 | 2 | 6 | 208 | 6 | 44 | 30 (border-marshal-banner) | 208 | 5 |
| 2 | 375 | 25898 | 4576 | 2167 | 2085 | 231 | 11 | 2 | 6 | 228 | 6 | 39 | 29 (border-marshal-banner) | 228 | 11 |
| 3 | 400 | 28329 | 4925 | 2286 | 2282 | 259 | 7 | 2 | 6 | 240 | 6 | 38 | 26 (border-marshal-banner) | 240 | 6 |

Rejected challenge attempts by reason (all seasons): `DEFENDER_SEASON_LIMIT` 49345, `OPPONENT_OUT_OF_RANGE` 9662, `DEFENDER_PROTECTED` 1961, `DEFENDER_DAILY_LIMIT` 1191, `REPEAT_TARGET` 300, `ALLIANCE_DAILY_LIMIT` 48.

## Currency and inequality

| Season | World coins minted | World coins spent | Duel coins minted (A1) | Max world coins one day | Gini world coins | Gini all coins | Top-10% share | Gini tiles at close |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 129145 | 85030 | 109368 | 60 | 0.151 | 0.199 | 0.186 | 0.262 |
| 2 | 139285 | 134650 | 121446 | 60 | 0.155 | 0.223 | 0.201 | 0.256 |
| 3 | 148925 | 144750 | 128472 | 60 | 0.145 | 0.224 | 0.207 | 0.251 |

Coin balances are cumulative across seasons (purchases, cosmetics and coins survive resets by design), so their Gini rises with activity differences; tile Gini resets every season because borders are rebuilt. Repeatable world income is capped at 60 coins per account per UTC day.

## Exploit probes (real rules, adversarial loops)

| Probe | Loop | Iterations | Coins gained | Max coins/day | Extra grants | Verdict | Outcome |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| Tile-trade ring (1 main + 10 alts) | main challenges every alt daily; alts never defend (forfeit) | 180 | 300 | 60 | 0 | bounded | coins/day capped at 60; main ends with 62 tiles (border-marshal-banner) |
| Draw farm (1 + 20 partners) | agreed draws for participation coins | 360 | 170 | 60 | 0 | bounded | participation coins stop at the shared daily cap (60/day) |
| Expiry loop | challenge, never play, let the reservation expire | 50 | 0 | 0 | 0 | bounded | no coins without an authoritative result; every reservation released |
| Resolution replay | resubmit the winning result 100 times and 100 forged results | 200 | 20 | 0 | 0 | bounded | 100 idempotent acknowledgements, one credit, one tile |
| Request retry storm | 1,000 retries of one timed-out request | 1000 | 0 | 0 | 0 | bounded | 1 challenge, 1 reservation |
| Season-close replay | repeat the settlement 20 times | 21 | 120 | 0 | 0 | bounded | season grants issued once |
| Alliance objective grind | one member contributes 500 times | 500 | 0 | 0 | 0 | bounded | 100 counted of 25,000 offered; objective still needs other members |

- **Flag — Tile-trade ring (1 main + 10 alts):** Alts can push one account to the top cosmetic recognition tier. Bounded (tier cap, 6 tiles per alt per season) but needs alt/collusion detection or a per-account gain cap.
- **Flag — Draw farm (1 + 20 partners):** Reaches the same daily ceiling as honest play with more partners; consider lowering participation coins for draws.

## Newcomer progression

- Newcomers (joined after launch): 79; founders: 314.
- Attacks received by newcomers during their first season: 434; from Veteran-band attackers while still Newcomer-band: 0 (the band rule makes this 0).
- Border tiles at their first season close: median 11, minimum 6 (floor is 6).
- Coins held on day 14 of their join season (world + duel): median 82.
- Starter protection (7 days, early exit after 5 training encounters by choice) and season resets mean a newcomer's worst first season still ends with 6 tiles and full access next season.

## Real AK-TR-1 encounter sample

12 challenges were played as real Full-catalog AK-TR-1 matches against the defender's published bot (Normal policy by default): attacker wins 2, defender wins 10, draws 0. The rest used the outcome model below (model attacker win rate 0.476 over 13441 encounters). The sample is too small to calibrate the model; it proves the encounter path runs end to end.

## Modelling assumptions (sim-side, PROPOSED)

| ID | Assumption |
| --- | --- |
| A1 | V1 duel income (outside the world): 10 coins per win, 4 per loss, at most 50 per day. The real V1 reward table is owned elsewhere. |
| A2 | Offline defence plays at a fixed Elo-equivalent of 1000; a defender is online 30% of the time and then uses their own skill. |
| A3 | Outcomes: Elo expectation on hidden skill (mean 1000, sd 150), 5% draws, 2% of accepted challenges abandoned (they expire). |
| A4 | Activity mix 55% casual (40% daily presence, 1 attack try), 35% regular (80%, 2), 10% grinder (100%, 4); 20% of players arrive after launch. |
| A5 | Spending: 40% chance per sink per active day when affordable (world coins), 25% from duel coins. |
| A6 | 30% of players join alliances; 2% request a shard migration each season. |
| A7 | Armies are snapshotted but effect-free (role table: Scout 2pt x2, Guard 3pt x2, Banner 1pt x2, Vanguard 4pt x1, Engineer 2pt x2; 10 points). |

## What this does not establish

- Real player behaviour, fun, perceived fairness or harassment patterns (human fairness tests).
- The outcome model's calibration against real encounters (needs many real AK-TR-1 encounters and live data).
- Load and storage behaviour of shards (a declared load scenario on real infrastructure).
- Army role effects: none are implemented; each needs its own simulation and rules approval.
