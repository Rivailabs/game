using System;
using System.Collections.Generic;
using System.Globalization;
using AstraKingdoms.World.Challenges;

namespace AstraKingdoms.World.Season
{
    /// <summary>One 18-day world season (aligned with ranked-season operations).</summary>
    public sealed class WorldSeason
    {
        public int Index { get; }
        public string SeasonId { get; }
        public long StartMs { get; }
        public long EndMs { get; }
        public string RulesId => WorldRules.RulesId;

        public WorldSeason(int index, long startMs)
        {
            if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
            Index = index;
            StartMs = startMs;
            EndMs = startMs + WorldRules.SeasonLengthMs;
            SeasonId = "world-s" + index.ToString(CultureInfo.InvariantCulture);
        }

        public bool Contains(long nowMs) => nowMs >= StartMs && nowMs < EndMs;

        public WorldSeason Next() => new WorldSeason(Index + 1, EndMs);
    }

    /// <summary>
    /// An authoritative seasonal world partition with a declared capacity, region and rules version
    /// (plan: "A shard is an authoritative seasonal world partition with a declared capacity, region
    /// and rules version"). The capacity is a measured admission limit, not a promise of simultaneous play.
    /// </summary>
    public sealed class ShardInfo
    {
        public string ShardId { get; }
        public string Region { get; }
        public int Capacity { get; }
        public string RulesVersion { get; }

        public ShardInfo(string shardId, string region, int capacity, string rulesVersion = WorldRules.RulesId)
        {
            if (string.IsNullOrEmpty(shardId)) throw new ArgumentException("Shard ID required.", nameof(shardId));
            if (string.IsNullOrEmpty(region)) throw new ArgumentException("Region required.", nameof(region));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            ShardId = shardId;
            Region = region;
            Capacity = capacity;
            RulesVersion = rulesVersion;
        }

        /// <summary>Friends may share a shard only when region and rules version match.</summary>
        public bool CompatibleWith(ShardInfo other) => other != null && Region == other.Region && RulesVersion == other.RulesVersion;
    }

    /// <summary>Proof that every shard settled a season (no outstanding reservations), required for migration.</summary>
    public sealed class SeasonBoundary
    {
        public string SeasonId { get; }
        public IReadOnlyList<string> ReconciledShards { get; }

        internal SeasonBoundary(string seasonId, IReadOnlyList<string> shards)
        {
            SeasonId = seasonId;
            ReconciledShards = shards;
        }
    }

    /// <summary>Outcome of one queued migration at a season boundary.</summary>
    public sealed class MigrationResult
    {
        public string AccountId { get; internal set; }
        public string FromShard { get; internal set; }
        public string ToShard { get; internal set; }
        public bool Applied { get; internal set; }
        public string Reason { get; internal set; }
    }

    /// <summary>
    /// Shard admission (thread-safe). Each account has exactly one active shard. Admission never
    /// exceeds a shard's declared capacity. Migration requests are only queued; they apply at a
    /// reconciled season boundary, in request order, while capacity allows.
    /// </summary>
    public sealed class ShardDirectory
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, ShardInfo> _shards = new Dictionary<string, ShardInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _active = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _count = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, string>> _migrations = new List<KeyValuePair<string, string>>();
        private readonly HashSet<string> _appliedBoundaries = new HashSet<string>(StringComparer.Ordinal);

        public void AddShard(ShardInfo shard)
        {
            if (shard == null) throw new ArgumentNullException(nameof(shard));
            lock (_gate)
            {
                if (_shards.ContainsKey(shard.ShardId)) throw new ArgumentException("Duplicate shard " + shard.ShardId);
                _shards[shard.ShardId] = shard;
                _count[shard.ShardId] = 0;
            }
        }

        public ShardInfo Shard(string shardId)
        {
            lock (_gate) return _shards.TryGetValue(shardId, out ShardInfo s) ? s : null;
        }

        public string ActiveShardOf(string accountId)
        {
            lock (_gate) return _active.TryGetValue(accountId, out string s) ? s : null;
        }

        public int Population(string shardId)
        {
            lock (_gate) return _count.TryGetValue(shardId, out int n) ? n : 0;
        }

        public IReadOnlyList<string> Members(string shardId)
        {
            lock (_gate)
            {
                var list = new List<string>();
                foreach (KeyValuePair<string, string> kv in _active)
                    if (kv.Value == shardId) list.Add(kv.Key);
                list.Sort(StringComparer.Ordinal);
                return list;
            }
        }

        /// <summary>Admits an account without an active shard. Returns a rejection code or null.</summary>
        public string Assign(string accountId, string shardId)
        {
            lock (_gate) return AssignLocked(accountId, shardId);
        }

        /// <summary>
        /// A friend placement request: admits the account to the friend's shard when it is in the
        /// requested region and below capacity; otherwise rejects (the caller may choose another
        /// shard). Admission never exceeds measured capacity.
        /// </summary>
        public string AssignNearFriend(string accountId, string friendId, string preferredRegion)
        {
            lock (_gate)
            {
                if (!_active.TryGetValue(friendId, out string friendShard)) return "FRIEND_NOT_PLACED";
                if (_shards[friendShard].Region != preferredRegion) return "FRIEND_SHARD_INCOMPATIBLE";
                return AssignLocked(accountId, friendShard);
            }
        }

        private string AssignLocked(string accountId, string shardId)
        {
            if (!_shards.ContainsKey(shardId)) return "UNKNOWN_SHARD";
            if (_active.ContainsKey(accountId)) return "ALREADY_ACTIVE";
            if (_count[shardId] >= _shards[shardId].Capacity) return "SHARD_FULL";
            _active[accountId] = shardId;
            _count[shardId]++;
            return null;
        }

        /// <summary>Queues a migration for the next reconciled season boundary. Nothing moves now.</summary>
        public string RequestMigration(string accountId, string targetShard)
        {
            lock (_gate)
            {
                if (!_active.TryGetValue(accountId, out string current)) return "NOT_ACTIVE";
                if (!_shards.TryGetValue(targetShard, out ShardInfo target)) return "UNKNOWN_SHARD";
                if (current == targetShard) return "SAME_SHARD";
                if (target.RulesVersion != _shards[current].RulesVersion) return "RULES_MISMATCH";
                _migrations.RemoveAll(m => m.Key == accountId);
                _migrations.Add(new KeyValuePair<string, string>(accountId, targetShard));
                return null;
            }
        }

        public int PendingMigrations
        {
            get { lock (_gate) return _migrations.Count; }
        }

        /// <summary>Builds the boundary token from shard settlements; null unless every shard is reconciled.</summary>
        public SeasonBoundary BoundaryFrom(string seasonId, IReadOnlyList<SeasonSettlement> settlements)
        {
            lock (_gate)
            {
                var reconciled = new List<string>();
                foreach (SeasonSettlement s in settlements)
                {
                    if (s.SeasonId != seasonId || !s.Reconciled) return null;
                    reconciled.Add(s.ShardId);
                }
                foreach (string shard in _shards.Keys)
                    if (!reconciled.Contains(shard)) return null;
                return new SeasonBoundary(seasonId, reconciled);
            }
        }

        /// <summary>Applies queued migrations once per reconciled boundary.</summary>
        public IReadOnlyList<MigrationResult> ApplyMigrations(SeasonBoundary boundary)
        {
            if (boundary == null) throw new ArgumentNullException(nameof(boundary), "Migration happens only at a reconciled season boundary.");
            lock (_gate)
            {
                var results = new List<MigrationResult>();
                if (!_appliedBoundaries.Add(boundary.SeasonId)) return results;
                foreach (KeyValuePair<string, string> m in _migrations)
                {
                    string from = _active[m.Key];
                    var r = new MigrationResult { AccountId = m.Key, FromShard = from, ToShard = m.Value };
                    if (_count[m.Value] >= _shards[m.Value].Capacity)
                    {
                        r.Reason = "SHARD_FULL";
                    }
                    else
                    {
                        _active[m.Key] = m.Value;
                        _count[from]--;
                        _count[m.Value]++;
                        r.Applied = true;
                    }
                    results.Add(r);
                }
                _migrations.Clear();
                return results;
            }
        }
    }
}
