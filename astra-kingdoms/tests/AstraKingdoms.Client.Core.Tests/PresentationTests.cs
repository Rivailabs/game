using AstraKingdoms.Client.Combat;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Client.Presentation;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Tests;

public sealed class PresentationTests
{
    private static Localizer English() =>
        new(LocalizationTable.Parse("en", File.ReadAllText(Path.Combine(Paths.Localization, "en.txt"))));

    private static VolleyResult Resolve(VolleyInput a, VolleyInput b)
    {
        Loadout la = Loadout.Create(CatalogPreset.Starter, new[] { 1, 2, 3, 4, 5 });
        Loadout lb = Loadout.Create(CatalogPreset.Starter, new[] { 1, 2, 3, 4, 5 });
        return Duel.Start(1, TerrainType.Plain, PlayerSide.B, la, lb, false, false, false).Resolve(a, b);
    }

    [Test]
    public void TimelineCompressesLongFlightsIntoTheResolutionWindow()
    {
        var t = new PlaybackTimeline(360L * 65536, 2.5, 0.7); // 3 s of simulated flight
        Assert.That(t.TotalSeconds, Is.LessThanOrEqualTo(2.5 + 1e-9));
        Assert.That(t.FlightSeconds, Is.EqualTo(1.8).Within(1e-9));
        Assert.That(t.Compression, Is.EqualTo(3.0 / 1.8).Within(1e-9));
        Assert.That(t.SimTimeAt(0), Is.EqualTo(0));
        Assert.That(t.SimTimeAt(0.9), Is.EqualTo(180L * 65536));
        Assert.That(t.SimTimeAt(10), Is.EqualTo(360L * 65536));

        var shortFlight = new PlaybackTimeline(60L * 65536, 2.5, 0.7); // 0.5 s: shown in real time
        Assert.That(shortFlight.Compression, Is.EqualTo(1.0));
        Assert.That(shortFlight.FlightSeconds, Is.EqualTo(0.5).Within(1e-9));
    }

    [Test]
    public void TimelineForARealVolleyStaysWithinBudget()
    {
        VolleyResult r = Resolve(new VolleyInput(3, 240, 0, 100, Dodge.None), new VolleyInput(1, 60, 0, 100, Dodge.Jump));
        var t = PlaybackTimeline.For(r);
        Assert.That(t.SimEndSubTicks, Is.GreaterThan(0));
        Assert.That(t.TotalSeconds, Is.LessThanOrEqualTo(RulesConstants.ResolutionReplayMaxMs / 1000.0 + 1e-9));
        long prev = -1;
        for (double s = 0; s <= t.FlightSeconds + 0.1; s += 0.05)
        {
            long now = t.SimTimeAt(s);
            Assert.That(now, Is.GreaterThanOrEqualTo(prev));
            prev = now;
        }
    }

    [Test]
    public void PreviewStartsAtTheLaunchPointAndStopsEarly()
    {
        var arcs = TrajectoryPreview.Compute(PlayerSide.A, 1, 80, 0, 100, 0, 0);
        Assert.That(arcs, Has.Count.EqualTo(1));
        PreviewPoint p0 = arcs[0][0];
        Assert.That(p0.X, Is.EqualTo(0.35).Within(1e-6));
        Assert.That(p0.Y, Is.EqualTo(1.35).Within(1e-6));
        Assert.That(p0.Z, Is.EqualTo(0).Within(1e-6));
        Assert.That(arcs[0].Count, Is.LessThanOrEqualTo(TrajectoryPreview.DefaultMaxTicks + 1));
        Assert.That(arcs[0][arcs[0].Count - 1].X, Is.GreaterThan(p0.X), "A shoots towards +x");

        var b = TrajectoryPreview.Compute(PlayerSide.B, 1, 80, 0, 100, 0, 0);
        Assert.That(b[0][0].X, Is.EqualTo(8 - 0.35).Within(1e-6));
        Assert.That(b[0][b[0].Count - 1].X, Is.LessThan(b[0][0].X), "B shoots towards -x");
        Assert.That(TrajectoryPreview.Compute(PlayerSide.A, 0, 0, 0, 100, 0, 0), Is.Empty, "Pass has no arc");
    }

    [Test]
    public void PreviewClampsOutOfRangeInput()
    {
        Assert.DoesNotThrow(() => TrajectoryPreview.Compute(PlayerSide.A, 2, 4000, 999, 10, 0, 0));
    }

    [Test]
    public void ExplanationNamesHitMissMultiplierAndHp()
    {
        var builder = new ExplanationBuilder(English(), s => s == PlayerSide.A ? "Asha" : "Bala");
        VolleyResult r = Resolve(new VolleyInput(1, 61, 0, 100, Dodge.None), VolleyInput.Pass());
        List<string> lines = builder.Build(r.Explanation);
        string text = string.Join("\n", lines);
        Assert.That(text, Does.Contain("Bala: no choice in time"));
        Assert.That(text, Does.Contain("Asha: HP"));
        Assert.That(text, Does.Contain("Bala: HP 100.00 -> " + Hp.Format(r.Explanation.B.HpAfterUnits)));
        Assert.That(text, Does.Not.Contain("explain."), "no raw keys");
        Assert.That(text, Does.Not.Contain("…"), "no missing-string placeholders");
    }

    [Test]
    public void ExplanationShowsHitsWithElementEffect()
    {
        var builder = new ExplanationBuilder(English(), s => s.ToString());
        // Find an aim that hits a standing target with Ember Arrow (Agni) against Gale Arrow (Vayu): Agni beats Vayu.
        var aim = AstraKingdoms.Rules.Bots.AimSolver.Solve(1, PlayerSide.A, Fixed.Zero, Fixed.Zero);
        VolleyResult r = Resolve(new VolleyInput(1, aim.PitchQdeg, aim.YawQdeg, 100, Dodge.None), new VolleyInput(2, -20, 32, 70, Dodge.None));
        Assert.That(r.Explanation.B.Incoming, Is.Not.Empty, "the solved aim should hit a standing target");
        string line = builder.AttackLine(r.Explanation, PlayerSide.A, conceal: false);
        Assert.That(line, Does.Contain("Ember Arrow (Agni (flame))"));
        Assert.That(line, Does.Contain("×1.5"));
        Assert.That(line, Does.Contain("beats"));
        string veiled = builder.AttackLine(r.Explanation, PlayerSide.A, conceal: true);
        Assert.That(veiled, Does.Not.Contain("Ember"));
        Assert.That(veiled, Does.Not.Contain("Agni"));
    }

    [Test]
    public void MultiplierText()
    {
        Assert.That(ExplanationBuilder.Multiplier(Rational.ThreeHalves), Is.EqualTo("×1.5"));
        Assert.That(ExplanationBuilder.Multiplier(Rational.Half), Is.EqualTo("×0.5"));
        Assert.That(ExplanationBuilder.Multiplier(Rational.ThreeQuarters), Is.EqualTo("×0.75"));
        Assert.That(ExplanationBuilder.Multiplier(Rational.One), Is.EqualTo("×1"));
    }

    [Test]
    public void ElementGlyphsAreDistinctShapes()
    {
        const int size = 64;
        var elements = (Element[])Enum.GetValues(typeof(Element));
        var masks = elements.ToDictionary(e => e, e => ElementGlyphs.Rasterize(e, size));
        foreach (Element e in elements)
        {
            double ink = ElementGlyphs.InkCount(masks[e]) / (double)(size * size);
            Assert.That(ink, Is.InRange(0.05, 0.65), e + " ink coverage");
        }
        for (int i = 0; i < elements.Length; i++)
        {
            for (int j = i + 1; j < elements.Length; j++)
            {
                int diff = 0;
                for (int k = 0; k < size * size; k++) if (masks[elements[i]][k] != masks[elements[j]][k]) diff++;
                Assert.That(diff / (double)(size * size), Is.GreaterThan(0.1), elements[i] + " vs " + elements[j]);
            }
        }
    }
}
