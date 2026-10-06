using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AstraKingdoms.V2.Kingdom
{
    /// <summary>What a decoration is. Purely visual: no decoration has any effect on matches.</summary>
    public enum DecorationKind : byte
    {
        Building = 0,
        Garden = 1,
        Banner = 2,
        Trophy = 3,
    }

    /// <summary>
    /// Each homeland plot has two placement slots: a main slot (a building or a garden) and an accent
    /// slot (a banner or a trophy).
    /// </summary>
    public enum PlotSlot : byte
    {
        Main = 0,
        Accent = 1,
    }

    /// <summary>How a decoration is obtained.</summary>
    public enum DecorationSource : byte
    {
        /// <summary>Owned by every account (also the safe default shown when an asset is retired).</summary>
        Default = 0,
        /// <summary>Bought with the existing earned cosmetic coins (Meta reward ledger).</summary>
        CoinShop = 1,
        /// <summary>Only through a grant record: a milestone, pass tier, league trophy or clan milestone.</summary>
        Grant = 2,
    }

    /// <summary>One decoration. Asset keys name art the art tickets will produce.</summary>
    public sealed class DecorationItem
    {
        public string Id { get; }
        public DecorationKind Kind { get; }
        public DecorationSource Source { get; }
        public string EnglishName { get; }
        public string AssetKey { get; }
        /// <summary>Earned-coin price for <see cref="DecorationSource.CoinShop"/> items. PROPOSED.</summary>
        public int CoinPrice { get; }
        /// <summary>The catalogue version that introduced the item.</summary>
        public int IntroducedIn { get; }
        /// <summary>The catalogue version that retired its asset (0 = active).</summary>
        public int RetiredIn { get; }
        /// <summary>For a retired item: what layouts show instead (same slot), or null for the slot's safe default.</summary>
        public string ReplacementId { get; }

        public DecorationItem(string id, DecorationKind kind, DecorationSource source, string englishName, string assetKey,
            int coinPrice = 0, int introducedIn = 1, int retiredIn = 0, string replacementId = null)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Kind = kind;
            Source = source;
            EnglishName = englishName ?? id;
            AssetKey = assetKey ?? throw new ArgumentNullException(nameof(assetKey));
            CoinPrice = coinPrice;
            IntroducedIn = introducedIn;
            RetiredIn = retiredIn;
            ReplacementId = replacementId;
        }

        public PlotSlot Slot => SlotOf(Kind);
        public bool IsRetired => RetiredIn > 0;
        public string NameKey => "decor." + Id;

        public static PlotSlot SlotOf(DecorationKind kind) => kind == DecorationKind.Banner || kind == DecorationKind.Trophy ? PlotSlot.Accent : PlotSlot.Main;

        public override string ToString() => Id;
    }

    /// <summary>
    /// Versioned decoration catalogue. A new version may add items or retire an asset; it never
    /// deletes an item, so ownership records keep resolving. A retired item stays owned but a layout
    /// shows its replacement (or the slot's safe default), so a homeland never renders a missing asset.
    /// </summary>
    public sealed class DecorationCatalog
    {
        private readonly Dictionary<string, DecorationItem> _byId;

        public int Version { get; }
        public IReadOnlyList<DecorationItem> Items { get; }

        public DecorationCatalog(int version, IEnumerable<DecorationItem> items)
        {
            if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
            Version = version;
            Items = items.ToArray();
            _byId = new Dictionary<string, DecorationItem>(StringComparer.Ordinal);
            foreach (DecorationItem i in Items)
            {
                if (_byId.ContainsKey(i.Id)) throw new ArgumentException("Duplicate decoration id " + i.Id);
                _byId.Add(i.Id, i);
            }
        }

        public DecorationItem Get(string id) => id != null && _byId.TryGetValue(id, out DecorationItem i) ? i : null;
        public bool Contains(string id) => Get(id) != null;

        /// <summary>The safe default for a slot (always owned, never retired).</summary>
        public DecorationItem SafeDefault(PlotSlot slot) => Items.First(i => i.Source == DecorationSource.Default && i.Slot == slot && !i.IsRetired);

        /// <summary>
        /// The item a layout shows for a stored decoration id: itself when active; for a retired asset,
        /// its replacement chain (same slot only); otherwise the slot's safe default. Unknown ids (for
        /// example a placement written by a newer client) also resolve to the safe default.
        /// </summary>
        public DecorationItem ResolveDisplayed(string id, PlotSlot slot)
        {
            DecorationItem item = Get(id);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (item != null && item.IsRetired && item.Slot == slot && seen.Add(item.Id))
                item = Get(item.ReplacementId);
            return item != null && !item.IsRetired && item.Slot == slot ? item : SafeDefault(slot);
        }

        /// <summary>Data checks for a catalogue release. Empty when valid.</summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            foreach (PlotSlot slot in new[] { PlotSlot.Main, PlotSlot.Accent })
            {
                int n = Items.Count(i => i.Source == DecorationSource.Default && i.Slot == slot && !i.IsRetired);
                if (n != 1) errors.Add("slot " + slot + " needs exactly one active default item (found " + n + ")");
            }
            foreach (DecorationItem i in Items)
            {
                if (i.Source == DecorationSource.CoinShop && i.CoinPrice <= 0) errors.Add(i.Id + ": coin-shop item needs a positive price");
                if (i.Source != DecorationSource.CoinShop && i.CoinPrice != 0) errors.Add(i.Id + ": only coin-shop items have a price");
                if (i.Kind == DecorationKind.Trophy && i.Source == DecorationSource.CoinShop) errors.Add(i.Id + ": trophies are earned, never bought");
                if (i.IntroducedIn < 1 || i.IntroducedIn > Version) errors.Add(i.Id + ": introduced in an unknown version");
                if (i.RetiredIn != 0 && (i.RetiredIn <= i.IntroducedIn || i.RetiredIn > Version)) errors.Add(i.Id + ": retired in an invalid version");
                if (i.ReplacementId != null)
                {
                    DecorationItem r = Get(i.ReplacementId);
                    if (r == null) errors.Add(i.Id + ": replacement " + i.ReplacementId + " does not exist");
                    else if (r.Slot != i.Slot) errors.Add(i.Id + ": replacement must use the same slot");
                    if (!i.IsRetired) errors.Add(i.Id + ": only retired items name a replacement");
                }
                if (!i.Id.StartsWith("decor.", StringComparison.Ordinal)) errors.Add(i.Id + ": decoration ids start with 'decor.'");
            }
            // Replacement chains must terminate.
            foreach (DecorationItem i in Items.Where(x => x.IsRetired))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                DecorationItem cur = i;
                while (cur != null && cur.IsRetired && cur.ReplacementId != null)
                {
                    if (!seen.Add(cur.Id)) { errors.Add(i.Id + ": replacement cycle"); break; }
                    cur = Get(cur.ReplacementId);
                }
            }
            return errors;
        }

        public const string MeadowId = "decor.meadow";
        public const string PlainPennantId = "decor.pennant-plain";

        /// <summary>League trophy decoration ids (granted at season settlement).</summary>
        public static string TrophyFor(string leagueName) => "decor.trophy-" + leagueName.ToLowerInvariant();

        /// <summary>Season pass reward ids follow one pattern so pass definitions stay data.</summary>
        public static string PassRewardId(int seasonNumber, int tier, bool paid) =>
            "decor.s" + seasonNumber.ToString(CultureInfo.InvariantCulture) + "-t" + tier.ToString("00", CultureInfo.InvariantCulture) + (paid ? "-paid" : "-free");

        /// <summary>Clan cooperative milestone rewards.</summary>
        public static string ClanRewardId(int index) => "decor.clan-banner-" + index.ToString(CultureInfo.InvariantCulture);

        /// <summary>Highest season number the default catalogue carries pass rewards for.</summary>
        public const int DefaultPassSeasons = 2;

        /// <summary>
        /// V2 default catalogue, version 2. Version 2 retired the "Clay Well" asset (replaced by the
        /// Stone Well) to exercise the retirement path from day one. Prices are PROPOSED, in the
        /// same earned coins as V1 cosmetics.
        /// </summary>
        public static readonly DecorationCatalog Default = BuildDefault();

        private static DecorationCatalog BuildDefault()
        {
            var items = new List<DecorationItem>
            {
                new DecorationItem(MeadowId, DecorationKind.Garden, DecorationSource.Default, "Meadow", "kingdom/garden/meadow"),
                new DecorationItem(PlainPennantId, DecorationKind.Banner, DecorationSource.Default, "Plain Pennant", "kingdom/banner/plain"),
                new DecorationItem("decor.cottage", DecorationKind.Building, DecorationSource.CoinShop, "Cottage", "kingdom/building/cottage", coinPrice: 60),
                new DecorationItem("decor.watchtower", DecorationKind.Building, DecorationSource.CoinShop, "Watchtower", "kingdom/building/watchtower", coinPrice: 120),
                new DecorationItem("decor.temple", DecorationKind.Building, DecorationSource.CoinShop, "Small Temple", "kingdom/building/temple", coinPrice: 150),
                new DecorationItem("decor.archery-range", DecorationKind.Building, DecorationSource.CoinShop, "Archery Range", "kingdom/building/archery_range", coinPrice: 100),
                new DecorationItem("decor.well-clay", DecorationKind.Building, DecorationSource.CoinShop, "Clay Well", "kingdom/building/well_clay", coinPrice: 40,
                    introducedIn: 1, retiredIn: 2, replacementId: "decor.well-stone"),
                new DecorationItem("decor.well-stone", DecorationKind.Building, DecorationSource.CoinShop, "Stone Well", "kingdom/building/well_stone", coinPrice: 40, introducedIn: 2),
                new DecorationItem("decor.lotus-pond", DecorationKind.Garden, DecorationSource.CoinShop, "Lotus Pond", "kingdom/garden/lotus_pond", coinPrice: 80),
                new DecorationItem("decor.mango-grove", DecorationKind.Garden, DecorationSource.CoinShop, "Mango Grove", "kingdom/garden/mango_grove", coinPrice: 90),
                new DecorationItem("decor.marigold-beds", DecorationKind.Garden, DecorationSource.CoinShop, "Marigold Beds", "kingdom/garden/marigold", coinPrice: 50),
                new DecorationItem("decor.banner-agni", DecorationKind.Banner, DecorationSource.CoinShop, "Agni Banner", "kingdom/banner/agni", coinPrice: 40),
                new DecorationItem("decor.banner-varuna", DecorationKind.Banner, DecorationSource.CoinShop, "Varuna Banner", "kingdom/banner/varuna", coinPrice: 40),
                new DecorationItem("decor.banner-vayu", DecorationKind.Banner, DecorationSource.CoinShop, "Vayu Banner", "kingdom/banner/vayu", coinPrice: 40),
            };
            foreach (string league in new[] { "Bronze", "Silver", "Gold", "Diamond" })
                items.Add(new DecorationItem(TrophyFor(league), DecorationKind.Trophy, DecorationSource.Grant, league + " Trophy", "kingdom/trophy/" + league.ToLowerInvariant()));
            for (int i = 1; i <= 3; i++)
                items.Add(new DecorationItem(ClanRewardId(i), DecorationKind.Banner, DecorationSource.Grant, "Clan Banner " + i, "kingdom/banner/clan_" + i));
            DecorationKind[] cycle = { DecorationKind.Garden, DecorationKind.Banner, DecorationKind.Building, DecorationKind.Trophy };
            for (int season = 1; season <= DefaultPassSeasons; season++)
                for (int tier = 1; tier <= 20; tier++)
                    foreach (bool paid in new[] { false, true })
                    {
                        if (!paid && tier % 2 == 1) continue; // free track rewards on even tiers
                        DecorationKind kind = cycle[(tier + (paid ? 1 : 0)) % cycle.Length];
                        string id = PassRewardId(season, tier, paid);
                        items.Add(new DecorationItem(id, kind, DecorationSource.Grant, "Season " + season + " tier " + tier + (paid ? " (pass)" : string.Empty),
                            "kingdom/season" + season + "/t" + tier.ToString("00", CultureInfo.InvariantCulture) + (paid ? "_paid" : "_free")));
                    }
            return new DecorationCatalog(2, items);
        }
    }
}
