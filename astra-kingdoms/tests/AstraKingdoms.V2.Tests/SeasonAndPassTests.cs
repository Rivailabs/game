using System.Reflection;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Shop;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.V2.Common;
using AstraKingdoms.V2.Integration;
using AstraKingdoms.V2.Kingdom;
using AstraKingdoms.V2.Ranked;
using AstraKingdoms.V2.Seasons;
using NUnit.Framework;

namespace AstraKingdoms.V2.Tests;

/// <summary>Season lifecycle (phases, reconciliation, settlement) and the cosmetic pass (purchase entitlement tests).</summary>
public class SeasonAndPassTests
{
    private const string Salt = "test-salt";

    private sealed class Rig
    {
        public V2Environment Env;
        public ManualClock Clock;
        public SeasonDefinition S1;
        public FakePurchaseVerifier Play = new();
        public PurchaseService Billing;
    }

    private static Rig Make(int seasons = 1)
    {
        var rig = new Rig();
        rig.Env = TestUtil.Env(out rig.Clock, seasons);
        rig.S1 = rig.Env.Calendar.Seasons[0];
        rig.Billing = new PurchaseService(rig.Play, rig.Env.Entitlements, new InMemoryAcknowledgementQueue(), rig.Env.StoreWithPasses(), rig.Clock,
            new BillingOptions { AccountIdSalt = Salt });
        rig.Clock.Set(rig.S1.StartsAt + TimeSpan.FromHours(2));
        return rig;
    }

    private static void Matches(Rig rig, string player, int n, string prefix = "pm")
    {
        for (int i = 0; i < n; i++)
            rig.Env.OnMatchReport(new MatchOutcomeReport(prefix + i, player, MatchKind.Practice, PlayerOutcome.Loss, MatchEnding.RoundsComplete, rig.Clock.UtcNow));
    }

    private static PurchaseStatus Buy(Rig rig, string player, bool pending = false, string token = null)
    {
        PurchaseAuthorization auth = rig.Env.Pass.AuthorizeCheckout(player, rig.S1.Id, AudienceProfile.Adult, null, rig.Env.StoreWithPasses(), Salt);
        if (!auth.Allowed) return PurchaseStatus.Canceled;
        token ??= "tok-" + player;
        rig.Play.AddPurchase(rig.S1.PassSku, token, new ProductPurchase(pending ? PlayPurchaseState.Pending : PlayPurchaseState.Purchased,
            AcknowledgementState.NotAcknowledged, 0, "GPA." + token, rig.Clock.UtcNow, null, auth.ObfuscatedAccountId));
        PurchaseResult r = rig.Billing.HandlePurchaseAsync(player, rig.S1.PassSku, token).GetAwaiter().GetResult();
        if (r.Status == PurchaseStatus.Granted || r.Status == PurchaseStatus.AlreadyOwned) rig.Env.Pass.DeliverPaidForOwner(player, rig.S1.Id);
        return r.Status;
    }

    // ------------------------------------------------------------------ lifecycle

    [Test]
    public void PhasesFollowThePublishedDates()
    {
        Rig rig = Make();
        SeasonDefinition s = rig.S1;
        rig.Clock.Set(s.StartsAt - TimeSpan.FromSeconds(1));
        Assert.That(rig.Env.Seasons.Phase(s.Id), Is.EqualTo(SeasonPhase.Scheduled));
        rig.Clock.Set(s.StartsAt);
        Assert.That(rig.Env.Seasons.Phase(s.Id), Is.EqualTo(SeasonPhase.Open));
        rig.Clock.Set(s.MatchmakingClosesAt);
        Assert.That(rig.Env.Seasons.Phase(s.Id), Is.EqualTo(SeasonPhase.MatchmakingClosed));
        rig.Clock.Set(s.EndsAt);
        Assert.That(rig.Env.Seasons.Phase(s.Id), Is.EqualTo(SeasonPhase.Ended));
        Assert.That(rig.Env.Seasons.Settle(s.Id).Status, Is.EqualTo(SettlementStatus.Settled));
        Assert.That(rig.Env.Seasons.Phase(s.Id), Is.EqualTo(SeasonPhase.Settled));
        Assert.That(s.PassSalesCloseAt, Is.EqualTo(s.EndsAt - TimeSpan.FromHours(24)));
        Assert.That(s.PassSku, Is.EqualTo("ak.pass.season_001"));
    }

    [Test]
    public void ConsecutiveSeasonsAreBackToBack()
    {
        V2Environment env = TestUtil.Env(out _, seasons: 3);
        Assert.That(env.Calendar.Seasons[1].StartsAt, Is.EqualTo(env.Calendar.Seasons[0].EndsAt));
        Assert.That(env.Calendar.Seasons[2].Id, Is.EqualTo("S3"));
        Assert.That(env.Calendar.Seasons.Select(s => s.PassSku).Distinct().Count(), Is.EqualTo(3), "one pass SKU per season");
    }

    [Test]
    public void SettlementCannotHappenEarlyAndWaitsForReconciliation()
    {
        Rig rig = Make();
        RankedMatchmaker mm = rig.Env.Matchmaker(rig.S1.Id);
        string snap = rig.Env.Snapshots.Current(rig.S1.Id).SnapshotId;
        mm.Enqueue("a", snap);
        mm.Enqueue("b", snap);
        RankedMatchTicket t = mm.Tick().Single();
        Assert.That(rig.Env.Seasons.Settle(rig.S1.Id).Status, Is.EqualTo(SettlementStatus.NotEnded));
        rig.Clock.Set(rig.S1.EndsAt + TimeSpan.FromMinutes(1));
        SettlementReport waiting = rig.Env.Seasons.Settle(rig.S1.Id);
        Assert.That(waiting.Status, Is.EqualTo(SettlementStatus.AwaitingReconciliation));
        Assert.That(waiting.UnresolvedMatchIds, Is.EqualTo(new[] { t.MatchId }));
        // The old-season match reports after the boundary: it still counts for season 1.
        Assert.That(rig.Env.Ranked.Record(new RankedMatchResult(t.MatchId, t.SeasonId, QueueKind.Ranked, t.SnapshotId, "a", "b", PlayerSide.A,
            MatchEnding.RoundsComplete, rig.S1.EndsAt - TimeSpan.FromMinutes(1))).Status, Is.EqualTo(ResultStatus.Rated));
        Assert.That(rig.Env.Seasons.Settle(rig.S1.Id).Status, Is.EqualTo(SettlementStatus.Settled));
        Assert.That(rig.Env.Seasons.Settle(rig.S1.Id).Status, Is.EqualTo(SettlementStatus.AlreadySettled));
        Assert.That(rig.Env.Ranked.Record(new RankedMatchResult(t.MatchId, t.SeasonId, QueueKind.Ranked, t.SnapshotId, "a", "b", PlayerSide.B,
            MatchEnding.RoundsComplete, rig.Clock.UtcNow)).Status, Is.EqualTo(ResultStatus.Duplicate));
    }

    [Test]
    public void OverdueMatchesAreCancelledUnderThePublishedPolicyAfterTheGrace()
    {
        Rig rig = Make();
        RankedMatchmaker mm = rig.Env.Matchmaker(rig.S1.Id);
        string snap = rig.Env.Snapshots.Current(rig.S1.Id).SnapshotId;
        mm.Enqueue("a", snap);
        mm.Enqueue("b", snap);
        RankedMatchTicket t = mm.Tick().Single();
        rig.Clock.Set(rig.S1.EndsAt + SeasonRules.Default.ReconciliationGrace - TimeSpan.FromSeconds(1));
        Assert.That(rig.Env.Seasons.Settle(rig.S1.Id).Status, Is.EqualTo(SettlementStatus.AwaitingReconciliation));
        rig.Clock.Set(rig.S1.EndsAt + SeasonRules.Default.ReconciliationGrace);
        SettlementReport r = rig.Env.Seasons.Settle(rig.S1.Id);
        Assert.That(r.Status, Is.EqualTo(SettlementStatus.Settled));
        Assert.That(r.CancelledByPolicy, Is.EqualTo(new[] { t.MatchId }));
        Assert.That(rig.Env.Ranked.Skill("a").Rating, Is.EqualTo(RankedRules.Default.StartRating));
        Assert.That(rig.Env.Audit.Entries.Any(e => e.Action == "ranked.cancel-overdue"), Is.True);
    }

    [Test]
    public void IncidentCancellationNeedsADecision()
    {
        Rig rig = Make();
        RankedMatchmaker mm = rig.Env.Matchmaker(rig.S1.Id);
        string snap = rig.Env.Snapshots.Current(rig.S1.Id).SnapshotId;
        mm.Enqueue("a", snap);
        mm.Enqueue("b", snap);
        RankedMatchTicket t = mm.Tick().Single();
        Assert.That(rig.Env.Ranked.CancelForIncident(rig.S1.Id, t.MatchId, "", "ops", rig.Clock.UtcNow), Is.False);
        Assert.That(rig.Env.Ranked.CancelForIncident(rig.S1.Id, t.MatchId, "INC-7", "ops", rig.Clock.UtcNow), Is.True);
        Assert.That(rig.Env.Ranked.CancelForIncident(rig.S1.Id, t.MatchId, "INC-7", "ops", rig.Clock.UtcNow), Is.False, "once");
    }

    [Test]
    public void LeagueTrophiesGoOnlyToPlacedPlayersOnce()
    {
        Rig rig = Make();
        RankedMatchmaker mm = rig.Env.Matchmaker(rig.S1.Id);
        string snap = rig.Env.Snapshots.Current(rig.S1.Id).SnapshotId;
        for (int i = 0; i < 5; i++)
        {
            mm.Enqueue("placed", snap);
            mm.Enqueue("o" + i, snap);
            rig.Clock.Advance(TimeSpan.FromMinutes(2));
            RankedMatchTicket t = mm.Tick().Single();
            rig.Env.Ranked.Record(new RankedMatchResult(t.MatchId, t.SeasonId, QueueKind.Ranked, t.SnapshotId, t.PlayerA, t.PlayerB, null, MatchEnding.RoundsComplete, rig.Clock.UtcNow));
        }
        rig.Clock.Set(rig.S1.EndsAt);
        SettlementReport r = rig.Env.Seasons.Settle(rig.S1.Id);
        Assert.That(r.LeagueRewards, Is.EqualTo(1));
        GrantRecord g = rig.Env.Grants.Find(GrantKeys.LeagueReward(rig.S1.Id, "placed"));
        Assert.That(g.ItemId, Is.EqualTo(DecorationCatalog.TrophyFor("Bronze")));
        Assert.That(rig.Env.Grants.Find(GrantKeys.LeagueReward(rig.S1.Id, "o0")), Is.Null);
        Assert.That(rig.Env.Kingdom.OwnedDecorations("placed"), Does.Contain(g.ItemId));
        Assert.That(rig.Env.RankedStore.SeasonsPlaced("placed"), Is.EqualTo(1));
        Assert.That(rig.Env.Kingdom.EvaluateMilestones("placed").Select(m => m.Id), Does.Contain("placed-1"));
    }

    // ------------------------------------------------------------------ pass

    [Test]
    public void ThePassDefinitionValidatesAndContainsNoPowerOrPressure()
    {
        Rig rig = Make();
        SeasonPassDefinition pass = rig.Env.Pass.Pass(rig.S1.Id);
        Assert.That(pass.Validate(DecorationCatalog.Default, CosmeticCatalog.Default), Is.Empty);
        Assert.That(pass.Tiers, Has.Count.EqualTo(20));
        Assert.That(pass.MatchesForAllTiers, Is.EqualTo(60), "about 3.3 matches a day over 18 days, any days");
        string[] forbidden = { "Rating", "Weapon", "Multiplier", "Boost", "Protection", "Daily", "Streak", "Login", "Ad", "Attendance" };
        foreach (Type t in new[] { typeof(SeasonPassDefinition), typeof(PassTier) })
            foreach (PropertyInfo p in t.GetProperties())
                foreach (string f in forbidden)
                    Assert.That(p.Name.StartsWith(f, StringComparison.Ordinal) || p.Name.Contains(f + "s"), Is.False, t.Name + "." + p.Name);
        // A reward colliding with a Meta paid cosmetic would be granted wholesale on purchase.
        var bad = new SeasonPassDefinition("x", rig.S1, 10, pass.Tiers.Select((t, i) => i == 0 ? new PassTier(1, 30, null, "bow.sunrise") : t));
        Assert.That(bad.Validate(DecorationCatalog.Default, CosmeticCatalog.Default), Has.Some.Contains("Meta cosmetic").Or.Some.Contains("unknown reward"));
    }

    [Test]
    public void PointsComeOnlyFromEligibleCompletedMatchesOncePerMatch()
    {
        Rig rig = Make();
        Matches(rig, "p", 3);
        Matches(rig, "p", 3); // the same ids again
        Assert.That(rig.Env.Pass.Points("p", rig.S1.Id), Is.EqualTo(30));
        var automation = new MatchOutcomeReport("auto", "p", MatchKind.Practice, PlayerOutcome.Win, MatchEnding.RoundsComplete, rig.Clock.UtcNow, isAutomation: true);
        var forfeit = new MatchOutcomeReport("ff", "p", MatchKind.OnlineHuman, PlayerOutcome.Win, MatchEnding.VoluntaryForfeit, rig.Clock.UtcNow);
        Assert.That(rig.Env.Pass.RecordMatch(automation), Is.False);
        Assert.That(rig.Env.Pass.RecordMatch(forfeit), Is.False);
        rig.Clock.Set(rig.S1.EndsAt + TimeSpan.FromMinutes(1));
        Assert.That(rig.Env.Pass.RecordMatch(new MatchOutcomeReport("late", "p", MatchKind.OnlineHuman, PlayerOutcome.Win, MatchEnding.RoundsComplete, rig.Clock.UtcNow)), Is.False,
            "a match completed after the end does not earn old-season points");
        Assert.That(rig.Env.Pass.RecordMatch(new MatchOutcomeReport("inSeason", "p", MatchKind.OnlineHuman, PlayerOutcome.Win, MatchEnding.RoundsComplete, rig.S1.EndsAt - TimeSpan.FromMinutes(3))),
            Is.True, "a result delivered late but completed in season still counts before settlement");
    }

    [Test]
    public void FreeClaimsWorkWithoutThePassAndPaidClaimsNeedIt()
    {
        Rig rig = Make();
        Matches(rig, "p", 6); // 60 points: tiers 1 and 2
        Assert.That(rig.Env.Pass.Claim("p", rig.S1.Id, 2, false), Is.EqualTo(ClaimStatus.Claimed));
        Assert.That(rig.Env.Pass.Claim("p", rig.S1.Id, 2, false), Is.EqualTo(ClaimStatus.AlreadyClaimed));
        Assert.That(rig.Env.Pass.Claim("p", rig.S1.Id, 1, false), Is.EqualTo(ClaimStatus.NoReward), "odd tiers have no free reward");
        Assert.That(rig.Env.Pass.Claim("p", rig.S1.Id, 3, false), Is.EqualTo(ClaimStatus.NoReward));
        Assert.That(rig.Env.Pass.Claim("p", rig.S1.Id, 4, false), Is.EqualTo(ClaimStatus.NotEarned));
        Assert.That(rig.Env.Pass.Claim("p", rig.S1.Id, 1, true), Is.EqualTo(ClaimStatus.RequiresPass));
        PassTrackView view = rig.Env.Pass.View("p", rig.S1.Id);
        Assert.That(view.EarnedTiers, Is.EqualTo(2));
        Assert.That(view.Tiers[0].Paid, Is.EqualTo(PassRewardState.RequiresPass));
        Assert.That(view.Tiers[1].Free, Is.EqualTo(PassRewardState.Claimed));
        Assert.That(view.Tiers[2].PointsRemaining, Is.EqualTo(30));
    }

    [Test]
    public void ThePreviewShowsClosingDateEarnedTiersAndRemainingRequirements()
    {
        Rig rig = Make();
        Matches(rig, "p", 7); // 70 points
        PassCheckoutPreview pv = rig.Env.Pass.Preview("p", rig.S1.Id, AudienceProfile.Adult, null);
        Assert.That(pv.CanPurchase, Is.True);
        Assert.That(pv.SalesCloseAt, Is.EqualTo(rig.S1.PassSalesCloseAt));
        Assert.That(pv.SeasonEndsAt, Is.EqualTo(rig.S1.EndsAt));
        Assert.That(pv.EarnedTiers, Is.EqualTo(2));
        Assert.That(pv.DeliveredOnPurchase, Has.Count.EqualTo(2));
        Assert.That(pv.RemainingRequirements.First(), Is.EqualTo(new KeyValuePair<int, int>(3, 20)));
        Assert.That(pv.RemainingRequirements, Has.Count.EqualTo(18));
        Assert.That(rig.Env.Pass.Preview("p", rig.S1.Id, AudienceProfile.Unknown, null).Block, Is.EqualTo(CheckoutBlock.Audience));
        Assert.That(rig.Env.Pass.Preview("p", rig.S1.Id, AudienceProfile.Child(), null).AudienceState, Is.EqualTo(OfferState.RequiresParentalConsent));
    }

    [Test]
    public void PurchaseGrantsAlreadyEarnedPaidRewardsOnceAndLaterTiersOnClaim()
    {
        Rig rig = Make();
        Matches(rig, "p", 9); // 90 points: tiers 1-3
        Assert.That(Buy(rig, "p"), Is.EqualTo(PurchaseStatus.Granted));
        Assert.That(rig.Env.Pass.OwnsPass("p", rig.S1.Id), Is.True, "ownership comes from Meta's entitlement ledger");
        Assert.That(Enumerable.Range(1, 3).All(t => rig.Env.Grants.Find(GrantKeys.PassTier(rig.S1.Id, "p", t, true)) != null), Is.True);
        Assert.That(rig.Env.Grants.Find(GrantKeys.PassTier(rig.S1.Id, "p", 4, true)), Is.Null, "unearned tiers are not entitlements");
        // Duplicate callback / restore / reinstall: nothing new.
        int grants = rig.Env.Grants.Count;
        Assert.That(rig.Billing.HandlePurchaseAsync("p", rig.S1.PassSku, "tok-p").GetAwaiter().GetResult().Status, Is.EqualTo(PurchaseStatus.AlreadyOwned));
        Assert.That(rig.Env.Pass.DeliverPaidForOwner("p", rig.S1.Id).PaidDelivered, Is.Zero);
        Assert.That(rig.Env.Grants.Count, Is.EqualTo(grants));
        Assert.That(Buy(rig, "p"), Is.EqualTo(PurchaseStatus.Canceled), "the checkout refuses a second purchase (owned)");
        Matches(rig, "p", 3, "more");
        Assert.That(rig.Env.Pass.Claim("p", rig.S1.Id, 4, true), Is.EqualTo(ClaimStatus.Claimed));
    }

    [Test]
    public void SalesStopInTheFinal24HoursButAPendingPaymentIsHonoured()
    {
        Rig rig = Make();
        Matches(rig, "late", 4);
        rig.Clock.Set(rig.S1.PassSalesCloseAt - TimeSpan.FromMinutes(1));
        Assert.That(Buy(rig, "late", pending: true), Is.EqualTo(PurchaseStatus.Pending));
        rig.Clock.Set(rig.S1.PassSalesCloseAt);
        Assert.That(rig.Env.Pass.Preview("other", rig.S1.Id, AudienceProfile.Adult, null).Block, Is.EqualTo(CheckoutBlock.SalesClosed));
        Assert.That(rig.Env.Pass.AuthorizeCheckout("other", rig.S1.Id, AudienceProfile.Adult, null, rig.Env.StoreWithPasses(), Salt).Allowed, Is.False);
        rig.Clock.Set(rig.S1.EndsAt + TimeSpan.FromHours(1));
        Assert.That(rig.Env.Seasons.Settle(rig.S1.Id).Status, Is.EqualTo(SettlementStatus.Settled));
        Assert.That(rig.Env.Grants.Find(GrantKeys.PassTier(rig.S1.Id, "late", 1, true)), Is.Null, "not paid yet at settlement");
        rig.Play.CompletePending("tok-late");
        Assert.That(rig.Billing.HandlePurchaseAsync("late", rig.S1.PassSku, "tok-late").GetAwaiter().GetResult().Status, Is.EqualTo(PurchaseStatus.Granted));
        DeliveryReport d = rig.Env.Pass.DeliverPaidForOwner("late", rig.S1.Id);
        Assert.That(d.PaidDelivered, Is.EqualTo(1), "late purchase delivers the earned paid tier");
        Assert.That(rig.Env.Pass.DeliverPaidForOwner("late", rig.S1.Id).PaidDelivered, Is.Zero);
    }

    [Test]
    public void SettlementDeliversEarnedUnclaimedRewardsAndNothingElse()
    {
        Rig rig = Make();
        Matches(rig, "free", 12);   // tiers 1-4, no pass
        Matches(rig, "owner", 12);
        Buy(rig, "owner");          // paid tiers 1-4 granted at purchase
        Matches(rig, "owner", 6, "extra"); // tiers 5-6 earned, unclaimed
        rig.Env.Pass.Claim("free", rig.S1.Id, 2, false);
        rig.Clock.Set(rig.S1.EndsAt);
        SettlementReport r = rig.Env.Seasons.Settle(rig.S1.Id);
        Assert.That(r.PassDeliveries.FreeDelivered, Is.EqualTo(1 + 3), "free: tier 4 (tier 2 was claimed); owner: tiers 2, 4, 6");
        Assert.That(r.PassDeliveries.PaidDelivered, Is.EqualTo(2), "owner: paid tiers 5 and 6");
        Assert.That(rig.Env.Grants.Find(GrantKeys.PassTier(rig.S1.Id, "free", 1, true)), Is.Null);
        Assert.That(rig.Env.Grants.Find(GrantKeys.PassTier(rig.S1.Id, "owner", 7, true)), Is.Null);
        Assert.That(rig.Env.Pass.Claim("owner", rig.S1.Id, 6, true), Is.EqualTo(ClaimStatus.AlreadyClaimed));
        Assert.That(rig.Env.Seasons.Settle(rig.S1.Id).PassDeliveries.FreeDelivered, Is.Zero);
        Assert.That(rig.Env.Pass.RecordMatch(new MatchOutcomeReport("after", "owner", MatchKind.Practice, PlayerOutcome.Win, MatchEnding.RoundsComplete,
            rig.S1.EndsAt - TimeSpan.FromMinutes(1))), Is.False, "no points into a settled season");
        Assert.That(rig.Env.Kingdom.OwnedDecorations("owner"), Does.Contain(DecorationCatalog.PassRewardId(1, 6, true)), "pass rewards are homeland decorations");
    }

    [Test]
    public void AVoidedPassStopsFurtherPaidDeliveries()
    {
        Rig rig = Make();
        Matches(rig, "p", 3);
        Buy(rig, "p");
        rig.Play.Void("tok-p", rig.Clock.UtcNow);
        rig.Billing.ProcessVoidedPurchasesAsync(rig.Clock.UtcNow - TimeSpan.FromDays(1)).GetAwaiter().GetResult();
        Assert.That(rig.Env.Pass.OwnsPass("p", rig.S1.Id), Is.False);
        Matches(rig, "p", 3, "later");
        Assert.That(rig.Env.Pass.Claim("p", rig.S1.Id, 2, true), Is.EqualTo(ClaimStatus.RequiresPass));
    }

    [Test]
    public void ThePassSkuIsAMetaStoreProductWithListedContents()
    {
        Rig rig = Make();
        StoreProduct p = rig.Env.StoreWithPasses().Find(rig.S1.PassSku);
        Assert.That(p, Is.Not.Null);
        Assert.That(p.Consumable, Is.False);
        Assert.That(p.Contents, Has.Count.EqualTo(20));
        Assert.That(p.EnglishDescription, Does.Contain("No rating"));
        // The paid contents are V2 decorations, so Meta's ownership resolver never grants them wholesale.
        CosmeticOwnership own = CosmeticOwnership.Resolve(CosmeticCatalog.Default, rig.Env.StoreWithPasses(), 1, null, new PlayerEntitlements(new[] { rig.S1.PassSku }));
        Assert.That(own.Ids.Intersect(p.Contents), Is.Empty);
    }
}
