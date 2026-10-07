using AstraKingdoms.World.Armies;
using AstraKingdoms.World.Challenges;
using AstraKingdoms.World.Season;

namespace AstraKingdoms.World.Tests;

/// <summary>Every row of the plan's "Protection armies and newcomer fairness" table.</summary>
public class ProtectionTableTests
{
    // ---------------- Loss ceiling ----------------

    [Test]
    public void LossCeiling_AtMostTwoBorderTilesPerUtcDay()
    {
        var w = new WorldKit();
        w.Add("def");
        foreach (string a in new[] { "a1", "a2", "a3" }) w.Add(a);
        w.Win("a1", "def");
        w.Win("a2", "def");
        Assert.That(w.Challenge("a3", "def").Code, Is.EqualTo("DEFENDER_DAILY_LIMIT"));
        Assert.That(w.Shard.TilesOwnedBy("def"), Is.EqualTo(10));
        Assert.That(w.Shard.LossesOnDay("def", WorldTime.UtcDay(w.Now)), Is.EqualTo(2));
        // The next UTC day the allowance is back.
        w.AdvanceDays(1);
        Assert.That(w.Challenge("a3", "def").Accepted, Is.True);
    }

    [Test]
    public void LossCeiling_OpenReservationsCountBeforeAnyResult()
    {
        var w = new WorldKit();
        w.Add("def");
        foreach (string a in new[] { "a1", "a2", "a3" }) w.Add(a);
        ChallengeReceipt c1 = w.Challenge("a1", "def");
        Assert.That(w.Challenge("a2", "def").Accepted, Is.True);
        Assert.That(w.Shard.Allowance("def", w.Now).DailyRemaining, Is.Zero);
        Assert.That(w.Challenge("a3", "def").Code, Is.EqualTo("DEFENDER_DAILY_LIMIT"));
        // A defence win releases the reserved allowance without a loss.
        Assert.That(w.Shard.Resolve(c1.Challenge.ChallengeId, "r1", EncounterOutcome.DefenderWins, w.Now).Accepted, Is.True);
        Assert.That(w.Shard.Allowance("def", w.Now).DailyRemaining, Is.EqualTo(1));
        Assert.That(w.Challenge("a3", "def").Accepted, Is.True);
    }

    [Test]
    public void LossCeiling_AtMostSixPerSeason()
    {
        var w = new WorldKit();
        w.Add("def");
        for (int i = 0; i < 8; i++) w.Add("a" + i);
        int attacker = 0;
        for (int day = 0; day < 3; day++)
        {
            w.Win("a" + attacker++, "def");
            w.Win("a" + attacker++, "def");
            w.AdvanceDays(1);
        }
        Assert.That(w.Shard.LossesInSeason("def"), Is.EqualTo(6));
        Assert.That(w.Shard.TilesOwnedBy("def"), Is.EqualTo(6));
        Assert.That(w.Challenge("a" + attacker, "def").Code, Is.EqualTo("DEFENDER_SEASON_LIMIT"));
        w.AdvanceDays(3);
        Assert.That(w.Challenge("a" + attacker, "def").Code, Is.EqualTo("DEFENDER_SEASON_LIMIT"), "online and offline losses combined");
    }

    [Test]
    public void LossCeiling_NeverTouchesTheTwelveHomelandPlots()
    {
        var w = new WorldKit();
        w.Add("def");
        for (int p = 0; p < WorldRules.HomelandPlots; p++) w.Accounts.Decorate("def", p, "garden-" + p);
        w.Add("a1");
        w.Add("a2");
        w.Win("a1", "def");
        w.Win("a2", "def");
        AccountSnapshot d = w.Accounts.Get("def");
        Assert.That(d.Homeland, Has.Count.EqualTo(12));
        Assert.That(d.Homeland.Select((x, i) => x == "garden-" + i).All(b => b), Is.True);
        // Homeland plots are not tiles at all.
        foreach (Challenge c in w.Shard.Challenges) Assert.That(c.TileId, Does.Contain("/border-"));
        Assert.That(w.Shard.TotalTiles, Is.EqualTo(3 * WorldRules.BorderTilesPerAccount));
    }

    // ---------------- Repeated targeting ----------------

    [Test]
    public void RepeatedTargeting_NoSameAttackerTwiceWithin24Hours()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        ChallengeReceipt first = w.Challenge("att", "def");
        Assert.That(first.Accepted, Is.True);
        w.Shard.Resolve(first.Challenge.ChallengeId, "r", EncounterOutcome.DefenderWins, w.Now);
        Assert.That(w.Challenge("att", "def").Code, Is.EqualTo("REPEAT_TARGET"), "even after a failed attack");
        w.Now += 23 * WorldTime.MsPerHour;
        Assert.That(w.Challenge("att", "def").Code, Is.EqualTo("REPEAT_TARGET"));
        w.Now += WorldTime.MsPerHour;
        Assert.That(w.Challenge("att", "def").Accepted, Is.True);
    }

    [Test]
    public void RepeatedTargeting_OneSuccessfulAttackPerAlliancePerDefenderPerDay()
    {
        var w = new WorldKit();
        w.Add("def");
        foreach (string m in new[] { "lead", "m1", "m2", "m3", "solo" }) w.Add(m);
        Assert.That(w.Alliances.Create("wolves", "lead", w.Now), Is.Null);
        foreach (string m in new[] { "m1", "m2", "m3" })
        {
            Assert.That(w.Alliances.Invite("lead", "wolves", m, w.Now), Is.Null);
            Assert.That(w.Alliances.Accept(m, "wolves", w.Now), Is.Null);
        }
        ChallengeReceipt open = w.Challenge("m1", "def");
        Assert.That(open.Accepted, Is.True);
        Assert.That(w.Challenge("m2", "def").Code, Is.EqualTo("ALLIANCE_DAILY_LIMIT"), "an open alliance reservation counts");
        // The first attempt fails: the alliance has no success yet, so another member may try.
        w.Shard.Resolve(open.Challenge.ChallengeId, "r1", EncounterOutcome.DefenderWins, w.Now);
        w.Win("m2", "def");
        Assert.That(w.Challenge("lead", "def").Code, Is.EqualTo("ALLIANCE_DAILY_LIMIT"));
        // Leaving the alliance does not bypass the cap for 24 hours.
        Assert.That(w.Alliances.Leave("m3", w.Now), Is.Null);
        Assert.That(w.Accounts.Get("m3").AllianceId, Is.Null);
        Assert.That(w.Challenge("m3", "def").Code, Is.EqualTo("ALLIANCE_DAILY_LIMIT"));
        // A non-member is limited only by the global ceiling.
        w.Win("solo", "def");
        Assert.That(w.Shard.TilesOwnedBy("def"), Is.EqualTo(10));
        w.Add("solo2");
        Assert.That(w.Challenge("solo2", "def").Code, Is.EqualTo("DEFENDER_DAILY_LIMIT"), "global limits still apply");
    }

    // ---------------- Starter protection ----------------

    [Test]
    public void StarterProtection_SevenDaysNoIncomingAndNoAttacking()
    {
        var w = new WorldKit();
        w.Add("vet");
        w.Add("new", encounters: 0, createdMs: w.Now);
        w.Add("new-friendly", encounters: 0);
        Assert.That(w.Challenge("vet", "new").Code, Is.EqualTo("DEFENDER_PROTECTED"));
        Assert.That(w.Challenge("new", "vet").Code, Is.EqualTo("ATTACKER_PROTECTED"));
        w.Now += 7 * WorldTime.MsPerDay - 1;
        Assert.That(w.Challenge("vet", "new").Code, Is.EqualTo("DEFENDER_PROTECTED"));
        w.Now += 1;
        Assert.That(w.Challenge("new", "new-friendly").Accepted, Is.True, "protection ends after seven days");
    }

    [Test]
    public void StarterProtection_EndsEarlyOnlyByChoiceAfterFiveTrainingEncounters()
    {
        var w = new WorldKit();
        w.Add("new", encounters: 0, createdMs: w.Now);
        w.Add("peer", encounters: 0);
        for (int i = 0; i < 4; i++) w.Accounts.RecordTrainingEncounter("new");
        Assert.That(w.Accounts.EndProtectionEarly("new"), Is.EqualTo("NEEDS_TRAINING"));
        Assert.That(w.Challenge("new", "peer").Code, Is.EqualTo("ATTACKER_PROTECTED"));
        w.Accounts.RecordTrainingEncounter("new");
        Assert.That(w.Accounts.Get("new").IsStarterProtected(w.Now), Is.True, "five trainings alone do not end it");
        Assert.That(w.Accounts.EndProtectionEarly("new"), Is.Null);
        Assert.That(w.Challenge("new", "peer").Accepted, Is.True);
    }

    // ---------------- Fair opponents ----------------

    [Test]
    public void FairOpponents_RatingRangeAndExperienceBands()
    {
        var w = new WorldKit();
        w.Add("mid", rating: 1000, encounters: 20);
        w.Add("far", rating: 1201, encounters: 20);
        w.Add("edge", rating: 1200, encounters: 20);
        w.Add("beginner", rating: 1000, encounters: 2);
        w.Add("veteran", rating: 1000, encounters: 80);
        Assert.That(w.Challenge("mid", "far").Code, Is.EqualTo("OPPONENT_OUT_OF_RANGE"));
        Assert.That(w.Challenge("mid", "edge").Accepted, Is.True);
        Assert.That(w.Challenge("veteran", "beginner").Code, Is.EqualTo("OPPONENT_OUT_OF_RANGE"), "same rating, different worlds of experience");
        Assert.That(w.Challenge("beginner", "veteran").Code, Is.EqualTo("OPPONENT_OUT_OF_RANGE"));
        Assert.That(w.Challenge("veteran", "mid").Accepted, Is.True, "adjacent bands within range");
        Assert.That(w.Accounts.Get("beginner").Band, Is.EqualTo(ExperienceBand.Newcomer));
        Assert.That(w.Accounts.Get("veteran").Band, Is.EqualTo(ExperienceBand.Veteran));
    }

    // ---------------- Tactical armies ----------------

    [Test]
    public void TacticalArmies_TenPointsRoleCostsAndSlotLimits()
    {
        Assert.That(ArmyRules.Validate(new[] { new ArmySlot(ArmyRole.Vanguard, 1), new ArmySlot(ArmyRole.Guard, 2) }), Is.Null, "4 + 6 = 10");
        Assert.That(ArmyRules.Validate(new[] { new ArmySlot(ArmyRole.Vanguard, 1), new ArmySlot(ArmyRole.Guard, 2), new ArmySlot(ArmyRole.Banner, 1) }),
            Is.EqualTo("ARMY_OVER_BUDGET"));
        Assert.That(ArmyRules.Validate(new[] { new ArmySlot(ArmyRole.Banner, 3) }), Is.EqualTo("ARMY_SLOT_LIMIT"));
        Assert.That(ArmyRules.Validate(new[] { new ArmySlot(ArmyRole.Scout, 1), new ArmySlot(ArmyRole.Scout, 1) }), Is.EqualTo("ARMY_DUPLICATE_ROLE"));
        Assert.That(ArmyRules.Validate(new[] { new ArmySlot((ArmyRole)99, 1) }), Is.EqualTo("ARMY_UNKNOWN_ROLE"));
        Assert.That(ArmyRules.Validate(new[] { new ArmySlot(ArmyRole.Scout, 0) }), Is.EqualTo("ARMY_COUNT"));
        Assert.Throws<ArgumentException>(() => TacticalArmy.Create(new[] { new ArmySlot(ArmyRole.Vanguard, 2) }));
        Assert.That(TacticalArmy.Create(new[] { new ArmySlot(ArmyRole.Scout, 2), new ArmySlot(ArmyRole.Engineer, 2) }).Cost, Is.EqualTo(8));
        foreach (ArmyRoleSpec s in ArmyRules.Roles) Assert.That(s.Cost, Is.InRange(1, WorldRules.DeploymentPoints));
    }

    [Test]
    public void TacticalArmies_LoansGiveEveryoneTheSameChoices()
    {
        Assert.That(ArmyRules.AvailableTo(0), Is.EqualTo(ArmyRules.AvailableTo(500)));
        Assert.That(ArmyRules.AvailableTo(0), Has.Count.EqualTo(ArmyRules.Roles.Count));
        // The budget is a constant: nothing an account earns raises it.
        Assert.That(typeof(ArmyRules).GetMethods().Any(m => m.Name.Contains("Budget") || m.Name.Contains("Buy")), Is.False);
    }

    [Test]
    public void TacticalArmies_AreSnapshottedWithTheChallenge()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        TacticalArmy army = TacticalArmy.Create(new[] { new ArmySlot(ArmyRole.Scout, 2), new ArmySlot(ArmyRole.Guard, 2) });
        ChallengeReceipt r = w.Shard.CreateChallenge(new ChallengeRequest("rq", "att", "def", null, army), w.Now);
        Assert.That(r.Accepted, Is.True);
        Assert.That(r.Challenge.AttackerArmy.Cost, Is.EqualTo(10));
    }

    // ---------------- Reset/recovery ----------------

    [Test]
    public void SeasonReset_RestoresBordersAndKeepsHomelandPurchasesAndCosmetics()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("a1");
        w.Add("a2");
        w.Accounts.Decorate("def", 0, "fountain");
        w.Accounts.AddPurchase("def", "pass-s1");
        Assert.That(w.Accounts.GrantCosmetic("g-1", "def", "skin-blue"), Is.True);
        w.Win("a1", "def");
        w.Win("a2", "def");
        long coinsBefore = w.Economy.Balance("def");
        Assert.That(w.Shard.TilesOwnedBy("def"), Is.EqualTo(10));
        Assert.That(w.Shard.TilesOwnedBy("a1"), Is.EqualTo(13));

        w.Now = w.Shard.Season.EndMs;
        SeasonBoundaryReport report = SeasonOperator.CloseAndRebuild(w.Directory, new[] { w.Shard }, w.Now);
        Assert.That(report.NextSeason.Index, Is.EqualTo(2));
        foreach (string id in new[] { "def", "a1", "a2" }) Assert.That(w.Shard.TilesOwnedBy(id), Is.EqualTo(12), id);
        AccountSnapshot d = w.Accounts.Get("def");
        Assert.That(d.Homeland[0], Is.EqualTo("fountain"));
        Assert.That(d.Purchases, Does.Contain("pass-s1"));
        Assert.That(d.Cosmetics, Does.Contain("skin-blue"));
        Assert.That(w.Economy.Balance("def"), Is.GreaterThanOrEqualTo(coinsBefore), "coins are kept (plus season grants)");
        Assert.That(d.ReEntryOffered, Is.True, "a safe re-entry route is offered on return");
        Assert.That(w.Accounts.Get("a1").ReEntryOffered, Is.False);
        Assert.That(w.Shard.LossesInSeason("def"), Is.Zero);
        w.Now += WorldTime.MsPerHour;
        Assert.That(w.Challenge("a1", "def").Accepted, Is.True, "fresh season, fresh allowance");
    }
}
