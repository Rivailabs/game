using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Economy;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Server;
using AstraKingdoms.Meta.Shop;
using AstraKingdoms.Server.Matches;
using Microsoft.Extensions.Options;

namespace AstraKingdoms.Server.MetaHost;

/// <summary>The meta library's clock over the service's <see cref="TimeProvider"/> (so tests drive both with one fake clock).</summary>
public sealed class TimeProviderClock : IClock
{
    private readonly TimeProvider _time;
    public TimeProviderClock(TimeProvider time) => _time = time;
    public DateTimeOffset UtcNow => _time.GetUtcNow();
}

/// <summary>Rewarded ads are never offered to a player who is in a live match (ticket 63).</summary>
public sealed class RegistryMatchPresence : IMatchPresence
{
    private readonly MatchRegistry _matches;
    public RegistryMatchPresence(MatchRegistry matches) => _matches = matches;
    public bool IsInActiveMatch(string playerId) => _matches.ActiveFor(playerId) != null;
}

/// <summary>A verifier for deployments without Play billing: every purchase is a configuration error, nothing is granted.</summary>
public sealed class DisabledPurchaseVerifier : IPurchaseVerifier
{
    private const string Why = "Play billing is not configured on this service";

    public Task<PurchaseVerification> GetProductPurchaseAsync(string productId, string purchaseToken, CancellationToken ct = default) =>
        Task.FromResult(new PurchaseVerification(VerificationOutcome.ConfigurationError, error: Why));

    public Task<AcknowledgeOutcome> AcknowledgeAsync(string productId, string purchaseToken, CancellationToken ct = default) =>
        Task.FromResult(AcknowledgeOutcome.TransientError);

    public Task<VoidedPurchasesPage> ListVoidedPurchasesAsync(DateTimeOffset startTime, string pageToken, CancellationToken ct = default) =>
        Task.FromResult(new VoidedPurchasesPage(VerificationOutcome.ConfigurationError, Array.Empty<VoidedPurchase>(), null));
}

/// <summary>
/// The meta library's services hosted over the durable <see cref="MetaSqliteStore"/>: one instance
/// per process, the player id always from the authenticated session.
/// </summary>
public sealed class MetaServices
{
    public MetaServices(MetaSqliteStore store, TimeProvider time, IPurchaseVerifier verifier, IMatchPresence presence,
        IAdVerifierKeyProvider adKeys, IOptions<ServerOptions> options)
    {
        MetaOptions o = options.Value.Meta;
        Store = store;
        Clock = new TimeProviderClock(time);
        Cosmetics = CosmeticCatalog.Default;
        Catalog = StoreCatalog.Default;
        AdPolicy = AdPolicy.V1Default();
        AdPolicy.RewardedAdsEnabled = o.Ads.RewardedAdsEnabled;
        AdPolicy.FamiliesCertifiedSdkConfirmed = o.Ads.FamiliesCertifiedSdkConfirmed;
        Collection = new CollectionPolicy
        {
            ChildProductAnalyticsApproved = o.Analytics.ChildProductAnalyticsApproved,
            ChildCrashReportsApproved = o.Analytics.ChildCrashReportsApproved,
        };
        AccountIdSalt = o.Billing.AccountIdSalt ?? string.Empty;
        Progression = new ProgressionService(store, Clock);
        DailyTasks = new DailyTaskService(store, store, Clock);
        Equipment = new EquipmentService(Cosmetics, store);
        CoinShop = new CoinShopService(Cosmetics, store, Clock);
        Purchases = new PurchaseService(verifier, store, store, Catalog, Clock, new BillingOptions
        {
            AccountIdSalt = AccountIdSalt,
            AcceptTestPurchases = o.Billing.AcceptTestPurchases,
        });
        Ads = new RewardedAdService(store, store, presence, Clock, AdPolicy);
        GuestMigration = new GuestMigrationService(store, Clock);
        Ssv = new SsvSignatureVerifier(adKeys, Clock);
    }

    public MetaSqliteStore Store { get; }
    public IClock Clock { get; }
    public CosmeticCatalog Cosmetics { get; }
    public StoreCatalog Catalog { get; }
    public AdPolicy AdPolicy { get; }
    public AudiencePolicy AudiencePolicy { get; } = new();
    public ShopPurchasePolicy ShopPolicy { get; } = new();
    public CollectionPolicy Collection { get; }
    public string AccountIdSalt { get; }
    public ProgressionService Progression { get; }
    public DailyTaskService DailyTasks { get; }
    public EquipmentService Equipment { get; }
    public CoinShopService CoinShop { get; }
    public PurchaseService Purchases { get; }
    public RewardedAdService Ads { get; }
    public GuestMigrationService GuestMigration { get; }
    public SsvSignatureVerifier Ssv { get; }

    public AudienceProfile Audience(string playerId) => Store.GetAudience(playerId);

    public PlayerEntitlements Entitlements(string playerId) => Purchases.GetEntitlements(playerId);

    /// <summary>What the player owns now: defaults, level rewards, coin purchases and active paid entitlements.</summary>
    public CosmeticOwnership Ownership(string playerId)
    {
        PlayerTotals t = Store.Totals(playerId);
        return CosmeticOwnership.Resolve(Cosmetics, Catalog, ProgressionRules.LevelFor(t.Xp), t.CoinCosmetics, Entitlements(playerId));
    }
}
