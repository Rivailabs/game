using System.Reflection;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Shop;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using NUnit.Framework;

namespace AstraKingdoms.Meta.Tests;

/// <summary>Ticket 59 (cosmetics change appearance only) and ticket 60 (shop terms and audience restrictions).</summary>
public class CosmeticAndShopTests
{
    private static readonly string[] CombatWords = { "damage", "hp", "health", "speed", "mass", "radius", "multiplier", "quota", "land", "timer", "deadline", "pitch", "yaw", "power", "dodge", "cover", "hitbox", "weapon", "element" };

    [Test]
    public void CatalogueHasThreeOutfitsAndOneDefaultPerSlot()
    {
        Assert.That(CosmeticCatalog.Default.InSlot(CosmeticSlot.ArcherOutfit).Count(), Is.EqualTo(3));
        foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
            Assert.That(CosmeticCatalog.Default.DefaultFor(slot), Is.Not.Null);
        Assert.That(CatalogValidator.Validate(StoreCatalog.Default, CosmeticCatalog.Default, AdPolicy.V1Default()), Is.Empty);
    }

    [Test]
    public void CosmeticTypesExposeNoCombatInputs()
    {
        foreach (Type t in new[] { typeof(CosmeticItem), typeof(CosmeticVisual), typeof(CosmeticAppearance), typeof(StoreProduct) })
        {
            foreach (PropertyInfo p in t.GetProperties())
            {
                string name = p.Name.ToLowerInvariant();
                Assert.That(CombatWords.Any(w => name.Contains(w, StringComparison.Ordinal)), Is.False, t.Name + "." + p.Name);
                Assert.That(p.PropertyType.Namespace?.StartsWith("AstraKingdoms.Rules", StringComparison.Ordinal) ?? false, Is.False, t.Name + "." + p.Name);
            }
        }
    }

    [Test]
    public void RulesAssemblyCannotSeeCosmeticsOrPurchases()
    {
        Assembly rules = typeof(MatchEngine).Assembly;
        Assert.That(rules.GetReferencedAssemblies().Any(a => a.Name.StartsWith("AstraKingdoms.Meta", StringComparison.Ordinal)), Is.False);
        foreach (Type t in rules.GetTypes())
            foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                Assert.That((m.DeclaringType?.Namespace ?? string.Empty).Contains("Meta", StringComparison.Ordinal), Is.False);
    }

    [Test]
    public void EquippingCosmeticsDoesNotChangeAMatch()
    {
        string Play()
        {
            BotMatchRunner.SeedFor(42, 7, out byte[] seed, out string id);
            MatchEngine e = BotMatchRunner.Run(MatchConfig.V1Starter(MatchMode.Practice), seed, id,
                BotPlayer.Create(PlayerSide.A, BotDifficulty.Normal, seed), BotPlayer.Create(PlayerSide.B, BotDifficulty.Hard, seed));
            return e.Result.ToString();
        }

        string before = Play();
        var equip = new EquipmentService(CosmeticCatalog.Default, new InMemoryEquipmentStore());
        var own = CosmeticOwnership.Resolve(CosmeticCatalog.Default, StoreCatalog.Default, 20, new[] { "outfit.ember-guard" }, new PlayerEntitlements(new[] { "ak.cosmetic.sunrise_pack" }));
        Assert.That(equip.Equip("p", "outfit.ember-guard", own), Is.EqualTo(EquipResult.Equipped));
        Assert.That(equip.Equip("p", CosmeticCatalog.LevelTitle, own), Is.EqualTo(EquipResult.Equipped));
        Assert.That(Play(), Is.EqualTo(before));
    }

    [Test]
    public void OwnershipCombinesEarnedAndPaidSourcesSeparately()
    {
        var own = CosmeticOwnership.Resolve(CosmeticCatalog.Default, StoreCatalog.Default, 17, new[] { "trail.sparks", "outfit.tide-warden" },
            new PlayerEntitlements(new[] { "ak.cosmetic.outfit_tide_warden" }));
        Assert.That(own.SourceOf("banner.lotus"), Is.EqualTo(CosmeticSource.LevelReward));
        Assert.That(own.Owns("bow.starlit"), Is.False, "level 18 reward");
        Assert.That(own.SourceOf("trail.sparks"), Is.EqualTo(CosmeticSource.CoinShop));
        Assert.That(own.SourceOf("outfit.tide-warden"), Is.EqualTo(CosmeticSource.Paid), "a paid item cannot be claimed through the coin ledger");
        var noPaid = CosmeticOwnership.Resolve(CosmeticCatalog.Default, StoreCatalog.Default, 1, new[] { "outfit.tide-warden" }, PlayerEntitlements.None);
        Assert.That(noPaid.Owns("outfit.tide-warden"), Is.False);
    }

    [Test]
    public void EquipRequiresOwnershipAndRevokedItemsFallBackToDefaults()
    {
        var store = new InMemoryEquipmentStore();
        var equip = new EquipmentService(CosmeticCatalog.Default, store);
        var paid = CosmeticOwnership.Resolve(CosmeticCatalog.Default, StoreCatalog.Default, 1, null, new PlayerEntitlements(new[] { "ak.cosmetic.outfit_tide_warden" }));
        var revoked = CosmeticOwnership.Resolve(CosmeticCatalog.Default, StoreCatalog.Default, 1, null, PlayerEntitlements.None);
        Assert.That(equip.Equip("p", "outfit.ember-guard", paid), Is.EqualTo(EquipResult.NotOwned));
        Assert.That(equip.Equip("p", "nope", paid), Is.EqualTo(EquipResult.UnknownItem));
        Assert.That(equip.Equip("p", "outfit.tide-warden", paid), Is.EqualTo(EquipResult.Equipped));
        Assert.That(equip.Appearance("p", paid).Items[CosmeticSlot.ArcherOutfit].Id, Is.EqualTo("outfit.tide-warden"));
        Assert.That(equip.Appearance("p", revoked).Items[CosmeticSlot.ArcherOutfit].Id, Is.EqualTo("outfit.wanderer"));
    }

    [Test]
    public void CoinPurchasesChargeOnceAndNeverGoNegative()
    {
        var ledger = new InMemoryRewardLedgerStore();
        var shop = new CoinShopService(CosmeticCatalog.Default, ledger, T0.Clock());
        Assert.That(shop.Buy("p", "trail.sparks"), Is.EqualTo(CoinPurchaseStatus.InsufficientCoins));
        ledger.TryAppend(new RewardLedgerEntry("seed", "p", LedgerSource.DailyTask, 0, 100, T0.Noon));
        var results = new CoinPurchaseStatus[20];
        Parallel.For(0, results.Length, i => results[i] = shop.Buy("p", "trail.sparks"));
        Assert.That(results.Count(r => r == CoinPurchaseStatus.Purchased), Is.EqualTo(1));
        Assert.That(ledger.Totals("p").Coins, Is.EqualTo(20));
        Assert.That(shop.Buy("p", "bow.carved"), Is.EqualTo(CoinPurchaseStatus.InsufficientCoins));
        Assert.That(shop.Buy("p", "outfit.tide-warden"), Is.EqualTo(CoinPurchaseStatus.NotForCoins));
        Assert.That(shop.Buy("p", CosmeticCatalog.LevelTitle), Is.EqualTo(CoinPurchaseStatus.NotForCoins));
    }

    private static readonly Dictionary<string, string> Prices = new()
    {
        { "ak.cosmetic.outfit_tide_warden", "₹89.00" },
        { "ak.cosmetic.sunrise_pack", "₹129.00" },
    };

    private static IReadOnlyList<ShopOfferView> Shelf(AudienceProfile audience, IReadOnlyDictionary<string, string> prices = null, PlayerEntitlements ent = null, long coins = 0)
    {
        var own = CosmeticOwnership.Resolve(CosmeticCatalog.Default, StoreCatalog.Default, 1, null, ent ?? PlayerEntitlements.None);
        return ShopPresenter.BuildShelf(StoreCatalog.Default, CosmeticCatalog.Default, own, ent ?? PlayerEntitlements.None, coins, audience, prices ?? Prices);
    }

    [Test]
    public void PaidOffersShowStorePriceContentsAndTerms()
    {
        var paid = Shelf(AudienceProfile.Adult).Where(o => o.Currency == OfferCurrency.Paid).ToList();
        Assert.That(paid, Has.Count.EqualTo(2));
        Assert.That(paid.All(o => o.State == OfferState.Available), Is.True);
        ShopOfferView pack = paid.Single(o => o.Id == "ak.cosmetic.sunrise_pack");
        Assert.That(pack.PriceText, Is.EqualTo("₹129.00"));
        Assert.That(pack.ContentNames, Is.EquivalentTo(new[] { "Sunrise Banner", "Sunrise Bow" }));
        Assert.That(pack.Terms, Does.Contain("Cosmetic only").And.Contain("never changes damage"));
        Assert.That(pack.Terms, Does.Contain("One-time purchase"));
    }

    [Test]
    public void MissingStorePriceIsNeverGuessed()
    {
        var shelf = Shelf(AudienceProfile.Adult, new Dictionary<string, string>());
        Assert.That(shelf.Where(o => o.Currency == OfferCurrency.Paid).All(o => o.State == OfferState.PriceUnavailable && o.PriceText == null), Is.True);
    }

    [Test]
    public void CoinOffersAreMarkedProvisionalAndRespectBalance()
    {
        var coin = Shelf(AudienceProfile.Child(), coins: 90).Where(o => o.Currency == OfferCurrency.Coins).ToList();
        Assert.That(coin.All(o => o.PriceIsProvisional), Is.True);
        Assert.That(coin.Single(o => o.Id == "trail.sparks").State, Is.EqualTo(OfferState.Available), "earned coins are not restricted by age");
        Assert.That(coin.Single(o => o.Id == "outfit.ember-guard").State, Is.EqualTo(OfferState.InsufficientCoins));
    }

    [TestCase(AgeGroup.Adult, ParentalConsent.NotRequested, OfferState.Available)]
    [TestCase(AgeGroup.Unknown, ParentalConsent.NotRequested, OfferState.RequiresAgeCheck)]
    [TestCase(AgeGroup.Child, ParentalConsent.NotRequested, OfferState.RequiresParentalConsent)]
    [TestCase(AgeGroup.Child, ParentalConsent.Pending, OfferState.RequiresParentalConsent)]
    [TestCase(AgeGroup.Child, ParentalConsent.Verified, OfferState.Available)]
    [TestCase(AgeGroup.Child, ParentalConsent.Refused, OfferState.BlockedForAudience)]
    public void AudienceRestrictionsOnPaidPurchases(AgeGroup group, ParentalConsent consent, OfferState expected)
    {
        var audience = new AudienceProfile(group, consent);
        Assert.That(Shelf(audience).Where(o => o.Currency == OfferCurrency.Paid).All(o => o.State == expected), Is.True);
        PurchaseAuthorization auth = ShopPresenter.AuthorizePaid("p", "ak.cosmetic.sunrise_pack", StoreCatalog.Default, PlayerEntitlements.None, audience, null, "salt");
        Assert.That(auth.State, Is.EqualTo(expected));
        Assert.That(auth.ObfuscatedAccountId != null, Is.EqualTo(expected == OfferState.Available));
    }

    [Test]
    public void ChildPurchasesCanBeDisabledEntirely()
    {
        var policy = new ShopPurchasePolicy { AllowChildPurchasesWithVerifiedConsent = false };
        Assert.That(ShopPresenter.PaidEligibility(AudienceProfile.Child(ParentalConsent.Verified), policy), Is.EqualTo(OfferState.BlockedForAudience));
    }

    [Test]
    public void OwnedProductsAreShownAsOwnedAndCannotBeAuthorisedAgain()
    {
        var ent = new PlayerEntitlements(new[] { "ak.cosmetic.sunrise_pack" });
        Assert.That(Shelf(AudienceProfile.Adult, ent: ent).Single(o => o.Id == "ak.cosmetic.sunrise_pack").State, Is.EqualTo(OfferState.Owned));
        Assert.That(ShopPresenter.AuthorizePaid("p", "ak.cosmetic.sunrise_pack", StoreCatalog.Default, ent, AudienceProfile.Adult, null, "s").State, Is.EqualTo(OfferState.Owned));
    }

    [Test]
    public void AgeClassification()
    {
        var policy = new AudiencePolicy();
        Assert.That(policy.Classify(null), Is.EqualTo(AgeGroup.Unknown));
        Assert.That(policy.Classify(17), Is.EqualTo(AgeGroup.Child));
        Assert.That(policy.Classify(18), Is.EqualTo(AgeGroup.Adult));
        Assert.That(policy.Classify(0), Is.EqualTo(AgeGroup.Unknown));
    }

    [Test]
    public void ValidatorRejectsSellingEarnedItemsAndLootBoxes()
    {
        var bad = new StoreCatalog(new[]
        {
            new StoreProduct("ak.bad.level_title", StoreProductKind.CosmeticBundle, new[] { CosmeticCatalog.LevelTitle }, "Title", "A level reward"),
            new StoreProduct("ak.bad.mystery", StoreProductKind.CosmeticBundle, Array.Empty<string>(), "Mystery", "Random item"),
            new StoreProduct("ak.bad.consumable", StoreProductKind.CosmeticBundle, new[] { "outfit.tide-warden" }, "T", "D", consumable: true),
        });
        IReadOnlyList<string> errors = CatalogValidator.Validate(bad, CosmeticCatalog.Default, AdPolicy.V1Default());
        Assert.That(errors, Has.Some.Contains("earned item"));
        Assert.That(errors, Has.Some.Contains("must list its contents"));
        Assert.That(errors, Has.Some.Contains("non-consumable"));
        Assert.That(errors, Has.Some.Contains("bow.sunrise: paid cosmetic not sold"));
    }
}
