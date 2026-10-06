using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Tests.Land;

[TestFixture]
public class ConservationTests
{
    [Test]
    public void RandomAutoCuts_PreserveAllInvariants()
    {
        var rng = new TestRng(2026);
        var template = TerrainTemplates.FullMirrored;
        var t = Territory.CreateInitial(template);
        int applied = 0;

        for (int step = 0; step < 120; step++)
        {
            var winner = (PlayerSide)(rng.Next() & 1);
            var loser = Board.Opponent(winner);
            if (t.CellCount(loser) == 0) break;

            CardId card = Cards.All[rng.Range(0, 5)];
            int diff = rng.Range(1, 10000);
            if (card == CardId.Vajra && diff <= 6000) diff = 6001;
            int quota = LandQuota.Compute(t.CellCount(loser), diff, card);

            List<int> frontier = Frontier.Cells(t, winner);
            var anchor = CellPoint.FromCellId(frontier[rng.Range(0, frontier.Count - 1)]);
            int rotation = rng.Range(0, 15);
            int scale = rng.Range(1, 160);
            if (!CardEnvelope.FitsAllowance(card, scale, rotation, quota)) scale = 1;
            var pose = new CardPose(anchor.X, anchor.Y, scale, rotation);

            int winnerBefore = t.CellCount(winner);
            CutResult cut = CutValidator.AutoCut(t, winner, card, pose, anchor, quota);
            Assert.That(cut.IsAccepted, Is.True, cut.ToString());
            Assert.That(cut.Cells.Count, Is.InRange(1, quota));

            TransferOutcome outcome = LandTransfer.Apply(t, cut);
            Assert.That(outcome.Applied, Is.True);
            Assert.That(outcome.WinnerCells, Is.EqualTo(winnerBefore + cut.Cells.Count));
            Assert.That(outcome.WinnerCells + outcome.LoserCells, Is.EqualTo(51040));
            Assert.That(t.FindInvariantViolation(), Is.Null);
            applied++;
            if (outcome.WinnerReachedVictory) break;
        }

        Assert.That(applied, Is.GreaterThan(10));
        foreach (int cell in Board.ActiveCellIds) Assert.That(t.TerrainAt(cell), Is.EqualTo(template[cell]));
    }

    [Test]
    public void RandomManualRectangles_NeverTransferMoreThanQuotaOrForeignCells()
    {
        var rng = new TestRng(77);
        var t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        int accepted = 0;

        for (int step = 0; step < 300; step++)
        {
            var winner = (PlayerSide)(rng.Next() & 1);
            var loser = Board.Opponent(winner);
            int quota = LandQuota.Compute(t.CellCount(loser), rng.Range(1, 10000), CardId.Garuda);
            List<int> frontier = Frontier.Cells(t, winner);
            var anchor = CellPoint.FromCellId(frontier[rng.Range(0, frontier.Count - 1)]);
            var pose = new CardPose(anchor.X, anchor.Y, rng.Range(4, 120), rng.Range(0, 15));

            int x0 = Math.Max(0, anchor.X - rng.Range(0, 30)), x1 = Math.Min(255, anchor.X + rng.Range(1, 30));
            int y0 = Math.Max(0, anchor.Y - rng.Range(0, 30)), y1 = Math.Min(255, anchor.Y + rng.Range(1, 30));
            CellPoint[] rect = { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

            var before = t.Clone();
            CutResult cut = CutValidator.Validate(t, winner, CardId.Garuda, pose, anchor, rect, quota);
            TransferOutcome outcome = LandTransfer.Apply(t, cut);
            if (cut.IsAccepted)
            {
                accepted++;
                Assert.That(outcome.Applied, Is.True);
                Assert.That(cut.Cells.Count, Is.InRange(1, quota));
                Assert.That(cut.Cells.All(c => before.IsOwnedBy(c, loser)), Is.True);
                Assert.That(cut.Cells, Does.Contain(anchor.CellId));
            }
            else
            {
                Assert.That(outcome.Applied, Is.False);
                Assert.That(t.ComputeOwnershipHash(), Is.EqualTo(before.ComputeOwnershipHash()));
            }
            Assert.That(t.FindInvariantViolation(), Is.Null);
        }
        Assert.That(accepted, Is.GreaterThan(50));
    }
}
