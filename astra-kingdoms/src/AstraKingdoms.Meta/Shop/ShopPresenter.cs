using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;

namespace AstraKingdoms.Meta.Shop
{
    public enum OfferCurrency : byte
    {
        /// <summary>Real money through Google Play Billing.</summary>
        Paid = 0,
        /// <summary>Earned cosmetic coins.</summary>
        Coins = 1,
    }

    public enum OfferState : byte
    {
        Available = 0,
        Owned = 1,
        /// <summary>Age not known yet: ask the neutral age question before any paid purchase.</summary>
        RequiresAgeCheck = 2,
        /// <summary>A child needs verified parental consent before a paid purchase.</summary>
        RequiresParentalConsent = 3,
        /// <summary>Paid purchases are not offered to this audience (e.g. consent refused).</summary>
        BlockedForAudience = 4,
        /// <summary>The store has not returned a price (offline, product not active): never show a guessed price.</summary>
        PriceUnavailable = 5,
        InsufficientCoins = 6,
    }

    /// <summary>Owner-configurable audience restrictions for paid purchases (proposed defaults; legal review needed).</summary>
    public sealed class ShopPurchasePolicy
    {
        /// <summary>Children may buy only with verified parental consent.</summary>
        public bool AllowChildPurchasesWithVerifiedConsent { get; set; } = true;
        /// <summary>Unknown-age players must answer the age question first.</summary>
        public bool AllowUnknownAgePurchases { get; set; }
    }

    /// <summary>One offer as the shop screen draws it. Everything the player needs to decide is on the card.</summary>
    public sealed class ShopOfferView
    {
        public OfferCurrency Currency { get; }
        /// <summary>SKU for paid offers, cosmetic id for coin offers.</summary>
        public string Id { get; }
        public string Title { get; }
        public string Description { get; }
        /// <summary>Display names of exactly what is received.</summary>
        public IReadOnlyList<string> ContentNames { get; }
        /// <summary>Store-localised price ("₹89.00") or "150 coins". Null when unavailable.</summary>
        public string PriceText { get; }
        public bool PriceIsProvisional { get; }
        public string Terms { get; }
        public OfferState State { get; }

        public ShopOfferView(OfferCurrency currency, string id, string title, string description, IReadOnlyList<string> contentNames,
            string priceText, bool provisional, string terms, OfferState state)
        {
            Currency = currency;
            Id = id;
            Title = title;
            Description = description;
            ContentNames = contentNames;
            PriceText = priceText;
            PriceIsProvisional = provisional;
            Terms = terms;
            State = state;
        }

        public bool CanBuy => State == OfferState.Available;
    }

    /// <summary>The server's answer before the client may open the Play billing flow.</summary>
    public sealed class PurchaseAuthorization
    {
        public OfferState State { get; }
        /// <summary>Passed to the billing flow; without it the server will not grant the purchase.</summary>
        public string ObfuscatedAccountId { get; }

        public PurchaseAuthorization(OfferState state, string obfuscatedAccountId)
        {
            State = state;
            ObfuscatedAccountId = obfuscatedAccountId;
        }

        public bool Allowed => State == OfferState.Available;
    }

    /// <summary>
    /// Ticket 60: builds the shop shelf with clear prices and terms and enforces audience-dependent
    /// purchase restrictions. No timers, no random contents, no premium currency.
    /// </summary>
    public static class ShopPresenter
    {
        public const string CoinTerms = "Bought with coins you earned by playing. Cosmetic only. No money is involved.";

        public static OfferState PaidEligibility(AudienceProfile audience, ShopPurchasePolicy policy)
        {
            policy = policy ?? new ShopPurchasePolicy();
            switch (audience?.AgeGroup ?? AgeGroup.Unknown)
            {
                case AgeGroup.Adult:
                    return OfferState.Available;
                case AgeGroup.Child:
                    if (!policy.AllowChildPurchasesWithVerifiedConsent || audience.ParentalConsent == ParentalConsent.Refused) return OfferState.BlockedForAudience;
                    return audience.ParentalConsent == ParentalConsent.Verified ? OfferState.Available : OfferState.RequiresParentalConsent;
                default:
                    return policy.AllowUnknownAgePurchases ? OfferState.Available : OfferState.RequiresAgeCheck;
            }
        }

        /// <summary>
        /// Server-side gate run before the billing flow starts. Only an allowed player receives the
        /// obfuscated account id; a purchase made without it is not granted, stays unacknowledged and
        /// is refunded by Play automatically.
        /// </summary>
        public static PurchaseAuthorization AuthorizePaid(string playerId, string sku, StoreCatalog store, PlayerEntitlements owned,
            AudienceProfile audience, ShopPurchasePolicy policy, string accountIdSalt)
        {
            if (store.Find(sku) == null) return new PurchaseAuthorization(OfferState.PriceUnavailable, null);
            if (owned != null && owned.Owns(sku)) return new PurchaseAuthorization(OfferState.Owned, null);
            OfferState state = PaidEligibility(audience, policy);
            return state == OfferState.Available
                ? new PurchaseAuthorization(state, ObfuscatedAccountId.For(playerId, accountIdSalt))
                : new PurchaseAuthorization(state, null);
        }

        public static IReadOnlyList<ShopOfferView> BuildShelf(StoreCatalog store, CosmeticCatalog cosmetics, CosmeticOwnership ownership,
            PlayerEntitlements entitlements, long earnedCoins, AudienceProfile audience, IReadOnlyDictionary<string, string> localizedPrices,
            ShopPurchasePolicy policy = null)
        {
            var shelf = new List<ShopOfferView>();
            OfferState paidState = PaidEligibility(audience, policy);
            foreach (StoreProduct p in store.Products)
            {
                string price = localizedPrices != null && localizedPrices.TryGetValue(p.Sku, out string pr) && !string.IsNullOrWhiteSpace(pr) ? pr : null;
                OfferState state = entitlements != null && entitlements.Owns(p.Sku) ? OfferState.Owned
                    : paidState != OfferState.Available ? paidState
                    : price == null ? OfferState.PriceUnavailable
                    : OfferState.Available;
                string[] names = p.Contents.Select(id => cosmetics.Get(id)?.EnglishName ?? id).ToArray();
                string description = p.Kind == StoreProductKind.RemoveNonRewardedAds ? p.EnglishDescription + " " + p.RemovesDescription : p.EnglishDescription;
                shelf.Add(new ShopOfferView(OfferCurrency.Paid, p.Sku, p.EnglishTitle, description, names, price, false, StoreCatalog.StandardTerms, state));
            }
            foreach (CosmeticItem c in cosmetics.Items.Where(i => i.Source == CosmeticSource.CoinShop))
            {
                OfferState state = ownership.Owns(c.Id) ? OfferState.Owned : earnedCoins < c.CoinPrice ? OfferState.InsufficientCoins : OfferState.Available;
                shelf.Add(new ShopOfferView(OfferCurrency.Coins, c.Id, c.EnglishName, "Cosmetic: " + c.Slot, new[] { c.EnglishName },
                    c.CoinPrice + " coins", c.PriceIsProvisional, CoinTerms, state));
            }
            return shelf;
        }
    }
}
