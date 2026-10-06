using AstraKingdoms.World.Challenges;
using AstraKingdoms.World.Economy;
using AstraKingdoms.World.Season;
using AstraKingdoms.World.Social;

namespace AstraKingdoms.World.Tests;

/// <summary>A one-shard world with a manual UTC clock for tests.</summary>
internal sealed class WorldKit
{
    /// <summary>UTC day 20,000 (2024-10-04) at 00:00; the season starts here.</summary>
    public static readonly long Day0 = WorldTime.StartOfUtcDay(20_000);

    public readonly AccountRegistry Accounts = new();
    public readonly EconomyLedger Economy = new();
    public readonly ShardDirectory Directory = new();
    public readonly WorldShard Shard;
    public readonly AllianceDirectory Alliances;
    public long Now;
    private int _requests;

    public WorldKit(int capacity = 1000, string shardId = "in-1", string region = "ap-south")
    {
        var info = new ShardInfo(shardId, region, capacity);
        Directory.AddShard(info);
        Shard = new WorldShard(info, new WorldSeason(1, Day0), Accounts, Economy);
        Alliances = new AllianceDirectory(Accounts);
        Now = Day0 + 2 * WorldTime.MsPerDay + 12 * WorldTime.MsPerHour; // day 2, noon
    }

    /// <summary>Creates an established (unprotected) account, places it and admits it.</summary>
    public string Add(string id, int rating = 1000, int encounters = 20, long? createdMs = null)
    {
        Assert.That(Accounts.Create(id, createdMs ?? Day0 - 30 * WorldTime.MsPerDay, rating), Is.True);
        for (int i = 0; i < encounters; i++) Accounts.RecordWorldEncounter(id);
        Assert.That(Directory.Assign(id, Shard.Info.ShardId), Is.Null);
        Assert.That(Shard.Admit(id), Is.Null);
        return id;
    }

    public string NextRequest() => "req-" + Interlocked.Increment(ref _requests);

    public ChallengeReceipt Challenge(string attacker, string defender, string? tile = null) =>
        Shard.CreateChallenge(new ChallengeRequest(NextRequest(), attacker, defender, tile), Now);

    /// <summary>Creates and resolves a challenge with the given outcome; asserts it was accepted.</summary>
    public Challenge Win(string attacker, string defender, EncounterOutcome outcome = EncounterOutcome.AttackerWins)
    {
        ChallengeReceipt r = Challenge(attacker, defender);
        Assert.That(r.Accepted, Is.True, r.ToString());
        ChallengeReceipt done = Shard.Resolve(r.Challenge.ChallengeId, "res-" + r.Challenge.ChallengeId, outcome, Now + 60_000);
        Assert.That(done.Accepted, Is.True, done.ToString());
        return done.Challenge;
    }

    public void AdvanceDays(int days) => Now += days * WorldTime.MsPerDay;
}
