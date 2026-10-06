using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraKingdoms.Meta.Billing
{
    /// <summary>Google Play <c>ProductPurchase.purchaseState</c>.</summary>
    public enum PlayPurchaseState : byte
    {
        Purchased = 0,
        Canceled = 1,
        Pending = 2,
    }

    /// <summary>Google Play <c>ProductPurchase.acknowledgementState</c>.</summary>
    public enum AcknowledgementState : byte
    {
        NotAcknowledged = 0,
        Acknowledged = 1,
    }

    /// <summary>
    /// The fields of the Play Developer API <c>purchases.products</c> resource that entitlement
    /// decisions use. <see cref="PurchaseType"/> is null for a real purchase, 0 for a licence-tester
    /// test purchase, 1 for a promo code, 2 for rewarded.
    /// </summary>
    public sealed class ProductPurchase
    {
        public PlayPurchaseState PurchaseState { get; }
        public AcknowledgementState AcknowledgementState { get; }
        /// <summary>0 = not consumed, 1 = consumed. Cosmetics are never consumed.</summary>
        public int ConsumptionState { get; }
        public string OrderId { get; }
        public DateTimeOffset PurchaseTime { get; }
        public int? PurchaseType { get; }
        public string ObfuscatedExternalAccountId { get; }
        public string RegionCode { get; }
        public int Quantity { get; }

        public ProductPurchase(PlayPurchaseState purchaseState, AcknowledgementState acknowledgementState, int consumptionState,
            string orderId, DateTimeOffset purchaseTime, int? purchaseType, string obfuscatedExternalAccountId, string regionCode = null, int quantity = 1)
        {
            PurchaseState = purchaseState;
            AcknowledgementState = acknowledgementState;
            ConsumptionState = consumptionState;
            OrderId = orderId;
            PurchaseTime = purchaseTime;
            PurchaseType = purchaseType;
            ObfuscatedExternalAccountId = obfuscatedExternalAccountId;
            RegionCode = regionCode;
            Quantity = quantity;
        }

        public bool IsTestPurchase => PurchaseType == 0;
    }

    public enum VerificationOutcome : byte
    {
        /// <summary>The store returned the purchase resource.</summary>
        Found = 0,
        /// <summary>The token is unknown/invalid for this package and product (HTTP 404/400/410).</summary>
        NotFound = 1,
        /// <summary>Network failure, 5xx or rate limiting: retry later; grant nothing now.</summary>
        TransientError = 2,
        /// <summary>Credentials or permissions are wrong (HTTP 401/403): an operator must fix configuration.</summary>
        ConfigurationError = 3,
    }

    public sealed class PurchaseVerification
    {
        public VerificationOutcome Outcome { get; }
        public ProductPurchase Purchase { get; }
        public string Error { get; }

        public PurchaseVerification(VerificationOutcome outcome, ProductPurchase purchase = null, string error = null)
        {
            Outcome = outcome;
            Purchase = purchase;
            Error = error;
        }
    }

    public enum AcknowledgeOutcome : byte
    {
        Acknowledged = 0,
        /// <summary>Retry later (network/5xx).</summary>
        TransientError = 1,
        /// <summary>Permanent failure (unknown token, configuration).</summary>
        Failed = 2,
    }

    /// <summary>One entry of the Play Developer API <c>purchases.voidedpurchases.list</c>.</summary>
    public sealed class VoidedPurchase
    {
        public string PurchaseToken { get; }
        public string OrderId { get; }
        public DateTimeOffset VoidedTime { get; }
        /// <summary>0 user, 1 developer, 2 Google.</summary>
        public int VoidedSource { get; }
        /// <summary>0 other, 1 remorse, 2 not received, 3 defective, 4 accidental, 5 fraud, 6 friendly fraud, 7 chargeback.</summary>
        public int VoidedReason { get; }

        public VoidedPurchase(string purchaseToken, string orderId, DateTimeOffset voidedTime, int voidedSource, int voidedReason)
        {
            PurchaseToken = purchaseToken;
            OrderId = orderId;
            VoidedTime = voidedTime;
            VoidedSource = voidedSource;
            VoidedReason = voidedReason;
        }
    }

    public sealed class VoidedPurchasesPage
    {
        public VerificationOutcome Outcome { get; }
        public IReadOnlyList<VoidedPurchase> Items { get; }
        public string NextPageToken { get; }

        public VoidedPurchasesPage(VerificationOutcome outcome, IReadOnlyList<VoidedPurchase> items, string nextPageToken)
        {
            Outcome = outcome;
            Items = items ?? Array.Empty<VoidedPurchase>();
            NextPageToken = nextPageToken;
        }
    }

    /// <summary>
    /// Server-side store verification (ticket 61). The production implementation is
    /// <c>AstraKingdoms.Meta.Server.GooglePlayPurchaseVerifier</c>; tests and local development use
    /// <see cref="FakePurchaseVerifier"/>. Never call a real implementation from the client: it needs
    /// the service-account key.
    /// </summary>
    public interface IPurchaseVerifier
    {
        Task<PurchaseVerification> GetProductPurchaseAsync(string productId, string purchaseToken, CancellationToken ct = default);
        Task<AcknowledgeOutcome> AcknowledgeAsync(string productId, string purchaseToken, CancellationToken ct = default);
        Task<VoidedPurchasesPage> ListVoidedPurchasesAsync(DateTimeOffset startTime, string pageToken, CancellationToken ct = default);
    }

    /// <summary>
    /// The account identifier passed to the billing flow as <c>obfuscatedAccountId</c>: a salted
    /// SHA-256 of the account id, so Play never sees the raw id and a token bought by one account
    /// cannot be redeemed by another.
    /// </summary>
    public static class ObfuscatedAccountId
    {
        public static string For(string accountId, string salt)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("account id required", nameof(accountId));
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes((salt ?? string.Empty) + "|" + accountId));
                var sb = new StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString(); // 64 hex chars: Play's maximum length
            }
        }
    }
}
