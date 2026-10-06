using System;
using System.Collections.Generic;

namespace AstraKingdoms.World.Season
{
    /// <summary>Coarse experience band used by fair-opponent matching.</summary>
    public enum ExperienceBand : byte
    {
        Newcomer = 0,
        Established = 1,
        Veteran = 2,
    }

    /// <summary>
    /// An immutable copy of a world account at one moment. The protected homeland, purchases and
    /// earned cosmetics live here, outside every border-loss calculation and every season reset.
    /// </summary>
    public sealed class AccountSnapshot
    {
        public string AccountId { get; internal set; }
        public long CreatedMs { get; internal set; }
        public int Rating { get; internal set; }
        public int WorldEncounters { get; internal set; }
        public int TrainingEncounters { get; internal set; }
        public bool ProtectionEndedEarly { get; internal set; }
        public string AllianceId { get; internal set; }
        public string PreviousAllianceId { get; internal set; }
        public long LeftAllianceMs { get; internal set; }
        /// <summary>The twelve protected homeland plots (decoration ID or null). Never attackable.</summary>
        public IReadOnlyList<string> Homeland { get; internal set; }
        public IReadOnlyCollection<string> Purchases { get; internal set; }
        public IReadOnlyCollection<string> Cosmetics { get; internal set; }
        /// <summary>Set after a season reset for an account that lost border tiles: a safe practice/re-entry route is offered.</summary>
        public bool ReEntryOffered { get; internal set; }

        public long StarterProtectionEndsMs => CreatedMs + WorldRules.StarterProtectionMs;

        /// <summary>Seven days without incoming conquest, unless explicitly ended early.</summary>
        public bool IsStarterProtected(long nowMs) => !ProtectionEndedEarly && nowMs < StarterProtectionEndsMs;

        public ExperienceBand Band =>
            WorldEncounters < WorldRules.NewcomerMaxWorldEncounters ? ExperienceBand.Newcomer
            : WorldEncounters >= WorldRules.VeteranMinWorldEncounters ? ExperienceBand.Veteran
            : ExperienceBand.Established;

        /// <summary>
        /// Alliance IDs this account counts for in the per-alliance cap: its current alliance and, for
        /// 24 hours after leaving, the previous one (so leaving cannot bypass the cap).
        /// </summary>
        public IEnumerable<string> AlliancesForCap(long nowMs)
        {
            if (!string.IsNullOrEmpty(AllianceId)) yield return AllianceId;
            if (!string.IsNullOrEmpty(PreviousAllianceId) && PreviousAllianceId != AllianceId &&
                nowMs - LeftAllianceMs < WorldRules.RepeatTargetWindowMs)
                yield return PreviousAllianceId;
        }
    }

    /// <summary>
    /// The persistent, cross-season account store (thread-safe, one lock). Shards and alliances read
    /// snapshots and mutate only through these methods. There is deliberately no method that moves
    /// homeland plots, purchases, cosmetics or coins from one account to another.
    /// </summary>
    public sealed class AccountRegistry
    {
        private sealed class Account
        {
            public string Id;
            public long CreatedMs;
            public int Rating;
            public int WorldEncounters;
            public int TrainingEncounters;
            public bool ProtectionEndedEarly;
            public string AllianceId;
            public string PreviousAllianceId;
            public long LeftAllianceMs;
            public readonly string[] Homeland = new string[WorldRules.HomelandPlots];
            public readonly HashSet<string> Purchases = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Cosmetics = new HashSet<string>(StringComparer.Ordinal);
            public bool ReEntryOffered;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Account> _accounts = new Dictionary<string, Account>(StringComparer.Ordinal);
        private readonly HashSet<string> _grantKeys = new HashSet<string>(StringComparer.Ordinal);

        public bool Create(string accountId, long nowMs, int rating = 1000)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("Account ID required.", nameof(accountId));
            lock (_gate)
            {
                if (_accounts.ContainsKey(accountId)) return false;
                _accounts[accountId] = new Account { Id = accountId, CreatedMs = nowMs, Rating = rating };
                return true;
            }
        }

        public bool Exists(string accountId)
        {
            lock (_gate) return _accounts.ContainsKey(accountId);
        }

        public AccountSnapshot Get(string accountId)
        {
            lock (_gate)
            {
                if (!_accounts.TryGetValue(accountId, out Account a)) return null;
                return new AccountSnapshot
                {
                    AccountId = a.Id,
                    CreatedMs = a.CreatedMs,
                    Rating = a.Rating,
                    WorldEncounters = a.WorldEncounters,
                    TrainingEncounters = a.TrainingEncounters,
                    ProtectionEndedEarly = a.ProtectionEndedEarly,
                    AllianceId = a.AllianceId,
                    PreviousAllianceId = a.PreviousAllianceId,
                    LeftAllianceMs = a.LeftAllianceMs,
                    Homeland = (string[])a.Homeland.Clone(),
                    Purchases = new List<string>(a.Purchases),
                    Cosmetics = new List<string>(a.Cosmetics),
                    ReEntryOffered = a.ReEntryOffered,
                };
            }
        }

        public IReadOnlyList<string> AllIds
        {
            get
            {
                lock (_gate)
                {
                    var ids = new List<string>(_accounts.Keys);
                    ids.Sort(StringComparer.Ordinal);
                    return ids;
                }
            }
        }

        /// <summary>Server-owned skill rating (from ranked play); the world only reads it.</summary>
        public void SetRating(string accountId, int rating) => With(accountId, a => a.Rating = rating);

        /// <summary>A safe practice encounter (no land at stake). Counts toward ending protection early.</summary>
        public void RecordTrainingEncounter(string accountId) => With(accountId, a => a.TrainingEncounters++);

        internal void RecordWorldEncounter(string accountId) => With(accountId, a => a.WorldEncounters++);

        /// <summary>
        /// The player explicitly ends starter protection early. Allowed only after five training
        /// encounters; returns a rejection code or null on success.
        /// </summary>
        public string EndProtectionEarly(string accountId)
        {
            lock (_gate)
            {
                if (!_accounts.TryGetValue(accountId, out Account a)) return "UNKNOWN_ACCOUNT";
                if (a.TrainingEncounters < WorldRules.TrainingEncountersToEndProtectionEarly) return "NEEDS_TRAINING";
                a.ProtectionEndedEarly = true;
                return null;
            }
        }

        /// <summary>Places a decoration on a homeland plot (0-11). Homeland is decorative and protected.</summary>
        public void Decorate(string accountId, int plot, string decorationId)
        {
            if (plot < 0 || plot >= WorldRules.HomelandPlots) throw new ArgumentOutOfRangeException(nameof(plot));
            With(accountId, a => a.Homeland[plot] = decorationId);
        }

        public void AddPurchase(string accountId, string purchaseId) => With(accountId, a => a.Purchases.Add(purchaseId));

        /// <summary>Grants a cosmetic once per grant key; returns false for a repeated key.</summary>
        public bool GrantCosmetic(string grantKey, string accountId, string cosmeticId)
        {
            lock (_gate)
            {
                if (!_accounts.TryGetValue(accountId, out Account a)) throw new ArgumentException("Unknown account " + accountId);
                if (!_grantKeys.Add(grantKey)) return false;
                a.Cosmetics.Add(cosmeticId);
                return true;
            }
        }

        public bool HasGrant(string grantKey)
        {
            lock (_gate) return _grantKeys.Contains(grantKey);
        }

        internal void SetReEntryOffered(string accountId, bool offered) => With(accountId, a => a.ReEntryOffered = offered);

        internal void SetAlliance(string accountId, string allianceId, long nowMs)
        {
            With(accountId, a =>
            {
                if (allianceId == null && a.AllianceId != null)
                {
                    a.PreviousAllianceId = a.AllianceId;
                    a.LeftAllianceMs = nowMs;
                }
                a.AllianceId = allianceId;
            });
        }

        private void With(string accountId, Action<Account> change)
        {
            lock (_gate)
            {
                if (!_accounts.TryGetValue(accountId, out Account a)) throw new ArgumentException("Unknown account " + accountId);
                change(a);
            }
        }
    }
}
