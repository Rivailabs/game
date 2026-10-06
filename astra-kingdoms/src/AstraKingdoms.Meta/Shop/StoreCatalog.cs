using System;
using System.Collections.Generic;
using System.Linq;

namespace AstraKingdoms.Meta.Shop
{
    /// <summary>What a paid store product delivers.</summary>
    public enum StoreProductKind : byte
    {
        /// <summary>A fixed, listed set of cosmetics (direct purchase; no random contents).</summary>
        CosmeticBundle = 0,
        /// <summary>
        /// Removes non-rewarded ads. Only valid when the ad policy approves non-rewarded ads (ticket 62);
        /// the catalogue validator rejects it otherwise.
        /// </summary>
        RemoveNonRewardedAds = 1,
    }

    /// <summary>
    /// One Play one-time (non-consumable) product. Prices are never stored here: the shop shows the
    /// localized price the store returns, so the displayed price is always the charged price.
    /// </summary>
    public sealed class StoreProduct
    {
        /// <summary>Play Console product id.</summary>
        public string Sku { get; }
        public StoreProductKind Kind { get; }
        /// <summary>Exactly what the buyer receives (cosmetic ids). Empty for remove-ads.</summary>
        public IReadOnlyList<string> Contents { get; }
        public string EnglishTitle { get; }
        /// <summary>Plain description of the content (plan: "clearly described content").</summary>
        public string EnglishDescription { get; }
        /// <summary>For remove-ads: what is removed and whether optional rewarded offers remain.</summary>
        public string RemovesDescription { get; }
        /// <summary>Always false in V1: cosmetics and remove-ads are durable, restored on reinstall.</summary>
        public bool Consumable { get; }

        public StoreProduct(string sku, StoreProductKind kind, IEnumerable<string> contents, string englishTitle, string englishDescription,
            string removesDescription = null, bool consumable = false)
        {
            Sku = sku ?? throw new ArgumentNullException(nameof(sku));
            Kind = kind;
            Contents = (contents ?? Array.Empty<string>()).ToArray();
            EnglishTitle = englishTitle;
            EnglishDescription = englishDescription;
            RemovesDescription = removesDescription;
            Consumable = consumable;
        }

        public override string ToString() => Sku;
    }

    /// <summary>The paid store catalogue.</summary>
    public sealed class StoreCatalog
    {
        private readonly Dictionary<string, StoreProduct> _bySku;

        public IReadOnlyList<StoreProduct> Products { get; }

        public StoreCatalog(IEnumerable<StoreProduct> products)
        {
            Products = products.ToArray();
            _bySku = new Dictionary<string, StoreProduct>(StringComparer.Ordinal);
            foreach (StoreProduct p in Products)
                if (!_bySku.ContainsKey(p.Sku)) _bySku.Add(p.Sku, p); // duplicates are reported by the validator
        }

        public StoreProduct Find(string sku) => sku != null && _bySku.TryGetValue(sku, out StoreProduct p) ? p : null;

        /// <summary>Terms shown next to every paid offer (draft wording; needs legal review).</summary>
        public const string StandardTerms =
            "One-time purchase. Cosmetic only: it changes how things look and never changes damage, health, land, timers, " +
            "matchmaking or which weapons you can use. Restored automatically when you sign in with the same account. " +
            "Refunds follow Google Play's refund policy.";

        /// <summary>V1 default: two direct cosmetic purchases and no remove-ads product (only rewarded ads exist).</summary>
        public static readonly StoreCatalog Default = new StoreCatalog(new[]
        {
            new StoreProduct("ak.cosmetic.outfit_tide_warden", StoreProductKind.CosmeticBundle, new[] { "outfit.tide-warden" },
                "Tide Warden outfit", "One archer outfit: Tide Warden (blue and sea-glass)."),
            new StoreProduct("ak.cosmetic.sunrise_pack", StoreProductKind.CosmeticBundle, new[] { "banner.sunrise", "bow.sunrise" },
                "Sunrise pack", "Two items: the Sunrise Banner and the Sunrise Bow skin."),
        });
    }
}
