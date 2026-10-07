using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Tests.Land;

[TestFixture]
public class BoardAndTerritoryTests
{
    [Test]
    public void ExactBoard_Has51040ActiveCells()
    {
        int count = 0;
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                if (Board.ComputeActive(x, y)) count++;
        Assert.That(count, Is.EqualTo(51040));
        Assert.That(Board.ActiveCellIds.Count, Is.EqualTo(51040));
        Assert.That(Board.ActiveCellIds, Is.Ordered.Ascending);
    }

    [Test]
    public void BoardMask_IsMirrorSymmetricAndCellIdsAreRowMajor()
    {
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                Assert.That(Board.IsActive(x, y), Is.EqualTo(Board.IsActive(255 - x, y)));
        Assert.That(Board.CellId(3, 2), Is.EqualTo(2 * 256 + 3));
        Assert.That(Board.X(515), Is.EqualTo(3));
        Assert.That(Board.Y(515), Is.EqualTo(2));
        Assert.That(Board.IsActive(-1, 128), Is.False);
        Assert.That(Board.IsActive(0, 0), Is.False);
        Assert.That(Board.IsActive(127, 127), Is.True);
    }

    [Test]
    public void InitialTerritory_SplitsEvenly_NoUnownedActiveCell()
    {
        var t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        Assert.That(t.CellCount(PlayerSide.A), Is.EqualTo(25520));
        Assert.That(t.CellCount(PlayerSide.B), Is.EqualTo(25520));
        Assert.That(t.FindInvariantViolation(), Is.Null);
        Assert.That(t.Revision, Is.EqualTo(0));
        foreach (int cell in Board.ActiveCellIds)
        {
            var expected = Board.X(cell) < 128 ? PlayerSide.A : PlayerSide.B;
            Assert.That(t.OwnerOf(cell), Is.EqualTo(expected));
        }
    }

    [Test]
    public void Transfer_MovesCellsAndBumpsRevision()
    {
        var t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        int[] cells = { Board.CellId(128, 100), Board.CellId(129, 100) };
        t.Transfer(cells, PlayerSide.B);
        Assert.That(t.CellCount(PlayerSide.A), Is.EqualTo(25522));
        Assert.That(t.CellCount(PlayerSide.B), Is.EqualTo(25518));
        Assert.That(t.OwnerOf(cells[0]), Is.EqualTo(PlayerSide.A));
        Assert.That(t.Revision, Is.EqualTo(1));
        t.CheckInvariants();
    }

    [Test]
    public void Transfer_IsAtomic_WhenAnyCellInvalid()
    {
        var t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        byte[] before = t.ComputeOwnershipHash();
        // Second cell belongs to A, so moving "from B" must fail without touching the first.
        int[] mixed = { Board.CellId(130, 100), Board.CellId(100, 100) };
        Assert.Throws<ArgumentException>(() => t.Transfer(mixed, PlayerSide.B));
        Assert.Throws<ArgumentException>(() => t.Transfer(new[] { Board.CellId(130, 100), Board.CellId(130, 100) }, PlayerSide.B));
        Assert.Throws<ArgumentException>(() => t.Transfer(new[] { Board.CellId(0, 0) }, PlayerSide.B));
        Assert.That(t.ComputeOwnershipHash(), Is.EqualTo(before));
        Assert.That(t.Revision, Is.EqualTo(0));
        Assert.That(t.CellCount(PlayerSide.B), Is.EqualTo(25520));
    }

    [Test]
    public void Clone_IsIndependent()
    {
        var t = Territory.CreateInitial(TerrainTemplates.FullMirrored);
        var c = t.Clone();
        c.Transfer(new[] { Board.CellId(128, 128) }, PlayerSide.B);
        Assert.That(t.CellCount(PlayerSide.B), Is.EqualTo(25520));
        Assert.That(c.CellCount(PlayerSide.B), Is.EqualTo(25519));
        Assert.That(c.Revision, Is.EqualTo(t.Revision + 1));
        Assert.That(c.TerrainAt(Board.CellId(128, 128)), Is.EqualTo(t.TerrainAt(Board.CellId(128, 128))));
    }

    [Test]
    public void BorderAnchor_RequiresFullEdge()
    {
        var t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        Assert.That(t.IsBorderAnchor(Board.CellId(128, 100), PlayerSide.A), Is.True);
        Assert.That(t.IsBorderAnchor(Board.CellId(129, 100), PlayerSide.A), Is.False);
        Assert.That(t.IsBorderAnchor(Board.CellId(127, 100), PlayerSide.A), Is.False, "own cell");
        Assert.That(t.IsBorderAnchor(Board.CellId(127, 100), PlayerSide.B), Is.True);

        // Give A an isolated cell at (139,99): (140,100) then touches A only diagonally.
        t.Transfer(new[] { Board.CellId(139, 99) }, PlayerSide.B);
        Assert.That(t.IsBorderAnchor(Board.CellId(140, 100), PlayerSide.A), Is.False);
        Assert.That(t.IsBorderAnchor(Board.CellId(140, 99), PlayerSide.A), Is.True);
    }

    [Test]
    public void OwnershipHash_ChangesWithOwnership()
    {
        var t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        byte[] h0 = t.ComputeOwnershipHash();
        t.Transfer(new[] { Board.CellId(128, 128) }, PlayerSide.B);
        Assert.That(t.ComputeOwnershipHash(), Is.Not.EqualTo(h0));
    }
}
