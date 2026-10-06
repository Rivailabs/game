using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.V2.Integration;
using NUnit.Framework;

namespace AstraKingdoms.V2.Tests;

internal static class TestUtil
{
    public static readonly DateTimeOffset Start = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero);

    public static ManualClock Clock() => new(Start - TimeSpan.FromDays(1));

    /// <summary>A V2 environment with season 1 published (8-minute measured maximum match).</summary>
    public static V2Environment Env(out ManualClock clock, int seasons = 1)
    {
        clock = Clock();
        var env = new V2Environment(clock, Start, TimeSpan.FromMinutes(8));
        for (int i = 0; i < seasons; i++) env.PublishNextSeason();
        return env;
    }

    /// <summary>Gives a player completed matches (and therefore coins) through Meta's real progression service.</summary>
    public static void PlayMatches(V2Environment env, string player, int count, string prefix = "m", int[] weapons = null)
    {
        for (int i = 0; i < count; i++)
            env.OnMatchReport(new MatchOutcomeReport(prefix + i, player, MatchKind.OnlineHuman, PlayerOutcome.Win, MatchEnding.RoundsComplete,
                env.Clock.UtcNow, weapons ?? new[] { 1 + (i % 3) }, CatalogPreset.Full));
    }

    public static MatchRecord BotRecord(long n = 1, bool full = true)
    {
        BotMatchRunner.SeedFor(20261006, n, out byte[] seed, out string id);
        MatchEngine e = BotMatchRunner.Run(full ? MatchConfig.V1Full(MatchMode.Practice) : MatchConfig.V1Starter(MatchMode.Practice), seed, id,
            BotPlayer.Create(PlayerSide.A, BotDifficulty.Hard, seed), BotPlayer.Create(PlayerSide.B, BotDifficulty.Normal, seed));
        Assert.That(e.Result, Is.Not.Null);
        return MatchRecord.FromEngine(e);
    }

    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AstraKingdoms.sln"))) dir = dir.Parent;
            Assert.That(dir, Is.Not.Null);
            return dir.FullName;
        }
    }
}
