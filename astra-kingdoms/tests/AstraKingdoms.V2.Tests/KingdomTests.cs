using System.Reflection;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.V2.Common;
using AstraKingdoms.V2.Integration;
using AstraKingdoms.V2.Kingdom;
using NUnit.Framework;

namespace AstraKingdoms.V2.Tests;

/// <summary>V2 kingdom package: property, migration/recovery and duplicate-grant tests.</summary>
public class KingdomTests
{
    private static KingdomService Kingdom(V2Environment env) => env.Kingdom;

    [Test]
    public void DefaultCatalogueAndMilestonesValidate()
    {
        Assert.That(DecorationCatalog.Default.Validate(), Is.Empty);
        Assert.That(MilestoneTable.Default.Validate(HomelandRules.Default), Is.Empty);
        Assert.That(HomelandRules.Default.PlotCount, Is.EqualTo(12));
        Assert.That(HomelandRules.Default.InitialPlots, Is.EqualTo(3));
    }

    [Test]
    public void ANewAccountHasThreeOpenProtectedPlotsAndDefaults()
    {
        V2Environment env = TestUtil.Env(out _);
        KingdomView view = env.Kingdom.View("p1");
        Assert.That(view.Plots, Has.Count.EqualTo(12));
        Assert.That(view.OpenPlotCount, Is.EqualTo(3));
        Assert.That(view.Plots.All(p => p.Protected), Is.True);
        Assert.That(view.Plots.All(p => p.Main.Id == DecorationCatalog.MeadowId && p.Accent.Id == DecorationCatalog.PlainPennantId), Is.True);
        Assert.That(view.Visits, Is.EqualTo(VisitAudience.FriendsOnly), "friends-only by default");
        Assert.That(view.Plots.Where(p => !p.Open).All(p => p.OpensWith != null), Is.True, "every locked plot shows its published milestone");
    }

    [Test]
    public void PlotsOpenThroughPublishedPlayMilestonesAndNeverClose()
    {
        V2Environment env = TestUtil.Env(out _);
        TestUtil.PlayMatches(env, "p1", 2);
        Assert.That(env.Kingdom.OpenPlots("p1"), Has.Count.EqualTo(3));
        TestUtil.PlayMatches(env, "p1", 1, prefix: "x");
        Assert.That(env.Kingdom.OpenPlots("p1"), Does.Contain(3), "matches-3 opens plot 3");
        TestUtil.PlayMatches(env, "p1", 10, prefix: "y", weapons: new[] { 1, 2, 3, 4, 5 });
        Assert.That(env.Kingdom.OpenPlots("p1"), Is.SupersetOf(new[] { 3, 4, 5 }));
        // A later correction of a match does not take an achievement away.
        int open = env.Kingdom.OpenPlots("p1").Count;
        env.Progression.Reverse(Meta.Progression.ProgressionService.MatchKey("y0", "p1"), "test correction");
        env.Kingdom.EvaluateMilestones("p1");
        Assert.That(env.Kingdom.OpenPlots("p1"), Has.Count.EqualTo(open));
    }

    [Test]
    public void MilestonesAreGrantedOnceDespiteRetriesReinstallsAndBackups()
    {
        V2Environment env = TestUtil.Env(out _);
        TestUtil.PlayMatches(env, "p1", 12);
        int grants = env.Grants.ForPlayer("p1").Count;
        Assert.That(grants, Is.EqualTo(2), "matches-3 and matches-10");
        for (int i = 0; i < 5; i++) Assert.That(env.Kingdom.EvaluateMilestones("p1"), Is.Empty, "retry");
        TestUtil.PlayMatches(env, "p1", 12); // the same match ids replayed by a reinstalled client
        Assert.That(env.Grants.ForPlayer("p1"), Has.Count.EqualTo(grants));
        IReadOnlyList<GrantRecord> backup = env.Grants.Export();
        Assert.That(env.Grants.Restore(backup), Is.Zero);
        Assert.That(env.Grants.Restore(backup.Take(1)), Is.Zero, "an older partial backup");
        Assert.That(env.Grants.ForPlayer("p1"), Has.Count.EqualTo(grants));
    }

    [Test]
    public void ConcurrentMilestoneEvaluationGrantsExactlyOnce()
    {
        V2Environment env = TestUtil.Env(out _);
        TestUtil.PlayMatches(env, "p1", 3);
        env.Grants.DeletePlayer("p1");
        int fresh = 0;
        Parallel.For(0, 64, _ => Interlocked.Add(ref fresh, env.Kingdom.EvaluateMilestones("p1").Count));
        Assert.That(fresh, Is.EqualTo(1));
    }

    [Test]
    public void DecorationsAreBoughtWithEarnedCoinsOnceAndNeverDoubleCharged()
    {
        V2Environment env = TestUtil.Env(out _);
        Assert.That(env.Kingdom.Buy("p1", "decor.cottage"), Is.EqualTo(DecorationBuyStatus.InsufficientCoins));
        TestUtil.PlayMatches(env, "p1", 5); // 15 coins each (human win)
        long coins = env.RewardLedger.Totals("p1").Coins;
        Assert.That(coins, Is.EqualTo(75));
        Assert.That(env.Kingdom.Buy("p1", "decor.cottage"), Is.EqualTo(DecorationBuyStatus.Purchased));
        Assert.That(env.Kingdom.Buy("p1", "decor.cottage"), Is.EqualTo(DecorationBuyStatus.AlreadyOwned));
        Assert.That(env.RewardLedger.Totals("p1").Coins, Is.EqualTo(15));
        Assert.That(env.Kingdom.OwnedDecorations("p1"), Does.Contain("decor.cottage"));
        Assert.That(env.Kingdom.Buy("p1", DecorationCatalog.TrophyFor("Gold")), Is.EqualTo(DecorationBuyStatus.NotForSale), "trophies are earned");
        Assert.That(env.Kingdom.Buy("p1", DecorationCatalog.MeadowId), Is.EqualTo(DecorationBuyStatus.NotForSale));
    }

    [Test]
    public void ConcurrentPurchasesChargeOnce()
    {
        V2Environment env = TestUtil.Env(out _);
        TestUtil.PlayMatches(env, "p1", 20);
        long before = env.RewardLedger.Totals("p1").Coins;
        var results = new System.Collections.Concurrent.ConcurrentBag<DecorationBuyStatus>();
        Parallel.For(0, 32, _ => results.Add(env.Kingdom.Buy("p1", "decor.temple")));
        Assert.That(results.Count(r => r == DecorationBuyStatus.Purchased), Is.EqualTo(1));
        Assert.That(env.RewardLedger.Totals("p1").Coins, Is.EqualTo(before - 150));
    }

    [Test]
    public void PlacementIsImmediateAndValidated()
    {
        V2Environment env = TestUtil.Env(out _);
        TestUtil.PlayMatches(env, "p1", 12);
        Assert.That(env.Kingdom.Buy("p1", "decor.watchtower"), Is.EqualTo(DecorationBuyStatus.Purchased));
        env.Kingdom.Buy("p1", "decor.banner-agni");
        LayoutSaveResult r = env.Kingdom.Place("p1", 0, new Placement(0, PlotSlot.Main, "decor.watchtower", 1));
        Assert.That(r.Status, Is.EqualTo(LayoutSaveStatus.Saved));
        Assert.That(env.Kingdom.View("p1").Plots[0].Main.Id, Is.EqualTo("decor.watchtower"), "no construction timer");
        Assert.That(env.Kingdom.Place("p1", r.Revision, new Placement(1, PlotSlot.Main, "decor.banner-agni")).Status, Is.EqualTo(LayoutSaveStatus.Invalid), "wrong slot");
        Assert.That(env.Kingdom.Place("p1", r.Revision, new Placement(1, PlotSlot.Main, "decor.temple")).Status, Is.EqualTo(LayoutSaveStatus.Invalid), "not owned");
        Assert.That(env.Kingdom.Place("p1", r.Revision, new Placement(11, PlotSlot.Accent, "decor.banner-agni")).Status, Is.EqualTo(LayoutSaveStatus.Invalid), "locked plot");
        Assert.That(env.Kingdom.Place("p1", r.Revision, new Placement(1, PlotSlot.Accent, "decor.banner-agni", 4)).Status, Is.EqualTo(LayoutSaveStatus.Invalid), "rotation");
        Assert.That(env.Kingdom.Place("p1", 0, new Placement(1, PlotSlot.Accent, "decor.banner-agni")).Status, Is.EqualTo(LayoutSaveStatus.Conflict), "stale revision");
        LayoutSaveResult r2 = env.Kingdom.Place("p1", r.Revision, new Placement(1, PlotSlot.Accent, "decor.banner-agni"));
        Assert.That(r2.Status, Is.EqualTo(LayoutSaveStatus.Saved));
        Assert.That(env.Kingdom.Clear("p1", r2.Revision, 0, PlotSlot.Main).Status, Is.EqualTo(LayoutSaveStatus.Saved));
        Assert.That(env.Kingdom.View("p1").Plots[0].Main.Id, Is.EqualTo(DecorationCatalog.MeadowId));
    }

    [Test]
    public void ARetiredAssetShowsItsReplacementAndStaysOwned()
    {
        // Catalogue version 1 sold the Clay Well; version 2 retired its asset.
        var v1 = new DecorationCatalog(1, DecorationCatalog.Default.Items.Where(i => i.IntroducedIn == 1)
            .Select(i => i.Id == "decor.well-clay" ? new DecorationItem(i.Id, i.Kind, i.Source, i.EnglishName, i.AssetKey, i.CoinPrice) : i));
        ManualClock clock = TestUtil.Clock();
        var ledger = new InMemoryRewardLedgerStore();
        var grants = new InMemoryGrantLedgerStore();
        var store = new InMemoryKingdomStore();
        var progression = new Meta.Progression.ProgressionService(ledger, clock);
        for (int i = 0; i < 5; i++)
            progression.GrantForMatch(new Meta.Progression.MatchOutcomeReport("m" + i, "p1", Meta.Progression.MatchKind.OnlineHuman,
                Meta.Progression.PlayerOutcome.Win, Meta.Progression.MatchEnding.RoundsComplete, clock.UtcNow));
        var oldClient = new KingdomService(v1, HomelandRules.Default, MilestoneTable.Default, ledger, grants, store, clock);
        Assert.That(oldClient.Buy("p1", "decor.well-clay"), Is.EqualTo(DecorationBuyStatus.Purchased));
        Assert.That(oldClient.Place("p1", 0, new Placement(2, PlotSlot.Main, "decor.well-clay")).Status, Is.EqualTo(LayoutSaveStatus.Saved));

        var current = new KingdomService(DecorationCatalog.Default, HomelandRules.Default, MilestoneTable.Default, ledger, grants, store, clock);
        PlotView plot = current.View("p1").Plots[2];
        Assert.That(plot.Main.Id, Is.EqualTo("decor.well-stone"));
        Assert.That(plot.UsedFallback, Is.True);
        Assert.That(current.OwnedDecorations("p1"), Does.Contain("decor.well-clay"), "previously bought cosmetics remain owned");
        Assert.That(current.Buy("p2", "decor.well-clay"), Is.EqualTo(DecorationBuyStatus.Retired));
        // The retired item may stay where it is, but it cannot be newly placed.
        long rev = current.View("p1").Layout.Revision;
        Assert.That(current.SaveLayout("p1", rev, current.View("p1").Layout.Placements.Append(new Placement(1, PlotSlot.Main, "decor.marigold-beds"))).Status,
            Is.EqualTo(LayoutSaveStatus.Invalid), "not owned");
        Assert.That(current.Place("p1", rev, new Placement(0, PlotSlot.Main, "decor.well-clay")).Status, Is.EqualTo(LayoutSaveStatus.Invalid));
    }

    [Test]
    public void AnUnknownOrChainRetiredIdFallsBackToTheSafeDefault()
    {
        var cat = new DecorationCatalog(3, DecorationCatalog.Default.Items.Concat(new[]
        {
            new DecorationItem("decor.a", DecorationKind.Building, DecorationSource.CoinShop, "A", "x/a", 10, 1, 2, "decor.b"),
            new DecorationItem("decor.b", DecorationKind.Building, DecorationSource.CoinShop, "B", "x/b", 10, 2, 3, null),
        }));
        Assert.That(cat.ResolveDisplayed("decor.a", PlotSlot.Main).Id, Is.EqualTo(DecorationCatalog.MeadowId));
        Assert.That(cat.ResolveDisplayed("decor.from-a-newer-client", PlotSlot.Main).Id, Is.EqualTo(DecorationCatalog.MeadowId));
        Assert.That(cat.ResolveDisplayed(null, PlotSlot.Accent).Id, Is.EqualTo(DecorationCatalog.PlainPennantId));
        var cyclic = new DecorationCatalog(2, DecorationCatalog.Default.Items.Concat(new[]
        {
            new DecorationItem("decor.c1", DecorationKind.Garden, DecorationSource.CoinShop, "C1", "x/c1", 10, 1, 2, "decor.c2"),
            new DecorationItem("decor.c2", DecorationKind.Garden, DecorationSource.CoinShop, "C2", "x/c2", 10, 1, 2, "decor.c1"),
        }));
        Assert.That(cyclic.Validate(), Has.Some.Contains("cycle"));
        Assert.That(cyclic.ResolveDisplayed("decor.c1", PlotSlot.Main).Id, Is.EqualTo(DecorationCatalog.MeadowId), "cycles terminate");
    }

    [Test]
    public void LayoutSchemaOneMigratesAndCorruptDataRecovers()
    {
        V2Environment env = TestUtil.Env(out _);
        TestUtil.PlayMatches(env, "p1", 10);
        env.Kingdom.Buy("p1", "decor.cottage");
        env.KingdomStore.Overwrite("p1", 4, "{\"schema\":1,\"revision\":4,\"plots\":[{\"plot\":0,\"decor\":\"decor.cottage\"},{\"plot\":99,\"decor\":\"decor.cottage\"}]}");
        LayoutLoadResult load = env.Kingdom.LoadLayout("p1");
        Assert.That(load.Status, Is.EqualTo(LayoutLoadStatus.Migrated));
        Assert.That(load.Layout.Placements.Single().DecorationId, Is.EqualTo("decor.cottage"));
        Assert.That(load.Layout.Revision, Is.EqualTo(4));
        Assert.That(env.Kingdom.PersistRecovery("p1"), Is.True);
        Assert.That(env.Kingdom.LoadLayout("p1").Status, Is.EqualTo(LayoutLoadStatus.Ok));
        Assert.That(env.Kingdom.LoadLayout("p1").Layout.Revision, Is.EqualTo(5));

        foreach (string corrupt in new[] { "{\"schema\":2,\"revision\":", "not json", "{\"schema\":9}", "{\"schema\":2,\"placements\":[{\"plot\":0,\"slot\":\"roof\",\"id\":\"decor.cottage\"}]}" })
        {
            env.KingdomStore.Overwrite("p1", 7, corrupt);
            KingdomView view = null;
            Assert.DoesNotThrow(() => view = env.Kingdom.View("p1"), corrupt);
            Assert.That(view.LoadStatus, Is.EqualTo(LayoutLoadStatus.RecoveredDefault).Or.EqualTo(LayoutLoadStatus.Repaired), corrupt);
            Assert.That(view.Plots, Has.Count.EqualTo(12));
            Assert.That(view.Layout.Revision, Is.EqualTo(7), "the revision column survives a corrupt blob, so the next save is not a conflict");
            Assert.That(env.Kingdom.Place("p1", 7, new Placement(0, PlotSlot.Main, "decor.cottage")).Status, Is.EqualTo(LayoutSaveStatus.Saved));
        }
    }

    [Test]
    public void LayoutRoundTripsThroughTheCurrentSchema()
    {
        var layout = new HomelandLayout(9, 2, new[] { new Placement(2, PlotSlot.Accent, "decor.banner-vayu", 3), new Placement(0, PlotSlot.Main, "decor.cottage", 1) });
        LayoutLoadResult back = LayoutSerializer.Read(LayoutSerializer.Write(layout), HomelandRules.Default, DecorationCatalog.Default);
        Assert.That(back.Status, Is.EqualTo(LayoutLoadStatus.Ok));
        Assert.That(back.Layout.Placements, Is.EqualTo(layout.Placements));
        Assert.That(back.Layout.Revision, Is.EqualTo(9));
    }

    [Test]
    public void SkippingDaysCostsNothingAndThereAreNoTimersOrAttackApis()
    {
        V2Environment env = TestUtil.Env(out ManualClock clock);
        TestUtil.PlayMatches(env, "p1", 10);
        env.Kingdom.Buy("p1", "decor.cottage");
        env.Kingdom.Place("p1", 0, new Placement(0, PlotSlot.Main, "decor.cottage"));
        KingdomView before = env.Kingdom.View("p1");
        clock.Advance(TimeSpan.FromDays(400));
        KingdomView after = env.Kingdom.View("p1");
        Assert.That(after.Plots.Select(p => p.Main.Id), Is.EqualTo(before.Plots.Select(p => p.Main.Id)));
        Assert.That(after.Coins, Is.EqualTo(before.Coins));
        Assert.That(after.OpenPlotCount, Is.EqualTo(before.OpenPlotCount));
        foreach (MethodInfo m in typeof(KingdomService).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.That(m.GetParameters().Any(p => p.ParameterType == typeof(TimeSpan)), Is.False, m.Name + " takes a duration");
            Assert.That(m.Name, Does.Not.Match("(?i)attack|occupy|confiscate|raid|repair|speed|accelerat|upkeep"), m.Name);
            Assert.That(m.GetParameters().Count(p => p.Name!.Contains("player", StringComparison.OrdinalIgnoreCase)), Is.LessThanOrEqualTo(1),
                m.Name + ": no kingdom operation acts on two accounts");
        }
    }

    [Test]
    public void SeasonChangesNeverRemoveOwnedDecorations()
    {
        V2Environment env = TestUtil.Env(out ManualClock clock, seasons: 2);
        TestUtil.PlayMatches(env, "p1", 10);
        env.Kingdom.Buy("p1", "decor.lotus-pond");
        env.Grants.TryAppend(new GrantRecord(GrantKeys.LeagueReward("S1", "p1"), "p1", GrantSource.SeasonLeagueReward, DecorationCatalog.TrophyFor("Silver"), "S1/Silver", clock.UtcNow));
        clock.Set(env.Calendar.Seasons[1].EndsAt + TimeSpan.FromDays(30));
        Assert.That(env.Kingdom.OwnedDecorations("p1"), Is.SupersetOf(new[] { "decor.lotus-pond", DecorationCatalog.TrophyFor("Silver") }));
    }
}
