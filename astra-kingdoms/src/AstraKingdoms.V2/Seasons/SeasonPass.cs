using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Shop;
using AstraKingdoms.V2.Common;
using AstraKingdoms.V2.Kingdom;

namespace AstraKingdoms.V2.Seasons
{
    /// <summary>One pass tier: the cumulative points it needs and its cosmetic rewards.</summary>
    public sealed class PassTier
    {
        public int Tier { get; }
        public int PointsRequired { get; }
        /// <summary>Free-track reward, or null when the tier has none.</summary>
        public string FreeRewardId { get; }
        public string PaidRewardId { get; }

        public PassTier(int tier, int pointsRequired, string freeRewardId, string paidRewardId)
        {
            Tier = tier;
            PointsRequired = pointsRequired;
            FreeRewardId = freeRewardId;
            PaidRewardId = paidRewardId;
        }
    }

    /// <summary>
    /// A season's cosmetic pass (plan: PROPOSED one-time purchase per season, twenty cosmetic reward
    /// tiers and a free track). Points come only from completed matches of any mode; there is no
    /// daily-login, ad-watching or purchase requirement, no rating, weapon, progress multiplier or
    /// protection anywhere in this type, and tiers are cumulative so any play schedule reaches them.
    /// </summary>
    public sealed class SeasonPassDefinition
    {
        public const string CurrentVersion = "AK-PASS-1";
        public const int TierCount = 20;

        public string Version { get; }
        public string SeasonId { get; }
        public int SeasonNumber { get; }
        public string Sku { get; }
        /// <summary>Points for each eligible, normally completed match (any mode, any outcome). PROPOSED.</summary>
        public int PointsPerCompletedMatch { get; }
        public IReadOnlyList<PassTier> Tiers { get; }

        public SeasonPassDefinition(string version, SeasonDefinition season, int pointsPerCompletedMatch, IEnumerable<PassTier> tiers)
        {
            if (season == null) throw new ArgumentNullException(nameof(season));
            Version = version;
            SeasonId = season.Id;
            SeasonNumber = season.Number;
            Sku = season.PassSku;
            PointsPerCompletedMatch = pointsPerCompletedMatch;
            Tiers = tiers.OrderBy(t => t.Tier).ToArray();
        }

        /// <summary>PROPOSED default: 30 points per tier (three completed matches), 600 points = 60 matches for tier 20 over 18 days; free rewards on even tiers.</summary>
        public static SeasonPassDefinition Default(SeasonDefinition season)
        {
            var tiers = new List<PassTier>();
            for (int t = 1; t <= TierCount; t++)
                tiers.Add(new PassTier(t, t * 30, t % 2 == 0 ? DecorationCatalog.PassRewardId(season.Number, t, false) : null,
                    DecorationCatalog.PassRewardId(season.Number, t, true)));
            return new SeasonPassDefinition(CurrentVersion, season, 10, tiers);
        }

        public int EarnedTiers(int points) => Tiers.Count(t => points >= t.PointsRequired);

        public PassTier Tier(int tier) => Tiers.FirstOrDefault(t => t.Tier == tier);

        /// <summary>Completed matches needed for the last tier.</summary>
        public int MatchesForAllTiers => (Tiers[Tiers.Count - 1].PointsRequired + PointsPerCompletedMatch - 1) / PointsPerCompletedMatch;

        public IReadOnlyList<string> Validate(DecorationCatalog decorations, CosmeticCatalog metaCosmetics, int maxMatchesForAllTiers = 90)
        {
            var errors = new List<string>();
            if (Tiers.Count != TierCount) errors.Add("a pass has exactly " + TierCount + " tiers");
            for (int i = 0; i < Tiers.Count; i++)
            {
                PassTier t = Tiers[i];
                if (t.Tier != i + 1) errors.Add("tiers must be numbered 1.." + TierCount);
                if (t.PointsRequired <= (i == 0 ? 0 : Tiers[i - 1].PointsRequired)) errors.Add("tier " + t.Tier + ": requirements must increase");
                foreach (string id in new[] { t.FreeRewardId, t.PaidRewardId }.Where(x => x != null))
                {
                    DecorationItem item = decorations.Get(id);
                    if (item == null) errors.Add("tier " + t.Tier + ": unknown reward " + id);
                    else if (item.Source != DecorationSource.Grant) errors.Add("tier " + t.Tier + ": pass rewards must be grant-only cosmetics (" + id + ")");
                    // A Meta paid cosmetic would be granted wholesale by CosmeticOwnership.Resolve on purchase,
                    // bypassing "earned tiers only"; pass rewards therefore never live in the Meta catalogue.
                    if (metaCosmetics != null && metaCosmetics.Contains(id)) errors.Add("tier " + t.Tier + ": reward " + id + " collides with a Meta cosmetic");
                }
                if (t.PaidRewardId == null) errors.Add("tier " + t.Tier + ": paid track needs a reward");
            }
            if (Tiers.Count(t => t.FreeRewardId != null) == 0) errors.Add("the free track needs rewards");
            if (PointsPerCompletedMatch <= 0) errors.Add("points per match must be positive");
            if (Tiers.Count > 0 && MatchesForAllTiers > maxMatchesForAllTiers) errors.Add("tier 20 needs " + MatchesForAllTiers + " matches; cap is " + maxMatchesForAllTiers);
            return errors;
        }
    }

    /// <summary>Store products for passes: one non-consumable Play product per season, listed through Meta's catalogue.</summary>
    public static class SeasonPassStore
    {
        public static StoreProduct Product(SeasonPassDefinition pass) =>
            new StoreProduct(pass.Sku, StoreProductKind.CosmeticBundle, pass.Tiers.Select(t => t.PaidRewardId),
                "Season " + pass.SeasonNumber + " cosmetic pass",
                "Cosmetic rewards on the pass track for season " + pass.SeasonNumber + ". Tiers you have already earned are delivered at purchase; " +
                "later tiers are earned by completing matches. No rating, weapons, progress boost or protection.");

        /// <summary>The Meta store catalogue plus the given passes (what Meta's PurchaseService verifies against).</summary>
        public static StoreCatalog Extend(StoreCatalog baseCatalog, IEnumerable<SeasonPassDefinition> passes) =>
            new StoreCatalog(baseCatalog.Products.Concat(passes.Select(Product)));
    }

    /// <summary>Pass points per (season, player). Server: a row per player plus a UNIQUE(source_key) table of applied matches.</summary>
    public interface IPassProgressStore
    {
        int Points(string seasonId, string playerId);
        /// <summary>Adds points once per <paramref name="sourceKey"/>; false for a duplicate.</summary>
        bool TryAdd(string seasonId, string playerId, string sourceKey, int points);
        IReadOnlyList<string> Players(string seasonId);
        int DeletePlayer(string playerId);
    }

    public sealed class InMemoryPassProgressStore : IPassProgressStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, int> _points = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _sources = new HashSet<string>(StringComparer.Ordinal);

        private static string Key(string season, string player) => season + "|" + player;

        public int Points(string seasonId, string playerId)
        {
            lock (_gate) return _points.TryGetValue(Key(seasonId, playerId), out int p) ? p : 0;
        }

        public bool TryAdd(string seasonId, string playerId, string sourceKey, int points)
        {
            lock (_gate)
            {
                if (!_sources.Add(sourceKey)) return false;
                string k = Key(seasonId, playerId);
                _points[k] = (_points.TryGetValue(k, out int p) ? p : 0) + points;
                return true;
            }
        }

        public IReadOnlyList<string> Players(string seasonId)
        {
            string prefix = seasonId + "|";
            lock (_gate) return _points.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k.Substring(prefix.Length)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }

        public int DeletePlayer(string playerId)
        {
            lock (_gate)
            {
                string suffix = "|" + playerId;
                List<string> keys = _points.Keys.Where(k => k.EndsWith(suffix, StringComparison.Ordinal)).ToList();
                foreach (string k in keys) _points.Remove(k);
                return keys.Count;
            }
        }
    }

    public enum PassRewardState : byte
    {
        /// <summary>Not earned yet.</summary>
        Locked = 0,
        /// <summary>Earned and waiting to be claimed (delivered automatically at settlement if unclaimed).</summary>
        Claimable = 1,
        Claimed = 2,
        /// <summary>Paid track, earned, but the pass is not owned.</summary>
        RequiresPass = 3,
        /// <summary>The tier has no reward on this track.</summary>
        None = 4,
    }

    public sealed class PassTierView
    {
        public PassTier Tier { get; }
        public PassRewardState Free { get; }
        public PassRewardState Paid { get; }
        public int PointsRemaining { get; }

        public PassTierView(PassTier tier, PassRewardState free, PassRewardState paid, int pointsRemaining)
        {
            Tier = tier;
            Free = free;
            Paid = paid;
            PointsRemaining = pointsRemaining;
        }
    }

    public sealed class PassTrackView
    {
        public SeasonPassDefinition Pass { get; }
        public int Points { get; }
        public int EarnedTiers { get; }
        public bool OwnsPass { get; }
        public IReadOnlyList<PassTierView> Tiers { get; }
        public DateTimeOffset SeasonEndsAt { get; }

        public PassTrackView(SeasonPassDefinition pass, int points, int earned, bool owns, IReadOnlyList<PassTierView> tiers, DateTimeOffset endsAt)
        {
            Pass = pass;
            Points = points;
            EarnedTiers = earned;
            OwnsPass = owns;
            Tiers = tiers;
            SeasonEndsAt = endsAt;
        }
    }

    public enum CheckoutBlock : byte
    {
        None = 0,
        AlreadyOwned = 1,
        SeasonNotStarted = 2,
        /// <summary>The final 24 hours (or later): new pass sales are stopped.</summary>
        SalesClosed = 3,
        /// <summary>Age check / parental consent / audience rule from Meta's shop policy.</summary>
        Audience = 4,
    }

    /// <summary>
    /// Everything the pass checkout shows before the store sheet (plan: "Show the closing date, earned
    /// tiers and remaining requirements before checkout").
    /// </summary>
    public sealed class PassCheckoutPreview
    {
        public string SeasonId { get; }
        public string Sku { get; }
        /// <summary>When new pass sales stop (24 h before the end).</summary>
        public DateTimeOffset SalesCloseAt { get; }
        /// <summary>When the season (and earning) closes.</summary>
        public DateTimeOffset SeasonEndsAt { get; }
        public int Points { get; }
        public int EarnedTiers { get; }
        /// <summary>Paid rewards delivered immediately on purchase because their tiers are already earned.</summary>
        public IReadOnlyList<string> DeliveredOnPurchase { get; }
        /// <summary>Remaining tiers and the points each still needs.</summary>
        public IReadOnlyList<KeyValuePair<int, int>> RemainingRequirements { get; }
        public int CompletedMatchesForAllTiers { get; }
        public CheckoutBlock Block { get; }
        public OfferState AudienceState { get; }
        public bool CanPurchase => Block == CheckoutBlock.None;

        public PassCheckoutPreview(string seasonId, string sku, DateTimeOffset salesCloseAt, DateTimeOffset endsAt, int points, int earned,
            IReadOnlyList<string> delivered, IReadOnlyList<KeyValuePair<int, int>> remaining, int matchesForAll, CheckoutBlock block, OfferState audience)
        {
            SeasonId = seasonId;
            Sku = sku;
            SalesCloseAt = salesCloseAt;
            SeasonEndsAt = endsAt;
            Points = points;
            EarnedTiers = earned;
            DeliveredOnPurchase = delivered;
            RemainingRequirements = remaining;
            CompletedMatchesForAllTiers = matchesForAll;
            Block = block;
            AudienceState = audience;
        }
    }

    public enum ClaimStatus : byte
    {
        Claimed = 0,
        AlreadyClaimed = 1,
        NotEarned = 2,
        RequiresPass = 3,
        NoReward = 4,
        UnknownSeason = 5,
    }

    /// <summary>Totals of one settlement or purchase delivery (for reconciliation reports).</summary>
    public sealed class DeliveryReport
    {
        public int FreeDelivered { get; set; }
        public int PaidDelivered { get; set; }
        public int AlreadyHeld { get; set; }
        public List<string> Keys { get; } = new List<string>();
    }

    /// <summary>
    /// The season pass. Ownership is read from Meta's paid entitlement ledger (the pass SKU), so a
    /// purchase is verified, acknowledged, restored and voided exactly like every other V1 purchase;
    /// rewards are V2 grant records with unique keys.
    /// </summary>
    public sealed class SeasonPassService
    {
        private readonly SeasonCalendar _calendar;
        private readonly Dictionary<string, SeasonPassDefinition> _passes = new Dictionary<string, SeasonPassDefinition>(StringComparer.Ordinal);
        private readonly IPassProgressStore _progress;
        private readonly IGrantLedgerStore _grants;
        private readonly IEntitlementLedgerStore _entitlements;
        private readonly IClock _clock;
        private readonly Func<string, bool> _isSettled;

        public SeasonPassService(SeasonCalendar calendar, IEnumerable<SeasonPassDefinition> passes, IPassProgressStore progress, IGrantLedgerStore grants,
            IEntitlementLedgerStore entitlements, IClock clock, Func<string, bool> isSettled)
        {
            _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
            foreach (SeasonPassDefinition p in passes ?? Array.Empty<SeasonPassDefinition>()) _passes[p.SeasonId] = p;
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
            _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _isSettled = isSettled ?? (_ => false);
        }

        public void AddPass(SeasonPassDefinition pass) => _passes[pass.SeasonId] = pass;
        public SeasonPassDefinition Pass(string seasonId) => seasonId != null && _passes.TryGetValue(seasonId, out var p) ? p : null;
        public IReadOnlyCollection<SeasonPassDefinition> Passes => _passes.Values;

        public static string PointsKey(string seasonId, string matchResultId, string playerId) => "pass-pts:" + seasonId + ":" + matchResultId + ":" + playerId;

        public bool OwnsPass(string playerId, string seasonId)
        {
            SeasonPassDefinition pass = Pass(seasonId);
            return pass != null && PlayerEntitlements.Fold(_entitlements.ForPlayer(playerId)).Owns(pass.Sku);
        }

        public int Points(string playerId, string seasonId) => _progress.Points(seasonId, playerId);

        /// <summary>
        /// Adds pass points for one match result. Eligible = Meta's reward eligibility (not automation,
        /// developer test or invalid; normally completed). Points go to the season the match completed in
        /// and stop at the season end; repeated reports of the same result add nothing.
        /// </summary>
        public bool RecordMatch(MatchOutcomeReport report)
        {
            if (report == null || RewardCalculator.Eligibility(report) != GrantEligibility.Eligible) return false;
            SeasonDefinition season = _calendar.At(report.CompletedAt);
            if (season == null || _isSettled(season.Id)) return false;
            SeasonPassDefinition pass = Pass(season.Id);
            if (pass == null) return false;
            return _progress.TryAdd(season.Id, report.PlayerId, PointsKey(season.Id, report.MatchResultId, report.PlayerId), pass.PointsPerCompletedMatch);
        }

        public PassTrackView View(string playerId, string seasonId)
        {
            SeasonPassDefinition pass = Pass(seasonId) ?? throw new ArgumentException("no pass for " + seasonId);
            int points = Points(playerId, seasonId);
            bool owns = OwnsPass(playerId, seasonId);
            var tiers = pass.Tiers.Select(t =>
            {
                bool earned = points >= t.PointsRequired;
                PassRewardState free = t.FreeRewardId == null ? PassRewardState.None
                    : !earned ? PassRewardState.Locked
                    : _grants.Find(GrantKeys.PassTier(seasonId, playerId, t.Tier, false)) != null ? PassRewardState.Claimed : PassRewardState.Claimable;
                PassRewardState paid = !earned ? PassRewardState.Locked
                    : _grants.Find(GrantKeys.PassTier(seasonId, playerId, t.Tier, true)) != null ? PassRewardState.Claimed
                    : owns ? PassRewardState.Claimable : PassRewardState.RequiresPass;
                return new PassTierView(t, free, paid, Math.Max(0, t.PointsRequired - points));
            }).ToArray();
            return new PassTrackView(pass, points, pass.EarnedTiers(points), owns, tiers, _calendar.Get(seasonId).EndsAt);
        }

        public ClaimStatus Claim(string playerId, string seasonId, int tier, bool paidTrack)
        {
            SeasonPassDefinition pass = Pass(seasonId);
            if (pass == null) return ClaimStatus.UnknownSeason;
            PassTier t = pass.Tier(tier);
            if (t == null) return ClaimStatus.NoReward;
            string item = paidTrack ? t.PaidRewardId : t.FreeRewardId;
            if (item == null) return ClaimStatus.NoReward;
            string key = GrantKeys.PassTier(seasonId, playerId, tier, paidTrack);
            if (_grants.Find(key) != null) return ClaimStatus.AlreadyClaimed;
            if (Points(playerId, seasonId) < t.PointsRequired) return ClaimStatus.NotEarned;
            if (paidTrack && !OwnsPass(playerId, seasonId)) return ClaimStatus.RequiresPass;
            return _grants.TryAppend(new GrantRecord(key, playerId, paidTrack ? GrantSource.PassPaid : GrantSource.PassFree, item, seasonId + "/t" + tier, _clock.UtcNow)).Appended
                ? ClaimStatus.Claimed
                : ClaimStatus.AlreadyClaimed;
        }

        /// <summary>The pre-checkout summary. Uses Meta's audience rules for paid purchases.</summary>
        public PassCheckoutPreview Preview(string playerId, string seasonId, AudienceProfile audience, ShopPurchasePolicy policy)
        {
            SeasonPassDefinition pass = Pass(seasonId) ?? throw new ArgumentException("no pass for " + seasonId);
            SeasonDefinition season = _calendar.Get(seasonId);
            DateTimeOffset now = _clock.UtcNow;
            int points = Points(playerId, seasonId);
            OfferState audienceState = ShopPresenter.PaidEligibility(audience, policy);
            CheckoutBlock block = CheckoutBlock.None;
            if (OwnsPass(playerId, seasonId)) block = CheckoutBlock.AlreadyOwned;
            else if (now < season.StartsAt) block = CheckoutBlock.SeasonNotStarted;
            else if (now >= season.PassSalesCloseAt || _isSettled(seasonId)) block = CheckoutBlock.SalesClosed;
            else if (audienceState != OfferState.Available) block = CheckoutBlock.Audience;
            var delivered = pass.Tiers.Where(t => points >= t.PointsRequired).Select(t => t.PaidRewardId).ToArray();
            var remaining = pass.Tiers.Where(t => points < t.PointsRequired).Select(t => new KeyValuePair<int, int>(t.Tier, t.PointsRequired - points)).ToArray();
            return new PassCheckoutPreview(seasonId, pass.Sku, season.PassSalesCloseAt, season.EndsAt, points, pass.EarnedTiers(points), delivered, remaining,
                pass.MatchesForAllTiers, block, audienceState);
        }

        /// <summary>
        /// Server-side gate before the billing flow: the sales window (not in the final 24 hours), then
        /// Meta's paid-purchase authorization (ownership, audience/consent, obfuscated account id).
        /// </summary>
        public PurchaseAuthorization AuthorizeCheckout(string playerId, string seasonId, AudienceProfile audience, ShopPurchasePolicy policy,
            StoreCatalog storeWithPasses, string accountIdSalt)
        {
            PassCheckoutPreview preview = Preview(playerId, seasonId, audience, policy);
            switch (preview.Block)
            {
                case CheckoutBlock.AlreadyOwned: return new PurchaseAuthorization(OfferState.Owned, null);
                case CheckoutBlock.SeasonNotStarted:
                case CheckoutBlock.SalesClosed: return new PurchaseAuthorization(OfferState.PriceUnavailable, null);
            }
            return ShopPresenter.AuthorizePaid(playerId, preview.Sku, storeWithPasses, PlayerEntitlements.Fold(_entitlements.ForPlayer(playerId)), audience,
                policy, accountIdSalt);
        }

        /// <summary>
        /// Delivers every already-earned paid-track reward once the pass entitlement exists. Call after
        /// Meta's PurchaseService reports Granted or AlreadyOwned, on restore, and on any pass view.
        /// It also covers a late purchase (a pending payment verified after settlement): the player paid,
        /// so the earned paid rewards are delivered then. Idempotent.
        /// </summary>
        public DeliveryReport DeliverPaidForOwner(string playerId, string seasonId)
        {
            var report = new DeliveryReport();
            if (!OwnsPass(playerId, seasonId)) return report;
            SeasonPassDefinition pass = Pass(seasonId);
            int points = Points(playerId, seasonId);
            foreach (PassTier t in pass.Tiers.Where(t => points >= t.PointsRequired))
                Deliver(playerId, seasonId, t, true, report);
            return report;
        }

        /// <summary>
        /// Settlement: automatically deliver every earned, unclaimed reward (free track always; paid
        /// track when the pass is owned). Unearned tiers are not entitlements and are not delivered.
        /// </summary>
        public DeliveryReport DeliverUnclaimedAtSettlement(string seasonId)
        {
            var report = new DeliveryReport();
            SeasonPassDefinition pass = Pass(seasonId);
            if (pass == null) return report;
            foreach (string player in _progress.Players(seasonId))
            {
                int points = Points(player, seasonId);
                bool owns = OwnsPass(player, seasonId);
                foreach (PassTier t in pass.Tiers.Where(t => points >= t.PointsRequired))
                {
                    if (t.FreeRewardId != null) Deliver(player, seasonId, t, false, report);
                    if (owns) Deliver(player, seasonId, t, true, report);
                }
            }
            return report;
        }

        private void Deliver(string playerId, string seasonId, PassTier t, bool paid, DeliveryReport report)
        {
            string item = paid ? t.PaidRewardId : t.FreeRewardId;
            if (item == null) return;
            string key = GrantKeys.PassTier(seasonId, playerId, t.Tier, paid);
            GrantAppendResult r = _grants.TryAppend(new GrantRecord(key, playerId, paid ? GrantSource.PassPaid : GrantSource.PassFree, item, seasonId + "/t" + t.Tier, _clock.UtcNow));
            if (!r.Appended) { report.AlreadyHeld++; return; }
            if (paid) report.PaidDelivered++; else report.FreeDelivered++;
            report.Keys.Add(key);
        }

        public int DeletePlayer(string playerId) => _progress.DeletePlayer(playerId);
    }
}
