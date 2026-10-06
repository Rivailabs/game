using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.V2.Common;
using AstraKingdoms.V2.Kingdom;
using AstraKingdoms.V2.Seasons;

namespace AstraKingdoms.V2.Ranked
{
    /// <summary>Which queue produced a match. Only <see cref="Ranked"/> results ever touch a rating.</summary>
    public enum QueueKind : byte
    {
        Ranked = 0,
        Casual = 1,
        Practice = 2,
        FriendRoom = 3,
    }

    /// <summary>
    /// The authoritative result of a ranked match as the match service reports it. It deliberately
    /// carries only identities and the outcome: no damage, HP, cells, coins, purchases or ad data,
    /// so none of those can influence a rating.
    /// </summary>
    public sealed class RankedMatchResult
    {
        public string MatchId { get; }
        public string SeasonId { get; }
        public QueueKind Queue { get; }
        public string SnapshotId { get; }
        public string PlayerA { get; }
        public string PlayerB { get; }
        /// <summary>The winning seat, or null for a draw.</summary>
        public PlayerSide? Winner { get; }
        public MatchEnding Ending { get; }
        /// <summary>False when server validation rejected the result (tampering, rules-hash mismatch, replay failure).</summary>
        public bool IsValid { get; }
        public DateTimeOffset CompletedAt { get; }

        public RankedMatchResult(string matchId, string seasonId, QueueKind queue, string snapshotId, string playerA, string playerB,
            PlayerSide? winner, MatchEnding ending, DateTimeOffset completedAt, bool isValid = true)
        {
            MatchId = matchId ?? throw new ArgumentNullException(nameof(matchId));
            SeasonId = seasonId ?? throw new ArgumentNullException(nameof(seasonId));
            Queue = queue;
            SnapshotId = snapshotId;
            PlayerA = playerA ?? throw new ArgumentNullException(nameof(playerA));
            PlayerB = playerB ?? throw new ArgumentNullException(nameof(playerB));
            Winner = winner;
            Ending = ending;
            CompletedAt = completedAt;
            IsValid = isValid;
        }

        public string Key => RankedService.ResultKey(SeasonId, MatchId);
    }

    /// <summary>A ranked match created by the matchmaker; it must be resolved or cancelled before settlement.</summary>
    public sealed class RankedMatchTicket
    {
        public string MatchId { get; }
        public string SeasonId { get; }
        public string SnapshotId { get; }
        public string PlayerA { get; }
        public string PlayerB { get; }
        public DateTimeOffset CreatedAt { get; }
        /// <summary>True when both seats came from the coordinated-group queue.</summary>
        public bool GroupMatch { get; }

        public RankedMatchTicket(string matchId, string seasonId, string snapshotId, string playerA, string playerB, DateTimeOffset createdAt, bool groupMatch)
        {
            MatchId = matchId;
            SeasonId = seasonId;
            SnapshotId = snapshotId;
            PlayerA = playerA;
            PlayerB = playerB;
            CreatedAt = createdAt;
            GroupMatch = groupMatch;
        }
    }

    public enum ResultStatus : byte
    {
        /// <summary>Applied: ratings and standings changed.</summary>
        Rated = 0,
        /// <summary>Resolved without a rating change under the published cancellation policy (technical incident, invalid result).</summary>
        Cancelled = 1,
        /// <summary>The same match was already processed; nothing changed (returns the original outcome).</summary>
        Duplicate = 2,
        /// <summary>Not a ranked queue result (casual, practice and friend rooms never touch ratings).</summary>
        ExcludedNotRanked = 3,
        UnknownSeason = 4,
        /// <summary>The season was already settled; no late change can alter final rewards.</summary>
        SeasonSettled = 5,
        /// <summary>No matchmaker ticket exists for this match id (a forged or misrouted result).</summary>
        UnknownMatch = 6,
        /// <summary>Players, season or snapshot disagree with the matchmaker's ticket.</summary>
        TicketMismatch = 7,
    }

    /// <summary>What processing one result did (stored, so a duplicate returns exactly the original).</summary>
    public sealed class ProcessedResult
    {
        public string Key { get; }
        public ResultStatus Status { get; }
        public string PlayerA { get; }
        public string PlayerB { get; }
        public int DeltaA { get; }
        public int DeltaB { get; }
        public DateTimeOffset At { get; }
        public string Note { get; }

        public ProcessedResult(string key, ResultStatus status, string playerA, string playerB, int deltaA, int deltaB, DateTimeOffset at, string note = null)
        {
            Key = key;
            Status = status;
            PlayerA = playerA;
            PlayerB = playerB;
            DeltaA = deltaA;
            DeltaB = deltaB;
            At = at;
            Note = note;
        }

        public ProcessedResult AsDuplicate() => new ProcessedResult(Key, ResultStatus.Duplicate, PlayerA, PlayerB, DeltaA, DeltaB, At, "original: " + Status);
    }

    /// <summary>The hidden, account-wide skill rating used for matchmaking (never reset).</summary>
    public sealed class PlayerSkill
    {
        public string PlayerId { get; }
        public int Rating { get; }
        public int RankedMatches { get; }

        public PlayerSkill(string playerId, int rating, int rankedMatches)
        {
            PlayerId = playerId;
            Rating = rating;
            RankedMatches = rankedMatches;
        }
    }

    /// <summary>A player's visible standing in one season.</summary>
    public sealed class SeasonStanding
    {
        public string SeasonId { get; }
        public string PlayerId { get; }
        public int SeasonRating { get; }
        /// <summary>The visible rating the season started from (soft reset or the starting rating).</summary>
        public int StartRating { get; }
        public int Matches { get; }
        public int Wins { get; }
        public int Draws { get; }
        public int Losses { get; }
        public League PeakLeague { get; }

        public SeasonStanding(string seasonId, string playerId, int seasonRating, int startRating, int matches, int wins, int draws, int losses, League peak)
        {
            SeasonId = seasonId;
            PlayerId = playerId;
            SeasonRating = seasonRating;
            StartRating = startRating;
            Matches = matches;
            Wins = wins;
            Draws = draws;
            Losses = losses;
            PeakLeague = peak;
        }

        public bool PlacementDone(RankedRules rules) => Matches >= rules.PlacementMatches;
        public int PlacementRemaining(RankedRules rules) => Math.Max(0, rules.PlacementMatches - Matches);

        /// <summary>The visible league, or null during placement.</summary>
        public League? VisibleLeague(RankedRules rules) => PlacementDone(rules) ? rules.LeagueFor(SeasonRating) : (League?)null;
    }

    /// <summary>
    /// Ranked persistence. Server integration: <see cref="RankedService.Record"/> must run as one
    /// transaction (the processed-result row has <c>UNIQUE(key)</c>; ticket, skill and standing rows are
    /// updated in the same transaction).
    /// </summary>
    public interface IRankedStore
    {
        PlayerSkill GetSkill(string playerId);
        void SetSkill(PlayerSkill skill);
        SeasonStanding GetStanding(string seasonId, string playerId);
        void SetStanding(SeasonStanding standing);
        IReadOnlyList<SeasonStanding> Standings(string seasonId);
        /// <summary>All of a player's standings, oldest season first.</summary>
        IReadOnlyList<SeasonStanding> History(string playerId);
        ProcessedResult FindResult(string key);
        bool TryAddResult(ProcessedResult result);
        void AddTicket(RankedMatchTicket ticket);
        RankedMatchTicket FindTicket(string matchId);
        void MarkResolved(string matchId);
        IReadOnlyList<RankedMatchTicket> Unresolved(string seasonId);
        IReadOnlyList<RankedMatchTicket> Tickets(string seasonId);
    }

    public sealed class InMemoryRankedStore : IRankedStore, ISeasonHistory
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, PlayerSkill> _skills = new Dictionary<string, PlayerSkill>(StringComparer.Ordinal);
        private readonly Dictionary<string, SeasonStanding> _standings = new Dictionary<string, SeasonStanding>(StringComparer.Ordinal);
        private readonly Dictionary<string, ProcessedResult> _results = new Dictionary<string, ProcessedResult>(StringComparer.Ordinal);
        private readonly Dictionary<string, RankedMatchTicket> _tickets = new Dictionary<string, RankedMatchTicket>(StringComparer.Ordinal);
        private readonly HashSet<string> _resolved = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _seasonOrder = new List<string>();
        private readonly RankedRules _rules;

        public InMemoryRankedStore(RankedRules rules = null) => _rules = rules ?? RankedRules.Default;

        private static string StandingKey(string season, string player) => season + "|" + player;

        public PlayerSkill GetSkill(string playerId) { lock (_gate) return _skills.TryGetValue(playerId, out var s) ? s : null; }
        public void SetSkill(PlayerSkill skill) { lock (_gate) _skills[skill.PlayerId] = skill; }

        public SeasonStanding GetStanding(string seasonId, string playerId)
        {
            lock (_gate) return _standings.TryGetValue(StandingKey(seasonId, playerId), out var s) ? s : null;
        }

        public void SetStanding(SeasonStanding standing)
        {
            lock (_gate)
            {
                if (!_seasonOrder.Contains(standing.SeasonId)) _seasonOrder.Add(standing.SeasonId);
                _standings[StandingKey(standing.SeasonId, standing.PlayerId)] = standing;
            }
        }

        public IReadOnlyList<SeasonStanding> Standings(string seasonId)
        {
            lock (_gate) return _standings.Values.Where(s => s.SeasonId == seasonId).OrderBy(s => s.PlayerId, StringComparer.Ordinal).ToArray();
        }

        public IReadOnlyList<SeasonStanding> History(string playerId)
        {
            lock (_gate)
                return _standings.Values.Where(s => s.PlayerId == playerId)
                    .OrderBy(s => SeasonNumber(s.SeasonId)).ToArray();
        }

        private static int SeasonNumber(string id) =>
            id != null && id.Length > 1 && int.TryParse(id.Substring(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;

        public ProcessedResult FindResult(string key) { lock (_gate) return _results.TryGetValue(key, out var r) ? r : null; }

        public bool TryAddResult(ProcessedResult result)
        {
            lock (_gate)
            {
                if (_results.ContainsKey(result.Key)) return false;
                _results.Add(result.Key, result);
                return true;
            }
        }

        public void AddTicket(RankedMatchTicket ticket) { lock (_gate) _tickets[ticket.MatchId] = ticket; }
        public RankedMatchTicket FindTicket(string matchId) { lock (_gate) return _tickets.TryGetValue(matchId, out var t) ? t : null; }
        public void MarkResolved(string matchId) { lock (_gate) _resolved.Add(matchId); }

        public IReadOnlyList<RankedMatchTicket> Unresolved(string seasonId)
        {
            lock (_gate) return _tickets.Values.Where(t => t.SeasonId == seasonId && !_resolved.Contains(t.MatchId)).OrderBy(t => t.MatchId, StringComparer.Ordinal).ToArray();
        }

        public IReadOnlyList<RankedMatchTicket> Tickets(string seasonId)
        {
            lock (_gate) return _tickets.Values.Where(t => t.SeasonId == seasonId).OrderBy(t => t.MatchId, StringComparer.Ordinal).ToArray();
        }

        public int ProcessedCount { get { lock (_gate) return _results.Count; } }

        /// <summary>Every processed result (rehearsal reconciliation and audits).</summary>
        public IReadOnlyList<ProcessedResult> ProcessedResults()
        {
            lock (_gate) return _results.Values.OrderBy(r => r.Key, StringComparer.Ordinal).ToArray();
        }

        /// <summary>Seasons in which the player completed placement (homeland milestone input).</summary>
        public int SeasonsPlaced(string playerId) => History(playerId).Count(s => s.PlacementDone(_rules));
    }

    /// <summary>
    /// The server-owned ranked rating (plan: "Ranked seasons and cosmetic pass"). Only results from the
    /// ranked queue, created by the matchmaker under the season's frozen snapshot, change ratings; each
    /// (season, match) is applied once. There is no absence decay: nothing here depends on elapsed time.
    /// </summary>
    public sealed class RankedService
    {
        private readonly object _gate = new object();
        private readonly IRankedStore _store;
        private readonly RankedRules _rules;
        private readonly SeasonCalendar _calendar;
        private readonly SnapshotRegistry _snapshots;
        private readonly Func<string, bool> _isSettled;
        private readonly AuditLog _audit;

        public RankedService(IRankedStore store, RankedRules rules, SeasonCalendar calendar, SnapshotRegistry snapshots,
            Func<string, bool> isSettled, AuditLog audit = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _rules = rules ?? RankedRules.Default;
            _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
            _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
            _isSettled = isSettled ?? (_ => false);
            _audit = audit ?? new AuditLog();
        }

        public RankedRules Rules => _rules;
        public IRankedStore Store => _store;

        public static string ResultKey(string seasonId, string matchId) => "ranked:" + seasonId + ":" + matchId;

        /// <summary>Called by the matchmaker when it creates a match; settlement waits until every ticket is resolved.</summary>
        public void RegisterTicket(RankedMatchTicket ticket)
        {
            if (ticket == null) throw new ArgumentNullException(nameof(ticket));
            _store.AddTicket(ticket);
        }

        public PlayerSkill Skill(string playerId) => _store.GetSkill(playerId) ?? new PlayerSkill(playerId, _rules.StartRating, 0);

        /// <summary>The player's standing for a season, creating it from the soft reset if needed (not persisted until a match).</summary>
        public SeasonStanding Standing(string seasonId, string playerId) => _store.GetStanding(seasonId, playerId) ?? InitialStanding(seasonId, playerId);

        private SeasonStanding InitialStanding(string seasonId, string playerId)
        {
            SeasonDefinition season = _calendar.Get(seasonId);
            SeasonStanding previous = _store.History(playerId)
                .Where(s => s.Matches > 0 && (season == null || SeasonNumberOf(s.SeasonId) < season.Number))
                .LastOrDefault();
            int start = previous == null ? _rules.StartRating : _rules.SoftResetRating(previous.SeasonRating);
            return new SeasonStanding(seasonId, playerId, start, start, 0, 0, 0, 0, _rules.LeagueFor(start));
        }

        private int SeasonNumberOf(string seasonId) => _calendar.Get(seasonId)?.Number ?? int.MaxValue;

        public ProcessedResult Record(RankedMatchResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            string key = result.Key;
            if (result.Queue != QueueKind.Ranked)
                return new ProcessedResult(key, ResultStatus.ExcludedNotRanked, result.PlayerA, result.PlayerB, 0, 0, result.CompletedAt);
            lock (_gate)
            {
                ProcessedResult existing = _store.FindResult(key);
                if (existing != null) return existing.AsDuplicate();
                SeasonDefinition season = _calendar.Get(result.SeasonId);
                if (season == null) return new ProcessedResult(key, ResultStatus.UnknownSeason, result.PlayerA, result.PlayerB, 0, 0, result.CompletedAt);
                if (_isSettled(season.Id)) return new ProcessedResult(key, ResultStatus.SeasonSettled, result.PlayerA, result.PlayerB, 0, 0, result.CompletedAt);
                RankedMatchTicket ticket = _store.FindTicket(result.MatchId);
                if (ticket == null) return new ProcessedResult(key, ResultStatus.UnknownMatch, result.PlayerA, result.PlayerB, 0, 0, result.CompletedAt);
                if (ticket.SeasonId != result.SeasonId || ticket.PlayerA != result.PlayerA || ticket.PlayerB != result.PlayerB ||
                    ticket.SnapshotId != result.SnapshotId || !_snapshots.IsKnown(season.Id, result.SnapshotId))
                    return new ProcessedResult(key, ResultStatus.TicketMismatch, result.PlayerA, result.PlayerB, 0, 0, result.CompletedAt);

                ProcessedResult processed;
                if (!result.IsValid || result.Ending == MatchEnding.TechnicalAbort)
                {
                    processed = new ProcessedResult(key, ResultStatus.Cancelled, result.PlayerA, result.PlayerB, 0, 0, result.CompletedAt,
                        result.IsValid ? "technical abort" : "invalid result");
                }
                else
                {
                    RatedOutcome a = result.Winner == null ? RatedOutcome.Draw : result.Winner == PlayerSide.A ? RatedOutcome.Win : RatedOutcome.Loss;
                    RatedOutcome b = a == RatedOutcome.Win ? RatedOutcome.Loss : a == RatedOutcome.Loss ? RatedOutcome.Win : RatedOutcome.Draw;
                    PlayerSkill skillA = Skill(result.PlayerA), skillB = Skill(result.PlayerB);
                    SeasonStanding standA = Standing(season.Id, result.PlayerA), standB = Standing(season.Id, result.PlayerB);
                    int kA = standA.PlacementDone(_rules) ? _rules.K : _rules.PlacementK;
                    int kB = standB.PlacementDone(_rules) ? _rules.K : _rules.PlacementK;
                    int dA = EloRating.Delta(skillA.Rating, skillB.Rating, a, kA, _rules);
                    int dB = EloRating.Delta(skillB.Rating, skillA.Rating, b, kB, _rules);
                    _store.SetSkill(new PlayerSkill(skillA.PlayerId, Math.Max(0, skillA.Rating + dA), skillA.RankedMatches + 1));
                    _store.SetSkill(new PlayerSkill(skillB.PlayerId, Math.Max(0, skillB.Rating + dB), skillB.RankedMatches + 1));
                    _store.SetStanding(Apply(standA, dA, a));
                    _store.SetStanding(Apply(standB, dB, b));
                    processed = new ProcessedResult(key, ResultStatus.Rated, result.PlayerA, result.PlayerB, dA, dB, result.CompletedAt);
                }
                if (!_store.TryAddResult(processed)) return _store.FindResult(key).AsDuplicate();
                _store.MarkResolved(result.MatchId);
                return processed;
            }
        }

        private SeasonStanding Apply(SeasonStanding s, int delta, RatedOutcome outcome)
        {
            int rating = Math.Max(0, s.SeasonRating + delta);
            int matches = s.Matches + 1;
            League peak = s.PeakLeague;
            if (matches >= _rules.PlacementMatches && _rules.LeagueFor(rating) > peak) peak = _rules.LeagueFor(rating);
            return new SeasonStanding(s.SeasonId, s.PlayerId, rating, s.StartRating, matches,
                s.Wins + (outcome == RatedOutcome.Win ? 1 : 0), s.Draws + (outcome == RatedOutcome.Draw ? 1 : 0),
                s.Losses + (outcome == RatedOutcome.Loss ? 1 : 0), peak);
        }

        /// <summary>
        /// Published cancellation policy: an old-season match that has not reported a result by the end
        /// of the season plus the reconciliation grace is treated as a technical incident and cancelled
        /// (no rating change for either player). Returns the cancelled tickets.
        /// </summary>
        public IReadOnlyList<RankedMatchTicket> CancelOverdue(string seasonId, DateTimeOffset now, SeasonRules rules)
        {
            SeasonDefinition season = _calendar.Get(seasonId);
            if (season == null || now < season.EndsAt + rules.ReconciliationGrace) return Array.Empty<RankedMatchTicket>();
            var cancelled = new List<RankedMatchTicket>();
            lock (_gate)
            {
                foreach (RankedMatchTicket t in _store.Unresolved(seasonId))
                {
                    string key = ResultKey(seasonId, t.MatchId);
                    if (_store.TryAddResult(new ProcessedResult(key, ResultStatus.Cancelled, t.PlayerA, t.PlayerB, 0, 0, now, "overdue: cancellation policy")))
                    {
                        _store.MarkResolved(t.MatchId);
                        _audit.Write(now, "system", "ranked.cancel-overdue", seasonId, t.MatchId);
                        cancelled.Add(t);
                    }
                }
            }
            return cancelled;
        }

        /// <summary>Cancels one match after a declared technical incident (support/operations decision, audited).</summary>
        public bool CancelForIncident(string seasonId, string matchId, string incidentId, string decidedBy, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(incidentId) || string.IsNullOrWhiteSpace(decidedBy)) return false;
            lock (_gate)
            {
                RankedMatchTicket t = _store.FindTicket(matchId);
                if (t == null || t.SeasonId != seasonId) return false;
                if (!_store.TryAddResult(new ProcessedResult(ResultKey(seasonId, matchId), ResultStatus.Cancelled, t.PlayerA, t.PlayerB, 0, 0, now, "incident " + incidentId)))
                    return false;
                _store.MarkResolved(matchId);
                _audit.Write(now, decidedBy, "ranked.cancel-incident", seasonId, matchId + " " + incidentId);
                return true;
            }
        }
    }
}
