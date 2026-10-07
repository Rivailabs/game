using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using static AstraKingdoms.Rules.Tests.Land.LandTestHelpers;

namespace AstraKingdoms.Rules.Tests.Land;

[TestFixture]
public class CutValidatorTests
{
    // Winner A (west), loser B (east). (128,127) is a B cell edge-adjacent to A's (127,127).
    private static readonly CellPoint Anchor = P(128, 127);

    // Chakra radius 45 cells centred inside B's half; 2Q = 7,348 comfortably holds it.
    private static readonly CardPose ChakraPose = new(150, 127, 180, 0);

    private static Territory NewTerritory() => Territory.CreateInitial(TerrainTemplates.PlainOnly);

    private static void AssertRejectedWithoutTransfer(Territory t, CutResult result, CutRejection reason)
    {
        Assert.That(result.IsAccepted, Is.False);
        Assert.That(result.Rejection, Is.EqualTo(reason));
        Assert.That(result.Cells, Is.Empty);
        byte[] before = t.ComputeOwnershipHash();
        TransferOutcome outcome = LandTransfer.Apply(t, result);
        Assert.That(outcome.Applied, Is.False);
        Assert.That(outcome.Failure, Is.EqualTo(TransferFailure.CutNotAccepted));
        Assert.That(outcome.CellsTransferred, Is.EqualTo(0));
        Assert.That(t.ComputeOwnershipHash(), Is.EqualTo(before));
        Assert.That(t.CellCount(PlayerSide.A) + t.CellCount(PlayerSide.B), Is.EqualTo(51040));
    }

    // ---------- Accepted cuts ----------

    [Test]
    public void ActualCut_TransfersExactly2100_NotQuotaOrEnvelopeArea()
    {
        var t = NewTerritory();
        int quota = LandQuota.Compute(t.CellCount(PlayerSide.B), 6000, CardId.Chakra);
        Assert.That(quota, Is.EqualTo(3674));

        // 35 x 60 cell-centre rectangle in B land touching the border column x = 128.
        CutResult cut = CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, Rect(128, 97, 162, 156), quota);
        Assert.That(cut.IsAccepted, Is.True, cut.ToString());
        Assert.That(cut.Cells, Has.Count.EqualTo(2100));
        Assert.That(cut.DiscardedCells, Is.Empty);

        TransferOutcome outcome = LandTransfer.Apply(t, cut);
        Assert.That(outcome.Applied, Is.True);
        Assert.That(outcome.CellsTransferred, Is.EqualTo(2100));
        Assert.That(outcome.WinnerCells, Is.EqualTo(25520 + 2100));
        Assert.That(outcome.LoserCells, Is.EqualTo(25520 - 2100));
        Assert.That(outcome.WinnerReachedVictory, Is.False);
        Assert.That(t.FindInvariantViolation(), Is.Null);
    }

    [Test]
    public void ShapeReachingIntoOwnLand_TransfersOnlyEligibleLoserCells()
    {
        var t = NewTerritory();
        // Same rectangle but extended 20 columns into A's land: A-owned cells are not eligible.
        CutResult cut = CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, Rect(108, 97, 162, 156), 3674);
        Assert.That(cut.IsAccepted, Is.True, cut.ToString());
        Assert.That(cut.Cells, Has.Count.EqualTo(2100));
        Assert.That(cut.Cells.All(c => Board.X(c) >= 128), Is.True);
    }

    [Test]
    public void PolygonLargerThanEnvelope_IsClippedToEnvelope()
    {
        var t = NewTerritory();
        var pose = new CardPose(136, 127, 40, 0); // radius 10 cells
        CutResult cut = CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, pose, Anchor, Rect(100, 60, 200, 200), 1531);
        Assert.That(cut.IsAccepted, Is.True, cut.ToString());
        var expected = CardEnvelope.RasterizeOnBoard(CardId.Chakra, pose).Where(c => t.IsOwnedBy(c, PlayerSide.B)).ToList();
        Assert.That(cut.Cells, Is.EqualTo(expected));
    }

    [Test]
    public void TinyEnvelopeOnAnchor_PermitsOneCellClaim()
    {
        var t = NewTerritory();
        var pose = new CardPose(Anchor.X, Anchor.Y, 1, 7);
        CutResult cut = CutValidator.Validate(t, PlayerSide.A, CardId.Suchi, pose, Anchor, new[] { P(126, 125), P(131, 127), P(126, 130) }, 1);
        Assert.That(cut.IsAccepted, Is.True, cut.ToString());
        Assert.That(cut.Cells, Is.EqualTo(new[] { Anchor.CellId }));
    }

    [Test]
    public void DisconnectedFragments_AreDiscardedForPreview()
    {
        var t = NewTerritory();
        // Give A a vertical wall at x = 140, splitting B's land inside the cut.
        var wall = Enumerable.Range(90, 71).Select(y => Board.CellId(140, y)).ToArray();
        t.Transfer(wall, PlayerSide.B);

        CutResult cut = CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, Rect(128, 100, 160, 150), 3674);
        Assert.That(cut.IsAccepted, Is.True, cut.ToString());
        Assert.That(cut.Cells, Has.Count.EqualTo(12 * 51));
        Assert.That(cut.Cells.All(c => Board.X(c) < 140), Is.True);
        Assert.That(cut.DiscardedCells, Has.Count.EqualTo(20 * 51));
        Assert.That(cut.DiscardedCells.All(c => Board.X(c) > 140), Is.True);
        Assert.That(IsFourConnected(cut.Cells), Is.True);
    }

    [Test]
    public void RepeatedClosingVertexAndDuplicates_AreNormalized()
    {
        var t = NewTerritory();
        var verts = new[] { P(128, 120), P(128, 120), P(140, 120), P(140, 130), P(140, 130), P(128, 130), P(128, 120) };
        CutResult cut = CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, verts, 3674);
        Assert.That(cut.IsAccepted, Is.True, cut.ToString());
        Assert.That(cut.Cells, Has.Count.EqualTo(13 * 11));
    }

    [Test]
    public void CellCentresOnEdges_AreIncluded()
    {
        var t = NewTerritory();
        // Right triangle with a diagonal hypotenuse through cell centres.
        var verts = new[] { P(128, 120), P(138, 130), P(128, 130) };
        CutResult cut = CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, verts, 3674);
        Assert.That(cut.IsAccepted, Is.True, cut.ToString());
        Assert.That(cut.Cells, Has.Count.EqualTo(66)); // 1 + 2 + ... + 11
        Assert.That(cut.Cells, Does.Contain(Board.CellId(133, 125)));
    }

    // ---------- Rejections ----------

    [Test]
    public void SelfCrossingStroke_IsRejected()
    {
        var t = NewTerritory();
        var bowtie = new[] { P(128, 110), P(150, 140), P(150, 110), P(128, 140) };
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, bowtie, 3674),
            CutRejection.SelfIntersecting);
    }

    [Test]
    public void NonadjacentVertexTouch_IsRejected()
    {
        var t = NewTerritory();
        // Figure-eight: vertex (140,120) is visited twice, so nonadjacent edges touch there.
        var pinch = new[] { P(128, 110), P(140, 120), P(150, 110), P(150, 130), P(140, 120), P(128, 130) };
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, pinch, 3674),
            CutRejection.SelfIntersecting);
    }

    [Test]
    public void DoublingBackEdge_IsOverlapping()
    {
        var t = NewTerritory();
        var spike = new[] { P(128, 110), P(150, 110), P(140, 110), P(140, 140), P(128, 140) };
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, spike, 3674),
            CutRejection.OverlappingEdges);
    }

    [Test]
    public void NonadjacentCollinearOverlap_IsOverlapping()
    {
        var t = NewTerritory();
        // Edges (130,110)-(130,140) and (130,135)-(130,115) run along the same column.
        var verts = new[] { P(130, 110), P(130, 140), P(145, 140), P(145, 135), P(130, 135), P(130, 115), P(145, 115), P(145, 110) };
        CutResult r = CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, verts, 3674);
        Assert.That(r.Rejection, Is.EqualTo(CutRejection.OverlappingEdges).Or.EqualTo(CutRejection.SelfIntersecting));
        AssertRejectedWithoutTransfer(t, r, r.Rejection);
    }

    [Test]
    public void MissingAnchor_IsRejected()
    {
        var t = NewTerritory();
        // Polygon away from the anchor.
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, Rect(135, 110, 150, 120), 3674),
            CutRejection.AnchorOutsidePolygon);
        // Anchor not owned by the loser.
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, P(127, 127), Rect(120, 110, 150, 140), 3674),
            CutRejection.AnchorNotOpponentOwned);
        // Anchor not touching the winner.
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, P(135, 127), Rect(128, 110, 150, 140), 3674),
            CutRejection.AnchorNotOnBorder);
        // Anchor off the active board.
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, P(255, 0), Rect(128, 110, 150, 140), 3674),
            CutRejection.AnchorNotOnBoard);
        // Envelope not containing the anchor.
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, new CardPose(170, 127, 40, 0), Anchor, Rect(128, 110, 180, 140), 3674),
            CutRejection.AnchorOutsideEnvelope);
    }

    [Test]
    public void DiagonalOnlyAnchor_IsRejected()
    {
        var t = NewTerritory();
        t.Transfer(new[] { Board.CellId(139, 99) }, PlayerSide.B); // A island touching (140,100) diagonally
        var pose = new CardPose(140, 100, 40, 0);
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, pose, P(140, 100), Rect(135, 95, 145, 105), 3674),
            CutRejection.AnchorNotOnBorder);
        // The edge-adjacent neighbour is a legal anchor.
        Assert.That(CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, pose, P(140, 99), Rect(135, 95, 145, 105), 3674).IsAccepted, Is.True);
    }

    [Test]
    public void TooManyVertices_IsRejectedWithoutResampling()
    {
        var t = NewTerritory();
        var verts = new List<CellPoint>();
        // A valid 129-vertex rectangle outline (collinear interior vertices are legal on their own).
        for (int x = 128; x <= 160; x++) verts.Add(P(x, 110));      // 33
        for (int y = 111; y <= 140; y++) verts.Add(P(160, y));      // 30
        for (int x = 159; x >= 128; x--) verts.Add(P(x, 140));      // 32
        for (int y = 139; y >= 111; y--) verts.Add(P(128, y));      // 29
        while (verts.Count < 129) verts.Add(verts[^1]);             // pad with duplicates to 129
        Assert.That(verts, Has.Count.EqualTo(129));
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, verts, 3674),
            CutRejection.TooManyVertices);

        // Exactly 128 entries is allowed.
        verts.RemoveAt(verts.Count - 1);
        Assert.That(CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, verts, 3674).IsAccepted, Is.True);
    }

    [Test]
    public void ZeroAreaPath_IsRejected()
    {
        var t = NewTerritory();
        var line = new[] { P(128, 127), P(140, 127), P(150, 127) };
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, line, 3674),
            CutRejection.ZeroArea);
    }

    [Test]
    public void TooFewDistinctVertices_IsRejected()
    {
        var t = NewTerritory();
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor,
            new[] { P(128, 127), P(140, 127), P(128, 127), P(140, 127) }, 3674), CutRejection.TooFewVertices);
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor,
            Array.Empty<CellPoint>(), 3674), CutRejection.TooFewVertices);
    }

    [Test]
    public void VertexOffGrid_IsRejected()
    {
        var t = NewTerritory();
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor,
            new[] { P(128, 127), P(256, 127), P(128, 140) }, 3674), CutRejection.VertexOutOfRange);
    }

    [Test]
    public void OverQuotaComponent_IsRejectedButPreviewed()
    {
        var t = NewTerritory();
        var pose = new CardPose(135, 127, 31, 0); // radius 7.75 cells, about 189 cells <= 2Q = 200
        CutResult r = CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, pose, Anchor, Rect(120, 100, 160, 150), 100);
        AssertRejectedWithoutTransfer(t, r, CutRejection.ExceedsQuota);
        Assert.That(r.PreviewCells.Count, Is.GreaterThan(100));
    }

    [Test]
    public void OversizedEnvelope_IsRejected()
    {
        var t = NewTerritory();
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, new CardPose(150, 127, 400, 0), Anchor, Rect(128, 110, 150, 140), 3674),
            CutRejection.EnvelopeTooLarge);
    }

    [Test]
    public void InvalidPoseOrNoAllowance_IsRejected()
    {
        var t = NewTerritory();
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, new CardPose(150, 127, 0, 0), Anchor, Rect(128, 110, 150, 140), 3674),
            CutRejection.InvalidPose);
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, new CardPose(150, 127, 40, 16), Anchor, Rect(128, 110, 150, 140), 3674),
            CutRejection.InvalidPose);
        AssertRejectedWithoutTransfer(t, CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, Rect(128, 110, 150, 140), 0),
            CutRejection.NoAllowance);
    }

    [Test]
    public void StaleCut_IsNotApplied()
    {
        var t = NewTerritory();
        CutResult cut = CutValidator.Validate(t, PlayerSide.A, CardId.Chakra, ChakraPose, Anchor, Rect(128, 110, 140, 140), 3674);
        Assert.That(cut.IsAccepted, Is.True);
        Assert.That(LandTransfer.Apply(t, cut).Applied, Is.True);
        int a = t.CellCount(PlayerSide.A);

        // Replaying the same accepted result is stale: no second mutation.
        TransferOutcome again = LandTransfer.Apply(t, cut);
        Assert.That(again.Applied, Is.False);
        Assert.That(again.Failure, Is.EqualTo(TransferFailure.StaleRevision));
        Assert.That(t.CellCount(PlayerSide.A), Is.EqualTo(a));
    }

    // ---------- Snapping ----------

    [Test]
    public void Snapping_NearestCentre_TiesChooseLower()
    {
        Assert.That(CutPolygon.SnapToCellIndex(10, 1), Is.EqualTo(10));
        Assert.That(CutPolygon.SnapToCellIndex(21, 2), Is.EqualTo(10), "10.5 ties to 10");
        Assert.That(CutPolygon.SnapToCellIndex(1050001, 100000), Is.EqualTo(11));
        Assert.That(CutPolygon.SnapToCellIndex(1049999, 100000), Is.EqualTo(10));
        Assert.That(CutPolygon.SnapToCellIndex(-1, 2), Is.EqualTo(0), "-0.5 ties to -1, clamped to 0");
        Assert.That(CutPolygon.SnapToCellIndex(1000, 1), Is.EqualTo(255));
        Assert.That(CutPolygon.SnapPoint(255, 257, 2), Is.EqualTo(P(127, 128)));
    }

    // ---------- Auto Cut ----------

    [Test]
    public void AutoCut_UsesNorthEastSouthWestBreadthFirstOrder()
    {
        var t = NewTerritory();
        // Radius-1 Chakra (5 cells) on the anchor: eligible = anchor, N, E, S (W is A-owned).
        var onAnchor = new CardPose(128, 127, 4, 0);
        Assert.That(CutValidator.AutoCut(t, PlayerSide.A, CardId.Chakra, onAnchor, Anchor, 3).Cells,
            Is.EquivalentTo(new[] { Anchor.CellId, Board.CellId(128, 126), Board.CellId(129, 127) }), "N then E before S");

        // Radius-1 Chakra on (129,127): the anchor's only eligible neighbour is East (129,127),
        // whose own neighbours are then visited N (129,126), E (130,127), S (129,128).
        var east = new CardPose(129, 127, 4, 0);
        Assert.That(CutValidator.AutoCut(t, PlayerSide.A, CardId.Chakra, east, Anchor, 3).Cells,
            Is.EquivalentTo(new[] { Anchor.CellId, Board.CellId(129, 127), Board.CellId(129, 126) }));
        Assert.That(CutValidator.AutoCut(t, PlayerSide.A, CardId.Chakra, east, Anchor, 4).Cells,
            Is.EquivalentTo(new[] { Anchor.CellId, Board.CellId(129, 127), Board.CellId(129, 126), Board.CellId(130, 127) }));

        // Q = 1 with a single-cell envelope claims just the anchor.
        Assert.That(CutValidator.AutoCut(t, PlayerSide.A, CardId.Chakra, new CardPose(128, 127, 1, 0), Anchor, 1).Cells,
            Is.EqualTo(new[] { Anchor.CellId }));
    }

    [Test]
    public void AutoCut_NeverExceedsQuota_IsConnectedAndEligible()
    {
        var t = NewTerritory();
        foreach (int quota in new[] { 1, 7, 150, 1531, 3674 })
        {
            foreach (CardId card in Cards.All)
            {
                int scale = MaxFittingScale(card, 3, quota);
                var pose = new CardPose(128, 127, scale, 3);
                CutResult r = CutValidator.AutoCut(t, PlayerSide.A, card, pose, Anchor, quota);
                if (!r.IsAccepted)
                {
                    // Only possible when the anchor is outside a very small envelope.
                    Assert.That(r.Rejection, Is.EqualTo(CutRejection.AnchorOutsideEnvelope), $"{card} Q={quota}");
                    continue;
                }
                Assert.That(r.Cells.Count, Is.LessThanOrEqualTo(quota));
                Assert.That(r.Cells, Does.Contain(Anchor.CellId));
                Assert.That(IsFourConnected(r.Cells), Is.True);
                Assert.That(r.Cells.All(c => t.IsOwnedBy(c, PlayerSide.B) && CardEnvelope.Contains(card, pose, c)), Is.True);
            }
        }
    }

    [Test]
    public void AutoCut_DoesNotPromiseToFillQuota()
    {
        var t = NewTerritory();
        var pose = new CardPose(128, 127, 8, 0); // radius 2 cells
        CutResult r = CutValidator.AutoCut(t, PlayerSide.A, CardId.Chakra, pose, Anchor, 1531);
        Assert.That(r.IsAccepted, Is.True);
        // Only the B half of a radius-2 disk centred on the border: x in {128,129,130}.
        Assert.That(r.Cells.Count, Is.EqualTo(5 + 3 + 1));
    }

    [Test]
    public void AutoCut_InvalidAnchor_IsRejected()
    {
        var t = NewTerritory();
        CutResult r = CutValidator.AutoCut(t, PlayerSide.A, CardId.Chakra, ChakraPose, P(135, 127), 3674);
        AssertRejectedWithoutTransfer(t, r, CutRejection.AnchorNotOnBorder);
    }

    // ---------- Reachable shortcut ----------

    [Test]
    public void ReachableShortcut_TwoMaximumVajraCuts_EndAtExactly90Percent()
    {
        var t = NewTerritory();
        const int maxDiff = 10000; // winner 100.00 HP, loser 0.00 HP

        // Largest Vajra scale whose canonical envelope holds at most 2Q = 20,416 cells.
        const int scale = 335;
        Assert.That(CardEnvelope.FitsAllowance(CardId.Vajra, scale, 0, 10208), Is.True);
        Assert.That(CardEnvelope.FitsAllowance(CardId.Vajra, scale + 1, 0, 10208), Is.False);

        // First cut: from the north end of the border, leaving B's remaining land in one piece.
        int q1 = LandQuota.Compute(t.CellCount(PlayerSide.B), maxDiff, CardId.Vajra);
        Assert.That(q1, Is.EqualTo(10208));
        CutResult cut1 = CutValidator.AutoCut(t, PlayerSide.A, CardId.Vajra, new CardPose(168, 59, scale, 0), P(128, 19), q1);
        TransferOutcome first = LandTransfer.Apply(t, cut1);
        Assert.That(first.Applied, Is.True, cut1.ToString());
        Assert.That(first.CellsTransferred, Is.EqualTo(10208));
        Assert.That(first.WinnerCells, Is.EqualTo(35728));
        Assert.That(first.WinnerReachedVictory, Is.False);

        // Second cut: anchored on the new border created by the first cut.
        int q2 = LandQuota.Compute(t.CellCount(PlayerSide.B), maxDiff, CardId.Vajra);
        Assert.That(q2, Is.EqualTo(10208));
        CutResult cut2 = CutValidator.AutoCut(t, PlayerSide.A, CardId.Vajra, new CardPose(204, 146, scale, 0), P(204, 106), q2);
        TransferOutcome second = LandTransfer.Apply(t, cut2);
        Assert.That(second.Applied, Is.True, cut2.ToString());
        Assert.That(second.CellsTransferred, Is.EqualTo(10208));
        Assert.That(second.WinnerCells, Is.EqualTo(45936));
        Assert.That(second.LoserCells, Is.EqualTo(5104));
        Assert.That(second.WinnerReachedVictory, Is.True);
        Assert.That(t.HasReachedVictory(PlayerSide.A), Is.True);
        Assert.That(t.FindInvariantViolation(), Is.Null);
    }

    [Test]
    public void OneCellShortOfShortcut_IsNotVictory()
    {
        var t = NewTerritory();
        var cells = t.CellsOwnedBy(PlayerSide.B).Take(45936 - 25520 - 1).ToList();
        t.Transfer(cells, PlayerSide.B);
        Assert.That(t.CellCount(PlayerSide.A), Is.EqualTo(45935));
        Assert.That(t.HasReachedVictory(PlayerSide.A), Is.False);
        t.Transfer(new[] { t.CellsOwnedBy(PlayerSide.B)[0] }, PlayerSide.B);
        Assert.That(t.HasReachedVictory(PlayerSide.A), Is.True);
    }
}
