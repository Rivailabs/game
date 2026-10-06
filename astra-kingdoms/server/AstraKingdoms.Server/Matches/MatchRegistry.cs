using System.Collections.Concurrent;
using System.Security.Cryptography;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Identity;
using AstraKingdoms.Server.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AstraKingdoms.Server.Matches;

/// <summary>Who sits in a seat when a match is created.</summary>
public sealed record SeatSpec(string Uid, string Label, BotDifficulty? Bot = null)
{
    public static SeatSpec Human(string uid, string label) => new(uid, label);
    public static SeatSpec ForBot(BotDifficulty difficulty) => new(null, BotLabel(difficulty), difficulty);

    /// <summary>The label every bot carries (never presented as a person).</summary>
    public static string BotLabel(BotDifficulty d) => "Bot - " + d + " (unranked)";
}

/// <summary>
/// All hosted matches (ticket 50): creation with a fresh secret seed and pinned rules, the
/// one-active-match-per-player rule, lookup for reconnection, and recovery at startup.
/// </summary>
public sealed class MatchRegistry
{
    private readonly ConcurrentDictionary<string, MatchHost> _matches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeByUid = new(StringComparer.Ordinal);
    private readonly object _createGate = new();
    private readonly IMatchRepository _repo;
    private readonly MatchServices _services;
    private readonly ServerOptions _options;
    private readonly ILogger<MatchRegistry> _log;

    public MatchRegistry(IMatchRepository repo, IAuditLog audit, IPlayerChannel channel, TimeProvider time, IOptions<ServerOptions> options,
        ILogger<MatchRegistry> log, ILoggerFactory loggers, Ops.ServiceMetrics metrics, CheckpointWriter checkpoints,
        IMatchSettlementHook settlement = null)
    {
        _repo = repo;
        _options = options.Value;
        _log = log;
        _services = new MatchServices
        {
            Repository = repo,
            Audit = audit,
            Channel = channel,
            Time = time,
            Timings = _options.Timings,
            Log = loggers.CreateLogger<MatchHost>(),
            Metrics = metrics,
            Checkpoints = checkpoints,
            Settlement = settlement,
        };
    }

    public int ActiveCount => _matches.Values.Count(m => !m.IsSettled);

    public MatchHost Get(string matchId) => matchId != null && _matches.TryGetValue(matchId, out MatchHost m) ? m : null;

    /// <summary>The unsettled match a player belongs to, or null.</summary>
    public MatchHost ActiveFor(string uid) =>
        uid != null && _activeByUid.TryGetValue(uid, out string id) && _matches.TryGetValue(id, out MatchHost m) && !m.IsSettled ? m : null;

    public IReadOnlyList<MatchHost> Active => _matches.Values.Where(m => !m.IsSettled).ToList();

    /// <summary>
    /// Creates and starts a match. Returns null when either person is already in an active match
    /// (so racing requests cannot create duplicates).
    /// </summary>
    public MatchHost Create(MatchConfig config, SeatSpec a, SeatSpec b, string origin)
    {
        lock (_createGate)
        {
            if (ActiveFor(a.Uid) != null || ActiveFor(b.Uid) != null) return null;
            byte[] seed = RandomNumberGenerator.GetBytes(32);
            string matchId = Guid.NewGuid().ToString("D");
            MatchEngine engine = MatchEngine.Create(config, seed, matchId);
            DateTimeOffset now = _services.Time.GetUtcNow();
            var stored = new StoredMatch
            {
                MatchId = matchId,
                Status = MatchStatus.Active,
                Origin = origin,
                Ranked = false,
                PlayerA = a.Uid ?? "bot",
                PlayerB = b.Uid ?? "bot",
                KindA = a.Bot.HasValue ? OpponentKinds.Bot : OpponentKinds.Human,
                KindB = b.Bot.HasValue ? OpponentKinds.Bot : OpponentKinds.Human,
                BotDifficulty = (a.Bot ?? b.Bot)?.ToString(),
                RulesVersion = config.RulesVersion,
                RulesHashHex = Hex.Encode(engine.RulesHash),
                SetupDeadlineMs = now.AddMilliseconds(_options.Timings.SetupMs).ToUnixTimeMilliseconds(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            MatchHost host = Build(engine, stored, a, b);
            Register(host);
            host.Start();
            return host;
        }
    }

    private MatchHost Build(MatchEngine engine, StoredMatch stored, SeatSpec a, SeatSpec b)
    {
        Participant Seat(SeatSpec spec, PlayerSide side) => new()
        {
            Side = side,
            Uid = spec.Uid,
            Label = spec.Label,
            // Bots use their own random streams (never derived from the secret match seed) and see only their PlayerView.
            Bot = spec.Bot.HasValue
                ? new BotPlayer(side, new BotPolicy(spec.Bot.Value, new BotRng(RandomUlong())), new BotRng(RandomUlong()))
                : null,
        };
        var host = new MatchHost(engine, stored, Seat(a, PlayerSide.A), Seat(b, PlayerSide.B), _services);
        host.Settled += OnSettled;
        return host;
    }

    private static ulong RandomUlong() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));

    private void Register(MatchHost host)
    {
        _matches[host.MatchId] = host;
        foreach (string uid in host.HumanUids) _activeByUid[uid] = host.MatchId;
    }

    private void OnSettled(MatchHost host)
    {
        foreach (string uid in host.HumanUids)
            _activeByUid.TryRemove(new KeyValuePair<string, string>(uid, host.MatchId));
    }

    // ------------------------------------------------------------------ shutdown and recovery

    /// <summary>Graceful shutdown: suspend (Preserve) or technically void (Void) every active match.</summary>
    public void OnShutdown(ShutdownMatchPolicy policy)
    {
        foreach (MatchHost m in Active)
        {
            if (policy == ShutdownMatchPolicy.Preserve) m.Suspend();
            else m.TechnicalVoid("shutdown");
        }
    }

    /// <summary>
    /// Startup: resume suspended matches (rebuilt by replaying their stored record) when they were
    /// suspended recently; settle everything else that was left open as a technical void. A match
    /// still marked active was interrupted by a crash: the service could not resolve it.
    /// </summary>
    public void RecoverAtStartup()
    {
        DateTimeOffset now = _services.Time.GetUtcNow();
        // Read both lists first: resuming a suspended match marks it active again.
        IReadOnlyList<StoredMatch> suspended = _repo.ListByStatus(MatchStatus.Suspended);
        IReadOnlyList<StoredMatch> interrupted = _repo.ListByStatus(MatchStatus.Active);
        foreach (StoredMatch s in suspended)
        {
            bool fresh = s.SuspendedAt.HasValue && now - s.SuspendedAt.Value <= TimeSpan.FromMinutes(_options.Lifecycle.MaxSuspendMinutes);
            MatchHost host = Rebuild(s);
            if (host == null) continue;
            Register(host);
            if (fresh)
            {
                host.ResumeAfterRestart(s.RemainingMsAtSuspend ?? 0);
                _log.LogInformation("Resumed suspended match {MatchId}", s.MatchId);
            }
            else
            {
                host.TechnicalVoid("suspended_too_long");
            }
        }
        foreach (StoredMatch s in interrupted)
        {
            MatchHost host = Rebuild(s);
            if (host == null) continue;
            Register(host);
            host.TechnicalVoid("service_interrupted");
        }
    }

    private MatchHost Rebuild(StoredMatch s)
    {
        ReplayReport report = Replayer.Verify(s.RecordJson);
        if (!report.Success)
        {
            // The stored record no longer replays (for example after an incompatible rules change):
            // settle it without inventing an outcome.
            _log.LogError("Match {MatchId} cannot be rebuilt: {Failure}", s.MatchId, report.Failure);
            s.Status = MatchStatus.Finished;
            s.Outcome = MatchOutcomes.TechnicalVoid;
            s.OutcomeDetail = "unrecoverable_record";
            s.ResultId = MatchHost.ResultIdFor(s.MatchId);
            s.UpdatedAt = _services.Time.GetUtcNow();
            _repo.Finalize(s, Array.Empty<RewardGrant>());
            return null;
        }
        BotDifficulty difficulty = Enum.TryParse(s.BotDifficulty, out BotDifficulty d) ? d : BotDifficulty.Normal;
        SeatSpec SeatOf(string uid, string kind) => kind == OpponentKinds.Bot ? SeatSpec.ForBot(difficulty) : SeatSpec.Human(uid, LabelFor(s.Origin));
        return Build(report.Engine, s, SeatOf(s.PlayerA, s.KindA), SeatOf(s.PlayerB, s.KindB));
    }

    /// <summary>The label a person shows to the opponent (V1 has no public names).</summary>
    public static string LabelFor(string origin) => origin == MatchOrigins.FriendRoom ? "Friend" : "Opponent";
}
