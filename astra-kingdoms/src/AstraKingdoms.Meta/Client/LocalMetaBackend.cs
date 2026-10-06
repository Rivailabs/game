using System;
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
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Meta.Client
{
    /// <summary>Where the offline guest profile is saved (Unity: a file under persistentDataPath).</summary>
    public interface ILocalPersistence
    {
        string Load();
        void Save(string json);
        void Delete();
    }

    public sealed class MemoryPersistence : ILocalPersistence
    {
        public string Text;
        public string Load() => Text;
        public void Save(string json) => Text = json;
        public void Delete() => Text = null;
    }

    /// <summary>
    /// In-process backend for the offline guest profile (and the editor/tests). It hosts the same
    /// services the server hosts, over in-memory stores saved to <see cref="ILocalPersistence"/>.
    /// Local progress stays a separate guest profile: it moves to an account only through the server's
    /// capped <see cref="GuestMigrationService"/>, using <see cref="LocalMatchSummaries"/> as evidence.
    /// Paid purchases need a verifier; without one (the default) they are unavailable offline.
    /// </summary>
    public sealed class LocalMetaBackend : IMetaBackend
    {
        public const int MaxStoredSummaries = 200;

        private readonly ILocalPersistence _persistence;
        private readonly IClock _clock;
        private readonly InMemoryRewardLedgerStore _ledger = new InMemoryRewardLedgerStore();
        private readonly InMemoryDailyTaskProgressStore _daily = new InMemoryDailyTaskProgressStore();
        private readonly InMemoryEquipmentStore _equipment = new InMemoryEquipmentStore();
        private readonly InMemoryEntitlementLedgerStore _entitlements = new InMemoryEntitlementLedgerStore();
        private readonly InMemoryAdTicketStore _tickets = new InMemoryAdTicketStore();
        private readonly List<LocalMatchSummary> _summaries = new List<LocalMatchSummary>();
        private readonly ProgressionService _progression;
        private readonly DailyTaskService _tasks;
        private readonly EquipmentService _equip;
        private readonly CoinShopService _coinShop;
        private readonly PurchaseService _purchases;
        private readonly RewardedAdService _ads;
        private readonly AudiencePolicy _audiencePolicy = new AudiencePolicy();
        private AudienceProfile _audience = AudienceProfile.Unknown;

        public string PlayerId { get; private set; }
        public CosmeticCatalog Cosmetics { get; }
        public StoreCatalog Store { get; }
        public AdPolicy AdPolicy { get; }
        public ShopPurchasePolicy ShopPolicy { get; } = new ShopPurchasePolicy();
        public string AccountIdSalt { get; }
        /// <summary>Saved analytics consent (the analytics client reads it).</summary>
        public Analytics.AnalyticsConsent AnalyticsConsent { get; set; } = Analytics.AnalyticsConsent.NotAsked;
        public bool PersonalisedAdsConsent { get; set; }

        private sealed class DelegatePresence : IMatchPresence
        {
            private readonly Func<bool> _inMatch;
            public DelegatePresence(Func<bool> inMatch) => _inMatch = inMatch;
            public bool IsInActiveMatch(string playerId) => _inMatch();
        }

        public LocalMetaBackend(ILocalPersistence persistence, IClock clock, Func<bool> isInMatch, IPurchaseVerifier devVerifier = null,
            CosmeticCatalog cosmetics = null, StoreCatalog store = null, AdPolicy adPolicy = null, string accountIdSalt = "local-guest")
        {
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Cosmetics = cosmetics ?? CosmeticCatalog.Default;
            Store = store ?? StoreCatalog.Default;
            AdPolicy = adPolicy ?? AdPolicy.V1Default();
            AccountIdSalt = accountIdSalt;
            _progression = new ProgressionService(_ledger, clock);
            _tasks = new DailyTaskService(_ledger, _daily, clock);
            _equip = new EquipmentService(Cosmetics, _equipment);
            _coinShop = new CoinShopService(Cosmetics, _ledger, clock);
            if (devVerifier != null)
                _purchases = new PurchaseService(devVerifier, _entitlements, new InMemoryAcknowledgementQueue(), Store, clock,
                    new BillingOptions { AccountIdSalt = accountIdSalt });
            _ads = new RewardedAdService(_ledger, _tickets, new DelegatePresence(isInMatch ?? (() => false)), clock, AdPolicy);
            Load();
        }

        public IReadOnlyList<LocalMatchSummary> LocalMatchSummaries => _summaries.ToArray();
        public PurchaseService Purchases => _purchases;
        public RewardedAdService Ads => _ads;

        // ---------------------------------------------------------------- profile and progression

        public Task<ProfileView> GetProfileAsync()
        {
            ProgressionProfile p = _progression.GetProfile(PlayerId);
            IReadOnlyList<LevelUnlock> next = p.Level >= ProgressionRules.MaxLevel ? Array.Empty<LevelUnlock>() : UnlockTable.AtLevel(p.Level + 1);
            return Task.FromResult(new ProfileView(p, _audience, next, true));
        }

        public Task<AudienceProfile> SetDeclaredAgeAsync(int? age)
        {
            AgeGroup g = _audiencePolicy.Classify(age);
            _audience = new AudienceProfile(g, g == AgeGroup.Child ? _audience.ParentalConsent : ParentalConsent.NotRequested);
            Save();
            return Task.FromResult(_audience);
        }

        public AudienceProfile Audience => _audience;

        public Task<MatchGrantResult> ReportLocalMatchAsync(MatchOutcomeReport report)
        {
            if (report.PlayerId != PlayerId)
                report = new MatchOutcomeReport(report.MatchResultId, PlayerId, report.Kind, report.Outcome, report.Ending, report.CompletedAt,
                    report.WeaponsUsed, report.Catalog, report.IsAutomation, report.IsDeveloperTest, report.IsValid);
            MatchGrantResult r = _progression.GrantForMatch(report);
            _tasks.RecordMatch(report);
            if (r.Status == GrantStatus.Granted && !_summaries.Any(s => s.MatchResultId == report.MatchResultId))
            {
                _summaries.Add(new LocalMatchSummary(report.MatchResultId, report.Kind, report.Outcome, report.Ending, report.CompletedAt,
                    report.IsAutomation, report.IsDeveloperTest));
                if (_summaries.Count > MaxStoredSummaries) _summaries.RemoveAt(0);
            }
            Save();
            return Task.FromResult(r);
        }

        public Task<bool> ReportPracticeExerciseAsync(string exerciseEventId)
        {
            bool changed = _tasks.RecordPracticeExercise(PlayerId, exerciseEventId, _clock.UtcNow);
            Save();
            return Task.FromResult(changed);
        }

        // ---------------------------------------------------------------- daily tasks

        public Task<IReadOnlyList<DailyTaskView>> GetDailyTasksAsync() => Task.FromResult(_tasks.GetTasks(PlayerId));

        public Task<ClaimResult> ClaimDailyTaskAsync(string taskId, string dayKey)
        {
            ClaimResult r = _tasks.Claim(PlayerId, taskId, dayKey);
            Save();
            return Task.FromResult(r);
        }

        // ---------------------------------------------------------------- cosmetics and shop

        private CosmeticOwnership Ownership()
        {
            PlayerTotals t = _ledger.Totals(PlayerId);
            return CosmeticOwnership.Resolve(Cosmetics, Store, ProgressionRules.LevelFor(t.Xp), t.CoinCosmetics, Entitlements());
        }

        private PlayerEntitlements Entitlements() => _purchases?.GetEntitlements(PlayerId) ?? PlayerEntitlements.None;

        public Task<LockerView> GetLockerAsync()
        {
            CosmeticOwnership own = Ownership();
            CosmeticAppearance look = _equip.Appearance(PlayerId, own);
            LockerEntry[] entries = Cosmetics.Items.Select(i => new LockerEntry(i, own.Owns(i.Id),
                look.Items.TryGetValue(i.Slot, out CosmeticItem eq) && eq.Id == i.Id, own.SourceOf(i.Id))).ToArray();
            return Task.FromResult(new LockerView(entries, look));
        }

        public Task<EquipResult> EquipAsync(string cosmeticId)
        {
            EquipResult r = _equip.Equip(PlayerId, cosmeticId, Ownership());
            Save();
            return Task.FromResult(r);
        }

        public Task<IReadOnlyList<ShopOfferView>> GetShopAsync(IReadOnlyDictionary<string, string> localizedPrices)
        {
            IReadOnlyDictionary<string, string> prices = _purchases == null ? new Dictionary<string, string>() : localizedPrices;
            return Task.FromResult(ShopPresenter.BuildShelf(Store, Cosmetics, Ownership(), Entitlements(), _ledger.Totals(PlayerId).Coins,
                _audience, prices, ShopPolicy));
        }

        public Task<CoinPurchaseStatus> BuyWithCoinsAsync(string cosmeticId)
        {
            CoinPurchaseStatus s = _coinShop.Buy(PlayerId, cosmeticId);
            Save();
            return Task.FromResult(s);
        }

        public Task<PurchaseAuthorization> AuthorizePaidPurchaseAsync(string sku)
        {
            if (_purchases == null) return Task.FromResult(new PurchaseAuthorization(OfferState.PriceUnavailable, null));
            return Task.FromResult(ShopPresenter.AuthorizePaid(PlayerId, sku, Store, Entitlements(), _audience, ShopPolicy, AccountIdSalt));
        }

        public async Task<PurchaseResult> VerifyPurchaseAsync(string sku, string purchaseToken)
        {
            if (_purchases == null) return new PurchaseResult(PurchaseStatus.RetryLater, sku, detail: "purchases need the online service");
            return await _purchases.HandlePurchaseAsync(PlayerId, sku, purchaseToken).ConfigureAwait(false);
        }

        public async Task<PlayerEntitlements> RestorePurchasesAsync(IReadOnlyList<KeyValuePair<string, string>> skuTokens)
        {
            if (_purchases == null) return PlayerEntitlements.None;
            return await _purchases.RestoreAsync(PlayerId, skuTokens).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- rewarded ads

        public Task<OfferResult> RequestAdOfferAsync(ScreenContext context, bool personalisedAdsConsent) =>
            Task.FromResult(_ads.IssueOffer(PlayerId, context, _audience, personalisedAdsConsent));

        public Task<TicketStatus> GetAdTicketStatusAsync(string ticketId) => Task.FromResult(_ads.GetTicketStatus(PlayerId, ticketId));

        /// <summary>
        /// DEVELOPMENT ONLY: stands in for the ad network's signed server callback in editor runs.
        /// Production rewards come only from <c>SsvSignatureVerifier</c> on the server.
        /// </summary>
        public CallbackOutcome DevelopmentSimulateVerifiedCallback(string ticketId, string transactionId)
        {
            CallbackOutcome o = _ads.HandleVerifiedCallback(new SsvCallback(transactionId, PlayerId, ticketId, "dev", AdPolicy.RewardedCoins, "coins", _clock.UtcNow));
            Save();
            return o;
        }

        // ---------------------------------------------------------------- deletion

        /// <summary>Guest profile: deletes everything stored on this device and starts a fresh profile id.</summary>
        public Task<DeletionState> RequestDeletionAsync()
        {
            _ledger.DeletePlayer(PlayerId);
            _daily.DeletePlayer(PlayerId);
            _equipment.DeletePlayer(PlayerId);
            _tickets.DeletePlayer(PlayerId);
            _summaries.Clear();
            _audience = AudienceProfile.Unknown;
            AnalyticsConsent = Analytics.AnalyticsConsent.NotAsked;
            PersonalisedAdsConsent = false;
            _persistence.Delete();
            PlayerId = NewGuestId();
            Save();
            return Task.FromResult(DeletionState.Completed);
        }

        // ---------------------------------------------------------------- persistence

        private static string NewGuestId() => "guest-" + Guid.NewGuid().ToString("N");

        public void Save()
        {
            JsonNode root = JsonNode.Object().Add("v", 1).Add("player", PlayerId)
                .Add("age", (long)_audience.AgeGroup).Add("parental", (long)_audience.ParentalConsent)
                .Add("consentProduct", (long)AnalyticsConsent.ProductAnalytics).Add("consentCrash", (long)AnalyticsConsent.CrashReports)
                .Add("personalisedAds", PersonalisedAdsConsent);
            JsonNode ledger = JsonNode.Array();
            foreach (RewardLedgerEntry e in _ledger.AllEntries())
            {
                JsonNode w = JsonNode.Array();
                foreach (int id in e.WeaponsUsed) w.Push(JsonNode.Of(id));
                ledger.Push(JsonNode.Object().Add("k", e.IdempotencyKey).Add("p", e.PlayerId).Add("s", (long)e.Source).Add("xp", e.XpDelta)
                    .Add("c", e.CoinDelta).Add("t", e.At.ToUnixTimeMilliseconds()).Add("r", e.Reference).Add("cos", e.CosmeticId)
                    .Add("fix", e.CorrectsKey).Add("w", w));
            }
            root.Add("ledger", ledger);
            JsonNode daily = JsonNode.Array();
            foreach (KeyValuePair<string, DailyProgress> row in _daily.Rows())
            {
                JsonNode m = JsonNode.Array(), el = JsonNode.Array(), pr = JsonNode.Array();
                foreach (string s in row.Value.CountedMatches) m.Push(JsonNode.Of(s));
                foreach (Element x in row.Value.Elements) el.Push(JsonNode.Of((long)x));
                foreach (string s in row.Value.PracticeEvents) pr.Push(JsonNode.Of(s));
                daily.Push(JsonNode.Object().Add("key", row.Key).Add("m", m).Add("e", el).Add("p", pr));
            }
            root.Add("daily", daily);
            JsonNode equip = JsonNode.Object();
            foreach (KeyValuePair<CosmeticSlot, string> kv in _equipment.Get(PlayerId)) equip.Add(((int)kv.Key).ToString(System.Globalization.CultureInfo.InvariantCulture), kv.Value);
            root.Add("equip", equip);
            JsonNode sums = JsonNode.Array();
            foreach (LocalMatchSummary s in _summaries)
                sums.Push(JsonNode.Object().Add("id", s.MatchResultId).Add("k", (long)s.Kind).Add("o", (long)s.Outcome).Add("e", (long)s.Ending)
                    .Add("t", s.CompletedAt.ToUnixTimeMilliseconds()));
            root.Add("summaries", sums);
            _persistence.Save(root.ToCanonicalString());
        }

        private void Load()
        {
            string text = _persistence.Load();
            PlayerId = NewGuestId();
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                JsonNode root = JsonNode.Parse(text);
                PlayerId = JsonRead.String(root, "player") ?? PlayerId;
                _audience = new AudienceProfile((AgeGroup)(JsonRead.Int(root, "age") ?? 0), (ParentalConsent)(JsonRead.Int(root, "parental") ?? 0));
                AnalyticsConsent = new Analytics.AnalyticsConsent((Analytics.ConsentChoice)(JsonRead.Int(root, "consentProduct") ?? 0),
                    (Analytics.ConsentChoice)(JsonRead.Int(root, "consentCrash") ?? 0));
                JsonNode pa = JsonRead.Member(root, "personalisedAds");
                PersonalisedAdsConsent = pa != null && pa.Kind == JsonKind.Bool && pa.BoolValue;
                var entries = new List<RewardLedgerEntry>();
                long seq = 0;
                foreach (JsonNode e in JsonRead.Member(root, "ledger")?.Items ?? (IReadOnlyList<JsonNode>)Array.Empty<JsonNode>())
                {
                    var weapons = (JsonRead.Member(e, "w")?.Items ?? (IReadOnlyList<JsonNode>)Array.Empty<JsonNode>()).Select(n => n.AsInt()).ToArray();
                    entries.Add(new RewardLedgerEntry(JsonRead.String(e, "k"), JsonRead.String(e, "p"), (LedgerSource)(JsonRead.Int(e, "s") ?? 0),
                        JsonRead.Int(e, "xp") ?? 0, JsonRead.Int(e, "c") ?? 0, DateTimeOffset.FromUnixTimeMilliseconds(JsonRead.Long(e, "t") ?? 0),
                        JsonRead.String(e, "r"), JsonRead.String(e, "cos"), weapons, JsonRead.String(e, "fix"), ++seq));
                }
                _ledger.Load(entries);
                foreach (JsonNode d in JsonRead.Member(root, "daily")?.Items ?? (IReadOnlyList<JsonNode>)Array.Empty<JsonNode>())
                {
                    string key = JsonRead.String(d, "key");
                    int bar = key?.LastIndexOf('|') ?? -1;
                    if (bar <= 0) continue;
                    var progress = new DailyProgress(
                        JsonRead.Member(d, "m").Items.Select(n => n.AsString()),
                        JsonRead.Member(d, "e").Items.Select(n => (Element)n.AsInt()),
                        JsonRead.Member(d, "p").Items.Select(n => n.AsString()));
                    _daily.Restore(key.Substring(0, bar), key.Substring(bar + 1), progress);
                }
                JsonNode equip = JsonRead.Member(root, "equip");
                if (equip != null && equip.Kind == JsonKind.Object)
                    foreach (KeyValuePair<string, JsonNode> kv in equip.Members)
                        _equipment.Set(PlayerId, (CosmeticSlot)int.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), kv.Value.AsString());
                foreach (JsonNode s in JsonRead.Member(root, "summaries")?.Items ?? (IReadOnlyList<JsonNode>)Array.Empty<JsonNode>())
                    _summaries.Add(new LocalMatchSummary(JsonRead.String(s, "id"), (MatchKind)(JsonRead.Int(s, "k") ?? 0), (PlayerOutcome)(JsonRead.Int(s, "o") ?? 0),
                        (MatchEnding)(JsonRead.Int(s, "e") ?? 0), DateTimeOffset.FromUnixTimeMilliseconds(JsonRead.Long(s, "t") ?? 0)));
            }
            catch (FormatException)
            {
                // A corrupt save starts a fresh guest profile rather than crashing; the old text is overwritten on the next save.
            }
            catch (ArgumentException)
            {
            }
        }
    }
}
