# Astra Kingdoms bot-policy screening report

> **These are bot-policy screening results, not balance proof.** Outcomes reflect three scripted policies (Easy/Normal/Hard) with fixed heuristics; humans will choose weapons, aim and cuts differently. Use them to spot structural problems and to decide what human tests to run, not to tune constants on a single aggregate.

## Run

- Rules: `AK-TR-1`, rules hash `e33d84b914ae8ec900c60448be7c7f9e63e08db3657bb2b235580d29917013eb`
- Config: AK-TR-1 Online Full cards=V1 terrain=AK-TR-1/full-01
- Matches: 2004 completed, 0 errors; base seed 20261006; 4 threads; 61.3 s
- Pairings (each played as mirrored pairs: same seed, policies swap seats): Hard v Hard, Normal v Normal, Easy v Easy, Hard v Normal, Hard v Easy, Normal v Easy
- Bots receive only their private view; all commands go through the authoritative engine (any rejection is an error).
- Win shares are among decisive outcomes (draws excluded, but counted); intervals are Wilson 95%.
- Like-for-like tables (weapons, elements, first attacker, terrain) use the 1002 mirror-policy matches only.

## Match length and terminal reasons

| Reason | Matches | Share |
|---|---:|---:|
| Territory90 | 407 | 20.3% |
| RoundsComplete | 1597 | 79.7% |

Round-eight frequency (match reached round 8): **83.3%** (1670/2004, Wilson 81.6% - 84.9%).
Match draws (equal cells after round 8): 0 (0.0%).

| Rounds played | Matches |
|---:|---:|
| 3 | 18 |
| 4 | 64 |
| 5 | 80 |
| 6 | 77 |
| 7 | 95 |
| 8 | 1670 |

## Policy strength (difficulty differences are observable)

Row policy's match result against the column policy, both seats pooled.

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| Hard v Normal | 334 | 271/63/0 | 81.1% | 76.6% - 85.0% |
| Hard v Easy | 334 | 334/0/0 | 100.0% | 98.9% - 100.0% |
| Normal v Easy | 334 | 321/13/0 | 96.1% | 93.5% - 97.7% |

## First-attacker effect

Match result of the player who attacked in round 1.

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| First attacker (mirror policies) | 1002 | 504/498/0 | 50.3% | 47.2% - 53.4% |
| First attacker (all matches) | 2004 | 1011/993/0 | 50.4% | 48.3% - 52.6% |
| First attacker, Hard v Hard | 334 | 138/196/0 | 41.3% | 36.2% - 46.7% |
| First attacker, Normal v Normal | 334 | 180/154/0 | 53.9% | 48.5% - 59.2% |
| First attacker, Easy v Easy | 334 | 186/148/0 | 55.7% | 50.3% - 60.9% |

## Weapons (usage-conditioned, mirror policies)

*Volley result*: the user's net HP change in that volley compared with the opponent's (higher wins). *Duel result*: duels in which the player used the weapon at least once. Loadout membership alone is never counted.

| Weapon | Volleys used | Volley W/L/D | Volley win share | Wilson 95% | Duels used | Duel W/L/D | Duel win share | Wilson 95% |
|---|---:|---:|---:|---|---:|---:|---:|---|
| 1 Ember Arrow (Agni) | 2146 | 1020/618/508 | 62.3% | 59.9% - 64.6% | 1538 | 876/618/44 | 58.6% | 56.1% - 61.1% |
| 2 Gale Arrow (Vayu) | 1794 | 784/666/344 | 54.1% | 51.5% - 56.6% | 1392 | 688/664/40 | 50.9% | 48.2% - 53.5% |
| 3 Stone Arrow (Prithvi) | 2598 | 592/1256/750 | 32.0% | 29.9% - 34.2% | 1906 | 776/1042/88 | 42.7% | 40.4% - 45.0% |
| 4 Spark Arrow (Vidyut) | 2266 | 1144/772/350 | 59.7% | 57.5% - 61.9% | 1648 | 902/704/42 | 56.2% | 53.7% - 58.6% |
| 5 Tide Arrow (Varuna) | 2108 | 800/792/516 | 50.3% | 47.8% - 52.7% | 1612 | 774/770/68 | 50.1% | 47.6% - 52.6% |
| 6 Fire Fan (Agni) | 3102 | 1902/1104/96 | 63.3% | 61.5% - 65.0% | 2012 | 1170/826/16 | 58.6% | 56.4% - 60.8% |
| 7 Twin Gust (Vayu) | 2430 | 1272/900/258 | 58.6% | 56.5% - 60.6% | 1694 | 864/792/38 | 52.2% | 49.8% - 54.6% |
| 8 Boulder Shot (Prithvi) | 3598 | 1324/1664/610 | 44.3% | 42.5% - 46.1% | 2274 | 1108/1092/74 | 50.4% | 48.3% - 52.5% |
| 9 Chain Bolt (Vidyut) | 2202 | 1070/788/344 | 57.6% | 55.3% - 59.8% | 1638 | 794/792/52 | 50.1% | 47.6% - 52.5% |
| 10 Mist Veil (Varuna) | 1928 | 286/992/650 | 22.4% | 20.2% - 24.7% | 1544 | 526/944/74 | 35.8% | 33.4% - 38.3% |
| 11 Ash Shield (Agni) | 1682 | 900/94/688 | 90.5% | 88.6% - 92.2% | 1374 | 742/538/94 | 58.0% | 55.2% - 60.6% |
| 12 Cyclone (Vayu) | 1948 | 448/870/630 | 34.0% | 31.5% - 36.6% | 1550 | 596/878/76 | 40.4% | 38.0% - 43.0% |
| 13 Iron Wall (Prithvi) | 1532 | 622/646/264 | 49.1% | 46.3% - 51.8% | 1294 | 614/640/40 | 49.0% | 46.2% - 51.7% |
| 14 Storm Net (Vidyut) | 1940 | 754/588/598 | 56.2% | 53.5% - 58.8% | 1488 | 768/656/64 | 53.9% | 51.3% - 56.5% |
| 15 Flood Arrow (Varuna) | 2504 | 648/1178/678 | 35.5% | 33.3% - 37.7% | 1844 | 758/1018/68 | 42.7% | 40.4% - 45.0% |
| 16 Sun Lance (Agni) | 3082 | 1444/1218/420 | 54.2% | 52.3% - 56.1% | 2074 | 1082/932/60 | 53.7% | 51.5% - 55.9% |
| 17 Sky Dive (Vayu) | 2884 | 672/1392/820 | 32.6% | 30.6% - 34.6% | 1948 | 754/1098/96 | 40.7% | 38.5% - 43.0% |
| 18 Quake Arrow (Prithvi) | 2214 | 940/912/362 | 50.8% | 48.5% - 53.0% | 1666 | 844/760/62 | 52.6% | 50.2% - 55.1% |
| 19 Thunder Crown (Vidyut) | 3314 | 1246/1350/718 | 48.0% | 46.1% - 49.9% | 2178 | 1116/962/100 | 53.7% | 51.6% - 55.8% |
| 20 Ocean Call (Varuna) | 2468 | 872/940/656 | 48.1% | 45.8% - 50.4% | 1722 | 834/830/58 | 50.1% | 47.7% - 52.5% |

## Element matchups (volley win share of row element vs column element, mirror policies)

Cells: win share among decisive volleys (n decisive). Diagonal pairs are same-element mirrors.

| Row \ Col | Agni | Vayu | Prithvi | Vidyut | Varuna |
|---|---:|---:|---:|---:|---:|
| Agni | 50.0% (1040) | 92.5% (1654) | 89.9% (2160) | 27.7% (1894) | 48.3% (1552) |
| Vayu | 7.5% (1654) | 50.0% (904) | 72.5% (1478) | 69.6% (1614) | 29.8% (1354) |
| Prithvi | 10.1% (2160) | 27.5% (1478) | 50.0% (1200) | 69.1% (1746) | 76.4% (1372) |
| Vidyut | 72.3% (1894) | 30.4% (1614) | 30.9% (1746) | 50.0% (900) | 87.5% (1558) |
| Varuna | 51.7% (1552) | 70.2% (1354) | 23.6% (1372) | 12.5% (1558) | 50.0% (672) |

Element totals (volleys, all opponents):

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| Agni | 8428 | 4746/2514/1168 | 65.4% | 64.3% - 66.5% |
| Vayu | 7528 | 2724/3376/1428 | 44.7% | 43.4% - 45.9% |
| Prithvi | 8222 | 2878/3878/1466 | 42.6% | 41.4% - 43.8% |
| Vidyut | 8106 | 3764/3048/1294 | 55.3% | 54.1% - 56.4% |
| Varuna | 7716 | 2270/3566/1880 | 38.9% | 37.7% - 40.2% |

## Terrain (defender's duel result, mirror policies)

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| Plain | 4374 | 2106/2118/150 | 49.9% | 48.4% - 51.4% |
| Fort | 830 | 438/356/36 | 55.2% | 51.7% - 58.6% |
| River | 756 | 510/208/38 | 71.0% | 67.6% - 74.2% |
| Forest | 872 | 414/424/34 | 49.4% | 46.0% - 52.8% |
| Armoury | 1160 | 562/548/50 | 50.6% | 47.7% - 53.6% |

## Comeback cohort

Players below 35% of the board (17864 cells) after duel four (round 4 completed and the match continued to round 5): their final match result.

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| Below 35% after duel 4 (mirror policies) | 156 | 4/152/0 | 2.6% | 1.0% - 6.4% |

(Checkpoint reached in 1000 mirror policies; cohort size 156.)

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| Below 35% after duel 4 (all matches) | 657 | 6/651/0 | 0.9% | 0.4% - 2.0% |

(Checkpoint reached in 1922 all matches; cohort size 657.)


## Reading notes

- Provisional plan targets: weapon 45-55%, element 47-53% (screening hypotheses, not acceptance criteria).
- Bot aim is solved against the opponent's baseline pose; dodges therefore matter a lot. Weapon results mix the weapon's geometry with how each policy picks and aims it, so a weak bot result can mean a weak policy rather than a weak weapon.
- Hard bots pick weapons by expected element value; usage counts per weapon are therefore uneven.
- Re-run with `dotnet run -c Release --project tools/AstraKingdoms.Sim -- --matches 10000` for the full workload.
