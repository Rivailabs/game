using System;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.World
{
    /// <summary>
    /// Frozen constants of the PROPOSED V4 world, rules <see cref="RulesId"/> (plan: "V4 bounded
    /// conquest with a recoverable kingdom", including the "Protection armies and newcomer fairness"
    /// table). Every number is a candidate requiring economy simulation, human fairness tests and
    /// owner approval. Changing one produces a new rules ID and hash. Nothing here touches the
    /// AK-TR-1 duel: encounters are ordinary AK-TR-1 matches and conquest never changes HP, damage,
    /// weapon access or purchases.
    /// </summary>
    public static class WorldRules
    {
        public const string RulesId = "AK-W4-0-proposed";

        // ---- World structure ----
        public const int SeasonDays = 18;
        public const int BorderTilesPerAccount = 12;
        public const int HomelandPlots = 12;

        // ---- Challenges ----
        public const long ChallengeExpiryMs = 10 * 60 * 1000L;

        // ---- Protection table ----
        public const int MaxBorderLossesPerUtcDay = 2;
        public const int MaxBorderLossesPerSeason = 6;
        public const long RepeatTargetWindowMs = 24 * WorldTime.MsPerHour;
        public const int MaxAllianceSuccessesPerDefenderPerUtcDay = 1;
        public const int StarterProtectionDays = 7;
        public const int TrainingEncountersToEndProtectionEarly = 5;

        // ---- Fair opponents (PROPOSED range; requires balance approval) ----
        public const int MaxRatingDifference = 200;
        public const int NewcomerMaxWorldEncounters = 10;
        public const int VeteranMinWorldEncounters = 50;

        // ---- Tactical armies ----
        public const int DeploymentPoints = 10;

        // ---- Alliances (reuse the twenty-member clan structure) ----
        public const int AllianceMaxMembers = 20;
        public const int AllianceMaxOfficers = 2;
        public const int ObjectiveContributionCapPerMember = 100;

        public const long SeasonLengthMs = SeasonDays * WorldTime.MsPerDay;
        public const long StarterProtectionMs = StarterProtectionDays * WorldTime.MsPerDay;

        private static readonly Lazy<byte[]> HashLazy = new Lazy<byte[]>(ComputeHash);

        /// <summary>SHA-256 over every constant, the army table, the economy table and the embedded AK-TR-1 rules hash.</summary>
        public static byte[] Hash => (byte[])HashLazy.Value.Clone();

        public static string HashHex => Hex.Encode(HashLazy.Value);

        private static byte[] ComputeHash()
        {
            var w = new CanonicalWriter();
            w.Ascii("AK-W4-RULES/1").Ascii(RulesId).Block(RulesBundle.Hash);
            w.Named("SeasonDays", SeasonDays).Named("BorderTilesPerAccount", BorderTilesPerAccount).Named("HomelandPlots", HomelandPlots)
             .Named("ChallengeExpiryMs", ChallengeExpiryMs).Named("MaxBorderLossesPerUtcDay", MaxBorderLossesPerUtcDay)
             .Named("MaxBorderLossesPerSeason", MaxBorderLossesPerSeason).Named("RepeatTargetWindowMs", RepeatTargetWindowMs)
             .Named("MaxAllianceSuccessesPerDefenderPerUtcDay", MaxAllianceSuccessesPerDefenderPerUtcDay)
             .Named("StarterProtectionDays", StarterProtectionDays)
             .Named("TrainingEncountersToEndProtectionEarly", TrainingEncountersToEndProtectionEarly)
             .Named("MaxRatingDifference", MaxRatingDifference).Named("NewcomerMaxWorldEncounters", NewcomerMaxWorldEncounters)
             .Named("VeteranMinWorldEncounters", VeteranMinWorldEncounters).Named("DeploymentPoints", DeploymentPoints)
             .Named("AllianceMaxMembers", AllianceMaxMembers).Named("AllianceMaxOfficers", AllianceMaxOfficers)
             .Named("ObjectiveContributionCapPerMember", ObjectiveContributionCapPerMember);
            Armies.ArmyRules.WriteTo(w);
            Economy.EconomyRules.WriteTo(w);
            return w.Sha256();
        }
    }

    /// <summary>UTC time helpers. The world never reads a clock itself; callers pass UTC milliseconds.</summary>
    public static class WorldTime
    {
        public const long MsPerHour = 3_600_000L;
        public const long MsPerDay = 24 * MsPerHour;

        /// <summary>UTC day number (days since the Unix epoch) of an instant.</summary>
        public static long UtcDay(long utcMs) => utcMs >= 0 ? utcMs / MsPerDay : (utcMs - MsPerDay + 1) / MsPerDay;

        public static long StartOfUtcDay(long day) => day * MsPerDay;
    }
}
