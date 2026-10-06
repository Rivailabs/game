using System;
using System.Globalization;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>
    /// Signed Q32.32 fixed-point number: <c>value = Raw / 2^32</c>. This is the authoritative
    /// coordinate, velocity and acceleration representation of the numerical combat contract.
    /// <para>
    /// Addition and subtraction are exact (checked). Multiplication and division use exact
    /// 128-bit intermediates and round once to the nearest representable value with ties to
    /// even; any result that does not fit in 64 bits throws <see cref="OverflowException"/>.
    /// No operation uses floating point; <see cref="ToDouble"/> exists for presentation only.
    /// </para>
    /// </summary>
    public readonly struct Fixed : IEquatable<Fixed>, IComparable<Fixed>
    {
        public const int FractionBits = 32;
        public const long OneRaw = 1L << FractionBits;

        /// <summary>Raw two's-complement Q32.32 bits.</summary>
        public readonly long Raw;

        private Fixed(long raw)
        {
            Raw = raw;
        }

        public static readonly Fixed Zero = new Fixed(0);
        public static readonly Fixed One = new Fixed(OneRaw);
        public static readonly Fixed Half = new Fixed(OneRaw / 2);

        public static Fixed FromRaw(long raw) => new Fixed(raw);

        public static Fixed FromInt(long value) => new Fixed(checked(value * OneRaw));

        /// <summary>
        /// The exact rational <paramref name="numerator"/>/<paramref name="denominator"/> rounded once to
        /// the nearest Q32.32 value, ties to even. Used for every decimal constant (e.g. 0.35 m, -9.8 m/s^2).
        /// </summary>
        public static Fixed FromRatio(long numerator, long denominator)
        {
            if (denominator == 0) throw new DivideByZeroException();
            bool negative = (numerator < 0) != (denominator < 0);
            Int128Lite n = Int128Lite.MulUnsigned(Int128Lite.Magnitude(numerator), (ulong)OneRaw);
            return new Fixed(DivideRounded(n, Int128Lite.Magnitude(denominator), negative));
        }

        /// <summary>Millimetres to metres (exactly rounded).</summary>
        public static Fixed FromMillimetres(long mm) => FromRatio(mm, 1000);

        public static Fixed operator +(Fixed a, Fixed b) => new Fixed(checked(a.Raw + b.Raw));
        public static Fixed operator -(Fixed a, Fixed b) => new Fixed(checked(a.Raw - b.Raw));
        public static Fixed operator -(Fixed a) => new Fixed(checked(-a.Raw));

        /// <summary>Product rounded to nearest, ties to even, using a 128-bit intermediate.</summary>
        public static Fixed operator *(Fixed a, Fixed b)
        {
            bool negative = (a.Raw < 0) != (b.Raw < 0);
            Int128Lite p = Int128Lite.MulUnsigned(Int128Lite.Magnitude(a.Raw), Int128Lite.Magnitude(b.Raw));
            Int128Lite q = p.ShiftRightLogical(FractionBits);
            ulong r = p.Lo & 0xFFFFFFFFUL;
            q = Int128Lite.RoundHalfEven(q, r, 1UL << FractionBits);
            return new Fixed(Int128Lite.ToSignedChecked(q, negative && !q.IsZero));
        }

        /// <summary>Quotient rounded to nearest, ties to even, using a 128-bit intermediate.</summary>
        public static Fixed operator /(Fixed a, Fixed b)
        {
            if (b.Raw == 0) throw new DivideByZeroException();
            bool negative = (a.Raw < 0) != (b.Raw < 0);
            Int128Lite n = new Int128Lite(0, Int128Lite.Magnitude(a.Raw)) << FractionBits;
            return new Fixed(DivideRounded(n, Int128Lite.Magnitude(b.Raw), negative));
        }

        /// <summary>Multiplies by an integer (exact, checked).</summary>
        public Fixed MulInt(long k) => new Fixed(checked(Raw * k));

        /// <summary>
        /// Divides by an integer, rounding to nearest with ties to even. This is the
        /// <c>round(a/120)</c> and <c>round(v/120)</c> of the integration rule.
        /// </summary>
        public Fixed DivInt(long divisor) => new Fixed(DivRoundHalfEven(Raw, divisor));

        /// <summary>Signed 64-bit division rounded to nearest, ties to even.</summary>
        public static long DivRoundHalfEven(long numerator, long divisor)
        {
            if (divisor == 0) throw new DivideByZeroException();
            bool negative = (numerator < 0) != (divisor < 0);
            ulong n = Int128Lite.Magnitude(numerator);
            ulong d = Int128Lite.Magnitude(divisor);
            ulong q = n / d;
            ulong r = n % d;
            ulong rest = d - r;
            if (r > rest || (r == rest && (q & 1UL) == 1UL)) q++;
            return Int128Lite.ToSignedChecked(new Int128Lite(0, q), negative && q != 0);
        }

        /// <summary>round(a x b / c) with ties to even, using an exact 128-bit intermediate.</summary>
        public static long MulDivRoundHalfEven(long a, long b, long c)
        {
            if (c == 0) throw new DivideByZeroException();
            bool negative = ((a < 0) != (b < 0)) != (c < 0);
            Int128Lite p = Int128Lite.MulUnsigned(Int128Lite.Magnitude(a), Int128Lite.Magnitude(b));
            return DivideRounded(p, Int128Lite.Magnitude(c), negative);
        }

        /// <summary>
        /// Square root rounded to nearest (exact integer square root of <c>Raw * 2^32</c>).
        /// Used for reported distances; hit decisions compare exact squared distances instead.
        /// </summary>
        public static Fixed Sqrt(Fixed x)
        {
            if (x.Raw < 0) throw new ArgumentOutOfRangeException(nameof(x), "Square root of a negative value.");
            Int128Lite n = new Int128Lite(0, (ulong)x.Raw) << FractionBits;
            ulong r = Int128Lite.SqrtFloorUnsigned(n);
            // Round to nearest: n > r^2 + r  <=>  sqrt(n) > r + 0.5 (n integer, so no exact tie).
            Int128Lite rr = Int128Lite.MulUnsigned(r, r) + new Int128Lite(0, r);
            if (Int128Lite.CompareUnsigned(n, rr) > 0) r++;
            return new Fixed((long)r);
        }

        public static Fixed Abs(Fixed a) => a.Raw < 0 ? -a : a;
        public static Fixed Min(Fixed a, Fixed b) => a.Raw <= b.Raw ? a : b;
        public static Fixed Max(Fixed a, Fixed b) => a.Raw >= b.Raw ? a : b;
        public static Fixed Clamp(Fixed v, Fixed lo, Fixed hi) => v.Raw < lo.Raw ? lo : v.Raw > hi.Raw ? hi : v;

        public static bool operator ==(Fixed a, Fixed b) => a.Raw == b.Raw;
        public static bool operator !=(Fixed a, Fixed b) => a.Raw != b.Raw;
        public static bool operator <(Fixed a, Fixed b) => a.Raw < b.Raw;
        public static bool operator >(Fixed a, Fixed b) => a.Raw > b.Raw;
        public static bool operator <=(Fixed a, Fixed b) => a.Raw <= b.Raw;
        public static bool operator >=(Fixed a, Fixed b) => a.Raw >= b.Raw;

        public int Sign => Raw < 0 ? -1 : Raw > 0 ? 1 : 0;

        public bool Equals(Fixed other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is Fixed f && f.Raw == Raw;
        public override int GetHashCode() => Raw.GetHashCode();
        public int CompareTo(Fixed other) => Raw.CompareTo(other.Raw);

        /// <summary>Presentation-only conversion (e.g. Unity transforms). Never feed back into rules.</summary>
        public double ToDouble() => Raw / (double)OneRaw;

        /// <summary>Canonical decimal text with six fractional digits, computed with integer arithmetic.</summary>
        public override string ToString()
        {
            bool negative = Raw < 0;
            Int128Lite scaled = Int128Lite.MulUnsigned(Int128Lite.Magnitude(Raw), 1000000UL);
            Int128Lite q = scaled.ShiftRightLogical(FractionBits);
            q = Int128Lite.RoundHalfEven(q, scaled.Lo & 0xFFFFFFFFUL, 1UL << FractionBits);
            ulong micros = q.Lo;
            string text = (micros / 1000000UL).ToString(CultureInfo.InvariantCulture) + "." +
                          (micros % 1000000UL).ToString("000000", CultureInfo.InvariantCulture);
            return negative && micros != 0 ? "-" + text : text;
        }

        private static long DivideRounded(Int128Lite magnitude, ulong divisor, bool negative)
        {
            Int128Lite q = Int128Lite.DivRemUnsigned(magnitude, divisor, out ulong r);
            q = Int128Lite.RoundHalfEven(q, r, divisor);
            return Int128Lite.ToSignedChecked(q, negative && !q.IsZero);
        }
    }

    /// <summary>Three-dimensional Q32.32 vector (metres, metres per second, ...).</summary>
    public readonly struct FixedVector3 : IEquatable<FixedVector3>
    {
        public readonly Fixed X;
        public readonly Fixed Y;
        public readonly Fixed Z;

        public FixedVector3(Fixed x, Fixed y, Fixed z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly FixedVector3 Zero = new FixedVector3(Fixed.Zero, Fixed.Zero, Fixed.Zero);

        public static FixedVector3 operator +(FixedVector3 a, FixedVector3 b) => new FixedVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static FixedVector3 operator -(FixedVector3 a, FixedVector3 b) => new FixedVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        /// <summary>Component-wise division by an integer, each rounded to nearest with ties to even.</summary>
        public FixedVector3 DivInt(long divisor) => new FixedVector3(X.DivInt(divisor), Y.DivInt(divisor), Z.DivInt(divisor));

        public bool Equals(FixedVector3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is FixedVector3 v && Equals(v);
        public override int GetHashCode() => (X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode();
        public static bool operator ==(FixedVector3 a, FixedVector3 b) => a.Equals(b);
        public static bool operator !=(FixedVector3 a, FixedVector3 b) => !a.Equals(b);

        public override string ToString() => "(" + X + ", " + Y + ", " + Z + ")";
    }
}
