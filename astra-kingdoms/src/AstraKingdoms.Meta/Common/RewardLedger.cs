using System;
using System.Collections.Generic;
using System.Linq;

namespace AstraKingdoms.Meta.Common
{
    /// <summary>Why a reward-ledger entry exists.</summary>
    public enum LedgerSource : byte
    {
        /// <summary>XP and cosmetic coins for one eligible match result (tickets 57 and 58).</summary>
        Match = 1,
        /// <summary>A claimed daily cosmetic task (ticket 58).</summary>
        DailyTask = 2,
        /// <summary>The one-time capped guest/offline migration grant (ticket 57).</summary>
        GuestMigration = 3,
        /// <summary>A server-verified rewarded-ad completion (ticket 63).</summary>
        RewardedAd = 4,
        /// <summary>Spending earned coins on a cosmetic (ticket 59/60). Negative coin delta.</summary>
        CoinPurchase = 5,
        /// <summary>A corrective entry that reverses an earlier one (never an edit in place).</summary>
        Correction = 6,
    }

    /// <summary>
    /// One immutable line of the earned-progress ledger: XP, earned cosmetic coins and coin-bought
    /// cosmetics. Paid store entitlements live in a different ledger (Billing.EntitlementLedger) so
    /// money never turns into coins or XP and earned items are never confused with purchases.
    /// </summary>
    public sealed class RewardLedgerEntry
    {
        /// <summary>Assigned by the store on append (1, 2, 3...). Zero before that.</summary>
        public long Sequence { get; }
        /// <summary>Globally unique key that makes the grant idempotent (e.g. "match:{resultId}:{player}").</summary>
        public string IdempotencyKey { get; }
        public string PlayerId { get; }
        public LedgerSource Source { get; }
        public int XpDelta { get; }
        public int CoinDelta { get; }
        /// <summary>Cosmetic granted by this entry (coin purchase), or null.</summary>
        public string CosmeticId { get; }
        /// <summary>Weapons the player used in the match (mastery counting), empty otherwise.</summary>
        public IReadOnlyList<int> WeaponsUsed { get; }
        /// <summary>Free-form non-personal reference (match result id, task id + day, ad transaction id).</summary>
        public string Reference { get; }
        /// <summary>For <see cref="LedgerSource.Correction"/>: the key of the entry being reversed.</summary>
        public string CorrectsKey { get; }
        public DateTimeOffset At { get; }

        public RewardLedgerEntry(string idempotencyKey, string playerId, LedgerSource source, int xpDelta, int coinDelta,
            DateTimeOffset at, string reference = null, string cosmeticId = null, IReadOnlyList<int> weaponsUsed = null,
            string correctsKey = null, long sequence = 0)
        {
            if (string.IsNullOrEmpty(idempotencyKey)) throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
            if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("A player id is required.", nameof(playerId));
            IdempotencyKey = idempotencyKey;
            PlayerId = playerId;
            Source = source;
            XpDelta = xpDelta;
            CoinDelta = coinDelta;
            At = at;
            Reference = reference;
            CosmeticId = cosmeticId;
            WeaponsUsed = weaponsUsed ?? Array.Empty<int>();
            CorrectsKey = correctsKey;
            Sequence = sequence;
        }

        internal RewardLedgerEntry WithSequence(long sequence) =>
            new RewardLedgerEntry(IdempotencyKey, PlayerId, Source, XpDelta, CoinDelta, At, Reference, CosmeticId, WeaponsUsed, CorrectsKey, sequence);

        /// <summary>True when two entries describe the same grant (ignoring sequence and time).</summary>
        public bool SamePayload(RewardLedgerEntry other) =>
            other != null && IdempotencyKey == other.IdempotencyKey && PlayerId == other.PlayerId && Source == other.Source &&
            XpDelta == other.XpDelta && CoinDelta == other.CoinDelta && CosmeticId == other.CosmeticId &&
            CorrectsKey == other.CorrectsKey && WeaponsUsed.SequenceEqual(other.WeaponsUsed);

        public override string ToString() => "#" + Sequence + " " + IdempotencyKey + " xp" + XpDelta + " coins" + CoinDelta;
    }

    /// <summary>Totals derived from a player's ledger. Never stored independently of the ledger.</summary>
    public sealed class PlayerTotals
    {
        public string PlayerId { get; }
        public long Xp { get; }
        public long Coins { get; }
        public IReadOnlyCollection<string> CoinCosmetics { get; }
        public IReadOnlyDictionary<int, int> WeaponUses { get; }
        public int EntryCount { get; }

        public PlayerTotals(string playerId, long xp, long coins, IReadOnlyCollection<string> coinCosmetics,
            IReadOnlyDictionary<int, int> weaponUses, int entryCount)
        {
            PlayerId = playerId;
            Xp = xp;
            Coins = coins;
            CoinCosmetics = coinCosmetics;
            WeaponUses = weaponUses;
            EntryCount = entryCount;
        }

        /// <summary>Folds entries (in sequence order) into totals; corrections are ordinary negative entries.</summary>
        public static PlayerTotals Fold(string playerId, IEnumerable<RewardLedgerEntry> entries)
        {
            long xp = 0, coins = 0;
            int count = 0;
            var cosmetics = new HashSet<string>(StringComparer.Ordinal);
            var uses = new Dictionary<int, int>();
            var reversed = new HashSet<string>(StringComparer.Ordinal);
            var list = entries.OrderBy(e => e.Sequence).ToList();
            foreach (RewardLedgerEntry e in list)
                if (e.Source == LedgerSource.Correction && e.CorrectsKey != null) reversed.Add(e.CorrectsKey);
            foreach (RewardLedgerEntry e in list)
            {
                count++;
                xp += e.XpDelta;
                coins += e.CoinDelta;
                if (reversed.Contains(e.IdempotencyKey)) continue; // its XP/coins are offset by the correction
                if (e.CosmeticId != null) cosmetics.Add(e.CosmeticId);
                foreach (int w in e.WeaponsUsed)
                    uses[w] = uses.TryGetValue(w, out int n) ? n + 1 : 1;
            }
            return new PlayerTotals(playerId, Math.Max(0, xp), coins, cosmetics, uses, count);
        }
    }

    public enum AppendStatus : byte
    {
        /// <summary>The entry was new and is now part of the ledger.</summary>
        Appended = 0,
        /// <summary>The key already existed; nothing changed. <see cref="LedgerAppendResult.Entry"/> is the original.</summary>
        Duplicate = 1,
        /// <summary>A precondition (e.g. sufficient coins) failed; nothing changed.</summary>
        Rejected = 2,
    }

    public sealed class LedgerAppendResult
    {
        public AppendStatus Status { get; }
        public RewardLedgerEntry Entry { get; }
        public string Reason { get; }
        /// <summary>For a duplicate: true when the retried request carried a different payload (a client bug or tampering).</summary>
        public bool PayloadMismatch { get; }

        public LedgerAppendResult(AppendStatus status, RewardLedgerEntry entry, string reason = null, bool payloadMismatch = false)
        {
            Status = status;
            Entry = entry;
            Reason = reason;
            PayloadMismatch = payloadMismatch;
        }
    }

    /// <summary>
    /// Append-only, idempotent store for the earned-progress ledger.
    /// <para>Server integration: implement with one table whose <c>idempotency_key</c> column has a
    /// UNIQUE constraint, and run <see cref="TryAppend"/> in a transaction that locks the player's
    /// rows (or uses serializable isolation) so the precondition sees the same totals the insert
    /// commits against. A unique-violation on insert must be reported as <see cref="AppendStatus.Duplicate"/>.</para>
    /// </summary>
    public interface IRewardLedgerStore
    {
        /// <summary>
        /// Appends <paramref name="entry"/> unless its key exists. <paramref name="precondition"/> sees the
        /// player's totals before the append and returns a rejection reason, or null to allow it.
        /// </summary>
        LedgerAppendResult TryAppend(RewardLedgerEntry entry, Func<PlayerTotals, string> precondition = null);

        RewardLedgerEntry Find(string idempotencyKey);

        IReadOnlyList<RewardLedgerEntry> Entries(string playerId);

        PlayerTotals Totals(string playerId);

        /// <summary>Erases every entry of a player (account deletion). Returns the number removed.</summary>
        int DeletePlayer(string playerId);
    }

    /// <summary>Thread-safe in-memory ledger (tests, the offline guest profile, single-process hosting).</summary>
    public sealed class InMemoryRewardLedgerStore : IRewardLedgerStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, RewardLedgerEntry> _byKey = new Dictionary<string, RewardLedgerEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<RewardLedgerEntry>> _byPlayer = new Dictionary<string, List<RewardLedgerEntry>>(StringComparer.Ordinal);
        private long _sequence;

        public LedgerAppendResult TryAppend(RewardLedgerEntry entry, Func<PlayerTotals, string> precondition = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            lock (_gate)
            {
                if (_byKey.TryGetValue(entry.IdempotencyKey, out RewardLedgerEntry existing))
                    return new LedgerAppendResult(AppendStatus.Duplicate, existing, "duplicate", !existing.SamePayload(entry));
                if (precondition != null)
                {
                    string reason = precondition(TotalsLocked(entry.PlayerId));
                    if (reason != null) return new LedgerAppendResult(AppendStatus.Rejected, null, reason);
                }
                RewardLedgerEntry stored = entry.WithSequence(++_sequence);
                _byKey.Add(stored.IdempotencyKey, stored);
                if (!_byPlayer.TryGetValue(stored.PlayerId, out List<RewardLedgerEntry> list))
                    _byPlayer.Add(stored.PlayerId, list = new List<RewardLedgerEntry>());
                list.Add(stored);
                return new LedgerAppendResult(AppendStatus.Appended, stored);
            }
        }

        public RewardLedgerEntry Find(string idempotencyKey)
        {
            lock (_gate) return _byKey.TryGetValue(idempotencyKey, out RewardLedgerEntry e) ? e : null;
        }

        public IReadOnlyList<RewardLedgerEntry> Entries(string playerId)
        {
            lock (_gate) return _byPlayer.TryGetValue(playerId, out List<RewardLedgerEntry> list) ? list.ToArray() : Array.Empty<RewardLedgerEntry>();
        }

        public PlayerTotals Totals(string playerId)
        {
            lock (_gate) return TotalsLocked(playerId);
        }

        public int DeletePlayer(string playerId)
        {
            lock (_gate)
            {
                if (!_byPlayer.TryGetValue(playerId, out List<RewardLedgerEntry> list)) return 0;
                foreach (RewardLedgerEntry e in list) _byKey.Remove(e.IdempotencyKey);
                _byPlayer.Remove(playerId);
                return list.Count;
            }
        }

        /// <summary>All entries in sequence order (used to persist the offline guest profile).</summary>
        public IReadOnlyList<RewardLedgerEntry> AllEntries()
        {
            lock (_gate) return _byKey.Values.OrderBy(e => e.Sequence).ToArray();
        }

        /// <summary>Restores persisted entries (keeps their original order; sequences are reassigned).</summary>
        public void Load(IEnumerable<RewardLedgerEntry> entries)
        {
            foreach (RewardLedgerEntry e in entries.OrderBy(x => x.Sequence)) TryAppend(e);
        }

        private PlayerTotals TotalsLocked(string playerId) =>
            PlayerTotals.Fold(playerId, _byPlayer.TryGetValue(playerId, out List<RewardLedgerEntry> list) ? (IEnumerable<RewardLedgerEntry>)list : Array.Empty<RewardLedgerEntry>());
    }
}
