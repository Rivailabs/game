using System;

namespace AstraKingdoms.Meta.Progression
{
    /// <summary>
    /// Initial economy experiment from the plan ("Modes and fair progression"). These are proposed
    /// learning-pace settings, not retention evidence; tune from observed learning.
    /// </summary>
    public static class ProgressionRules
    {
        public const int XpCompletedHumanMatch = 100;
        public const int XpWinBonus = 25;
        public const int XpDrawBonus = 10;
        public const int XpPracticeMatch = 50;
        public const int XpPerLevel = 300;
        public const int MaxLevel = 20;
        /// <summary>Levels 2-16 each introduce one weapon.</summary>
        public const int FirstWeaponUnlockLevel = 2;
        public const int LastWeaponUnlockLevel = 16;

        public const int CoinsCompletedMatch = 10;
        public const int CoinsWinBonus = 5;
        public const int CoinsDailyTask = 20;

        /// <summary>XP needed to reach <see cref="MaxLevel"/> (19 × 300).</summary>
        public const int XpForMaxLevel = (MaxLevel - 1) * XpPerLevel;

        /// <summary>Level for a total XP: 1 + floor(xp / 300), capped at 20. XP keeps accruing after 20 (mastery).</summary>
        public static int LevelFor(long totalXp)
        {
            if (totalXp <= 0) return 1;
            long level = 1 + totalXp / XpPerLevel;
            return (int)Math.Min(MaxLevel, level);
        }

        /// <summary>XP still needed for the next level, or 0 at the maximum level.</summary>
        public static int XpToNextLevel(long totalXp)
        {
            int level = LevelFor(totalXp);
            if (level >= MaxLevel) return 0;
            return (int)((long)level * XpPerLevel - Math.Max(0, totalXp));
        }

        /// <summary>XP earned inside the current level (0-299), or the XP beyond level 20.</summary>
        public static long XpIntoLevel(long totalXp)
        {
            if (totalXp <= 0) return 0;
            int level = LevelFor(totalXp);
            return totalXp - (long)(level - 1) * XpPerLevel;
        }
    }

    /// <summary>Why a match did or did not earn rewards.</summary>
    public enum GrantEligibility : byte
    {
        Eligible = 0,
        Automation = 1,
        DeveloperTest = 2,
        InvalidMatch = 3,
        /// <summary>Forfeits and technical aborts are not "completed" matches (plan: completion definition).</summary>
        NotCompleted = 4,
    }

    /// <summary>XP and coins for one report, before idempotency is applied.</summary>
    public sealed class MatchReward
    {
        public GrantEligibility Eligibility { get; }
        public int Xp { get; }
        public int Coins { get; }

        public MatchReward(GrantEligibility eligibility, int xp, int coins)
        {
            Eligibility = eligibility;
            Xp = xp;
            Coins = coins;
        }

        public static MatchReward None(GrantEligibility why) => new MatchReward(why, 0, 0);
    }

    /// <summary>Pure reward arithmetic for one match report (tickets 57 and 58).</summary>
    public static class RewardCalculator
    {
        /// <summary>Is the report eligible at all? Order matters only for the reported reason.</summary>
        public static GrantEligibility Eligibility(MatchOutcomeReport r)
        {
            if (r.IsAutomation) return GrantEligibility.Automation;
            if (r.IsDeveloperTest) return GrantEligibility.DeveloperTest;
            if (!r.IsValid) return GrantEligibility.InvalidMatch;
            if (!r.IsNormallyCompleted) return GrantEligibility.NotCompleted;
            return GrantEligibility.Eligible;
        }

        /// <summary>
        /// Human matches: 100 XP, +25 win, +10 draw; 10 coins, +5 win. Practice and labelled-bot matches:
        /// a flat 50 XP and 10 coins (no outcome bonus, so beating an Easy bot is not a farm).
        /// An unattributed outcome (shared phone, device profile) earns the base amounts only.
        /// </summary>
        public static MatchReward Compute(MatchOutcomeReport r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            GrantEligibility e = Eligibility(r);
            if (e != GrantEligibility.Eligible) return MatchReward.None(e);
            bool human = r.Kind == MatchKind.OnlineHuman || r.Kind == MatchKind.LocalSharedPhone;
            if (!human) return new MatchReward(e, ProgressionRules.XpPracticeMatch, ProgressionRules.CoinsCompletedMatch);
            int xp = ProgressionRules.XpCompletedHumanMatch;
            int coins = ProgressionRules.CoinsCompletedMatch;
            if (r.Outcome == PlayerOutcome.Win)
            {
                xp += ProgressionRules.XpWinBonus;
                coins += ProgressionRules.CoinsWinBonus;
            }
            else if (r.Outcome == PlayerOutcome.Draw)
            {
                xp += ProgressionRules.XpDrawBonus;
            }
            return new MatchReward(e, xp, coins);
        }
    }
}
