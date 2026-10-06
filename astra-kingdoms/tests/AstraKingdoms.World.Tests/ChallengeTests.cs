using System.Collections.Concurrent;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.World.Challenges;

namespace AstraKingdoms.World.Tests;

/// <summary>Plan: "Challenges and offline defence" — uniqueness, reservation, snapshot, expiry, idempotency and concurrency.</summary>
public class ChallengeTests
{
    [Test]
    public void Creation_ReservesATileAndSnapshotsRulesAndTheDefence()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        DefencePublication mine = DefencePublication.Create(new[] { 6, 7, 8, 9, 10, 11 }, 12, BotDifficulty.Hard, DefenceMode.Automatic, null);
        Assert.That(w.Shard.PublishDefence("def", mine), Is.Null);
        ChallengeReceipt r = w.Challenge("att", "def");
        Assert.That(r.Accepted, Is.True);
        Challenge c = r.Challenge;
        Assert.That(c.TileId, Is.EqualTo("def/border-01"));
        Assert.That(w.Shard.Tile(c.TileId)!.ReservedBy, Is.EqualTo(c.ChallengeId));
        Assert.That(c.ExpiresMs - c.CreatedMs, Is.EqualTo(10 * 60 * 1000));
        Assert.That(c.WorldRulesHashHex, Is.EqualTo(WorldRules.HashHex));
        Assert.That(c.DuelRulesHashHex, Is.EqualTo(RulesBundle.HashHex));
        Assert.That(c.DefenceSnapshot, Is.SameAs(mine));
        string snapshot = c.SnapshotHashHex();
        // Republishing later does not change an existing challenge.
        w.Shard.PublishDefence("def", DefencePublication.Default);
        Assert.That(w.Shard.Lookup(c.ChallengeId)!.DefenceSnapshot, Is.SameAs(mine));
        Assert.That(w.Shard.Lookup(c.ChallengeId)!.SnapshotHashHex(), Is.EqualTo(snapshot));
    }

    [Test]
    public void Creation_IsIdempotentByRequestId()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        var req = new ChallengeRequest("same-id", "att", "def");
        ChallengeReceipt first = w.Shard.CreateChallenge(req, w.Now);
        ChallengeReceipt again = w.Shard.CreateChallenge(req, w.Now + 1000);
        Assert.That(again.Accepted && again.Replayed, Is.True);
        Assert.That(again.Challenge.ChallengeId, Is.EqualTo(first.Challenge.ChallengeId));
        Assert.That(w.Shard.Challenges, Has.Count.EqualTo(1));
        Assert.That(w.Shard.Allowance("def", w.Now).DailyRemaining, Is.EqualTo(1), "one reservation only");
        Assert.That(w.Shard.LookupByRequest("same-id")!.ChallengeId, Is.EqualTo(first.Challenge.ChallengeId));
        Assert.That(w.Shard.LookupByRequest("never-sent"), Is.Null);
        w.Add("other");
        Assert.That(w.Shard.CreateChallenge(new ChallengeRequest("same-id", "other", "def"), w.Now).Code, Is.EqualTo("REQUEST_ID_CONFLICT"));
    }

    [Test]
    public void Expiry_CancelsTheReservationWithoutTransfer()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        Challenge c = w.Challenge("att", "def").Challenge;
        ChallengeReceipt late = w.Shard.Resolve(c.ChallengeId, "r", EncounterOutcome.AttackerWins, c.ExpiresMs);
        Assert.That(late.Code, Is.EqualTo("EXPIRED"));
        Assert.That(w.Shard.Tile(c.TileId)!.OwnerId, Is.EqualTo("def"));
        Assert.That(w.Shard.Tile(c.TileId)!.ReservedBy, Is.Null);
        Assert.That(w.Shard.Allowance("def", w.Now).DailyRemaining, Is.EqualTo(2));
        Assert.That(w.Shard.Resolve(c.ChallengeId, "r", EncounterOutcome.AttackerWins, c.ExpiresMs).Code, Is.EqualTo("CANCELLED"));
        // A retry after timing out cannot create a second challenge from the same request either.
        Assert.That(w.Shard.CreateChallenge(new ChallengeRequest(c.RequestId, "att", "def"), w.Now + 1).Replayed, Is.True);
    }

    [Test]
    public void ExpireDue_ReleasesStaleReservations()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("a1");
        w.Add("a2");
        w.Challenge("a1", "def");
        w.Challenge("a2", "def");
        Assert.That(w.Shard.ExpireDue(w.Now + 10 * 60 * 1000 - 1), Is.Zero);
        Assert.That(w.Shard.ExpireDue(w.Now + 10 * 60 * 1000), Is.EqualTo(2));
        Assert.That(w.Shard.Challenges.All(c => c.State == ChallengeState.Cancelled && c.CancelReason == "EXPIRED"), Is.True);
        Assert.That(w.Shard.Allowance("def", w.Now).DailyRemaining, Is.EqualTo(2));
    }

    [Test]
    public void Resolve_CommitsOwnershipAndRewardsExactlyOnce()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        Challenge c = w.Challenge("att", "def").Challenge;
        Assert.That(w.Shard.Resolve(c.ChallengeId, "match-hash-1", EncounterOutcome.AttackerWins, w.Now).Accepted, Is.True);
        long att = w.Economy.Balance("att"), def = w.Economy.Balance("def");
        Assert.That(att, Is.EqualTo(Economy.EconomyRules.CoinsPerEncounterWin));
        Assert.That(def, Is.EqualTo(Economy.EconomyRules.CoinsPerEncounterParticipation));
        ChallengeReceipt again = w.Shard.Resolve(c.ChallengeId, "match-hash-1", EncounterOutcome.AttackerWins, w.Now);
        Assert.That(again.Accepted && again.Replayed, Is.True);
        Assert.That(w.Shard.Resolve(c.ChallengeId, "match-hash-2", EncounterOutcome.DefenderWins, w.Now).Code, Is.EqualTo("ALREADY_RESOLVED"));
        Assert.That(w.Economy.Balance("att"), Is.EqualTo(att));
        Assert.That(w.Economy.Balance("def"), Is.EqualTo(def));
        Assert.That(w.Shard.TilesOwnedBy("att"), Is.EqualTo(13));
        Assert.That(w.Shard.LossesInSeason("def"), Is.EqualTo(1));
    }

    [Test]
    public void DrawOrCancel_MovesNothing()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("a1");
        w.Add("a2");
        Challenge d = w.Win("a1", "def", EncounterOutcome.Draw);
        Assert.That(d.Outcome, Is.EqualTo(EncounterOutcome.Draw));
        Challenge c = w.Challenge("a2", "def").Challenge;
        Assert.That(w.Shard.Cancel(c.ChallengeId, "TECHNICAL").Accepted, Is.True);
        Assert.That(w.Shard.TilesOwnedBy("def"), Is.EqualTo(12));
        Assert.That(w.Shard.LossesInSeason("def"), Is.Zero);
    }

    [Test]
    public void Tiles_CannotBeReservedTwice()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("a1");
        w.Add("a2");
        Assert.That(w.Challenge("a1", "def", "def/border-05").Accepted, Is.True);
        Assert.That(w.Challenge("a2", "def", "def/border-05").Code, Is.EqualTo("TILE_RESERVED"));
        Assert.That(w.Challenge("a2", "def", "a1/border-01").Code, Is.EqualTo("TILE_NOT_ELIGIBLE"));
        Assert.That(w.Challenge("a2", "def").Challenge.TileId, Is.EqualTo("def/border-01"), "auto-pick skips the reserved tile");
    }

    [Test]
    public void Concurrency_ParallelAttacksOnOneTileReserveItOnce()
    {
        var w = new WorldKit();
        w.Add("def");
        for (int i = 0; i < 64; i++) w.Add("a" + i);
        var accepted = new ConcurrentBag<Challenge>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            ChallengeReceipt r = w.Shard.CreateChallenge(new ChallengeRequest("p" + i, "a" + i, "def", "def/border-07"), w.Now);
            if (r.Accepted) accepted.Add(r.Challenge);
        });
        Assert.That(accepted, Has.Count.EqualTo(1));
    }

    [Test]
    public void Concurrency_ParallelAttacksNeverExceedTheLossBudget()
    {
        var w = new WorldKit();
        w.Add("def");
        for (int i = 0; i < 200; i++) w.Add("a" + i);
        int totalTiles = w.Shard.TotalTiles;
        var accepted = new ConcurrentBag<Challenge>();
        // Wave 1: 200 simultaneous attacks with auto-picked tiles.
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 32 }, i =>
        {
            ChallengeReceipt r = w.Shard.CreateChallenge(new ChallengeRequest("q" + i, "a" + i, "def"), w.Now);
            if (r.Accepted) accepted.Add(r.Challenge);
        });
        Assert.That(accepted, Has.Count.EqualTo(2));
        Assert.That(accepted.Select(c => c.TileId).Distinct().Count(), Is.EqualTo(2));
        // Wave 2: resolve those (twice each, concurrently) while 100 more attacks race in.
        Parallel.For(0, 104, new ParallelOptions { MaxDegreeOfParallelism = 32 }, i =>
        {
            if (i < 4)
            {
                Challenge c = accepted.ElementAt(i % 2);
                w.Shard.Resolve(c.ChallengeId, "hash-" + c.ChallengeId, EncounterOutcome.AttackerWins, w.Now + 1);
            }
            else
            {
                w.Shard.CreateChallenge(new ChallengeRequest("late" + i, "a" + (i + 50), "def"), w.Now + 1);
            }
        });
        Assert.That(w.Shard.LossesOnDay("def", WorldTime.UtcDay(w.Now)), Is.EqualTo(2));
        Assert.That(w.Shard.TilesOwnedBy("def"), Is.EqualTo(10));
        Assert.That(w.Shard.Challenges.Count(c => c.IsOpen), Is.Zero, "no reservation beyond the budget");
        Assert.That(w.Shard.TotalTiles, Is.EqualTo(totalTiles), "tiles are conserved");
        int owned = w.Shard.Members.Sum(m => w.Shard.TilesOwnedBy(m));
        Assert.That(owned, Is.EqualTo(totalTiles), "every tile has exactly one owner");
    }

    [Test]
    public void Concurrency_DuplicateRequestsCreateOneChallenge()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        var ids = new ConcurrentBag<string>();
        Parallel.For(0, 50, i => ids.Add(w.Shard.CreateChallenge(new ChallengeRequest("dup", "att", "def"), w.Now).Challenge.ChallengeId));
        Assert.That(ids.Distinct().Count(), Is.EqualTo(1));
        Assert.That(w.Shard.Challenges, Has.Count.EqualTo(1));
    }

    [Test]
    public void Validation_RejectsBadParticipants()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        Assert.That(w.Challenge("att", "att").Code, Is.EqualTo("SELF_TARGET"));
        Assert.That(w.Challenge("att", "ghost").Code, Is.EqualTo("NOT_IN_SHARD"));
        w.Now = w.Shard.Season.EndMs;
        Assert.That(w.Challenge("att", "def").Code, Is.EqualTo("SEASON_NOT_OPEN"));
    }
}
