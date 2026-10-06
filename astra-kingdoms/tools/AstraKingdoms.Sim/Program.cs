using System.Diagnostics;
using System.Globalization;
using System.Text;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Sim;

/// <summary>
/// Runs N bot-vs-bot matches over several policy pairings with mirrored seats and writes a
/// markdown report and a CSV of stratified outcomes with Wilson 95% intervals.
/// Usage: dotnet run --project tools/AstraKingdoms.Sim -- [--matches 10000] [--seed 20261006]
///        [--catalog full|starter|pilot] [--out reports] [--name bot-screening]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options == null) return 2;

        MatchConfig config = options.Catalog switch
        {
            "starter" => MatchConfig.V1Starter(MatchMode.Online),
            "pilot" => MatchConfig.Pilot(MatchMode.Online),
            _ => MatchConfig.V1Full(MatchMode.Online),
        };

        var pairings = new (BotDifficulty X, BotDifficulty Y)[]
        {
            (BotDifficulty.Hard, BotDifficulty.Hard),
            (BotDifficulty.Normal, BotDifficulty.Normal),
            (BotDifficulty.Easy, BotDifficulty.Easy),
            (BotDifficulty.Hard, BotDifficulty.Normal),
            (BotDifficulty.Hard, BotDifficulty.Easy),
            (BotDifficulty.Normal, BotDifficulty.Easy),
        };

        // Mirrored pairs: game 2k and 2k+1 share a seed; the policies swap seats.
        int pairsPerPairing = Math.Max(1, (options.Matches + 2 * pairings.Length - 1) / (2 * pairings.Length)); // round up
        int total = pairsPerPairing * 2 * pairings.Length;
        var jobs = new (int Pairing, int Pair, bool Swapped)[total];
        int j = 0;
        for (int p = 0; p < pairings.Length; p++)
            for (int k = 0; k < pairsPerPairing; k++)
            {
                jobs[j++] = (p, k, false);
                jobs[j++] = (p, k, true);
            }

        Console.WriteLine($"Rules {RulesBundle.Version} hash {RulesBundle.HashHex}");
        Console.WriteLine($"Config {config}; {total} matches ({pairings.Length} pairings x {pairsPerPairing} mirrored pairs); base seed {options.Seed}");

        // Warm static tables before timing (terrain template, rules hash).
        _ = RulesBundle.Hash;
        var results = new MatchSummary?[total];
        var errors = new string?[total];
        var sw = Stopwatch.StartNew();
        int done = 0;
        Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = options.Threads }, i =>
        {
            var job = jobs[i];
            var (x, y) = pairings[job.Pairing];
            BotDifficulty a = job.Swapped ? y : x;
            BotDifficulty b = job.Swapped ? x : y;
            BotMatchRunner.SeedFor(options.Seed, (long)job.Pairing * 1_000_000 + job.Pair, out byte[] seed, out string matchId);
            try
            {
                MatchEngine engine = BotMatchRunner.Run(config, seed, matchId,
                    BotPlayer.Create(PlayerSide.A, a, seed), BotPlayer.Create(PlayerSide.B, b, seed));
                results[i] = MatchSummary.From(engine, a, b, job.Pairing, pairings[job.Pairing]);
            }
            catch (Exception e)
            {
                errors[i] = e.GetType().Name + ": " + e.Message;
            }
            int n = Interlocked.Increment(ref done);
            if (n % 500 == 0) Console.WriteLine($"  {n}/{total} matches ({sw.Elapsed.TotalSeconds:F0}s)");
        });
        sw.Stop();

        var report = new Report(options, config, pairings, results, errors, sw.Elapsed);
        Directory.CreateDirectory(options.OutDir);
        string md = Path.Combine(options.OutDir, options.Name + ".md");
        string csv = Path.Combine(options.OutDir, options.Name + ".csv");
        File.WriteAllText(md, report.Markdown());
        File.WriteAllText(csv, report.Csv());
        Console.WriteLine($"Finished in {sw.Elapsed.TotalSeconds:F1}s. Wrote {md} and {csv}.");
        return errors.Any(e => e != null) ? 1 : 0;
    }
}

internal sealed class Options
{
    public int Matches = 10_000;
    public ulong Seed = 20261006;
    public string Catalog = "full";
    public string OutDir = "reports";
    public string Name = "bot-screening";
    public int Threads = Environment.ProcessorCount;

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value for " + a);
            switch (a)
            {
                case "--matches": o.Matches = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = ulong.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--catalog": o.Catalog = Next().ToLowerInvariant(); break;
                case "--out": o.OutDir = Next(); break;
                case "--name": o.Name = Next(); break;
                case "--threads": o.Threads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "-h":
                case "--help":
                    Console.WriteLine("Options: --matches N --seed S --catalog full|starter|pilot --out DIR --name NAME --threads T");
                    return null;
                default:
                    Console.Error.WriteLine("Unknown option " + a);
                    return null;
            }
        }
        if (o.Matches < 12) o.Matches = 12;
        return o;
    }
}

/// <summary>Per-volley facts used for usage-conditioned statistics.</summary>
internal readonly record struct VolleyFact(int Round, int Volley, int WeaponA, int WeaponB, int NetA, int NetB);

/// <summary>Per-duel facts.</summary>
internal readonly record struct DuelFact(int Round, TerrainType Terrain, PlayerSide Defender, DuelResult Result, int[] WeaponsA, int[] WeaponsB);

internal sealed class MatchSummary
{
    public int Pairing;
    public bool Mirror;
    public BotDifficulty PolicyA, PolicyB;
    public PlayerSide FirstAttacker;
    public MatchResult Result = null!;
    public List<VolleyFact> Volleys = new();
    public List<DuelFact> Duels = new();
    /// <summary>Cells of A and B after round 4 when the match continued into round 5 (null otherwise).</summary>
    public (int A, int B)? CellsAfterRound4;

    public static MatchSummary From(MatchEngine engine, BotDifficulty a, BotDifficulty b, int pairing, (BotDifficulty X, BotDifficulty Y) pair)
    {
        var s = new MatchSummary
        {
            Pairing = pairing, Mirror = pair.X == pair.Y, PolicyA = a, PolicyB = b,
            FirstAttacker = engine.FirstAttacker, Result = engine.Result!,
        };
        foreach (RoundRecord r in engine.Rounds)
        {
            int hpA = RulesConstants.StartHpUnits, hpB = RulesConstants.StartHpUnits;
            var usedA = new SortedSet<int>();
            var usedB = new SortedSet<int>();
            foreach (VolleyRecord v in r.Volleys)
            {
                s.Volleys.Add(new VolleyFact(r.Round, v.Volley, v.WeaponA, v.WeaponB, v.HpA - hpA, v.HpB - hpB));
                hpA = v.HpA;
                hpB = v.HpB;
                if (WeaponCatalog.IsRegularId(v.WeaponA)) usedA.Add(v.WeaponA);
                if (WeaponCatalog.IsRegularId(v.WeaponB)) usedB.Add(v.WeaponB);
            }
            if (r.DuelResult != DuelResult.InProgress)
                s.Duels.Add(new DuelFact(r.Round, r.Terrain, Board(r.Attacker), r.DuelResult, usedA.ToArray(), usedB.ToArray()));
            // Checkpoint: round 4 finished and the match continued into round 5.
            if (r.Round == 4 && engine.Rounds.Count >= 5)
                s.CellsAfterRound4 = (r.CellsA, r.CellsB);
        }
        return s;
    }

    private static PlayerSide Board(PlayerSide attacker) => attacker == PlayerSide.A ? PlayerSide.B : PlayerSide.A;
}

/// <summary>Binary outcome tally with a Wilson 95% interval.</summary>
internal sealed class Tally
{
    public long Wins, Losses, Draws;
    public long Decisive => Wins + Losses;
    public long Total => Wins + Losses + Draws;

    public void Add(int sign)
    {
        if (sign > 0) Wins++;
        else if (sign < 0) Losses++;
        else Draws++;
    }

    /// <summary>Win share among decisive outcomes.</summary>
    public double Rate => Decisive == 0 ? double.NaN : (double)Wins / Decisive;

    public (double Lo, double Hi) Wilson() => Stats.Wilson(Wins, Decisive);
}

internal static class Stats
{
    public static (double Lo, double Hi) Wilson(long k, long n, double z = 1.96)
    {
        if (n == 0) return (double.NaN, double.NaN);
        double p = (double)k / n;
        double z2 = z * z;
        double denom = 1 + z2 / n;
        double center = (p + z2 / (2.0 * n)) / denom;
        double half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / denom;
        return (Math.Max(0, center - half), Math.Min(1, center + half));
    }

    public static string Pct(double v) => double.IsNaN(v) ? "n/a" : (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    public static string Interval((double Lo, double Hi) ci) =>
        double.IsNaN(ci.Lo) ? "n/a" : Pct(ci.Lo) + " - " + Pct(ci.Hi);
}

internal sealed class Report
{
    private readonly Options _o;
    private readonly MatchConfig _config;
    private readonly (BotDifficulty X, BotDifficulty Y)[] _pairings;
    private readonly List<MatchSummary> _ok;
    private readonly List<string> _errors;
    private readonly TimeSpan _elapsed;
    private readonly List<string[]> _csv = new();

    public Report(Options o, MatchConfig config, (BotDifficulty X, BotDifficulty Y)[] pairings, MatchSummary?[] results, string?[] errors, TimeSpan elapsed)
    {
        _o = o;
        _config = config;
        _pairings = pairings;
        _ok = results.Where(r => r != null).Select(r => r!).ToList();
        _errors = errors.Where(e => e != null).Select(e => e!).ToList();
        _elapsed = elapsed;
    }

    private static int Sign(int v) => v > 0 ? 1 : v < 0 ? -1 : 0;

    private static int Outcome(MatchResult r, PlayerSide side) =>
        r.Winner == null ? 0 : r.Winner == side ? 1 : -1;

    private void Row(StringBuilder sb, string metric, string group, string key, Tally t, string? extra = null)
    {
        var ci = t.Wilson();
        sb.Append("| ").Append(key).Append(" | ").Append(t.Total).Append(" | ").Append(t.Wins).Append('/').Append(t.Losses).Append('/').Append(t.Draws)
          .Append(" | ").Append(Stats.Pct(t.Rate)).Append(" | ").Append(Stats.Interval(ci));
        if (extra != null) sb.Append(" | ").Append(extra);
        sb.Append(" |\n");
        _csv.Add(new[]
        {
            metric, group, key, t.Total.ToString(CultureInfo.InvariantCulture), t.Wins.ToString(CultureInfo.InvariantCulture),
            t.Losses.ToString(CultureInfo.InvariantCulture), t.Draws.ToString(CultureInfo.InvariantCulture),
            F(t.Rate), F(ci.Lo), F(ci.Hi),
        });
    }

    private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("F4", CultureInfo.InvariantCulture);

    private const string TableHead = "| Key | n | W/L/D | Win share (decisive) | Wilson 95% |\n|---|---:|---:|---:|---|\n";

    public string Markdown()
    {
        var sb = new StringBuilder();
        var mirror = _ok.Where(m => m.Mirror).ToList();
        sb.Append("# Astra Kingdoms bot-policy screening report\n\n");
        sb.Append("> **These are bot-policy screening results, not balance proof.** Outcomes reflect three scripted policies ");
        sb.Append("(Easy/Normal/Hard) with fixed heuristics; humans will choose weapons, aim and cuts differently. ");
        sb.Append("Use them to spot structural problems and to decide what human tests to run, not to tune constants on a single aggregate.\n\n");
        sb.Append("## Run\n\n");
        sb.Append($"- Rules: `{RulesBundle.Version}`, rules hash `{RulesBundle.HashHex}`\n");
        sb.Append($"- Config: {_config}\n");
        sb.Append($"- Matches: {_ok.Count} completed, {_errors.Count} errors; base seed {_o.Seed}; {_o.Threads} threads; {_elapsed.TotalSeconds:F1} s\n");
        sb.Append("- Pairings (each played as mirrored pairs: same seed, policies swap seats): ");
        sb.Append(string.Join(", ", _pairings.Select(p => p.X + " v " + p.Y))).Append('\n');
        sb.Append("- Bots receive only their private view; all commands go through the authoritative engine (any rejection is an error).\n");
        sb.Append("- Win shares are among decisive outcomes (draws excluded, but counted); intervals are Wilson 95%.\n");
        sb.Append($"- Like-for-like tables (weapons, elements, first attacker, terrain) use the {mirror.Count} mirror-policy matches only.\n\n");
        if (_errors.Count > 0)
        {
            sb.Append("## Errors\n\n");
            foreach (string e in _errors.Distinct().Take(10)) sb.Append("- ").Append(e).Append('\n');
            sb.Append('\n');
        }

        // ---- Terminal reasons and round-8 frequency ----
        sb.Append("## Match length and terminal reasons\n\n| Reason | Matches | Share |\n|---|---:|---:|\n");
        foreach (TerminalReason reason in Enum.GetValues<TerminalReason>())
        {
            int n = _ok.Count(m => m.Result.Reason == reason);
            if (n == 0) continue;
            sb.Append($"| {reason} | {n} | {Stats.Pct((double)n / _ok.Count)} |\n");
            _csv.Add(new[] { "terminal_reason", "all", reason.ToString(), _ok.Count.ToString(CultureInfo.InvariantCulture), n.ToString(CultureInfo.InvariantCulture), "", "", F((double)n / _ok.Count), "", "" });
        }
        int reached8 = _ok.Count(m => m.Result.RoundsPlayed == 8);
        var ci8 = Stats.Wilson(reached8, _ok.Count);
        sb.Append($"\nRound-eight frequency (match reached round 8): **{Stats.Pct((double)reached8 / _ok.Count)}** ({reached8}/{_ok.Count}, Wilson {Stats.Interval(ci8)}).\n");
        int draws = _ok.Count(m => m.Result.IsDraw);
        sb.Append($"Match draws (equal cells after round 8): {draws} ({Stats.Pct((double)draws / _ok.Count)}).\n\n");
        _csv.Add(new[] { "round8_frequency", "all", "reached_round_8", _ok.Count.ToString(CultureInfo.InvariantCulture), reached8.ToString(CultureInfo.InvariantCulture), "", "", F((double)reached8 / _ok.Count), F(ci8.Lo), F(ci8.Hi) });
        sb.Append("| Rounds played | Matches |\n|---:|---:|\n");
        for (int r = 1; r <= 8; r++)
        {
            int n = _ok.Count(m => m.Result.RoundsPlayed == r);
            if (n > 0) sb.Append($"| {r} | {n} |\n");
        }
        sb.Append('\n');

        // ---- Policy pairings ----
        sb.Append("## Policy strength (difficulty differences are observable)\n\nRow policy's match result against the column policy, both seats pooled.\n\n");
        sb.Append(TableHead);
        foreach (var (x, y) in _pairings.Where(p => p.X != p.Y))
        {
            var t = new Tally();
            foreach (var m in _ok.Where(m => m.Pairing == Array.IndexOf(_pairings, (x, y))))
                t.Add(m.PolicyA == x ? Outcome(m.Result, PlayerSide.A) : Outcome(m.Result, PlayerSide.B));
            Row(sb, "policy_pairing", "all", x + " v " + y, t);
        }
        sb.Append('\n');

        // ---- First attacker ----
        sb.Append("## First-attacker effect\n\nMatch result of the player who attacked in round 1.\n\n").Append(TableHead);
        foreach (var group in new[] { ("mirror policies", mirror), ("all matches", _ok) })
        {
            var t = new Tally();
            foreach (var m in group.Item2) t.Add(Outcome(m.Result, m.FirstAttacker));
            Row(sb, "first_attacker", group.Item1, "First attacker (" + group.Item1 + ")", t);
        }
        foreach (var (x, y) in _pairings.Where(p => p.X == p.Y))
        {
            var t = new Tally();
            foreach (var m in mirror.Where(m => m.PolicyA == x)) t.Add(Outcome(m.Result, m.FirstAttacker));
            Row(sb, "first_attacker", x + " mirror", "First attacker, " + x + " v " + y, t);
        }
        sb.Append('\n');

        // ---- Weapons: usage-conditioned volley and duel results ----
        sb.Append("## Weapons (usage-conditioned, mirror policies)\n\n");
        sb.Append("*Volley result*: the user's net HP change in that volley compared with the opponent's (higher wins). ");
        sb.Append("*Duel result*: duels in which the player used the weapon at least once. ");
        sb.Append("Loadout membership alone is never counted.\n\n");
        sb.Append("| Weapon | Volleys used | Volley W/L/D | Volley win share | Wilson 95% | Duels used | Duel W/L/D | Duel win share | Wilson 95% |\n");
        sb.Append("|---|---:|---:|---:|---|---:|---:|---:|---|\n");
        var volleyByWeapon = new Dictionary<int, Tally>();
        var duelByWeapon = new Dictionary<int, Tally>();
        var elementMatrix = new Tally[6, 6];
        for (int a = 0; a < 6; a++) for (int b = 0; b < 6; b++) elementMatrix[a, b] = new Tally();
        foreach (var m in mirror)
        {
            foreach (var v in m.Volleys)
            {
                int sA = Sign(v.NetA - v.NetB);
                if (WeaponCatalog.IsRegularId(v.WeaponA)) Get(volleyByWeapon, v.WeaponA).Add(sA);
                if (WeaponCatalog.IsRegularId(v.WeaponB)) Get(volleyByWeapon, v.WeaponB).Add(-sA);
                if (WeaponCatalog.IsRegularId(v.WeaponA) && WeaponCatalog.IsRegularId(v.WeaponB))
                {
                    int ea = (int)WeaponCatalog.Get(v.WeaponA).Element, eb = (int)WeaponCatalog.Get(v.WeaponB).Element;
                    elementMatrix[ea, eb].Add(sA);
                    elementMatrix[eb, ea].Add(-sA);
                }
            }
            foreach (var d in m.Duels)
            {
                int sA = d.Result == DuelResult.AWins ? 1 : d.Result == DuelResult.BWins ? -1 : 0;
                foreach (int w in d.WeaponsA) Get(duelByWeapon, w).Add(sA);
                foreach (int w in d.WeaponsB) Get(duelByWeapon, w).Add(-sA);
            }
        }
        foreach (WeaponDefinition w in WeaponCatalog.ForPreset(_config.Catalog))
        {
            Tally tv = Get(volleyByWeapon, w.Id), td = Get(duelByWeapon, w.Id);
            sb.Append($"| {w.Id} {w.Name} ({w.Element}) | {tv.Total} | {tv.Wins}/{tv.Losses}/{tv.Draws} | {Stats.Pct(tv.Rate)} | {Stats.Interval(tv.Wilson())} | ");
            sb.Append($"{td.Total} | {td.Wins}/{td.Losses}/{td.Draws} | {Stats.Pct(td.Rate)} | {Stats.Interval(td.Wilson())} |\n");
            AddCsv("weapon_volley", "mirror", w.Id + " " + w.Name, tv);
            AddCsv("weapon_duel", "mirror", w.Id + " " + w.Name, td);
        }
        sb.Append('\n');

        // ---- Element matchups ----
        sb.Append("## Element matchups (volley win share of row element vs column element, mirror policies)\n\n");
        sb.Append("Cells: win share among decisive volleys (n decisive). Diagonal pairs are same-element mirrors.\n\n| Row \\ Col |");
        for (int b = 1; b <= 5; b++) sb.Append(' ').Append((Element)b).Append(" |");
        sb.Append("\n|---|");
        for (int b = 1; b <= 5; b++) sb.Append("---:|");
        sb.Append('\n');
        for (int a = 1; a <= 5; a++)
        {
            sb.Append("| ").Append((Element)a).Append(" |");
            for (int b = 1; b <= 5; b++)
            {
                Tally t = elementMatrix[a, b];
                sb.Append(' ').Append(Stats.Pct(t.Rate)).Append(" (").Append(t.Decisive).Append(") |");
                AddCsv("element_matchup", "mirror", (Element)a + " v " + (Element)b, t);
            }
            sb.Append('\n');
        }
        sb.Append('\n');
        sb.Append("Element totals (volleys, all opponents):\n\n").Append(TableHead);
        for (int a = 1; a <= 5; a++)
        {
            var t = new Tally();
            for (int b = 1; b <= 5; b++)
            {
                if (a == b) continue;
                t.Wins += elementMatrix[a, b].Wins;
                t.Losses += elementMatrix[a, b].Losses;
                t.Draws += elementMatrix[a, b].Draws;
            }
            Row(sb, "element_total", "mirror", ((Element)a).ToString(), t);
        }
        sb.Append('\n');

        // ---- Terrain ----
        sb.Append("## Terrain (defender's duel result, mirror policies)\n\n").Append(TableHead);
        foreach (TerrainType terrain in Enum.GetValues<TerrainType>())
        {
            var t = new Tally();
            foreach (var m in mirror)
                foreach (var d in m.Duels.Where(d => d.Terrain == terrain))
                {
                    int sA = d.Result == DuelResult.AWins ? 1 : d.Result == DuelResult.BWins ? -1 : 0;
                    t.Add(d.Defender == PlayerSide.A ? sA : -sA);
                }
            if (t.Total > 0) Row(sb, "terrain_defender", "mirror", terrain.ToString(), t);
        }
        sb.Append('\n');

        // ---- Comeback cohort ----
        const int threshold = RulesConstants.ActiveCells * 35 / 100; // 17,864 cells
        sb.Append("## Comeback cohort\n\n");
        sb.Append($"Players below 35% of the board ({threshold} cells) after duel four (round 4 completed and the match continued to round 5): their final match result.\n\n");
        sb.Append(TableHead);
        foreach (var group in new[] { ("mirror policies", mirror), ("all matches", _ok) })
        {
            var t = new Tally();
            int checkpoint = 0;
            foreach (var m in group.Item2)
            {
                if (m.CellsAfterRound4 == null) continue;
                checkpoint++;
                var (ca, cb) = m.CellsAfterRound4.Value;
                if (ca < threshold) t.Add(Outcome(m.Result, PlayerSide.A));
                if (cb < threshold) t.Add(Outcome(m.Result, PlayerSide.B));
            }
            Row(sb, "comeback_below35_after_duel4", group.Item1, "Below 35% after duel 4 (" + group.Item1 + ")", t,
                null);
            sb.Append($"\n(Checkpoint reached in {checkpoint} {group.Item1}; cohort size {t.Total}.)\n\n");
            if (group.Item1 == "mirror policies") sb.Append(TableHead);
        }
        sb.Append('\n');

        sb.Append("## Reading notes\n\n");
        sb.Append("- Provisional plan targets: weapon 45-55%, element 47-53% (screening hypotheses, not acceptance criteria).\n");
        sb.Append("- Bot aim is solved against the opponent's baseline pose; dodges therefore matter a lot. Weapon results mix the weapon's ");
        sb.Append("geometry with how each policy picks and aims it, so a weak bot result can mean a weak policy rather than a weak weapon.\n");
        sb.Append("- Hard bots pick weapons by expected element value; usage counts per weapon are therefore uneven.\n");
        sb.Append("- Re-run with `dotnet run -c Release --project tools/AstraKingdoms.Sim -- --matches 10000` for the full workload.\n");
        return sb.ToString();
    }

    private void AddCsv(string metric, string group, string key, Tally t)
    {
        var ci = t.Wilson();
        _csv.Add(new[]
        {
            metric, group, key, t.Total.ToString(CultureInfo.InvariantCulture), t.Wins.ToString(CultureInfo.InvariantCulture),
            t.Losses.ToString(CultureInfo.InvariantCulture), t.Draws.ToString(CultureInfo.InvariantCulture), F(t.Rate), F(ci.Lo), F(ci.Hi),
        });
    }

    private static Tally Get(Dictionary<int, Tally> d, int k)
    {
        if (!d.TryGetValue(k, out Tally? t)) d[k] = t = new Tally();
        return t;
    }

    public string Csv()
    {
        if (_csv.Count == 0) Markdown();
        var sb = new StringBuilder("metric,group,key,n,wins,losses,draws,win_share,wilson_lo,wilson_hi\n");
        foreach (var row in _csv) sb.Append(string.Join(",", row.Select(Escape))).Append('\n');
        return sb.ToString();
    }

    private static string Escape(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
