using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Shop;

namespace AstraKingdoms.Meta.Billing
{
    public enum PurchaseStatus : byte
    {
        /// <summary>Verified and granted now.</summary>
        Granted = 0,
        /// <summary>Already granted to this player (duplicate callback, restore, reinstall). Still owned.</summary>
        AlreadyOwned = 1,
        /// <summary>Payment not completed yet (e.g. cash/slow payment). Nothing granted; the client keeps the transaction open.</summary>
        Pending = 2,
        /// <summary>Retry later (network/store outage). Nothing granted; the client must not finish the transaction.</summary>
        RetryLater = 3,
        Canceled = 4,
        /// <summary>The token was voided (refund/chargeback) and can never grant again.</summary>
        Revoked = 5,
        InvalidToken = 6,
        UnknownProduct = 7,
        /// <summary>The purchase belongs to a different account (obfuscated account id mismatch, or token already used).</summary>
        AccountMismatch = 8,
        TestPurchaseNotAccepted = 9,
        ConfigurationError = 10,
    }

    public sealed class PurchaseResult
    {
        public PurchaseStatus Status { get; }
        public string Sku { get; }
        /// <summary>True when the store purchase is acknowledged (or was already).</summary>
        public bool Acknowledged { get; }
        /// <summary>Tell the client it may finish (acknowledge locally / close) the transaction.</summary>
        public bool ClientMayFinishTransaction => Status == PurchaseStatus.Granted || Status == PurchaseStatus.AlreadyOwned ||
                                                  Status == PurchaseStatus.Canceled || Status == PurchaseStatus.Revoked;
        public string Detail { get; }
        /// <summary>A licence-tester purchase (excluded from revenue reporting).</summary>
        public bool IsTestPurchase { get; }

        public PurchaseResult(PurchaseStatus status, string sku, bool acknowledged = false, string detail = null, bool isTestPurchase = false)
        {
            Status = status;
            Sku = sku;
            Acknowledged = acknowledged;
            Detail = detail;
            IsTestPurchase = isTestPurchase;
        }

        public override string ToString() => Status + " " + Sku + (Detail != null ? " (" + Detail + ")" : string.Empty);
    }

    /// <summary>A granted purchase whose acknowledgement has not succeeded yet.</summary>
    public sealed class PendingAcknowledgement
    {
        public string Sku { get; }
        public string PurchaseToken { get; }
        /// <summary>Play refunds and revokes unacknowledged one-time purchases three days after purchase.</summary>
        public DateTimeOffset Deadline { get; }
        public int Attempts { get; }

        public PendingAcknowledgement(string sku, string purchaseToken, DateTimeOffset deadline, int attempts)
        {
            Sku = sku;
            PurchaseToken = purchaseToken;
            Deadline = deadline;
            Attempts = attempts;
        }
    }

    /// <summary>Durable retry queue for acknowledgements (server: a table keyed by purchase token).</summary>
    public interface IAcknowledgementQueue
    {
        void Upsert(PendingAcknowledgement item);
        void Remove(string purchaseToken);
        IReadOnlyList<PendingAcknowledgement> All();
    }

    public sealed class InMemoryAcknowledgementQueue : IAcknowledgementQueue
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, PendingAcknowledgement> _items = new Dictionary<string, PendingAcknowledgement>(StringComparer.Ordinal);

        public void Upsert(PendingAcknowledgement item)
        {
            lock (_gate) _items[item.PurchaseToken] = item;
        }

        public void Remove(string purchaseToken)
        {
            lock (_gate) _items.Remove(purchaseToken);
        }

        public IReadOnlyList<PendingAcknowledgement> All()
        {
            lock (_gate) return _items.Values.ToArray();
        }
    }

    /// <summary>Billing settings for one deployment.</summary>
    public sealed class BillingOptions
    {
        /// <summary>Salt for <see cref="ObfuscatedAccountId"/> (a server secret, identical to the one the client flow uses via the server).</summary>
        public string AccountIdSalt { get; set; } = string.Empty;
        /// <summary>Reject purchases without an obfuscated account id (the client always sets one).</summary>
        public bool RequireObfuscatedAccountId { get; set; } = true;
        /// <summary>Accept licence-tester purchases (internal/closed tracks). Turn off for production if desired.</summary>
        public bool AcceptTestPurchases { get; set; } = true;
        /// <summary>Play's acknowledgement window for one-time products.</summary>
        public TimeSpan AcknowledgementWindow { get; set; } = TimeSpan.FromDays(3);
        /// <summary>Alert when an acknowledgement is still pending this close to the deadline.</summary>
        public TimeSpan AcknowledgementAlertMargin { get; set; } = TimeSpan.FromHours(24);
    }

    /// <summary>A purchase-state change, for the analytics "purchase state" event (ticket 64).</summary>
    public sealed class PurchaseStateReport
    {
        public string PlayerId { get; }
        public string Sku { get; }
        public PurchaseStatus Status { get; }
        public bool IsTestPurchase { get; }

        public PurchaseStateReport(string playerId, string sku, PurchaseStatus status, bool isTestPurchase)
        {
            PlayerId = playerId;
            Sku = sku;
            Status = status;
            IsTestPurchase = isTestPurchase;
        }
    }

    /// <summary>
    /// Ticket 61: verifies Play purchases on the server before granting, handles pending, duplicate
    /// callbacks, restore/reinstall, voided purchases and acknowledgement within three days, and
    /// writes the append-only entitlement ledger.
    /// <para>Server integration: expose <see cref="HandlePurchaseAsync"/> and <see cref="RestoreAsync"/>
    /// as authenticated endpoints (player id from the session, never from the body). Run
    /// <see cref="RetryAcknowledgementsAsync"/> every few minutes and
    /// <see cref="ProcessVoidedPurchasesAsync"/> at least daily (or on Real-time Developer
    /// Notifications). Route a Real-time Developer Notification for a completed pending purchase to
    /// <see cref="HandlePurchaseAsync"/> with the player resolved from the obfuscated account id.</para>
    /// </summary>
    public sealed class PurchaseService
    {
        private readonly IPurchaseVerifier _verifier;
        private readonly IEntitlementLedgerStore _ledger;
        private readonly IAcknowledgementQueue _acks;
        private readonly StoreCatalog _catalog;
        private readonly IClock _clock;
        private readonly BillingOptions _options;

        public event Action<PurchaseStateReport> PurchaseStateChanged;

        public PurchaseService(IPurchaseVerifier verifier, IEntitlementLedgerStore ledger, IAcknowledgementQueue acks,
            StoreCatalog catalog, IClock clock, BillingOptions options = null)
        {
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _acks = acks ?? throw new ArgumentNullException(nameof(acks));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options ?? new BillingOptions();
        }

        public static string GrantKey(string purchaseToken) => "grant:" + purchaseToken;
        public static string RevokeKey(string purchaseToken) => "revoke:" + purchaseToken;

        public async Task<PurchaseResult> HandlePurchaseAsync(string playerId, string sku, string purchaseToken, CancellationToken ct = default)
        {
            PurchaseResult r = await HandleCoreAsync(playerId, sku, purchaseToken, ct).ConfigureAwait(false);
            PurchaseStateChanged?.Invoke(new PurchaseStateReport(playerId, sku, r.Status, r.IsTestPurchase));
            return r;
        }

        private async Task<PurchaseResult> HandleCoreAsync(string playerId, string sku, string purchaseToken, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("player id required", nameof(playerId));
            StoreProduct product = _catalog.Find(sku);
            if (product == null) return new PurchaseResult(PurchaseStatus.UnknownProduct, sku);
            if (string.IsNullOrEmpty(purchaseToken)) return new PurchaseResult(PurchaseStatus.InvalidToken, sku);

            // A voided token never grants again, whoever presents it.
            if (_ledger.Find(RevokeKey(purchaseToken)) != null) return new PurchaseResult(PurchaseStatus.Revoked, sku);

            EntitlementEntry prior = _ledger.Find(GrantKey(purchaseToken));
            if (prior != null && (prior.PlayerId != playerId || prior.Sku != sku))
                return new PurchaseResult(PurchaseStatus.AccountMismatch, sku, detail: "token already redeemed");

            PurchaseVerification v = await _verifier.GetProductPurchaseAsync(sku, purchaseToken, ct).ConfigureAwait(false);
            switch (v.Outcome)
            {
                case VerificationOutcome.NotFound: return new PurchaseResult(PurchaseStatus.InvalidToken, sku);
                case VerificationOutcome.TransientError: return new PurchaseResult(PurchaseStatus.RetryLater, sku, detail: v.Error);
                case VerificationOutcome.ConfigurationError: return new PurchaseResult(PurchaseStatus.ConfigurationError, sku, detail: v.Error);
            }
            ProductPurchase p = v.Purchase;
            if (p.PurchaseState == PlayPurchaseState.Pending) return new PurchaseResult(PurchaseStatus.Pending, sku);
            if (p.PurchaseState == PlayPurchaseState.Canceled) return new PurchaseResult(PurchaseStatus.Canceled, sku);

            string expected = ObfuscatedAccountId.For(playerId, _options.AccountIdSalt);
            if (string.IsNullOrEmpty(p.ObfuscatedExternalAccountId))
            {
                if (_options.RequireObfuscatedAccountId) return new PurchaseResult(PurchaseStatus.AccountMismatch, sku, detail: "missing obfuscated account id");
            }
            else if (!string.Equals(p.ObfuscatedExternalAccountId, expected, StringComparison.Ordinal))
            {
                return new PurchaseResult(PurchaseStatus.AccountMismatch, sku);
            }
            if (p.IsTestPurchase && !_options.AcceptTestPurchases) return new PurchaseResult(PurchaseStatus.TestPurchaseNotAccepted, sku);

            var entry = new EntitlementEntry(GrantKey(purchaseToken), playerId, sku, purchaseToken, p.OrderId,
                EntitlementAction.Grant, EntitlementReason.VerifiedPurchase, _clock.UtcNow, p.IsTestPurchase);
            EntitlementEntry stored = _ledger.TryAppend(entry, out bool appended);
            if (!appended && stored.PlayerId != playerId)
                return new PurchaseResult(PurchaseStatus.AccountMismatch, sku, detail: "token already redeemed");

            bool acked = p.AcknowledgementState == AcknowledgementState.Acknowledged;
            if (!acked) acked = await TryAcknowledgeAsync(sku, purchaseToken, p.PurchaseTime + _options.AcknowledgementWindow, 0, ct).ConfigureAwait(false);
            return new PurchaseResult(appended ? PurchaseStatus.Granted : PurchaseStatus.AlreadyOwned, sku, acked, isTestPurchase: p.IsTestPurchase);
        }

        private async Task<bool> TryAcknowledgeAsync(string sku, string token, DateTimeOffset deadline, int attempts, CancellationToken ct)
        {
            AcknowledgeOutcome a;
            try
            {
                a = await _verifier.AcknowledgeAsync(sku, token, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                a = AcknowledgeOutcome.TransientError;
            }
            if (a == AcknowledgeOutcome.Acknowledged)
            {
                _acks.Remove(token);
                return true;
            }
            _acks.Upsert(new PendingAcknowledgement(sku, token, deadline, attempts + 1));
            return false;
        }

        /// <summary>Restore/reinstall: re-checks the tokens the device still has, then returns server-side ownership.</summary>
        public async Task<PlayerEntitlements> RestoreAsync(string playerId, IEnumerable<KeyValuePair<string, string>> skuTokens, CancellationToken ct = default)
        {
            foreach (KeyValuePair<string, string> st in skuTokens ?? Array.Empty<KeyValuePair<string, string>>())
                await HandlePurchaseAsync(playerId, st.Key, st.Value, ct).ConfigureAwait(false);
            return GetEntitlements(playerId);
        }

        /// <summary>Server-side ownership: the source of truth across devices and reinstalls.</summary>
        public PlayerEntitlements GetEntitlements(string playerId) => PlayerEntitlements.Fold(_ledger.ForPlayer(playerId));

        /// <summary>Retries pending acknowledgements; returns those at risk of Play's automatic refund.</summary>
        public async Task<IReadOnlyList<PendingAcknowledgement>> RetryAcknowledgementsAsync(CancellationToken ct = default)
        {
            var atRisk = new List<PendingAcknowledgement>();
            foreach (PendingAcknowledgement item in _acks.All())
            {
                bool ok = await TryAcknowledgeAsync(item.Sku, item.PurchaseToken, item.Deadline, item.Attempts, ct).ConfigureAwait(false);
                if (!ok && _clock.UtcNow >= item.Deadline - _options.AcknowledgementAlertMargin) atRisk.Add(item);
            }
            return atRisk;
        }

        /// <summary>
        /// Pulls voided purchases since <paramref name="since"/> and appends one revoke per token.
        /// Returns the number of new revocations, or -1 when the store could not be reached.
        /// </summary>
        public async Task<int> ProcessVoidedPurchasesAsync(DateTimeOffset since, CancellationToken ct = default)
        {
            int revoked = 0;
            string page = null;
            do
            {
                VoidedPurchasesPage result = await _verifier.ListVoidedPurchasesAsync(since, page, ct).ConfigureAwait(false);
                if (result.Outcome != VerificationOutcome.Found) return -1;
                foreach (VoidedPurchase vp in result.Items)
                    if (Revoke(vp.PurchaseToken, vp.OrderId, EntitlementReason.Voided, "voided reason " + vp.VoidedReason)) revoked++;
                page = result.NextPageToken;
            }
            while (!string.IsNullOrEmpty(page));
            return revoked;
        }

        /// <summary>Appends a revoke for a token (idempotent). Unknown tokens are recorded so they can never be granted later.</summary>
        public bool Revoke(string purchaseToken, string orderId, EntitlementReason reason, string note)
        {
            EntitlementEntry grant = _ledger.Find(GrantKey(purchaseToken));
            var entry = new EntitlementEntry(RevokeKey(purchaseToken), grant?.PlayerId ?? "unattributed", grant?.Sku, purchaseToken,
                orderId ?? grant?.OrderId, EntitlementAction.Revoke, reason, _clock.UtcNow, grant?.IsTestPurchase ?? false, note);
            _ledger.TryAppend(entry, out bool appended);
            if (appended)
            {
                _acks.Remove(purchaseToken);
                if (grant != null) PurchaseStateChanged?.Invoke(new PurchaseStateReport(grant.PlayerId, grant.Sku, PurchaseStatus.Revoked, grant.IsTestPurchase));
            }
            return appended;
        }

        /// <summary>
        /// A corrective transaction by operations (e.g. re-granting after a mistaken revoke). It is a
        /// new ledger line with a reference; nothing is edited.
        /// </summary>
        public bool CorrectiveGrant(string playerId, string sku, string correctionId, string note)
        {
            if (_catalog.Find(sku) == null) return false;
            EntitlementEntry original = _ledger.ForPlayer(playerId).Where(e => e.Sku == sku).OrderBy(e => e.Sequence).LastOrDefault();
            var entry = new EntitlementEntry("correction:" + correctionId, playerId, sku, original?.PurchaseToken ?? "correction:" + correctionId,
                original?.OrderId, EntitlementAction.Grant, EntitlementReason.SupportCorrection, _clock.UtcNow, note: note);
            _ledger.TryAppend(entry, out bool appended);
            return appended;
        }
    }
}
