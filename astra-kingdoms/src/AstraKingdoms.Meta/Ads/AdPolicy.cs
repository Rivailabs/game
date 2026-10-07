using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Shop;

namespace AstraKingdoms.Meta.Ads
{
    public enum AdFormat : byte
    {
        /// <summary>Opt-in: the player chooses to watch for a cosmetic reward.</summary>
        Rewarded = 0,
        /// <summary>Full-screen ad the player did not ask for.</summary>
        Interstitial = 1,
        Banner = 2,
    }

    /// <summary>Where the player is. Ads are never offered or shown in <see cref="InMatch"/>.</summary>
    public enum ScreenContext : byte
    {
        Home = 0,
        Shop = 1,
        Profile = 2,
        DailyTasks = 3,
        /// <summary>The match-result screen after a match has ended (outside the match).</summary>
        MatchResult = 4,
        /// <summary>Anything from loadout entry to the final result: selection, handover, resolution, land cut.</summary>
        InMatch = 5,
    }

    public sealed class AdPlacement
    {
        public string Id { get; }
        public AdFormat Format { get; }
        public IReadOnlyList<ScreenContext> Contexts { get; }

        public AdPlacement(string id, AdFormat format, params ScreenContext[] contexts)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Format = format;
            Contexts = contexts ?? Array.Empty<ScreenContext>();
        }
    }

    /// <summary>
    /// The advertising policy (ticket 62). V1 default: optional rewarded ads outside matches only,
    /// no non-rewarded ads, therefore no remove-ads product.
    /// </summary>
    public sealed class AdPolicy
    {
        public bool RewardedAdsEnabled { get; set; } = true;
        /// <summary>True only after an owner decision approves non-rewarded ads outside matches.</summary>
        public bool NonRewardedAdsApproved { get; set; }
        public IReadOnlyList<AdPlacement> Placements { get; set; } = new[]
        {
            new AdPlacement("rewarded.home", AdFormat.Rewarded, ScreenContext.Home, ScreenContext.Shop, ScreenContext.DailyTasks),
        };
        /// <summary>Cosmetic coins per verified rewarded view (placeholder pending earning-pace measurement).</summary>
        public int RewardedCoins { get; set; } = 10;
        public int MaxRewardedPerDay { get; set; } = 5;
        /// <summary>
        /// Owner confirmation that the ad SDK and every mediation adapter are Families self-certified
        /// and configured for non-interest-based ads. Until then, children and users of unknown age
        /// receive no ad offers at all.
        /// </summary>
        public bool FamiliesCertifiedSdkConfirmed { get; set; }

        public static AdPolicy V1Default() => new AdPolicy();
    }

    /// <summary>Request flags handed to the ad SDK for one load.</summary>
    public sealed class AdRequestOptions
    {
        /// <summary>Request non-personalised (non-interest-based) ads.</summary>
        public bool NonPersonalized { get; }
        /// <summary>Child-directed treatment (COPPA-style tag) for children.</summary>
        public bool TagForChildDirectedTreatment { get; }
        /// <summary>Under-age-of-consent treatment for children and unknown age.</summary>
        public bool TagForUnderAgeOfConsent { get; }
        /// <summary>Maximum ad content rating: "G" for child-safe treatment, "PG" otherwise.</summary>
        public string MaxAdContentRating { get; }

        public AdRequestOptions(bool nonPersonalized, bool childDirected, bool underAge, string maxRating)
        {
            NonPersonalized = nonPersonalized;
            TagForChildDirectedTreatment = childDirected;
            TagForUnderAgeOfConsent = underAge;
            MaxAdContentRating = maxRating;
        }

        /// <summary>
        /// Children and unknown age always get non-personalised, G-rated, under-age requests. Adults get
        /// personalised ads only with consent.
        /// </summary>
        public static AdRequestOptions For(AudienceProfile audience, bool personalisedAdsConsent)
        {
            bool childSafe = audience == null || audience.NeedsChildSafeTreatment;
            return new AdRequestOptions(childSafe || !personalisedAdsConsent, audience?.AgeGroup == AgeGroup.Child, childSafe, childSafe ? "G" : "PG");
        }
    }

    public enum OfferDecision : byte
    {
        Available = 0,
        Disabled = 1,
        /// <summary>Never during a match.</summary>
        InMatch = 2,
        DailyCapReached = 3,
        /// <summary>Child/unknown-age player and the SDK stack is not confirmed Families-compliant.</summary>
        AudienceNotSupported = 4,
        NoPlacementHere = 5,
    }

    /// <summary>Pure decisions shared by client (what to show) and server (what to allow).</summary>
    public static class AdRules
    {
        public static OfferDecision CanOfferRewarded(AdPolicy policy, ScreenContext context, AudienceProfile audience, int rewardedToday)
        {
            if (policy == null || !policy.RewardedAdsEnabled) return OfferDecision.Disabled;
            if (context == ScreenContext.InMatch) return OfferDecision.InMatch;
            if (!policy.Placements.Any(p => p.Format == AdFormat.Rewarded && p.Contexts.Contains(context))) return OfferDecision.NoPlacementHere;
            if ((audience == null || audience.NeedsChildSafeTreatment) && !policy.FamiliesCertifiedSdkConfirmed) return OfferDecision.AudienceNotSupported;
            if (rewardedToday >= policy.MaxRewardedPerDay) return OfferDecision.DailyCapReached;
            return OfferDecision.Available;
        }

        /// <summary>
        /// Whether a non-rewarded placement may show. False in V1 (not approved); false in a match;
        /// false when the player owns the remove-ads entitlement (server ledger, so it survives reinstalls).
        /// </summary>
        public static bool MayShowNonRewarded(AdPolicy policy, StoreCatalog catalog, AdPlacement placement, ScreenContext context, PlayerEntitlements entitlements)
        {
            if (policy == null || placement == null || placement.Format == AdFormat.Rewarded) return false;
            if (!policy.NonRewardedAdsApproved || context == ScreenContext.InMatch) return false;
            if (!placement.Contexts.Contains(context)) return false;
            return !OwnsRemoveAds(catalog, entitlements);
        }

        public static bool OwnsRemoveAds(StoreCatalog catalog, PlayerEntitlements entitlements) =>
            entitlements != null && catalog.Products.Any(p => p.Kind == StoreProductKind.RemoveNonRewardedAds && entitlements.Owns(p.Sku));
    }

    /// <summary>
    /// Catalogue and policy validation (tickets 60 and 62), run in tests and at server start; a
    /// non-empty result blocks the release.
    /// </summary>
    public static class CatalogValidator
    {
        public static IReadOnlyList<string> Validate(StoreCatalog store, CosmeticCatalog cosmetics, AdPolicy ads)
        {
            var errors = new List<string>();
            var skus = new HashSet<string>(StringComparer.Ordinal);
            foreach (StoreProduct p in store.Products)
            {
                if (!skus.Add(p.Sku)) errors.Add(p.Sku + ": duplicate SKU");
                if (p.Consumable) errors.Add(p.Sku + ": V1 products must be non-consumable so they restore across reinstalls");
                if (string.IsNullOrWhiteSpace(p.EnglishTitle) || string.IsNullOrWhiteSpace(p.EnglishDescription))
                    errors.Add(p.Sku + ": title and a clear description of the content are required");
                if (p.Kind == StoreProductKind.CosmeticBundle)
                {
                    if (p.Contents.Count == 0) errors.Add(p.Sku + ": a cosmetic product must list its contents");
                    foreach (string id in p.Contents)
                    {
                        CosmeticItem item = cosmetics.Get(id);
                        if (item == null) errors.Add(p.Sku + ": unknown content " + id);
                        else if (item.Source != CosmeticSource.Paid) errors.Add(p.Sku + ": " + id + " is an earned item and cannot be sold");
                    }
                }
                else if (p.Kind == StoreProductKind.RemoveNonRewardedAds)
                {
                    if (p.Contents.Count != 0) errors.Add(p.Sku + ": remove-ads must not bundle other content");
                    bool hasNonRewarded = ads != null && ads.NonRewardedAdsApproved && ads.Placements.Any(x => x.Format != AdFormat.Rewarded);
                    if (!hasNonRewarded)
                        errors.Add(p.Sku + ": no remove-ads product while the only advertising is optional rewarded advertising");
                    if (string.IsNullOrWhiteSpace(p.RemovesDescription) || p.RemovesDescription.IndexOf("rewarded", StringComparison.OrdinalIgnoreCase) < 0)
                        errors.Add(p.Sku + ": must state exactly what it removes and whether optional rewarded offers remain");
                }
            }
            foreach (CosmeticItem c in cosmetics.Items.Where(i => i.Source == CosmeticSource.Paid))
                if (!store.Products.Any(p => p.Contents.Contains(c.Id))) errors.Add(c.Id + ": paid cosmetic not sold by any product");
            foreach (CosmeticItem c in cosmetics.Items.Where(i => i.Source == CosmeticSource.CoinShop))
                if (c.CoinPrice <= 0) errors.Add(c.Id + ": coin item needs a positive (provisional) price");
            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
                if (cosmetics.Items.Count(i => i.Slot == slot && i.Source == CosmeticSource.Default) != 1)
                    errors.Add(slot + ": exactly one default item required");

            if (ads != null)
            {
                foreach (AdPlacement pl in ads.Placements)
                {
                    if (pl.Contexts.Contains(ScreenContext.InMatch)) errors.Add(pl.Id + ": ads are never placed inside a match");
                    if (pl.Format != AdFormat.Rewarded && !ads.NonRewardedAdsApproved) errors.Add(pl.Id + ": non-rewarded placement without approval");
                }
                if (ads.RewardedCoins <= 0 || ads.RewardedCoins > 50) errors.Add("rewarded ads must grant a small cosmetic-coin reward (1-50)");
                if (ads.MaxRewardedPerDay <= 0) errors.Add("rewarded daily cap must be positive");
            }
            return errors;
        }
    }
}
