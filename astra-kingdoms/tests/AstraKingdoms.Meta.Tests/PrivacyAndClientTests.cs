using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Client;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Economy;
using AstraKingdoms.Meta.Privacy;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Shop;
using NUnit.Framework;

namespace AstraKingdoms.Meta.Tests;

/// <summary>Plan "Store deletion and purchase readiness" and "Data and artifact retention defaults".</summary>
public class DeletionAndRetentionTests
{
    private sealed class FlakyProvider : IDataEraser
    {
        public int FailuresLeft;
        public int Calls;
        public string Name => "analytics-provider";
        public DataCategory Category => DataCategory.RawProductAnalytics;

        public Task<EraseOutcome> EraseAsync(string accountId, CancellationToken ct = default)
        {
            Calls++;
            if (FailuresLeft-- > 0) return Task.FromResult(EraseOutcome.ProviderUnavailable);
            return Task.FromResult(EraseOutcome.Erased);
        }
    }

    private ManualClock _clock;
    private InMemoryRewardLedgerStore _ledger;
    private InMemoryEntitlementLedgerStore _entitlements;
    private InMemoryDailyTaskProgressStore _daily;
    private InMemoryEquipmentStore _equip;
    private InMemoryAdTicketStore _tickets;
    private FlakyProvider _provider;
    private AccountDeletionService _svc;

    [SetUp]
    public void SetUp()
    {
        _clock = T0.Clock();
        _ledger = new InMemoryRewardLedgerStore();
        _entitlements = new InMemoryEntitlementLedgerStore();
        _daily = new InMemoryDailyTaskProgressStore();
        _equip = new InMemoryEquipmentStore();
        _tickets = new InMemoryAdTicketStore();
        _provider = new FlakyProvider();
        _svc = new AccountDeletionService(new[]
        {
            MetaErasers.RewardLedger(_ledger), MetaErasers.DailyTasks(_daily), MetaErasers.Equipment(_equip), MetaErasers.AdTickets(_tickets),
            MetaErasers.Entitlements(_entitlements, id => "deleted-" + id.GetHashCode().ToString("x", System.Globalization.CultureInfo.InvariantCulture)),
            _provider,
        }, _clock);

        new ProgressionService(_ledger, _clock).GrantForMatch(T0.Human("m", "alice", PlayerOutcome.Win, T0.Noon));
        new DailyTaskService(_ledger, _daily, _clock).RecordPracticeExercise("alice", "ex", T0.Noon);
        _equip.Set("alice", CosmeticSlot.Banner, "banner.plain");
        _entitlements.TryAppend(new EntitlementEntry("grant:t", "alice", "ak.cosmetic.sunrise_pack", "t", "GPA.1", EntitlementAction.Grant,
            EntitlementReason.VerifiedPurchase, T0.Noon), out _);
        new ProgressionService(_ledger, _clock).GrantForMatch(T0.Human("m", "bob", PlayerOutcome.Loss, T0.Noon));
    }

    [Test]
    public async Task InAppRequestDeletesAssociatedDataAndRetainsJustifiedPurchaseRecords()
    {
        DeletionRequest r = _svc.Request("alice", DeletionChannel.InApp, sessionValid: true);
        Assert.That(r.State, Is.EqualTo(DeletionState.InProgress));
        await _svc.ProcessAsync(r.RequestId);
        Assert.That(r.State, Is.EqualTo(DeletionState.Completed));
        Assert.That(_ledger.Entries("alice"), Is.Empty);
        Assert.That(_daily.Get("alice", "2026-10-06").PracticeEvents, Is.Empty);
        Assert.That(_equip.Get("alice"), Is.Empty);
        Assert.That(_entitlements.ForPlayer("alice"), Is.Empty);
        Assert.That(_entitlements.All().Single().PlayerId, Does.StartWith("deleted-"), "kept for accounting, re-keyed");
        Assert.That(r.Steps["purchase-records"], Is.EqualTo(EraseOutcome.RetainedJustified));
        Assert.That(_ledger.Entries("bob"), Has.Count.EqualTo(1), "other players untouched");
    }

    [Test]
    public async Task WebRequestsAndExpiredSessionsNeedVerificationFirst()
    {
        DeletionRequest web = _svc.Request("alice", DeletionChannel.Web, sessionValid: false);
        Assert.That(web.State, Is.EqualTo(DeletionState.AwaitingVerification));
        await _svc.ProcessAsync(web.RequestId);
        Assert.That(_ledger.Entries("alice"), Is.Not.Empty, "nothing happens before ownership is verified");
        Assert.That(_svc.Request("alice", DeletionChannel.InApp, sessionValid: false), Is.SameAs(web), "one open request per account");
        Assert.That(_svc.ConfirmVerification(web.RequestId), Is.True);
        Assert.That(_svc.ConfirmVerification(web.RequestId), Is.False);
        await _svc.ProcessAsync(web.RequestId);
        Assert.That(web.State, Is.EqualTo(DeletionState.Completed));

        DeletionRequest expired = _svc.Request("bob", DeletionChannel.InApp, sessionValid: false);
        Assert.That(expired.State, Is.EqualTo(DeletionState.AwaitingVerification));
        Assert.That(_svc.Request("bob", DeletionChannel.InApp, sessionValid: true).State, Is.EqualTo(DeletionState.InProgress), "signing in again verifies");
    }

    [Test]
    public async Task ProviderOutageLeavesAPartialRequestThatCompletesOnRetry()
    {
        _provider.FailuresLeft = 2;
        DeletionRequest r = _svc.Request("alice", DeletionChannel.InApp, true);
        await _svc.ProcessAsync(r.RequestId);
        Assert.That(r.State, Is.EqualTo(DeletionState.PartiallyCompleted));
        Assert.That(_ledger.Entries("alice"), Is.Empty, "local stores are already erased");
        await _svc.ProcessPendingAsync();
        Assert.That(r.State, Is.EqualTo(DeletionState.PartiallyCompleted));
        _clock.Advance(TimeSpan.FromDays(31));
        Assert.That(_svc.Overdue().Single(), Is.SameAs(r));
        await _svc.ProcessPendingAsync();
        Assert.That(r.State, Is.EqualTo(DeletionState.Completed));
        Assert.That(r.CompletedAt, Is.EqualTo(_clock.UtcNow));
        Assert.That(_provider.Calls, Is.EqualTo(3), "finished steps are not repeated");
        Assert.That(_svc.Overdue(), Is.Empty);
    }

    [Test]
    public async Task DeletingAnAccountWithNoDataStillCompletes()
    {
        DeletionRequest r = _svc.Request("never-played", DeletionChannel.InApp, true);
        await _svc.ProcessAsync(r.RequestId);
        Assert.That(r.State, Is.EqualTo(DeletionState.Completed));
    }

    [Test]
    public void PlaceholderLinksFailReleaseValidation()
    {
        var links = new PrivacyLinks();
        Assert.That(links.ValidateForRelease(), Has.Count.EqualTo(3));
        links.AccountDeletionUrl = "https://astra.example.org/delete";
        links.PrivacyPolicyUrl = "https://astra.example.org/privacy";
        links.GrievanceUrl = "http://astra.example.org/grievance";
        Assert.That(links.ValidateForRelease(), Is.EqualTo(new[] { "grievance URL must be https" }));
        Assert.That(links.InAppPath, Does.Contain("Delete my data"));
    }

    [Test]
    public void RetentionSweepUsesThePlanDefaults()
    {
        Assert.That(RetentionDefaults.For(DataCategory.MatchDiagnosticRecord).MaxAge, Is.EqualTo(TimeSpan.FromDays(30)));
        Assert.That(RetentionDefaults.For(DataCategory.RawProductAnalytics).MaxAge, Is.EqualTo(TimeSpan.FromDays(90)));
        Assert.That(RetentionDefaults.For(DataCategory.PurchaseRecord).NeedsDecision, Is.True);
        var now = T0.Noon;
        var records = new[] { ("young", now.AddDays(-30).AddTicks(1), false), ("old", now.AddDays(-30), false), ("held", now.AddDays(-60), true) };
        var expired = RetentionSweeper.Expired(records, DataCategory.MatchDiagnosticRecord, r => r.Item2, r => r.Item3, now);
        Assert.That(expired.Select(r => r.Item1), Is.EqualTo(new[] { "old" }));
        Assert.That(RetentionSweeper.Expired(records, DataCategory.AccountProfile, r => r.Item2, null, now), Is.Empty);
        var analytics = RetentionSweeper.Expired(records, DataCategory.RawProductAnalytics, r => r.Item2, r => r.Item3, now);
        Assert.That(analytics, Is.Empty, "analytics allow no support hold, but nothing is 90 days old");
    }
}

/// <summary>The offline guest backend and the client purchase flow (device side of ticket 61).</summary>
public class LocalBackendAndClientFlowTests
{
    [Test]
    public async Task GuestProfilePersistsAcrossRestarts()
    {
        var store = new MemoryPersistence();
        var clock = T0.Clock();
        var a = new LocalMetaBackend(store, clock, () => false);
        await a.SetDeclaredAgeAsync(25);
        await a.ReportLocalMatchAsync(T0.Practice("p1", "ignored", T0.Noon, 1, 2));
        await a.ReportLocalMatchAsync(T0.Practice("p2", "ignored", T0.Noon, 3));
        await a.ReportPracticeExerciseAsync("ex-1");
        Assert.That((await a.ClaimDailyTaskAsync("finish-two-matches", "2026-10-06")).Status, Is.EqualTo(ClaimStatus.Claimed));
        Assert.That((await a.ClaimDailyTaskAsync("two-elements", "2026-10-06")).Status, Is.EqualTo(ClaimStatus.Claimed));
        Assert.That((await a.ClaimDailyTaskAsync("practice-exercise", "2026-10-06")).Status, Is.EqualTo(ClaimStatus.Claimed));
        Assert.That(await a.BuyWithCoinsAsync("trail.sparks"), Is.EqualTo(CoinPurchaseStatus.Purchased));
        Assert.That(await a.EquipAsync("trail.sparks"), Is.EqualTo(EquipResult.Equipped));
        ProfileView before = await a.GetProfileAsync();

        var b = new LocalMetaBackend(store, clock, () => false);
        ProfileView after = await b.GetProfileAsync();
        Assert.That(b.PlayerId, Is.EqualTo(a.PlayerId));
        Assert.That(after.Progress.TotalXp, Is.EqualTo(before.Progress.TotalXp).And.EqualTo(100));
        Assert.That(after.Progress.EarnedCoins, Is.EqualTo(before.Progress.EarnedCoins).And.EqualTo(20 + 60 - 80));
        Assert.That(after.Audience.AgeGroup, Is.EqualTo(AgeGroup.Adult));
        Assert.That((await b.GetLockerAsync()).Appearance.Items[CosmeticSlot.ArrowTrail].Id, Is.EqualTo("trail.sparks"));
        Assert.That((await b.GetDailyTasksAsync()).All(t => t.Claimed), Is.True);
        Assert.That((await b.ClaimDailyTaskAsync("finish-two-matches", "2026-10-06")).Status, Is.EqualTo(ClaimStatus.AlreadyClaimed));
        Assert.That((await b.ReportLocalMatchAsync(T0.Practice("p1", "x", T0.Noon))).Status, Is.EqualTo(GrantStatus.AlreadyGranted));
        Assert.That(b.LocalMatchSummaries, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task CorruptSaveStartsAFreshProfile()
    {
        var store = new MemoryPersistence { Text = "{not json" };
        var b = new LocalMetaBackend(store, T0.Clock(), () => false);
        Assert.That((await b.GetProfileAsync()).Progress.TotalXp, Is.Zero);
        Assert.That(b.PlayerId, Does.StartWith("guest-"));
    }

    [Test]
    public async Task GuestDeletionWipesTheDevice()
    {
        var store = new MemoryPersistence();
        var b = new LocalMetaBackend(store, T0.Clock(), () => false);
        string old = b.PlayerId;
        await b.ReportLocalMatchAsync(T0.Practice("p1", "x", T0.Noon));
        Assert.That(await b.RequestDeletionAsync(), Is.EqualTo(DeletionState.Completed));
        Assert.That(b.PlayerId, Is.Not.EqualTo(old));
        Assert.That((await b.GetProfileAsync()).Progress.TotalXp, Is.Zero);
        Assert.That(store.Text, Does.Not.Contain(old));
        Assert.That(b.LocalMatchSummaries, Is.Empty);
    }

    [Test]
    public async Task OfflineBackendOffersNoPaidPurchases()
    {
        var b = new LocalMetaBackend(new MemoryPersistence(), T0.Clock(), () => false);
        await b.SetDeclaredAgeAsync(30);
        var shelf = await b.GetShopAsync(new Dictionary<string, string> { { "ak.cosmetic.sunrise_pack", "₹129.00" } });
        Assert.That(shelf.Where(o => o.Currency == OfferCurrency.Paid).All(o => o.State == OfferState.PriceUnavailable), Is.True);
        Assert.That((await b.AuthorizePaidPurchaseAsync("ak.cosmetic.sunrise_pack")).Allowed, Is.False);
    }

    private sealed class Harness
    {
        public LocalMetaBackend Backend;
        public FakeStoreBridge Store;
        public ClientPurchaseFlow Flow;
        public readonly List<PurchaseFlowUpdate> Updates = new();
        public readonly List<StoreTransaction> Delivered = new();
    }

    private static Harness Setup(int? age = 30)
    {
        var clock = T0.Clock();
        var play = new FakePurchaseVerifier();
        var h = new Harness { Backend = new LocalMetaBackend(new MemoryPersistence(), clock, () => false, play) };
        h.Backend.SetDeclaredAgeAsync(age).Wait();
        h.Store = new FakeStoreBridge(play, () => clock.UtcNow);
        h.Store.Prices["ak.cosmetic.sunrise_pack"] = "₹129.00";
        h.Store.Prices["ak.cosmetic.outfit_tide_warden"] = "₹89.00";
        h.Store.InitializeAsync(StoreCatalog.Default.Products.Select(p => p.Sku)).Wait();
        h.Store.PurchaseUpdated += h.Delivered.Add;
        h.Flow = new ClientPurchaseFlow(h.Store, h.Backend);
        h.Flow.Updated += h.Updates.Add;
        return h;
    }

    // The fake store and local backend complete synchronously, so each store callback is fully
    // handled (verified, finished) by the time the triggering call returns.

    [Test]
    public async Task PurchaseIsGrantedOnlyAfterServerVerificationAndThenFinished()
    {
        Harness h = Setup();
        Assert.That(h.Flow.Prices()["ak.cosmetic.sunrise_pack"], Is.EqualTo("₹129.00"));
        await h.Flow.BuyAsync("ak.cosmetic.sunrise_pack");
        Assert.That(h.Updates.Select(u => u.State), Is.EqualTo(new[] { PurchaseFlowState.InStore, PurchaseFlowState.Granted }));
        Assert.That(h.Store.OpenTransactions, Is.Empty);
        Assert.That(h.Store.FinishedCount, Is.EqualTo(1));
        Assert.That((await h.Backend.GetLockerAsync()).Entries.Single(e => e.Item.Id == "bow.sunrise").OwnedVia, Is.EqualTo(CosmeticSource.Paid));
        Assert.That((await h.Backend.GetProfileAsync()).Progress.EarnedCoins, Is.Zero, "money never becomes coins");
        // A duplicate callback from the store is harmless.
        Assert.That((await h.Flow.HandleAsync(h.Delivered.Single())).State, Is.EqualTo(PurchaseFlowState.AlreadyOwned));
        Assert.That(h.Backend.Purchases.GetEntitlements(h.Backend.PlayerId).ActiveSkus, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task PendingPaymentStaysOpenUntilCompleted()
    {
        Harness h = Setup();
        h.Store.NextPending = true;
        await h.Flow.BuyAsync("ak.cosmetic.outfit_tide_warden");
        Assert.That(h.Updates.Last().State, Is.EqualTo(PurchaseFlowState.PendingPayment));
        Assert.That(h.Store.OpenTransactions, Has.Count.EqualTo(1));
        Assert.That((await h.Backend.GetLockerAsync()).Entries.Single(e => e.Item.Id == "outfit.tide-warden").Owned, Is.False);
        h.Store.RedeliverOpen(); // the store re-delivers the still-pending purchase at the next start
        Assert.That(h.Updates.Last().State, Is.EqualTo(PurchaseFlowState.PendingPayment));
        h.Store.CompletePending(h.Store.OpenTransactions.Single().PurchaseToken);
        Assert.That(h.Updates.Last().State, Is.EqualTo(PurchaseFlowState.Granted));
        Assert.That(h.Store.OpenTransactions, Is.Empty);
        Assert.That((await h.Backend.GetLockerAsync()).Entries.Single(e => e.Item.Id == "outfit.tide-warden").Owned, Is.True);
    }

    [Test]
    public async Task CancelledPurchaseChangesNothing()
    {
        Harness h = Setup();
        h.Store.NextFailure = StoreFailure.UserCancelled;
        await h.Flow.BuyAsync("ak.cosmetic.sunrise_pack");
        Assert.That(h.Updates.Last().State, Is.EqualTo(PurchaseFlowState.Cancelled));
        Assert.That((await h.Backend.GetLockerAsync()).Entries.Single(e => e.Item.Id == "bow.sunrise").Owned, Is.False);
    }

    [Test]
    public async Task ChildWithoutConsentNeverReachesTheStore()
    {
        Harness h = Setup(age: 12);
        PurchaseFlowUpdate u = await h.Flow.BuyAsync("ak.cosmetic.sunrise_pack");
        Assert.That(u.State, Is.EqualTo(PurchaseFlowState.NotAllowed));
        Assert.That(u.Refusal, Is.EqualTo(OfferState.RequiresParentalConsent));
        Assert.That(h.Delivered, Is.Empty);
        Harness unknown = Setup(age: null);
        Assert.That((await unknown.Flow.BuyAsync("ak.cosmetic.sunrise_pack")).Refusal, Is.EqualTo(OfferState.RequiresAgeCheck));
        Assert.That(unknown.Delivered, Is.Empty);
    }

    [Test]
    public async Task ReinstallRestoreKeepsOwnership()
    {
        Harness h = Setup();
        await h.Flow.BuyAsync("ak.cosmetic.sunrise_pack");
        h.Store.Reinstall();
        await h.Store.InitializeAsync(StoreCatalog.Default.Products.Select(p => p.Sku));
        PlayerEntitlements restored = await h.Flow.RestoreAsync();
        Assert.That(restored.Owns("ak.cosmetic.sunrise_pack"), Is.True);
    }
}
