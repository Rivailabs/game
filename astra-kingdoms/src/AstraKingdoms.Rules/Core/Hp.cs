namespace AstraKingdoms.Rules.Core
{
    /// <summary>Helpers for HP stored in integer hundredths (units).</summary>
    public static class Hp
    {
        public static int FromWhole(int hp) => hp * RulesConstants.HpUnitsPerHp;

        public static int Clamp(long units)
        {
            if (units < 0) return 0;
            if (units > RulesConstants.StartHpUnits) return RulesConstants.StartHpUnits;
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
