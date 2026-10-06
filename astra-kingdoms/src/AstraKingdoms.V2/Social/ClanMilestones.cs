using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.V2.Common;
using AstraKingdoms.V2.Kingdom;

namespace AstraKingdoms.V2.Social
{
    /// <summary>
    /// Cooperative clan cosmetic milestones for one season (plan: "capped individual contribution so a
    /// few highly active members cannot control all progress"). PROPOSED: one point per completed
    /// match; each member contributes at most <see cref="PerMemberCap"/> points per clan per season,
    /// which is less than the first threshold, so no single player can unlock anything alone.
    /// </summary>
    public sealed class ClanMilestoneTrack
    {
        public string Version { get; } = "AK-CLAN-MILESTONES-1";
        public IReadOnlyList<int> Thresholds { get; }
        public IReadOnlyList<string> RewardIds { get; }
        public int PerMemberCap { get; }
        public int PointsPerMatch { get; }

        public ClanMilestoneTrack(IReadOnlyList<int> thresholds, IReadOnlyList<string> rewardIds, int perMemberCap, int pointsPerMatch)
        {
            if (thresholds.Count != rewardIds.Count) throw new ArgumentException("one reward per threshold");
            Thresholds = thresholds;
            RewardIds = rewardIds;
            PerMemberCap = perMemberCap;
            PointsPerMatch = pointsPerMatch;
        }

        public static readonly ClanMilestoneTrack Default = new ClanMilestoneTrack(new[] { 60, 150, 300 },
            new[] { DecorationCatalog.ClanRewardId(1), DecorationCatalog.ClanRewardId(2), DecorationCatalog.ClanRewardId(3) }, 25, 1);

        public IReadOnlyList<string> Validate(int maxMembers)
        {
            var errors = new List<string>();
            if (Thresholds.Count == 0) errors.Add("no thresholds");
            if (PerMemberCap >= Thresholds[0]) errors.Add("one member could reach the first milestone alone");
            if ((long)PerMemberCap * maxMembers < Thresholds[Thresholds.Count - 1]) errors.Add("a full clan cannot reach the last milestone");
            for (int i = 1; i < Thresholds.Count; i++)
                if (Thresholds[i] <= Thresholds[i - 1]) errors.Add("thresholds must increase");
            return errors;
        }

        public static string MilestoneId(int index) => "m" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed class ClanProgressView
    {
        public int Total { get; }
        public int ReachedCount { get; }
        public IReadOnlyDictionary<string, int> Contributions { get; }

        public ClanProgressView(int total, int reached, IReadOnlyDictionary<string, int> contributions)
        {
            Total = total;
            ReachedCount = reached;
            Contributions = contributions;
        }
    }

    /// <summary>Contribution bookkeeping and milestone grants (one grant record per member per milestone).</summary>
    public sealed class ClanMilestoneService
    {
        private readonly object _gate = new object();
        private readonly ClanMilestoneTrack _track;
        private readonly ClanService _clans;
        private readonly ClanChatService _feed;
        private readonly IGrantLedgerStore _grants;
        private readonly IClock _clock;
        // (clan|season|player) -> points; contributions survive leave/rejoin so the cap cannot be reset.
        private readonly Dictionary<string, int> _contrib = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _sources = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _reached = new HashSet<string>(StringComparer.Ordinal);

        public ClanMilestoneService(ClanMilestoneTrack track, ClanService clans, ClanChatService feed, IGrantLedgerStore grants, IClock clock)
        {
            _track = track ?? ClanMilestoneTrack.Default;
            _clans = clans ?? throw new ArgumentNullException(nameof(clans));
            _feed = feed;
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public ClanMilestoneTrack Track => _track;

        /// <summary>Adds a member's contribution for one completed match (idempotent per match result).</summary>
        public int RecordMatch(string seasonId, MatchOutcomeReport report)
        {
            if (report == null || RewardCalculator.Eligibility(report) != GrantEligibility.Eligible) return 0;
            Clan clan = _clans.ClanOf(report.PlayerId);
            if (clan == null) return 0;
            lock (_gate)
            {
                string source = clan.Id + "|" + seasonId + "|" + report.MatchResultId + "|" + report.PlayerId;
                if (!_sources.Add(source)) return 0;
                string key = clan.Id + "|" + seasonId + "|" + report.PlayerId;
                int before = _contrib.TryGetValue(key, out int v) ? v : 0;
                int after = Math.Min(_track.PerMemberCap, before + _track.PointsPerMatch);
                _contrib[key] = after;
                int added = after - before;
                if (added > 0) GrantReachedLocked(clan, seasonId);
                return added;
            }
        }

        public ClanProgressView Progress(string clanId, string seasonId)
        {
            lock (_gate)
            {
                string prefix = clanId + "|" + seasonId + "|";
                var contributions = _contrib.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key.Substring(prefix.Length), kv => kv.Value, StringComparer.Ordinal);
                int total = contributions.Values.Sum();
                return new ClanProgressView(total, _track.Thresholds.Count(t => total >= t), contributions);
            }
        }

        private void GrantReachedLocked(Clan clan, string seasonId)
        {
            string prefix = clan.Id + "|" + seasonId + "|";
            int total = _contrib.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(kv => kv.Value);
            for (int i = 0; i < _track.Thresholds.Count; i++)
            {
                if (total < _track.Thresholds[i]) break;
                string milestone = ClanMilestoneTrack.MilestoneId(i);
                bool first = _reached.Add(prefix + milestone);
                foreach (ClanMember m in clan.Members)
                    _grants.TryAppend(new GrantRecord(GrantKeys.ClanMilestone(clan.Id, seasonId, milestone, m.PlayerId), m.PlayerId, GrantSource.ClanMilestone,
                        _track.RewardIds[i], clan.Id + "/" + seasonId + "/" + milestone, _clock.UtcNow));
                if (first) _feed?.PostSystemNotice(clan.Id, "clan.notice.milestoneReached");
            }
        }
    }
}
