using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>
    /// Per-contact damage arithmetic: base damage x elemental factor x dodge factor x cover factor
    /// as exact rationals, rounded once to 0.01 HP with half-cent ties rounded up.
    /// </summary>
    public static class DamageCalculator
    {
        /// <summary>Chain Bolt's neutral bonus against a covered target (bypasses cover, element and dodge).</summary>
        public const int ChainBonusUnits = 10 * RulesConstants.HpUnitsPerHp;
        public const int BurnUnits = 5 * RulesConstants.HpUnitsPerHp;
        public const int OceanHealUnits = 15 * RulesConstants.HpUnitsPerHp;
        public const int RiverHealUnits = 10 * RulesConstants.HpUnitsPerHp;

        /// <summary>Elemental factor of a projectile against the target's committed defensive element.</summary>
        public static Rational ElementFactor(Element projectile, Element defender, bool thunderActive) =>
            ElementChart.Multiplier(projectile, defender, thunderActive);

        /// <summary>
        /// Dodge factor: full for core/full burst, half for a graze. A charged Forest volley caps the
        /// factor at one half, so a graze stays at one half rather than becoming a quarter.
        /// </summary>
        public static Rational DodgeFactor(ContactKind kind, bool forestActive)
        {
            Rational f = kind == ContactKind.Graze || kind == ContactKind.BurstGraze ? Rational.Half : Rational.One;
            return forestActive ? Rational.Min(f, Rational.Half) : f;
        }

        /// <summary>Cover reduces normal direct damage by 25%; sources do not stack.</summary>
        public static Rational CoverFactor(bool covered) => covered ? Rational.ThreeQuarters : Rational.One;

        /// <summary>Product of the factors applied to base damage, rounded once (half-up) to HP units.</summary>
        public static int ContactDamage(int baseUnits, Rational element, Rational dodge, Rational cover)
        {
            Rational product = element * dodge * cover;
            return checked((int)product.ApplyRoundHalfUp(baseUnits));
        }

        /// <summary>
        /// Single simultaneous health update:
        /// clamp(old − direct − dueBurn + ocean + river, 0, 100 HP).
        /// </summary>
        public static int ApplyHealthBatch(int oldUnits, int directUnits, int burnUnits, int oceanUnits, int riverUnits) =>
            Hp.Clamp((long)oldUnits - directUnits - burnUnits + oceanUnits + riverUnits);
    }
}
