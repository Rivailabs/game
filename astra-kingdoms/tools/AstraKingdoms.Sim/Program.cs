using System.Diagnostics;
using System.Globalization;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Sim;

/// <summary>
/// Balance screening and review (tickets 23 and 25).
/// <list type="bullet">
/// <item>Bot mode: N bot-vs-bot matches over six policy pairings with mirrored seats, optionally
/// with unlock cohorts (<c>--cohorts</c>).</item>
/// <item>Ingest mode: the same report over verified human playtest records (<c>--ingest DIR</c>).</item>
/// </list>
/// Usage: dotnet run --project tools/AstraKingdoms.Sim -- [--matches 10000] [--seed 20261006]
///        [--catalog full|starter|pilot] [--cohorts] [--out reports] [--name bot-screening] [--threads T]
///        [--ingest DIR [--include-synthetic]] [--write-example FILE]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Options? options = Options.Parse(args);
        if (options == null) return 2;
        if (options.WriteExample != null)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(options.WriteExample));
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(options.WriteExample, PlaytestRecord.SyntheticExample(options.Seed));
            Console.WriteLine("Wrote synthetic example playtest record " + options.WriteExample);
            return 0;
        }
        return options.IngestDir != null ? RunIngest(options) : RunBots(options);
    }

    private static int RunIngest(Options o)
    {
        if (!Directory.Exists(o.IngestDir))
        {
            Console.Error.WriteLine("No such folder: " + o.IngestDir);
            return 2;
        }
        IngestResult ingest = PlaytestRecord.IngestDirectory(o.IngestDir!, o.IncludeSynthetic);
        var ctx = new ReportContext
        {
            Title = "Astra Kingdoms balance review (playtest records)",
            SourceDescription = "AK-PLAYTEST-RECORD/1 files under " + o.IngestDir + ", each re-executed by the replay verifier",
            RunLines = { $"This build runs rules {RulesBundle.Version}, hash `{RulesBundle.HashHex}` (records of other hashes are excluded)" },
            Excluded = ingest.Excluded,
        };
        Write(o, new BalanceReport(ctx, ingest.Observations));
        Console.WriteLine($"Ingested {ingest.Observations.Count} matches, excluded {ingest.Excluded.Count}.");
        return 0;
    }

    private static int RunBots(Options options)
    {
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

        // Mirrored pairs: game 2k and 2k+1 share a seed; the policies (and cohorts) swap seats.
        int pairsPerPairing = Math.Max(1, (options.Matches + 2 * pairings.Length - 1) / (2 * pairings.Length));
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
        Console.WriteLine($"Config {config}; {total} matches ({pairings.Length} pairings x {pairsPerPairing} mirrored pairs); base seed {options.Seed}; cohorts {(options.Cohorts ? "on" : "off")}");

        _ = RulesBundle.Hash; // warm static tables before timing
        var results = new MatchObservation?[total];
        var errors = new string?[total];
        var sw = Stopwatch.StartNew();
        int done = 0;
        Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = options.Threads }, i =>
        {
            var job = jobs[i];
            try
            {
                results[i] = PlayOne(config, options, pairings[job.Pairing], job.Pairing, job.Pair, job.Swapped);
            }
            catch (Exception e)
            {
                errors[i] = e.GetType().Name + ": " + e.Message;
            }
            int n = Interlocked.Increment(ref done);
            if (n % 500 == 0) Console.WriteLine($"  {n}/{total} matches ({sw.Elapsed.TotalSeconds:F0}s)");
        });
        sw.Stop();

        var ctx = new ReportContext
        {
            Title = "Astra Kingdoms bot-policy screening report",
            SourceDescription = "bot simulation through the authoritative engine (bots see only their private view; any rejected command is an error)",
            RunLines =
            {
                $"Rules: `{RulesBundle.Version}`, rules hash `{RulesBundle.HashHex}`",
                $"Config: {config}",
                $"Matches: {results.Count(r => r != null)} completed, {errors.Count(e => e != null)} errors; base seed {options.Seed}; {options.Threads} threads; {sw.Elapsed.TotalSeconds:F1} s",
                "Pairings (mirrored pairs: same seed, policies swap seats): " + string.Join(", ", pairings.Select(p => p.X + " v " + p.Y)),
                options.Cohorts
                    ? "Unlock cohorts ON: each seat gets an account level from {" + string.Join(", ", UnlockCohorts.SimulatedLevels) + "} and equips only weapons unlocked at that level (familiarity assumption)"
                    : "Unlock cohorts off (every bot may equip the whole loaned catalogue)",
            },
            Errors = errors.Where(e => e != null).Select(e => e!).ToList(),
        };
        Write(options, new BalanceReport(ctx, results.Where(r => r != null).Select(r => r!)));
        return errors.Any(e => e != null) ? 1 : 0;
    }

    /// <summary>Plays one simulated match and returns its observation.</summary>
    internal static MatchObservation PlayOne(MatchConfig config, Options options, (BotDifficulty X, BotDifficulty Y) pairing, int pairingIndex, int pair, bool swapped)
    {
        BotDifficulty a = swapped ? pairing.Y : pairing.X;
        BotDifficulty b = swapped ? pairing.X : pairing.Y;
        BotMatchRunner.SeedFor(options.Seed, (long)pairingIndex * 1_000_000 + pair, out byte[] seed, out string matchId);
        int? levelX = null, levelY = null;
        if (options.Cohorts)
        {
            // Both levels come from the pair index, so the mirrored game swaps them with the policies.
            var rng = new BotRng(options.Seed ^ (ulong)(pairingIndex * 7919 + pair) * 0x9E3779B97F4A7C15UL);
            levelX = UnlockCohorts.SimulatedLevels[rng.Next(UnlockCohorts.SimulatedLevels.Length)];
            levelY = UnlockCohorts.SimulatedLevels[rng.Next(UnlockCohorts.SimulatedLevels.Length)];
        }
        int? levelA = swapped ? levelY : levelX, levelB = swapped ? levelX : levelY;
        BotPlayer botA = Seat(PlayerSide.A, a, levelA, seed), botB = Seat(PlayerSide.B, b, levelB, seed);
        MatchEngine engine = BotMatchRunner.Run(config, seed, matchId, botA, botB);
        return MatchObservation.FromEngine(engine, SeatInfo.Bot(a.ToString(), levelA), SeatInfo.Bot(b.ToString(), levelB),
            MatchObservation.BotSimulation, pairingIndex);
    }

    private static BotPlayer Seat(PlayerSide side, BotDifficulty d, int? level, byte[] seed)
    {
        BotPlayer standard = BotPlayer.Create(side, d, seed);
        if (level == null) return standard;
        var policy = new FamiliarWeaponsPolicy(standard.Policy, level.Value, BotRng.FromMatchSeed(seed, side, 0xC0_4047UL));
        return new BotPlayer(side, policy, BotRng.FromMatchSeed(seed, side, 0x1D5_0000UL + (ulong)d));
    }

    private static void Write(Options o, BalanceReport report)
    {
        Directory.CreateDirectory(o.OutDir);
        string md = Path.Combine(o.OutDir, o.Name + ".md");
        string csv = Path.Combine(o.OutDir, o.Name + ".csv");
        File.WriteAllText(md, report.Markdown());
        File.WriteAllText(csv, report.Csv());
        Console.WriteLine($"Wrote {md} and {csv}.");
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
    public bool Cohorts;
    public string? IngestDir;
    public bool IncludeSynthetic;
    public string? WriteExample;

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        bool nameSet = false;
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
                case "--name": o.Name = Next(); nameSet = true; break;
                case "--threads": o.Threads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--cohorts": o.Cohorts = true; break;
                case "--ingest": o.IngestDir = Next(); break;
                case "--include-synthetic": o.IncludeSynthetic = true; break;
                case "--write-example": o.WriteExample = Next(); break;
                case "-h":
                case "--help":
                    Console.WriteLine("Options: --matches N --seed S --catalog full|starter|pilot --cohorts --out DIR --name NAME --threads T");
                    Console.WriteLine("         --ingest DIR [--include-synthetic]   report over AK-PLAYTEST-RECORD/1 files");
                    Console.WriteLine("         --write-example FILE                 write a labelled synthetic playtest record");
                    return null;
                default:
                    Console.Error.WriteLine("Unknown option " + a);
                    return null;
            }
        }
        if (o.Matches < 12) o.Matches = 12;
        if (!nameSet && o.IngestDir != null) o.Name = "playtest-review";
        return o;
    }
}
