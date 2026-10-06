using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Common;

namespace AstraKingdoms.Meta.Progression
{
    /// <summary>
    /// The published, capped, one-time rule that moves offline/guest progress into an account
    /// (plan: "never automatic trust in client-provided totals"). The proposed caps are learning-pace
    /// placeholders to be fixed at the progression milestone.
    /// </summary>
    public sealed class GuestMigrationPolicy
    {
        /// <summary>At most this much XP moves over (1,200 XP = level 5).</summary>
        public int MaxXp { get; set; } = 1200;
        /// <summary>At most this many earned coins move over.</summary>
        public int MaxCoins { get; set; } = 150;
        /// <summary>At most this many local match summaries are considered (newest first).</summary>
        public int MaxMatchesConsidered { get; set; } = 200;
        /// <summary>Local matches older than this are ignored.</summary>
        public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(90);

        /// <summary>Player-facing statement of the rule (shown before the player confirms).</summary>
        public string PublishedRule =>
            "When you sign in, progress earned while playing as a guest on this device is added to your account once. " +
            "The server recalculates it from your recent local matches using the normal rules and adds at most " +
            MaxXp + " XP and " + MaxCoins + " coins. It can only be added to one account, and only once per account.";
    }

    /// <summary>One locally recorded match as the client reports it. Totals are never accepted.</summary>
    public sealed class LocalMatchSummary
    {
        public string MatchResultId { get; }
        public MatchKind Kind { get; }
        public PlayerOutcome Outcome { get; }
        public MatchEnding Ending { get; }
        public DateTimeOffset CompletedAt { get; }
        public bool IsAutomation { get; }
        public bool IsDeveloperTest { get; }

        public LocalMatchSummary(string matchResultId, MatchKind kind, PlayerOutcome outcome, MatchEnding ending, DateTimeOffset completedAt,
            bool isAutomation = false, bool isDeveloperTest = false)
        {
            MatchResultId = matchResultId;
            Kind = kind;
            Outcome = outcome;
            Ending = ending;
            CompletedAt = completedAt;
            IsAutomation = isAutomation;
            IsDeveloperTest = isDeveloperTest;
        }
    }

    public enum MigrationStatus : byte
    {
        Granted = 0,
        /// <summary>This account already received its one migration grant.</summary>
        AccountAlreadyMigrated = 1,
        /// <summary>This guest profile was already migrated (into this or another account).</summary>
        GuestAlreadyMigrated = 2,
        /// <summary>No eligible local progress.</summary>
        NothingToMigrate = 3,
    }

    public sealed class MigrationResult
    {
        public MigrationStatus Status { get; }
        public int XpGranted { get; }
        public int CoinsGranted { get; }
        /// <summary>Recalculated amounts before the cap (for support/audit).</summary>
        public int XpRecalculated { get; }
        public int CoinsRecalculated { get; }
        public int MatchesCounted { get; }

        public MigrationResult(MigrationStatus status, int xp, int coins, int xpRecalc, int coinsRecalc, int matches)
        {
            Status = status;
            XpGranted = xp;
            CoinsGranted = coins;
            XpRecalculated = xpRecalc;
            CoinsRecalculated = coinsRecalc;
            MatchesCounted = matches;
        }
    }

    /// <summary>
    /// Server-side guest migration (ticket 57). Recomputes rewards from match summaries with
    /// <see cref="RewardCalculator"/>, de-duplicates ids, caps, and writes one ledger entry per
    /// account. A marker entry per guest profile stops the same local data from seeding many accounts.
    /// </summary>
    public sealed class GuestMigrationService
    {
        private readonly IRewardLedgerStore _ledger;
        private readonly IClock _clock;
        private readonly GuestMigrationPolicy _policy;

        public GuestMigrationService(IRewardLedgerStore ledger, IClock clock, GuestMigrationPolicy policy = null)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _policy = policy ?? new GuestMigrationPolicy();
        }

        public GuestMigrationPolicy Policy => _policy;

        public static string AccountKey(string accountId) => "migration:" + accountId;
        public static string GuestKey(string guestProfileId) => "migration-guest:" + guestProfileId;

        public MigrationResult Migrate(string accountId, string guestProfileId, IReadOnlyList<LocalMatchSummary> localMatches)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("account id required", nameof(accountId));
            if (string.IsNullOrEmpty(guestProfileId)) throw new ArgumentException("guest profile id required", nameof(guestProfileId));

            // Check the account first so a second guest profile is not consumed by an account that cannot receive it.
            if (_ledger.Find(AccountKey(accountId)) != null)
                return new MigrationResult(MigrationStatus.AccountAlreadyMigrated, 0, 0, 0, 0, 0);

            DateTimeOffset now = _clock.UtcNow;
            int xp = 0, coins = 0, counted = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            IEnumerable<LocalMatchSummary> candidates = (localMatches ?? Array.Empty<LocalMatchSummary>())
                .Where(m => m != null && !string.IsNullOrEmpty(m.MatchResultId))
                .Where(m => m.CompletedAt <= now && now - m.CompletedAt <= _policy.MaxAge)
                .OrderByDescending(m => m.CompletedAt)
                .Take(_policy.MaxMatchesConsidered);
            foreach (LocalMatchSummary m in candidates)
            {
                if (!seen.Add(m.MatchResultId)) continue;
                var report = new MatchOutcomeReport(m.MatchResultId, accountId, m.Kind, m.Outcome, m.Ending, m.CompletedAt,
                    isAutomation: m.IsAutomation, isDeveloperTest: m.IsDeveloperTest);
                MatchReward r = RewardCalculator.Compute(report);
                if (r.Eligibility != GrantEligibility.Eligible) continue;
                xp += r.Xp;
                coins += r.Coins;
                counted++;
            }
            if (counted == 0) return new MigrationResult(MigrationStatus.NothingToMigrate, 0, 0, 0, 0, 0);

            var marker = new RewardLedgerEntry(GuestKey(guestProfileId), "guest:" + guestProfileId, LedgerSource.GuestMigration, 0, 0, now,
                reference: "migrated-to:" + accountId);
            LedgerAppendResult markerResult = _ledger.TryAppend(marker);
            if (markerResult.Status == AppendStatus.Duplicate)
                return new MigrationResult(MigrationStatus.GuestAlreadyMigrated, 0, 0, xp, coins, counted);

            int grantXp = Math.Min(xp, _policy.MaxXp);
            int grantCoins = Math.Min(coins, _policy.MaxCoins);
            var grant = new RewardLedgerEntry(AccountKey(accountId), accountId, LedgerSource.GuestMigration, grantXp, grantCoins, now,
                reference: "guest:" + guestProfileId);
            LedgerAppendResult grantResult = _ledger.TryAppend(grant);
            if (grantResult.Status == AppendStatus.Duplicate) // a concurrent request won the race
                return new MigrationResult(MigrationStatus.AccountAlreadyMigrated, 0, 0, xp, coins, counted);
            return new MigrationResult(MigrationStatus.Granted, grantXp, grantCoins, xp, coins, counted);
        }
    }
}
