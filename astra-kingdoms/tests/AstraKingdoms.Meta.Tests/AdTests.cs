using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Server;
using AstraKingdoms.Meta.Shop;
using NUnit.Framework;

namespace AstraKingdoms.Meta.Tests;

/// <summary>Ticket 62: ad policy and the conditional remove-ads entitlement.</summary>
public class AdPolicyTests
{
    private static readonly StoreProduct RemoveAds = new("ak.remove_ads", StoreProductKind.RemoveNonRewardedAds, null, "Remove ads",
        "Removes interstitial ads between matches.", "Optional rewarded offers stay available if you choose to watch them.");

    private static StoreCatalog WithRemoveAds(StoreProduct p) => new(StoreCatalog.Default.Products.Concat(new[] { p }));

    private static AdPolicy NonRewardedApproved() => new()
    {
        NonRewardedAdsApproved = true,
        Placements = new[]
        {
            new AdPlacement("rewarded.home", AdFormat.Rewarded, ScreenContext.Home),
            new AdPlacement("interstitial.after-result", AdFormat.Interstitial, ScreenContext.MatchResult),
        },
    };

    [Test]
    public void V1DefaultHasRewardedOnlyAndNoRemoveAdsSku()
    {
        Assert.That(AdPolicy.V1Default().Placements.All(p => p.Format == AdFormat.Rewarded), Is.True);
        Assert.That(StoreCatalog.Default.Products.Any(p => p.Kind == StoreProductKind.RemoveNonRewardedAds), Is.False);
        Assert.That(CatalogValidator.Validate(StoreCatalog.Default, CosmeticCatalog.Default, AdPolicy.V1Default()), Is.Empty);
    }

    [Test]
    public void RemoveAdsSkuIsRejectedWhileOnlyRewardedAdsExist()
    {
        IReadOnlyList<string> errors = CatalogValidator.Validate(WithRemoveAds(RemoveAds), CosmeticCatalog.Default, AdPolicy.V1Default());
        Assert.That(errors, Has.Some.Contains("no remove-ads product while the only advertising is optional rewarded advertising"));
        // Approval without any non-rewarded placement is still rewarded-only.
        var approvedButNone = new AdPolicy { NonRewardedAdsApproved = true };
        Assert.That(CatalogValidator.Validate(WithRemoveAds(RemoveAds), CosmeticCatalog.Default, approvedButNone), Is.Not.Empty);
    }

    [Test]
    public void RemoveAdsIsValidOnlyWithApprovedNonRewardedAdsAndAClearDescription()
    {
        Assert.That(CatalogValidator.Validate(WithRemoveAds(RemoveAds), CosmeticCatalog.Default, NonRewardedApproved()), Is.Empty);
        var vague = new StoreProduct("ak.remove_ads", StoreProductKind.RemoveNonRewardedAds, null, "Remove ads", "No ads!", "");
        Assert.That(CatalogValidator.Validate(WithRemoveAds(vague), CosmeticCatalog.Default, NonRewardedApproved()),
            Has.Some.Contains("whether optional rewarded offers remain"));
        var consumable = new StoreProduct("ak.remove_ads", StoreProductKind.RemoveNonRewardedAds, null, "Remove ads", "d", "rewarded remain", consumable: true);
        Assert.That(CatalogValidator.Validate(WithRemoveAds(consumable), CosmeticCatalog.Default, NonRewardedApproved()), Has.Some.Contains("non-consumable"));
    }

    [Test]
    public void PlacementsNeverInsideMatchesAndNonRewardedNeedsApproval()
    {
        var policy = new AdPolicy
        {
            Placements = new[]
            {
                new AdPlacement("bad.rewarded", AdFormat.Rewarded, ScreenContext.InMatch),
                new AdPlacement("bad.banner", AdFormat.Banner, ScreenContext.Home),
            },
        };
        IReadOnlyList<string> errors = CatalogValidator.Validate(StoreCatalog.Default, CosmeticCatalog.Default, policy);
        Assert.That(errors, Has.Some.Contains("never placed inside a match"));
        Assert.That(errors, Has.Some.Contains("non-rewarded placement without approval"));
    }

    [Test]
    public async Task RemoveAdsEntitlementSuppressesNonRewardedAdsAcrossReinstalls()
    {
        StoreCatalog catalog = WithRemoveAds(RemoveAds);
        AdPolicy policy = NonRewardedApproved();
        AdPlacement interstitial = policy.Placements[1];
        var clock = T0.Clock();
        var play = new FakePurchaseVerifier();
        var ledger = new InMemoryEntitlementLedgerStore();
        var options = new BillingOptions { AccountIdSalt = "s" };
        var svc = new PurchaseService(play, ledger, new InMemoryAcknowledgementQueue(), catalog, clock, options);

        Assert.That(AdRules.MayShowNonRewarded(policy, catalog, interstitial, ScreenContext.MatchResult, svc.GetEntitlements("p")), Is.True);
        Assert.That(AdRules.MayShowNonRewarded(policy, catalog, interstitial, ScreenContext.InMatch, svc.GetEntitlements("p")), Is.False);
        play.AddPurchase("ak.remove_ads", "tok", new ProductPurchase(PlayPurchaseState.Purchased, AcknowledgementState.NotAcknowledged, 0, "o", clock.UtcNow, null,
            ObfuscatedAccountId.For("p", "s")));
        await svc.HandlePurchaseAsync("p", "ak.remove_ads", "tok");
        Assert.That(AdRules.MayShowNonRewarded(policy, catalog, interstitial, ScreenContext.MatchResult, svc.GetEntitlements("p")), Is.False);

        // Reinstall: the device has no local state; the server ledger still holds the entitlement.
        var afterReinstall = new PurchaseService(play, ledger, new InMemoryAcknowledgementQueue(), catalog, clock, options);
        PlayerEntitlements restored = await afterReinstall.RestoreAsync("p", Array.Empty<KeyValuePair<string, string>>());
        Assert.That(AdRules.MayShowNonRewarded(policy, catalog, interstitial, ScreenContext.MatchResult, restored), Is.False);
        // Rewarded offers remain available (as the product description promises).
        Assert.That(AdRules.CanOfferRewarded(policy, ScreenContext.Home, AudienceProfile.Adult, 0), Is.EqualTo(OfferDecision.Available));
    }

    [Test]
    public void NonRewardedNeverShowsInV1()
    {
        var banner = new AdPlacement("x", AdFormat.Interstitial, ScreenContext.Home);
        Assert.That(AdRules.MayShowNonRewarded(AdPolicy.V1Default(), StoreCatalog.Default, banner, ScreenContext.Home, PlayerEntitlements.None), Is.False);
    }

    [Test]
    public void RequestFlagsFollowTheAudience()
    {
        AdRequestOptions child = AdRequestOptions.For(AudienceProfile.Child(), true);
        Assert.That(child.NonPersonalized && child.TagForChildDirectedTreatment && child.TagForUnderAgeOfConsent, Is.True);
        Assert.That(child.MaxAdContentRating, Is.EqualTo("G"));
        AdRequestOptions unknown = AdRequestOptions.For(AudienceProfile.Unknown, true);
        Assert.That(unknown.NonPersonalized && unknown.TagForUnderAgeOfConsent, Is.True);
        Assert.That(unknown.TagForChildDirectedTreatment, Is.False);
        Assert.That(AdRequestOptions.For(AudienceProfile.Adult, false).NonPersonalized, Is.True);
        Assert.That(AdRequestOptions.For(AudienceProfile.Adult, true).NonPersonalized, Is.False);
    }
}

/// <summary>Ticket 63: rewarded ads outside matches; verified rewards are granted once; decline/failure is safe.</summary>
public class RewardedAdTests
{
    private sealed class Presence : IMatchPresence
    {
        public bool InMatch;
        public bool IsInActiveMatch(string playerId) => InMatch;
    }

    private ManualClock _clock;
    private InMemoryRewardLedgerStore _ledger;
    private Presence _presence;
    private RewardedAdService _svc;
    private AdPolicy _policy;

    [SetUp]
    public void SetUp()
    {
        _clock = T0.Clock();
        _ledger = new InMemoryRewardLedgerStore();
        _presence = new Presence();
        _policy = AdPolicy.V1Default();
        _svc = new RewardedAdService(_ledger, new InMemoryAdTicketStore(), _presence, _clock, _policy);
    }

    private SsvCallback Callback(AdOfferTicket t, string txn, string user = null) =>
        new(txn, user ?? t.PlayerId, t.TicketId, "unit", 10, "coins", _clock.UtcNow);

    [Test]
    public void NeverOfferedDuringAMatch()
    {
        _presence.InMatch = true;
        OfferResult r = _svc.IssueOffer("p", ScreenContext.Home, AudienceProfile.Adult, false);
        Assert.That(r.Decision, Is.EqualTo(OfferDecision.InMatch));
        Assert.That(r.Ticket, Is.Null);
        _presence.InMatch = false;
        Assert.That(_svc.IssueOffer("p", ScreenContext.InMatch, AudienceProfile.Adult, false).Decision, Is.EqualTo(OfferDecision.InMatch));
        Assert.That(_svc.IssueOffer("p", ScreenContext.Profile, AudienceProfile.Adult, false).Decision, Is.EqualTo(OfferDecision.NoPlacementHere));
    }

    [Test]
    public void ChildAndUnknownAgeNeedFamiliesCertifiedSdkThenGetNonPersonalisedRequests()
    {
        Assert.That(_svc.IssueOffer("kid", ScreenContext.Home, AudienceProfile.Child(), true).Decision, Is.EqualTo(OfferDecision.AudienceNotSupported));
        Assert.That(_svc.IssueOffer("anon", ScreenContext.Home, AudienceProfile.Unknown, true).Decision, Is.EqualTo(OfferDecision.AudienceNotSupported));
        _policy.FamiliesCertifiedSdkConfirmed = true;
        OfferResult r = _svc.IssueOffer("kid", ScreenContext.Home, AudienceProfile.Child(), true);
        Assert.That(r.Decision, Is.EqualTo(OfferDecision.Available));
        Assert.That(r.Ticket.RequestOptions.NonPersonalized, Is.True);
        Assert.That(r.Ticket.RequestOptions.TagForChildDirectedTreatment, Is.True);
    }

    [Test]
    public void VerifiedRewardIsGrantedOnceEvenWithRetriesAndNewTransactionIds()
    {
        AdOfferTicket t = _svc.IssueOffer("p", ScreenContext.Home, AudienceProfile.Adult, false).Ticket;
        Assert.That(_svc.GetTicketStatus("p", t.TicketId), Is.EqualTo(TicketStatus.Open));
        Assert.That(_svc.HandleVerifiedCallback(Callback(t, "txn-1")), Is.EqualTo(CallbackOutcome.Granted));
        Assert.That(_svc.HandleVerifiedCallback(Callback(t, "txn-1")), Is.EqualTo(CallbackOutcome.Duplicate));
        Assert.That(_svc.HandleVerifiedCallback(Callback(t, "txn-2")), Is.EqualTo(CallbackOutcome.Duplicate));
        Assert.That(_ledger.Totals("p").Coins, Is.EqualTo(10));
        Assert.That(_svc.GetTicketStatus("p", t.TicketId), Is.EqualTo(TicketStatus.Rewarded));
        Assert.That(_svc.GetTicketStatus("someone-else", t.TicketId), Is.EqualTo(TicketStatus.Unknown));
    }

    [Test]
    public void ConcurrentCallbacksGrantOnce()
    {
        AdOfferTicket t = _svc.IssueOffer("p", ScreenContext.Home, AudienceProfile.Adult, false).Ticket;
        var outcomes = new CallbackOutcome[32];
        Parallel.For(0, outcomes.Length, i => outcomes[i] = _svc.HandleVerifiedCallback(Callback(t, "txn-" + i)));
        Assert.That(outcomes.Count(o => o == CallbackOutcome.Granted), Is.EqualTo(1));
        Assert.That(_ledger.Totals("p").Coins, Is.EqualTo(10));
    }

    [Test]
    public void WrongUserAndUnknownTicketsAreRefused()
    {
        AdOfferTicket t = _svc.IssueOffer("p", ScreenContext.Home, AudienceProfile.Adult, false).Ticket;
        Assert.That(_svc.HandleVerifiedCallback(Callback(t, "x", "mallory")), Is.EqualTo(CallbackOutcome.WrongUser));
        Assert.That(_svc.HandleVerifiedCallback(new SsvCallback("x", "p", "forged", "u", 10, "coins", _clock.UtcNow)), Is.EqualTo(CallbackOutcome.UnknownTicket));
        Assert.That(_ledger.Totals("p").Coins, Is.Zero);
    }

    [Test]
    public void TicketExpiryBoundary()
    {
        AdOfferTicket t = _svc.IssueOffer("p", ScreenContext.Home, AudienceProfile.Adult, false).Ticket;
        DateTimeOffset closes = t.ExpiresAt + _svc.CallbackGrace;
        _clock.Set(closes.AddTicks(-1));
        Assert.That(_svc.GetTicketStatus("p", t.TicketId), Is.EqualTo(TicketStatus.Open));
        AdOfferTicket t2 = _svc.IssueOffer("p", ScreenContext.Home, AudienceProfile.Adult, false).Ticket;
        _clock.Set(closes);
        Assert.That(_svc.HandleVerifiedCallback(Callback(t, "late")), Is.EqualTo(CallbackOutcome.TicketExpired));
        Assert.That(_svc.GetTicketStatus("p", t.TicketId), Is.EqualTo(TicketStatus.Expired));
        Assert.That(_svc.HandleVerifiedCallback(Callback(t2, "ok")), Is.EqualTo(CallbackOutcome.Granted));
    }

    [Test]
    public void DailyCapStopsOffersAndGrants()
    {
        var tickets = Enumerable.Range(0, _policy.MaxRewardedPerDay + 1)
            .Select(_ => _svc.IssueOffer("p", ScreenContext.Home, AudienceProfile.Adult, false).Ticket).ToList();
        for (int i = 0; i < _policy.MaxRewardedPerDay; i++) Assert.That(_svc.HandleVerifiedCallback(Callback(tickets[i], "t" + i)), Is.EqualTo(CallbackOutcome.Granted));
        Assert.That(_svc.HandleVerifiedCallback(Callback(tickets.Last(), "extra")), Is.EqualTo(CallbackOutcome.DailyCapReached));
        Assert.That(_svc.IssueOffer("p", ScreenContext.Home, AudienceProfile.Adult, false).Decision, Is.EqualTo(OfferDecision.DailyCapReached));
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.That(_svc.IssueOffer("p", ScreenContext.Home, AudienceProfile.Adult, false).Decision, Is.EqualTo(OfferDecision.Available));
    }

    [Test]
    public async Task DeclineAndFailureAreSafe()
    {
        var backend = new Client.LocalMetaBackend(new Client.MemoryPersistence(), _clock, () => false);
        await backend.SetDeclaredAgeAsync(30);
        var provider = new FakeRewardedAdProvider { NextShow = AdShowOutcome.Dismissed };
        var flow = new Client.RewardedAdFlow(provider, backend, () => backend.PlayerId) { PollDelay = TimeSpan.Zero, StatusPolls = 2 };
        var declined = await flow.RunAsync(ScreenContext.Home, false);
        Assert.That(declined.result, Is.EqualTo(Client.AdFlowResult.Declined));
        provider.NextLoad = AdLoadResult.NoFill;
        Assert.That((await flow.RunAsync(ScreenContext.Home, false)).result, Is.EqualTo(Client.AdFlowResult.NoFill));
        provider.NextLoad = AdLoadResult.Loaded;
        provider.NextShow = AdShowOutcome.Failed;
        Assert.That((await flow.RunAsync(ScreenContext.Home, false)).result, Is.EqualTo(Client.AdFlowResult.Failed));
        Assert.That((await backend.GetProfileAsync()).Progress.EarnedCoins, Is.Zero, "no callback, no reward, no penalty");

        provider.NextShow = AdShowOutcome.EarnedReward;
        var earned = await flow.RunAsync(ScreenContext.Home, false);
        Assert.That(earned.result, Is.EqualTo(Client.AdFlowResult.AwaitingVerification), "the device's 'earned' signal grants nothing by itself");
        Assert.That(provider.LastCustomData, Is.EqualTo(earned.ticketId));
        Assert.That(provider.LastOptions.NonPersonalized, Is.True);
        Assert.That(backend.DevelopmentSimulateVerifiedCallback(earned.ticketId, "txn"), Is.EqualTo(CallbackOutcome.Granted));
        Assert.That((await backend.GetProfileAsync()).Progress.EarnedCoins, Is.EqualTo(10));
    }
}

/// <summary>Ticket 63: SSV callback signature verification with locally generated P-256 keys.</summary>
public class SsvSignatureTests
{
    private sealed class Keys : IAdVerifierKeyProvider
    {
        public Dictionary<long, byte[]> Current = new();
        public Dictionary<long, byte[]> AfterRefresh;
        public int Refreshes;

        public Task<IReadOnlyDictionary<long, byte[]>> GetKeysAsync(bool forceRefresh, CancellationToken ct = default)
        {
            if (forceRefresh)
            {
                Refreshes++;
                if (AfterRefresh != null) Current = AfterRefresh;
            }
            return Task.FromResult<IReadOnlyDictionary<long, byte[]>>(Current);
        }
    }

    private ECDsa _key;
    private Keys _keys;
    private ManualClock _clock;
    private SsvSignatureVerifier _verifier;

    [SetUp]
    public void SetUp()
    {
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _keys = new Keys();
        _keys.Current[3335741209] = _key.ExportSubjectPublicKeyInfo();
        _clock = T0.Clock();
        _verifier = new SsvSignatureVerifier(_keys, _clock);
    }

    [TearDown]
    public void TearDown() => _key.Dispose();

    private string Message(string amount = "10", DateTimeOffset? at = null) =>
        "ad_network=5450213213286189855&ad_unit=1234567890&custom_data=ticket%2Dabc&reward_amount=" + amount +
        "&reward_item=coins&timestamp=" + (at ?? _clock.UtcNow).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
        "&transaction_id=123456789&user_id=guest-1";

    private string Signed(string message, ECDsa key = null, long keyId = 3335741209)
    {
        byte[] p1363 = (key ?? _key).SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256);
        return message + "&signature=" + Base64Url.Encode(EcdsaSignature.P1363ToDer(p1363)) + "&key_id=" + keyId.ToString(CultureInfo.InvariantCulture);
    }

    [Test]
    public async Task ValidCallbackVerifiesAndParses()
    {
        SsvVerification v = await _verifier.VerifyAsync(Signed(Message()));
        Assert.That(v.Status, Is.EqualTo(SsvVerificationStatus.Verified));
        Assert.That(v.Callback.CustomData, Is.EqualTo("ticket-abc"));
        Assert.That(v.Callback.UserId, Is.EqualTo("guest-1"));
        Assert.That(v.Callback.TransactionId, Is.EqualTo("123456789"));
        Assert.That(v.Callback.RewardAmount, Is.EqualTo(10));
        Assert.That(_keys.Refreshes, Is.Zero);
        Assert.That((await _verifier.VerifyAsync("?" + Signed(Message()))).Status, Is.EqualTo(SsvVerificationStatus.Verified));
    }

    [Test]
    public async Task TamperingBreaksTheSignature()
    {
        string signed = Signed(Message());
        string tampered = signed.Replace("reward_amount=10", "reward_amount=99", StringComparison.Ordinal);
        Assert.That((await _verifier.VerifyAsync(tampered)).Status, Is.EqualTo(SsvVerificationStatus.BadSignature));
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.That((await _verifier.VerifyAsync(Signed(Message(), other))).Status, Is.EqualTo(SsvVerificationStatus.BadSignature));
    }

    [Test]
    public async Task UnknownKeyTriggersOneRefreshAndRotationIsPickedUp()
    {
        using ECDsa rotated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string signed = Signed(Message(), rotated, 42);
        Assert.That((await _verifier.VerifyAsync(signed)).Status, Is.EqualTo(SsvVerificationStatus.UnknownKey));
        Assert.That(_keys.Refreshes, Is.EqualTo(1));
        _keys.AfterRefresh = new Dictionary<long, byte[]> { { 42, rotated.ExportSubjectPublicKeyInfo() } };
        Assert.That((await _verifier.VerifyAsync(signed)).Status, Is.EqualTo(SsvVerificationStatus.Verified));
    }

    [Test]
    public async Task StaleAndFutureTimestampsAreRejected()
    {
        Assert.That((await _verifier.VerifyAsync(Signed(Message(at: _clock.UtcNow.AddHours(-1).AddMilliseconds(-1))))).Status, Is.EqualTo(SsvVerificationStatus.Stale));
        Assert.That((await _verifier.VerifyAsync(Signed(Message(at: _clock.UtcNow.AddHours(-1))))).Status, Is.EqualTo(SsvVerificationStatus.Verified));
        Assert.That((await _verifier.VerifyAsync(Signed(Message(at: _clock.UtcNow.AddMinutes(6))))).Status, Is.EqualTo(SsvVerificationStatus.Stale));
    }

    [TestCase("")]
    [TestCase("user_id=x")]
    [TestCase("user_id=x&signature=@@@&key_id=1")]
    [TestCase("user_id=x&signature=MEUCIQ&key_id=notanumber")]
    public async Task MalformedQueriesAreRejected(string q)
    {
        SsvVerificationStatus s = (await _verifier.VerifyAsync(q)).Status;
        Assert.That(s, Is.EqualTo(SsvVerificationStatus.Malformed).Or.EqualTo(SsvVerificationStatus.BadSignature));
    }

    [Test]
    public void DerAndP1363RoundTrip()
    {
        for (int i = 0; i < 50; i++)
        {
            byte[] sig = _key.SignData(BitConverter.GetBytes(i), HashAlgorithmName.SHA256);
            Assert.That(EcdsaSignature.DerToP1363(EcdsaSignature.P1363ToDer(sig), 32), Is.EqualTo(sig));
        }
        byte[] leadingZeros = new byte[64];
        leadingZeros[31] = 1;
        leadingZeros[63] = 0x80;
        Assert.That(EcdsaSignature.DerToP1363(EcdsaSignature.P1363ToDer(leadingZeros), 32), Is.EqualTo(leadingZeros));
        Assert.That(EcdsaSignature.DerToP1363(new byte[] { 0x30, 0x02, 0x02, 0x00 }, 32), Is.Null);
    }

    [Test]
    public async Task HttpKeyProviderParsesTheAdMobKeyDocumentAndCaches()
    {
        var http = new ScriptedHttp();
        string b64 = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        http.Handler = _ => new HttpReply(200, "{\"keys\":[{\"keyId\":3335741209,\"pem\":\"-----BEGIN PUBLIC KEY-----\\n" + b64 + "\\n-----END PUBLIC KEY-----\",\"base64\":\"" + b64 + "\"}," +
                                               "{\"keyId\":7,\"pem\":\"garbage\"}]}");
        var provider = new HttpAdVerifierKeyProvider(http, _clock);
        IReadOnlyDictionary<long, byte[]> keys = await provider.GetKeysAsync(false);
        Assert.That(keys.Keys, Is.EquivalentTo(new long[] { 3335741209 }));
        await provider.GetKeysAsync(false);
        await provider.GetKeysAsync(true); // forced refresh within the minimum interval is throttled
        Assert.That(http.Calls, Has.Count.EqualTo(1));
        Assert.That(http.Calls[0].Url, Is.EqualTo(HttpAdVerifierKeyProvider.AdMobKeysUrl));
        _clock.Advance(TimeSpan.FromMinutes(2));
        await provider.GetKeysAsync(true);
        Assert.That(http.Calls, Has.Count.EqualTo(2));
        http.Handler = _ => new HttpReply(503, "");
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.That((await provider.GetKeysAsync(false)).ContainsKey(3335741209), Is.True, "an outage keeps the last good keys");

        var verifier = new SsvSignatureVerifier(provider, _clock);
        Assert.That((await verifier.VerifyAsync(Signed(Message()))).Status, Is.EqualTo(SsvVerificationStatus.Verified));
    }

    [Test]
    public async Task VerifiedCallbackFeedsTheRewardService()
    {
        var ledger = new InMemoryRewardLedgerStore();
        var presence = new FixedPresence();
        var svc = new RewardedAdService(ledger, new InMemoryAdTicketStore(), presence, _clock, AdPolicy.V1Default());
        AdOfferTicket t = svc.IssueOffer("guest-1", ScreenContext.Home, AudienceProfile.Adult, false).Ticket;
        string msg = Message().Replace("custom_data=ticket%2Dabc", "custom_data=" + t.TicketId, StringComparison.Ordinal);
        SsvVerification v = await _verifier.VerifyAsync(Signed(msg));
        Assert.That(svc.HandleVerifiedCallback(v.Callback), Is.EqualTo(CallbackOutcome.Granted));
        Assert.That(svc.HandleVerifiedCallback(v.Callback), Is.EqualTo(CallbackOutcome.Duplicate));
        Assert.That(ledger.Totals("guest-1").Coins, Is.EqualTo(10));
    }

    private sealed class FixedPresence : IMatchPresence
    {
        public bool IsInActiveMatch(string playerId) => false;
    }
}
