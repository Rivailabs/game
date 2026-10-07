using System;
using System.Collections.Generic;
using System.Linq;

namespace AstraKingdoms.V2.Common
{
    /// <summary>Why a V2 grant record exists.</summary>
    public enum GrantSource : byte
    {
        /// <summary>A published play milestone was reached (a non-spendable achievement; may open a homeland plot).</summary>
        KingdomMilestone = 1,
        /// <summary>A free-track season pass tier.</summary>
        PassFree = 2,
        /// <summary>A paid-track season pass tier (requires the season's pass entitlement).</summary>
        PassPaid = 3,
        /// <summary>The end-of-season league trophy.</summary>
        SeasonLeagueReward = 4,
        /// <summary>A cooperative clan cosmetic milestone.</summary>
        ClanMilestone = 5,
        /// <summary>A documented support correction (a reference is required).</summary>
        SupportCorrection = 6,
    }

    /// <summary>
    /// One immutable V2 entitlement record. V2 deliberately has no currency of its own: decorations
    /// are bought with Meta's earned cosmetic coins (Meta's reward ledger), the pass is a paid SKU in
    /// Meta's entitlement ledger, and everything else that V2 hands out (achievements, pass tiers,
    /// trophies, clan rewards) is one of these records. Each record has a globally unique key built by
    /// <see cref="GrantKeys"/>, so a retried request, a reinstall that replays the client queue, or a
    /// restored backup can never deliver the same grant twice.
    /// </summary>
    public sealed class GrantRecord
    {
        /// <summary>Assigned by the store on append (1, 2, 3...). Zero before that.</summary>
        public long Sequence { get; }
        public string Key { get; }
        public string PlayerId { get; }
        public GrantSource Source { get; }
        /// <summary>The cosmetic or decoration granted; null for a pure achievement.</summary>
        public string ItemId { get; }
        /// <summary>Non-personal reference: milestone id, season id + tier, clan milestone id.</summary>
        public string Reference { get; }
        public DateTimeOffset At { get; }

        public GrantRecord(string key, string playerId, GrantSource source, string itemId, string reference, DateTimeOffset at, long sequence = 0)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A grant key is required.", nameof(key));
            if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("A player id is required.", nameof(playerId));
            Key = key;
            PlayerId = playerId;
            Source = source;
            ItemId = itemId;
            Reference = reference;
            At = at;
            Sequence = sequence;
        }

        internal GrantRecord WithSequence(long sequence) => new GrantRecord(Key, PlayerId, Source, ItemId, Reference, At, sequence);

        /// <summary>True when both records describe the same grant (ignores sequence and time).</summary>
        public bool SamePayload(GrantRecord other) =>
            other != null && Key == other.Key && PlayerId == other.PlayerId && Source == other.Source &&
            ItemId == other.ItemId && Reference == other.Reference;

        public override string ToString() => "#" + Sequence + " " + Key + (ItemId != null ? " -> " + ItemId : string.Empty);
    }

    /// <summary>Outcome of one append.</summary>
    public sealed class GrantAppendResult
    {
        public bool Appended { get; }
        /// <summary>The stored record (the original one when this was a duplicate).</summary>
        public GrantRecord Record { get; }
        /// <summary>A duplicate key arrived with a different payload (a client bug or tampering; alert).</summary>
        public bool PayloadMismatch { get; }

        public GrantAppendResult(bool appended, GrantRecord record, bool payloadMismatch)
        {
            Appended = appended;
            Record = record;
            PayloadMismatch = payloadMismatch;
        }
    }

    /// <summary>
    /// Append-only, idempotent store of V2 grant records.
    /// <para>Server integration: one table with <c>UNIQUE(key)</c>; insert inside the caller's
    /// transaction; a unique violation is reported as a duplicate (never an error); rows are never
    /// updated. Account deletion removes the player's rows (<see cref="DeletePlayer"/>).</para>
    /// </summary>
    public interface IGrantLedgerStore
    {
        GrantAppendResult TryAppend(GrantRecord record);
        GrantRecord Find(string key);
        IReadOnlyList<GrantRecord> ForPlayer(string playerId);
        int DeletePlayer(string playerId);
    }

    /// <summary>Thread-safe in-memory grant ledger (tests, the offline device profile, single-process hosting).</summary>
    public sealed class InMemoryGrantLedgerStore : IGrantLedgerStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, GrantRecord> _byKey = new Dictionary<string, GrantRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<GrantRecord>> _byPlayer = new Dictionary<string, List<GrantRecord>>(StringComparer.Ordinal);
        private long _sequence;

        public GrantAppendResult TryAppend(GrantRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            lock (_gate)
            {
                if (_byKey.TryGetValue(record.Key, out GrantRecord existing))
                    return new GrantAppendResult(false, existing, !existing.SamePayload(record));
                GrantRecord stored = record.WithSequence(++_sequence);
                _byKey.Add(stored.Key, stored);
                if (!_byPlayer.TryGetValue(stored.PlayerId, out List<GrantRecord> list)) _byPlayer.Add(stored.PlayerId, list = new List<GrantRecord>());
                list.Add(stored);
                return new GrantAppendResult(true, stored, false);
            }
        }

        public GrantRecord Find(string key)
        {
            lock (_gate) return key != null && _byKey.TryGetValue(key, out GrantRecord r) ? r : null;
        }

        public IReadOnlyList<GrantRecord> ForPlayer(string playerId)
        {
            lock (_gate) return playerId != null && _byPlayer.TryGetValue(playerId, out List<GrantRecord> l) ? l.ToArray() : Array.Empty<GrantRecord>();
        }

        public int DeletePlayer(string playerId)
        {
            lock (_gate)
            {
                if (!_byPlayer.TryGetValue(playerId, out List<GrantRecord> list)) return 0;
                foreach (GrantRecord r in list) _byKey.Remove(r.Key);
                _byPlayer.Remove(playerId);
                return list.Count;
            }
        }

        /// <summary>Every record in sequence order (backup export).</summary>
        public IReadOnlyList<GrantRecord> Export()
        {
            lock (_gate) return _byKey.Values.OrderBy(r => r.Sequence).ToArray();
        }

        /// <summary>
        /// Restores a backup by re-appending its records. Records already present are skipped, so
        /// restoring the same (or an older) backup twice never duplicates a grant. Returns the
        /// number of records that were actually new.
        /// </summary>
        public int Restore(IEnumerable<GrantRecord> backup)
        {
            int added = 0;
            foreach (GrantRecord r in backup.OrderBy(x => x.Sequence))
                if (TryAppend(r).Appended) added++;
            return added;
        }

        public int Count
        {
            get { lock (_gate) return _byKey.Count; }
        }
    }

    /// <summary>
    /// The only place V2 grant keys are built. Keys contain the season/match/milestone identifiers
    /// that make a grant unique, so the same logical grant always maps to the same key.
    /// </summary>
    public static class GrantKeys
    {
        public static string Milestone(string playerId, string milestoneId) => "v2:milestone:" + milestoneId + ":" + playerId;

        public static string PassTier(string seasonId, string playerId, int tier, bool paid) =>
            "v2:pass:" + seasonId + ":" + (paid ? "paid" : "free") + ":" + tier.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + playerId;

        public static string LeagueReward(string seasonId, string playerId) => "v2:league:" + seasonId + ":" + playerId;

        public static string ClanMilestone(string clanId, string seasonId, string milestoneId, string playerId) =>
            "v2:clan:" + clanId + ":" + seasonId + ":" + milestoneId + ":" + playerId;

        public static string SupportCorrection(string caseId, string playerId) => "v2:support:" + caseId + ":" + playerId;
    }

    /// <summary>Helpers that fold a player's grant records.</summary>
    public static class Grants
    {
        /// <summary>Distinct item ids granted to the player (any source).</summary>
        public static IReadOnlyCollection<string> Items(IGrantLedgerStore store, string playerId) =>
            new HashSet<string>(store.ForPlayer(playerId).Where(r => r.ItemId != null).Select(r => r.ItemId), StringComparer.Ordinal);

        public static bool Has(IGrantLedgerStore store, string key) => store.Find(key) != null;
    }
}
