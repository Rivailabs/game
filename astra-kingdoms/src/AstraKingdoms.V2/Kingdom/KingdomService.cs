using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Kingdom
{
    /// <summary>Who may visit a homeland (read-only). Default: friends only.</summary>
    public enum VisitAudience : byte
    {
        /// <summary>The owner turned visits off.</summary>
        Nobody = 0,
        FriendsOnly = 1,
        FriendsAndClanmates = 2,
    }

    /// <summary>Per-account kingdom persistence (server: one row per player with a revision column for compare-and-swap).</summary>
    public interface IKingdomStore
    {
        /// <summary>The stored layout JSON, or null when the player never saved one.</summary>
        string LoadLayout(string playerId);
        /// <summary>The authoritative revision column (0 when nothing was saved). Independent of the JSON, so a corrupt blob still has a revision.</summary>
        long StoredRevision(string playerId);
        /// <summary>Stores <paramref name="json"/> only if the stored revision still equals <paramref name="expectedRevision"/>.</summary>
        bool TrySaveLayout(string playerId, long expectedRevision, long newRevision, string json);
        VisitAudience GetVisitAudience(string playerId);
        void SetVisitAudience(string playerId, VisitAudience audience);
        int DeletePlayer(string playerId);
    }

    public sealed class InMemoryKingdomStore : IKingdomStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, KeyValuePair<long, string>> _layouts = new Dictionary<string, KeyValuePair<long, string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, VisitAudience> _visits = new Dictionary<string, VisitAudience>(StringComparer.Ordinal);

        public string LoadLayout(string playerId)
        {
            lock (_gate) return _layouts.TryGetValue(playerId, out var v) ? v.Value : null;
        }

        public long StoredRevision(string playerId)
        {
            lock (_gate) return _layouts.TryGetValue(playerId, out var v) ? v.Key : 0;
        }

        public bool TrySaveLayout(string playerId, long expectedRevision, long newRevision, string json)
        {
            lock (_gate)
            {
                long current = _layouts.TryGetValue(playerId, out var v) ? v.Key : 0;
                if (current != expectedRevision) return false;
                _layouts[playerId] = new KeyValuePair<long, string>(newRevision, json);
                return true;
            }
        }

        /// <summary>Writes raw bytes as if a corrupt or old file had been restored (tests and migration drills).</summary>
        public void Overwrite(string playerId, long revision, string json)
        {
            lock (_gate) _layouts[playerId] = new KeyValuePair<long, string>(revision, json);
        }

        public VisitAudience GetVisitAudience(string playerId)
        {
            lock (_gate) return _visits.TryGetValue(playerId, out VisitAudience a) ? a : VisitAudience.FriendsOnly;
        }

        public void SetVisitAudience(string playerId, VisitAudience audience)
        {
            lock (_gate) _visits[playerId] = audience;
        }

        public int DeletePlayer(string playerId)
        {
            lock (_gate)
            {
                int n = _layouts.Remove(playerId) ? 1 : 0;
                n += _visits.Remove(playerId) ? 1 : 0;
                return n;
            }
        }
    }

    /// <summary>How many ranked seasons a player completed placement in (milestone input). Implemented by the ranked store.</summary>
    public interface ISeasonHistory
    {
        int SeasonsPlaced(string playerId);
    }

    public sealed class NoSeasonHistory : ISeasonHistory
    {
        public static readonly NoSeasonHistory Instance = new NoSeasonHistory();
        public int SeasonsPlaced(string playerId) => 0;
    }

    public enum DecorationBuyStatus : byte
    {
        Purchased = 0,
        AlreadyOwned = 1,
        InsufficientCoins = 2,
        /// <summary>Not a coin-shop item (default, grant-only, unknown).</summary>
        NotForSale = 3,
        /// <summary>The item's asset was retired; it can no longer be bought (owners keep it).</summary>
        Retired = 4,
    }

    public enum LayoutSaveStatus : byte
    {
        Saved = 0,
        /// <summary>The layout changed elsewhere (another device); reload and retry.</summary>
        Conflict = 1,
        Invalid = 2,
        /// <summary>The submitted layout equals the stored one; nothing was written.</summary>
        Unchanged = 3,
    }

    public sealed class LayoutSaveResult
    {
        public LayoutSaveStatus Status { get; }
        public long Revision { get; }
        public IReadOnlyList<string> Problems { get; }

        public LayoutSaveResult(LayoutSaveStatus status, long revision, IReadOnlyList<string> problems = null)
        {
            Status = status;
            Revision = revision;
            Problems = problems ?? Array.Empty<string>();
        }
    }

    /// <summary>A milestone and whether it has been reached (from grant records, so it is permanent).</summary>
    public sealed class MilestoneStatus
    {
        public PlayMilestone Milestone { get; }
        public int Current { get; }
        public bool Reached { get; }

        public MilestoneStatus(PlayMilestone milestone, int current, bool reached)
        {
            Milestone = milestone;
            Current = current;
            Reached = reached;
        }
    }

    /// <summary>Everything the kingdom screen shows.</summary>
    public sealed class KingdomView
    {
        public IReadOnlyList<PlotView> Plots { get; }
        public HomelandLayout Layout { get; }
        public LayoutLoadStatus LoadStatus { get; }
        public long Coins { get; }
        public IReadOnlyCollection<string> OwnedDecorations { get; }
        public IReadOnlyList<MilestoneStatus> Milestones { get; }
        public VisitAudience Visits { get; }

        public KingdomView(IReadOnlyList<PlotView> plots, HomelandLayout layout, LayoutLoadStatus loadStatus, long coins,
            IReadOnlyCollection<string> owned, IReadOnlyList<MilestoneStatus> milestones, VisitAudience visits)
        {
            Plots = plots;
            Layout = layout;
            LoadStatus = loadStatus;
            Coins = coins;
            OwnedDecorations = owned;
            Milestones = milestones;
            Visits = visits;
        }

        public int OpenPlotCount => Plots.Count(p => p.Open);
    }

    /// <summary>
    /// The persistent, protected homeland (plan: "Persistent kingdom and protected homeland").
    /// <list type="bullet">
    /// <item>Decorations are bought with Meta's earned cosmetic coins: one Meta reward-ledger
    /// <see cref="LedgerSource.CoinPurchase"/> entry per (player, decoration), so a retry never charges twice.</item>
    /// <item>Milestones are non-spendable achievements stored as V2 grant records (unique keys).</item>
    /// <item>There are no timers, repairs, accelerators, upkeep or decay: no method takes a duration and
    /// an absent player loses nothing.</item>
    /// <item>No method lets one account change another account's homeland; all plots are protected.</item>
    /// </list>
    /// </summary>
    public sealed class KingdomService
    {
        private readonly DecorationCatalog _catalog;
        private readonly HomelandRules _rules;
        private readonly MilestoneTable _milestones;
        private readonly IRewardLedgerStore _coins;
        private readonly IGrantLedgerStore _grants;
        private readonly IKingdomStore _store;
        private readonly ISeasonHistory _seasons;
        private readonly IClock _clock;

        public KingdomService(DecorationCatalog catalog, HomelandRules rules, MilestoneTable milestones, IRewardLedgerStore coinLedger,
            IGrantLedgerStore grants, IKingdomStore store, IClock clock, ISeasonHistory seasons = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _milestones = milestones ?? throw new ArgumentNullException(nameof(milestones));
            _coins = coinLedger ?? throw new ArgumentNullException(nameof(coinLedger));
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _seasons = seasons ?? NoSeasonHistory.Instance;
        }

        public DecorationCatalog Catalog => _catalog;
        public HomelandRules Rules => _rules;

        public static string PurchaseKey(string playerId, string decorationId) => "v2-decor:" + playerId + ":" + decorationId;

        // ------------------------------------------------------------------ ownership and purchases

        /// <summary>Defaults, decorations bought with earned coins, and decorations from grant records.</summary>
        public IReadOnlyCollection<string> OwnedDecorations(string playerId)
        {
            var owned = new HashSet<string>(StringComparer.Ordinal);
            foreach (DecorationItem i in _catalog.Items.Where(i => i.Source == DecorationSource.Default)) owned.Add(i.Id);
            IReadOnlyList<RewardLedgerEntry> entries = _coins.Entries(playerId);
            var reversed = new HashSet<string>(entries.Where(e => e.Source == LedgerSource.Correction && e.CorrectsKey != null).Select(e => e.CorrectsKey), StringComparer.Ordinal);
            foreach (RewardLedgerEntry e in entries)
                if (e.Source == LedgerSource.CoinPurchase && e.CosmeticId != null && _catalog.Contains(e.CosmeticId) && !reversed.Contains(e.IdempotencyKey))
                    owned.Add(e.CosmeticId);
            foreach (string id in Grants.Items(_grants, playerId))
                if (_catalog.Contains(id)) owned.Add(id);
            return owned;
        }

        public DecorationBuyStatus Buy(string playerId, string decorationId)
        {
            DecorationItem item = _catalog.Get(decorationId);
            if (item == null || item.Source != DecorationSource.CoinShop) return DecorationBuyStatus.NotForSale;
            if (_coins.Find(PurchaseKey(playerId, decorationId)) != null) return DecorationBuyStatus.AlreadyOwned;
            if (item.IsRetired) return DecorationBuyStatus.Retired;
            var entry = new RewardLedgerEntry(PurchaseKey(playerId, decorationId), playerId, LedgerSource.CoinPurchase, 0, -item.CoinPrice, _clock.UtcNow,
                reference: decorationId, cosmeticId: decorationId);
            LedgerAppendResult r = _coins.TryAppend(entry, totals => totals.Coins < item.CoinPrice ? "insufficient coins" : null);
            switch (r.Status)
            {
                case AppendStatus.Appended: return DecorationBuyStatus.Purchased;
                case AppendStatus.Duplicate: return DecorationBuyStatus.AlreadyOwned;
                default: return DecorationBuyStatus.InsufficientCoins;
            }
        }

        // ------------------------------------------------------------------ milestones and plots

        public PlayStats Stats(string playerId) => PlayStats.FromLedger(_coins, playerId, _seasons.SeasonsPlaced(playerId));

        /// <summary>
        /// Records every newly reached milestone as a grant record and returns the ones that are new
        /// in this call. Safe to call on every match result, login, retry or restore.
        /// </summary>
        public IReadOnlyList<PlayMilestone> EvaluateMilestones(string playerId)
        {
            PlayStats stats = Stats(playerId);
            var fresh = new List<PlayMilestone>();
            foreach (PlayMilestone m in _milestones.Milestones)
            {
                if (stats.Value(m.Stat) < m.Threshold) continue;
                var record = new GrantRecord(GrantKeys.Milestone(playerId, m.Id), playerId, GrantSource.KingdomMilestone, null, m.Id, _clock.UtcNow);
                if (_grants.TryAppend(record).Appended) fresh.Add(m);
            }
            return fresh;
        }

        public IReadOnlyList<MilestoneStatus> MilestoneStatuses(string playerId)
        {
            PlayStats stats = Stats(playerId);
            return _milestones.Milestones
                .Select(m => new MilestoneStatus(m, stats.Value(m.Stat), _grants.Find(GrantKeys.Milestone(playerId, m.Id)) != null))
                .ToArray();
        }

        /// <summary>Open plots: the initial ones plus those whose milestone grant exists. Never shrinks.</summary>
        public IReadOnlyCollection<int> OpenPlots(string playerId)
        {
            var open = new HashSet<int>(Enumerable.Range(0, _rules.InitialPlots));
            foreach (PlayMilestone m in _milestones.Milestones)
                if (m.OpensPlot >= 0 && _grants.Find(GrantKeys.Milestone(playerId, m.Id)) != null) open.Add(m.OpensPlot);
            return open;
        }

        // ------------------------------------------------------------------ layout

        /// <summary>Reads the stored layout (never throws); its revision is the store's authoritative revision column.</summary>
        public LayoutLoadResult LoadLayout(string playerId)
        {
            LayoutLoadResult r = LayoutSerializer.Read(_store.LoadLayout(playerId), _rules, _catalog);
            long revision = _store.StoredRevision(playerId);
            return new LayoutLoadResult(new HomelandLayout(revision, r.Layout.CatalogVersion, r.Layout.Placements), r.Status, r.Notes);
        }

        public KingdomView View(string playerId)
        {
            LayoutLoadResult load = LoadLayout(playerId);
            IReadOnlyCollection<int> open = OpenPlots(playerId);
            return new KingdomView(BuildPlots(load.Layout, open), load.Layout, load.Status, _coins.Totals(playerId).Coins,
                OwnedDecorations(playerId), MilestoneStatuses(playerId), _store.GetVisitAudience(playerId));
        }

        internal IReadOnlyList<PlotView> BuildPlots(HomelandLayout layout, IReadOnlyCollection<int> open)
        {
            var plots = new List<PlotView>(_rules.PlotCount);
            for (int i = 0; i < _rules.PlotCount; i++)
            {
                bool isOpen = open.Contains(i);
                Placement main = isOpen ? layout.At(i, PlotSlot.Main) : null;
                Placement accent = isOpen ? layout.At(i, PlotSlot.Accent) : null;
                DecorationItem mainItem = _catalog.ResolveDisplayed(main?.DecorationId, PlotSlot.Main);
                DecorationItem accentItem = _catalog.ResolveDisplayed(accent?.DecorationId, PlotSlot.Accent);
                bool fallback = (main != null && mainItem.Id != main.DecorationId) || (accent != null && accentItem.Id != accent.DecorationId);
                PlayMilestone opener = isOpen ? null : _milestones.Milestones.FirstOrDefault(m => m.OpensPlot == i);
                plots.Add(new PlotView(i, isOpen, opener, mainItem, accentItem, main?.Rotation ?? 0, accent?.Rotation ?? 0, fallback));
            }
            return plots;
        }

        /// <summary>
        /// Replaces the whole layout. Every placement must be in an open plot, in the right slot, owned,
        /// and (for newly placed items) not retired; a retired item that was already placed may stay and
        /// keeps displaying its replacement. <paramref name="expectedRevision"/> must equal the stored
        /// revision (optimistic concurrency between devices).
        /// </summary>
        public LayoutSaveResult SaveLayout(string playerId, long expectedRevision, IEnumerable<Placement> placements)
        {
            LayoutLoadResult current = LoadLayout(playerId);
            List<Placement> list = (placements ?? Array.Empty<Placement>()).ToList();
            var problems = new List<string>();
            IReadOnlyCollection<int> open = OpenPlots(playerId);
            IReadOnlyCollection<string> owned = OwnedDecorations(playerId);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Placement p in list)
            {
                string structural = LayoutSerializer.StructuralProblem(p, _rules, _catalog);
                if (structural != null) { problems.Add(p + ": " + structural); continue; }
                if (!seen.Add(p.Plot + "/" + p.Slot)) problems.Add(p + ": two placements in one slot");
                if (!open.Contains(p.Plot)) problems.Add(p + ": plot is not open yet");
                DecorationItem item = _catalog.Get(p.DecorationId);
                if (item == null) { problems.Add(p + ": unknown decoration"); continue; }
                if (!owned.Contains(item.Id)) problems.Add(p + ": not owned");
                bool alreadyThere = current.Layout.At(p.Plot, p.Slot)?.DecorationId == item.Id;
                if (item.IsRetired && !alreadyThere) problems.Add(p + ": retired items cannot be newly placed");
            }
            if (problems.Count > 0) return new LayoutSaveResult(LayoutSaveStatus.Invalid, current.Layout.Revision, problems);
            if (expectedRevision != current.Layout.Revision) return new LayoutSaveResult(LayoutSaveStatus.Conflict, current.Layout.Revision);
            var next = new HomelandLayout(current.Layout.Revision + 1, _catalog.Version, list);
            if (current.Status == LayoutLoadStatus.Ok && next.Placements.SequenceEqual(current.Layout.Placements) && current.Layout.CatalogVersion == _catalog.Version)
                return new LayoutSaveResult(LayoutSaveStatus.Unchanged, current.Layout.Revision);
            return _store.TrySaveLayout(playerId, expectedRevision, next.Revision, LayoutSerializer.Write(next))
                ? new LayoutSaveResult(LayoutSaveStatus.Saved, next.Revision)
                : new LayoutSaveResult(LayoutSaveStatus.Conflict, LoadLayout(playerId).Layout.Revision);
        }

        /// <summary>Places (or replaces) one decoration.</summary>
        public LayoutSaveResult Place(string playerId, long expectedRevision, Placement placement)
        {
            HomelandLayout layout = LoadLayout(playerId).Layout;
            var list = layout.Placements.Where(p => !(p.Plot == placement.Plot && p.Slot == placement.Slot)).ToList();
            list.Add(placement);
            return SaveLayout(playerId, expectedRevision, list);
        }

        /// <summary>Empties one slot (it shows the slot's safe default).</summary>
        public LayoutSaveResult Clear(string playerId, long expectedRevision, int plot, PlotSlot slot)
        {
            HomelandLayout layout = LoadLayout(playerId).Layout;
            return SaveLayout(playerId, expectedRevision, layout.Placements.Where(p => !(p.Plot == plot && p.Slot == slot)));
        }

        /// <summary>
        /// Rewrites a migrated, repaired or recovered layout in the current schema so the next load is
        /// clean. Returns false when nothing needed rewriting or another device saved first.
        /// </summary>
        public bool PersistRecovery(string playerId)
        {
            LayoutLoadResult load = LoadLayout(playerId);
            if (load.Status == LayoutLoadStatus.Ok) return false;
            var fixedLayout = new HomelandLayout(load.Layout.Revision + 1, _catalog.Version, load.Layout.Placements);
            return _store.TrySaveLayout(playerId, load.Layout.Revision, fixedLayout.Revision, LayoutSerializer.Write(fixedLayout));
        }

        // ------------------------------------------------------------------ visits

        public VisitAudience GetVisitAudience(string playerId) => _store.GetVisitAudience(playerId);

        /// <summary>The owner chooses who may visit; <see cref="VisitAudience.Nobody"/> disables visits.</summary>
        public void SetVisitAudience(string playerId, VisitAudience audience) => _store.SetVisitAudience(playerId, audience);

        /// <summary>Account deletion: layout and visit settings. Coin purchases and grants are deleted with their own ledgers.</summary>
        public int DeletePlayer(string playerId) => _store.DeletePlayer(playerId);
    }
}
