using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.V2.Common;
using AstraKingdoms.V2.Seasons;

namespace AstraKingdoms.V2.Ranked
{
    /// <summary>Matchmaking constants (PROPOSED): the acceptable skill gap widens the longer a player waits.</summary>
    public sealed class MatchmakingRules
    {
        public int BaseWindow { get; }
        public int WidenPerStep { get; }
        public TimeSpan Step { get; }
        public int MaxWindow { get; }

        public MatchmakingRules(int baseWindow, int widenPerStep, TimeSpan step, int maxWindow)
        {
            BaseWindow = baseWindow;
            WidenPerStep = widenPerStep;
            Step = step;
            MaxWindow = maxWindow;
        }

        public static readonly MatchmakingRules Default = new MatchmakingRules(50, 25, TimeSpan.FromSeconds(10), 400);

        public int WindowAfter(TimeSpan waited)
        {
            long steps = waited <= TimeSpan.Zero ? 0 : waited.Ticks / Step.Ticks;
            return (int)Math.Min(MaxWindow, BaseWindow + steps * WidenPerStep);
        }
    }

    public enum EnqueueStatus : byte
    {
        Queued = 0,
        AlreadyQueued = 1,
        /// <summary>Matchmaking for this season is closed (before the boundary) or the season is not open.</summary>
        QueueClosed = 2,
        /// <summary>The player has not acknowledged the season's current rules/catalogue snapshot.</summary>
        SnapshotNotAcknowledged = 3,
    }

    /// <summary>One player waiting. <see cref="GroupId"/> is set when the player queued as part of a coordinated group (party/clan squad).</summary>
    public sealed class QueueEntry
    {
        public string PlayerId { get; }
        public string GroupId { get; }
        public DateTimeOffset EnqueuedAt { get; }
        public int SkillRating { get; }

        public QueueEntry(string playerId, string groupId, DateTimeOffset enqueuedAt, int skillRating)
        {
            PlayerId = playerId;
            GroupId = groupId;
            EnqueuedAt = enqueuedAt;
            SkillRating = skillRating;
        }

        public bool IsGroup => GroupId != null;
    }

    /// <summary>
    /// Ranked matchmaking for one season. Pairs players by the hidden skill rating, never mixes a
    /// coordinated group into the solo pool (and never pairs two members of the same group, which
    /// would invite win trading), never pairs accounts that blocked each other, and stops creating
    /// matches at <see cref="SeasonDefinition.MatchmakingClosesAt"/>, which is the season end minus the
    /// measured maximum match duration and a margin. Deterministic for a given queue.
    /// </summary>
    public sealed class RankedMatchmaker
    {
        private readonly object _gate = new object();
        private readonly List<QueueEntry> _queue = new List<QueueEntry>();
        private readonly RankedService _ranked;
        private readonly SnapshotRegistry _snapshots;
        private readonly MatchmakingRules _rules;
        private readonly IClock _clock;
        private readonly ISocialRelations _relations;
        private int _counter;

        public SeasonDefinition Season { get; }

        public RankedMatchmaker(SeasonDefinition season, RankedService ranked, SnapshotRegistry snapshots, IClock clock,
            MatchmakingRules rules = null, ISocialRelations relations = null)
        {
            Season = season ?? throw new ArgumentNullException(nameof(season));
            _ranked = ranked ?? throw new ArgumentNullException(nameof(ranked));
            _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _rules = rules ?? MatchmakingRules.Default;
            _relations = relations;
        }

        public bool IsOpen(DateTimeOffset now) => now >= Season.StartsAt && now < Season.MatchmakingClosesAt;

        /// <summary>
        /// Joins the queue. <paramref name="acknowledgedSnapshotId"/> is the snapshot the entry screen
        /// displayed and the player confirmed; a stale acknowledgement (an emergency change happened)
        /// must be shown again.
        /// </summary>
        public EnqueueStatus Enqueue(string playerId, string acknowledgedSnapshotId, string groupId = null)
        {
            DateTimeOffset now = _clock.UtcNow;
            if (!IsOpen(now)) return EnqueueStatus.QueueClosed;
            SeasonBalanceSnapshot current = _snapshots.Current(Season.Id);
            if (current == null || current.SnapshotId != acknowledgedSnapshotId) return EnqueueStatus.SnapshotNotAcknowledged;
            lock (_gate)
            {
                if (_queue.Any(e => e.PlayerId == playerId)) return EnqueueStatus.AlreadyQueued;
                _queue.Add(new QueueEntry(playerId, groupId, now, _ranked.Skill(playerId).Rating));
                return EnqueueStatus.Queued;
            }
        }

        public bool Leave(string playerId)
        {
            lock (_gate) return _queue.RemoveAll(e => e.PlayerId == playerId) > 0;
        }

        public int Waiting
        {
            get { lock (_gate) return _queue.Count; }
        }

        /// <summary>
        /// Forms matches. After the close time the queue is emptied and the removed players are
        /// returned in <paramref name="closedOut"/> (their clients show the season-closing message).
        /// </summary>
        public IReadOnlyList<RankedMatchTicket> Tick(out IReadOnlyList<string> closedOut)
        {
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                if (!IsOpen(now))
                {
                    closedOut = _queue.Select(e => e.PlayerId).ToArray();
                    _queue.Clear();
                    return Array.Empty<RankedMatchTicket>();
                }
                closedOut = Array.Empty<string>();
                SeasonBalanceSnapshot snapshot = _snapshots.Current(Season.Id);
                var created = new List<RankedMatchTicket>();
                foreach (bool groupPool in new[] { false, true })
                {
                    List<QueueEntry> pool = _queue.Where(e => e.IsGroup == groupPool)
                        .OrderBy(e => e.EnqueuedAt).ThenBy(e => e.PlayerId, StringComparer.Ordinal).ToList();
                    var taken = new HashSet<string>(StringComparer.Ordinal);
                    foreach (QueueEntry a in pool)
                    {
                        if (taken.Contains(a.PlayerId)) continue;
                        QueueEntry best = null;
                        int bestGap = int.MaxValue;
                        foreach (QueueEntry b in pool)
                        {
                            if (b == a || taken.Contains(b.PlayerId)) continue;
                            if (a.IsGroup && a.GroupId == b.GroupId) continue;
                            if (_relations != null && _relations.IsBlockedEitherWay(a.PlayerId, b.PlayerId)) continue;
                            int gap = Math.Abs(a.SkillRating - b.SkillRating);
                            if (gap > _rules.WindowAfter(now - a.EnqueuedAt) || gap > _rules.WindowAfter(now - b.EnqueuedAt)) continue;
                            if (gap < bestGap) { best = b; bestGap = gap; }
                        }
                        if (best == null) continue;
                        taken.Add(a.PlayerId);
                        taken.Add(best.PlayerId);
                        string matchId = Season.Id + "-m" + (++_counter).ToString("000000", CultureInfo.InvariantCulture);
                        var ticket = new RankedMatchTicket(matchId, Season.Id, snapshot.SnapshotId, a.PlayerId, best.PlayerId, now, groupPool);
                        _ranked.RegisterTicket(ticket);
                        created.Add(ticket);
                    }
                    _queue.RemoveAll(e => taken.Contains(e.PlayerId));
                }
                return created;
            }
        }

        public IReadOnlyList<RankedMatchTicket> Tick() => Tick(out _);
    }

    /// <summary>What the ranked entry screen shows before the player may queue.</summary>
    public sealed class RankedEntryView
    {
        public SeasonDefinition Season { get; }
        public SeasonBalanceSnapshot Snapshot { get; }
        public SeasonStanding Standing { get; }
        public League? VisibleLeague { get; }
        public int PlacementRemaining { get; }
        public bool QueueOpen { get; }
        public IReadOnlyList<EmergencyChangeRecord> EmergencyChanges { get; }

        public RankedEntryView(SeasonDefinition season, SeasonBalanceSnapshot snapshot, SeasonStanding standing, RankedRules rules, bool queueOpen,
            IReadOnlyList<EmergencyChangeRecord> emergencies)
        {
            Season = season;
            Snapshot = snapshot;
            Standing = standing;
            VisibleLeague = standing.VisibleLeague(rules);
            PlacementRemaining = standing.PlacementRemaining(rules);
            QueueOpen = queueOpen;
            EmergencyChanges = emergencies;
        }

        public static RankedEntryView Build(RankedMatchmaker mm, RankedService ranked, SnapshotRegistry snapshots, string playerId, DateTimeOffset now) =>
            new RankedEntryView(mm.Season, snapshots.Current(mm.Season.Id), ranked.Standing(mm.Season.Id, playerId), ranked.Rules, mm.IsOpen(now),
                snapshots.EmergencyChanges.Where(e => e.NewSnapshotId.StartsWith(mm.Season.Id + ".", StringComparison.Ordinal)).ToArray());
    }
}
