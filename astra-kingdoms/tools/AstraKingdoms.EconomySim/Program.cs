using System.Diagnostics;
using System.Globalization;

namespace AstraKingdoms.EconomySim;

/// <summary>
/// V4 economy discovery simulator. Simulates players over several 18-day seasons against the real
/// AK-W4-0-proposed world rules and writes a markdown report and a per-season CSV.
/// Usage: dotnet run -c Release --project tools/AstraKingdoms.EconomySim --
///        [--players 400] [--seasons 3] [--shards 2] [--seed 20261006] [--real-encounters 12]
///        [--out reports] [--name economy-discovery-AK-W4-0]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        SimOptions? options = SimOptions.Parse(args);
        if (options == null) return 2;
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"World rules {World.WorldRules.RulesId} hash {World.WorldRules.HashHex}");
        Console.WriteLine($"{options.Players} players, {options.Seasons} seasons, {options.Shards} shards, seed {options.Seed}");

        var sim = new WorldSimulation(options);
        SimulationResult result = sim.Run();
        IReadOnlyList<ProbeResult> probes = ExploitProbes.RunAll();
        sw.Stop();

        var report = new Report(options, result, probes, sw.Elapsed);
        Directory.CreateDirectory(options.OutDir);
        string md = Path.Combine(options.OutDir, options.Name + ".md");
        string csv = Path.Combine(options.OutDir, options.Name + ".csv");
        File.WriteAllText(md, report.Markdown());
        File.WriteAllText(csv, report.Csv());
        Console.WriteLine(report.ConsoleSummary());
        Console.WriteLine($"Finished in {sw.Elapsed.TotalSeconds:F1}s. Wrote {md} and {csv}.");
        return result.InvariantViolations.Count == 0 && probes.All(p => !p.IsExploit) ? 0 : 1;
    }
}

internal sealed class SimOptions
{
    public int Players = 400;
    public int Seasons = 3;
    public int Shards = 2;
    public ulong Seed = 20261006;
    public int RealEncounters = 12;
    public int RingAlts = 6;
    public string OutDir = "reports";
    public string Name = "economy-discovery-AK-W4-0";

    public static SimOptions? Parse(string[] args)
    {
        var o = new SimOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value for " + a);
            switch (a)
            {
                case "--players": o.Players = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seasons": o.Seasons = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--shards": o.Shards = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = ulong.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--real-encounters": o.RealEncounters = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--ring-alts": o.RingAlts = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--out": o.OutDir = Next(); break;
                case "--name": o.Name = Next(); break;
                case "-h":
                case "--help":
                    Console.WriteLine("Options: --players N --seasons S --shards K --seed X --real-encounters R --ring-alts A --out DIR --name NAME");
                    return null;
                default:
                    Console.Error.WriteLine("Unknown option " + a);
                    return null;
            }
        }
        o.Players = Math.Max(40, o.Players);
        o.Seasons = Math.Max(1, o.Seasons);
        o.Shards = Math.Max(1, o.Shards);
        return o;
    }
}
