using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Billing;

namespace AstraKingdoms.Meta.Client
{
    /// <summary>A product as the device store reports it (localised price included).</summary>
    public sealed class StoreProductInfo
    {
        public string Sku { get; }
        public string LocalizedPrice { get; }
        public string LocalizedTitle { get; }
        public bool Available { get; }

        public StoreProductInfo(string sku, string localizedPrice, string localizedTitle, bool available)
        {
            Sku = sku;
            LocalizedPrice = localizedPrice;
            LocalizedTitle = localizedTitle;
            Available = available;
        }
    }

    /// <summary>A purchase the device store delivered (new, pending, or unfinished from an earlier run).</summary>
    public sealed class StoreTransaction
    {
        public string Sku { get; }
        public string PurchaseToken { get; }
        public string OrderId { get; }
        public bool IsPending { get; }

        public StoreTransaction(string sku, string purchaseToken, string orderId, bool isPending)
        {
            Sku = sku;
            PurchaseToken = purchaseToken;
            OrderId = orderId;
            IsPending = isPending;
        }
    }

    public enum StoreFailure : byte
    {
        UserCancelled = 0,
        PaymentDeclined = 1,
        ProductUnavailable = 2,
        /// <summary>The store says the player already owns it: run a restore.</summary>
        AlreadyOwned = 3,
        StoreUnavailable = 4,
        Unknown = 5,
    }

    /// <summary>
    /// Device-store boundary, shaped like Unity IAP (initialise with product ids, purchase with an
    /// obfuscated account id, purchases arrive by callback and stay open until finished, restore).
    /// The Unity adapter lives in <c>unity/Assets/Scripts/Meta/Store/UnityIapStoreBridge.cs</c>.
    /// The client never grants anything itself: it forwards tokens to the backend and finishes a
    /// transaction only after the backend says so.
    /// </summary>
    public interface IStoreBridge
    {
        bool IsInitialized { get; }
        IReadOnlyList<StoreProductInfo> Products { get; }
        Task<bool> InitializeAsync(IEnumerable<string> skus);
        void Purchase(string sku, string obfuscatedAccountId);
        /// <summary>Raised for completed and pending purchases, including unfinished ones re-delivered at start-up.</summary>
        event Action<StoreTransaction> PurchaseUpdated;
        event Action<string, StoreFailure> PurchaseFailed;
        /// <summary>Close the transaction (Unity IAP: ConfirmPendingPurchase). Only after server verification.</summary>
        void FinishTransaction(StoreTransaction transaction);
        /// <summary>Owned purchases known to the device store (reinstall/restore).</summary>
        Task<IReadOnlyList<StoreTransaction>> QueryOwnedAsync();
    }

    /// <summary>
    /// Scripted store for tests and the editor. When given a <see cref="FakePurchaseVerifier"/>, each
    /// purchase is also registered there, so the full client → backend → verifier path runs offline.
    /// </summary>
    public sealed class FakeStoreBridge : IStoreBridge
    {
        private readonly Dictionary<string, StoreProductInfo> _catalog = new Dictionary<string, StoreProductInfo>(StringComparer.Ordinal);
        private readonly List<StoreTransaction> _open = new List<StoreTransaction>();
        private readonly List<StoreTransaction> _owned = new List<StoreTransaction>();
        private readonly FakePurchaseVerifier _verifier;
        private readonly Func<DateTimeOffset> _now;
        private int _counter;

        public FakeStoreBridge(FakePurchaseVerifier verifier = null, Func<DateTimeOffset> now = null)
        {
            _verifier = verifier;
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public bool IsInitialized { get; private set; }
        public IReadOnlyList<StoreProductInfo> Products => _catalog.Values.ToArray();
        public IReadOnlyList<StoreTransaction> OpenTransactions => _open.ToArray();
        public int FinishedCount { get; private set; }

        /// <summary>The next purchase fails with this reason instead of succeeding.</summary>
        public StoreFailure? NextFailure { get; set; }
        /// <summary>The next purchase is delivered as pending (slow payment method).</summary>
        public bool NextPending { get; set; }
        /// <summary>Price strings to report, by SKU; missing → product unavailable.</summary>
        public Dictionary<string, string> Prices { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public event Action<StoreTransaction> PurchaseUpdated;
        public event Action<string, StoreFailure> PurchaseFailed;

        public Task<bool> InitializeAsync(IEnumerable<string> skus)
        {
            foreach (string sku in skus)
                _catalog[sku] = new StoreProductInfo(sku, Prices.TryGetValue(sku, out string p) ? p : null, sku, Prices.ContainsKey(sku));
            IsInitialized = true;
            foreach (StoreTransaction t in _open.ToArray()) PurchaseUpdated?.Invoke(t); // unfinished purchases are re-delivered
            return Task.FromResult(true);
        }

        public void Purchase(string sku, string obfuscatedAccountId)
        {
            if (NextFailure.HasValue)
            {
                StoreFailure f = NextFailure.Value;
                NextFailure = null;
                PurchaseFailed?.Invoke(sku, f);
                return;
            }
            if (!_catalog.TryGetValue(sku, out StoreProductInfo info) || !info.Available)
            {
                PurchaseFailed?.Invoke(sku, StoreFailure.ProductUnavailable);
                return;
            }
            string token = "fake-token-" + (++_counter);
            bool pending = NextPending;
            NextPending = false;
            _verifier?.AddPurchase(sku, token, new ProductPurchase(pending ? PlayPurchaseState.Pending : PlayPurchaseState.Purchased,
                AcknowledgementState.NotAcknowledged, 0, "GPA.FAKE-" + _counter, _now(), 0, obfuscatedAccountId));
            var t = new StoreTransaction(sku, token, "GPA.FAKE-" + _counter, pending);
            _open.Add(t);
            PurchaseUpdated?.Invoke(t);
        }

        /// <summary>Simulates the slow payment completing.</summary>
        public void CompletePending(string token)
        {
            int i = _open.FindIndex(t => t.PurchaseToken == token);
            if (i < 0) return;
            StoreTransaction done = new StoreTransaction(_open[i].Sku, token, _open[i].OrderId, false);
            _open[i] = done;
            _verifier?.CompletePending(token);
            PurchaseUpdated?.Invoke(done);
        }

        /// <summary>Simulates the store re-delivering the same callback (duplicate callback).</summary>
        public void RedeliverOpen()
        {
            foreach (StoreTransaction t in _open.ToArray()) PurchaseUpdated?.Invoke(t);
        }

        public void FinishTransaction(StoreTransaction transaction)
        {
            int removed = _open.RemoveAll(t => t.PurchaseToken == transaction.PurchaseToken);
            if (removed > 0)
            {
                FinishedCount++;
                if (!transaction.IsPending) _owned.Add(transaction);
            }
        }

        /// <summary>Simulates uninstall/reinstall: the device forgets open callbacks but Play still knows owned purchases.</summary>
        public void Reinstall()
        {
            IsInitialized = false;
            _catalog.Clear();
        }

        public Task<IReadOnlyList<StoreTransaction>> QueryOwnedAsync() => Task.FromResult<IReadOnlyList<StoreTransaction>>(_owned.ToArray());
    }
}
