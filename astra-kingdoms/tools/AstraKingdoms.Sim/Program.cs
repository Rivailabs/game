using System.Diagnostics;
using System.Globalization;
using AstraKingdoms.Rules.Balance;
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
/// <item>Balance bundles (ticket 24): <c>--bundle FILE</c> pins every simulated match to an
/// <c>AK-BALANCE-BUNDLE/1</c> release (or lets ingest verify records of that bundle);
/// <c>--compare</c> also runs the AK-TR-1 baseline on the same seeds and writes a PROPOSED
/// side-by-side screening comparison.</item>
/// </list>
/// Usage: dotnet run --project tools/AstraKingdoms.Sim -- [--matches 10000] [--seed 20261006]
///        [--catalog full|starter|pilot] [--cohorts] [--out reports] [--name bot-screening] [--threads T]
///        [--bundle FILE [--compare]] [--ingest DIR [--include-synthetic]] [--write-example FILE]
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
        BalanceBundle? bundle = null;
        if (options.BundlePath != null)
        {
            bundle = LoadBundle(options.BundlePath);
            if (bundle == null) return 2;
        }
        if (options.IngestDir != null) return RunIngest(options, bundle);
        return bundle != null && options.Compare ? RunCompare(options, bundle) : RunBots(options, bundle);
    }

    /// <summary>Reads and validates a balance bundle file; prints the issues and returns null when it cannot run.</summary>
    internal static BalanceBundle? LoadBundle(string path)
    {
        BalanceBundle bundle;
        try
        {
            bundle = BalanceBundle.FromJson(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException || e is FormatException || e is ArgumentException || e is OverflowException)
        {
            Console.Error.WriteLine("Cannot read balance bundle " + path + ": " + e.Message);
            return null;
        }
        IReadOnlyList<BalanceIssue> issues = BalanceValidator.Validate(bundle);
        if (issues.Count > 0)
        {
            Console.Error.WriteLine("Balance bundle " + bundle.BundleId + " fails validation:");
            foreach (BalanceIssue i in issues) Console.Error.WriteLine("  " + i);
            return null;
        }
        return bundle;
    }

    /// <summary>The room preset for <c>--catalog</c>, pinned to the bundle when one is given.</summary>
    internal static MatchConfig Room(string catalog, BalanceBundle? bundle)
    {
        MatchConfig config = catalog switch
        {
            "starter" => MatchConfig.V1Starter(MatchMode.Online),
            "pilot" => MatchConfig.Pilot(MatchMode.Online),
            _ => MatchConfig.V1Full(MatchMode.Online),
        };
        return bundle == null ? config : config.WithParameters(bundle.ToParameters());
    }

    private static string RulesLine(MatchConfig config) =>
        config.Parameters.IsDefault
            ? $"Rules: `{RulesBundle.Version}`, rules hash `{RulesBundle.HashHex}`"
            : $"Rules: balance bundle `{config.RulesVersion}` on `{config.Parameters.BaseRulesVersion}`, effective rules hash `{config.Parameters.RulesHashHex}`, bundle content `{config.Parameters.BalanceContentHashHex}`";

    private static int RunIngest(Options o, BalanceBundle? bundle)
    {
        if (!Directory.Exists(o.IngestDir))
        {
            Console.Error.WriteLine("No such folder: " + o.IngestDir);
            return 2;
        }
        Func<string, BalanceBundle?>? resolver = bundle == null ? null : id => id == bundle.BundleId ? bundle : null;
        IngestResult ingest = PlaytestRecord.IngestDirectory(o.IngestDir!, o.IncludeSynthetic, resolver);
        string bundleNote = bundle == null ? "" : $"; records pinned to balance bundle `{bundle.BundleId}` (content `{bundle.ContentHashHex}`) are verified against it";
        var ctx = new ReportContext
        {
            Title = "Astra Kingdoms balance review (playtest records)",
            SourceDescription = "AK-PLAYTEST-RECORD/1 files under " + o.IngestDir + ", each re-executed by the replay verifier",
            RunLines = { $"This build runs rules {RulesBundle.Version}, hash `{RulesBundle.HashHex}` (records of other hashes are excluded){bundleNote}" },
            Excluded = ingest.Excluded,
        };
        Write(o, new BalanceReport(ctx, ingest.Observations));
        Console.WriteLine($"Ingested {ingest.Observations.Count} matches, excluded {ingest.Excluded.Count}.");
        return 0;
    }

    private static readonly (BotDifficulty X, BotDifficulty Y)[] Pairings =
    {
        (BotDifficulty.Hard, BotDifficulty.Hard),
        (BotDifficulty.Normal, BotDifficulty.Normal),
        (BotDifficulty.Easy, BotDifficulty.Easy),
        (BotDifficulty.Hard, BotDifficulty.Normal),
        (BotDifficulty.Hard, BotDifficulty.Easy),
        (BotDifficulty.Normal, BotDifficulty.Easy),
    };

    /// <summary>Outcome of one batch of simulated matches.</summary>
    private sealed record Batch(List<MatchObservation> Results, List<string> Errors, int Total, TimeSpan Elapsed);

    private static int RunBots(Options options, BalanceBundle? bundle)
    {
        MatchConfig config = Room(options.Catalog, bundle);
        Console.WriteLine(RulesLine(config).Replace("`", ""));
        Batch batch = Simulate(config, options);
        var ctx = new ReportContext
        {
            Title = bundle == null ? "Astra Kingdoms bot-policy screening report" : $"Astra Kingdoms bot-policy screening report (balance bundle {bundle.BundleId})",
            SourceDescription = "bot simulation through the authoritative engine (bots see only their private view; any rejected command is an error)",
            RunLines = RunLines(config, options, batch),
            Errors = batch.Errors,
        };
        Write(options, new BalanceReport(ctx, batch.Results));
        return batch.Errors.Count > 0 ? 1 : 0;
    }

    /// <summary>Baseline and bundle on identical jobs, then the PROPOSED comparison report.</summary>
    private static int RunCompare(Options options, BalanceBundle bundle)
    {
        MatchConfig baseConfig = Room(options.Catalog, null);
        MatchConfig tunedConfig = Room(options.Catalog, bundle);
        Console.WriteLine("Baseline: " + RulesLine(baseConfig).Replace("`", ""));
        Batch baseline = Simulate(baseConfig, options);
        Console.WriteLine("Proposed: " + RulesLine(tunedConfig).Replace("`", ""));
        Batch tuned = Simulate(tunedConfig, options);

        var lines = new List<string>
        {
            "Baseline " + RulesLine(baseConfig),
            "Proposed " + RulesLine(tunedConfig),
            $"Config: {tunedConfig}",
            $"Matches per column: {baseline.Results.Count} / {tuned.Results.Count} completed, {baseline.Errors.Count} / {tuned.Errors.Count} errors; base seed {options.Seed}; {options.Threads} threads; {baseline.Elapsed.TotalSeconds:F1} s + {tuned.Elapsed.TotalSeconds:F1} s",
            "Pairings (mirrored pairs: same seed, policies swap seats): " + string.Join(", ", Pairings.Select(p => p.X + " v " + p.Y)),
            CohortLine(options),
        };
        var comparison = new BundleComparison(bundle, baseline.Results, tuned.Results, lines);
        Directory.CreateDirectory(options.OutDir);
        string md = Path.Combine(options.OutDir, options.Name + ".md");
        string csv = Path.Combine(options.OutDir, options.Name + ".csv");
        File.WriteAllText(md, comparison.Markdown());
        File.WriteAllText(csv, comparison.Csv());
        Console.WriteLine($"Wrote {md} and {csv}.");
        Console.WriteLine($"FLAG count {comparison.BaselineFlags.Count} -> {comparison.ProposedFlags.Count} (PROPOSED, not adopted).");
        foreach (string e in baseline.Errors.Concat(tuned.Errors).Take(5)) Console.Error.WriteLine("  error: " + e);
        return baseline.Errors.Count + tuned.Errors.Count > 0 ? 1 : 0;
    }

    private static string CohortLine(Options options) =>
        options.Cohorts
            ? "Unlock cohorts ON: each seat gets an account level from {" + string.Join(", ", UnlockCohorts.SimulatedLevels) + "} and equips only weapons unlocked at that level (familiarity assumption)"
            : "Unlock cohorts off (every bot may equip the whole loaned catalogue)";

    private static List<string> RunLines(MatchConfig config, Options options, Batch batch) => new()
    {
        RulesLine(config),
        $"Config: {config}",
        $"Matches: {batch.Results.Count} completed, {batch.Errors.Count} errors; base seed {options.Seed}; {options.Threads} threads; {batch.Elapsed.TotalSeconds:F1} s",
        "Pairings (mirrored pairs: same seed, policies swap seats): " + string.Join(", ", Pairings.Select(p => p.X + " v " + p.Y)),
        CohortLine(options),
    };

    /// <summary>Plays every job of the run under <paramref name="config"/> (deterministic per job).</summary>
    private static Batch Simulate(MatchConfig config, Options options)
    {
        var pairings = Pairings;

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

        Console.WriteLine($"Config {config}; {total} matches ({pairings.Length} pairings x {pairsPerPairing} mirrored pairs); base seed {options.Seed}; cohorts {(options.Cohorts ? "on" : "off")}");

        _ = config.Parameters.RulesHash; // warm static tables before timing
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
        return new Batch(results.Where(r => r != null).Select(r => r!).ToList(), errors.Where(e => e != null).Select(e => e!).ToList(), total, sw.Elapsed);
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
    public string? BundlePath;
    public bool Compare;

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
                case "--bundle": o.BundlePath = Next(); break;
                case "--compare": o.Compare = true; break;
                case "-h":
                case "--help":
                    Console.WriteLine("Options: --matches N --seed S --catalog full|starter|pilot --cohorts --out DIR --name NAME --threads T");
                    Console.WriteLine("         --ingest DIR [--include-synthetic]   report over AK-PLAYTEST-RECORD/1 files");
                    Console.WriteLine("         --write-example FILE                 write a labelled synthetic playtest record");
                    Console.WriteLine("         --bundle FILE                        pin matches to an AK-BALANCE-BUNDLE/1 release (ingest: verify its records)");
                    Console.WriteLine("         --compare                            with --bundle: also run the baseline and write a PROPOSED comparison");
                    return null;
                default:
                    Console.Error.WriteLine("Unknown option " + a);
                    return null;
            }
        }
        if (o.Matches < 12) o.Matches = 12;
        if (o.Compare && o.BundlePath == null)
        {
            Console.Error.WriteLine("--compare needs --bundle FILE");
            return null;
        }
        if (!nameSet && o.IngestDir != null) o.Name = "playtest-review";
        if (!nameSet && o.Compare) o.Name = "bundle-comparison";
        return o;
    }
}
