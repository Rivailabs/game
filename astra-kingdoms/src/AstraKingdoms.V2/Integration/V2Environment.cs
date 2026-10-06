using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Shop;
using AstraKingdoms.Rules.Balance;
using AstraKingdoms.V2.Common;
using AstraKingdoms.V2.Kingdom;
using AstraKingdoms.V2.Ranked;
using AstraKingdoms.V2.Seasons;

namespace AstraKingdoms.V2.Integration
{
    /// <summary>
    /// Composition root for the V2 progression services over in-memory stores. It is the reference
    /// wiring for the online server (replace each InMemory* store with its database implementation)
    /// and the environment the season rehearsal harness and the tests run in. Meta's reward ledger,
    /// entitlement ledger and PurchaseService are shared, never duplicated.
    /// </summary>
    public sealed class V2Environment
    {
        public IClock Clock { get; }
        public AuditLog Audit { get; } = new AuditLog();

        // Meta (shared, reused)
        public IRewardLedgerStore RewardLedger { get; }
        public IEntitlementLedgerStore Entitlements { get; }
        public ProgressionService Progression { get; }

        // V2
        public InMemoryGrantLedgerStore Grants { get; } = new InMemoryGrantLedgerStore();
        public SeasonCalendar Calendar { get; }
        public SnapshotRegistry Snapshots { get; }
        public InMemoryRankedStore RankedStore { get; }
        public RankedService Ranked { get; }
        public InMemorySettlementStore SettlementStore { get; } = new InMemorySettlementStore();
        public InMemoryPassProgressStore PassProgress { get; } = new InMemoryPassProgressStore();
        public SeasonPassService Pass { get; }
        public SeasonService Seasons { get; }
        public InMemoryKingdomStore KingdomStore { get; } = new InMemoryKingdomStore();
        public KingdomService Kingdom { get; }
        public BalanceBundle Balance { get; }
        private readonly Dictionary<string, RankedMatchmaker> _matchmakers = new Dictionary<string, RankedMatchmaker>(StringComparer.Ordinal);
        private readonly TimeSpan? _measuredMaxMatch;
        private readonly DateTimeOffset _firstStart;

        public V2Environment(IClock clock, DateTimeOffset firstSeasonStart, TimeSpan? measuredMaxMatchDuration, IRewardLedgerStore rewardLedger = null,
            IEntitlementLedgerStore entitlements = null, SeasonRules seasonRules = null, RankedRules rankedRules = null, BalanceBundle balance = null)
        {
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _firstStart = firstSeasonStart;
            _measuredMaxMatch = measuredMaxMatchDuration;
            RewardLedger = rewardLedger ?? new InMemoryRewardLedgerStore();
            Entitlements = entitlements ?? new InMemoryEntitlementLedgerStore();
            Progression = new ProgressionService(RewardLedger, clock);
            Balance = balance ?? BalanceBundle.Baseline();
            Calendar = new SeasonCalendar(seasonRules ?? SeasonRules.Default);
            Snapshots = new SnapshotRegistry(Audit);
            RankedStore = new InMemoryRankedStore(rankedRules);
            Ranked = new RankedService(RankedStore, rankedRules ?? RankedRules.Default, Calendar, Snapshots, SettlementStore.IsSettled, Audit);
            Pass = new SeasonPassService(Calendar, null, PassProgress, Grants, Entitlements, clock, SettlementStore.IsSettled);
            Seasons = new SeasonService(Calendar, SettlementStore, Ranked, Pass, Grants, clock, Audit);
            Kingdom = new KingdomService(DecorationCatalog.Default, HomelandRules.Default, MilestoneTable.Default, RewardLedger, Grants, KingdomStore, clock, RankedStore);
        }

        /// <summary>Publishes the next season: dates, the frozen balance snapshot and its pass.</summary>
        public SeasonDefinition PublishNextSeason()
        {
            SeasonDefinition s = Calendar.PublishNext(_firstStart, _measuredMaxMatch);
            Snapshots.Freeze(new SeasonBalanceSnapshot(SnapshotRegistry.SnapshotIdFor(s.Id, 1), s.Id, Balance, Ranked.Rules, Clock.UtcNow));
            Pass.AddPass(SeasonPassDefinition.Default(s));
            _matchmakers[s.Id] = new RankedMatchmaker(s, Ranked, Snapshots, Clock);
            return s;
        }

        public RankedMatchmaker Matchmaker(string seasonId) => _matchmakers.TryGetValue(seasonId, out var m) ? m : null;

        /// <summary>Meta's store catalogue extended with every published pass SKU.</summary>
        public StoreCatalog StoreWithPasses() => SeasonPassStore.Extend(StoreCatalog.Default, Pass.Passes.OrderBy(p => p.SeasonNumber));

        /// <summary>
        /// The match-service hook for one human seat of a finished match: Meta progression (XP/coins),
        /// pass points, then homeland milestones. All three are idempotent per match result.
        /// </summary>
        public void OnMatchReport(MatchOutcomeReport report)
        {
            Progression.GrantForMatch(report);
            Pass.RecordMatch(report);
            Kingdom.EvaluateMilestones(report.PlayerId);
        }
    }
}
