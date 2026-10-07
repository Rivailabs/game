using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Shop;

namespace AstraKingdoms.Meta.Cosmetics
{
    /// <summary>Which cosmetics a player owns and why. Earned and paid sources stay distinguishable.</summary>
    public sealed class CosmeticOwnership
    {
        private readonly Dictionary<string, CosmeticSource> _owned;

        public CosmeticOwnership(IDictionary<string, CosmeticSource> owned) =>
            _owned = new Dictionary<string, CosmeticSource>(owned, StringComparer.Ordinal);

        public bool Owns(string cosmeticId) => cosmeticId != null && _owned.ContainsKey(cosmeticId);
        public IReadOnlyCollection<string> Ids => _owned.Keys;
        public CosmeticSource? SourceOf(string id) => id != null && _owned.TryGetValue(id, out CosmeticSource s) ? s : (CosmeticSource?)null;

        /// <summary>
        /// Defaults + level rewards up to <paramref name="level"/> + items bought with earned coins + the
        /// contents of active paid entitlements (a revoked purchase drops out automatically).
        /// </summary>
        public static CosmeticOwnership Resolve(CosmeticCatalog catalog, StoreCatalog store, int level, IEnumerable<string> coinPurchases,
            PlayerEntitlements paid)
        {
            var owned = new Dictionary<string, CosmeticSource>(StringComparer.Ordinal);
            foreach (CosmeticItem i in catalog.Items.Where(i => i.Source == CosmeticSource.Default)) owned[i.Id] = CosmeticSource.Default;
            foreach (string id in UnlockTable.CosmeticsUpTo(level))
                if (catalog.Contains(id)) owned[id] = CosmeticSource.LevelReward;
            foreach (string id in coinPurchases ?? Array.Empty<string>())
                if (catalog.Get(id)?.Source == CosmeticSource.CoinShop) owned[id] = CosmeticSource.CoinShop;
            if (paid != null)
                foreach (StoreProduct p in store.Products.Where(p => p.Kind == StoreProductKind.CosmeticBundle && paid.Owns(p.Sku)))
                    foreach (string id in p.Contents)
                        if (catalog.Get(id)?.Source == CosmeticSource.Paid) owned[id] = CosmeticSource.Paid;
            return new CosmeticOwnership(owned);
        }
    }

    /// <summary>
    /// What the presentation layer receives: one visual per slot. This is the only cosmetic data that
    /// reaches the client's match presentation; nothing here is an input to the rules engine.
    /// </summary>
    public sealed class CosmeticAppearance
    {
        public IReadOnlyDictionary<CosmeticSlot, CosmeticItem> Items { get; }

        public CosmeticAppearance(IReadOnlyDictionary<CosmeticSlot, CosmeticItem> items) => Items = items;

        public CosmeticVisual VisualFor(CosmeticSlot slot) => Items.TryGetValue(slot, out CosmeticItem i) ? i.Visual : null;
    }

    public interface IEquipmentStore
    {
        IReadOnlyDictionary<CosmeticSlot, string> Get(string playerId);
        void Set(string playerId, CosmeticSlot slot, string cosmeticId);
        int DeletePlayer(string playerId);
    }

    public sealed class InMemoryEquipmentStore : IEquipmentStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Dictionary<CosmeticSlot, string>> _rows = new Dictionary<string, Dictionary<CosmeticSlot, string>>(StringComparer.Ordinal);

        public IReadOnlyDictionary<CosmeticSlot, string> Get(string playerId)
        {
            lock (_gate)
                return _rows.TryGetValue(playerId, out Dictionary<CosmeticSlot, string> d)
                    ? new Dictionary<CosmeticSlot, string>(d)
                    : new Dictionary<CosmeticSlot, string>();
        }

        public void Set(string playerId, CosmeticSlot slot, string cosmeticId)
        {
            lock (_gate)
            {
                if (!_rows.TryGetValue(playerId, out Dictionary<CosmeticSlot, string> d)) _rows[playerId] = d = new Dictionary<CosmeticSlot, string>();
                d[slot] = cosmeticId;
            }
        }

        public int DeletePlayer(string playerId)
        {
            lock (_gate) return _rows.Remove(playerId) ? 1 : 0;
        }
    }

    public enum EquipResult : byte
    {
        Equipped = 0,
        UnknownItem = 1,
        NotOwned = 2,
    }

    /// <summary>Ticket 59: equip owned cosmetics; resolve the appearance (unowned or revoked items fall back to defaults).</summary>
    public sealed class EquipmentService
    {
        private readonly CosmeticCatalog _catalog;
        private readonly IEquipmentStore _store;

        public EquipmentService(CosmeticCatalog catalog, IEquipmentStore store)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public EquipResult Equip(string playerId, string cosmeticId, CosmeticOwnership ownership)
        {
            CosmeticItem item = _catalog.Get(cosmeticId);
            if (item == null) return EquipResult.UnknownItem;
            if (!ownership.Owns(cosmeticId)) return EquipResult.NotOwned;
            _store.Set(playerId, item.Slot, cosmeticId);
            return EquipResult.Equipped;
        }

        public CosmeticAppearance Appearance(string playerId, CosmeticOwnership ownership)
        {
            IReadOnlyDictionary<CosmeticSlot, string> chosen = _store.Get(playerId);
            var items = new Dictionary<CosmeticSlot, CosmeticItem>();
            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
            {
                CosmeticItem item = chosen.TryGetValue(slot, out string id) && ownership.Owns(id) ? _catalog.Get(id) : null;
                items[slot] = item ?? _catalog.DefaultFor(slot);
            }
            return new CosmeticAppearance(items);
        }
    }

    public enum CoinPurchaseStatus : byte
    {
        Purchased = 0,
        AlreadyOwned = 1,
        InsufficientCoins = 2,
        NotForCoins = 3,
    }

    /// <summary>Buying coin-shop cosmetics with earned coins: one ledger entry per (player, item), so retries never double-charge.</summary>
    public sealed class CoinShopService
    {
        private readonly CosmeticCatalog _catalog;
        private readonly IRewardLedgerStore _ledger;
        private readonly IClock _clock;

        public CoinShopService(CosmeticCatalog catalog, IRewardLedgerStore ledger, IClock clock)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public static string PurchaseKey(string playerId, string cosmeticId) => "coin-purchase:" + playerId + ":" + cosmeticId;

        public CoinPurchaseStatus Buy(string playerId, string cosmeticId)
        {
            CosmeticItem item = _catalog.Get(cosmeticId);
            if (item == null || item.Source != CosmeticSource.CoinShop || item.CoinPrice <= 0) return CoinPurchaseStatus.NotForCoins;
            var entry = new RewardLedgerEntry(PurchaseKey(playerId, cosmeticId), playerId, LedgerSource.CoinPurchase, 0, -item.CoinPrice, _clock.UtcNow,
                reference: cosmeticId, cosmeticId: cosmeticId);
            LedgerAppendResult r = _ledger.TryAppend(entry, totals => totals.Coins < item.CoinPrice ? "insufficient coins" : null);
            switch (r.Status)
            {
                case AppendStatus.Appended: return CoinPurchaseStatus.Purchased;
                case AppendStatus.Duplicate: return CoinPurchaseStatus.AlreadyOwned;
                default: return CoinPurchaseStatus.InsufficientCoins;
            }
        }
    }
}
