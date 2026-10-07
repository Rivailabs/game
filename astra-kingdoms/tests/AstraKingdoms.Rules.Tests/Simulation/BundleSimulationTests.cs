using AstraKingdoms.Rules.Balance;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Sim;

namespace AstraKingdoms.Rules.Tests.Simulation;

/// <summary>Ticket 24 tooling: the simulator runs pinned to a balance bundle and compares it with the baseline.</summary>
public class BundleSimulationTests
{
    private static readonly BalanceBundle Tuned = new("AK-TR-1.s1", RulesConstants.RulesVersion, new[]
    {
        new KeyValuePair<string, long>("Duel.StartHpUnits", 12000),
        new KeyValuePair<string, long>("Damage.RiverHealUnits", 500),
    }, RulesConstants.RulesVersion, "simulation test");

    private static MatchObservation Observe(MatchConfig config, int n, BotDifficulty a, BotDifficulty b)
    {
        BotMatchRunner.SeedFor(91, n, out byte[] seed, out string id);
        MatchEngine e = BotMatchRunner.Run(config, seed, id, BotPlayer.Create(PlayerSide.A, a, seed), BotPlayer.Create(PlayerSide.B, b, seed));
        return MatchObservation.FromEngine(e, SeatInfo.Bot(a.ToString()), SeatInfo.Bot(b.ToString()), MatchObservation.BotSimulation, 0);
    }

    [Test]
    public void Observation_UsesThePinnedStartingHp()
    {
        BotMatchRunner.SeedFor(91, 1, out byte[] seed, out string id);
        MatchEngine e = BotMatchRunner.Run(MatchConfig.V1Full().WithParameters(Tuned.ToParameters()), seed, id,
            BotPlayer.Create(PlayerSide.A, BotDifficulty.Hard, seed), BotPlayer.Create(PlayerSide.B, BotDifficulty.Hard, seed));
        MatchObservation o = MatchObservation.FromEngine(e, SeatInfo.Bot("Hard"), SeatInfo.Bot("Hard"), MatchObservation.BotSimulation);
        Assert.That(o.Volleys[0].NetA, Is.EqualTo(e.Rounds[0].Volleys[0].HpA - 12000), "net HP change is measured from the tuned 120.00 HP start");
        Assert.That(o.Volleys[0].NetB, Is.EqualTo(e.Rounds[0].Volleys[0].HpB - 12000));
    }

    [Test]
    public void Comparison_IsLabelledProposed_AndListsOverridesAndFlags()
    {
        var pairs = new[] { (BotDifficulty.Hard, BotDifficulty.Hard), (BotDifficulty.Normal, BotDifficulty.Easy) };
        var baseline = new List<MatchObservation>();
        var proposed = new List<MatchObservation>();
        for (int n = 0; n < 6; n++)
        {
            var (a, b) = pairs[n % pairs.Length];
            baseline.Add(Observe(MatchConfig.V1Full(), n, a, b));
            proposed.Add(Observe(MatchConfig.V1Full().WithParameters(Tuned.ToParameters()), n, a, b));
        }
        Assert.That(proposed.All(o => o.RulesHashHex == Tuned.EffectiveRulesHashHex), Is.True);
        Assert.That(baseline.All(o => o.RulesHashHex == RulesBundle.HashHex), Is.True);

        var cmp = new BundleComparison(Tuned, baseline, proposed, new[] { "test run" });
        string md = cmp.Markdown();
        Assert.That(md, Does.Contain("PROPOSED - NOT ADOPTED"));
        Assert.That(md, Does.Contain("`Duel.StartHpUnits` | 10000 | 12000 | +20.0%"));
        Assert.That(md, Does.Contain(Tuned.EffectiveRulesHashHex));
        Assert.That(md, Does.Contain("Paired matches whose final result or cell count changed"));
        Assert.That(md, Does.Contain("of 6."));
        string csv = cmp.Csv();
        Assert.That(csv.Split('\n')[0], Does.StartWith("section,kind,key,baseline_n"));
        Assert.That(csv, Does.Contain("screening,element,Agni"));
    }

    [Test]
    public void Ingest_VerifiesTunedPlaytestRecordsOnlyAgainstAKnownBundle()
    {
        BotMatchRunner.SeedFor(91, 2, out byte[] seed, out string id);
        MatchEngine e = BotMatchRunner.Run(MatchConfig.V1Full().WithParameters(Tuned.ToParameters()), seed, id,
            BotPlayer.Create(PlayerSide.A, BotDifficulty.Normal, seed), BotPlayer.Create(PlayerSide.B, BotDifficulty.Hard, seed));
        string json = PlaytestRecord.Write(MatchRecord.FromEngine(e), SeatInfo.Human("T07", 5), SeatInfo.Bot("Hard"), "pt-b", "2026-10-06T10:00:00Z", "test", false);

        Assert.That(PlaytestRecord.Read(json, false, out string? reason), Is.Null);
        Assert.That(reason, Does.Contain("UnknownBundle"));
        MatchObservation? o = PlaytestRecord.Read(json, false, b => b == Tuned.BundleId ? Tuned : null, out reason);
        Assert.That(o, Is.Not.Null, reason);
        Assert.That(o!.Config.RulesVersion, Is.EqualTo("AK-TR-1.s1"));
    }

    [Test]
    public void ShippedProposal_IsAValidExecutableBundleMarkedProposed()
    {
        string? dir = TestContext.CurrentContext.TestDirectory;
        string? file = null;
        while (dir != null && file == null)
        {
            string candidate = Path.Combine(dir, "tools", "AstraKingdoms.Sim", "examples", "AK-TR-1.p1.PROPOSED.balance.json");
            if (File.Exists(candidate)) file = candidate;
            dir = Path.GetDirectoryName(dir);
        }
        Assert.That(file, Is.Not.Null, "the proposal file ships with the simulator");
        BalanceBundle p = BalanceBundle.FromJson(File.ReadAllText(file!));
        Assert.That(BalanceValidator.Validate(p), Is.Empty);
        Assert.That(BalanceChannel.ParameterizedEngine(p), Is.True);
        Assert.That(p.Notes, Does.StartWith("PROPOSED, NOT ADOPTED"));
        Assert.That(p.EffectiveRulesHashHex, Is.Not.EqualTo(RulesBundle.HashHex));
    }
}
