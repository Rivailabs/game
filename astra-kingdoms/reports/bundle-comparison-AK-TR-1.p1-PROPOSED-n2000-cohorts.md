# PROPOSED balance bundle `AK-TR-1.p1` versus `AK-TR-1`

> **PROPOSED - NOT ADOPTED.** This is a bot-policy screening comparison of a candidate balance release. Nothing here is live: the bundle changes outcomes only for matches pinned to it, and it is pinned only after a release authority publishes it to a balance channel (ticket 24). Bot results are screening evidence, not balance proof; human playtests decide.

## Candidate bundle

- Bundle ID: `AK-TR-1.p1` (derived from `AK-TR-1`)
- Content hash: `54419911ea184d6e53c4f548df69c45ee0745649c275be009abf5110518fe9fc`
- Effective rules hash: `9189338cb3106512292de1ab076689d4f753d11e96149a6148a5249e2e0f2a71` (baseline `e33d84b914ae8ec900c60448be7c7f9e63e08db3657bb2b235580d29917013eb`)
- Notes: PROPOSED, NOT ADOPTED. Bot-screening response to reports/balance-review-AK-TR-1-n2000-cohorts.md: River heal 10->4; Agni damage trimmed (Ember, Fire Fan, Ash Shield, Sun Lance); Stone Arrow, Cyclone, Mist Veil, Boulder, Quake, Flood, Tide raised.

| Tunable | Baseline | Proposed | Change | Unit |
|---|---:|---:|---:|---|
| `Damage.RiverHealUnits` | 1000 | 400 | -60.0% | HP/100 |
| `Weapon.1.DamageUnits` | 3000 | 2600 | -13.3% | HP/100 per projectile |
| `Weapon.10.DamageUnits` | 2000 | 3000 | +50.0% | HP/100 per projectile |
| `Weapon.11.DamageUnits` | 1500 | 1000 | -33.3% | HP/100 per projectile |
| `Weapon.12.DamageUnits` | 2200 | 3200 | +45.5% | HP/100 per projectile |
| `Weapon.15.DamageUnits` | 3200 | 3600 | +12.5% | HP/100 per projectile |
| `Weapon.16.DamageUnits` | 4000 | 3600 | -10.0% | HP/100 per projectile |
| `Weapon.18.DamageUnits` | 3000 | 3400 | +13.3% | HP/100 per projectile |
| `Weapon.3.DamageUnits` | 3500 | 4300 | +22.9% | HP/100 per projectile |
| `Weapon.5.DamageUnits` | 3000 | 3300 | +10.0% | HP/100 per projectile |
| `Weapon.6.DamageUnits` | 1200 | 1000 | -16.7% | HP/100 per projectile |
| `Weapon.8.DamageUnits` | 4500 | 5000 | +11.1% | HP/100 per projectile |

## Run

- Baseline Rules: `AK-TR-1`, rules hash `e33d84b914ae8ec900c60448be7c7f9e63e08db3657bb2b235580d29917013eb`
- Proposed Rules: balance bundle `AK-TR-1.p1` on `AK-TR-1`, effective rules hash `9189338cb3106512292de1ab076689d4f753d11e96149a6148a5249e2e0f2a71`, bundle content `54419911ea184d6e53c4f548df69c45ee0745649c275be009abf5110518fe9fc`
- Config: AK-TR-1.p1 Online Full cards=V1 terrain=AK-TR-1/full-01
- Matches per column: 2004 / 2004 completed, 0 / 0 errors; base seed 20261006; 4 threads; 42.8 s + 38.1 s
- Pairings (mirrored pairs: same seed, policies swap seats): Hard v Hard, Normal v Normal, Easy v Easy, Hard v Normal, Hard v Easy, Normal v Easy
- Unlock cohorts ON: each seat gets an account level from {1, 4, 8, 12, 16, 20} and equips only weapons unlocked at that level (familiarity assumption)
- Both columns replay the same seeds, pairings and seat swaps; paired matches differ only in their pinned parameters.
- Win shares are among decisive outcomes; intervals are Wilson 95%; status uses the same Bonferroni family rule as the screening report.

## Match shape

| Metric | Baseline | Proposed | Delta |
|---|---:|---:|---:|
| Reached the final round | 85.9% (1722/2004) | 84.2% (1688/2004) | -1.7 pp |
| Ended by territory victory | 16.7% (335/2004) | 19.0% (381/2004) | +2.3 pp |
| Duels drawn | 2.5% (378/15315) | 2.4% (357/15191) | -0.1 pp |
| First attacker wins (mirror, decisive) | 47.1% (470/998) | 49.0% (488/996) | +1.9 pp |
| Trailing < 35% after round 4 comes back (mirror, decisive) | 3.7% (5/136) | 1.3% (2/151) | -2.4 pp |

Paired matches whose final result or cell count changed: **1995** of 2004.

## Screening flags: baseline versus proposed

FLAG count: **8 → 4** (4 resolved, 0 introduced). Rows show every comparison that is flagged or on watch in either run; the rest are consistent with their band in both.

| Kind | Key | Band | Baseline | Status | Proposed | Status | Delta |
|---|---|---|---:|---|---:|---|---:|
| element | Agni | 47.0%-53.0% | 63.3% (8104) | FLAG | 63.0% (7113) | FLAG | -0.4 pp |
| element | Prithvi | 47.0%-53.0% | 36.6% (7222) | FLAG | 39.8% (7426) | FLAG | +3.2 pp |
| element | Vidyut | 47.0%-53.0% | 55.6% (6369) | FLAG | 55.0% (5436) | watch | -0.6 pp |
| element | Varuna | 47.0%-53.0% | 44.0% (5433) | FLAG | 44.2% (5916) | FLAG | +0.2 pp |
| weapon (duel, used) | 3 Stone Arrow | 45.0%-55.0% | 41.5% (3327) | FLAG | 43.7% (3445) | ok | +2.2 pp |
| weapon (duel, used) | 10 Mist Veil | 45.0%-55.0% | 35.7% (1224) | FLAG | 38.5% (1321) | FLAG | +2.8 pp |
| weapon (duel, used) | 11 Ash Shield | 45.0%-55.0% | 59.0% (1036) | watch | 56.2% (1006) | ok | -2.8 pp |
| weapon (duel, used) | 12 Cyclone | 45.0%-55.0% | 38.6% (1123) | FLAG | 45.8% (1412) | ok | +7.2 pp |
| weapon (duel, used) | 17 Sky Dive | 45.0%-55.0% | 40.7% (678) | watch | 44.5% (721) | ok | +3.8 pp |
| terrain defender | River | 45.0%-55.0% | 75.6% (679) | FLAG | 59.9% (756) | watch | -15.6 pp |

## Reading notes

- Element multipliers (150% / 50%) and abilities are structural in AK-TR-1 and cannot be changed by a balance bundle; element totals can only move through the damage of that element's weapons. Element flags that persist need a rules-version change, not a bundle.
- Bots aim from a solver and pick weapons by fixed heuristics; Hard bots read tuned damage from the pinned parameters, so usage shifts are part of the result.
- A bundle that clears bot flags still needs human playtests (ticket 25 ingest with `--bundle`) before any publication decision.
- Re-run: `dotnet run -c Release --project tools/AstraKingdoms.Sim -- --matches 2000 --cohorts --bundle <file> --compare`.
