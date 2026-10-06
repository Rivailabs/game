using AstraKingdoms.Client.Land;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Client.Tests;

/// <summary>Land presentation tickets 37-42.</summary>
public sealed class LandPresentationTests
{
    private static Territory Initial(bool full = false) => Territory.CreateInitial(full ? TerrainTemplates.FullMirrored : TerrainTemplates.PlainOnly);

    /// <summary>Takes up to <paramref name="count"/> B cells by BFS from a border cell (a stand-in for an accepted cut).</summary>
    private static List<int> BorderBite(Territory t, int count, out int anchor)
    {
        anchor = Board.ActiveCellIds.First(id => t.IsBorderAnchor(id, PlayerSide.A));
        var taken = new List<int>();
        var seen = new HashSet<int> { anchor };
        var q = new Queue<int>(new[] { anchor });
        while (q.Count > 0 && taken.Count < count)
        {
            int id = q.Dequeue();
            taken.Add(id);
            for (int k = 0; k < 4; k++)
            {
                int nx = Board.X(id) + Board.NeighbourDx[k], ny = Board.Y(id) + Board.NeighbourDy[k];
                if (!Board.IsActive(nx, ny)) continue;
                int n = Board.CellId(nx, ny);
                if (t.IsOwnedBy(n, PlayerSide.B) && seen.Add(n)) q.Enqueue(n);
            }
        }
        return taken;
    }

    [Test]
    public void Contours_FollowOwnershipAndCannotAlterTotals()
    {
        Territory t = Initial();
        t.Transfer(BorderBite(t, 700, out _), PlayerSide.B);
        string hash = Convert.ToHexString(t.ComputeOwnershipHash());
        int a = t.CellCount(PlayerSide.A), b = t.CellCount(PlayerSide.B);
        byte[] owners = OwnershipContours.Snapshot(t);
        List<ContourSegment> contour = OwnershipContours.Build(owners);
        var overlay = new OwnershipOverlay();
        overlay.Paint(owners, patterns: true, contour);
        Assert.That(Convert.ToHexString(t.ComputeOwnershipHash()), Is.EqualTo(hash), "rendering reads a copy");
        Assert.That((t.CellCount(PlayerSide.A), t.CellCount(PlayerSide.B)), Is.EqualTo((a, b)));
        Assert.That(a + b, Is.EqualTo(RulesConstants.ActiveCells));
        Assert.That(contour, Is.Not.Empty);
        foreach (ContourSegment s in contour)
            foreach (var (x, y) in new[] { (s.X0, s.Y0), (s.X1, s.Y1) })
            {
                // Every endpoint is the midpoint between two lattice cells on opposite sides of the A boundary.
                bool horizontal = Math.Abs(x - Math.Floor(x)) < 1e-9; // x on a cell edge between centres
                int x0 = horizontal ? (int)x - 1 : (int)Math.Floor(x - 0.5), y0 = horizontal ? (int)Math.Floor(y - 0.5) : (int)y - 1;
                int x1 = horizontal ? x0 + 1 : x0, y1 = horizontal ? y0 : y0 + 1;
                Assert.That(IsA(owners, x0, y0), Is.Not.EqualTo(IsA(owners, x1, y1)), "contour point " + x + "," + y + " separates A from not-A");
            }
        Assert.That(overlay.Ink.Count(i => i == (byte)OverlayInk.Contour), Is.GreaterThan(0));
    }

    private static bool IsA(byte[] owners, int x, int y) => Board.IsOnGrid(x, y) && owners[Board.CellId(x, y)] == (byte)PlayerSide.A;

    [Test]
    public void OwnershipPatterns_DistinguishOwnersWithoutColour()
    {
        for (int bx = 0; bx < 4; bx++)
            for (int by = 0; by < 4; by++)
            {
                int inkA = 0, inkB = 0, differ = 0;
                for (int y = 0; y < 12; y++)
                    for (int x = 0; x < 12; x++)
                    {
                        bool pa = OwnershipOverlay.PatternInk(PlayerSide.A, bx * 12 + x, by * 12 + y), pb = OwnershipOverlay.PatternInk(PlayerSide.B, bx * 12 + x, by * 12 + y);
                        inkA += pa ? 1 : 0;
                        inkB += pb ? 1 : 0;
                        differ += pa != pb ? 1 : 0;
                    }
                Assert.That(inkA, Is.GreaterThan(10));
                Assert.That(inkB, Is.GreaterThan(10));
                Assert.That(differ, Is.GreaterThan(20), "every 3x3-cell patch shows a different texture per owner");
            }
    }

    [TestCase(25520, 25520, 500, 500)]
    [TestCase(45936, 5104, 900, 100)]
    [TestCase(1, 51039, 0, 1000)]
    [TestCase(17013, 34027, 333, 667)]
    public void Totals_ReconcileToTheBoardAndTo100Percent(int a, int b, int pa, int pb)
    {
        LandTotals t = LandTotals.From(a, b);
        Assert.That(t.PermilleA + t.PermilleB, Is.EqualTo(1000));
        Assert.That((t.PermilleA, t.PermilleB), Is.EqualTo((pa, pb)));
        Assert.That(t.CellsA + t.CellsB, Is.EqualTo(a + b));
        Assert.That(LandTotals.PercentText(333), Is.EqualTo("33.3"));
    }

    [Test]
    public void TerrainLabels_AnchorInsideTheirRegions()
    {
        Territory t = Initial(full: true);
        List<TerrainLabel> labels = BoardLabels.TerrainRegions(t);
        Assert.That(labels, Is.Not.Empty);
        Assert.That(labels.Select(l => l.Terrain).Distinct(), Is.SupersetOf(new[] { TerrainType.Fort, TerrainType.River, TerrainType.Forest, TerrainType.Armoury }));
        foreach (TerrainLabel l in labels)
        {
            Assert.That(t.TerrainAt(l.AnchorCellId), Is.EqualTo(l.Terrain));
            Assert.That(l.CellsA + l.CellsB, Is.EqualTo(l.Cells));
        }
        Assert.That(BoardLabels.TerrainRegions(Initial()), Is.Empty, "plain land is not labelled");
    }

    [Test]
    public void CardChoices_MatchTheRuleRecord()
    {
        MatchFactory.ForAutoplay(11, out byte[] seed, out string id);
        var host = new LocalMatchHost(MatchConfig.V1Full(MatchMode.Practice), seed, id, SeatKind.Bot, SeatKind.Bot, BotDifficulty.Hard,
            MatchFactory.DeterministicRequestIds(11), new HostTimings { BotCutDelaySeconds = 1000 });
        host.Start();
        for (int i = 0; i < 400 && host.Stage != HostStage.CardAndCut; i++) host.Tick(1.0);
        Assert.That(host.Stage, Is.EqualTo(HostStage.CardAndCut), "a duel was won");
        PublicSnapshot s = host.Snapshot();
        PlayerSide loser = Board.Opponent(s.DuelWinner!.Value);
        CardChoiceExplanation x = CardChoices.Explain(s.OfferedCards, s.OfferedQuotas, s.HpDifferenceUnits, s.Cells(loser));
        Assert.That(x.Consistent, Is.True);
        Assert.That(x.Choices.Select(c => c.Card), Is.EqualTo(s.OfferedCards));
        Assert.That(x.Choices.Select(c => c.Quota), Is.EqualTo(s.OfferedQuotas));
        foreach (CardChoice c in x.Choices) Assert.That(c.Quota, Is.EqualTo(host.Engine.QuotaFor(c.Card)));
        Assert.That(x.VajraLockedByMargin, Is.EqualTo(!s.OfferedCards.Contains(CardId.Vajra) && s.HpDifferenceUnits <= RulesConstants.VajraMinExclusiveDiffUnits));
    }

    [Test]
    public void CardChoices_NameTheLimitingTerm()
    {
        var x = CardChoices.Explain(new[] { CardId.Chakra, CardId.Padma, CardId.Vajra }, new[]
        {
            LandQuota.Compute(25520, 100, 12), LandQuota.Compute(25520, 100, 10), LandQuota.Compute(25520, 100, 20),
        }, 100, 25520);
        Assert.That(x.Choices.All(c => c.LimitedBy == QuotaLimit.Floor), Is.True, "a 1 HP margin hits the 3% floor");
        var big = CardChoices.Explain(new[] { CardId.Vajra }, new[] { LandQuota.Compute(900, 9000, 20) }, 9000, 900);
        Assert.That(big.Choices[0].LimitedBy, Is.EqualTo(QuotaLimit.LoserLand));
        Assert.That(big.Choices[0].Quota, Is.EqualTo(900));
        var mid = CardChoices.Explain(new[] { CardId.Suchi }, new[] { LandQuota.Compute(25520, 5000, 18) }, 5000, 25520);
        Assert.That(mid.Choices[0].LimitedBy, Is.EqualTo(QuotaLimit.CapTimesMargin));
        var wrong = CardChoices.Explain(new[] { CardId.Suchi }, new[] { 1 }, 5000, 25520);
        Assert.That(wrong.Consistent, Is.False);
        Assert.That(wrong.Choices[0].Quota, Is.EqualTo(1), "the engine's value is shown even if it disagreed");
    }

    [Test]
    public void BadGestures_CannotExceedTheQuotaOrCorruptOwnership()
    {
        MatchFactory.ForAutoplay(5, out byte[] seed, out string id);
        var host = new LocalMatchHost(MatchConfig.Pilot(MatchMode.Practice), seed, id, SeatKind.Bot, SeatKind.Bot, BotDifficulty.Hard,
            MatchFactory.DeterministicRequestIds(5), new HostTimings { BotCutDelaySeconds = 1000 });
        host.Start();
        for (int i = 0; i < 400 && host.Stage != HostStage.CardAndCut; i++) host.Tick(1.0);
        Assert.That(host.Stage, Is.EqualTo(HostStage.CardAndCut));
        PlayerSide winner = host.StageSide!.Value;
        PublicSnapshot snap = host.Snapshot();
        Territory before = host.CloneTerritory();
        string hash = Convert.ToHexString(before.ComputeOwnershipHash());
        var rng = new Random(7);
        var gesture = new CutGesture();
        int accepted = 0;
        for (int trial = 0; trial < 60; trial++)
        {
            // Random scribbles, huge loops, points off the board, single taps.
            gesture.Begin(rng.NextDouble(), rng.NextDouble());
            int n = rng.Next(0, 400);
            for (int i = 0; i < n; i++) gesture.Add(rng.NextDouble() * 1.4 - 0.2, rng.NextDouble() * 1.4 - 0.2);
            gesture.End(rng.NextDouble(), rng.NextDouble());
            List<CellPoint> poly = gesture.Polygon(out GestureHint hint);
            Assert.That(poly.Count, Is.LessThanOrEqualTo(RulesConstants.MaxCutVertices));
            if (hint == GestureHint.TooShort) Assert.That(poly.Count, Is.LessThan(3));
            int card = rng.Next(snap.OfferedCards.Count);
            CardPose pose = new CardPose(rng.Next(256), rng.Next(256), rng.Next(1, 300), rng.Next(16));
            int anchor = CutAssist.PickAnchor(before, winner, snap.OfferedCards[card], pose, poly.Count >= 3 ? poly : null);
            CutResult r = host.PreviewCut(winner, snap.OfferedCards[card], pose, anchor, CutMode.Manual, poly);
            if (anchor < 0)
            {
                Assert.That(r, Is.Null, "no border cell inside the card: nothing to preview");
                continue;
            }
            Assert.That(r, Is.Not.Null);
            if (r.IsAccepted)
            {
                accepted++;
                Assert.That(r.Cells.Count, Is.LessThanOrEqualTo(snap.OfferedQuotas[card]), "never over the whole-board quota");
            }
            Assert.That(Convert.ToHexString(host.CloneTerritory().ComputeOwnershipHash()), Is.EqualTo(hash), "a preview never changes ownership");
        }
        Assert.That(host.Stage, Is.EqualTo(HostStage.CardAndCut));
        Assert.That(CutGesture.HintKey(GestureHint.ClosedAutomatically), Is.EqualTo("land.gesture.ClosedAutomatically"));
        TestContext.WriteLine("accepted random gestures: " + accepted);
    }

    [Test]
    public void Transfer_FinishesAtTheActualUpdatedOwnership()
    {
        Territory t = Initial();
        byte[] before = OwnershipContours.Snapshot(t);
        List<int> bite = BorderBite(t, 900, out int anchor);
        t.Transfer(bite, PlayerSide.B);
        byte[] after = OwnershipContours.Snapshot(t);
        var plan = new LandTransferPlan(before, after, PlayerSide.A, anchor);
        Assert.That(plan.Cells, Has.Count.EqualTo(bite.Count));
        Assert.That(plan.Cells[0], Is.EqualTo(anchor), "the wave starts at the anchor");
        Assert.That(plan.Frame(1.0), Is.EqualTo(after), "the animation ends exactly at the engine's ownership");
        Assert.That(plan.Frame(0.0), Is.EqualTo(before));
        int prev = -1;
        for (double p = 0; p <= 1.0001; p += 0.05)
        {
            int n = plan.RevealedCount(p);
            Assert.That(n, Is.GreaterThanOrEqualTo(prev));
            prev = n;
        }
        Assert.That(before[plan.JumpFromCellId], Is.EqualTo((byte)PlayerSide.A), "the warrior jumps from the cutter's land");
        Assert.That(bite, Does.Contain(plan.JumpToCellId), "and lands inside the captured land");
        plan.Warrior(1.0, out _, out _, out double h);
        Assert.That(h, Is.EqualTo(0).Within(1e-9));
        var none = new LandTransferPlan(before, before, PlayerSide.A, anchor);
        Assert.That(none.Cells, Is.Empty, "a timed-out cut moves nothing");
    }

    [Test]
    public void Result_SummaryAndRematchHappenOnce()
    {
        var r = new MatchResult(TerminalReason.Territory90, PlayerSide.B, null, 5000, 46040, 6);
        ResultSummary s = ResultSummary.From(r, side => side.ToString());
        Assert.That(s.Title.Key, Is.EqualTo("result.winner"));
        Assert.That(s.Title.Args, Is.EqualTo(new object[] { "B" }));
        Assert.That(s.Reason.Key, Is.EqualTo("reason.Territory90"));
        Assert.That(s.Totals.PermilleA + s.Totals.PermilleB, Is.EqualTo(1000));
        var f = ResultSummary.From(new MatchResult(TerminalReason.Forfeit, PlayerSide.A, PlayerSide.B, 25520, 25520, 3), side => side.ToString());
        Assert.That((f.Reason.Key, f.Reason.Args[0]), Is.EqualTo(("reason.Forfeit", (object)"B")));
        Assert.That(ResultSummary.From(new MatchResult(TerminalReason.RoundsComplete, null, null, 25520, 25520, 8), side => "").Title.Key, Is.EqualTo("result.draw"));

        var guard = new RematchGuard();
        Assert.That(guard.TryRematch(), Is.False, "nothing finished yet");
        guard.MatchFinished("m1");
        Assert.That(guard.TryRematch(), Is.True);
        Assert.That(guard.TryRematch(), Is.False, "a double tap creates one rematch");
        guard.MatchFinished("m2");
        Assert.That(guard.TryRematch(), Is.True);
    }
}
