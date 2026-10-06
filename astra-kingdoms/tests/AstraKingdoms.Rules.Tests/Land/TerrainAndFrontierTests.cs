using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Tests.Land;

[TestFixture]
public class TerrainAndFrontierTests
{
    private static readonly byte[] ZeroSeed = new byte[32];

    [Test]
    public void PlainTemplate_IsAllPlainAndValid()
    {
        var plain = TerrainTemplates.PlainOnly;
        Assert.That(plain.Count(TerrainType.Plain), Is.EqualTo(51040));
        Assert.That(TerrainTemplates.Validate(plain, requireFullCounts: false), Is.Empty);
        Assert.That(TerrainTemplates.Validate(plain, requireFullCounts: true), Is.Not.Empty);
        Assert.That(TerrainTemplates.ForCatalog(CatalogPreset.Starter), Is.SameAs(plain));
    }

    [Test]
    public void FullTemplate_HasExactCountsPerBoardAndHalf()
    {
        var full = TerrainTemplates.FullMirrored;
        Assert.That(TerrainTemplates.Validate(full, requireFullCounts: true), Is.Empty);
        Assert.That(full.Count(TerrainType.Plain), Is.EqualTo(30624));
        foreach (var t in new[] { TerrainType.Fort, TerrainType.River, TerrainType.Forest, TerrainType.Armoury })
        {
            Assert.That(full.Count(t), Is.EqualTo(5104), t.ToString());
            Assert.That(full.CountInStartingHalf(PlayerSide.A, t), Is.EqualTo(2552), t.ToString());
            Assert.That(full.CountInStartingHalf(PlayerSide.B, t), Is.EqualTo(2552), t.ToString());
        }
        Assert.That(full.CountInStartingHalf(PlayerSide.A, TerrainType.Plain), Is.EqualTo(15312));
        Assert.That(TerrainTemplates.ForCatalog(CatalogPreset.Full), Is.SameAs(full));
    }

    [Test]
    public void FullTemplate_IsMirroredAndDeterministic()
    {
        var full = TerrainTemplates.FullMirrored;
        foreach (int cell in Board.ActiveCellIds)
        {
            int x = Board.X(cell), y = Board.Y(cell);
            Assert.That(full.At(x, y), Is.EqualTo(full.At(255 - x, y)));
        }
        // Inactive cells hold Plain.
        Assert.That(full.At(0, 0), Is.EqualTo(TerrainType.Plain));
    }

    [Test]
    public void FullTemplate_InitialFrontierOffersEverySpecialTerrain()
    {
        var territory = Territory.CreateInitial(TerrainTemplates.FullMirrored);
        var kinds = Frontier.Cells(territory, PlayerSide.A).Select(territory.TerrainAt).Distinct().ToList();
        Assert.That(kinds, Is.EquivalentTo(new[]
        {
            TerrainType.Plain, TerrainType.Fort, TerrainType.River, TerrainType.Forest, TerrainType.Armoury,
        }));
    }

    [Test]
    public void Validator_DetectsBrokenMirrorAndCounts()
    {
        TerrainType[] cells = TerrainTemplates.FullMirrored.ToArray();
        cells[Board.CellId(10, 128)] = cells[Board.CellId(10, 128)] == TerrainType.Plain ? TerrainType.Fort : TerrainType.Plain;
        var broken = new TerrainTemplate("broken", cells);
        Assert.That(TerrainTemplates.Validate(broken, requireFullCounts: true), Is.Not.Empty);
    }

    [Test]
    public void Territory_TerrainMatchesTemplateAndSurvivesTransfers()
    {
        var full = TerrainTemplates.FullMirrored;
        var t = Territory.CreateInitial(full);
        var cells = Enumerable.Range(128, 20).Select(x => Board.CellId(x, 128)).ToArray();
        t.Transfer(cells, PlayerSide.B);
        foreach (int cell in Board.ActiveCellIds) Assert.That(t.TerrainAt(cell), Is.EqualTo(full[cell]));
        Assert.That(t.FindInvariantViolation(), Is.Null);
    }

    [Test]
    public void Frontier_InitialIsDefenderColumnAdjacentToCentreLine()
    {
        var t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        List<int> forA = Frontier.Cells(t, PlayerSide.A);
        List<int> forB = Frontier.Cells(t, PlayerSide.B);
        int column = Enumerable.Range(0, 256).Count(y => Board.IsActive(128, y));
        Assert.That(forA, Has.Count.EqualTo(column));
        Assert.That(forA.All(c => Board.X(c) == 128), Is.True);
        Assert.That(forB.All(c => Board.X(c) == 127), Is.True);
        Assert.That(forA, Is.Ordered.Ascending);
    }

    [Test]
    public void FrontierSelection_IsDeterministicAndUsesTerrainStream()
    {
        var t = Territory.CreateInitial(TerrainTemplates.FullMirrored);
        List<int> frontier = Frontier.Cells(t, PlayerSide.A);
        var s1 = Frontier.SelectDuelTerrain(t, PlayerSide.A, ZeroSeed, 1);
        var s2 = Frontier.SelectDuelTerrain(t.Clone(), PlayerSide.A, ZeroSeed, 1);
        Assert.That(s2.CellId, Is.EqualTo(s1.CellId));

        int expectedIndex = SeededStream.Terrain(ZeroSeed, 1).NextIndex(frontier.Count);
        Assert.That(s1.CellId, Is.EqualTo(frontier[expectedIndex]));
        Assert.That(s1.Terrain, Is.EqualTo(t.TerrainAt(s1.CellId)));
        Assert.That(s1.FrontierCount, Is.EqualTo(frontier.Count));
        Assert.That(s1.StreamCounter, Is.GreaterThanOrEqualTo(1u));
        Assert.That(t.IsOwnedBy(s1.CellId, PlayerSide.B), Is.True);
    }

    [Test]
    public void FrontierSelection_FollowsOwnershipChanges()
    {
        var t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        var before = Frontier.Cells(t, PlayerSide.A);
        t.Transfer(new[] { Board.CellId(128, 100), Board.CellId(129, 100) }, PlayerSide.B);
        var after = Frontier.Cells(t, PlayerSide.A);
        Assert.That(after, Does.Not.Contain(Board.CellId(128, 100)));
        Assert.That(after, Does.Contain(Board.CellId(130, 100)));
        Assert.That(after, Does.Contain(Board.CellId(129, 99)));
        Assert.That(after, Is.Not.EqualTo(before));
    }

    [Test]
    public void FrontierSelection_RejectsRoundOutsideOneToEight()
    {
        var t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
        Assert.Throws<ArgumentOutOfRangeException>(() => Frontier.SelectDuelTerrain(t, PlayerSide.A, ZeroSeed, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Frontier.SelectDuelTerrain(t, PlayerSide.A, ZeroSeed, 9));
    }
}
