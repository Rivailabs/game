using System.Security.Cryptography;
using System.Text;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Identity;
using AstraKingdoms.Server.Storage;
using Microsoft.Extensions.Logging;

namespace AstraKingdoms.Server.Matches;

/// <summary>One seat of a hosted match.</summary>
public sealed class Participant
{
    public PlayerSide Side { get; init; }
    /// <summary>Account ID of a person; null for a bot.</summary>
    public string Uid { get; init; }
    public BotPlayer Bot { get; init; }
    /// <summary>What the opponent sees: always says "Bot" for a bot.</summary>
    public string Label { get; init; }
    public bool IsBot => Bot != null;
    public string Ref => IsBot ? "bot" : PlayerRef.Of(Uid);

    // Delivery bookkeeping (guarded by the host's lock).
    internal long SentSeq;
    internal ulong SentMapRevision = ulong.MaxValue;
    internal long AckSeq;
}

/// <summary>Delivers messages to a player's live connection, if any.</summary>
public interface IPlayerChannel
{
    /// <summary>Queues a message on the player's authenticated connection; false when none is live.</summary>
    bool Send(string uid, JsonNode message);
    bool IsConnected(string uid);
}

/// <summary>Collaborators shared by every hosted match.</summary>
public sealed class MatchServices
{
    public IMatchRepository Repository { get; init; }
    public IAuditLog Audit { get; init; }
    public IPlayerChannel Channel { get; init; }
    public TimeProvider Time { get; init; }
    public TimingOptions Timings { get; init; }
    public ILogger Log { get; init; }
    public Ops.ServiceMetrics Metrics { get; init; }
    public CheckpointWriter Checkpoints { get; init; }
}

/// <summary>
/// Hosts one authoritative match (tickets 50, 51, 54): the shared rules engine with an immutable
/// config and rules hash, the online phase clock, private per-player delivery, durable writes at
/// creation, suspension and settlement plus a background checkpoint each round, idempotent
/// finalization and the audit trail.
/// <para>
/// <b>Authority.</b> Clients send typed commands; the authenticated connection decides the seat.
/// Every outcome comes from <see cref="MatchEngine"/>. The server alone issues
/// <see cref="AdvancePhaseCommand"/> when a deadline passes (2 s announcement, 12 s concurrent
/// choice, 2.5 s replay, 12 s card and cut). Receive time is authoritative: a command processed at
/// or after the open deadline is evaluated only after that deadline has been applied, so a late
/// lock can never beat the Pass. A disconnect changes nothing: accepted choices stay, the deadline
/// keeps running.
/// </para>
/// <para>
/// <b>Privacy.</b> Each person receives only their own <see cref="PlayerView"/> and public events.
/// The audit trail records that a lock happened, never its contents; the full record (seed and
/// choices) is stored for reproduction and disclosed to participants only after the match ends.
/// </para>
/// All public members take the host lock, so commands, timers and reconnects are serialized.
/// </summary>
public sealed class MatchHost
{
    private readonly object _gate = new();
    private readonly MatchServices _s;
    private readonly Participant[] _seats;
    private readonly StoredMatch _stored;
    private ITimer _clock;
    private ulong _clockRevision;
    private DateTimeOffset? _deadline;
    private readonly ITimer[] _botTimers = new ITimer[2];
    private readonly ulong[] _botRevision = new ulong[2];
    private bool _started;
    private bool _settling;
    private int _persistedRound = -1;

    public MatchHost(MatchEngine engine, StoredMatch stored, Participant a, Participant b, MatchServices services)
    {
        Engine = engine;
        _stored = stored;
        _seats = new[] { a, b };
        _s = services;
        Rules = new MatchRules
        {
            Config = engine.Config,
            RulesHashHex = Hex.Encode(engine.RulesHash),
            Timings = services.Timings.ToWire(),
            Ranked = false,
        };
    }

    public MatchEngine Engine { get; }
    public string MatchId => Engine.MatchId;
    public string Origin => _stored.Origin;
    public MatchRules Rules { get; }
    /// <summary>Settled: finished, voided or technically voided. No further commands are accepted.</summary>
    public bool IsSettled { get; private set; }
    /// <summary>Suspended by a graceful shutdown (Preserve policy).</summary>
    public bool IsSuspended { get; private set; }
    public string Outcome => _stored.Outcome;

    /// <summary>Deadline of the open phase (null when settled or suspended).</summary>
    public DateTimeOffset? Deadline
    {
        get { lock (_gate) return IsSettled || IsSuspended ? null : _deadline; }
    }
    public string ResultId => _stored.ResultId;

    /// <summary>Raised once when the match settles (the registry frees both players).</summary>
    public event Action<MatchHost> Settled;

    public Participant Seat(PlayerSide side) => _seats[(int)side];

    public Participant ParticipantFor(string uid)
    {
        if (uid == null) return null;
        foreach (Participant p in _seats)
            if (!p.IsBot && p.Uid == uid) return p;
        return null;
    }

    public IEnumerable<string> HumanUids => _seats.Where(p => !p.IsBot).Select(p => p.Uid);

    /// <summary>Deterministic result ID for a match: rewards key on it, so a retried grant is a no-op.</summary>
    public static string ResultIdFor(string matchId)
    {
        byte[] h = SHA256.HashData(Encoding.ASCII.GetBytes("AK-RESULT/1|" + matchId));
        h[6] = (byte)((h[6] & 0x0F) | 0x50); // name-based UUID layout (version 5 style)
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Persists, announces and starts the clock of a new match.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _persistedRound = Engine.RoundIndex;
            Persist(); // the row exists (participants, rules) before anyone can act
            _s.Audit.Append(MatchId, "server", "match_created",
                "origin=" + Origin + " a=" + _seats[0].Ref + " b=" + _seats[1].Ref + " rules=" + Rules.RulesHashHex + " config=" + Engine.Config);
            _s.Log.LogInformation("Match {MatchId} created ({Origin}) {PlayerA} vs {PlayerB}", MatchId, Origin, _seats[0].Ref, _seats[1].Ref);
            foreach (Participant p in _seats)
                if (!p.IsBot) _s.Channel.Send(p.Uid, StartMessage(p));
            AfterChange();
        }
    }

    /// <summary>Resumes a match rebuilt from storage with the phase time it had left when suspended.</summary>
    public void ResumeAfterRestart(long remainingMs)
    {
        lock (_gate)
        {
            _started = true;
            _stored.Status = MatchStatus.Active;
            _stored.SuspendedAt = null;
            _stored.RemainingMsAtSuspend = null;
            _clockRevision = Engine.StateRevision;
            SetDeadline(_s.Time.GetUtcNow().AddMilliseconds(Math.Max(0, remainingMs)));
            _s.Audit.Append(MatchId, "server", "match_resumed", "remaining_ms=" + remainingMs);
            Persist();
            ScheduleBots();
        }
    }

    /// <summary>Graceful shutdown with the Preserve policy: freeze the clock and persist the time left.</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            if (IsSettled || IsSuspended) return;
            IsSuspended = true;
            DateTimeOffset now = _s.Time.GetUtcNow();
            long remaining = _deadline.HasValue ? Math.Max(0, (long)(_deadline.Value - now).TotalMilliseconds) : 0;
            StopTimers();
            _stored.Status = MatchStatus.Suspended;
            _stored.SuspendedAt = now;
            _stored.RemainingMsAtSuspend = remaining;
            Persist();
            _s.Audit.Append(MatchId, "server", "match_suspended", "remaining_ms=" + remaining);
        }
    }

    /// <summary>
    /// Settles the match as a technical void: the service could not resolve it (setup expiry,
    /// shutdown with the Void policy, unrecoverable restart). No reward and no loss for anyone.
    /// </summary>
    public void TechnicalVoid(string detail)
    {
        lock (_gate)
        {
            if (_settling) return;
            _stored.Outcome = MatchOutcomes.TechnicalVoid;
            _stored.OutcomeDetail = detail;
            SettleLocked(null, Array.Empty<RewardGrant>());
        }
    }

    // ------------------------------------------------------------------ player input

    /// <summary>A player's command, already decoded. Sends the receipt (or error) back to that player.</summary>
    public void HandleCommand(string uid, string rid, MatchCommand command)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            HandleCommandTimed(uid, rid, command);
        }
        finally
        {
            _s.Metrics?.RecordCommand(started);
        }
    }

    private void HandleCommandTimed(string uid, string rid, MatchCommand command)
    {
        lock (_gate)
        {
            Participant p = ParticipantFor(uid);
            if (p == null)
            {
                Error(uid, rid, ErrorCodes.NotParticipant, "Not a participant of this match.");
                return;
            }
            if (IsSuspended)
            {
                Error(uid, rid, ErrorCodes.ServerDraining, "The service is restarting; reconnect shortly.");
                return;
            }
            if (IsSettled)
            {
                Error(uid, rid, ErrorCodes.MatchClosed, "The match has ended.");
                return;
            }

            ExpireDueLocked(); // receive time is authoritative: an overdue deadline applies first
            if (IsSettled)
            {
                Error(uid, rid, ErrorCodes.MatchClosed, "The match has ended.");
                return;
            }

            int before = Engine.CommandLog.Count;
            CommandReceipt receipt = Engine.Submit(p.Side, command);
            bool applied = receipt.Accepted && Engine.CommandLog.Count > before;
            if (applied)
                _s.Audit.Append(MatchId, p.Ref, "command_accepted",
                    "kind=" + receipt.Kind + " round=" + receipt.RoundIndex + " volley=" + receipt.VolleyIndex + " input_rev=" + receipt.InputRevision +
                    " request=" + receipt.RequestId + (command is SubmitCutCommand ? " cells=" + receipt.CellsTransferred : string.Empty));
            else if (receipt.Accepted)
                _s.Audit.Append(MatchId, p.Ref, "command_duplicate", "kind=" + receipt.Kind + " request=" + receipt.RequestId);
            else
                _s.Audit.Append(MatchId, p.Ref, "command_rejected", "kind=" + command.Kind + " code=" + receipt.RejectCode);

            _s.Log.LogInformation("Match {MatchId} {Player} {Kind} {Outcome} {Code}", MatchId, p.Ref, command.Kind,
                applied ? "accepted" : receipt.Accepted ? "duplicate" : "rejected", receipt.RejectCode);
            _s.Channel.Send(uid, ReceiptMessage.From(rid, receipt).ToJson());
            if (applied) AfterChange();
        }
    }

    /// <summary>Reconnection: re-announces the match and sends a snapshot plus the events after <paramref name="ackSeq"/>.</summary>
    public void Resume(string uid, string rid, long ackSeq)
    {
        lock (_gate)
        {
            Participant p = ParticipantFor(uid);
            if (p == null)
            {
                Error(uid, rid, ErrorCodes.NotParticipant, "Not a participant of this match.");
                return;
            }
            _s.Channel.Send(uid, StartMessage(p));
            Publish(p, snapshot: true, Math.Max(0, ackSeq));
            if (IsSettled) _s.Channel.Send(uid, EndMessage(p));
            _s.Audit.Append(MatchId, p.Ref, "resumed", "ack=" + ackSeq);
        }
    }

    public void Ack(string uid, long seq)
    {
        lock (_gate)
        {
            Participant p = ParticipantFor(uid);
            if (p != null && seq > p.AckSeq && seq <= p.SentSeq) p.AckSeq = seq;
        }
    }

    /// <summary>The private view of a participant (HTTP read access; ticket 49 authorization is checked by the caller).</summary>
    public JsonNode ViewJson(string uid)
    {
        lock (_gate)
        {
            Participant p = ParticipantFor(uid);
            return p == null ? null : PlayerViewCodec.ToJson(Engine.GetView(p.Side), includeOwnership: false);
        }
    }

    /// <summary>A participant's connection state changed: the opponent's status line is refreshed (nothing else changes).</summary>
    public void PresenceChanged()
    {
        lock (_gate)
        {
            if (IsSettled || !_started) return;
            foreach (Participant p in _seats)
                if (!p.IsBot) Publish(p, snapshot: false, 0);
        }
    }

    // ------------------------------------------------------------------ clock

    private long PhaseMs(MatchPhase phase) => phase switch
    {
        MatchPhase.TerrainAnnounce => _s.Timings.AnnounceMs,
        MatchPhase.Selection => _s.Timings.ChoiceMs,
        MatchPhase.Resolution => _s.Timings.ReplayMs,
        MatchPhase.CardAndCut => _s.Timings.CutMs,
        _ => 0,
    };

    /// <summary>Starts the clock for a newly published phase. A retry never restarts it: only a new revision does.</summary>
    private void ArmClock()
    {
        if (Engine.IsOver || Engine.StateRevision == _clockRevision) return;
        _clockRevision = Engine.StateRevision;
        DateTimeOffset now = _s.Time.GetUtcNow();
        SetDeadline(Engine.Phase == MatchPhase.Setup
            ? DateTimeOffset.FromUnixTimeMilliseconds(_stored.SetupDeadlineMs)
            : now.AddMilliseconds(PhaseMs(Engine.Phase)));
    }

    private void SetDeadline(DateTimeOffset deadline)
    {
        _clock?.Dispose();
        _deadline = deadline;
        _stored.PhaseDeadlineMs = deadline.ToUnixTimeMilliseconds();
        ulong revision = _clockRevision;
        TimeSpan due = deadline - _s.Time.GetUtcNow();
        if (due < TimeSpan.FromMilliseconds(1)) due = TimeSpan.FromMilliseconds(1); // never fire inside the caller's lock
        _clock = _s.Time.CreateTimer(_ => OnClock(revision), null, due, Timeout.InfiniteTimeSpan);
    }

    private void OnClock(ulong revision)
    {
        lock (_gate)
        {
            if (IsSettled || IsSuspended || Engine.StateRevision != revision || !_deadline.HasValue) return;
            // System timers run on a coarse tick and can fire a few milliseconds before the deadline
            // as measured by the wall clock: re-arm for the remainder instead of dropping the deadline.
            if (_s.Time.GetUtcNow() < _deadline.Value)
            {
                SetDeadline(_deadline.Value);
                return;
            }
            ExpireDueLocked();
        }
    }

    /// <summary>Applies every deadline that has passed (the server's AdvancePhase, or a setup technical void).</summary>
    private void ExpireDueLocked()
    {
        for (int guard = 0; guard < 8 && !IsSettled && _deadline.HasValue && _s.Time.GetUtcNow() >= _deadline.Value; guard++)
        {
            if (Engine.Phase == MatchPhase.Setup)
            {
                _stored.Outcome = MatchOutcomes.TechnicalVoid;
                _stored.OutcomeDetail = "setup_timeout";
                SettleLocked(null, Array.Empty<RewardGrant>());
                return;
            }
            MatchPhase phase = Engine.Phase;
            CommandReceipt r = Engine.Advance(Engine.CreateAdvance(Guid.NewGuid().ToString("D")));
            if (!r.Accepted)
            {
                _s.Log.LogError("Match {MatchId} server advance rejected: {Code}", MatchId, r.RejectCode);
                _stored.Outcome = MatchOutcomes.TechnicalVoid;
                _stored.OutcomeDetail = "advance_rejected";
                SettleLocked(null, Array.Empty<RewardGrant>());
                return;
            }
            _s.Audit.Append(MatchId, "server", "deadline", "phase=" + phase + " round=" + r.RoundIndex + " volley=" + r.VolleyIndex + " input_rev=" + r.InputRevision);
            AfterChange();
        }
    }

    // ------------------------------------------------------------------ bots

    /// <summary>Lets each bot act on its own view, after the configured thinking delay.</summary>
    private void ScheduleBots()
    {
        if (IsSettled || IsSuspended || Engine.IsOver) return;
        foreach (Participant p in _seats)
        {
            if (!p.IsBot) continue;
            int i = (int)p.Side;
            if (_botRevision[i] == Engine.StateRevision) continue; // already scheduled (or acted) for this snapshot
            if (!BotHasWork(Engine.GetView(p.Side))) continue;
            _botRevision[i] = Engine.StateRevision;
            _botTimers[i]?.Dispose();
            _botTimers[i] = null;
            ulong revision = Engine.StateRevision;
            if (_s.Timings.BotThinkMs <= 0)
            {
                OnBot(p, revision); // acts now (re-entrant: the host lock is already held)
                continue;
            }
            _botTimers[i] = _s.Time.CreateTimer(_ => OnBot(p, revision), null, TimeSpan.FromMilliseconds(_s.Timings.BotThinkMs),
                Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>True when a seat still has something to submit in the open phase (the bot itself may still decline a cut).</summary>
    private static bool BotHasWork(PlayerView v) => v.Phase switch
    {
        MatchPhase.Setup => v.OwnLoadout == null,
        MatchPhase.Selection => v.OwnLock == null,
        MatchPhase.CardAndCut => v.IsCutTurn,
        _ => false,
    };

    private void OnBot(Participant bot, ulong revision)
    {
        lock (_gate)
        {
            if (IsSettled || IsSuspended || Engine.StateRevision != revision) return;
            ExpireDueLocked();
            if (IsSettled || Engine.StateRevision != revision) return;
            MatchCommand cmd = bot.Bot.Decide(Engine.GetView(bot.Side));
            if (cmd == null) return;
            CommandReceipt r = Engine.Submit(bot.Side, cmd);
            if (!r.Accepted)
            {
                _s.Log.LogWarning("Match {MatchId} bot command rejected: {Code}", MatchId, r.RejectCode);
                return; // the deadline will Pass for it
            }
            _s.Audit.Append(MatchId, "bot", "command_accepted", "kind=" + r.Kind + " round=" + r.RoundIndex + " volley=" + r.VolleyIndex + " input_rev=" + r.InputRevision);
            AfterChange();
        }
    }

    // ------------------------------------------------------------------ state changes

    private void AfterChange()
    {
        // Checkpoint once per round (and at creation). Settlement and graceful suspension always write
        // the full record; a crash between checkpoints settles the match as a technical void anyway,
        // so writing every command would only serialize all matches on the single SQLite writer.
        if (Engine.RoundIndex != _persistedRound && !Engine.IsOver)
        {
            _persistedRound = Engine.RoundIndex;
            Checkpoint();
        }
        if (Engine.IsOver)
        {
            Finish();
            return;
        }
        ArmClock();
        foreach (Participant p in _seats)
            if (!p.IsBot) Publish(p, snapshot: false, 0);
        ScheduleBots();
    }

    /// <summary>Durable write now (creation, resume, suspension).</summary>
    private void Persist()
    {
        _stored.RecordJson = MatchRecord.FromEngine(Engine).ToJson();
        _stored.UpdatedAt = _s.Time.GetUtcNow();
        if (_s.Checkpoints != null) _s.Checkpoints.WriteNow(_stored, _s.Repository.Save);
        else _s.Repository.Save(_stored);
    }

    /// <summary>Background checkpoint of a copy of the current state (never blocks command handling on storage).</summary>
    private void Checkpoint()
    {
        if (_s.Checkpoints == null)
        {
            Persist();
            return;
        }
        _stored.RecordJson = MatchRecord.FromEngine(Engine).ToJson();
        _stored.UpdatedAt = _s.Time.GetUtcNow();
        _s.Checkpoints.Enqueue(_stored.Copy());
    }

    private void Finish()
    {
        MatchResult result = Engine.Result;
        _stored.Outcome = result.Reason switch
        {
            TerminalReason.Territory90 or TerminalReason.RoundsComplete => MatchOutcomes.Completed,
            TerminalReason.Forfeit => MatchOutcomes.Forfeit,
            _ => MatchOutcomes.Void,
        };
        _stored.OutcomeDetail = result.Reason.ToString();
        SettleLocked(result, RewardPolicy.Grants(ResultIdFor(MatchId), _stored.Outcome, result, _seats));
    }

    private void SettleLocked(MatchResult result, IReadOnlyList<RewardGrant> grants)
    {
        if (_settling) return;
        _settling = true;
        StopTimers();
        _deadline = null;
        _stored.Status = MatchStatus.Finished;
        _stored.ResultId = ResultIdFor(MatchId);
        _stored.ResultJson = PlayerViewCodec.ResultNode(result).ToCanonicalString();
        _stored.PhaseDeadlineMs = null;
        _stored.RecordJson = MatchRecord.FromEngine(Engine).ToJson();
        _stored.UpdatedAt = _s.Time.GetUtcNow();
        IReadOnlyList<RewardGrant> granted = Array.Empty<RewardGrant>();
        if (_s.Checkpoints != null) _s.Checkpoints.WriteNow(_stored, m => granted = _s.Repository.Finalize(m, grants));
        else granted = _s.Repository.Finalize(_stored, grants);
        _s.Audit.Append(MatchId, "server", "match_settled",
            "outcome=" + _stored.Outcome + " detail=" + _stored.OutcomeDetail + " result=" + (result?.ToString() ?? "-") + " result_id=" + _stored.ResultId +
            " grants_new=" + granted.Count);
        if (result != null)
            _s.Audit.Append(MatchId, "server", "choices_revealed", "record_sha256=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_stored.RecordJson))).ToLowerInvariant());
        _s.Log.LogInformation("Match {MatchId} settled: {Outcome} ({Detail})", MatchId, _stored.Outcome, _stored.OutcomeDetail);
        IsSettled = true; // only now: everything above is stored before anyone observes the settlement
        foreach (Participant p in _seats)
        {
            if (p.IsBot) continue;
            if (result != null) Publish(p, snapshot: false, 0); // final view: seed and loadouts disclosed
            _s.Channel.Send(p.Uid, EndMessage(p));
        }
        Settled?.Invoke(this);
    }

    private void StopTimers()
    {
        _clock?.Dispose();
        _clock = null;
        for (int i = 0; i < 2; i++)
        {
            _botTimers[i]?.Dispose();
            _botTimers[i] = null;
        }
    }

    // ------------------------------------------------------------------ messages

    private void Publish(Participant p, bool snapshot, long ackSeq)
    {
        PlayerView view = Engine.GetView(p.Side);
        bool includeMap = snapshot || p.SentMapRevision != view.MapRevision;
        long after = snapshot ? ackSeq : p.SentSeq;
        IReadOnlyList<MatchEvent> events = Engine.EventsAfter(after);
        var msg = new MatchUpdateMessage
        {
            MatchId = MatchId,
            Snapshot = snapshot,
            View = PlayerViewCodec.ToJson(view, includeMap),
            LastSeq = events.Count > 0 ? events[events.Count - 1].Sequence : after,
            DeadlineRemainingMs = _deadline.HasValue && !IsSettled
                ? Math.Max(0, (long)(_deadline.Value - _s.Time.GetUtcNow()).TotalMilliseconds)
                : -1,
            OpponentConnected = OpponentConnected(p),
        };
        foreach (MatchEvent e in events) msg.Events.Add(WireEvent.From(e));
        if (_s.Channel.Send(p.Uid, msg.ToJson()))
        {
            if (msg.LastSeq > p.SentSeq || snapshot) p.SentSeq = msg.LastSeq;
            p.SentMapRevision = view.MapRevision;
        }
    }

    private bool OpponentConnected(Participant p)
    {
        Participant o = _seats[1 - (int)p.Side];
        return o.IsBot || _s.Channel.IsConnected(o.Uid);
    }

    private JsonNode StartMessage(Participant p)
    {
        Participant o = _seats[1 - (int)p.Side];
        return new MatchStartMessage
        {
            MatchId = MatchId,
            Side = p.Side,
            OpponentKind = o.IsBot ? OpponentKinds.Bot : OpponentKinds.Human,
            OpponentLabel = o.Label,
            Origin = Origin,
            Rules = Rules,
        }.ToJson();
    }

    private JsonNode EndMessage(Participant p)
    {
        RewardGrant g = RewardPolicy.Grants(_stored.ResultId, _stored.Outcome, Engine.Result, _seats).FirstOrDefault(x => x.PlayerUid == p.Uid);
        return new MatchEndMessage
        {
            MatchId = MatchId,
            Outcome = _stored.Outcome,
            Result = _stored.Outcome == MatchOutcomes.TechnicalVoid ? null : Engine.Result,
            ResultId = _stored.ResultId,
            Detail = _stored.OutcomeDetail,
            RewardXp = g?.Xp ?? 0,
            RewardCoins = g?.Coins ?? 0,
        }.ToJson();
    }

    private void Error(string uid, string rid, string code, string message) =>
        _s.Channel.Send(uid, new ErrorMessage { Rid = rid, Code = code, Message = message }.ToJson());
}

/// <summary>
/// Proposed V1 reward values (plan: "Modes and fair progression"): a completed human match gives
/// 100 XP (+25 win, +10 draw) and 10 coins (+5 win); a completed unranked bot match counts like
/// practice (50 XP). Forfeits, voids and technical voids grant nothing. Each grant is keyed by the
/// match's result ID, so repeating a finalization or reward request cannot duplicate it.
/// </summary>
public static class RewardPolicy
{
    public static IReadOnlyList<RewardGrant> Grants(string resultId, string outcome, MatchResult result, IReadOnlyList<Participant> seats)
    {
        var list = new List<RewardGrant>();
        if (outcome != MatchOutcomes.Completed || result == null) return list;
        bool vsBot = seats.Any(s => s.IsBot);
        foreach (Participant p in seats)
        {
            if (p.IsBot) continue;
            int xp, coins;
            if (vsBot)
            {
                xp = 50;
                coins = 0;
            }
            else
            {
                bool won = result.Winner == p.Side;
                xp = 100 + (won ? 25 : 0) + (result.Winner == null ? 10 : 0);
                coins = 10 + (won ? 5 : 0);
            }
            list.Add(new RewardGrant(resultId, p.Uid, xp, coins));
        }
        return list;
    }
}
