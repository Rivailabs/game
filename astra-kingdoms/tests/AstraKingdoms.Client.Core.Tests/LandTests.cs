using AstraKingdoms.Client.Land;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Client.Tests;

public sealed class LandTests
{
    [Test]
    public void SimplifierCapsAt128AndKeepsShortStrokes()
    {
        var rng = new Random(3);
        for (int trial = 0; trial < 20; trial++)
        {
            var stroke = new List<CellPoint>();
            int n = 130 + trial * 97;
            for (int i = 0; i < n; i++)
            {
                double a = i * 2 * Math.PI / n;
                double r = 40 + rng.Next(0, 12);
                stroke.Add(new CellPoint(128 + (int)(r * Math.Cos(a)), 128 + (int)(r * Math.Sin(a))));
            }
            List<CellPoint> s = StrokeSimplifier.Simplify(stroke);
            Assert.That(s.Count, Is.InRange(3, RulesConstants.MaxCutVertices));
            for (int i = 1; i < s.Count; i++) Assert.That(s[i], Is.Not.EqualTo(s[i - 1]));
        }
        var tri = new List<CellPoint> { new(1, 1), new(1, 1), new(10, 1), new(5, 9), new(1, 1) };
        Assert.That(StrokeSimplifier.Simplify(tri), Is.EqualTo(new[] { new CellPoint(1, 1), new CellPoint(10, 1), new CellPoint(5, 9) }));
    }

    [Test]
    public void SimplifiedRingIsStillALegalPolygon()
    {
        var stroke = new List<CellPoint>();
        for (int i = 0; i < 1000; i++)
        {
            double a = i * 2 * Math.PI / 1000;
            stroke.Add(new CellPoint(150 + (int)Math.Round(30 * Math.Cos(a)), 120 + (int)Math.Round(30 * Math.Sin(a))));
        }
        List<CellPoint> s = StrokeSimplifier.Simplify(stroke);
        Assert.That(CutPolygon.TryCreate(s, out _), Is.EqualTo(CutRejection.None));
    }

    [Test]
    public void SnappingUsesCellCentresAndTiesDown()
    {
        Assert.That(BoardMapping.Snap(0, 0), Is.EqualTo(new CellPoint(0, 0)));
        Assert.That(BoardMapping.Snap(1, 1), Is.EqualTo(new CellPoint(255, 255)));
        // Centre of cell 10 is at u = 10.5/256.
        Assert.That(BoardMapping.Snap(10.5 / 256, 20.5 / 256), Is.EqualTo(new CellPoint(10, 20)));
        // Exactly on the boundary between cells 10 and 11 (u = 11/256): ties choose the lower index.
        Assert.That(BoardMapping.Snap(11.0 / 256, 11.0 / 256), Is.EqualTo(new CellPoint(10, 10)));
    }

    [Test]
    public void RotationFromDegreesWraps()
    {
        Assert.That(BoardMapping.RotationFromDegrees(0), Is.EqualTo(0));
        Assert.That(BoardMapping.RotationFromDegrees(22.5), Is.EqualTo(1));
        Assert.That(BoardMapping.RotationFromDegrees(-22.5), Is.EqualTo(15));
        Assert.That(BoardMapping.RotationFromDegrees(360), Is.EqualTo(0));
    }

    [Test]
    public void RasterPixelsEqualExactCellCounts()
    {
        Territory t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        var raster = new BoardRaster();
        raster.PaintOwnership(t);
        Assert.That(raster.Count(CellPaint.OwnerA), Is.EqualTo(t.CellCount(PlayerSide.A)));
        Assert.That(raster.Count(CellPaint.OwnerB), Is.EqualTo(t.CellCount(PlayerSide.B)));
        Assert.That(raster.Count(CellPaint.Outside), Is.EqualTo(Board.GridCellCount - RulesConstants.ActiveCells));
        Assert.That(raster.At(1, 128), Is.EqualTo(CellPaint.OwnerA));
        Assert.That(raster.At(0, 128), Is.EqualTo(CellPaint.Outside), "(0,128) is just outside the circle");
        Assert.That(raster.At(254, 128), Is.EqualTo(CellPaint.OwnerB));
    }

    [Test]
    public void EnvelopeOverlayMarksExactlyTheRasterizedCells()
    {
        Territory t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        var raster = new BoardRaster();
        raster.PaintOwnership(t);
        var pose = new CardPose(128, 128, 40, 0);
        List<int> cells = CardEnvelope.RasterizeOnBoard(CardId.Chakra, pose);
        raster.OverlayEnvelope(cells);
        Assert.That(raster.Count(CellPaint.EnvelopeA) + raster.Count(CellPaint.EnvelopeB), Is.EqualTo(cells.Count));
        Assert.That(raster.Count(CellPaint.OwnerA) + raster.Count(CellPaint.EnvelopeA), Is.EqualTo(t.CellCount(PlayerSide.A)));
    }

    [Test]
    public void AnchorPickerFindsABorderCellInsideTheEnvelope()
    {
        Territory t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        var pose = new CardPose(130, 128, 40, 0);
        int anchor = CutAssist.PickAnchor(t, PlayerSide.A, CardId.Chakra, pose, null);
        Assert.That(anchor, Is.GreaterThanOrEqualTo(0));
        Assert.That(t.IsBorderAnchor(anchor, PlayerSide.A), Is.True);
        Assert.That(CardEnvelope.Contains(CardId.Chakra, pose, anchor), Is.True);
        Assert.That(Board.X(anchor), Is.EqualTo(128), "B's border column next to A's x = 127");
    }

    [Test]
    public void DefaultPoseFitsTheAllowanceAndAutoCutIsLegal()
    {
        Territory t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        int quota = LandQuota.Compute(t.CellCount(PlayerSide.B), 4000, CardId.Suchi);
        Assert.That(CutAssist.DefaultPose(t, PlayerSide.A, CardId.Suchi, quota, Board.CellId(128, 100), out CardPose pose), Is.True);
        Assert.That(CardEnvelope.FitsAllowance(CardId.Suchi, pose.ScaleQuarters, pose.Rotation, quota), Is.True);
        int anchor = CutAssist.PickAnchor(t, PlayerSide.A, CardId.Suchi, pose, null);
        CutResult r = CutValidator.AutoCut(t, PlayerSide.A, CardId.Suchi, pose, CellPoint.FromCellId(anchor), quota);
        Assert.That(r.IsAccepted, Is.True, r.ToString());
        Assert.That(r.Cells.Count, Is.InRange(1, quota));
        Assert.That(CutAssist.ClampScale(CardId.Suchi, 0, quota, 100000), Is.LessThanOrEqualTo(RulesConstants.MaxScaleQuarters));
    }

    [Test]
    public void PolylineOverlayDrawsClosedOutline()
    {
        var raster = new BoardRaster();
        raster.PaintOwnership(Territory.CreateInitial(TerrainTemplates.PlainOnly));
        raster.OverlayPolyline(new[] { new CellPoint(100, 100), new CellPoint(110, 100), new CellPoint(110, 110) }, closed: true);
        Assert.That(raster.At(105, 100), Is.EqualTo(CellPaint.Stroke));
        Assert.That(raster.At(105, 105), Is.EqualTo(CellPaint.Stroke), "closing edge from (110,110) back to (100,100)");
    }
}
