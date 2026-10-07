using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.V2.Common;
using AstraKingdoms.V2.Kingdom;
using AstraKingdoms.V2.Ranked;

namespace AstraKingdoms.V2.Seasons
{
    /// <summary>Settled-season flags (server: a row per season written in the settlement transaction).</summary>
    public interface ISettlementStore
    {
        bool IsSettled(string seasonId);
        bool TryMarkSettled(string seasonId, DateTimeOffset at);
    }

    public sealed class InMemorySettlementStore : ISettlementStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, DateTimeOffset> _settled = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        public bool IsSettled(string seasonId)
        {
            lock (_gate) return seasonId != null && _settled.ContainsKey(seasonId);
        }

        public bool TryMarkSettled(string seasonId, DateTimeOffset at)
        {
            lock (_gate)
            {
                if (_settled.ContainsKey(seasonId)) return false;
                _settled.Add(seasonId, at);
                return true;
            }
        }
    }

    public enum SettlementStatus : byte
    {
        Settled = 0,
        AlreadySettled = 1,
        /// <summary>The season boundary has not passed.</summary>
        NotEnded = 2,
        /// <summary>Old-season matches are still unresolved and the reconciliation grace has not expired.</summary>
        AwaitingReconciliation = 3,
        UnknownSeason = 4,
    }

    public sealed class SettlementReport
    {
        public string SeasonId { get; }
        public SettlementStatus Status { get; }
        public IReadOnlyList<string> UnresolvedMatchIds { get; }
        public IReadOnlyList<string> CancelledByPolicy { get; }
        public int LeagueRewards { get; }
        public DeliveryReport PassDeliveries { get; }
        public DateTimeOffset At { get; }

        public SettlementReport(string seasonId, SettlementStatus status, IReadOnlyList<string> unresolved, IReadOnlyList<string> cancelled,
            int leagueRewards, DeliveryReport pass, DateTimeOffset at)
        {
            SeasonId = seasonId;
            Status = status;
            UnresolvedMatchIds = unresolved ?? Array.Empty<string>();
            CancelledByPolicy = cancelled ?? Array.Empty<string>();
            LeagueRewards = leagueRewards;
            PassDeliveries = pass ?? new DeliveryReport();
            At = at;
        }
    }

    /// <summary>
    /// The 18-day season lifecycle (plan: "Ranked seasons and cosmetic pass", "Live operations
    /// cadence"): publish dates and snapshot → open → matchmaking closes before the boundary by the
    /// measured maximum match duration → the boundary passes → every old-season match is reconciled
    /// (results still arriving are applied to the old season; overdue ones are cancelled under the
    /// published policy) → final rewards: league trophies and automatic delivery of earned, unclaimed
    /// pass rewards → settled. Settlement is idempotent and the next season's visible ratings come
    /// from the soft reset, lazily, when a player first plays it.
    /// </summary>
    public sealed class SeasonService
    {
        private readonly object _gate = new object();
        private readonly SeasonCalendar _calendar;
        private readonly ISettlementStore _settlements;
        private readonly RankedService _ranked;
        private readonly SeasonPassService _pass;
        private readonly IGrantLedgerStore _grants;
        private readonly IClock _clock;
        private readonly AuditLog _audit;

        public SeasonService(SeasonCalendar calendar, ISettlementStore settlements, RankedService ranked, SeasonPassService pass,
            IGrantLedgerStore grants, IClock clock, AuditLog audit = null)
        {
            _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
            _settlements = settlements ?? throw new ArgumentNullException(nameof(settlements));
            _ranked = ranked ?? throw new ArgumentNullException(nameof(ranked));
            _pass = pass ?? throw new ArgumentNullException(nameof(pass));
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _audit = audit ?? new AuditLog();
        }

        public SeasonCalendar Calendar => _calendar;

        public SeasonPhase Phase(string seasonId)
        {
            SeasonDefinition s = _calendar.Get(seasonId) ?? throw new ArgumentException("unknown season " + seasonId);
            return s.PhaseAt(_clock.UtcNow, _settlements.IsSettled(seasonId));
        }

        public SeasonDefinition Current => _calendar.At(_clock.UtcNow);

        public SettlementReport Settle(string seasonId)
        {
            DateTimeOffset now = _clock.UtcNow;
            SeasonDefinition season = _calendar.Get(seasonId);
            if (season == null) return new SettlementReport(seasonId, SettlementStatus.UnknownSeason, null, null, 0, null, now);
            lock (_gate)
            {
                if (_settlements.IsSettled(seasonId)) return new SettlementReport(seasonId, SettlementStatus.AlreadySettled, null, null, 0, null, now);
                if (now < season.EndsAt) return new SettlementReport(seasonId, SettlementStatus.NotEnded, null, null, 0, null, now);

                IReadOnlyList<RankedMatchTicket> cancelled = _ranked.CancelOverdue(seasonId, now, _calendar.Rules);
                IReadOnlyList<RankedMatchTicket> unresolved = _ranked.Store.Unresolved(seasonId);
                if (unresolved.Count > 0)
                    return new SettlementReport(seasonId, SettlementStatus.AwaitingReconciliation, unresolved.Select(t => t.MatchId).ToArray(),
                        cancelled.Select(t => t.MatchId).ToArray(), 0, null, now);

                int trophies = 0;
                foreach (SeasonStanding s in _ranked.Store.Standings(seasonId))
                {
                    if (!s.PlacementDone(_ranked.Rules)) continue;
                    League final = _ranked.Rules.LeagueFor(s.SeasonRating);
                    var record = new GrantRecord(GrantKeys.LeagueReward(seasonId, s.PlayerId), s.PlayerId, GrantSource.SeasonLeagueReward,
                        DecorationCatalog.TrophyFor(final.ToString()), seasonId + "/" + final, now);
                    if (_grants.TryAppend(record).Appended) trophies++;
                }
                DeliveryReport pass = _pass.DeliverUnclaimedAtSettlement(seasonId);
                _settlements.TryMarkSettled(seasonId, now);
                _audit.Write(now, "system", "season.settled", seasonId,
                    "trophies=" + trophies + " passFree=" + pass.FreeDelivered + " passPaid=" + pass.PaidDelivered + " cancelled=" + cancelled.Count);
                return new SettlementReport(seasonId, SettlementStatus.Settled, null, cancelled.Select(t => t.MatchId).ToArray(), trophies, pass, now);
            }
        }
    }
}
