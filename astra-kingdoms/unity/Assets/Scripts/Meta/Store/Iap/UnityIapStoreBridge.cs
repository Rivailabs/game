#if AK_UNITY_IAP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Client;
using UnityEngine;
using UnityEngine.Purchasing;

namespace AstraKingdoms.Client.Meta.Store
{
    /// <summary>
    /// <see cref="IStoreBridge"/> over Unity IAP 4.x with Google Play Billing.
    /// <list type="bullet">
    /// <item>All V1 products are non-consumable.</item>
    /// <item><see cref="ProcessPurchase"/> always returns <see cref="PurchaseProcessingResult.Pending"/>; the
    /// transaction is confirmed only through <see cref="FinishTransaction"/> after server verification,
    /// so Unity IAP re-delivers unverified purchases on the next start.</item>
    /// <item>Deferred (slow payment) purchases are reported as pending and never finished.</item>
    /// <item>On Google Play, <c>Product.transactionID</c> is the purchase token the server verifies.</item>
    /// </list>
    /// The assembly compiles only when com.unity.purchasing is installed (asmdef version define).
    /// UNVERIFIED: compiled against hand-written stubs only; check SetObfuscatedAccountId and
    /// IsPurchasedProductDeferred against the installed package version in the editor.
    /// </summary>
    public sealed class UnityIapStoreBridge : IStoreBridge, IDetailedStoreListener
    {
        private IStoreController _controller;
        private IGooglePlayStoreExtensions _google;
        private TaskCompletionSource<bool> _init;

        public bool IsInitialized => _controller != null;

        public IReadOnlyList<StoreProductInfo> Products =>
            _controller == null
                ? (IReadOnlyList<StoreProductInfo>)Array.Empty<StoreProductInfo>()
                : _controller.products.all.Select(p => new StoreProductInfo(p.definition.id, p.metadata.localizedPriceString, p.metadata.localizedTitle,
                    p.availableToPurchase)).ToArray();

        public event Action<StoreTransaction> PurchaseUpdated;
        public event Action<string, StoreFailure> PurchaseFailed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install() => MetaServices.StoreBridgeFactory = () => new UnityIapStoreBridge();

        public Task<bool> InitializeAsync(IEnumerable<string> skus)
        {
            if (_init != null) return _init.Task;
            _init = new TaskCompletionSource<bool>();
            ConfigurationBuilder builder = ConfigurationBuilder.Instance(StandardPurchasingModule.Instance());
            foreach (string sku in skus) builder.AddProduct(sku, ProductType.NonConsumable);
            UnityPurchasing.Initialize(this, builder);
            return _init.Task;
        }

        public void Purchase(string sku, string obfuscatedAccountId)
        {
            if (_controller == null)
            {
                PurchaseFailed?.Invoke(sku, StoreFailure.StoreUnavailable);
                return;
            }
            Product p = _controller.products.WithID(sku);
            if (p == null || !p.availableToPurchase)
            {
                PurchaseFailed?.Invoke(sku, StoreFailure.ProductUnavailable);
                return;
            }
            _google?.SetObfuscatedAccountId(obfuscatedAccountId);
            _controller.InitiatePurchase(p);
        }

        public void FinishTransaction(StoreTransaction transaction)
        {
            Product p = _controller?.products.WithID(transaction.Sku);
            if (p != null) _controller.ConfirmPendingPurchase(p);
        }

        public Task<IReadOnlyList<StoreTransaction>> QueryOwnedAsync()
        {
            IReadOnlyList<StoreTransaction> owned = _controller == null
                ? Array.Empty<StoreTransaction>()
                : _controller.products.all.Where(p => p.hasReceipt && !string.IsNullOrEmpty(p.transactionID))
                    .Select(p => new StoreTransaction(p.definition.id, p.transactionID, null, IsDeferred(p))).ToArray();
            return Task.FromResult(owned);
        }

        private bool IsDeferred(Product p) => _google != null && _google.IsPurchasedProductDeferred(p);

        // ------------------------------------------------------------------ IDetailedStoreListener

        public void OnInitialized(IStoreController controller, IExtensionProvider extensions)
        {
            _controller = controller;
            _google = extensions.GetExtension<IGooglePlayStoreExtensions>();
            _init?.TrySetResult(true);
        }

        public void OnInitializeFailed(InitializationFailureReason error) => OnInitializeFailed(error, null);

        public void OnInitializeFailed(InitializationFailureReason error, string message)
        {
            Debug.LogWarning("[Store] Unity IAP initialisation failed: " + error + " " + message);
            _init?.TrySetResult(false);
        }

        public PurchaseProcessingResult ProcessPurchase(PurchaseEventArgs purchaseEvent)
        {
            Product p = purchaseEvent.purchasedProduct;
            PurchaseUpdated?.Invoke(new StoreTransaction(p.definition.id, p.transactionID, null, IsDeferred(p)));
            return PurchaseProcessingResult.Pending; // confirmed later by FinishTransaction, after server verification
        }

        public void OnPurchaseFailed(Product product, PurchaseFailureReason failureReason) =>
            PurchaseFailed?.Invoke(product?.definition.id, Map(failureReason));

        public void OnPurchaseFailed(Product product, PurchaseFailureDescription failureDescription) =>
            PurchaseFailed?.Invoke(product?.definition.id ?? failureDescription.productId, Map(failureDescription.reason));

        private static StoreFailure Map(PurchaseFailureReason r)
        {
            switch (r)
            {
                case PurchaseFailureReason.UserCancelled: return StoreFailure.UserCancelled;
                case PurchaseFailureReason.PaymentDeclined: return StoreFailure.PaymentDeclined;
                case PurchaseFailureReason.ProductUnavailable: return StoreFailure.ProductUnavailable;
                case PurchaseFailureReason.DuplicateTransaction: return StoreFailure.AlreadyOwned;
                case PurchaseFailureReason.PurchasingUnavailable: return StoreFailure.StoreUnavailable;
                default: return StoreFailure.Unknown;
            }
        }
    }
}
#endif
