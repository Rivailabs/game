using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Shop;

namespace AstraKingdoms.Meta.Client
{
    public enum PurchaseFlowState : byte
    {
        /// <summary>The server refused before any payment (age check, consent, owned, unavailable).</summary>
        NotAllowed = 0,
        /// <summary>Waiting for the store UI.</summary>
        InStore = 1,
        /// <summary>Payment pending (e.g. cash): the item will arrive when the payment completes.</summary>
        PendingPayment = 2,
        Granted = 3,
        /// <summary>Already owned (duplicate callback or restore).</summary>
        AlreadyOwned = 4,
        /// <summary>Cancelled by the player or declined by the store: nothing charged, nothing granted.</summary>
        Cancelled = 5,
        /// <summary>Server could not verify yet; the transaction stays open and is retried.</summary>
        VerificationDeferred = 6,
        Failed = 7,
    }

    /// <summary>Progress of one paid purchase, for the shop UI.</summary>
    public sealed class PurchaseFlowUpdate
    {
        public string Sku { get; }
        public PurchaseFlowState State { get; }
        public OfferState? Refusal { get; }
        public string Detail { get; }

        public PurchaseFlowUpdate(string sku, PurchaseFlowState state, OfferState? refusal = null, string detail = null)
        {
            Sku = sku;
            State = state;
            Refusal = refusal;
            Detail = detail;
        }
    }

    /// <summary>
    /// Client purchase orchestration (ticket 61, device side): ask the backend for authorisation (which
    /// enforces audience rules and returns the obfuscated account id), open the store, send every
    /// delivered transaction to the backend, and finish it only when the backend says so. Pending
    /// and unverifiable transactions stay open, so the store re-delivers them later.
    /// </summary>
    public sealed class ClientPurchaseFlow : IDisposable
    {
        private readonly IStoreBridge _store;
        private readonly IMetaBackend _backend;
        private readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.Ordinal);

        public event Action<PurchaseFlowUpdate> Updated;

        public ClientPurchaseFlow(IStoreBridge store, IMetaBackend backend)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _store.PurchaseUpdated += OnPurchaseUpdated;
            _store.PurchaseFailed += OnPurchaseFailed;
        }

        public void Dispose()
        {
            _store.PurchaseUpdated -= OnPurchaseUpdated;
            _store.PurchaseFailed -= OnPurchaseFailed;
        }

        /// <summary>Localised prices for the shelf (never guessed: missing → "price unavailable").</summary>
        public IReadOnlyDictionary<string, string> Prices() =>
            _store.Products.Where(p => p.Available && !string.IsNullOrEmpty(p.LocalizedPrice)).ToDictionary(p => p.Sku, p => p.LocalizedPrice);

        public async Task<PurchaseFlowUpdate> BuyAsync(string sku)
        {
            PurchaseAuthorization auth = await _backend.AuthorizePaidPurchaseAsync(sku).ConfigureAwait(false);
            if (!auth.Allowed)
            {
                var refused = new PurchaseFlowUpdate(sku, PurchaseFlowState.NotAllowed, auth.State);
                Updated?.Invoke(refused);
                return refused;
            }
            var update = new PurchaseFlowUpdate(sku, PurchaseFlowState.InStore);
            Updated?.Invoke(update);
            _store.Purchase(sku, auth.ObfuscatedAccountId);
            return update;
        }

        /// <summary>Reinstall/restore: hands every owned device purchase to the backend, then reports server ownership.</summary>
        public async Task<PlayerEntitlements> RestoreAsync()
        {
            IReadOnlyList<StoreTransaction> owned = await _store.QueryOwnedAsync().ConfigureAwait(false);
            return await _backend.RestorePurchasesAsync(owned.Select(t => new KeyValuePair<string, string>(t.Sku, t.PurchaseToken)).ToList()).ConfigureAwait(false);
        }

        private void OnPurchaseUpdated(StoreTransaction t) => _ = HandleAsync(t);

        /// <summary>Handles one store callback; public for tests (they await it).</summary>
        public async Task<PurchaseFlowUpdate> HandleAsync(StoreTransaction t)
        {
            if (t.IsPending)
            {
                var pending = new PurchaseFlowUpdate(t.Sku, PurchaseFlowState.PendingPayment);
                Updated?.Invoke(pending);
                return pending;
            }
            lock (_inFlight)
                if (!_inFlight.Add(t.PurchaseToken)) return new PurchaseFlowUpdate(t.Sku, PurchaseFlowState.VerificationDeferred, detail: "already verifying");
            try
            {
                PurchaseResult r = await _backend.VerifyPurchaseAsync(t.Sku, t.PurchaseToken).ConfigureAwait(false);
                if (r.ClientMayFinishTransaction) _store.FinishTransaction(t);
                PurchaseFlowUpdate u;
                switch (r.Status)
                {
                    case PurchaseStatus.Granted: u = new PurchaseFlowUpdate(t.Sku, PurchaseFlowState.Granted); break;
                    case PurchaseStatus.AlreadyOwned: u = new PurchaseFlowUpdate(t.Sku, PurchaseFlowState.AlreadyOwned); break;
                    case PurchaseStatus.Pending: u = new PurchaseFlowUpdate(t.Sku, PurchaseFlowState.PendingPayment); break;
                    case PurchaseStatus.RetryLater:
                    case PurchaseStatus.ConfigurationError: u = new PurchaseFlowUpdate(t.Sku, PurchaseFlowState.VerificationDeferred, detail: r.Detail); break;
                    case PurchaseStatus.Canceled: u = new PurchaseFlowUpdate(t.Sku, PurchaseFlowState.Cancelled); break;
                    default: u = new PurchaseFlowUpdate(t.Sku, PurchaseFlowState.Failed, detail: r.Status.ToString()); break;
                }
                Updated?.Invoke(u);
                return u;
            }
            finally
            {
                lock (_inFlight) _inFlight.Remove(t.PurchaseToken);
            }
        }

        private void OnPurchaseFailed(string sku, StoreFailure failure)
        {
            PurchaseFlowState s = failure == StoreFailure.UserCancelled || failure == StoreFailure.PaymentDeclined ? PurchaseFlowState.Cancelled
                : failure == StoreFailure.AlreadyOwned ? PurchaseFlowState.AlreadyOwned : PurchaseFlowState.Failed;
            Updated?.Invoke(new PurchaseFlowUpdate(sku, s, detail: failure.ToString()));
            if (failure == StoreFailure.AlreadyOwned) _ = RestoreAsync();
        }
    }

    public enum AdFlowResult : byte
    {
        /// <summary>The server confirmed the verified reward.</summary>
        Rewarded = 0,
        NotOffered = 1,
        NoFill = 2,
        /// <summary>The player closed the ad early or declined: nothing lost, can try again later.</summary>
        Declined = 3,
        Failed = 4,
        /// <summary>The SDK said earned, but the server has not confirmed yet; the reward appears when it does.</summary>
        AwaitingVerification = 5,
    }

    /// <summary>
    /// Client rewarded-ad flow (ticket 63): only when the server issues an offer (outside matches),
    /// loads with the audience-derived request flags, shows, then waits for the server's verified
    /// reward. A decline or failure leaves no penalty and grants nothing.
    /// </summary>
    public sealed class RewardedAdFlow
    {
        private readonly IRewardedAdProvider _provider;
        private readonly IMetaBackend _backend;
        private readonly Func<string> _userId;

        public int StatusPolls { get; set; } = 5;
        public TimeSpan PollDelay { get; set; } = TimeSpan.FromSeconds(1);

        public RewardedAdFlow(IRewardedAdProvider provider, IMetaBackend backend, Func<string> ssvUserId)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _userId = ssvUserId ?? throw new ArgumentNullException(nameof(ssvUserId));
        }

        public async Task<(AdFlowResult result, OfferDecision decision, string ticketId)> RunAsync(ScreenContext context, bool personalisedAdsConsent,
            CancellationToken ct = default)
        {
            OfferResult offer = await _backend.RequestAdOfferAsync(context, personalisedAdsConsent).ConfigureAwait(false);
            if (offer.Decision != OfferDecision.Available) return (AdFlowResult.NotOffered, offer.Decision, null);
            AdLoadResult load = await _provider.LoadAsync(offer.Ticket.RequestOptions, ct).ConfigureAwait(false);
            if (load != AdLoadResult.Loaded) return (load == AdLoadResult.NoFill ? AdFlowResult.NoFill : AdFlowResult.Failed, offer.Decision, offer.Ticket.TicketId);
            AdShowOutcome shown = await _provider.ShowAsync(_userId(), offer.Ticket.TicketId, ct).ConfigureAwait(false);
            if (shown == AdShowOutcome.Dismissed) return (AdFlowResult.Declined, offer.Decision, offer.Ticket.TicketId);
            if (shown != AdShowOutcome.EarnedReward) return (AdFlowResult.Failed, offer.Decision, offer.Ticket.TicketId);
            for (int i = 0; i < StatusPolls; i++)
            {
                TicketStatus s = await _backend.GetAdTicketStatusAsync(offer.Ticket.TicketId).ConfigureAwait(false);
                if (s == TicketStatus.Rewarded) return (AdFlowResult.Rewarded, offer.Decision, offer.Ticket.TicketId);
                if (s == TicketStatus.Expired || s == TicketStatus.Unknown) break;
                if (PollDelay > TimeSpan.Zero) await Task.Delay(PollDelay, ct).ConfigureAwait(false);
            }
            return (AdFlowResult.AwaitingVerification, offer.Decision, offer.Ticket.TicketId);
        }
    }
}
