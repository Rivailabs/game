using System;
using System.Linq;
using AstraKingdoms.Client.Meta.Platform;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Client;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Privacy;
using AstraKingdoms.Meta.Shop;
using UnityEngine;

namespace AstraKingdoms.Client.Meta
{
    /// <summary>
    /// Composition root for the meta module. Today the backend is the offline guest profile
    /// (<see cref="LocalMetaBackend"/>); when the online server exposes the meta endpoints, an HTTP
    /// <see cref="IMetaBackend"/> replaces it here and nothing else changes.
    /// </summary>
    public sealed class MetaServices
    {
        /// <summary>Set by the Unity IAP adapter assembly (compiled only when com.unity.purchasing is installed).</summary>
        public static Func<IStoreBridge> StoreBridgeFactory;
        /// <summary>Set by an ad-network adapter once a Families-certified network is chosen (none yet).</summary>
        public static Func<IRewardedAdProvider> RewardedAdProviderFactory;
        /// <summary>
        /// Development builds count as developer tests, so they earn nothing (plan: no XP in developer
        /// tests). Flip in the editor to exercise the progression screens locally.
        /// </summary>
        public static bool GrantInDevelopmentBuilds;

        public LocalMetaBackend Backend { get; private set; }
        public IMetaBackend Api => Backend;
        public AnalyticsClient Analytics { get; private set; }
        public IStoreBridge Store { get; private set; }
        public ClientPurchaseFlow Purchases { get; private set; }
        public IRewardedAdProvider AdProvider { get; private set; }
        public RewardedAdFlow AdFlow { get; private set; }
        public PrivacyLinks Links { get; } = new PrivacyLinks();
        public MetaText Text { get; private set; }
        public bool DevelopmentBuild { get; private set; }
        public Func<bool> IsInMatch { get; private set; }
        /// <summary>True when the store is the development fake (editor and development builds without Unity IAP).</summary>
        public bool UsingTestStore { get; private set; }

        public static MetaServices Create(string language, Func<bool> isInMatch, bool developmentBuild)
        {
            var s = new MetaServices { DevelopmentBuild = developmentBuild, IsInMatch = isInMatch ?? (() => false) };
            s.Text = MetaText.Load(language);
            IClock clock = SystemClock.Instance;

            IPurchaseVerifier devVerifier = null;
            if (StoreBridgeFactory != null)
            {
                s.Store = StoreBridgeFactory();
            }
            else if (developmentBuild)
            {
                var fakePlay = new FakePurchaseVerifier();
                var fakeStore = new FakeStoreBridge(fakePlay);
                fakeStore.Prices["ak.cosmetic.outfit_tide_warden"] = "TEST ₹89.00";
                fakeStore.Prices["ak.cosmetic.sunrise_pack"] = "TEST ₹129.00";
                s.Store = fakeStore;
                devVerifier = fakePlay;
                s.UsingTestStore = true;
            }
            else
            {
                s.Store = new UnavailableStoreBridge();
            }

            // Real purchases are verified by the online server (its Google Play verifier). Until the
            // HTTP backend exists, only the development test store can complete a purchase locally.
            s.Backend = new LocalMetaBackend(FilePersistence.Default(), clock, s.IsInMatch, devVerifier);
            s.Purchases = new ClientPurchaseFlow(s.Store, s.Backend);
            _ = s.Store.InitializeAsync(StoreCatalog.Default.Products.Select(p => p.Sku));

            if (RewardedAdProviderFactory != null) s.AdProvider = RewardedAdProviderFactory();
            else if (developmentBuild) s.AdProvider = new FakeRewardedAdProvider();
            else s.AdProvider = new UnavailableRewardedAdProvider();
            s.AdFlow = new RewardedAdFlow(s.AdProvider, s.Backend, () => s.Backend.PlayerId);

            s.Analytics = new AnalyticsClient(new PendingEndpointAnalyticsSink(developmentBuild), clock, () => s.Backend.AnalyticsConsent,
                () => s.Backend.Audience)
            {
                SessionFlags = developmentBuild ? TrafficFlags.Internal : TrafficFlags.None,
                CrashReporter = new ConsentAwareCrashReporter(),
            };
            s.Analytics.OnConsentChanged();
            s.Purchases.Updated += u => s.Analytics.Track(AnalyticsEventType.PurchaseState, AnalyticsEvents.PurchaseState(u.Sku, PurchaseStateToken(u.State), s.UsingTestStore));
            return s;
        }

        /// <summary>Maps the client flow state to the schema's purchase_state token.</summary>
        public static string PurchaseStateToken(PurchaseFlowState s)
        {
            switch (s)
            {
                case PurchaseFlowState.InStore: return "started";
                case PurchaseFlowState.PendingPayment: return "pending";
                case PurchaseFlowState.Granted: return "granted";
                case PurchaseFlowState.AlreadyOwned: return "already_owned";
                case PurchaseFlowState.Cancelled: return "canceled";
                case PurchaseFlowState.VerificationDeferred: return "retry_later";
                default: return "failed";
            }
        }

        /// <summary>Saves consent choices and re-applies them to collection (withdrawal drops queued events).</summary>
        public void SetConsent(AnalyticsConsent consent, bool personalisedAds)
        {
            Backend.AnalyticsConsent = consent;
            Backend.PersonalisedAdsConsent = personalisedAds && Backend.Audience.AgeGroup == AgeGroup.Adult;
            Backend.Save();
            Analytics.OnConsentChanged();
        }

        public void OpenUrl(string url)
        {
            if (!string.IsNullOrEmpty(url)) Application.OpenURL(url);
        }
    }
}
