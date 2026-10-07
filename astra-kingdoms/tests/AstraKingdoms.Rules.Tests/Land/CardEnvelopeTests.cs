using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Tests.Land;

/// <summary>
/// Envelope rasterization checks. Doubles appear only here, as an independent reference for the
/// integer implementation.
/// </summary>
[TestFixture]
public class CardEnvelopeTests
{
    /// <summary>Signed margin of a local point: positive inside, negative outside.</summary>
    private static double ReferenceMargin(CardId card, double u, double v)
    {
        switch (card)
        {
            case CardId.Chakra:
                return 1 - (u * u + v * v);
            case CardId.Garuda:
                return 1 - (Math.Abs(u) / 2 + Math.Abs(v));
            case CardId.Suchi:
            {
                (double x, double y)[] tri = { (-0.5, -0.5), (2, 0), (-0.5, 0.5) };
                double m = double.MaxValue;
                for (int i = 0; i < 3; i++)
                {
                    var a = tri[i];
                    var b = tri[(i + 1) % 3];
                    double len = Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
                    m = Math.Min(m, ((b.x - a.x) * (v - a.y) - (b.y - a.y) * (u - a.x)) / len);
                }
                return m;
            }
            case CardId.Makara:
            {
                static double Rect(double u, double v, double u0, double u1, double v0, double v1) =>
                    Math.Min(Math.Min(u - u0, u1 - u), Math.Min(v - v0, v1 - v));
                return Math.Max(Rect(u, v, -1, 1.5, -0.5, 0.5), Rect(u, v, 0.5, 1.5, 0.5, 1.5));
            }
            case CardId.Padma:
            {
                (double x, double y)[] centres = { (0, 0), (0.45, 0), (-0.45, 0), (0, 0.45), (0, -0.45) };
                return centres.Max(c => 0.55 * 0.55 - ((u - c.x) * (u - c.x) + (v - c.y) * (v - c.y)));
            }
            case CardId.Vajra:
                return Math.Max(1 - Math.Abs(u) - Math.Abs(v), Math.Min(1.8 - Math.Abs(u), 0.25 - Math.Abs(v)));
            default:
                throw new ArgumentOutOfRangeException(nameof(card));
        }
    }

    private static double ReferenceArea(CardId card) => card switch
    {
        CardId.Chakra => Math.PI,
        CardId.Garuda => 4.0,
        CardId.Suchi => 1.25,
        CardId.Makara => 3.5,
        CardId.Vajra => 2.925,
        _ => throw new ArgumentOutOfRangeException(nameof(card)),
    };

    [Test]
    public void TrigTable_IsQuarterTurnSymmetricAndNormalized()
    {
        const double k = 1 << 30;
        for (int i = 0; i < 16; i++)
        {
            Assert.That(CardEnvelope.CosTable[(i + 4) % 16], Is.EqualTo(-CardEnvelope.SinTable[i]));
            Assert.That(CardEnvelope.SinTable[(i + 4) % 16], Is.EqualTo(CardEnvelope.CosTable[i]));
            Assert.That(CardEnvelope.CosTable[i], Is.EqualTo((long)Math.Round(Math.Cos(i * Math.PI / 8) * k)));
            Assert.That(CardEnvelope.SinTable[i], Is.EqualTo((long)Math.Round(Math.Sin(i * Math.PI / 8) * k)));
        }
    }

    [Test]
    public void IntegerMembership_MatchesDoubleReferenceAwayFromBoundary()
    {
        var rng = new TestRng(12345);
        int compared = 0;
        foreach (CardId card in Cards.All)
        {
            for (int i = 0; i < 4000; i++)
            {
                int q = rng.Range(1, 400);
                int rot = rng.Range(0, 15);
                int reach = q / 2 + 2;
                int dx = rng.Range(-reach, reach);
                int dy = rng.Range(-reach, reach);
                double theta = rot * Math.PI / 8;
                double s = q / 4.0;
                double u = (dx * Math.Cos(theta) + dy * Math.Sin(theta)) / s;
                double v = (-dx * Math.Sin(theta) + dy * Math.Cos(theta)) / s;
                double margin = ReferenceMargin(card, u, v);
                if (Math.Abs(margin) < 1e-6) continue;
                compared++;
                Assert.That(CardEnvelope.ContainsOffset(card, q, rot, dx, dy), Is.EqualTo(margin > 0),
                    $"{card} q={q} rot={rot} d=({dx},{dy})");
            }
        }
        Assert.That(compared, Is.GreaterThan(20000));
    }

    [Test]
    public void ExactBoundaryPoints_AreIncluded()
    {
        // Scale 4 quarters = 1 cell per local unit, rotation 0: local coordinates are the offsets.
        Assert.That(CardEnvelope.ContainsOffset(CardId.Chakra, 4, 0, 1, 0), Is.True);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Chakra, 4, 0, 1, 1), Is.False);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Garuda, 4, 0, 2, 0), Is.True);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Garuda, 4, 0, 0, 1), Is.True);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Suchi, 4, 0, 2, 0), Is.True);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Suchi, 4, 0, 3, 0), Is.False);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Vajra, 4, 0, 1, 0), Is.True);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Vajra, 4, 0, 0, 1), Is.True);
        // Scale 8 = 2 cells per unit: Makara corner (1.5,1.5) -> offset (3,3), Vajra end (1.8,0) -> 3.6.
        Assert.That(CardEnvelope.ContainsOffset(CardId.Makara, 8, 0, 3, 3), Is.True);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Makara, 8, 0, -2, 1), Is.True);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Makara, 8, 0, -2, 2), Is.False);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Vajra, 8, 0, 3, 0), Is.True);
        Assert.That(CardEnvelope.ContainsOffset(CardId.Vajra, 8, 0, 4, 0), Is.False);
    }

    [Test]
    public void SmallestScale_IsSingleCentreCell()
    {
        foreach (CardId card in Cards.All)
            for (int rot = 0; rot < 16; rot++)
                Assert.That(CardEnvelope.CountCanonicalCells(card, 1, rot), Is.EqualTo(1), $"{card} rot {rot}");
    }

    [TestCase(400)]
    [TestCase(1024)]
    public void Chakra_LargeScale_ApproximatesPiRSquared(int q)
    {
        double r = q / 4.0;
        int count = CardEnvelope.CountCanonicalCells(CardId.Chakra, q, 0);
        Assert.That(count, Is.EqualTo(Math.PI * r * r).Within(0.2).Percent);
    }

    [Test]
    public void Chakra_IsRotationInvariant()
    {
        int baseline = CardEnvelope.CountCanonicalCells(CardId.Chakra, 203, 0);
        for (int rot = 1; rot < 16; rot++)
            Assert.That(CardEnvelope.CountCanonicalCells(CardId.Chakra, 203, rot), Is.EqualTo(baseline).Within(4), $"rot {rot}");
        // Quarter turns of a disk are exact.
        Assert.That(CardEnvelope.CountCanonicalCells(CardId.Chakra, 203, 4), Is.EqualTo(baseline));
    }

    [Test]
    public void Suchi_AllSixteenRotationsHaveSimilarCounts()
    {
        double expected = 1.25 * 40 * 40;
        for (int rot = 0; rot < 16; rot++)
            Assert.That(CardEnvelope.CountCanonicalCells(CardId.Suchi, 160, rot), Is.EqualTo(expected).Within(3).Percent, $"rot {rot}");
    }

    [Test]
    public void CardAreas_MatchCanonicalShapes()
    {
        // 200 cells per local unit, so closed-boundary lattice points add well under 1.5%.
        const int q = 800;
        const double cellsPerUnit = q / 4.0;
        foreach (CardId card in new[] { CardId.Chakra, CardId.Garuda, CardId.Suchi, CardId.Makara, CardId.Vajra })
        {
            for (int rot = 0; rot < 16; rot += 5)
            {
                double expected = ReferenceArea(card) * cellsPerUnit * cellsPerUnit;
                Assert.That(CardEnvelope.CountCanonicalCells(card, q, rot), Is.EqualTo(expected).Within(1.5).Percent, $"{card} rot {rot}");
            }
        }

        // Padma: numerically integrate the union of disks on a fine grid.
        int inside = 0;
        const int n = 1000;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (ReferenceMargin(CardId.Padma, -1 + 2.0 * (i + 0.5) / n, -1 + 2.0 * (j + 0.5) / n) >= 0) inside++;
        double padmaArea = 4.0 * inside / (n * (double)n);
        Assert.That(CardEnvelope.CountCanonicalCells(CardId.Padma, q, 0), Is.EqualTo(padmaArea * cellsPerUnit * cellsPerUnit).Within(1.5).Percent);
    }

    [Test]
    public void Rotation_TurnsClockwiseOnTheYDownMap()
    {
        // Suchi points along +u. Scale 40 quarters = 10 cells per unit, so its tip is 20 cells out.
        var right = new CardPose(100, 100, 40, 0);
        var down = new CardPose(100, 100, 40, 4);
        var left = new CardPose(100, 100, 40, 8);
        var downRight = new CardPose(100, 100, 40, 2);
        Assert.That(CardEnvelope.Contains(CardId.Suchi, right, 118, 100), Is.True);
        Assert.That(CardEnvelope.Contains(CardId.Suchi, right, 100, 118), Is.False);
        Assert.That(CardEnvelope.Contains(CardId.Suchi, down, 100, 118), Is.True, "rotation 4 points to +y (down)");
        Assert.That(CardEnvelope.Contains(CardId.Suchi, down, 118, 100), Is.False);
        Assert.That(CardEnvelope.Contains(CardId.Suchi, left, 82, 100), Is.True);
        Assert.That(CardEnvelope.Contains(CardId.Suchi, downRight, 112, 112), Is.True);
    }

    [Test]
    public void RasterizeOnBoard_ClipsToActiveCells()
    {
        var pose = new CardPose(0, 128, 400, 0);
        List<int> cells = CardEnvelope.RasterizeOnBoard(CardId.Chakra, pose);
        Assert.That(cells, Is.Ordered.Ascending);
        Assert.That(cells.All(Board.IsActive), Is.True);
        Assert.That(cells.Count, Is.LessThan(CardEnvelope.CountCanonicalCells(CardId.Chakra, 400, 0)));
        Assert.That(cells.All(c => CardEnvelope.Contains(CardId.Chakra, pose, c)), Is.True);
    }

    [Test]
    public void FitsAllowance_AgreesWithCanonicalCount()
    {
        foreach (CardId card in Cards.All)
        {
            int count = CardEnvelope.CountCanonicalCells(card, 120, 5);
            int exactQuota = (count + 1) / 2; // 2Q >= count
            Assert.That(CardEnvelope.FitsAllowance(card, 120, 5, exactQuota), Is.True, card.ToString());
            if (count % 2 == 0)
                Assert.That(CardEnvelope.FitsAllowance(card, 120, 5, count / 2 - 1), Is.False, card.ToString());
            else
                Assert.That(CardEnvelope.FitsAllowance(card, 120, 5, count / 2), Is.False, card.ToString());
            Assert.That(CardEnvelope.FitsAllowance(card, 120, 5, 0), Is.False);
        }
        Assert.That(CardEnvelope.FitsAllowance(CardId.Makara, 1024, 0, 10), Is.False);
    }

    [Test]
    public void InvalidPoses_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CardEnvelope.ContainsOffset(CardId.Chakra, 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CardEnvelope.ContainsOffset(CardId.Chakra, 1025, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CardEnvelope.ContainsOffset(CardId.Chakra, 4, 16, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CardEnvelope.Contains(CardId.Chakra, new CardPose(256, 0, 4, 0), 0, 0));
    }
}
