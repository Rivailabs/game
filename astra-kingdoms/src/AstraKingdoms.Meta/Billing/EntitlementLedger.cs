using System;
using System.Collections.Generic;
using System.Linq;

namespace AstraKingdoms.Meta.Billing
{
    public enum EntitlementAction : byte
    {
        Grant = 0,
        Revoke = 1,
    }

    /// <summary>Why an entitlement entry was written.</summary>
    public enum EntitlementReason : byte
    {
        VerifiedPurchase = 0,
        /// <summary>Play reported the purchase voided (refund, chargeback, cancellation, auto-refund after no acknowledgement).</summary>
        Voided = 1,
        /// <summary>A corrective transaction written by support/operations (documented reference required).</summary>
        SupportCorrection = 2,
    }

    /// <summary>
    /// One immutable line of the paid-entitlement ledger. Paid entitlements are kept apart from the
    /// earned-progress ledger: a purchase grants listed cosmetics (or the remove-ads entitlement),
    /// never coins, XP or weapon access.
    /// </summary>
    public sealed class EntitlementEntry
    {
        public long Sequence { get; }
        public string IdempotencyKey { get; }
        public string PlayerId { get; }
        public string Sku { get; }
        public string PurchaseToken { get; }
        public string OrderId { get; }
        public EntitlementAction Action { get; }
        public EntitlementReason Reason { get; }
        public bool IsTestPurchase { get; }
        public string Note { get; }
        public DateTimeOffset At { get; }

        public EntitlementEntry(string idempotencyKey, string playerId, string sku, string purchaseToken, string orderId,
            EntitlementAction action, EntitlementReason reason, DateTimeOffset at, bool isTestPurchase = false, string note = null, long sequence = 0)
        {
            if (string.IsNullOrEmpty(idempotencyKey)) throw new ArgumentException("key required", nameof(idempotencyKey));
            IdempotencyKey = idempotencyKey;
            PlayerId = playerId;
            Sku = sku;
            PurchaseToken = purchaseToken;
            OrderId = orderId;
            Action = action;
            Reason = reason;
            At = at;
            IsTestPurchase = isTestPurchase;
            Note = note;
            Sequence = sequence;
        }

        internal EntitlementEntry With(long sequence, string playerId) =>
            new EntitlementEntry(IdempotencyKey, playerId, Sku, PurchaseToken, OrderId, Action, Reason, At, IsTestPurchase, Note, sequence);

        public override string ToString() => "#" + Sequence + " " + Action + " " + Sku + " (" + Reason + ")";
    }

    /// <summary>
    /// Append-only paid-entitlement ledger.
    /// <para>Server integration: a table with UNIQUE(idempotency_key) and an index on purchase_token;
    /// entries are inserted, never updated or deleted, except that account deletion replaces the
    /// player id with a pseudonym (<see cref="Pseudonymise"/>) because purchase records are kept for
    /// accounting, tax and fraud purposes (see docs/privacy-data-map.md).</para>
    /// </summary>
    public interface IEntitlementLedgerStore
    {
        /// <summary>Appends unless the key exists; returns the stored (or pre-existing) entry and whether it was new.</summary>
        EntitlementEntry TryAppend(EntitlementEntry entry, out bool appended);
        EntitlementEntry Find(string idempotencyKey);
        IReadOnlyList<EntitlementEntry> ForPlayer(string playerId);
        IReadOnlyList<EntitlementEntry> ForToken(string purchaseToken);
        int Pseudonymise(string playerId, string pseudonym);
    }

    public sealed class InMemoryEntitlementLedgerStore : IEntitlementLedgerStore
    {
        private readonly object _gate = new object();
        private readonly List<EntitlementEntry> _entries = new List<EntitlementEntry>();
        private readonly Dictionary<string, EntitlementEntry> _byKey = new Dictionary<string, EntitlementEntry>(StringComparer.Ordinal);

        public EntitlementEntry TryAppend(EntitlementEntry entry, out bool appended)
        {
            lock (_gate)
            {
                if (_byKey.TryGetValue(entry.IdempotencyKey, out EntitlementEntry existing))
                {
                    appended = false;
                    return existing;
                }
                EntitlementEntry stored = entry.With(_entries.Count + 1, entry.PlayerId);
                _entries.Add(stored);
                _byKey.Add(stored.IdempotencyKey, stored);
                appended = true;
                return stored;
            }
        }

        public EntitlementEntry Find(string idempotencyKey)
        {
            lock (_gate) return _byKey.TryGetValue(idempotencyKey, out EntitlementEntry e) ? e : null;
        }

        public IReadOnlyList<EntitlementEntry> ForPlayer(string playerId)
        {
            lock (_gate) return _entries.Where(e => e.PlayerId == playerId).ToArray();
        }

        public IReadOnlyList<EntitlementEntry> ForToken(string purchaseToken)
        {
            lock (_gate) return _entries.Where(e => e.PurchaseToken == purchaseToken).ToArray();
        }

        public int Pseudonymise(string playerId, string pseudonym)
        {
            lock (_gate)
            {
                int n = 0;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].PlayerId != playerId) continue;
                    EntitlementEntry replaced = _entries[i].With(_entries[i].Sequence, pseudonym);
                    _entries[i] = replaced;
                    _byKey[replaced.IdempotencyKey] = replaced;
                    n++;
                }
                return n;
            }
        }

        public IReadOnlyList<EntitlementEntry> All()
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    /// <summary>A player's current paid entitlements, folded from the ledger.</summary>
    public sealed class PlayerEntitlements
    {
        public IReadOnlyCollection<string> ActiveSkus { get; }

        public PlayerEntitlements(IEnumerable<string> activeSkus) => ActiveSkus = new HashSet<string>(activeSkus, StringComparer.Ordinal);

        public bool Owns(string sku) => ActiveSkus.Contains(sku);

        public static readonly PlayerEntitlements None = new PlayerEntitlements(Array.Empty<string>());

        /// <summary>
        /// Per purchase token, the latest entry decides (grant = active, revoke = inactive); a SKU is
        /// owned while any of its tokens is active.
        /// </summary>
        public static PlayerEntitlements Fold(IEnumerable<EntitlementEntry> entries)
        {
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (IGrouping<string, EntitlementEntry> token in entries.GroupBy(e => e.PurchaseToken ?? e.IdempotencyKey))
            {
                EntitlementEntry last = token.OrderBy(e => e.Sequence).Last();
                if (last.Action == EntitlementAction.Grant) active.Add(last.Sku);
            }
            return new PlayerEntitlements(active);
        }
    }
}
