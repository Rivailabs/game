namespace AstraKingdoms.Rules.Core
{
    /// <summary>
    /// The five-element counter cycle. Each element beats two and loses to two:
    /// Agni beats Vayu, Prithvi; Vayu beats Prithvi, Vidyut; Prithvi beats Vidyut, Varuna;
    /// Vidyut beats Varuna, Agni; Varuna beats Agni, Vayu.
    /// </summary>
    public static class ElementChart
    {
        /// <summary>True when <paramref name="attacker"/> has advantage over <paramref name="defender"/>.</summary>
        public static bool Beats(Element attacker, Element defender)
        {
            if (attacker == Element.Neutral || defender == Element.Neutral || attacker == defender) return false;
            // Elements 1..5 in cycle order; attacker beats the next two in the cycle.
            int a = (int)attacker - 1;
            int d = (int)defender - 1;
            int diff = ((d - a) % 5 + 5) % 5;
            return diff == 1 || diff == 2;
        }

        /// <summary>
        /// Elemental factor: 150% advantage, 50% disadvantage, otherwise 100%.
        /// Thunder Crown's unsuppressed ability replaces 150% with 200% (not multiplied).
        /// </summary>
        public static Rational Multiplier(Element attacker, Element defender, bool thunderAdvantage = false)
        {
            if (Beats(attacker, defender)) return thunderAdvantage ? Rational.Double : Rational.ThreeHalves;
            if (Beats(defender, attacker)) return Rational.Half;
            return Rational.One;
        }
    }
}
