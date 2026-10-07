using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Meta.Economy
{
    /// <summary>
    /// What a daily task measures. There is intentionally no "watch an ad" or "buy something" kind:
    /// tasks must never require ad viewing or purchases (plan: "Modes and fair progression").
    /// </summary>
    public enum TaskRequirement : byte
    {
        /// <summary>Completed eligible matches (any mode).</summary>
        CompletedMatches = 0,
        /// <summary>Distinct elements used across completed eligible matches.</summary>
        DistinctElements = 1,
        /// <summary>Completed practice exercises or practice matches.</summary>
        PracticeExercises = 2,
    }

    public sealed class DailyTaskDefinition
    {
        public string Id { get; }
        public TaskRequirement Requirement { get; }
        public int Target { get; }
        public int RewardCoins { get; }
        public string EnglishText { get; }
        public string TextKey => "task." + Id;

        public DailyTaskDefinition(string id, TaskRequirement requirement, int target, int rewardCoins, string englishText)
        {
            Id = id;
            Requirement = requirement;
            Target = target;
            RewardCoins = rewardCoins;
            EnglishText = englishText;
        }
    }

    /// <summary>The three optional daily tasks (pilot values, 20 coins each).</summary>
    public static class DailyTaskCatalog
    {
        public static readonly IReadOnlyList<DailyTaskDefinition> Default = new[]
        {
            new DailyTaskDefinition("finish-two-matches", TaskRequirement.CompletedMatches, 2, ProgressionRules.CoinsDailyTask, "Finish two matches"),
            new DailyTaskDefinition("two-elements", TaskRequirement.DistinctElements, 2, ProgressionRules.CoinsDailyTask, "Use two different elements"),
            new DailyTaskDefinition("practice-exercise", TaskRequirement.PracticeExercises, 1, ProgressionRules.CoinsDailyTask, "Complete a practice exercise"),
        };

        public static DailyTaskDefinition Find(string id) => Default.FirstOrDefault(d => d.Id == id);
    }

    /// <summary>
    /// When a task day starts. Days are computed from the authority's clock in a fixed offset, so a
    /// device clock change cannot open a new day. Default: midnight India Standard Time (UTC+05:30).
    /// </summary>
    public sealed class DailyResetPolicy
    {
        public TimeSpan UtcOffset { get; }

        public DailyResetPolicy(TimeSpan utcOffset) => UtcOffset = utcOffset;

        public static readonly DailyResetPolicy IndiaStandardTime = new DailyResetPolicy(new TimeSpan(5, 30, 0));

        public string DayKey(DateTimeOffset instant) =>
            instant.ToOffset(UtcOffset).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>The instant the given day ends (exclusive); claims expire then.</summary>
        public DateTimeOffset DayEnd(string dayKey)
        {
            DateTime date = DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new DateTimeOffset(date.AddDays(1), UtcOffset);
        }
    }

    /// <summary>One player's progress for one task day. Immutable; replaced atomically by the store.</summary>
    public sealed class DailyProgress
    {
        public IReadOnlyCollection<string> CountedMatches { get; }
        public IReadOnlyCollection<Element> Elements { get; }
        public IReadOnlyCollection<string> PracticeEvents { get; }

        public DailyProgress(IEnumerable<string> matches, IEnumerable<Element> elements, IEnumerable<string> practice)
        {
            CountedMatches = new HashSet<string>(matches ?? Array.Empty<string>(), StringComparer.Ordinal);
            Elements = new HashSet<Element>(elements ?? Array.Empty<Element>());
            PracticeEvents = new HashSet<string>(practice ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        public static readonly DailyProgress Empty = new DailyProgress(null, null, null);

        public int ValueFor(TaskRequirement r)
        {
            switch (r)
            {
                case TaskRequirement.CompletedMatches: return CountedMatches.Count;
                case TaskRequirement.DistinctElements: return Elements.Count;
                case TaskRequirement.PracticeExercises: return PracticeEvents.Count;
                default: return 0;
            }
        }
    }

    /// <summary>
    /// Atomic per-(player, day) progress storage.
    /// <para>Server integration: one row per (player_id, day_key) holding the three sets, updated in
    /// a transaction (or with optimistic concurrency on a version column) so concurrent match
    /// results for one player cannot lose an update.</para>
    /// </summary>
    public interface IDailyTaskProgressStore
    {
        DailyProgress Get(string playerId, string dayKey);
        DailyProgress Update(string playerId, string dayKey, Func<DailyProgress, DailyProgress> mutate);
        int DeletePlayer(string playerId);
    }

    public sealed class InMemoryDailyTaskProgressStore : IDailyTaskProgressStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, DailyProgress> _rows = new Dictionary<string, DailyProgress>(StringComparer.Ordinal);

        private static string Key(string playerId, string dayKey) => playerId + "|" + dayKey;

        public DailyProgress Get(string playerId, string dayKey)
        {
            lock (_gate) return _rows.TryGetValue(Key(playerId, dayKey), out DailyProgress p) ? p : DailyProgress.Empty;
        }

        public DailyProgress Update(string playerId, string dayKey, Func<DailyProgress, DailyProgress> mutate)
        {
            lock (_gate)
            {
                string k = Key(playerId, dayKey);
                DailyProgress current = _rows.TryGetValue(k, out DailyProgress p) ? p : DailyProgress.Empty;
                DailyProgress next = mutate(current) ?? current;
                _rows[k] = next;
                return next;
            }
        }

        public int DeletePlayer(string playerId)
        {
            lock (_gate)
            {
                string prefix = playerId + "|";
                List<string> keys = _rows.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                foreach (string k in keys) _rows.Remove(k);
                return keys.Count;
            }
        }

        /// <summary>Snapshot for persisting the offline guest profile.</summary>
        public IReadOnlyList<KeyValuePair<string, DailyProgress>> Rows()
        {
            lock (_gate) return _rows.ToList();
        }

        public void Restore(string playerId, string dayKey, DailyProgress progress)
        {
            lock (_gate) _rows[Key(playerId, dayKey)] = progress;
        }
    }

    public enum ClaimStatus : byte
    {
        Claimed = 0,
        /// <summary>Already claimed (a retry): nothing new is granted; the original amount is reported.</summary>
        AlreadyClaimed = 1,
        NotComplete = 2,
        /// <summary>The task day has ended; unclaimed rewards lapse (no back-fill).</summary>
        Expired = 3,
        /// <summary>The day has not started (client clock ahead of the authority).</summary>
        NotYetAvailable = 4,
        UnknownTask = 5,
    }

    public sealed class ClaimResult
    {
        public ClaimStatus Status { get; }
        public int Coins { get; }

        public ClaimResult(ClaimStatus status, int coins)
        {
            Status = status;
            Coins = coins;
        }
    }

    /// <summary>One task as shown to the player.</summary>
    public sealed class DailyTaskView
    {
        public DailyTaskDefinition Definition { get; }
        public string DayKey { get; }
        public int Progress { get; }
        public bool Complete => Progress >= Definition.Target;
        public bool Claimed { get; }
        public bool Claimable => Complete && !Claimed;
        public DateTimeOffset ExpiresAt { get; }

        public DailyTaskView(DailyTaskDefinition definition, string dayKey, int progress, bool claimed, DateTimeOffset expiresAt)
        {
            Definition = definition;
            DayKey = dayKey;
            Progress = Math.Min(progress, definition.Target);
            Claimed = claimed;
            ExpiresAt = expiresAt;
        }
    }

    /// <summary>
    /// Ticket 58: optional daily cosmetic tasks. Progress events are de-duplicated by match result id
    /// / exercise event id; claims are ledger entries keyed by (player, day, task), so claim retries,
    /// double taps and concurrent requests grant once. Unclaimed tasks expire at the day's end.
    /// <para>Server integration: call <see cref="RecordMatch"/> next to
    /// <see cref="ProgressionService.GrantForMatch"/>, <see cref="RecordPracticeExercise"/> from the
    /// tutorial/practice service, and expose <see cref="GetTasks"/>/<see cref="Claim"/> as
    /// authenticated endpoints for the calling player only.</para>
    /// </summary>
    public sealed class DailyTaskService
    {
        private readonly IRewardLedgerStore _ledger;
        private readonly IDailyTaskProgressStore _progress;
        private readonly IClock _clock;
        private readonly DailyResetPolicy _reset;
        private readonly IReadOnlyList<DailyTaskDefinition> _tasks;

        public DailyTaskService(IRewardLedgerStore ledger, IDailyTaskProgressStore progress, IClock clock,
            DailyResetPolicy reset = null, IReadOnlyList<DailyTaskDefinition> tasks = null)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _reset = reset ?? DailyResetPolicy.IndiaStandardTime;
            _tasks = tasks ?? DailyTaskCatalog.Default;
        }

        public DailyResetPolicy Reset => _reset;

        public static string ClaimKey(string playerId, string dayKey, string taskId) => "daily:" + playerId + ":" + dayKey + ":" + taskId;

        /// <summary>Counts an eligible match towards the day in which it completed. Repeats are ignored.</summary>
        public bool RecordMatch(MatchOutcomeReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (RewardCalculator.Eligibility(report) != GrantEligibility.Eligible) return false;
            string day = _reset.DayKey(report.CompletedAt);
            bool changed = false;
            _progress.Update(report.PlayerId, day, p =>
            {
                if (p.CountedMatches.Contains(report.MatchResultId)) return p;
                changed = true;
                IEnumerable<string> practice = p.PracticeEvents;
                if (report.Kind == MatchKind.Practice) practice = practice.Concat(new[] { "match:" + report.MatchResultId });
                return new DailyProgress(p.CountedMatches.Concat(new[] { report.MatchResultId }), p.Elements.Concat(report.ElementsUsed), practice);
            });
            return changed;
        }

        /// <summary>Counts a completed practice exercise (tutorial drill, aiming exercise). Repeats are ignored.</summary>
        public bool RecordPracticeExercise(string playerId, string exerciseEventId, DateTimeOffset completedAt)
        {
            if (string.IsNullOrEmpty(exerciseEventId)) throw new ArgumentException("event id required", nameof(exerciseEventId));
            string day = _reset.DayKey(completedAt);
            string key = "exercise:" + exerciseEventId;
            bool changed = false;
            _progress.Update(playerId, day, p =>
            {
                if (p.PracticeEvents.Contains(key)) return p;
                changed = true;
                return new DailyProgress(p.CountedMatches, p.Elements, p.PracticeEvents.Concat(new[] { key }));
            });
            return changed;
        }

        /// <summary>Today's tasks for a player (by the authority's clock).</summary>
        public IReadOnlyList<DailyTaskView> GetTasks(string playerId)
        {
            string day = _reset.DayKey(_clock.UtcNow);
            DailyProgress p = _progress.Get(playerId, day);
            DateTimeOffset end = _reset.DayEnd(day);
            return _tasks.Select(t => new DailyTaskView(t, day, p.ValueFor(t.Requirement),
                _ledger.Find(ClaimKey(playerId, day, t.Id)) != null, end)).ToArray();
        }

        /// <summary>
        /// Claims a task for <paramref name="dayKey"/> (the day the client displayed). Only the current
        /// day can be claimed; a retry after success returns <see cref="ClaimStatus.AlreadyClaimed"/>
        /// even after the day ended, so a lost response never looks like a lost reward.
        /// </summary>
        public ClaimResult Claim(string playerId, string taskId, string dayKey)
        {
            DailyTaskDefinition def = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (def == null || !DateTime.TryParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return new ClaimResult(ClaimStatus.UnknownTask, 0);
            string key = ClaimKey(playerId, dayKey, taskId);
            RewardLedgerEntry prior = _ledger.Find(key);
            if (prior != null) return new ClaimResult(ClaimStatus.AlreadyClaimed, prior.CoinDelta);

            string today = _reset.DayKey(_clock.UtcNow);
            int cmp = string.CompareOrdinal(dayKey, today);
            if (cmp < 0) return new ClaimResult(ClaimStatus.Expired, 0);
            if (cmp > 0) return new ClaimResult(ClaimStatus.NotYetAvailable, 0);
            if (_progress.Get(playerId, dayKey).ValueFor(def.Requirement) < def.Target) return new ClaimResult(ClaimStatus.NotComplete, 0);

            var entry = new RewardLedgerEntry(key, playerId, LedgerSource.DailyTask, 0, def.RewardCoins, _clock.UtcNow, reference: taskId + "@" + dayKey);
            LedgerAppendResult r = _ledger.TryAppend(entry);
            return r.Status == AppendStatus.Appended
                ? new ClaimResult(ClaimStatus.Claimed, def.RewardCoins)
                : new ClaimResult(ClaimStatus.AlreadyClaimed, r.Entry?.CoinDelta ?? 0);
        }
    }
}
