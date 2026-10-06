using System;
using System.Collections.Generic;
using System.Linq;

namespace AstraKingdoms.Meta.Cosmetics
{
    /// <summary>Where a cosmetic is worn. One item per slot can be equipped.</summary>
    public enum CosmeticSlot : byte
    {
        ArcherOutfit = 0,
        BowSkin = 1,
        Banner = 2,
        ArrowTrail = 3,
        Title = 4,
    }

    /// <summary>How an item is obtained.</summary>
    public enum CosmeticSource : byte
    {
        /// <summary>Owned by every account from the start.</summary>
        Default = 0,
        /// <summary>Granted on reaching <see cref="CosmeticItem.LevelRequired"/> (levels 17-20).</summary>
        LevelReward = 1,
        /// <summary>Bought with earned cosmetic coins.</summary>
        CoinShop = 2,
        /// <summary>Contained in a paid store product (direct purchase).</summary>
        Paid = 3,
    }

    /// <summary>
    /// Pure presentation data. Deliberately has no field that could feed combat: no damage, speed,
    /// hitbox, timing or multiplier. Colours are 0xRRGGBB; the asset key selects a mesh/texture set.
    /// </summary>
    public sealed class CosmeticVisual
    {
        public string AssetKey { get; }
        public uint PrimaryRgb { get; }
        public uint SecondaryRgb { get; }

        public CosmeticVisual(string assetKey, uint primaryRgb, uint secondaryRgb)
        {
            AssetKey = assetKey ?? throw new ArgumentNullException(nameof(assetKey));
            PrimaryRgb = primaryRgb & 0xFFFFFF;
            SecondaryRgb = secondaryRgb & 0xFFFFFF;
        }
    }

    /// <summary>One cosmetic (ticket 59). Appearance only.</summary>
    public sealed class CosmeticItem
    {
        public string Id { get; }
        public CosmeticSlot Slot { get; }
        public CosmeticSource Source { get; }
        /// <summary>Localization key of the display name (falls back to <see cref="EnglishName"/>).</summary>
        public string NameKey => "cosmetic." + Id;
        public string EnglishName { get; }
        public CosmeticVisual Visual { get; }
        /// <summary>For <see cref="CosmeticSource.LevelReward"/>.</summary>
        public int LevelRequired { get; }
        /// <summary>For <see cref="CosmeticSource.CoinShop"/>. Provisional until earning pace is measured.</summary>
        public int CoinPrice { get; }
        /// <summary>Coin prices are placeholders (plan: set prices only after inventory and earning pace are measured).</summary>
        public bool PriceIsProvisional { get; }

        public CosmeticItem(string id, CosmeticSlot slot, CosmeticSource source, string englishName, CosmeticVisual visual,
            int levelRequired = 0, int coinPrice = 0, bool priceIsProvisional = true)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Slot = slot;
            Source = source;
            EnglishName = englishName ?? id;
            Visual = visual ?? throw new ArgumentNullException(nameof(visual));
            LevelRequired = levelRequired;
            CoinPrice = coinPrice;
            PriceIsProvisional = priceIsProvisional;
        }

        public override string ToString() => Id;
    }

    /// <summary>The V1 cosmetic catalogue as data, plus lookup helpers.</summary>
    public sealed class CosmeticCatalog
    {
        private readonly Dictionary<string, CosmeticItem> _byId;

        public IReadOnlyList<CosmeticItem> Items { get; }

        public CosmeticCatalog(IEnumerable<CosmeticItem> items)
        {
            Items = items.ToArray();
            _byId = new Dictionary<string, CosmeticItem>(StringComparer.Ordinal);
            foreach (CosmeticItem i in Items)
            {
                if (_byId.ContainsKey(i.Id)) throw new ArgumentException("Duplicate cosmetic id " + i.Id);
                _byId.Add(i.Id, i);
            }
        }

        public CosmeticItem Get(string id) => id != null && _byId.TryGetValue(id, out CosmeticItem i) ? i : null;
        public bool Contains(string id) => Get(id) != null;

        public IEnumerable<CosmeticItem> InSlot(CosmeticSlot slot) => Items.Where(i => i.Slot == slot);

        /// <summary>The item every account wears in a slot until it equips something else.</summary>
        public CosmeticItem DefaultFor(CosmeticSlot slot) => Items.First(i => i.Slot == slot && i.Source == CosmeticSource.Default);

        // Item ids referenced by the unlock table and the store catalogue.
        public const string LevelBanner = "banner.lotus";
        public const string LevelBow = "bow.starlit";
        public const string LevelTrail = "trail.comet";
        public const string LevelTitle = "title.astra-master";

        /// <summary>
        /// V1 starter catalogue: three archer outfits, four bow skins (one inside the paid sunrise
        /// pack), three banners, three arrow trails and two titles. Asset keys name art
        /// that the art tickets will produce; until then the client tints the grey-box fighter.
        /// </summary>
        public static readonly CosmeticCatalog Default = new CosmeticCatalog(new[]
        {
            new CosmeticItem("outfit.wanderer", CosmeticSlot.ArcherOutfit, CosmeticSource.Default, "Wanderer", new CosmeticVisual("outfit/wanderer", 0x6B5B45, 0xC9B38A)),
            new CosmeticItem("outfit.ember-guard", CosmeticSlot.ArcherOutfit, CosmeticSource.CoinShop, "Ember Guard", new CosmeticVisual("outfit/ember_guard", 0x9C2F1F, 0xE3A33B), coinPrice: 150),
            new CosmeticItem("outfit.tide-warden", CosmeticSlot.ArcherOutfit, CosmeticSource.Paid, "Tide Warden", new CosmeticVisual("outfit/tide_warden", 0x1F4E8C, 0x8FD3E8)),

            new CosmeticItem("bow.plain", CosmeticSlot.BowSkin, CosmeticSource.Default, "Plain Bow", new CosmeticVisual("bow/plain", 0x7A5230, 0x3B2A1A)),
            new CosmeticItem("bow.carved", CosmeticSlot.BowSkin, CosmeticSource.CoinShop, "Carved Bow", new CosmeticVisual("bow/carved", 0x8E6A3E, 0xD8C08A), coinPrice: 100),
            new CosmeticItem(LevelBow, CosmeticSlot.BowSkin, CosmeticSource.LevelReward, "Starlit Bow", new CosmeticVisual("bow/starlit", 0x2C2F6B, 0xF2E6A0), levelRequired: 18),
            new CosmeticItem("bow.sunrise", CosmeticSlot.BowSkin, CosmeticSource.Paid, "Sunrise Bow", new CosmeticVisual("bow/sunrise", 0xE07A2E, 0xFFD27A)),

            new CosmeticItem("banner.plain", CosmeticSlot.Banner, CosmeticSource.Default, "Plain Banner", new CosmeticVisual("banner/plain", 0x5C5C66, 0xBDBDC6)),
            new CosmeticItem(LevelBanner, CosmeticSlot.Banner, CosmeticSource.LevelReward, "Lotus Banner", new CosmeticVisual("banner/lotus", 0xC2577F, 0xF6D6E2), levelRequired: 17),
            new CosmeticItem("banner.sunrise", CosmeticSlot.Banner, CosmeticSource.Paid, "Sunrise Banner", new CosmeticVisual("banner/sunrise", 0xE07A2E, 0xFFD27A)),

            new CosmeticItem("trail.none", CosmeticSlot.ArrowTrail, CosmeticSource.Default, "No Trail", new CosmeticVisual("trail/none", 0xFFFFFF, 0xFFFFFF)),
            new CosmeticItem("trail.sparks", CosmeticSlot.ArrowTrail, CosmeticSource.CoinShop, "Spark Trail", new CosmeticVisual("trail/sparks", 0xF5C542, 0xFFF3C4), coinPrice: 80),
            new CosmeticItem(LevelTrail, CosmeticSlot.ArrowTrail, CosmeticSource.LevelReward, "Comet Trail", new CosmeticVisual("trail/comet", 0x9AD0FF, 0xFFFFFF), levelRequired: 19),

            new CosmeticItem("title.none", CosmeticSlot.Title, CosmeticSource.Default, "No Title", new CosmeticVisual("title/none", 0xFFFFFF, 0xFFFFFF)),
            new CosmeticItem(LevelTitle, CosmeticSlot.Title, CosmeticSource.LevelReward, "Astra Master", new CosmeticVisual("title/astra_master", 0xF2C14E, 0x2B2B2B), levelRequired: 20),
        });
    }
}
