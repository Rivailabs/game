using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AstraKingdoms.Rules.Balance;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Ranked
{
    /// <summary>
    /// The rules and catalogue a season's ranked matches use, frozen for the season and shown before
    /// queue entry (plan: "Display the rules/catalog snapshot before entry"; "Freeze the approved
    /// balance snapshot for the season"). Ranked uses the normalised Full catalogue: every participant
    /// can equip the same eligible weapons regardless of account progression, with the unchanged
    /// equipment limits of the rules.
    /// </summary>
    public sealed class SeasonBalanceSnapshot
    {
        public string SnapshotId { get; }
        public string SeasonId { get; }
        public string BalanceBundleId { get; }
        public string RulesVersion { get; }
        public string EffectiveRulesHashHex { get; }
        public CatalogPreset Catalog { get; }
        public IReadOnlyList<int> EligibleWeaponIds { get; }
        public IReadOnlyList<CardId> Cards { get; }
        public int MinSlots { get; }
        public int MaxSlots { get; }
        public string RankedRulesVersion { get; }
        public DateTimeOffset FrozenAt { get; }

        public SeasonBalanceSnapshot(string snapshotId, string seasonId, BalanceBundle bundle, RankedRules ranked, DateTimeOffset frozenAt)
        {
            if (bundle == null) throw new ArgumentNullException(nameof(bundle));
            SnapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
            SeasonId = seasonId ?? throw new ArgumentNullException(nameof(seasonId));
            BalanceBundleId = bundle.BundleId;
            RulesVersion = bundle.BaseRulesVersion;
            EffectiveRulesHashHex = bundle.EffectiveRulesHashHex;
            Catalog = CatalogPreset.Full;
            EligibleWeaponIds = WeaponCatalog.ForPreset(CatalogPreset.Full).Select(w => w.Id).ToArray();
            Cards = Rules.Core.Cards.All.ToArray();
            MinSlots = RulesConstants.MinSlots;
            MaxSlots = RulesConstants.FullMaxSlots;
            RankedRulesVersion = (ranked ?? RankedRules.Default).Version;
            FrozenAt = frozenAt;
        }

        /// <summary>Short form of the rules hash for the entry screen.</summary>
        public string ShortHash => EffectiveRulesHashHex.Substring(0, Math.Min(12, EffectiveRulesHashHex.Length));

        /// <summary>
        /// Ranked is symmetric by construction: the eligible list depends only on the snapshot, never on
        /// an account, and the slot limits equal the rules' own Full-catalogue limits.
        /// </summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (MaxSlots != RulesConstants.FullMaxSlots || MinSlots != RulesConstants.MinSlots) errors.Add("equipment limits differ from the rules");
            if (EligibleWeaponIds.Count != RulesConstants.RegularWeaponCount) errors.Add("ranked must use the full normalised catalogue");
            if (EligibleWeaponIds.Contains(RulesConstants.BrahmastraWeaponId)) errors.Add("Brahmastra is a private experiment, never ranked");
            return errors;
        }
    }

    /// <summary>
    /// A documented emergency change to a frozen season snapshot (plan: "an emergency exploit fix needs
    /// an incident decision and player explanation").
    /// </summary>
    public sealed class EmergencyChangeRecord
    {
        public string IncidentId { get; }
        /// <summary>Who decided (owner role), recorded for the audit trail.</summary>
        public string DecidedBy { get; }
        public string Reason { get; }
        /// <summary>The message shown to players (required; published with the change).</summary>
        public string PlayerExplanation { get; }
        public string PreviousSnapshotId { get; }
        public string NewSnapshotId { get; }
        public DateTimeOffset At { get; }

        public EmergencyChangeRecord(string incidentId, string decidedBy, string reason, string playerExplanation,
            string previousSnapshotId, string newSnapshotId, DateTimeOffset at)
        {
            IncidentId = incidentId;
            DecidedBy = decidedBy;
            Reason = reason;
            PlayerExplanation = playerExplanation;
            PreviousSnapshotId = previousSnapshotId;
            NewSnapshotId = newSnapshotId;
            At = at;
        }
    }

    public enum SnapshotChangeStatus : byte
    {
        Applied = 0,
        /// <summary>The season already has a frozen snapshot and no valid emergency record was supplied.</summary>
        Frozen = 1,
        InvalidRecord = 2,
        InvalidSnapshot = 3,
    }

    /// <summary>
    /// Per-season snapshot registry. The first snapshot of a season is frozen on publication; the only
    /// way to replace it is <see cref="ApplyEmergencyChange"/> with a complete record. Matches created
    /// under an earlier snapshot of the same season remain valid (they are recorded with their id).
    /// </summary>
    public sealed class SnapshotRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, List<SeasonBalanceSnapshot>> _history = new Dictionary<string, List<SeasonBalanceSnapshot>>(StringComparer.Ordinal);
        private readonly List<EmergencyChangeRecord> _emergencies = new List<EmergencyChangeRecord>();
        private readonly AuditLog _audit;

        public SnapshotRegistry(AuditLog audit = null) => _audit = audit ?? new AuditLog();

        public SnapshotChangeStatus Freeze(SeasonBalanceSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Validate().Count > 0) return SnapshotChangeStatus.InvalidSnapshot;
            lock (_gate)
            {
                if (_history.ContainsKey(snapshot.SeasonId)) return SnapshotChangeStatus.Frozen;
                _history[snapshot.SeasonId] = new List<SeasonBalanceSnapshot> { snapshot };
                _audit.Write(snapshot.FrozenAt, "system", "snapshot.freeze", snapshot.SeasonId, snapshot.SnapshotId + " " + snapshot.BalanceBundleId);
                return SnapshotChangeStatus.Applied;
            }
        }

        public SnapshotChangeStatus ApplyEmergencyChange(SeasonBalanceSnapshot replacement, EmergencyChangeRecord record)
        {
            if (replacement == null || replacement.Validate().Count > 0) return SnapshotChangeStatus.InvalidSnapshot;
            if (record == null || string.IsNullOrWhiteSpace(record.IncidentId) || string.IsNullOrWhiteSpace(record.DecidedBy) ||
                string.IsNullOrWhiteSpace(record.PlayerExplanation) || string.IsNullOrWhiteSpace(record.Reason) || record.NewSnapshotId != replacement.SnapshotId)
                return SnapshotChangeStatus.InvalidRecord;
            lock (_gate)
            {
                if (!_history.TryGetValue(replacement.SeasonId, out List<SeasonBalanceSnapshot> list)) return SnapshotChangeStatus.InvalidRecord;
                if (record.PreviousSnapshotId != list[list.Count - 1].SnapshotId) return SnapshotChangeStatus.InvalidRecord;
                if (list.Any(s => s.SnapshotId == replacement.SnapshotId)) return SnapshotChangeStatus.InvalidRecord;
                list.Add(replacement);
                _emergencies.Add(record);
                _audit.Write(record.At, record.DecidedBy, "snapshot.emergency", replacement.SeasonId,
                    record.IncidentId + " " + record.PreviousSnapshotId + " -> " + record.NewSnapshotId);
                return SnapshotChangeStatus.Applied;
            }
        }

        /// <summary>The snapshot new matches of the season use.</summary>
        public SeasonBalanceSnapshot Current(string seasonId)
        {
            lock (_gate) return _history.TryGetValue(seasonId, out var list) ? list[list.Count - 1] : null;
        }

        /// <summary>True when a match recorded with <paramref name="snapshotId"/> belongs to the season's history.</summary>
        public bool IsKnown(string seasonId, string snapshotId)
        {
            lock (_gate) return _history.TryGetValue(seasonId, out var list) && list.Any(s => s.SnapshotId == snapshotId);
        }

        public IReadOnlyList<EmergencyChangeRecord> EmergencyChanges
        {
            get { lock (_gate) return _emergencies.ToArray(); }
        }

        public static string SnapshotIdFor(string seasonId, int revision) => seasonId + ".snap" + revision.ToString(CultureInfo.InvariantCulture);
    }
}
