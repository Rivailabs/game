namespace AstraKingdoms.Rules.Core
{
    /// <summary>Helpers for HP stored in integer hundredths (units).</summary>
    public static class Hp
    {
        public static int FromWhole(int hp) => hp * RulesConstants.HpUnitsPerHp;

        /// <summary>Clamps to 0..AK-TR-1 starting HP (100.00).</summary>
        public static int Clamp(long units) => Clamp(units, RulesConstants.StartHpUnits);

        /// <summary>Clamps to 0..<paramref name="maxUnits"/> (the match's starting HP, ticket 24).</summary>
        public static int Clamp(long units, int maxUnits)
        {
            if (units < 0) return 0;
            if (units > maxUnits) return maxUnits;
            return (int)units;
        }

        /// <summary>Formats units as "45.00".</summary>
        public static string Format(int units)
        {
            int whole = units / 100;
            int frac = System.Math.Abs(units % 100);
            string sign = units < 0 && whole == 0 ? "-" : string.Empty;
            return sign + whole.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
                   frac.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
