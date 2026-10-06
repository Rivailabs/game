# Astra Kingdoms bot-policy screening report

> **Bot-policy screening results, not balance proof.** Outcomes reflect scripted policies (Easy/Normal/Hard) with fixed heuristics; humans choose weapons, aim and cuts differently. Use them to choose human tests, not to tune constants on one aggregate.

## Data

- Rules: `AK-TR-1`, rules hash `e33d84b914ae8ec900c60448be7c7f9e63e08db3657bb2b235580d29917013eb`
- Config: AK-TR-1 Online Full cards=V1 terrain=AK-TR-1/full-01
- Matches: 2004 completed, 0 errors; base seed 20261006; 4 threads; 194.2 s
- Pairings (mirrored pairs: same seed, policies swap seats): Hard v Hard, Normal v Normal, Easy v Easy, Hard v Normal, Hard v Easy, Normal v Easy
- Unlock cohorts ON: each seat gets an account level from {1, 4, 8, 12, 16, 20} and equips only weapons unlocked at that level (familiarity assumption)
- Source: bot simulation through the authoritative engine (bots see only their private view; any rejected command is an error)
- Matches used: 2004 (0 with a human seat, 0 synthetic); like-for-like (mirror-label) matches: 1002
- Rules hashes: `e33d84b914ae8ec900c60448be7c7f9e63e08db3657bb2b235580d29917013eb`
- Configs: AK-TR-1 Online Full cards=V1 terrain=AK-TR-1/full-01
- Win shares are among decisive outcomes (draws counted separately); intervals are Wilson 95%; strata with fewer than 30 decisive outcomes are marked insufficient.

## Match length and terminal reasons

| Reason | Matches | Share |
|---|---:|---:|
| Territory90 | 335 | 16.7% |
| RoundsComplete | 1669 | 83.3% |

Reached round 8: **85.9%** (1722/2004, Wilson 84.3% - 87.4%). Match draws: 4.

## Policy pairings

Row label's match result against the column label, both seats pooled (bots by difficulty; people as "human").

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| Easy v Hard | 334 | 0/334/0 | 0.0% | 0.0% - 1.1% |
| Easy v Normal | 334 | 10/324/0 | 3.0% | 1.6% - 5.4% |
| Hard v Normal | 334 | 271/63/0 | 81.1% | 76.6% - 85.0% |

Mirror pairings played: Hard v Hard (334), Normal v Normal (334), Easy v Easy (334)

## First-attacker effect

Match result of the player who attacked in round 1.

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| All matches | 2004 | 982/1018/4 | 49.1% | 46.9% - 51.3% |
| Mirror labels | 1002 | 470/528/4 | 47.1% | 44.0% - 50.2% |
| First attacker, Easy v Easy | 334 | 163/171/0 | 48.8% | 43.5% - 54.1% |
| First attacker, Hard v Hard | 334 | 158/172/4 | 47.9% | 42.5% - 53.3% |
| First attacker, Normal v Normal | 334 | 149/185/0 | 44.6% | 39.4% - 50.0% |
| Catalog Full | 2004 | 982/1018/4 | 49.1% | 46.9% - 51.3% |

## Unlock cohorts

Account-level bands (levels 2-16 introduce the 15 non-starter weapons). Full rooms loan all twenty, so a band describes familiarity, never an unequal catalogue. For bot runs the band is a simulation assumption: the bot equips only weapons its level has unlocked.

Row band's match result against the column band (seats pooled; mirror labels only, so policy strength is held equal).

| Row \ Col | L1 (starters) | L2-8 | L9-16 | L17-20 | unknown level |
|---|---:|---:|---:|---:|---:|
| L1 (starters) | 50.0% (36) | 43.5% (124) | 42.4% (132) | 44.8% (58) | - |
| L2-8 | 56.5% (124) | 50.0% (180) | 55.0% (202) | 50.0% (138) | - |
| L9-16 | 57.6% (132) | 45.0% (202) | 50.0% (228) | 46.4% (84) | - |
| L17-20 | 55.2% (58) | 50.0% (138) | 53.6% (84) | 50.0% (76) | - |
| unknown level | - | - | - | - | - |

Result against other bands, and the share of volleys fired with non-starter weapons:

| Band | n vs other bands | W/L/D | Win share | Wilson 95% | Non-starter volley share |
|---|---:|---:|---:|---|---:|
| L1 (starters) | 314 | 136/178/0 | 43.3% | 37.9% - 48.8% | 0.0% |
| L2-8 | 464 | 250/214/0 | 53.9% | 49.3% - 58.4% | 52.5% |
| L9-16 | 418 | 206/212/0 | 49.3% | 44.5% - 54.1% | 73.1% |
| L17-20 | 280 | 146/134/0 | 52.1% | 46.3% - 57.9% | 76.8% |

## Element matchups (volley win share of row vs column, mirror labels)

| Row \ Col | Agni | Vayu | Prithvi | Vidyut | Varuna |
|---|---:|---:|---:|---:|---:|
| Agni | 50.0% (1758) | 88.8% (1676) | 92.6% (2764) | 22.1% (1966) | 38.3% (1698) |
| Vayu | 11.2% (1676) | 50.0% (1054) | 79.8% (1428) | 71.6% (1399) | 32.4% (1243) |
| Prithvi | 7.4% (2764) | 20.2% (1428) | 50.0% (1090) | 70.3% (1771) | 71.9% (1259) |
| Vidyut | 77.9% (1966) | 28.4% (1399) | 29.7% (1771) | 50.0% (670) | 87.9% (1233) |
| Varuna | 61.7% (1698) | 67.6% (1243) | 28.1% (1259) | 12.1% (1233) | 50.0% (776) |

Element totals (volleys against other elements):

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| Agni | 9320 | 5133/2971/1216 | 63.3% | 62.3% - 64.4% |
| Vayu | 7010 | 2730/3016/1264 | 47.5% | 46.2% - 48.8% |
| Prithvi | 8656 | 2644/4578/1434 | 36.6% | 35.5% - 37.7% |
| Vidyut | 7539 | 3540/2829/1170 | 55.6% | 54.4% - 56.8% |
| Varuna | 7103 | 2390/3043/1670 | 44.0% | 42.7% - 45.3% |

## Weapons: usage-conditioned versus loadout-contained (mirror labels)

- *Volley*: the user's net HP change in a volley where they fired the weapon, against the opponent's.
- *Duel (used)*: duels in which the player fired the weapon at least once.
- *Match (in loadout)*: matches whose loadout contained the weapon, used or not. This mixes the weapon with everything else in the loadout; a large gap from the usage-conditioned columns means the weapon's effect depends on when it is chosen.
- *Equip rate*: share of seats that equipped it; *use rate*: volleys fired with it per volley in which it was equipped.

| Weapon | Volleys | Volley share | Wilson 95% | Duels used | Duel share (used) | Wilson 95% | Matches in loadout | Match share (in loadout) | Wilson 95% | Equip rate | Use rate |
|---|---:|---:|---|---:|---:|---|---:|---:|---|---:|---:|
| 1 Ember Arrow (Agni) | 4546 | 58.1% | 56.4% - 59.7% | 3206 | 55.4% | 53.7% - 57.2% | 1137 | 52.8% | 49.9% - 55.7% | 56.7% | 16.7% |
| 2 Gale Arrow (Vayu) | 3193 | 52.6% | 50.6% - 54.5% | 2568 | 50.7% | 48.7% - 52.6% | 1148 | 50.5% | 47.6% - 53.4% | 57.3% | 11.6% |
| 3 Stone Arrow (Prithvi) | 4866 | 29.6% | 28.1% - 31.1% | 3494 | 41.5% | 39.8% - 43.2% | 1152 | 46.4% | 43.5% - 49.3% | 57.5% | 17.7% |
| 4 Spark Arrow (Vidyut) | 4778 | 58.6% | 57.1% - 60.2% | 3383 | 52.6% | 50.9% - 54.3% | 1169 | 50.9% | 48.0% - 53.8% | 58.3% | 17.1% |
| 5 Tide Arrow (Varuna) | 4615 | 52.2% | 50.6% - 53.9% | 3383 | 50.3% | 48.6% - 52.0% | 1184 | 49.7% | 46.9% - 52.6% | 59.1% | 16.3% |
| 6 Fire Fan (Agni) | 4279 | 59.9% | 58.4% - 61.4% | 2655 | 56.4% | 54.5% - 58.3% | 835 | 55.0% | 51.6% - 58.3% | 41.7% | 21.5% |
| 7 Twin Gust (Vayu) | 2767 | 55.2% | 53.3% - 57.2% | 2014 | 51.0% | 48.8% - 53.2% | 846 | 51.3% | 47.9% - 54.7% | 42.2% | 13.7% |
| 8 Boulder Shot (Prithvi) | 4039 | 43.5% | 41.8% - 45.2% | 2624 | 51.1% | 49.2% - 53.0% | 823 | 57.6% | 54.2% - 60.9% | 41.1% | 20.6% |
| 9 Chain Bolt (Vidyut) | 1746 | 57.7% | 55.2% - 60.3% | 1283 | 50.8% | 48.0% - 53.6% | 512 | 49.8% | 45.5% - 54.1% | 25.5% | 14.3% |
| 10 Mist Veil (Varuna) | 1572 | 24.5% | 22.1% - 27.1% | 1267 | 35.7% | 33.1% - 38.4% | 537 | 40.8% | 36.7% - 45.0% | 26.8% | 12.2% |
| 11 Ash Shield (Agni) | 1313 | 87.7% | 85.3% - 89.7% | 1084 | 59.0% | 56.0% - 61.9% | 502 | 50.0% | 45.6% - 54.4% | 25.0% | 11.0% |
| 12 Cyclone (Vayu) | 1452 | 30.8% | 28.1% - 33.7% | 1176 | 38.6% | 35.8% - 41.5% | 544 | 44.1% | 40.0% - 48.3% | 27.1% | 11.2% |
| 13 Iron Wall (Prithvi) | 954 | 47.2% | 43.7% - 50.6% | 821 | 46.8% | 43.3% - 50.2% | 390 | 44.1% | 39.3% - 49.1% | 19.5% | 10.3% |
| 14 Storm Net (Vidyut) | 1120 | 46.9% | 43.4% - 50.4% | 848 | 52.3% | 48.9% - 55.7% | 361 | 52.1% | 46.9% - 57.2% | 18.0% | 13.0% |
| 15 Flood Arrow (Varuna) | 1369 | 39.2% | 36.2% - 42.2% | 1014 | 45.5% | 42.4% - 48.7% | 372 | 45.7% | 40.7% - 50.8% | 18.6% | 15.4% |
| 16 Sun Lance (Agni) | 1576 | 55.4% | 52.9% - 58.0% | 1036 | 55.5% | 52.4% - 58.5% | 328 | 54.6% | 49.2% - 59.9% | 16.4% | 20.1% |
| 17 Sky Dive (Vayu) | 1074 | 33.0% | 29.8% - 36.3% | 705 | 40.7% | 37.1% - 44.4% | 250 | 52.0% | 45.8% - 58.1% | 12.5% | 18.0% |
| 18 Quake Arrow (Prithvi) | 645 | 51.7% | 47.6% - 55.9% | 504 | 52.7% | 48.3% - 57.1% | 224 | 57.6% | 51.0% - 63.9% | 11.2% | 12.1% |
| 19 Thunder Crown (Vidyut) | 1125 | 42.2% | 39.0% - 45.5% | 736 | 49.7% | 46.0% - 53.4% | 230 | 49.6% | 43.2% - 56.0% | 11.5% | 20.5% |
| 20 Ocean Call (Varuna) | 839 | 48.6% | 44.6% - 52.7% | 596 | 51.1% | 47.0% - 55.1% | 216 | 50.9% | 44.3% - 57.5% | 10.8% | 16.3% |

## Terrain (defender's duel result, mirror labels)

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| Plain | 4356 | 2080/2121/155 | 49.5% | 48.0% - 51.0% |
| Fort | 862 | 473/367/22 | 56.3% | 52.9% - 59.6% |
| River | 715 | 513/166/36 | 75.6% | 72.2% - 78.6% |
| Forest | 868 | 387/436/45 | 47.0% | 43.6% - 50.4% |
| Armoury | 1204 | 555/608/41 | 47.7% | 44.9% - 50.6% |

## Comeback behaviour (mirror labels)

Final match result of the player who trailed in cells after a checkpoint round, for matches that continued past it.

| Checkpoint | Trailing by | n | W/L/D | Win share | Wilson 95% |
|---|---|---:|---:|---:|---|
| After round 2 | < 35% of board | 38 | 2/36/0 | 5.3% | 1.5% - 17.3% |
| After round 2 | 35-45% | 407 | 83/324/0 | 20.4% | 16.8% - 24.6% |
| After round 2 | 45-50% | 481 | 203/278/0 | 42.2% | 37.9% - 46.7% |
| After round 4 | < 35% of board | 136 | 5/131/0 | 3.7% | 1.6% - 8.3% |
| After round 4 | 35-45% | 445 | 82/363/0 | 18.4% | 15.1% - 22.3% |
| After round 4 | 45-50% | 407 | 155/252/0 | 38.1% | 33.5% - 42.9% |
| After round 6 | < 35% of board | 223 | 4/219/0 | 1.8% | 0.7% - 4.5% |
| After round 6 | 35-45% | 415 | 44/371/0 | 10.6% | 8.0% - 13.9% |
| After round 6 | 45-50% | 355 | 124/231/0 | 34.9% | 30.2% - 40.0% |

| Lead changes per match | Matches |
|---:|---:|
| 0 | 409 |
| 1 | 260 |
| 2 | 178 |
| 3 | 95 |
| 4 | 41 |
| 5 | 16 |
| 6 | 3 |

## Screening flags and uncertainty

31 comparisons are screened against provisional bands. **Flag**: the Bonferroni family interval (z = 3.15, overall alpha 0.05) lies entirely outside the band. **Watch**: only the per-comparison 95% interval lies outside. **Insufficient**: fewer than 30 decisive outcomes. Everything else is consistent with the band at this sample size (which is not proof of balance).

| Kind | Key | n decisive | Win share | Band | Wilson 95% | Family interval | Status |
|---|---|---:|---:|---|---|---|---|
| element | Agni | 8104 | 63.3% | 47.0%-53.0% | 62.3% - 64.4% | 61.6% - 65.0% | FLAG |
| element | Prithvi | 7222 | 36.6% | 47.0%-53.0% | 35.5% - 37.7% | 34.8% - 38.4% | FLAG |
| element | Vidyut | 6369 | 55.6% | 47.0%-53.0% | 54.4% - 56.8% | 53.6% - 57.5% | FLAG |
| element | Varuna | 5433 | 44.0% | 47.0%-53.0% | 42.7% - 45.3% | 41.9% - 46.1% | FLAG |
| weapon (duel, used) | 3 Stone Arrow | 3327 | 41.5% | 45.0%-55.0% | 39.8% - 43.2% | 38.8% - 44.2% | FLAG |
| weapon (duel, used) | 10 Mist Veil | 1224 | 35.7% | 45.0%-55.0% | 33.1% - 38.4% | 31.5% - 40.1% | FLAG |
| weapon (duel, used) | 11 Ash Shield | 1036 | 59.0% | 45.0%-55.0% | 56.0% - 61.9% | 54.1% - 63.7% | watch |
| weapon (duel, used) | 12 Cyclone | 1123 | 38.6% | 45.0%-55.0% | 35.8% - 41.5% | 34.2% - 43.3% | FLAG |
| weapon (duel, used) | 17 Sky Dive | 678 | 40.7% | 45.0%-55.0% | 37.1% - 44.4% | 34.9% - 46.8% | watch |
| terrain defender | River | 679 | 75.6% | 45.0%-55.0% | 72.2% - 78.6% | 70.0% - 80.4% | FLAG |

## Reading notes

- Bands (weapon 45-55%, element 47-53%) are screening hypotheses from the plan, not acceptance criteria; a weapon need not match an aggregate win rate.
- Compare like-for-like strata (mirror labels, same catalogue). Bot weapon results mix the weapon's geometry with how each policy picks and aims it.
- Usage-conditioned and loadout-contained results answer different questions; neither is causal.
- Human tests still decide clarity and enjoyment. Re-run: `dotnet run -c Release --project tools/AstraKingdoms.Sim -- --matches 10000 --cohorts` (bots) or `-- --ingest <folder>` (playtest records).
