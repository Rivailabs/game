using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Sim;

namespace AstraKingdoms.Rules.Tests.Simulation;

/// <summary>Ticket 25: the stratified review runs identically over bot matches and verified playtest records.</summary>
public class BalanceReviewTests
{
    private static MatchEngine Play(ulong n, BotDifficulty a, BotDifficulty b, MatchConfig? config = null, IBotPolicy? policyA = null)
    {
        BotMatchRunner.SeedFor(77, (long)n, out byte[] seed, out string id);
        BotPlayer botA = policyA == null ? BotPlayer.Create(PlayerSide.A, a, seed) : new BotPlayer(PlayerSide.A, policyA, BotRng.FromMatchSeed(seed, PlayerSide.A, 9));
        return BotMatchRunner.Run(config ?? MatchConfig.V1Full(MatchMode.Online), seed, id, botA, BotPlayer.Create(PlayerSide.B, b, seed));
    }

    private static string PlaytestJson(MatchEngine engine, SeatInfo a, SeatInfo b, bool synthetic = false) =>
        PlaytestRecord.Write(MatchRecord.FromEngine(engine), a, b, "pt-test", "2026-10-06T10:00:00Z", "test", synthetic);

    [Test]
    public void PlaytestRecord_RoundTripsThroughTheVerifier()
    {
        MatchEngine engine = Play(1, BotDifficulty.Normal, BotDifficulty.Hard);
        string json = PlaytestJson(engine, SeatInfo.Human("T03", 4), SeatInfo.Bot("Hard"));
        MatchObservation? o = PlaytestRecord.Read(json, allowSynthetic: false, out string? reason);
        Assert.That(o, Is.Not.Null, reason);
        Assert.That(o!.Source, Is.EqualTo(MatchObservation.Playtest));
        Assert.That(o.A.IsHuman, Is.True);
        Assert.That(o.A.AccountLevel, Is.EqualTo(4));
        Assert.That(o.B.Label, Is.EqualTo("Hard"));
        Assert.That(o.Result.Winner, Is.EqualTo(engine.Result!.Winner));
        Assert.That(o.LoadoutA, Is.Not.Empty, "loadouts come from the verified command log");
        Assert.That(o.PairingLabel, Is.EqualTo("Hard v human"));
    }

    [Test]
    public void TamperedOrPrivacyUnsafeRecords_AreExcludedWithAReason()
    {
        MatchEngine engine = Play(2, BotDifficulty.Easy, BotDifficulty.Easy);
        string good = PlaytestJson(engine, SeatInfo.Human("T01", 1), SeatInfo.Human("T02", 1));

        string tampered = good.Replace("\"winner\":\"A\"", "\"winner\":\"B\"").Replace("\"winner\":\"B\"", "\"winner\":\"A\"");
        if (tampered == good) tampered = good.Replace("\"rounds_played\":", "\"rounds_played\":1");
        Assert.That(PlaytestRecord.Read(tampered, false, out string? r1), Is.Null);
        Assert.That(r1, Does.Contain("verification").Or.Contain("malformed"));

        string email = good.Replace("\"player_ref\":\"T01\"", "\"player_ref\":\"someone@example.com\"");
        Assert.That(PlaytestRecord.Read(email, false, out string? r2), Is.Null);
        Assert.That(r2, Does.Contain("player_ref"));

        string noConsent = good.Replace("\"consent_recorded\":true", "\"consent_recorded\":false");
        Assert.That(PlaytestRecord.Read(noConsent, false, out string? r3), Is.Null);
        Assert.That(r3, Does.Contain("consent"));

        Assert.That(PlaytestRecord.Read("{not json", false, out string? r4), Is.Null);
        Assert.That(r4, Does.StartWith("malformed"));
    }

    [Test]
    public void SyntheticExamples_AreSkippedUnlessExplicitlyIncluded()
    {
        string example = PlaytestRecord.SyntheticExample();
        Assert.That(PlaytestRecord.Read(example, false, out string? reason), Is.Null);
        Assert.That(reason, Does.Contain("synthetic"));
        MatchObservation? o = PlaytestRecord.Read(example, true, out _);
        Assert.That(o, Is.Not.Null);
        Assert.That(o!.Synthetic, Is.True);
        string md = new BalanceReport(new ReportContext { SourceDescription = "test" }, new[] { o }).Markdown();
        Assert.That(md, Does.Contain("synthetic example records"));
    }

    [Test]
    public void IngestDirectory_ReportsUsableAndExcludedFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ak-ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), PlaytestJson(Play(3, BotDifficulty.Hard, BotDifficulty.Hard), SeatInfo.Human("T1", 2), SeatInfo.Human("T2", 12)));
            File.WriteAllText(Path.Combine(dir, "b.json"), "{}");
            IngestResult r = PlaytestRecord.IngestDirectory(dir, false);
            Assert.That(r.Observations, Has.Count.EqualTo(1));
            Assert.That(r.Excluded.Select(e => e.File), Is.EqualTo(new[] { "b.json" }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Report_HasEveryStratumForHumanAndBotData()
    {
        var obs = new List<MatchObservation>();
        for (ulong i = 0; i < 4; i++)
        {
            MatchEngine e = Play(10 + i, BotDifficulty.Normal, BotDifficulty.Normal);
            obs.Add(MatchObservation.FromEngine(e, SeatInfo.Human("P" + i, 1 + (int)i * 5), SeatInfo.Human("Q" + i, 20 - (int)i * 5), MatchObservation.Playtest));
        }
        obs.Add(MatchObservation.FromEngine(Play(20, BotDifficulty.Hard, BotDifficulty.Easy), SeatInfo.Bot("Hard"), SeatInfo.Bot("Easy"), MatchObservation.BotSimulation));
        var report = new BalanceReport(new ReportContext { SourceDescription = "mixed test data" }, obs);
        string md = report.Markdown();
        foreach (string section in new[]
                 {
                     "## Policy pairings", "## First-attacker effect", "## Unlock cohorts", "## Element matchups",
                     "## Weapons: usage-conditioned versus loadout-contained", "## Terrain", "## Comeback behaviour",
                     "## Screening flags and uncertainty", "Human playtest data",
                 })
            Assert.That(md, Does.Contain(section));
        Assert.That(md, Does.Contain("insufficient"), "tiny human samples are labelled insufficient");
        Assert.That(md, Does.Contain("Easy v Hard"));
        string csv = report.Csv();
        Assert.That(csv, Does.StartWith("metric,group,key,n,wins,losses,draws,win_share,wilson_lo,wilson_hi\n"));
        Assert.That(csv, Does.Contain("weapon_match_in_loadout"));
        Assert.That(csv, Does.Contain("weapon_duel_used"));
        Assert.That(csv, Does.Contain("cohort_matchup"));
        Assert.That(csv, Does.Contain("comeback_trailing"));
    }

    [Test]
    public void FamiliarityPolicy_EquipsOnlyUnlockedWeapons_AndPlaysLegally()
    {
        BotMatchRunner.SeedFor(5, 0, out byte[] seed, out _);
        var inner = BotPlayer.Create(PlayerSide.A, BotDifficulty.Hard, seed).Policy;
        var policy = new FamiliarWeaponsPolicy(inner, 1, new BotRng(3));
        MatchEngine e = Play(30, BotDifficulty.Hard, BotDifficulty.Hard, policyA: policy);
        MatchObservation o = MatchObservation.FromEngine(e, SeatInfo.Bot("Hard", 1), SeatInfo.Bot("Hard"), MatchObservation.BotSimulation);
        Assert.That(o.LoadoutA, Is.Not.Empty);
        Assert.That(o.LoadoutA.All(id => WeaponCatalog.Get(id).IsStarter), Is.True, "a level-1 seat equips starters only");
        Assert.That(o.ReserveA, Is.EqualTo(0), "five starters cannot fill six slots, so no reserve");
        Assert.That(UnlockCohorts.Band(1), Is.EqualTo("L1 (starters)"));
        Assert.That(UnlockCohorts.Band(16), Is.EqualTo("L9-16"));
        Assert.That(UnlockCohorts.Unlocked(16), Has.Count.EqualTo(20));
    }

    [Test]
    public void Statistics_AreCorrect()
    {
        Assert.That(Stats.InverseNormal(0.975), Is.EqualTo(1.959964).Within(1e-5));
        Assert.That(Stats.BonferroniZ(1), Is.EqualTo(1.959964).Within(1e-5));
        Assert.That(Stats.BonferroniZ(20), Is.GreaterThan(3.0).And.LessThan(3.1));
        var (lo, hi) = Stats.Wilson(50, 100);
        Assert.That(lo, Is.EqualTo(0.4038).Within(1e-3));
        Assert.That(hi, Is.EqualTo(0.5962).Within(1e-3));
        Assert.That(double.IsNaN(Stats.Wilson(0, 0).Lo), Is.True);
    }
}
