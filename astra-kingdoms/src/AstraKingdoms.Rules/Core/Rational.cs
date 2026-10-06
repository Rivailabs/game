using System;

namespace AstraKingdoms.Rules.Core
{
    /// <summary>
    /// Exact non-negative rational used for damage multipliers. Damage is computed as an exact
    /// product and rounded once (half-up) to HP units, per the numerical combat contract.
    /// </summary>
    public readonly struct Rational : IEquatable<Rational>
    {
        public readonly long Numerator;
        public readonly long Denominator;

        public Rational(long numerator, long denominator)
        {
            if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
            if (numerator < 0) throw new ArgumentOutOfRangeException(nameof(numerator));
            long g = Gcd(numerator, denominator);
            Numerator = numerator / g;
            Denominator = denominator / g;
        }

        public static readonly Rational One = new Rational(1, 1);
        public static readonly Rational Zero = new Rational(0, 1);
        public static readonly Rational Half = new Rational(1, 2);
        public static readonly Rational ThreeHalves = new Rational(3, 2);
        public static readonly Rational Double = new Rational(2, 1);
        public static readonly Rational ThreeQuarters = new Rational(3, 4);

        public static Rational operator *(Rational a, Rational b)
        {
            long g1 = Gcd(a.Numerator, b.Denominator);
            long g2 = Gcd(b.Numerator, a.Denominator);
            return new Rational(
                checked((a.Numerator / g1) * (b.Numerator / g2)),
                checked((a.Denominator / g2) * (b.Denominator / g1)));
        }

        public static bool operator <(Rational a, Rational b) =>
            checked(a.Numerator * b.Denominator) < checked(b.Numerator * a.Denominator);

        public static bool operator >(Rational a, Rational b) => b < a;

        public static Rational Min(Rational a, Rational b) => a < b ? a : b;

        /// <summary>Multiplies an integer by this rational and rounds once to nearest, ties up.</summary>
        public long ApplyRoundHalfUp(long value)
        {
            // floor((2*v*n + d) / (2*d)) gives round-half-up for non-negative values.
            long num = checked(2 * value * Numerator + Denominator);
            long den = checked(2 * Denominator);
            return num / den;
        }

        public bool Equals(Rational other) => Numerator == other.Numerator && Denominator == other.Denominator;
        public override bool Equals(object obj) => obj is Rational r && Equals(r);
        public override int GetHashCode() => (Numerator.GetHashCode() * 397) ^ Denominator.GetHashCode();
        public override string ToString() => Numerator + "/" + Denominator;

        private static long Gcd(long a, long b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            if (a == 0) return b == 0 ? 1 : b;
            while (b != 0)
            {
                long t = a % b;
                a = b;
                b = t;
            }
            return a;
        }
    }
}
