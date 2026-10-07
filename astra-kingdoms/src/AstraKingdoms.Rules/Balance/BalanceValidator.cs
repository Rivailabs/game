using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Balance
{
    /// <summary>One reason a balance bundle cannot be published.</summary>
    public sealed class BalanceIssue
    {
        public string Code { get; }
        public string Detail { get; }

        public BalanceIssue(string code, string detail)
        {
            Code = code;
            Detail = detail;
        }

        public override string ToString() => Code + ": " + Detail;
    }

    /// <summary>
    /// Static validation of a balance bundle (ticket 24: "invalid releases fail validation"). It
    /// checks identity rules (the frozen baseline, version-ID shape, lineage), that every override
    /// names a schema tunable, is in range and actually differs from the baseline, and cross-field
    /// consistency. History-dependent checks (ID reuse, unknown predecessor, executability) live in
    /// <see cref="BalanceChannel"/>.
    /// </summary>
    public static class BalanceValidator
    {
        public const int MaxIdLength = 48;
        public const int MaxNotesLength = 400;

        /// <summary>Base rule versions this assembly can describe. Only AK-TR-1 exists.</summary>
        public static readonly IReadOnlyList<string> KnownBaseVersions = new[] { RulesConstants.RulesVersion };

        public static IReadOnlyList<BalanceIssue> Validate(BalanceBundle bundle)
        {
            var issues = new List<BalanceIssue>();
            if (bundle == null)
            {
                issues.Add(new BalanceIssue("NULL", "No bundle."));
                return issues;
            }

            // ---- identity ----
            bool knownBase = false;
            foreach (string v in KnownBaseVersions) knownBase |= v == bundle.BaseRulesVersion;
            if (!knownBase) issues.Add(new BalanceIssue("BASE_VERSION", "Unknown base rules version '" + bundle.BaseRulesVersion + "'."));

            if (!IsValidId(bundle.BundleId))
                issues.Add(new BalanceIssue("BUNDLE_ID", "Bundle IDs are 1-" + MaxIdLength + " ASCII letters, digits, '.', '-' or '_'."));
            else if (knownBase)
            {
                bool isBaseId = bundle.BundleId == bundle.BaseRulesVersion;
                if (isBaseId && bundle.Overrides.Count > 0)
                    issues.Add(new BalanceIssue("FROZEN_BASELINE",
                        bundle.BaseRulesVersion + " is frozen; any change needs a new bundle ID such as " + bundle.BaseRulesVersion + ".b2."));
                if (!isBaseId && !bundle.BundleId.StartsWith(bundle.BaseRulesVersion + ".", StringComparison.Ordinal))
                    issues.Add(new BalanceIssue("BUNDLE_ID", "A tuned bundle ID must start with '" + bundle.BaseRulesVersion + ".'."));
                if (!isBaseId && bundle.Overrides.Count == 0)
                    issues.Add(new BalanceIssue("EMPTY_RELEASE", "A new bundle ID without overrides would duplicate the baseline."));
                if (!isBaseId && string.IsNullOrEmpty(bundle.PreviousBundleId))
                    issues.Add(new BalanceIssue("LINEAGE", "A tuned bundle must name the bundle it was derived from."));
            }
            if (bundle.PreviousBundleId != null && bundle.PreviousBundleId == bundle.BundleId)
                issues.Add(new BalanceIssue("LINEAGE", "A bundle cannot be its own predecessor."));
            if (bundle.Notes.Length > MaxNotesLength || !IsAscii(bundle.Notes))
                issues.Add(new BalanceIssue("NOTES", "Notes are ASCII text of at most " + MaxNotesLength + " characters."));

            // ---- overrides ----
            foreach (var kv in bundle.Overrides)
            {
                if (!TunableSchema.TryGet(kv.Key, out TunableDefinition def))
                {
                    issues.Add(new BalanceIssue("UNKNOWN_TUNABLE", "'" + kv.Key + "' is not tunable (structural values are frozen)."));
                    continue;
                }
                if (!def.InRange(kv.Value))
                    issues.Add(new BalanceIssue("OUT_OF_RANGE", def.Name + " = " + kv.Value + " is outside " + def.Min + ".." + def.Max + "."));
                else if (kv.Value == def.Baseline)
                    issues.Add(new BalanceIssue("REDUNDANT_OVERRIDE", def.Name + " equals its baseline; remove it."));
            }

            // ---- cross-field consistency (only meaningful when every name is known) ----
            bool allKnown = true;
            foreach (var kv in bundle.Overrides) allKnown &= TunableSchema.TryGet(kv.Key, out _);
            if (allKnown) CrossField(bundle, issues);
            return issues;
        }

        public static bool IsValid(BalanceBundle bundle) => Validate(bundle).Count == 0;

        private static void CrossField(BalanceBundle b, List<BalanceIssue> issues)
        {
            if (b.ValueOf("Timing.SharedPhoneCutWindowMs") < b.ValueOf("Timing.OnlineCutWindowMs"))
                issues.Add(new BalanceIssue("CROSS_FIELD", "The shared-phone cut window cannot be shorter than the online window."));
            if (b.ValueOf("Timing.SharedPhoneHandoverMs") >= b.ValueOf("Timing.ChoiceDeadlineMs"))
                issues.Add(new BalanceIssue("CROSS_FIELD", "The handover must be shorter than one player's choice time."));
            if (b.ValueOf("Land.VajraMinExclusiveDiffUnits") >= b.ValueOf("Duel.StartHpUnits"))
                issues.Add(new BalanceIssue("CROSS_FIELD", "Vajra would be unreachable: its HP-difference gate is not below starting HP."));
            foreach (WeaponDefinition w in WeaponCatalog.All)
            {
                long unlock = b.ValueOf(TunableSchema.WeaponUnlockName(w.Id));
                if (w.IsStarter && unlock != 1)
                    issues.Add(new BalanceIssue("CROSS_FIELD", "Starter weapon " + w.Id + " must stay available from level 1."));
                if (!w.IsStarter && unlock < 2)
                    issues.Add(new BalanceIssue("CROSS_FIELD", "Weapon " + w.Id + " is introduced by progression and needs a level of at least 2."));
            }
        }

        private static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength) return false;
            foreach (char c in id)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        private static bool IsAscii(string s)
        {
            foreach (char c in s)
                if (c > 0x7F) return false;
            return true;
        }
    }
}
