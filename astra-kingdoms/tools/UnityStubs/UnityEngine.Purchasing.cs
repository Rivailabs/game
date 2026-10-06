// Compile-only stubs of the Unity IAP package (com.unity.purchasing 4.x) surface that
// unity/Assets/Scripts/Meta/Store/UnityIapStoreBridge.cs uses. Written from the documented API, not
// generated from the package; never executed. The adapter compiles only with AK_UNITY_IAP, which
// the project sets after the package is installed, so a signature drift is caught in the editor.
using System;

namespace UnityEngine.Purchasing
{
    public enum ProductType
    {
        Consumable = 0,
        NonConsumable = 1,
        Subscription = 2,
    }

    public enum PurchaseProcessingResult
    {
        Complete = 0,
        Pending = 1,
    }

    public enum PurchaseFailureReason
    {
        PurchasingUnavailable = 0,
        ExistingPurchasePending = 1,
        ProductUnavailable = 2,
        SignatureInvalid = 3,
        UserCancelled = 4,
        PaymentDeclined = 5,
        DuplicateTransaction = 6,
        Unknown = 7,
    }

    public enum InitializationFailureReason
    {
        PurchasingUnavailable = 0,
        NoProductsAvailable = 1,
        AppNotKnown = 2,
    }

    public class ProductDefinition
    {
        public string id => throw null;
        public string storeSpecificId => throw null;
        public ProductType type => throw null;
    }

    public class ProductMetadata
    {
        public string localizedPriceString => throw null;
        public string localizedTitle => throw null;
        public string localizedDescription => throw null;
        public string isoCurrencyCode => throw null;
        public decimal localizedPrice => throw null;
    }

    public class Product
    {
        public ProductDefinition definition => throw null;
        public ProductMetadata metadata => throw null;
        public bool availableToPurchase => throw null;
        public string transactionID => throw null;
        public bool hasReceipt => throw null;
        public string receipt => throw null;
    }

    public class ProductCollection
    {
        public Product WithID(string id) => throw null;
        public Product[] all => throw null;
    }

    public class PurchaseEventArgs
    {
        public Product purchasedProduct => throw null;
    }

    public class PurchaseFailureDescription
    {
        public string productId => throw null;
        public PurchaseFailureReason reason => throw null;
        public string message => throw null;
    }

    public interface IStoreController
    {
        ProductCollection products { get; }
        void InitiatePurchase(Product product);
        void InitiatePurchase(string productId);
        void ConfirmPendingPurchase(Product product);
    }

    public interface IStoreExtension
    {
    }

    public interface IExtensionProvider
    {
        T GetExtension<T>() where T : IStoreExtension;
    }

    public interface IGooglePlayStoreExtensions : IStoreExtension
    {
        void SetObfuscatedAccountId(string accountId);
        bool IsPurchasedProductDeferred(Product product);
        void RestoreTransactions(Action<bool, string> callback);
    }

    public interface IStoreListener
    {
        void OnInitializeFailed(InitializationFailureReason error);
        void OnInitializeFailed(InitializationFailureReason error, string message);
        PurchaseProcessingResult ProcessPurchase(PurchaseEventArgs purchaseEvent);
        void OnPurchaseFailed(Product product, PurchaseFailureReason failureReason);
        void OnInitialized(IStoreController controller, IExtensionProvider extensions);
    }

    public interface IDetailedStoreListener : IStoreListener
    {
        void OnPurchaseFailed(Product product, PurchaseFailureDescription failureDescription);
    }

    public interface IPurchasingModule
    {
    }

    public class StandardPurchasingModule : IPurchasingModule
    {
        public static StandardPurchasingModule Instance() => throw null;
    }

    public class ConfigurationBuilder
    {
        public static ConfigurationBuilder Instance(IPurchasingModule first, params IPurchasingModule[] rest) => throw null;
        public ConfigurationBuilder AddProduct(string id, ProductType type) => throw null;
    }

    public static class UnityPurchasing
    {
        public static void Initialize(IDetailedStoreListener listener, ConfigurationBuilder builder) { }
    }
}
