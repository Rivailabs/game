using System.Security.Cryptography;
using AstraKingdoms.Rules.Combat;

namespace AstraKingdoms.Rules.Tests.Combat;

public class FixedTests
{
    private const long One = Fixed.OneRaw;

    [Test]
    public void FromRatio_RoundsOnceToNearest()
    {
        Assert.That(Fixed.FromRatio(1, 3).Raw, Is.EqualTo(1431655765L));
        Assert.That(Fixed.FromRatio(-1, 3).Raw, Is.EqualTo(-1431655765L));
        Assert.That(Fixed.FromRatio(35, 100).ToString(), Is.EqualTo("0.350000"));
        Assert.That(Fixed.FromRatio(-98, 10).ToString(), Is.EqualTo("-9.800000"));
    }

    [Test]
    public void FromRatio_TiesToEven()
    {
        long twoPow33 = 1L << 33;
        Assert.That(Fixed.FromRatio(1, twoPow33).Raw, Is.EqualTo(0));   // 0.5 raw -> 0
        Assert.That(Fixed.FromRatio(3, twoPow33).Raw, Is.EqualTo(2));   // 1.5 raw -> 2
        Assert.That(Fixed.FromRatio(5, twoPow33).Raw, Is.EqualTo(2));   // 2.5 raw -> 2
        Assert.That(Fixed.FromRatio(-3, twoPow33).Raw, Is.EqualTo(-2)); // symmetric
    }

    [Test]
    public void Multiply_RoundsTiesToEvenWithWideIntermediate()
    {
        Assert.That((Fixed.FromRaw(1) * Fixed.Half).Raw, Is.EqualTo(0));
        Assert.That((Fixed.FromRaw(3) * Fixed.Half).Raw, Is.EqualTo(2));
        Assert.That((Fixed.FromRaw(-3) * Fixed.Half).Raw, Is.EqualTo(-2));
        Assert.That((Fixed.FromInt(3) * Fixed.FromInt(-4)).Raw, Is.EqualTo(-12 * One));
        Assert.That((Fixed.FromRaw(long.MaxValue) * Fixed.One).Raw, Is.EqualTo(long.MaxValue));
        Assert.That((Fixed.FromRaw(long.MinValue) * Fixed.One).Raw, Is.EqualTo(long.MinValue));
        // (2^20)^2 = 2^40 does not fit in Q32.32.
        Assert.Throws<OverflowException>(() => { var _ = Fixed.FromInt(1 << 20) * Fixed.FromInt(1 << 20); });
    }

    [Test]
    public void Divide_RoundsTiesToEven()
    {
        Assert.That((Fixed.One / Fixed.FromInt(3)).Raw, Is.EqualTo(1431655765L));
        Assert.That((Fixed.FromInt(-7) / Fixed.FromInt(2)).ToString(), Is.EqualTo("-3.500000"));
        Assert.That((Fixed.FromRaw(1) / Fixed.FromInt(2)).Raw, Is.EqualTo(0));
        Assert.That((Fixed.FromRaw(3) / Fixed.FromInt(2)).Raw, Is.EqualTo(2));
        Assert.Throws<DivideByZeroException>(() => { var _ = Fixed.One / Fixed.Zero; });
        Assert.Throws<OverflowException>(() => { var _ = Fixed.FromInt(1 << 30) / Fixed.FromRaw(1); });
    }

    [Test]
    public void DivInt_IsTheIntegrationRounding()
    {
        Assert.That(Fixed.FromRaw(5).DivInt(2).Raw, Is.EqualTo(2));
        Assert.That(Fixed.FromRaw(7).DivInt(2).Raw, Is.EqualTo(4));
        Assert.That(Fixed.FromRaw(-5).DivInt(2).Raw, Is.EqualTo(-2));
        Assert.That(Fixed.FromRaw(-7).DivInt(2).Raw, Is.EqualTo(-4));
        Assert.That(Fixed.FromInt(12).DivInt(120).Raw, Is.EqualTo(Fixed.FromRatio(1, 10).Raw));
    }

    [Test]
    public void Sqrt_IsExactlyRounded()
    {
        Assert.That(Fixed.Sqrt(Fixed.FromInt(4)), Is.EqualTo(Fixed.FromInt(2)));
        Assert.That(Fixed.Sqrt(Fixed.FromInt(2)).Raw, Is.EqualTo(6074001000L)); // 6074000999.952...
        Assert.That(Fixed.Sqrt(Fixed.Zero), Is.EqualTo(Fixed.Zero));
        Assert.That(Fixed.Sqrt(Fixed.FromRatio(2025, 10000)).ToString(), Is.EqualTo("0.450000"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fixed.Sqrt(Fixed.FromInt(-1)));
    }

    [Test]
    public void AddSubtract_AreChecked()
    {
        Assert.Throws<OverflowException>(() => { var _ = Fixed.FromRaw(long.MaxValue) + Fixed.FromRaw(1); });
        Assert.That((Fixed.FromInt(3) - Fixed.FromInt(5)).ToString(), Is.EqualTo("-2.000000"));
        Assert.That(Fixed.FromInt(1) < Fixed.FromInt(2), Is.True);
    }

    [Test]
    public void MulDiv_MatchesRationalReference()
    {
        // Pseudo-random but fixed operands; reference computed with System.Int128 on the test side.
        long seed = 0x1234567;
        for (int i = 0; i < 2000; i++)
        {
            seed = seed * 6364136223846793005L + 1442695040888963407L;
            long a = seed >> 30;
            seed = seed * 6364136223846793005L + 1442695040888963407L;
            long b = ((seed >> 34) & 0x3FFFFFFF) | (1L << 20);
            if ((seed & 1) == 1) b = -b;
            Int128 product = (Int128)a * b;
            Assert.That((Fixed.FromRaw(a) * Fixed.FromRaw(b)).Raw, Is.EqualTo((long)RoundHalfEven(product, (Int128)1 << 32)));
            Int128 num = (Int128)a << 32;
            Assert.That((Fixed.FromRaw(a) / Fixed.FromRaw(b)).Raw, Is.EqualTo((long)RoundHalfEven(num, b)));
        }
    }

    private static Int128 RoundHalfEven(Int128 n, Int128 d)
    {
        bool neg = (n < 0) != (d < 0);
        Int128 an = Int128.Abs(n), ad = Int128.Abs(d);
        Int128 q = an / ad, r = an % ad;
        if (2 * r > ad || (2 * r == ad && (q & 1) == 1)) q++;
        return neg ? -q : q;
    }
}

public class TrigTableTests
{
    [Test]
    public void TableHashMatchesCommittedBytes()
    {
        byte[] hash = SHA256.HashData(TrigTable.GetTableBytes());
        Assert.That(Convert.ToHexString(hash).ToLowerInvariant(), Is.EqualTo(TrigTable.Sha256Hex));
        Assert.That(TrigTable.Version, Is.EqualTo("AK-TRIG-1"));
        Assert.That(TrigTable.EntryCount, Is.EqualTo(361));
    }

    [Test]
    public void ExactAnchorValues()
    {
        Assert.That(TrigTable.Sin(0), Is.EqualTo(Fixed.Zero));
        Assert.That(TrigTable.Sin(360), Is.EqualTo(Fixed.One));
        Assert.That(TrigTable.Sin(120), Is.EqualTo(Fixed.Half)); // sin 30
        Assert.That(TrigTable.Cos(0), Is.EqualTo(Fixed.One));
        Assert.That(TrigTable.Cos(240), Is.EqualTo(Fixed.Half)); // cos 60
        Assert.That(TrigTable.Sin(-120), Is.EqualTo(-Fixed.Half));
    }

    [Test]
    public void SymmetryAndAccuracyOverWholeRange()
    {
        for (int q = TrigTable.MinQdeg; q <= TrigTable.MaxQdeg; q++)
        {
            Assert.That(TrigTable.Sin(-q), Is.EqualTo(-TrigTable.Sin(q)));
            Assert.That(TrigTable.Cos(-q), Is.EqualTo(TrigTable.Cos(q)));
            // Test-side floating reference only; the engine never uses it.
            double rad = q * Math.PI / 720.0;
            Assert.That(Math.Abs(TrigTable.Sin(q).Raw - Math.Sin(rad) * Fixed.OneRaw), Is.LessThanOrEqualTo(1.0));
            Assert.That(Math.Abs(TrigTable.Cos(q).Raw - Math.Cos(rad) * Fixed.OneRaw), Is.LessThanOrEqualTo(1.0));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => TrigTable.Sin(361));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrigTable.Cos(-361));
    }
}
