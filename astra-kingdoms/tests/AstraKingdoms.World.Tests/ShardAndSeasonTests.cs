using AstraKingdoms.Rules.Match;
using AstraKingdoms.World.Challenges;
using AstraKingdoms.World.Economy;
using AstraKingdoms.World.Season;

namespace AstraKingdoms.World.Tests;

/// <summary>Plan: "World structure" (season, shards, one active shard, migration at a reconciled boundary) and season close.</summary>
public class ShardAndSeasonTests
{
    [Test]
    public void Rules_AreSeparatelyIdentifiedAndEmbedTheDuelRulesHash()
    {
        Assert.That(WorldRules.RulesId, Is.EqualTo("AK-W4-0-proposed"));
        Assert.That(WorldRules.Hash, Is.Not.EqualTo(RulesBundle.Hash));
        Assert.That(WorldRules.HashHex, Has.Length.EqualTo(64));
        Assert.That(WorldRules.SeasonDays, Is.EqualTo(18));
        Assert.That(WorldRules.BorderTilesPerAccount, Is.EqualTo(12));
        Assert.That(WorldRules.HomelandPlots, Is.EqualTo(12));
        var s = new WorldSeason(1, WorldKit.Day0);
        Assert.That(s.EndMs - s.StartMs, Is.EqualTo(18 * WorldTime.MsPerDay));
        Assert.That(s.Next().StartMs, Is.EqualTo(s.EndMs));
        Assert.That(WorldTime.UtcDay(WorldKit.Day0 + WorldTime.MsPerDay - 1), Is.EqualTo(20_000));
        Assert.That(WorldTime.UtcDay(-1), Is.EqualTo(-1));
    }

    [Test]
    public void Shards_DeclareCapacityRegionAndRulesAndAdmitOneActiveShardPerAccount()
    {
        var dir = new ShardDirectory();
        dir.AddShard(new ShardInfo("in-1", "ap-south", 2));
        dir.AddShard(new ShardInfo("in-2", "ap-south", 5));
        dir.AddShard(new ShardInfo("eu-1", "eu-west", 5));
        Assert.That(dir.Assign("x", "in-1"), Is.Null);
        Assert.That(dir.Assign("x", "in-2"), Is.EqualTo("ALREADY_ACTIVE"));
        Assert.That(dir.Assign("y", "in-1"), Is.Null);
        Assert.That(dir.Assign("z", "in-1"), Is.EqualTo("SHARD_FULL"));
        Assert.That(dir.Population("in-1"), Is.EqualTo(2));
        // Friends may request compatible placement, never beyond capacity.
        Assert.That(dir.AssignNearFriend("z", "x", "ap-south"), Is.EqualTo("SHARD_FULL"));
        Assert.That(dir.AssignNearFriend("z", "x", "eu-west"), Is.EqualTo("FRIEND_SHARD_INCOMPATIBLE"));
        Assert.That(dir.Assign("w", "in-2"), Is.Null);
        Assert.That(dir.AssignNearFriend("z", "w", "ap-south"), Is.Null);
        Assert.That(dir.ActiveShardOf("z"), Is.EqualTo("in-2"));
        Assert.That(new ShardInfo("a", "r", 1).CompatibleWith(new ShardInfo("b", "r", 9)), Is.True);
        Assert.That(new ShardInfo("a", "r", 1).CompatibleWith(new ShardInfo("b", "r", 9, "AK-W4-1")), Is.False);
    }

    [Test]
    public void Shards_AdmissionIsCapacitySafeUnderConcurrency()
    {
        var dir = new ShardDirectory();
        dir.AddShard(new ShardInfo("in-1", "ap-south", 100));
        int ok = 0;
        Parallel.For(0, 1000, i => { if (dir.Assign("acct-" + i, "in-1") == null) Interlocked.Increment(ref ok); });
        Assert.That(ok, Is.EqualTo(100));
        Assert.That(dir.Population("in-1"), Is.EqualTo(100));
    }

    [Test]
    public void Migration_HappensOnlyAtAReconciledSeasonBoundary()
    {
        var accounts = new AccountRegistry();
        var economy = new EconomyLedger();
        var dir = new ShardDirectory();
        var a = new ShardInfo("in-1", "ap-south", 10);
        var b = new ShardInfo("in-2", "ap-south", 1);
        dir.AddShard(a);
        dir.AddShard(b);
        var season = new WorldSeason(1, WorldKit.Day0);
        var sa = new WorldShard(a, season, accounts, economy);
        var sb = new WorldShard(b, season, accounts, economy);
        foreach (string id in new[] { "p", "q", "r" })
        {
            accounts.Create(id, WorldKit.Day0 - 30 * WorldTime.MsPerDay);
            dir.Assign(id, "in-1");
            sa.Admit(id);
        }
        Assert.That(dir.RequestMigration("p", "in-2"), Is.Null);
        Assert.That(dir.RequestMigration("q", "in-2"), Is.Null);
        Assert.That(dir.ActiveShardOf("p"), Is.EqualTo("in-1"), "nothing moves mid-season");
        Assert.That(dir.RequestMigration("p", "in-1"), Is.EqualTo("SAME_SHARD"));

        long end = season.EndMs;
        // Only one shard settled: no boundary, no migration.
        SeasonSettlement onlyA = sa.Close(end);
        Assert.That(dir.BoundaryFrom(season.SeasonId, new[] { onlyA }), Is.Null);
        Assert.Throws<ArgumentNullException>(() => dir.ApplyMigrations(null!));

        SeasonBoundaryReport report = SeasonOperator.CloseAndRebuild(dir, new[] { sa, sb }, end);
        Assert.That(report.Migrations.Count(m => m.Applied), Is.EqualTo(1), "capacity 1 admits one migrant");
        Assert.That(report.Migrations.Single(m => !m.Applied).Reason, Is.EqualTo("SHARD_FULL"));
        Assert.That(dir.ActiveShardOf("p"), Is.EqualTo("in-2"));
        Assert.That(dir.ActiveShardOf("q"), Is.EqualTo("in-1"));
        Assert.That(sb.IsMember("p") && !sa.IsMember("p"), Is.True);
        Assert.That(sb.TilesOwnedBy("p"), Is.EqualTo(12));
        Assert.That(dir.PendingMigrations, Is.Zero);
    }

    [Test]
    public void SeasonClose_StopsChallengesSettlesOrCancelsAndSnapshots()
    {
        var w = new WorldKit();
        foreach (string id in new[] { "d1", "d2", "a1", "a2", "a3" }) w.Add(id);
        w.Win("a1", "d1");
        Challenge pendingWin = w.Challenge("a2", "d1").Challenge;
        Challenge pendingNone = w.Challenge("a3", "d2").Challenge;
        SeasonSettlement s = w.Shard.Close(w.Now, c => c.ChallengeId == pendingWin.ChallengeId
            ? new KeyValuePair<string, EncounterOutcome>("final-hash", EncounterOutcome.AttackerWins)
            : null);
        Assert.That(w.Shard.State, Is.EqualTo(ShardSeasonState.Closed));
        Assert.That(s.Settled, Is.EqualTo(1));
        Assert.That(s.Cancelled, Is.EqualTo(1));
        Assert.That(s.Reconciled, Is.True);
        Assert.That(w.Shard.Lookup(pendingNone.ChallengeId)!.CancelReason, Is.EqualTo("SEASON_CLOSE"));
        Assert.That(s.TilesHeld["d1"], Is.EqualTo(10));
        Assert.That(s.TilesHeld["a2"], Is.EqualTo(13));
        Assert.That(s.TilesHeld["d2"], Is.EqualTo(12), "a cancelled reservation moves nothing");
        Assert.That(s.TileOwnership.Values.Count(o => o == "a1"), Is.EqualTo(13));
        Assert.That(w.Challenge("a3", "d1").Code, Is.EqualTo("SEASON_NOT_OPEN"));
        Assert.That(w.Shard.Resolve(pendingNone.ChallengeId, "x", EncounterOutcome.AttackerWins, w.Now).Code, Is.EqualTo("CANCELLED"));
    }

    [Test]
    public void SeasonClose_GrantsRewardsOnce()
    {
        var w = new WorldKit();
        foreach (string id in new[] { "d1", "a1", "idle" }) w.Add(id);
        w.Win("a1", "d1");
        SeasonSettlement first = w.Shard.Close(w.Now);
        long a1Coins = w.Economy.Balance("a1");
        int a1Cosmetics = w.Accounts.Get("a1").Cosmetics.Count;
        Assert.That(first.GrantsIssued, Is.GreaterThan(0));
        Assert.That(w.Accounts.Get("a1").Cosmetics, Does.Contain("border-keeper-banner/world-s1"));
        Assert.That(w.Accounts.Get("d1").Cosmetics.Any(), Is.False, "below the twelve-tile tier");
        Assert.That(w.Economy.Balance("idle"), Is.Zero, "participation coins need an encounter");
        SeasonSettlement second = w.Shard.Close(w.Now + 1);
        Assert.That(second, Is.SameAs(first));
        Assert.That(w.Economy.Balance("a1"), Is.EqualTo(a1Coins));
        Assert.That(w.Accounts.Get("a1").Cosmetics.Count, Is.EqualTo(a1Cosmetics));
    }

    [Test]
    public void SeasonSnapshots_AreDeterministicReplays()
    {
        string Run()
        {
            var w = new WorldKit();
            foreach (string id in new[] { "d1", "d2", "a1", "a2", "a3" }) w.Add(id);
            w.Win("a1", "d1");
            w.Win("a2", "d1", EncounterOutcome.DefenderWins);
            w.Win("a3", "d2");
            w.AdvanceDays(1);
            w.Win("a1", "d2");
            return w.Shard.Close(w.Now).SnapshotHashHex;
        }
        string first = Run(), second = Run();
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Rebuild_RequiresAClosedSeasonInOrder()
    {
        var w = new WorldKit();
        w.Add("x");
        Assert.That(w.Shard.RebuildForNextSeason(w.Shard.Season.Next(), new[] { "x" }), Is.EqualTo("NOT_CLOSED"));
        w.Shard.Close(w.Now);
        Assert.That(w.Shard.RebuildForNextSeason(new WorldSeason(5, 0), new[] { "x" }), Is.EqualTo("SEASON_ORDER"));
        Assert.That(w.Shard.RebuildForNextSeason(w.Shard.Season.Next(), new[] { "x" }), Is.Null);
        Assert.That(w.Shard.Season.Index, Is.EqualTo(2));
        Assert.That(w.Shard.State, Is.EqualTo(ShardSeasonState.Open));
    }
}
