# Astra Kingdoms balance review (playtest records)

> **Human playtest data.** Small samples: most strata will read "insufficient". Clarity and enjoyment are decided by observation notes, not by these tables. Every record was re-executed by the replay verifier before it was counted.

> **Contains synthetic example records** (bot-played, labelled as human only to demonstrate the format). Do not read them as human data.

## Data

- This build runs rules AK-TR-1, hash `e33d84b914ae8ec900c60448be7c7f9e63e08db3657bb2b235580d29917013eb` (records of other hashes are excluded)
- Source: AK-PLAYTEST-RECORD/1 files under tools/AstraKingdoms.Sim/examples, each re-executed by the replay verifier
- Matches used: 1 (1 with a human seat, 1 synthetic); like-for-like (mirror-label) matches: 0
- Rules hashes: `e33d84b914ae8ec900c60448be7c7f9e63e08db3657bb2b235580d29917013eb`
- Configs: AK-TR-1 Online Full cards=V1 terrain=AK-TR-1/full-01
- Win shares are among decisive outcomes (draws counted separately); intervals are Wilson 95%; strata with fewer than 30 decisive outcomes are marked insufficient.

## Match length and terminal reasons

| Reason | Matches | Share |
|---|---:|---:|
| RoundsComplete | 1 | 100.0% |

Reached round 8: **100.0%** (1/1, Wilson 20.7% - 100.0%). Match draws: 0.

## Policy pairings

Row label's match result against the column label, both seats pooled (bots by difficulty; people as "human").

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| Hard v human | 1 | 1/0/0 | 100.0% (insufficient) | 20.7% - 100.0% |

Mirror pairings played: 

## First-attacker effect

Match result of the player who attacked in round 1.

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| All matches | 1 | 0/1/0 | 0.0% (insufficient) | 0.0% - 79.3% |
| Mirror labels | 0 | 0/0/0 | n/a | n/a |
| Catalog Full | 1 | 0/1/0 | 0.0% (insufficient) | 0.0% - 79.3% |

## Unlock cohorts

Account-level bands (levels 2-16 introduce the 15 non-starter weapons). Full rooms loan all twenty, so a band describes familiarity, never an unequal catalogue. For bot runs the band is a simulation assumption: the bot equips only weapons its level has unlocked.

Row band's match result against the column band (seats pooled; mirror labels only, so policy strength is held equal).

| Row \ Col | L1 (starters) | L2-8 | L9-16 | L17-20 | unknown level |
|---|---:|---:|---:|---:|---:|
| L1 (starters) | - | - | - | - | - |
| L2-8 | - | - | - | - | - |
| L9-16 | - | - | - | - | - |
| L17-20 | - | - | - | - | - |
| unknown level | - | - | - | - | - |

Result against other bands, and the share of volleys fired with non-starter weapons:

| Band | n vs other bands | W/L/D | Win share | Wilson 95% | Non-starter volley share |
|---|---:|---:|---:|---|---:|

## Element matchups (volley win share of row vs column, mirror labels)

| Row \ Col | Agni | Vayu | Prithvi | Vidyut | Varuna |
|---|---:|---:|---:|---:|---:|
| Agni | n/a (0) | n/a (0) | n/a (0) | n/a (0) | n/a (0) |
| Vayu | n/a (0) | n/a (0) | n/a (0) | n/a (0) | n/a (0) |
| Prithvi | n/a (0) | n/a (0) | n/a (0) | n/a (0) | n/a (0) |
| Vidyut | n/a (0) | n/a (0) | n/a (0) | n/a (0) | n/a (0) |
| Varuna | n/a (0) | n/a (0) | n/a (0) | n/a (0) | n/a (0) |

Element totals (volleys against other elements):

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|
| Agni | 0 | 0/0/0 | n/a | n/a |
| Vayu | 0 | 0/0/0 | n/a | n/a |
| Prithvi | 0 | 0/0/0 | n/a | n/a |
| Vidyut | 0 | 0/0/0 | n/a | n/a |
| Varuna | 0 | 0/0/0 | n/a | n/a |

## Weapons: usage-conditioned versus loadout-contained (mirror labels)

- *Volley*: the user's net HP change in a volley where they fired the weapon, against the opponent's.
- *Duel (used)*: duels in which the player fired the weapon at least once.
- *Match (in loadout)*: matches whose loadout contained the weapon, used or not. This mixes the weapon with everything else in the loadout; a large gap from the usage-conditioned columns means the weapon's effect depends on when it is chosen.
- *Equip rate*: share of seats that equipped it; *use rate*: volleys fired with it per volley in which it was equipped.

| Weapon | Volleys | Volley share | Wilson 95% | Duels used | Duel share (used) | Wilson 95% | Matches in loadout | Match share (in loadout) | Wilson 95% | Equip rate | Use rate |
|---|---:|---:|---|---:|---:|---|---:|---:|---|---:|---:|
| 1 Ember Arrow (Agni) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 2 Gale Arrow (Vayu) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 3 Stone Arrow (Prithvi) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 4 Spark Arrow (Vidyut) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 5 Tide Arrow (Varuna) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 6 Fire Fan (Agni) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 7 Twin Gust (Vayu) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 8 Boulder Shot (Prithvi) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 9 Chain Bolt (Vidyut) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 10 Mist Veil (Varuna) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 11 Ash Shield (Agni) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 12 Cyclone (Vayu) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 13 Iron Wall (Prithvi) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 14 Storm Net (Vidyut) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 15 Flood Arrow (Varuna) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 16 Sun Lance (Agni) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 17 Sky Dive (Vayu) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 18 Quake Arrow (Prithvi) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 19 Thunder Crown (Vidyut) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |
| 20 Ocean Call (Varuna) | 0 | n/a | n/a | 0 | n/a | n/a | 0 | n/a | n/a | n/a | n/a |

## Terrain (defender's duel result, mirror labels)

| Key | n | W/L/D | Win share (decisive) | Wilson 95% |
|---|---:|---:|---:|---|

## Comeback behaviour (mirror labels)

Final match result of the player who trailed in cells after a checkpoint round, for matches that continued past it.

| Checkpoint | Trailing by | n | W/L/D | Win share | Wilson 95% |
|---|---|---:|---:|---:|---|
| After round 2 | < 35% of board | 0 | 0/0/0 | n/a | n/a |
| After round 2 | 35-45% | 0 | 0/0/0 | n/a | n/a |
| After round 2 | 45-50% | 0 | 0/0/0 | n/a | n/a |
| After round 4 | < 35% of board | 0 | 0/0/0 | n/a | n/a |
| After round 4 | 35-45% | 0 | 0/0/0 | n/a | n/a |
| After round 4 | 45-50% | 0 | 0/0/0 | n/a | n/a |
| After round 6 | < 35% of board | 0 | 0/0/0 | n/a | n/a |
| After round 6 | 35-45% | 0 | 0/0/0 | n/a | n/a |
| After round 6 | 45-50% | 0 | 0/0/0 | n/a | n/a |

| Lead changes per match | Matches |
|---:|---:|

## Screening flags and uncertainty

26 comparisons are screened against provisional bands. **Flag**: the Bonferroni family interval (z = 3.10, overall alpha 0.05) lies entirely outside the band. **Watch**: only the per-comparison 95% interval lies outside. **Insufficient**: fewer than 30 decisive outcomes. Everything else is consistent with the band at this sample size (which is not proof of balance).

| Kind | Key | n decisive | Win share | Band | Wilson 95% | Family interval | Status |
|---|---|---:|---:|---|---|---|---|
| first attacker | mirror labels | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| element | Agni | 0 | n/a | 47.0%-53.0% | n/a | n/a | insufficient |
| element | Vayu | 0 | n/a | 47.0%-53.0% | n/a | n/a | insufficient |
| element | Prithvi | 0 | n/a | 47.0%-53.0% | n/a | n/a | insufficient |
| element | Vidyut | 0 | n/a | 47.0%-53.0% | n/a | n/a | insufficient |
| element | Varuna | 0 | n/a | 47.0%-53.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 1 Ember Arrow | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 2 Gale Arrow | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 3 Stone Arrow | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 4 Spark Arrow | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 5 Tide Arrow | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 6 Fire Fan | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 7 Twin Gust | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 8 Boulder Shot | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 9 Chain Bolt | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 10 Mist Veil | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 11 Ash Shield | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 12 Cyclone | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 13 Iron Wall | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 14 Storm Net | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 15 Flood Arrow | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 16 Sun Lance | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 17 Sky Dive | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 18 Quake Arrow | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 19 Thunder Crown | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |
| weapon (duel, used) | 20 Ocean Call | 0 | n/a | 45.0%-55.0% | n/a | n/a | insufficient |

## Reading notes

- Bands (weapon 45-55%, element 47-53%) are screening hypotheses from the plan, not acceptance criteria; a weapon need not match an aggregate win rate.
- Compare like-for-like strata (mirror labels, same catalogue). Bot weapon results mix the weapon's geometry with how each policy picks and aims it.
- Usage-conditioned and loadout-contained results answer different questions; neither is causal.
- Human tests still decide clarity and enjoyment. Re-run: `dotnet run -c Release --project tools/AstraKingdoms.Sim -- --matches 10000 --cohorts` (bots) or `-- --ingest <folder>` (playtest records).
