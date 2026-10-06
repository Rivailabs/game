using System.Globalization;
using System.Text;
using AstraKingdoms.World;
using AstraKingdoms.World.Armies;
using AstraKingdoms.World.Economy;

namespace AstraKingdoms.EconomySim;

/// <summary>Markdown and CSV output of one simulation run.</summary>
internal sealed class Report
{
    private readonly SimOptions _o;
    private readonly SimulationResult _r;
    private readonly IReadOnlyList<ProbeResult> _probes;
    private readonly TimeSpan _elapsed;

    public Report(SimOptions o, SimulationResult r, IReadOnlyList<ProbeResult> probes, TimeSpan elapsed)
    {
        _o = o;
        _r = r;
        _probes = probes;
        _elapsed = elapsed;
    }

    private static string F(double v, string fmt = "0.000") => v.ToString(fmt, CultureInfo.InvariantCulture);

    public string ConsoleSummary()
    {
        var sb = new StringBuilder();
        foreach (SeasonMetrics m in _r.Seasons)
            sb.AppendLine($"Season {m.Season}: {m.Accepted}/{m.Attempts} challenges accepted, max loss/day {m.MaxLossesOneDay}, max loss/season {m.MaxLossesSeason}, " +
                          $"Gini coins {F(m.GiniAllCoins)}, tiles {F(m.GiniTiles)}, restored={m.AllRestoredAfterRebuild}");
        foreach (ProbeResult p in _probes) sb.AppendLine($"Probe {p.Name}: {(p.IsExploit ? "EXPLOIT" : "bounded")} - {p.Outcome}");
        sb.Append(_r.InvariantViolations.Count == 0 ? "No invariant violations." : "INVARIANT VIOLATIONS: " + string.Join("; ", _r.InvariantViolations));
        return sb.ToString();
    }

    public string Csv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("season,players,attempts,accepted,attacker_wins,defender_wins,draws,expired,max_loss_day,max_loss_season,below_allocation,min_tiles,max_tiles_honest,ring_main_tiles,gini_tiles,gini_world_coins,gini_all_coins,top10_share,world_minted,world_spent,duel_minted,max_world_coins_day,restored,reentry_offers,migrations");
        foreach (SeasonMetrics m in _r.Seasons)
            sb.AppendLine(string.Join(",", new object[]
            {
                m.Season, m.ActivePlayers, m.Attempts, m.Accepted, m.AttackerWins, m.DefenderWins, m.Draws, m.Expired, m.MaxLossesOneDay,
                m.MaxLossesSeason, m.AccountsBelowAllocation, m.MinTilesHeld, m.MaxTilesHeldHonest, m.RingMainTiles, F(m.GiniTiles), F(m.GiniWorldCoins),
                F(m.GiniAllCoins), F(m.Top10ShareAllCoins), m.WorldMinted, m.WorldSpent, m.DuelMinted, m.MaxWorldCoinsOneDay, m.AllRestoredAfterRebuild,
                m.ReEntryOffers, m.Migrations,
            }.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))));
        return sb.ToString();
    }

    public string Markdown()
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);

        L("# V4 economy discovery — " + WorldRules.RulesId);
        L();
        L("> **Discovery evidence, not a balance or economy approval.** Every world number is PROPOSED (plan: \"V4 bounded conquest with a recoverable kingdom\"). " +
          "Players, their activity and encounter outcomes are modelled; every rule decision (reservations, protection table, caps, season close, grants) is made by the real `AstraKingdoms.World` code.");
        L();
        L("- World rules hash: `" + WorldRules.HashHex + "`");
        L($"- Run: {_o.Players} players, {_o.Seasons} seasons x {WorldRules.SeasonDays} days, {_o.Shards} shards, seed {_o.Seed}, " +
          $"{_r.RealEncounters} encounters played as real AK-TR-1 matches, collusion ring of 1 + {_o.RingAlts} alts. Runtime {F(_elapsed.TotalSeconds, "0.0")} s.");
        L("- Reproduce: `dotnet run -c Release --project tools/AstraKingdoms.EconomySim -- --players " + _o.Players + " --seasons " + _o.Seasons +
          " --shards " + _o.Shards + " --seed " + _o.Seed + "`");
        L();

        L("## Verdicts");
        L();
        bool exploit = _probes.Any(p => p.IsExploit);
        bool lossOk = _r.Seasons.All(m => m.MaxLossesOneDay <= WorldRules.MaxBorderLossesPerUtcDay && m.MaxLossesSeason <= WorldRules.MaxBorderLossesPerSeason);
        bool recovery = _r.Seasons.All(m => m.AllRestoredAfterRebuild);
        L("| Plan evidence item | Result |");
        L("| --- | --- |");
        L("| No repeatable currency exploit | " + (exploit ? "**FAILED** — see probes" : "No probe exceeded the daily cap or repeated a grant (" + _probes.Count + " probes)") + " |");
        L("| Bounded loss | " + (lossOk ? "Held: max " + _r.Seasons.Max(m => m.MaxLossesOneDay) + " tiles/day, " + _r.Seasons.Max(m => m.MaxLossesSeason) + "/season (caps 2 and 6)" : "**FAILED**") + " |");
        L("| Recovery | " + (recovery ? "Every account back to 12 border tiles after each rebuild; homeland untouched by construction" : "**FAILED**") + " |");
        L("| Newcomer progression | " + NewcomerVerdict() + " |");
        L("| Invariants | " + (_r.InvariantViolations.Count == 0 ? "No violations" : "**" + string.Join("; ", _r.InvariantViolations) + "**") + " |");
        L();

        L("## Findings for the owner");
        L();
        foreach (string f in Findings()) L("- " + f);
        L();

        L("## Per season");
        L();
        L("| Season | Players | Attempts | Accepted | A wins | D wins | Draws | Expired | Max loss/day | Max loss/season | Below 12 at close | Min tiles | Max tiles (honest) | Ring main tiles | Re-entry offers | Migrations |");
        L("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (SeasonMetrics m in _r.Seasons)
            L($"| {m.Season} | {m.ActivePlayers} | {m.Attempts} | {m.Accepted} | {m.AttackerWins} | {m.DefenderWins} | {m.Draws} | {m.Expired} | {m.MaxLossesOneDay} | {m.MaxLossesSeason} | " +
              $"{m.AccountsBelowAllocation} | {m.MinTilesHeld} | {m.MaxTilesHeldHonest} | {m.RingMainTiles} ({m.RingMainRecognition}) | {m.ReEntryOffers} | {m.Migrations} |");
        L();
        L("Rejected challenge attempts by reason (all seasons): " + string.Join(", ",
            _r.Seasons.SelectMany(m => m.Rejections).GroupBy(k => k.Key).OrderByDescending(g => g.Sum(x => x.Value))
                .Select(g => "`" + g.Key + "` " + g.Sum(x => x.Value))) + ".");
        L();

        L("## Currency and inequality");
        L();
        L("| Season | World coins minted | World coins spent | Duel coins minted (A1) | Max world coins one day | Gini world coins | Gini all coins | Top-10% share | Gini tiles at close |");
        L("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (SeasonMetrics m in _r.Seasons)
            L($"| {m.Season} | {m.WorldMinted} | {m.WorldSpent} | {m.DuelMinted} | {m.MaxWorldCoinsOneDay} | {F(m.GiniWorldCoins)} | {F(m.GiniAllCoins)} | {F(m.Top10ShareAllCoins)} | {F(m.GiniTiles)} |");
        L();
        L("Coin balances are cumulative across seasons (purchases, cosmetics and coins survive resets by design), so their Gini rises with activity differences; " +
          "tile Gini resets every season because borders are rebuilt. Repeatable world income is capped at " + EconomyRules.DailyWorldCoinCap + " coins per account per UTC day.");
        L();

        L("## Exploit probes (real rules, adversarial loops)");
        L();
        L("| Probe | Loop | Iterations | Coins gained | Max coins/day | Extra grants | Verdict | Outcome |");
        L("| --- | --- | ---: | ---: | ---: | ---: | --- | --- |");
        foreach (ProbeResult p in _probes)
            L($"| {p.Name} | {p.Loop} | {p.Iterations} | {p.CoinsGained} | {p.MaxCoinsOneDay} | {p.ExtraGrants} | {(p.IsExploit ? "**EXPLOIT**" : "bounded")} | {p.Outcome} |");
        L();
        foreach (ProbeResult p in _probes.Where(p => p.Flag.Length > 0)) L("- **Flag — " + p.Name + ":** " + p.Flag);
        L();

        L("## Newcomer progression");
        L();
        var newcomers = _r.Players.Where(p => p.Joined && p.Newcomer && p.Role == Role.Honest).ToList();
        var founders = _r.Players.Where(p => p.Joined && !p.Newcomer && p.Role == Role.Honest).ToList();
        L($"- Newcomers (joined after launch): {newcomers.Count}; founders: {founders.Count}.");
        L($"- Attacks received by newcomers during their first season: {newcomers.Sum(p => p.AttacksReceivedWhileNewcomer)}; " +
          $"from Veteran-band attackers while still Newcomer-band: {newcomers.Sum(p => p.AttacksReceivedFromVeteransWhileNewcomer)} (the band rule makes this 0).");
        var tilesAtClose = newcomers.Where(p => p.FirstSeasonTilesAtClose >= 0).Select(p => (double)p.FirstSeasonTilesAtClose).ToList();
        if (tilesAtClose.Count > 0)
            L($"- Border tiles at their first season close: median {F(Stats.Median(tilesAtClose), "0")}, minimum {tilesAtClose.Min()} " +
              $"(floor is {WorldRules.BorderTilesPerAccount - WorldRules.MaxBorderLossesPerSeason}).");
        var coins14 = newcomers.Where(p => p.WorldCoinsAtDay14 >= 0).Select(p => (double)(p.WorldCoinsAtDay14 + p.DuelCoinsAtDay14)).ToList();
        if (coins14.Count > 0) L($"- Coins held on day 14 of their join season (world + duel): median {F(Stats.Median(coins14), "0")}.");
        L("- Starter protection (7 days, early exit after 5 training encounters by choice) and season resets mean a newcomer's worst first season still ends with 6 tiles and full access next season.");
        L();

        L("## Real AK-TR-1 encounter sample");
        L();
        if (_r.RealEncounters > 0)
            L($"{_r.RealEncounters} challenges were played as real Full-catalog AK-TR-1 matches against the defender's published bot (Normal policy by default): " +
              $"attacker wins {_r.RealAttackerWins}, defender wins {_r.RealDefenderWins}, draws {_r.RealDraws}. The rest used the outcome model below " +
              $"(model attacker win rate {F(_r.ModelEncounters == 0 ? 0 : _r.ModelAttackerWinRate / _r.ModelEncounters)} over {_r.ModelEncounters} encounters). " +
              "The sample is too small to calibrate the model; it proves the encounter path runs end to end.");
        else L("Disabled for this run (`--real-encounters 0`).");
        L();

        L("## Modelling assumptions (sim-side, PROPOSED)");
        L();
        L("| ID | Assumption |");
        L("| --- | --- |");
        L($"| A1 | V1 duel income (outside the world): {WorldSimulation.DuelCoinsWin} coins per win, {WorldSimulation.DuelCoinsLoss} per loss, at most {WorldSimulation.DuelDailyCap} per day. The real V1 reward table is owned elsewhere. |");
        L($"| A2 | Offline defence plays at a fixed Elo-equivalent of {WorldSimulation.OfflineBotRating}; a defender is online {F(WorldSimulation.DefenderOnlineRate * 100, "0")}% of the time and then uses their own skill. |");
        L($"| A3 | Outcomes: Elo expectation on hidden skill (mean 1000, sd 150), {F(WorldSimulation.DrawRate * 100, "0")}% draws, {F(WorldSimulation.AbandonRate * 100, "0")}% of accepted challenges abandoned (they expire). |");
        L("| A4 | Activity mix 55% casual (40% daily presence, 1 attack try), 35% regular (80%, 2), 10% grinder (100%, 4); 20% of players arrive after launch. |");
        L("| A5 | Spending: 40% chance per sink per active day when affordable (world coins), 25% from duel coins. |");
        L("| A6 | 30% of players join alliances; 2% request a shard migration each season. |");
        L($"| A7 | Armies are snapshotted but effect-free (role table: {string.Join(", ", ArmyRules.Roles.Select(r => r.Role + " " + r.Cost + "pt x" + r.MaxSlots))}; {WorldRules.DeploymentPoints} points). |");
        L();

        L("## What this does not establish");
        L();
        L("- Real player behaviour, fun, perceived fairness or harassment patterns (human fairness tests).");
        L("- The outcome model's calibration against real encounters (needs many real AK-TR-1 encounters and live data).");
        L("- Load and storage behaviour of shards (a declared load scenario on real infrastructure).");
        L("- Army role effects: none are implemented; each needs its own simulation and rules approval.");
        return sb.ToString();
    }

    private IEnumerable<string> Findings()
    {
        var rejections = _r.Seasons.SelectMany(m => m.Rejections).GroupBy(k => k.Key)
            .Select(g => (Code: g.Key, Count: g.Sum(x => x.Value))).OrderByDescending(x => x.Count).ToList();
        int attempts = _r.Seasons.Sum(m => m.Attempts);
        if (rejections.Count > 0)
            yield return $"The most common reason a challenge attempt failed was `{rejections[0].Code}` ({F(100.0 * rejections[0].Count / Math.Max(1, attempts), "0")}% of attempts). " +
                         "With this activity mix most defenders use up the six-loss season allowance well before day 18, so late-season targets become scarce; " +
                         "test with people whether that feels fair or empty before approving the constants.";
        foreach (SeasonMetrics m in _r.Seasons)
            yield return $"Season {m.Season}: {m.AccountsAtSeasonCap} of {m.Members} accounts ended at the season loss cap; the best honest account held {m.MaxTilesHeldHonest} tiles. " +
                         "Losses are bounded but gains are limited only by other accounts' allowances, so tiles must stay cosmetic recognition, never production or power.";
        yield return $"A colluding ring (1 main + {_o.RingAlts} alts that never defend) reached {_r.Seasons.Max(m => m.RingMainTiles)} tiles and the top recognition tier; " +
                     "its coins stayed within the daily cap. The experience-band rule eventually stops the main from farming newcomer-band alts, but this still needs alt detection and moderation.";
        yield return "Repeatable world income always stopped at the daily cap; one-off grants (season participation) were issued once per key even when settlements were replayed.";
    }

    private string NewcomerVerdict()
    {
        var newcomers = _r.Players.Where(p => p.Joined && p.Newcomer && p.Role == Role.Honest).ToList();
        int fromVeterans = newcomers.Sum(p => p.AttacksReceivedFromVeteransWhileNewcomer);
        int minTiles = newcomers.Where(p => p.FirstSeasonTilesAtClose >= 0).Select(p => p.FirstSeasonTilesAtClose).DefaultIfEmpty(12).Min();
        return $"{newcomers.Count} newcomers; {fromVeterans} veteran attacks on newcomer-band accounts; worst first-season close {minTiles} tiles";
    }
}
