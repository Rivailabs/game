using AstraKingdoms.Modes.FourPlayer;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Modes.Tests.FourPlayer;

/// <summary>Plan row "Start" and "Land changes"/"Geometry": four equal sectors and four-owner card fixtures.</summary>
public class FourOwnerBoardTests
{
    [Test]
    public void RulesIdentity_IsSeparateFromTheTwoPlayerRuleset()
    {
        Assert.That(FourPlayerRules.RulesId, Is.EqualTo("AK-4P-0-proposed"));
        Assert.That(FourPlayerRules.RulesId, Is.Not.EqualTo(RulesConstants.RulesVersion));
        Assert.That(FourPlayerRules.QueueId, Does.Contain(FourPlayerRules.RulesId));
        Assert.That(FourPlayerRules.Hash, Is.Not.EqualTo(RulesBundle.Hash));
        Assert.That(FourPlayerRules.HashHex, Has.Length.EqualTo(64));
        // A two-player match cannot be configured with the four-player rules ID.
        Assert.Throws<RulesViolationException>(() =>
            new MatchConfig(MatchMode.Online, CatalogPreset.Full, CardOfferRule.V1, TerrainTemplates.FullId, false, FourPlayerRules.RulesId));
        Assert.That(FourPlayerRules.TransferCapCells, Is.EqualTo(2552));
        Assert.That(FourPlayerRules.InitialCellsPerKingdom, Is.EqualTo(12760));
    }

    [Test]
    public void EqualSectors_EachKingdomOwnsAQuarter()
    {
        FourOwnerTerritory t = FourOwnerTerritory.CreateEqualSectors();
        foreach (Kingdom k in FourKit.All) Assert.That(t.CellCount(k), Is.EqualTo(12760), k.ToString());
        Assert.That(t.NeutralCellCount, Is.Zero);
        Assert.That(t.FindInvariantViolation(), Is.Null);
        // Each quarter is the mirror image of its neighbours.
        foreach (int id in Board.ActiveCellIds)
        {
            int x = Board.X(id), y = Board.Y(id);
            Kingdom k = t.OwnerOf(id)!.Value;
            Assert.That(t.OwnerOf(Board.CellId(Board.MirrorX(x), y)), Is.EqualTo(Mirror(k, true, false)));
        }
    }

    private static Kingdom Mirror(Kingdom k, bool horizontal, bool vertical)
    {
        bool east = k == Kingdom.B || k == Kingdom.C, south = k == Kingdom.C || k == Kingdom.D;
        if (horizontal) east = !east;
        if (vertical) south = !south;
        return !south ? (east ? Kingdom.B : Kingdom.A) : (east ? Kingdom.C : Kingdom.D);
    }

    [TestCase(Kingdom.A, Kingdom.B, true)]
    [TestCase(Kingdom.B, Kingdom.C, true)]
    [TestCase(Kingdom.C, Kingdom.D, true)]
    [TestCase(Kingdom.D, Kingdom.A, true)]
    [TestCase(Kingdom.A, Kingdom.C, false)]
    [TestCase(Kingdom.B, Kingdom.D, false)]
    public void SectorAdjacency_DiagonalPairsShareNoBorder(Kingdom p, Kingdom q, bool shareEdge)
    {
        FourOwnerTerritory t = FourOwnerTerritory.CreateEqualSectors();
        bool found = false;
        foreach (int id in Board.ActiveCellIds)
        {
            if (!t.IsOwnedBy(id, p)) continue;
            int x = Board.X(id), y = Board.Y(id);
            for (int d = 0; d < 4 && !found; d++)
                if (t.IsOwnedBy(x + Board.NeighbourDx[d], y + Board.NeighbourDy[d], q)) found = true;
            if (found) break;
        }
        Assert.That(found, Is.EqualTo(shareEdge));
    }

    [Test]
    public void ApplyTransfers_IsAtomicAndRejectsOverlap()
    {
        FourOwnerTerritory t = FourOwnerTerritory.CreateEqualSectors();
        int a = FourKit.Interior(Kingdom.A), c = FourKit.Interior(Kingdom.C);
        byte[] before = t.ComputeOwnershipHash();
        // Second transfer reuses a cell of the first: nothing may change.
        Assert.Throws<ArgumentException>(() => t.ApplyTransfers(new[]
        {
            new CellTransfer(Kingdom.A, Kingdom.B, new[] { a }),
            new CellTransfer(Kingdom.A, Kingdom.D, new[] { a }),
        }));
        // Wrong source owner: nothing may change.
        Assert.Throws<ArgumentException>(() => t.ApplyTransfers(new[]
        {
            new CellTransfer(Kingdom.C, Kingdom.D, new[] { c }),
            new CellTransfer(Kingdom.B, Kingdom.D, new[] { a }),
        }));
        Assert.That(t.ComputeOwnershipHash(), Is.EqualTo(before));
        Assert.That(t.Revision, Is.Zero);

        t.ApplyTransfers(new[] { new CellTransfer(Kingdom.A, Kingdom.B, new[] { a }), new CellTransfer(Kingdom.C, Kingdom.D, new[] { c }) });
        Assert.That(t.Revision, Is.EqualTo(1), "one batch, one revision");
        Assert.That(t.OwnerOf(a), Is.EqualTo(Kingdom.B));
        Assert.That(t.OwnerOf(c), Is.EqualTo(Kingdom.D));
        Assert.That(t.CellCount(Kingdom.A), Is.EqualTo(12759));
        Assert.That(t.FindInvariantViolation(), Is.Null);
    }

    [Test]
    public void AreaIsConservedAcrossFourOwnersUnderRandomBatches()
    {
        FourOwnerTerritory t = FourOwnerTerritory.CreateEqualSectors();
        var rng = new Rules.Bots.BotRng(99);
        for (int batch = 0; batch < 40; batch++)
        {
            var used = new HashSet<int>();
            var transfers = new List<CellTransfer>();
            for (int j = 0; j < 3; j++)
            {
                var from = (Kingdom)rng.Next(4);
                var to = (Kingdom)((((int)from) + 1 + rng.Next(3)) % 4);
                List<int> owned = t.CellsOwnedBy(from);
                if (owned.Count == 0) continue;
                var cells = new List<int>();
                for (int n = 0; n < 50; n++)
                {
                    int cell = owned[rng.Next(owned.Count)];
                    if (used.Add(cell)) cells.Add(cell);
                }
                transfers.Add(new CellTransfer(from, to, cells));
            }
            t.ApplyTransfers(transfers);
            if (batch == 20) t.LockToNeutral((Kingdom)rng.Next(4));
            Assert.That(t.FindInvariantViolation(), Is.Null);
            Assert.That(FourKit.All.Sum(k => t.CellCount(k)) + t.NeutralCellCount, Is.EqualTo(51040));
        }
    }

    [Test]
    public void LockToNeutral_GivesTheLandToNobody()
    {
        FourOwnerTerritory t = FourOwnerTerritory.CreateEqualSectors();
        Assert.That(t.LockToNeutral(Kingdom.D), Is.EqualTo(12760));
        Assert.That(t.CellCount(Kingdom.D), Is.Zero);
        Assert.That(t.NeutralCellCount, Is.EqualTo(12760));
        foreach (Kingdom k in new[] { Kingdom.A, Kingdom.B, Kingdom.C }) Assert.That(t.CellCount(k), Is.EqualTo(12760));
        Assert.That(t.OwnerOf(FourKit.Interior(Kingdom.D)), Is.Null);
        // Neutral land can never be transferred.
        Assert.Throws<ArgumentException>(() =>
            t.ApplyTransfers(new[] { new CellTransfer(Kingdom.D, Kingdom.A, new[] { FourKit.Interior(Kingdom.D) }) }));
    }

    // ---------------------------------------------------------------- card geometry fixtures

    [Test]
    public void Allowance_IsCappedAtFivePercentAndTheLosersArea()
    {
        // A Vajra-sized win would allow 20% under AK-TR-1; the candidate caps it at 2,552.
        Assert.That(LandQuota.Compute(12760, 10000, CardId.Vajra), Is.EqualTo(10208));
        Assert.That(FourPlayerCutRules.Allowance(12760, 10000, CardId.Vajra), Is.EqualTo(2552));
        // The 3% floor (1,531) stays below the cap.
        Assert.That(FourPlayerCutRules.Allowance(12760, 1, CardId.Chakra), Is.EqualTo(1531));
        // A small loser caps the allowance at its own area.
        Assert.That(FourPlayerCutRules.Allowance(700, 10000, CardId.Suchi), Is.EqualTo(700));
        Assert.That(FourPlayerCutRules.Allowance(12760, 0, CardId.Suchi), Is.Zero, "a draw allows nothing");
    }

    [Test]
    public void DiagonalPair_WinnerCutsAPocketWithoutASharedBorder()
    {
        FourOwnerTerritory t = FourOwnerTerritory.CreateEqualSectors();
        int anchor = FourKit.Interior(Kingdom.C); // deep in C, far from A
        int quota = FourPlayerCutRules.Allowance(t.CellCount(Kingdom.C), 9000, CardId.Chakra);
        int scale = FourPlayerCutRules.LargestFittingScale(CardId.Chakra, 0, quota);
        var pose = new CardPose(Board.X(anchor), Board.Y(anchor), scale, 0);
        FourPlayerCutResult r = FourPlayerCutRules.AutoCut(t, Kingdom.A, Kingdom.C, CardId.Chakra, pose, CellPoint.FromCellId(anchor), quota);
        Assert.That(r.IsAccepted, Is.True, r.ToString());
        Assert.That(r.Cells.Count, Is.GreaterThan(0).And.LessThanOrEqualTo(quota));
        Assert.That(r.Cells.All(c => t.IsOwnedBy(c, Kingdom.C)), Is.True);
        // The captured pocket does not touch A's land anywhere.
        foreach (int c in r.Cells)
            for (int d = 0; d < 4; d++)
                Assert.That(t.IsOwnedBy(Board.X(c) + Board.NeighbourDx[d], Board.Y(c) + Board.NeighbourDy[d], Kingdom.A), Is.False);
        t.ApplyTransfers(new[] { r.ToTransfer() });
        Assert.That(t.CellCount(Kingdom.A), Is.EqualTo(12760 + r.Cells.Count));
        Assert.That(t.FindInvariantViolation(), Is.Null);
    }

    [Test]
    public void EnvelopeOverAThirdKingdom_TakesOnlyTheDefeatedOpponentsCells()
    {
        FourOwnerTerritory t = FourOwnerTerritory.CreateEqualSectors();
        // Anchor in C right at the C/B boundary (x >= 128, y = 128): the envelope spans B and C.
        var anchor = new CellPoint(180, 128);
        Assert.That(t.IsOwnedBy(anchor.CellId, Kingdom.C), Is.True);
        int quota = FourPlayerRules.TransferCapCells;
        int scale = FourPlayerCutRules.LargestFittingScale(CardId.Chakra, 0, quota);
        var pose = new CardPose(anchor.X, anchor.Y, scale, 0);
        List<int> envelope = CardEnvelope.RasterizeOnBoard(CardId.Chakra, pose);
        Assert.That(envelope.Any(c => t.IsOwnedBy(c, Kingdom.B)), Is.True, "fixture: the envelope covers B");

        FourPlayerCutResult auto = FourPlayerCutRules.AutoCut(t, Kingdom.D, Kingdom.C, CardId.Chakra, pose, anchor, quota);
        Assert.That(auto.IsAccepted, Is.True);
        Assert.That(auto.Cells.All(c => t.IsOwnedBy(c, Kingdom.C)), Is.True);

        // Manual polygon covering the same area: still C only, B cells are not even candidates.
        var square = new[] { new CellPoint(160, 100), new CellPoint(200, 100), new CellPoint(200, 150), new CellPoint(160, 150) };
        FourPlayerCutResult manual = FourPlayerCutRules.Validate(t, Kingdom.D, Kingdom.C, CardId.Chakra, pose, anchor, square, quota);
        Assert.That(manual.IsAccepted, Is.True, manual.ToString());
        Assert.That(manual.Cells.All(c => t.IsOwnedBy(c, Kingdom.C)), Is.True);
        Assert.That(manual.DiscardedCells.Any(c => !t.IsOwnedBy(c, Kingdom.C)), Is.False);
    }

    [Test]
    public void NeutralLand_IsNeverEligible()
    {
        FourOwnerTerritory t = FourOwnerTerritory.CreateEqualSectors();
        t.LockToNeutral(Kingdom.B);
        var anchor = new CellPoint(180, 128); // C, next to B's (now neutral) land
        int scale = FourPlayerCutRules.LargestFittingScale(CardId.Chakra, 0, 2552);
        var pose = new CardPose(anchor.X, anchor.Y, scale, 0);
        FourPlayerCutResult r = FourPlayerCutRules.AutoCut(t, Kingdom.A, Kingdom.C, CardId.Chakra, pose, anchor, 2552);
        Assert.That(r.Cells.Any(c => t.IsNeutral(c)), Is.False);
        // Anchoring on neutral land is rejected.
        var neutralAnchor = new CellPoint(180, 100);
        Assert.That(t.IsNeutral(neutralAnchor.CellId), Is.True);
        FourPlayerCutResult bad = FourPlayerCutRules.AutoCut(t, Kingdom.A, Kingdom.C, CardId.Chakra,
            new CardPose(180, 100, scale, 0), neutralAnchor, 2552);
        Assert.That(bad.Rejection, Is.EqualTo(CutRejection.AnchorNotOpponentOwned));
    }

    [Test]
    public void CommonChecks_KeepTheV1EnvelopeAndPoseRules()
    {
        FourOwnerTerritory t = FourOwnerTerritory.CreateEqualSectors();
        var anchor = CellPoint.FromCellId(FourKit.Interior(Kingdom.B));
        Assert.That(FourPlayerCutRules.CheckCommon(t, Kingdom.B, CardId.Suchi, new CardPose(anchor.X, anchor.Y, 40, 0), anchor, 0),
            Is.EqualTo(CutRejection.NoAllowance));
        Assert.That(FourPlayerCutRules.CheckCommon(t, Kingdom.B, CardId.Suchi, new CardPose(anchor.X, anchor.Y, 0, 0), anchor, 100),
            Is.EqualTo(CutRejection.InvalidPose));
        Assert.That(FourPlayerCutRules.CheckCommon(t, Kingdom.B, CardId.Suchi, new CardPose(anchor.X, anchor.Y, 1024, 0), anchor, 100),
            Is.EqualTo(CutRejection.EnvelopeTooLarge));
        Assert.That(FourPlayerCutRules.CheckCommon(t, Kingdom.C, CardId.Suchi, new CardPose(anchor.X, anchor.Y, 20, 0), anchor, 100),
            Is.EqualTo(CutRejection.AnchorNotOpponentOwned));
        Assert.That(FourPlayerCutRules.CheckCommon(t, Kingdom.B, CardId.Suchi, new CardPose(anchor.X - 60, anchor.Y, 20, 0), anchor, 100),
            Is.EqualTo(CutRejection.AnchorOutsideEnvelope));
        Assert.That(FourPlayerCutRules.CheckCommon(t, Kingdom.B, CardId.Suchi, new CardPose(anchor.X, anchor.Y, 20, 0), anchor, 100),
            Is.EqualTo(CutRejection.None));
    }

    [Test]
    public void ManualCut_LargerThanQuotaIsRejected()
    {
        FourOwnerTerritory t = FourOwnerTerritory.CreateEqualSectors();
        var anchor = CellPoint.FromCellId(FourKit.Interior(Kingdom.D));
        int quota = 200;
        int scale = FourPlayerCutRules.LargestFittingScale(CardId.Chakra, 0, quota);
        var pose = new CardPose(anchor.X, anchor.Y, scale, 0);
        // A polygon covering the whole envelope captures more than Q (envelope may hold up to 2Q).
        var big = new[]
        {
            new CellPoint(anchor.X - 40, anchor.Y - 40), new CellPoint(anchor.X + 40, anchor.Y - 40),
            new CellPoint(anchor.X + 40, anchor.Y + 40), new CellPoint(anchor.X - 40, anchor.Y + 40),
        };
        FourPlayerCutResult r = FourPlayerCutRules.Validate(t, Kingdom.B, Kingdom.D, CardId.Chakra, pose, anchor, big, quota);
        Assert.That(r.Rejection, Is.EqualTo(CutRejection.ExceedsQuota));
        Assert.That(r.Cells, Is.Empty);
    }
}
