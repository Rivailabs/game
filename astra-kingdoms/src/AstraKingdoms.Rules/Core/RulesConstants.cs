namespace AstraKingdoms.Rules.Core
{
    /// <summary>
    /// Frozen numeric defaults for ruleset AK-TR-1. These are proposed, unvalidated design
    /// constants (see plan: "Game rules and acceptance cases"). Changing any value that alters an
    /// outcome requires a new rules version, never a silent code edit.
    /// </summary>
    public static class RulesConstants
    {
        public const string RulesVersion = "AK-TR-1";

        // ---- Board ----
        public const int BoardSize = 256;
        public const int ActiveCells = 51040;
        public const int InitialCellsPerPlayer = 25520;
        /// <summary>90% shortcut: a player reaching at least this many cells wins immediately.</summary>
        public const int VictoryCells = 45936;

        // ---- Match / duel ----
        public const int MaxRounds = 8;
        public const int MaxVolleys = 3;
        /// <summary>HP is stored in hundredths: 100.00 HP = 10,000 units.</summary>
        public const int HpUnitsPerHp = 100;
        public const int StartHpUnits = 100 * HpUnitsPerHp;

        // ---- Loadouts ----
        public const int StarterMaxSlots = 5;
        public const int FullMaxSlots = 6;
        public const int MinSlots = 1;
        public const int RegularWeaponCount = 20;
        public const int StarterWeaponCount = 5;
        /// <summary>Experimental Brahmastra ID (outside the progression table).</summary>
        public const int BrahmastraWeaponId = 1000;
        /// <summary>Server-created Pass ID.</summary>
        public const int PassWeaponId = 0;

        // ---- Input ranges ----
        /// <summary>One pitch/yaw unit is 0.25 degrees.</summary>
        public const int QuarterDegreesPerDegree = 4;
        public const int MinYawQdeg = -8 * 4;
        public const int MaxYawQdeg = 8 * 4;
        public const int MinPowerPercent = 70;
        public const int MaxPowerPercent = 100;

        // ---- Land ----
        /// <summary>Quota floor of 3% expressed in parts-per-million of (cap% x HP difference units).</summary>
        public const long QuotaFloorPpm = 30000;
        public const long QuotaDenominator = 1000000;
        /// <summary>Vajra requires HP difference strictly greater than 60.00 HP.</summary>
        public const int VajraMinExclusiveDiffUnits = 6000;
        public const int MaxCutVertices = 128;
        public const int RotationSteps = 16;
        public const int MinScaleQuarters = 1;
        public const int MaxScaleQuarters = 1024;

        // ---- Timing (milliseconds) ----
        public const int TerrainAnnouncementMs = 2000;
        public const int ChoiceDeadlineMs = 12000;
        public const int SharedPhoneHandoverMs = 6000;
        public const int ResolutionReplayMaxMs = 2500;
        public const int OnlineCutWindowMs = 12000;
        public const int SharedPhoneCutWindowMs = 20000;
        public const int ConsecutiveTimeoutsToForfeit = 2;

        // ---- Physics ----
        public const int TicksPerSecond = 120;
        public const int MaxTicksPerVolley = 360;
        public const int SubTicksPerTick = 65536;
    }
}
