using System;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>
    /// Minimal two's-complement 128-bit integer used for checked wide intermediates.
    /// netstandard2.1 has neither <c>Int128</c> nor a signed <c>Math.BigMul(long, long, out long)</c>,
    /// so the few operations the combat contract needs are implemented here with plain
    /// 64-bit integer arithmetic. Every operation is exact; nothing here touches floating point.
    /// </summary>
    internal readonly struct Int128Lite : IComparable<Int128Lite>
    {
        public readonly ulong Hi;
        public readonly ulong Lo;

        public Int128Lite(ulong hi, ulong lo)
        {
            Hi = hi;
            Lo = lo;
        }

        public static readonly Int128Lite Zero = new Int128Lite(0, 0);

        public static Int128Lite FromLong(long v) => new Int128Lite(v < 0 ? ulong.MaxValue : 0UL, unchecked((ulong)v));

        public bool IsNegative => (Hi >> 63) != 0;

        public bool IsZero => Hi == 0 && Lo == 0;

        /// <summary>Exact unsigned 64 x 64 -> 128 bit product.</summary>
        public static Int128Lite MulUnsigned(ulong a, ulong b)
        {
            ulong aL = a & 0xFFFFFFFFUL, aH = a >> 32;
            ulong bL = b & 0xFFFFFFFFUL, bH = b >> 32;
            ulong ll = aL * bL;
            ulong lh = aL * bH;
            ulong hl = aH * bL;
            ulong hh = aH * bH;
            ulong mid = (ll >> 32) + (lh & 0xFFFFFFFFUL) + (hl & 0xFFFFFFFFUL);
            ulong lo = (ll & 0xFFFFFFFFUL) | (mid << 32);
            ulong hi = hh + (lh >> 32) + (hl >> 32) + (mid >> 32);
            return new Int128Lite(hi, lo);
        }

        /// <summary>Exact signed 64 x 64 -> 128 bit product.</summary>
        public static Int128Lite Mul(long a, long b)
        {
            Int128Lite p = MulUnsigned(Magnitude(a), Magnitude(b));
            return (a < 0) != (b < 0) ? -p : p;
        }

        /// <summary>|v| as an unsigned value; correct for long.MinValue.</summary>
        public static ulong Magnitude(long v) => v < 0 ? unchecked((ulong)(-(v + 1)) + 1UL) : (ulong)v;

        public static Int128Lite operator +(Int128Lite a, Int128Lite b)
        {
            ulong lo = unchecked(a.Lo + b.Lo);
            ulong carry = lo < a.Lo ? 1UL : 0UL;
            return new Int128Lite(unchecked(a.Hi + b.Hi + carry), lo);
        }

        public static Int128Lite operator -(Int128Lite a) => new Int128Lite(~a.Hi, ~a.Lo) + new Int128Lite(0, 1);

        public static Int128Lite operator -(Int128Lite a, Int128Lite b) => a + (-b);

        public static Int128Lite operator <<(Int128Lite a, int shift)
        {
            if (shift == 0) return a;
            if (shift >= 64) return new Int128Lite(a.Lo << (shift - 64), 0);
            return new Int128Lite((a.Hi << shift) | (a.Lo >> (64 - shift)), a.Lo << shift);
        }

        /// <summary>Logical (unsigned) right shift.</summary>
        public Int128Lite ShiftRightLogical(int shift)
        {
            if (shift == 0) return this;
            if (shift >= 64) return new Int128Lite(0, Hi >> (shift - 64));
            return new Int128Lite(Hi >> shift, (Lo >> shift) | (Hi << (64 - shift)));
        }

        /// <summary>Signed comparison.</summary>
        public int CompareTo(Int128Lite other)
        {
            long h1 = unchecked((long)Hi), h2 = unchecked((long)other.Hi);
            if (h1 != h2) return h1 < h2 ? -1 : 1;
            if (Lo != other.Lo) return Lo < other.Lo ? -1 : 1;
            return 0;
        }

        /// <summary>Unsigned comparison (both values treated as non-negative 128-bit magnitudes).</summary>
        public static int CompareUnsigned(Int128Lite a, Int128Lite b)
        {
            if (a.Hi != b.Hi) return a.Hi < b.Hi ? -1 : 1;
            if (a.Lo != b.Lo) return a.Lo < b.Lo ? -1 : 1;
            return 0;
        }

        public static bool operator <(Int128Lite a, Int128Lite b) => a.CompareTo(b) < 0;
        public static bool operator >(Int128Lite a, Int128Lite b) => a.CompareTo(b) > 0;
        public static bool operator <=(Int128Lite a, Int128Lite b) => a.CompareTo(b) <= 0;
        public static bool operator >=(Int128Lite a, Int128Lite b) => a.CompareTo(b) >= 0;

        /// <summary>
        /// Unsigned 128 / 64 long division by shift-and-subtract. Returns the full 128-bit quotient.
        /// </summary>
        public static Int128Lite DivRemUnsigned(Int128Lite n, ulong d, out ulong remainder)
        {
            if (d == 0) throw new DivideByZeroException();
            ulong qHi = 0, qLo = 0, rem = 0;
            for (int i = 127; i >= 0; i--)
            {
                ulong bit = i >= 64 ? (n.Hi >> (i - 64)) & 1UL : (n.Lo >> i) & 1UL;
                ulong carry = rem >> 63;
                rem = (rem << 1) | bit;
                if (carry != 0 || rem >= d)
                {
                    rem = unchecked(rem - d);
                    if (i >= 64) qHi |= 1UL << (i - 64);
                    else qLo |= 1UL << i;
                }
            }
            remainder = rem;
            return new Int128Lite(qHi, qLo);
        }

        /// <summary>
        /// Rounds the unsigned magnitude <paramref name="q"/> (with remainder r of divisor d) to
        /// nearest, ties to even. Returns the adjusted magnitude.
        /// </summary>
        public static Int128Lite RoundHalfEven(Int128Lite q, ulong r, ulong d)
        {
            ulong rest = d - r; // r < d, so 2r vs d is the same as r vs d - r
            bool up = r > rest || (r == rest && (q.Lo & 1UL) == 1UL);
            return up ? q + new Int128Lite(0, 1) : q;
        }

        /// <summary>Converts a non-negative magnitude to a signed long with the given sign, checking overflow.</summary>
        public static long ToSignedChecked(Int128Lite magnitude, bool negative)
        {
            if (magnitude.Hi != 0) throw new OverflowException("Fixed-point result exceeds 64 bits.");
            if (negative)
            {
                if (magnitude.Lo > 1UL << 63) throw new OverflowException("Fixed-point result exceeds 64 bits.");
                return unchecked(-(long)magnitude.Lo);
            }
            if (magnitude.Lo > long.MaxValue) throw new OverflowException("Fixed-point result exceeds 64 bits.");
            return (long)magnitude.Lo;
        }

        /// <summary>Floor square root of an unsigned 128-bit value (digit-by-digit method).</summary>
        public static ulong SqrtFloorUnsigned(Int128Lite n)
        {
            Int128Lite res = Zero;
            Int128Lite bit = new Int128Lite(1UL << 62, 0); // 4^63
            while (CompareUnsigned(bit, n) > 0) bit = bit.ShiftRightLogical(2);
            while (!bit.IsZero)
            {
                Int128Lite trial = res + bit;
                if (CompareUnsigned(n, trial) >= 0)
                {
                    n = n - trial;
                    res = res.ShiftRightLogical(1) + bit;
                }
                else
                {
                    res = res.ShiftRightLogical(1);
                }
                bit = bit.ShiftRightLogical(2);
            }
            return res.Lo;
        }

        public override string ToString() => "0x" + Hi.ToString("X16") + Lo.ToString("X16");
    }
}
