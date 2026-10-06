using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Common;

namespace AstraKingdoms.Meta.Progression
{
    /// <summary>Mastery recognition tiers per weapon (cosmetic badges only).</summary>
    public enum MasteryTier : byte
    {
        None = 0,
        Bronze = 1,
        Silver = 2,
        Gold = 3,
    }

    /// <summary>Proposed mastery thresholds: completed eligible matches in which the weapon was used.</summary>
    public static class Mastery
    {
        public const int BronzeUses = 10;
        public const int SilverUses = 25;
        public const int GoldUses = 50;

        public static MasteryTier TierFor(int uses) =>
            uses >= GoldUses ? MasteryTier.Gold : uses >= SilverUses ? MasteryTier.Silver : uses >= BronzeUses ? MasteryTier.Bronze : MasteryTier.None;
    }

    /// <summary>A read-only view of a player's earned progression.</summary>
    public sealed class ProgressionProfile
    {
        public string PlayerId { get; }
        public long TotalXp { get; }
        public int Level { get; }
        public long XpIntoLevel { get; }
        public int XpToNextLevel { get; }
        public long EarnedCoins { get; }
        public IReadOnlyList<int> OwnedWeaponIds { get; }
        public IReadOnlyDictionary<int, MasteryTier> Mastery { get; }

        public ProgressionProfile(PlayerTotals totals)
        {
            PlayerId = totals.PlayerId;
            TotalXp = totals.Xp;
            Level = ProgressionRules.LevelFor(totals.Xp);
            XpIntoLevel = ProgressionRules.XpIntoLevel(totals.Xp);
            XpToNextLevel = ProgressionRules.XpToNextLevel(totals.Xp);
            EarnedCoins = totals.Coins;
            OwnedWeaponIds = WeaponAccess.Owned(Level).Select(w => w.Id).ToArray();
            Mastery = totals.WeaponUses.ToDictionary(kv => kv.Key, kv => Progression.Mastery.TierFor(kv.Value));
        }
    }

    public enum GrantStatus : byte
    {
        Granted = 0,
        /// <summary>This match result was already granted to this player; nothing changed.</summary>
        AlreadyGranted = 1,
        /// <summary>The report is not eligible (automation, developer test, invalid, not completed).</summary>
        Ineligible = 2,
    }

    /// <summary>Outcome of one grant request.</summary>
    public sealed class MatchGrantResult
    {
        public GrantStatus Status { get; }
        public GrantEligibility Eligibility { get; }
        public int XpGranted { get; }
        public int CoinsGranted { get; }
        public int LevelBefore { get; }
        public int LevelAfter { get; }
        public IReadOnlyList<LevelUnlock> NewUnlocks { get; }
        public string IdempotencyKey { get; }

        public MatchGrantResult(GrantStatus status, GrantEligibility eligibility, int xp, int coins, int levelBefore, int levelAfter,
            IReadOnlyList<LevelUnlock> unlocks, string key)
        {
            Status = status;
            Eligibility = eligibility;
            XpGranted = xp;
            CoinsGranted = coins;
            LevelBefore = levelBefore;
            LevelAfter = levelAfter;
            NewUnlocks = unlocks ?? Array.Empty<LevelUnlock>();
            IdempotencyKey = key;
        }
    }

    /// <summary>
    /// Ticket 57: grants XP (and the per-match cosmetic coins of ticket 58) exactly once per
    /// (match result id, player), and exposes the derived profile.
    /// <para>Server integration: host one instance per process over a database-backed
    /// <see cref="IRewardLedgerStore"/>; call <see cref="GrantForMatch"/> from the match service's
    /// terminal-result handler with the authoritative result, once per human seat. Repeated calls
    /// (retries, duplicate events, reconnect replays) are harmless.</para>
    /// </summary>
    public sealed class ProgressionService
    {
        private readonly IRewardLedgerStore _ledger;
        private readonly IClock _clock;

        public ProgressionService(IRewardLedgerStore ledger, IClock clock)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public static string MatchKey(string matchResultId, string playerId) => "match:" + matchResultId + ":" + playerId;

        public MatchGrantResult GrantForMatch(MatchOutcomeReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            string key = MatchKey(report.MatchResultId, report.PlayerId);
            MatchReward reward = RewardCalculator.Compute(report);
            int levelBefore = ProgressionRules.LevelFor(_ledger.Totals(report.PlayerId).Xp);
            if (reward.Eligibility != GrantEligibility.Eligible)
                return new MatchGrantResult(GrantStatus.Ineligible, reward.Eligibility, 0, 0, levelBefore, levelBefore, null, key);

            var entry = new RewardLedgerEntry(key, report.PlayerId, LedgerSource.Match, reward.Xp, reward.Coins, _clock.UtcNow,
                reference: report.MatchResultId, weaponsUsed: report.WeaponsUsed);
            LedgerAppendResult appended = _ledger.TryAppend(entry);
            if (appended.Status == AppendStatus.Duplicate)
            {
                int level = ProgressionRules.LevelFor(_ledger.Totals(report.PlayerId).Xp);
                return new MatchGrantResult(GrantStatus.AlreadyGranted, reward.Eligibility, 0, 0, level, level, null, key);
            }
            // Level before/after are computed around this entry, so concurrent grants still report consistent unlocks.
            PlayerTotals after = _ledger.Totals(report.PlayerId);
            long xpBefore = after.Xp - reward.Xp;
            int before = ProgressionRules.LevelFor(xpBefore);
            int now = ProgressionRules.LevelFor(after.Xp);
            return new MatchGrantResult(GrantStatus.Granted, reward.Eligibility, reward.Xp, reward.Coins, before, now,
                UnlockTable.Between(before, now), key);
        }

        public ProgressionProfile GetProfile(string playerId) => new ProgressionProfile(_ledger.Totals(playerId));

        /// <summary>
        /// Reverses an earlier grant with a corrective entry (e.g. a match later found invalid). The
        /// original entry stays; reversing twice is a no-op.
        /// </summary>
        public LedgerAppendResult Reverse(string originalKey, string reason)
        {
            RewardLedgerEntry original = _ledger.Find(originalKey);
            if (original == null) return new LedgerAppendResult(AppendStatus.Rejected, null, "unknown entry");
            if (original.Source == LedgerSource.Correction) return new LedgerAppendResult(AppendStatus.Rejected, null, "cannot correct a correction");
            var correction = new RewardLedgerEntry("correction:" + originalKey, original.PlayerId, LedgerSource.Correction,
                -original.XpDelta, -original.CoinDelta, _clock.UtcNow, reference: reason, correctsKey: originalKey);
            return _ledger.TryAppend(correction);
        }
    }
}
