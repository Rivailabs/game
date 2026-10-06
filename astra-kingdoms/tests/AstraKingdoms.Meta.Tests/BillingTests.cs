using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Shop;
using NUnit.Framework;

namespace AstraKingdoms.Meta.Tests;

/// <summary>Ticket 61: verified grants, pending, duplicates, restore, revocation, acknowledgement, append-only ledger.</summary>
public class BillingTests
{
    private const string Sku = "ak.cosmetic.sunrise_pack";
    private const string Salt = "test-salt";
    private ManualClock _clock;
    private FakePurchaseVerifier _play;
    private InMemoryEntitlementLedgerStore _ledger;
    private InMemoryAcknowledgementQueue _acks;
    private PurchaseService _svc;
    private readonly List<PurchaseStateReport> _states = new();

    [SetUp]
    public void SetUp()
    {
        _clock = T0.Clock();
        _play = new FakePurchaseVerifier();
        _ledger = new InMemoryEntitlementLedgerStore();
        _acks = new InMemoryAcknowledgementQueue();
        _svc = NewService();
        _states.Clear();
        _svc.PurchaseStateChanged += _states.Add;
    }

    private PurchaseService NewService(BillingOptions options = null) =>
        new PurchaseService(_play, _ledger, _acks, StoreCatalog.Default, _clock, options ?? new BillingOptions { AccountIdSalt = Salt });

    private ProductPurchase Purchase(string player, PlayPurchaseState state = PlayPurchaseState.Purchased, int? type = null,
        AcknowledgementState ack = AcknowledgementState.NotAcknowledged) =>
        new ProductPurchase(state, ack, 0, "GPA.1", _clock.UtcNow, type, player == null ? null : ObfuscatedAccountId.For(player, Salt), "IN");

    [Test]
    public async Task VerifiedPurchaseGrantsAndAcknowledges()
    {
        _play.AddPurchase(Sku, "tok", Purchase("alice"));
        PurchaseResult r = await _svc.HandlePurchaseAsync("alice", Sku, "tok");
        Assert.That(r.Status, Is.EqualTo(PurchaseStatus.Granted));
        Assert.That(r.Acknowledged, Is.True);
        Assert.That(r.ClientMayFinishTransaction, Is.True);
        Assert.That(_play.AcknowledgementOf("tok"), Is.EqualTo(AcknowledgementState.Acknowledged));
        Assert.That(_svc.GetEntitlements("alice").Owns(Sku), Is.True);
        Assert.That(_states.Single().Status, Is.EqualTo(PurchaseStatus.Granted));
    }

    [Test]
    public async Task DuplicateCallbacksGrantOnceAndDoNotReacknowledge()
    {
        _play.AddPurchase(Sku, "tok", Purchase("alice"));
        await _svc.HandlePurchaseAsync("alice", Sku, "tok");
        PurchaseResult again = await _svc.HandlePurchaseAsync("alice", Sku, "tok");
        Assert.That(again.Status, Is.EqualTo(PurchaseStatus.AlreadyOwned));
        Assert.That(again.Acknowledged, Is.True);
        Assert.That(_play.AcknowledgeCalls, Is.EqualTo(1));
        Assert.That(_ledger.All(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentCallbacksWriteOneGrant()
    {
        _play.AddPurchase(Sku, "tok", Purchase("alice"));
        PurchaseResult[] results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => _svc.HandlePurchaseAsync("alice", Sku, "tok"))));
        Assert.That(results.Count(r => r.Status == PurchaseStatus.Granted), Is.EqualTo(1));
        Assert.That(results.All(r => r.Status == PurchaseStatus.Granted || r.Status == PurchaseStatus.AlreadyOwned), Is.True);
        Assert.That(_ledger.All().Count(e => e.Action == EntitlementAction.Grant), Is.EqualTo(1));
    }

    [Test]
    public async Task PendingGrantsNothingUntilPaymentCompletes()
    {
        _play.AddPurchase(Sku, "tok", Purchase("alice", PlayPurchaseState.Pending));
        PurchaseResult pending = await _svc.HandlePurchaseAsync("alice", Sku, "tok");
        Assert.That(pending.Status, Is.EqualTo(PurchaseStatus.Pending));
        Assert.That(pending.ClientMayFinishTransaction, Is.False);
        Assert.That(_svc.GetEntitlements("alice").Owns(Sku), Is.False);
        Assert.That(_play.AcknowledgeCalls, Is.Zero, "pending purchases cannot be acknowledged");
        _play.CompletePending("tok");
        Assert.That((await _svc.HandlePurchaseAsync("alice", Sku, "tok")).Status, Is.EqualTo(PurchaseStatus.Granted));
    }

    [Test]
    public async Task CancelledInvalidAndUnknownProducts()
    {
        _play.AddPurchase(Sku, "cancelled", Purchase("alice", PlayPurchaseState.Canceled));
        Assert.That((await _svc.HandlePurchaseAsync("alice", Sku, "cancelled")).Status, Is.EqualTo(PurchaseStatus.Canceled));
        Assert.That((await _svc.HandlePurchaseAsync("alice", Sku, "forged")).Status, Is.EqualTo(PurchaseStatus.InvalidToken));
        Assert.That((await _svc.HandlePurchaseAsync("alice", "ak.unknown", "x")).Status, Is.EqualTo(PurchaseStatus.UnknownProduct));
        Assert.That((await _svc.HandlePurchaseAsync("alice", Sku, "")).Status, Is.EqualTo(PurchaseStatus.InvalidToken));
        Assert.That(_ledger.All(), Is.Empty);
    }

    [Test]
    public async Task StoreOutageDefersWithoutGranting()
    {
        _play.AddPurchase(Sku, "tok", Purchase("alice"));
        _play.FailNextVerifications = 1;
        PurchaseResult r = await _svc.HandlePurchaseAsync("alice", Sku, "tok");
        Assert.That(r.Status, Is.EqualTo(PurchaseStatus.RetryLater));
        Assert.That(r.ClientMayFinishTransaction, Is.False);
        Assert.That(_ledger.All(), Is.Empty);
        Assert.That((await _svc.HandlePurchaseAsync("alice", Sku, "tok")).Status, Is.EqualTo(PurchaseStatus.Granted));
    }

    [Test]
    public async Task TokensAreBoundToTheBuyingAccount()
    {
        _play.AddPurchase(Sku, "tok", Purchase("alice"));
        Assert.That((await _svc.HandlePurchaseAsync("mallory", Sku, "tok")).Status, Is.EqualTo(PurchaseStatus.AccountMismatch));
        Assert.That((await _svc.HandlePurchaseAsync("alice", Sku, "tok")).Status, Is.EqualTo(PurchaseStatus.Granted));
        Assert.That((await _svc.HandlePurchaseAsync("mallory", Sku, "tok")).Status, Is.EqualTo(PurchaseStatus.AccountMismatch));
        Assert.That(_svc.GetEntitlements("mallory").ActiveSkus, Is.Empty);

        _play.AddPurchase(Sku, "anon", Purchase(null));
        Assert.That((await _svc.HandlePurchaseAsync("alice", Sku, "anon")).Status, Is.EqualTo(PurchaseStatus.AccountMismatch));
    }

    [Test]
    public async Task TestPurchasesCanBeRefusedAndAreFlagged()
    {
        _play.AddPurchase(Sku, "test", Purchase("alice", type: 0));
        var strict = NewService(new BillingOptions { AccountIdSalt = Salt, AcceptTestPurchases = false });
        Assert.That((await strict.HandlePurchaseAsync("alice", Sku, "test")).Status, Is.EqualTo(PurchaseStatus.TestPurchaseNotAccepted));
        PurchaseResult ok = await _svc.HandlePurchaseAsync("alice", Sku, "test");
        Assert.That(ok.IsTestPurchase, Is.True);
        Assert.That(_ledger.All().Single().IsTestPurchase, Is.True);
        Assert.That(_states.Last().IsTestPurchase, Is.True);
    }

    [Test]
    public async Task FailedAcknowledgementIsRetriedAndFlaggedNearTheThreeDayDeadline()
    {
        _play.AddPurchase(Sku, "tok", Purchase("alice"));
        _play.FailNextAcknowledgements = 3;
        PurchaseResult r = await _svc.HandlePurchaseAsync("alice", Sku, "tok");
        Assert.That(r.Status, Is.EqualTo(PurchaseStatus.Granted), "the entitlement is granted even if acknowledgement must be retried");
        Assert.That(r.Acknowledged, Is.False);
        PendingAcknowledgement pending = _acks.All().Single();
        Assert.That(pending.Deadline, Is.EqualTo(T0.Noon.AddDays(3)));

        Assert.That(await _svc.RetryAcknowledgementsAsync(), Is.Empty, "not yet at risk");
        _clock.Set(T0.Noon.AddDays(2).AddHours(1));
        IReadOnlyList<PendingAcknowledgement> atRisk = await _svc.RetryAcknowledgementsAsync();
        Assert.That(atRisk.Single().PurchaseToken, Is.EqualTo("tok"));
        Assert.That(atRisk.Single().Attempts, Is.EqualTo(2));
        Assert.That(await _svc.RetryAcknowledgementsAsync(), Is.Empty);
        Assert.That(_acks.All(), Is.Empty);
        Assert.That(_play.AcknowledgementOf("tok"), Is.EqualTo(AcknowledgementState.Acknowledged));
    }

    [Test]
    public async Task AlreadyAcknowledgedPurchasesAreNotAcknowledgedAgain()
    {
        _play.AddPurchase(Sku, "tok", Purchase("alice", ack: AcknowledgementState.Acknowledged));
        PurchaseResult r = await _svc.HandlePurchaseAsync("alice", Sku, "tok");
        Assert.That(r.Acknowledged, Is.True);
        Assert.That(_play.AcknowledgeCalls, Is.Zero);
    }

    [Test]
    public async Task VoidedPurchasesAreRevokedOnceAcrossPagesAndCannotBeReplayed()
    {
        for (int i = 0; i < 5; i++)
        {
            _play.AddPurchase(Sku, "t" + i, Purchase("p" + i));
            await _svc.HandlePurchaseAsync("p" + i, Sku, "t" + i);
        }
        _play.Void("t1", T0.Noon.AddHours(1));
        _play.Void("t3", T0.Noon.AddHours(2), reason: 7);
        _play.Void("never-granted", T0.Noon.AddHours(2));
        _clock.Advance(TimeSpan.FromHours(3));
        Assert.That(await _svc.ProcessVoidedPurchasesAsync(T0.Noon), Is.EqualTo(3));
        Assert.That(await _svc.ProcessVoidedPurchasesAsync(T0.Noon), Is.Zero, "idempotent");
        Assert.That(_svc.GetEntitlements("p1").Owns(Sku), Is.False);
        Assert.That(_svc.GetEntitlements("p3").Owns(Sku), Is.False);
        Assert.That(_svc.GetEntitlements("p2").Owns(Sku), Is.True);
        Assert.That((await _svc.HandlePurchaseAsync("p1", Sku, "t1")).Status, Is.EqualTo(PurchaseStatus.Revoked));
        _play.AddPurchase(Sku, "never-granted", Purchase("p9"));
        Assert.That((await _svc.HandlePurchaseAsync("p9", Sku, "never-granted")).Status, Is.EqualTo(PurchaseStatus.Revoked));
        Assert.That(_states.Count(s => s.Status == PurchaseStatus.Revoked && s.PlayerId == "p1"), Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task LedgerIsAppendOnlyWithCorrectiveTransactions()
    {
        _play.AddPurchase(Sku, "tok", Purchase("alice"));
        await _svc.HandlePurchaseAsync("alice", Sku, "tok");
        EntitlementEntry grant = _ledger.All().Single();
        _svc.Revoke("tok", null, EntitlementReason.Voided, "mistaken refund report");
        Assert.That(_svc.GetEntitlements("alice").Owns(Sku), Is.False);
        Assert.That(_svc.CorrectiveGrant("alice", Sku, "SUP-17", "support verified payment"), Is.True);
        Assert.That(_svc.CorrectiveGrant("alice", Sku, "SUP-17", "duplicate submit"), Is.False);
        Assert.That(_svc.GetEntitlements("alice").Owns(Sku), Is.True);
        IReadOnlyList<EntitlementEntry> all = _ledger.All();
        Assert.That(all.Select(e => e.Action), Is.EqualTo(new[] { EntitlementAction.Grant, EntitlementAction.Revoke, EntitlementAction.Grant }));
        Assert.That(all[0], Is.SameAs(grant), "earlier lines are never rewritten");
        Assert.That(all.Select(e => e.Sequence), Is.EqualTo(new long[] { 1, 2, 3 }));
    }

    [Test]
    public async Task RestoreAfterReinstallUsesServerOwnership()
    {
        _play.AddPurchase(Sku, "tok", Purchase("alice"));
        await _svc.HandlePurchaseAsync("alice", Sku, "tok");
        // A new service instance over the same durable ledger = a server restart; the device sends nothing (fresh install).
        var afterReinstall = NewService();
        PlayerEntitlements ent = await afterReinstall.RestoreAsync("alice", Array.Empty<KeyValuePair<string, string>>());
        Assert.That(ent.Owns(Sku), Is.True);
        // Restoring with the device's token is harmless.
        ent = await afterReinstall.RestoreAsync("alice", new[] { new KeyValuePair<string, string>(Sku, "tok") });
        Assert.That(ent.Owns(Sku), Is.True);
        Assert.That(_ledger.All(), Has.Count.EqualTo(1));
    }

    [Test]
    public void PaidEntitlementsNeverTouchEarnedCoins()
    {
        // The entitlement ledger has no coin field at all; the reward ledger has no purchase source.
        Assert.That(typeof(EntitlementEntry).GetProperties().Any(p => p.Name.Contains("Coin", StringComparison.Ordinal)), Is.False);
        Assert.That(Enum.GetNames(typeof(LedgerSource)).Any(n => n.Contains("Paid", StringComparison.Ordinal) || n.Contains("Iap", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public void ObfuscatedAccountIdIsStableSaltedAndWithinPlayLimits()
    {
        string a = ObfuscatedAccountId.For("alice", "s1");
        Assert.That(a, Has.Length.EqualTo(64));
        Assert.That(ObfuscatedAccountId.For("alice", "s1"), Is.EqualTo(a));
        Assert.That(ObfuscatedAccountId.For("alice", "s2"), Is.Not.EqualTo(a));
        Assert.That(a, Does.Not.Contain("alice"));
    }
}
