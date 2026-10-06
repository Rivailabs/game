namespace AstraKingdoms.Server.Storage;

/// <summary>Persisted match lifecycle states.</summary>
public static class MatchStatus
{
    public const string Active = "active";
    /// <summary>Paused by a graceful shutdown with the phase time that was left; resumed at startup.</summary>
    public const string Suspended = "suspended";
    /// <summary>Settled (completed, forfeit, void or technical void). Rewards are final.</summary>
    public const string Finished = "finished";
}

/// <summary>
/// One match as stored. <see cref="RecordJson"/> is the rules assembly's canonical match record
/// (config, seed, every accepted command), so a match can be rebuilt exactly by replaying it. It
/// contains the secret seed and choices: the store is access-restricted (RUNBOOK.md).
/// </summary>
public sealed class StoredMatch
{
    public string MatchId { get; set; }
    public string Status { get; set; } = MatchStatus.Active;
    public string Origin { get; set; }
    public bool Ranked { get; set; }
    public string PlayerA { get; set; }
    public string PlayerB { get; set; }
    public string KindA { get; set; }
    public string KindB { get; set; }
    public string BotDifficulty { get; set; }
    public string RulesVersion { get; set; }
    public string RulesHashHex { get; set; }
    public string RecordJson { get; set; }
    /// <summary>Absolute deadline of the open phase (Unix ms), or null.</summary>
    public long? PhaseDeadlineMs { get; set; }
    /// <summary>Phase time left when suspended (ms), or null.</summary>
    public long? RemainingMsAtSuspend { get; set; }
    public long SetupDeadlineMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public string Outcome { get; set; }
    public string ResultJson { get; set; }
    public string ResultId { get; set; }
    public string OutcomeDetail { get; set; }
    public DateTimeOffset? RetainUntil { get; set; }

    public bool IsParticipant(string uid) => uid != null && (uid == PlayerA || uid == PlayerB);
}

/// <summary>One reward grant. (ResultId, PlayerUid) is unique: a repeated grant request is a no-op.</summary>
public sealed record RewardGrant(string ResultId, string PlayerUid, int Xp, int Coins);

public interface IMatchRepository
{
    /// <summary>Inserts or replaces the match row.</summary>
    void Save(StoredMatch match);
    StoredMatch Get(string matchId);
    IReadOnlyList<StoredMatch> ListByStatus(string status);
    /// <summary>
    /// Atomically stores the settled match and inserts its grants. Returns only grants that were new;
    /// a second finalization of the same result grants nothing.
    /// </summary>
    IReadOnlyList<RewardGrant> Finalize(StoredMatch match, IReadOnlyList<RewardGrant> grants);
    /// <summary>Deletes settled matches whose retention ended. Returns the number removed.</summary>
    int PurgeExpired(DateTimeOffset now);
    /// <summary>Readiness probe.</summary>
    bool Ping();
}

public interface IRewardLedger
{
    IReadOnlyList<RewardGrant> GrantsFor(string playerUid);
    IReadOnlyList<RewardGrant> GrantsForResult(string resultId);
}

public sealed record AuditEntry(long Id, DateTimeOffset At, string MatchId, string ActorRef, string Action, string Detail);

/// <summary>Append-only audit trail. Never holds an unrevealed choice; actors are pseudonymous references.</summary>
public interface IAuditLog
{
    void Append(string matchId, string actorRef, string action, string detail);
    IReadOnlyList<AuditEntry> Read(string matchId, int limit = 1000);
}

public sealed class Grievance
{
    public string Id { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    /// <summary>Pseudonymous reference of an authenticated reporter, or null.</summary>
    public string ReporterRef { get; set; }
    public string Category { get; set; }
    public string MatchId { get; set; }
    public string Description { get; set; }
    public string Contact { get; set; }
    public string Status { get; set; } = "received";
}

public interface IGrievanceStore
{
    void Add(Grievance grievance);
    Grievance Get(string id);
}
