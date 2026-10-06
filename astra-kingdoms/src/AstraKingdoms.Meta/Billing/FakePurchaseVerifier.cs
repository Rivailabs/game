using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AstraKingdoms.Meta.Billing
{
    /// <summary>
    /// In-memory stand-in for the Play Developer API (tests and local development only). It models
    /// pending payments, acknowledgement, voiding and transient outages so the purchase service's
    /// paths can be exercised without Google Play. It proves nothing about the real API.
    /// </summary>
    public sealed class FakePurchaseVerifier : IPurchaseVerifier
    {
        private sealed class Record
        {
            public string Sku;
            public ProductPurchase Purchase;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Record> _tokens = new Dictionary<string, Record>(StringComparer.Ordinal);
        private readonly List<VoidedPurchase> _voided = new List<VoidedPurchase>();

        /// <summary>The next N verification calls fail transiently.</summary>
        public int FailNextVerifications { get; set; }
        /// <summary>The next N acknowledgement calls fail transiently.</summary>
        public int FailNextAcknowledgements { get; set; }
        public int AcknowledgeCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        /// <summary>Page size for voided purchase listing (to test paging).</summary>
        public int VoidedPageSize { get; set; } = 2;

        public void AddPurchase(string sku, string token, ProductPurchase purchase)
        {
            lock (_gate) _tokens[token] = new Record { Sku = sku, Purchase = purchase };
        }

        /// <summary>Moves a pending purchase to purchased (the payment completed).</summary>
        public void CompletePending(string token)
        {
            lock (_gate)
            {
                ProductPurchase p = _tokens[token].Purchase;
                _tokens[token].Purchase = new ProductPurchase(PlayPurchaseState.Purchased, p.AcknowledgementState, p.ConsumptionState, p.OrderId,
                    p.PurchaseTime, p.PurchaseType, p.ObfuscatedExternalAccountId, p.RegionCode, p.Quantity);
            }
        }

        public void Void(string token, DateTimeOffset at, int reason = 1)
        {
            lock (_gate) _voided.Add(new VoidedPurchase(token, _tokens.TryGetValue(token, out Record r) ? r.Purchase.OrderId : null, at, 0, reason));
        }

        public AcknowledgementState AcknowledgementOf(string token)
        {
            lock (_gate) return _tokens[token].Purchase.AcknowledgementState;
        }

        public Task<PurchaseVerification> GetProductPurchaseAsync(string productId, string purchaseToken, CancellationToken ct = default)
        {
            lock (_gate)
            {
                VerifyCalls++;
                if (FailNextVerifications > 0)
                {
                    FailNextVerifications--;
                    return Task.FromResult(new PurchaseVerification(VerificationOutcome.TransientError, error: "simulated outage"));
                }
                if (purchaseToken == null || !_tokens.TryGetValue(purchaseToken, out Record r) || r.Sku != productId)
                    return Task.FromResult(new PurchaseVerification(VerificationOutcome.NotFound));
                return Task.FromResult(new PurchaseVerification(VerificationOutcome.Found, r.Purchase));
            }
        }

        public Task<AcknowledgeOutcome> AcknowledgeAsync(string productId, string purchaseToken, CancellationToken ct = default)
        {
            lock (_gate)
            {
                AcknowledgeCalls++;
                if (FailNextAcknowledgements > 0)
                {
                    FailNextAcknowledgements--;
                    return Task.FromResult(AcknowledgeOutcome.TransientError);
                }
                if (!_tokens.TryGetValue(purchaseToken, out Record r) || r.Sku != productId) return Task.FromResult(AcknowledgeOutcome.Failed);
                ProductPurchase p = r.Purchase;
                r.Purchase = new ProductPurchase(p.PurchaseState, AcknowledgementState.Acknowledged, p.ConsumptionState, p.OrderId, p.PurchaseTime,
                    p.PurchaseType, p.ObfuscatedExternalAccountId, p.RegionCode, p.Quantity);
                return Task.FromResult(AcknowledgeOutcome.Acknowledged);
            }
        }

        public Task<VoidedPurchasesPage> ListVoidedPurchasesAsync(DateTimeOffset startTime, string pageToken, CancellationToken ct = default)
        {
            lock (_gate)
            {
                int offset = string.IsNullOrEmpty(pageToken) ? 0 : int.Parse(pageToken, System.Globalization.CultureInfo.InvariantCulture);
                List<VoidedPurchase> matching = _voided.Where(v => v.VoidedTime >= startTime).ToList();
                List<VoidedPurchase> page = matching.Skip(offset).Take(VoidedPageSize).ToList();
                string next = offset + page.Count < matching.Count ? (offset + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                return Task.FromResult(new VoidedPurchasesPage(VerificationOutcome.Found, page, next));
            }
        }
    }
}
