using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Economy;
using AstraKingdoms.Meta.Privacy;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Shop;

namespace AstraKingdoms.Meta.Client
{
    /// <summary>The profile screen's data.</summary>
    public sealed class ProfileView
    {
        public ProgressionProfile Progress { get; }
        public AudienceProfile Audience { get; }
        /// <summary>The next level's unlocks (empty at level 20).</summary>
        public IReadOnlyList<LevelUnlock> NextUnlocks { get; }
        public bool IsGuest { get; }

        public ProfileView(ProgressionProfile progress, AudienceProfile audience, IReadOnlyList<LevelUnlock> nextUnlocks, bool isGuest)
        {
            Progress = progress;
            Audience = audience;
            NextUnlocks = nextUnlocks;
            IsGuest = isGuest;
        }
    }

    public sealed class LockerEntry
    {
        public CosmeticItem Item { get; }
        public bool Owned { get; }
        public bool Equipped { get; }
        public CosmeticSource? OwnedVia { get; }

        public LockerEntry(CosmeticItem item, bool owned, bool equipped, CosmeticSource? ownedVia)
        {
            Item = item;
            Owned = owned;
            Equipped = equipped;
            OwnedVia = ownedVia;
        }
    }

    public sealed class LockerView
    {
        public IReadOnlyList<LockerEntry> Entries { get; }
        public CosmeticAppearance Appearance { get; }

        public LockerView(IReadOnlyList<LockerEntry> entries, CosmeticAppearance appearance)
        {
            Entries = entries;
            Appearance = appearance;
        }

        public IEnumerable<LockerEntry> InSlot(CosmeticSlot slot) => Entries.Where(e => e.Item.Slot == slot);
    }

    /// <summary>
    /// Everything the client's meta screens need, as async calls. Two implementations are planned:
    /// <see cref="LocalMetaBackend"/> (offline guest profile, editor, tests; hosts the services
    /// in-process) and an HTTP client for the online server (not written here: the server is being
    /// built separately; the method list below is the endpoint list it should expose).
    /// </summary>
    public interface IMetaBackend
    {
        Task<ProfileView> GetProfileAsync();
        /// <summary>Declared age from the neutral age screen (null = skipped → Unknown).</summary>
        Task<AudienceProfile> SetDeclaredAgeAsync(int? age);

        /// <summary>Offline/local matches only. Online grants are made by the server from the authoritative result.</summary>
        Task<MatchGrantResult> ReportLocalMatchAsync(MatchOutcomeReport report);
        Task<bool> ReportPracticeExerciseAsync(string exerciseEventId);

        Task<IReadOnlyList<DailyTaskView>> GetDailyTasksAsync();
        Task<ClaimResult> ClaimDailyTaskAsync(string taskId, string dayKey);

        Task<LockerView> GetLockerAsync();
        Task<EquipResult> EquipAsync(string cosmeticId);

        Task<IReadOnlyList<ShopOfferView>> GetShopAsync(IReadOnlyDictionary<string, string> localizedPrices);
        Task<CoinPurchaseStatus> BuyWithCoinsAsync(string cosmeticId);
        Task<PurchaseAuthorization> AuthorizePaidPurchaseAsync(string sku);
        Task<PurchaseResult> VerifyPurchaseAsync(string sku, string purchaseToken);
        Task<PlayerEntitlements> RestorePurchasesAsync(IReadOnlyList<KeyValuePair<string, string>> skuTokens);

        Task<OfferResult> RequestAdOfferAsync(ScreenContext context, bool personalisedAdsConsent);
        Task<TicketStatus> GetAdTicketStatusAsync(string ticketId);

        /// <summary>Account deletion (online) or local data deletion (guest).</summary>
        Task<DeletionState> RequestDeletionAsync();
    }
}
