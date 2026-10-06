using AstraKingdoms.World.Economy;
using AstraKingdoms.World.Social;

namespace AstraKingdoms.World.Tests;

/// <summary>Plan: alliances on the twenty-member clan structure; capped contributions; no confiscation; bounded coins.</summary>
public class AllianceAndEconomyTests
{
    private static WorldKit WithAlliance(int members)
    {
        var w = new WorldKit();
        w.Add("lead");
        Assert.That(w.Alliances.Create("hawks", "lead", w.Now), Is.Null);
        for (int i = 1; i < members; i++)
        {
            w.Add("m" + i);
            Assert.That(w.Alliances.Invite("lead", "hawks", "m" + i, w.Now), Is.Null);
            Assert.That(w.Alliances.Accept("m" + i, "hawks", w.Now), Is.Null);
        }
        return w;
    }

    [Test]
    public void Roster_HoldsTwentyWithOneLeaderAndAtMostTwoOfficers()
    {
        WorldKit w = WithAlliance(20);
        Assert.That(w.Alliances.MemberCount("hawks"), Is.EqualTo(20));
        w.Add("late");
        Assert.That(w.Alliances.Invite("lead", "hawks", "late", w.Now), Is.Null);
        Assert.That(w.Alliances.Accept("late", "hawks", w.Now), Is.EqualTo("ALLIANCE_FULL"));
        Assert.That(w.Alliances.SetOfficer("lead", "hawks", "m1", true, w.Now), Is.Null);
        Assert.That(w.Alliances.SetOfficer("lead", "hawks", "m2", true, w.Now), Is.Null);
        Assert.That(w.Alliances.SetOfficer("lead", "hawks", "m3", true, w.Now), Is.EqualTo("OFFICER_LIMIT"));
        Assert.That(w.Alliances.SetOfficer("m1", "hawks", "m3", true, w.Now), Is.EqualTo("NOT_PERMITTED"), "only the leader changes officer roles");
        Assert.That(w.Alliances.RoleOf("hawks", "lead"), Is.EqualTo(AllianceRole.Leader));
        Assert.That(w.Alliances.AuditOf("hawks").Count(a => a.Action == "promote"), Is.EqualTo(2));
    }

    [Test]
    public void Permissions_OfficersInviteAndOrganiseMembersDoNot()
    {
        WorldKit w = WithAlliance(4);
        w.Alliances.SetOfficer("lead", "hawks", "m1", true, w.Now);
        w.Add("guest");
        Assert.That(w.Alliances.Invite("m2", "hawks", "guest", w.Now), Is.EqualTo("NOT_PERMITTED"));
        Assert.That(w.Alliances.Invite("m1", "hawks", "guest", w.Now), Is.Null);
        Assert.That(w.Alliances.ScheduleObjective("m2", "hawks", "o", 10, "c", w.Now, w.Now + 1000), Is.EqualTo("NOT_PERMITTED"));
        Assert.That(w.Alliances.ScheduleObjective("m1", "hawks", "o", 10, "c", w.Now, w.Now + 1000), Is.Null);
        Assert.That(w.Alliances.Remove("m1", "hawks", "lead", w.Now), Is.EqualTo("NOT_PERMITTED"));
        Assert.That(w.Alliances.Remove("m2", "hawks", "m3", w.Now), Is.EqualTo("NOT_PERMITTED"));
        Assert.That(w.Alliances.Leave("lead", w.Now), Is.EqualTo("LEADER_MUST_HAND_OVER"));
        Assert.That(w.Alliances.HandOverLeadership("lead", "hawks", "m2", w.Now), Is.Null);
        Assert.That(w.Alliances.RoleOf("hawks", "m2"), Is.EqualTo(AllianceRole.Leader));
    }

    [Test]
    public void Removal_ConfiscatesNothing()
    {
        WorldKit w = WithAlliance(3);
        w.Economy.Credit("k1", "m1", 50, 1, CoinSource.EncounterWin);
        w.Accounts.GrantCosmetic("g", "m1", "hat");
        w.Accounts.Decorate("m1", 3, "statue");
        int tiles = w.Shard.TilesOwnedBy("m1");
        Assert.That(w.Alliances.Remove("lead", "hawks", "m1", w.Now), Is.Null);
        Assert.That(w.Economy.Balance("m1"), Is.EqualTo(50));
        Assert.That(w.Accounts.Get("m1").Cosmetics, Does.Contain("hat"));
        Assert.That(w.Accounts.Get("m1").Homeland[3], Is.EqualTo("statue"));
        Assert.That(w.Shard.TilesOwnedBy("m1"), Is.EqualTo(tiles));
        Assert.That(w.Accounts.Get("m1").AllianceId, Is.Null);
        // There is no API that moves assets between accounts.
        foreach (Type t in new[] { typeof(AllianceDirectory), typeof(EconomyLedger), typeof(Season.AccountRegistry) })
            Assert.That(t.GetMethods().Any(m => m.Name.Contains("Transfer") || m.Name.Contains("Confiscate") || m.Name.Contains("Wallet")), Is.False, t.Name);
    }

    [Test]
    public void Objectives_CapContributionsRequireOptInAndRewardOnce()
    {
        WorldKit w = WithAlliance(6);
        long start = w.Now, end = w.Now + WorldTime.MsPerDay;
        Assert.That(w.Alliances.ScheduleObjective("lead", "hawks", "harvest", 250, "harvest-banner", start, end), Is.Null);
        Assert.That(w.Alliances.Contribute("m1", "hawks", "harvest", 50, "k0", w.Now), Is.Zero, "must opt in first");
        foreach (string m in new[] { "m1", "m2", "m3" }) Assert.That(w.Alliances.OptIn(m, "hawks", "harvest"), Is.Null);
        // One very active member cannot exceed the per-member cap.
        Assert.That(w.Alliances.Contribute("m1", "hawks", "harvest", 500, "k1", w.Now), Is.EqualTo(WorldRules.ObjectiveContributionCapPerMember));
        Assert.That(w.Alliances.Contribute("m1", "hawks", "harvest", 10, "k2", w.Now), Is.Zero);
        Assert.That(w.Alliances.Contribute("m2", "hawks", "harvest", 100, "k3", w.Now), Is.EqualTo(100));
        Assert.That(w.Alliances.Contribute("m2", "hawks", "harvest", 100, "k3", w.Now), Is.Zero, "idempotent key");
        Assert.That(w.Alliances.Objective("hawks", "harvest")!.Completed, Is.False, "200 < 250: a few members cannot finish alone");
        Assert.That(w.Alliances.Contribute("m3", "hawks", "harvest", 60, "k4", w.Now), Is.EqualTo(60));
        Assert.That(w.Alliances.Objective("hawks", "harvest")!.Completed, Is.True);
        foreach (string m in new[] { "m1", "m2", "m3" }) Assert.That(w.Accounts.Get(m).Cosmetics, Does.Contain("harvest-banner"));
        // Non-participants are not penalised and receive nothing.
        Assert.That(w.Accounts.Get("m4").Cosmetics, Is.Empty);
        Assert.That(w.Economy.Balance("m4"), Is.Zero);
        Assert.That(w.Alliances.Contribute("m3", "hawks", "harvest", 10, "k5", w.Now), Is.EqualTo(10));
        Assert.That(w.Accounts.Get("m3").Cosmetics.Count(c => c == "harvest-banner"), Is.EqualTo(1), "granted once");
        Assert.That(w.Alliances.Contribute("m3", "hawks", "harvest", 10, "k6", end), Is.Zero, "outside the scheduled window");
    }

    [Test]
    public void Economy_WorldCoinsAreDailyCappedAndIdempotent()
    {
        var e = new EconomyLedger();
        Assert.That(e.Credit("a", "p", 20, 7, CoinSource.EncounterWin), Is.EqualTo(20));
        Assert.That(e.Credit("a", "p", 20, 7, CoinSource.EncounterWin), Is.Zero, "same key");
        Assert.That(e.Credit("b", "p", 30, 7, CoinSource.EncounterWin), Is.EqualTo(30));
        Assert.That(e.Credit("c", "p", 30, 7, CoinSource.EncounterWin), Is.EqualTo(10), "daily cap of 60");
        Assert.That(e.Credit("d", "p", 30, 7, CoinSource.SeasonParticipation), Is.Zero);
        Assert.That(e.Credit("e", "p", 30, 8, CoinSource.EncounterWin), Is.EqualTo(30), "new UTC day");
        Assert.That(e.Credit("f", "p", 500, 8, CoinSource.IncidentCompensation), Is.EqualTo(500), "recorded compensation is outside the cap");
        Assert.That(e.Balance("p"), Is.EqualTo(590));
        Assert.That(e.Spend("s1", "p", "decoration-garden"), Is.True);
        Assert.That(e.Spend("s1", "p", "decoration-garden"), Is.True, "replayed spend is not charged twice");
        Assert.That(e.Balance("p"), Is.EqualTo(440));
        Assert.That(e.Spend("s2", "poor", "decoration-small"), Is.False, "no overdraft");
        Assert.That(e.Spend("s3", "p", "extra-attack"), Is.False, "no power sinks exist");
        Assert.That(EconomyRules.Sinks.All(s => s.Key.StartsWith("decoration") || s.Key.StartsWith("banner")), Is.True);
    }

    [Test]
    public void Economy_RecognitionIsBoundedAndCosmetic()
    {
        Assert.That(EconomyRules.RecognitionFor(11), Is.Null);
        Assert.That(EconomyRules.RecognitionFor(12), Is.EqualTo("border-keeper-banner"));
        Assert.That(EconomyRules.RecognitionFor(17), Is.EqualTo("border-warden-banner"));
        Assert.That(EconomyRules.RecognitionFor(1000), Is.EqualTo("border-marshal-banner"), "no tier beyond the top");
    }
}
