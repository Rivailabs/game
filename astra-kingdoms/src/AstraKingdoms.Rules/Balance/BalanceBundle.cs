using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Rules.Balance
{
    /// <summary>
    /// A versioned balance release (ticket 24): a set of overrides of <see cref="TunableSchema"/>
    /// values on top of a frozen base ruleset. The baseline bundle has the base version as its ID
    /// and no overrides; any change needs a new, never-reused bundle ID that starts with the base
    /// version (for example <c>AK-TR-1.b2</c>). Bundles are immutable once constructed.
    /// <para>
    /// The <see cref="EffectiveRulesHash"/> is the ordinary rules hash computed over the base
    /// rules bundle contents with the overrides applied and the bundle ID as rules version, so a
    /// tuned match can never be replayed (or resolved) by an engine of another configuration.
    /// </para>
    /// </summary>
    public sealed class BalanceBundle
    {
        public const string Format = "AK-BALANCE-BUNDLE/1";

        private readonly SortedDictionary<string, long> _overrides;
        private byte[] _contentHash;
        private byte[] _effectiveHash;
        private RulesParameters _parameters;

        public string BundleId { get; }
        public string BaseRulesVersion { get; }
        /// <summary>Bundle this release was derived from (null for the baseline).</summary>
        public string PreviousBundleId { get; }
        /// <summary>Short ASCII change note for the release log.</summary>
        public string Notes { get; }
        /// <summary>Overrides by tunable name, in ordinal name order.</summary>
        public IReadOnlyDictionary<string, long> Overrides => _overrides;

        public bool IsBaseline => _overrides.Count == 0 && BundleId == BaseRulesVersion;

        public BalanceBundle(string bundleId, string baseRulesVersion, IEnumerable<KeyValuePair<string, long>> overrides,
            string previousBundleId = null, string notes = null)
        {
            BundleId = bundleId;
            BaseRulesVersion = baseRulesVersion;
            PreviousBundleId = previousBundleId;
            Notes = notes ?? string.Empty;
            _overrides = new SortedDictionary<string, long>(StringComparer.Ordinal);
            if (overrides != null)
            {
                foreach (var kv in overrides)
                {
                    if (kv.Key == null) throw new ArgumentException("Override names cannot be null.", nameof(overrides));
                    if (_overrides.ContainsKey(kv.Key)) throw new ArgumentException("Duplicate override '" + kv.Key + "'.", nameof(overrides));
                    _overrides.Add(kv.Key, kv.Value);
                }
            }
        }

        /// <summary>The compiled AK-TR-1 rules as a bundle (no overrides).</summary>
        public static BalanceBundle Baseline() => new BalanceBundle(RulesConstants.RulesVersion, RulesConstants.RulesVersion, null);

        /// <summary>The value in force for a tunable: the override, or the baseline.</summary>
        public long ValueOf(string tunableName)
        {
            if (!TunableSchema.TryGet(tunableName, out TunableDefinition def)) throw new ArgumentException("Unknown tunable '" + tunableName + "'.");
            return _overrides.TryGetValue(tunableName, out long v) ? v : def.Baseline;
        }

        /// <summary>SHA-256 over the canonical bundle encoding (identity of the release file content).</summary>
        public byte[] ContentHash => (byte[])(_contentHash ?? (_contentHash = ComputeContentHash())).Clone();

        public string ContentHashHex => Hex.Encode(_contentHash ?? (_contentHash = ComputeContentHash()));

        /// <summary>
        /// Rules hash of the effective contents (base contents + overrides, rules version = bundle ID).
        /// For the baseline bundle this equals <see cref="RulesBundle.Hash"/>. Throws for overrides that
        /// are not in the schema; call <see cref="BalanceValidator.Validate"/> first.
        /// </summary>
        public byte[] EffectiveRulesHash => (byte[])(_effectiveHash ?? (_effectiveHash = ToRulesContents().ComputeHash())).Clone();

        public string EffectiveRulesHashHex => Hex.Encode(_effectiveHash ?? (_effectiveHash = ToRulesContents().ComputeHash()));

        /// <summary>
        /// The engine parameters of this release (ticket 24), cached. The AK-TR-1 baseline returns
        /// the shared <see cref="RulesParameters.Default"/>. Throws <see cref="ArgumentException"/>
        /// when the bundle fails <see cref="BalanceValidator"/>. Pin the result to a match with
        /// <see cref="MatchConfig.WithParameters"/>.
        /// </summary>
        public RulesParameters ToParameters()
        {
            RulesParameters cached = _parameters;
            if (cached != null) return cached;
            bool compiledBaseline = IsBaseline && BundleId == RulesConstants.RulesVersion;
            RulesParameters built = compiledBaseline ? RulesParameters.Default : RulesParameters.FromBundle(this);
            // First writer wins, so every caller (and every pinned match) shares one instance.
            return System.Threading.Interlocked.CompareExchange(ref _parameters, built, null) ?? built;
        }

        /// <summary>The base rules contents with the overrides applied.</summary>
        public RulesBundleContents ToRulesContents()
        {
            RulesBundleContents c = RulesBundleContents.Current();
            if (IsBaseline) return c;
            c.RulesVersion = BundleId;
            foreach (var kv in _overrides)
            {
                if (!TunableSchema.TryGet(kv.Key, out TunableDefinition def)) throw new InvalidOperationException("Unknown tunable '" + kv.Key + "'.");
                Apply(c, def, kv.Value);
            }
            return c;
        }

        private static void Apply(RulesBundleContents c, TunableDefinition def, long value)
        {
            switch (def.Target)
            {
                case TunableTarget.Constant:
                    int i = c.Constants.FindIndex(k => k.Key == def.ConstantName);
                    if (i < 0) throw new InvalidOperationException("Constant '" + def.ConstantName + "' is not in the rules bundle.");
                    c.Constants[i] = new KeyValuePair<string, long>(def.ConstantName, value);
                    break;
                case TunableTarget.CardCap:
                    int ci = c.CardCaps.FindIndex(k => k.Key == def.EntityId);
                    c.CardCaps[ci] = new KeyValuePair<int, int>(def.EntityId, checked((int)value));
                    break;
                case TunableTarget.WeaponDamage:
                    Row(c, def.EntityId).DamageUnits = checked((int)value);
                    break;
                case TunableTarget.WeaponMass:
                    Row(c, def.EntityId).Mass = checked((int)value);
                    break;
                case TunableTarget.WeaponUnlockLevel:
                    Row(c, def.EntityId).UnlockLevel = checked((int)value);
                    break;
                default:
                    throw new InvalidOperationException("Unknown tunable target " + def.Target + ".");
            }
        }

        private static WeaponRow Row(RulesBundleContents c, int weaponId)
        {
            foreach (WeaponRow row in c.Weapons)
                if (row.Id == weaponId) return row;
            throw new InvalidOperationException("Weapon " + weaponId + " is not in the rules bundle.");
        }

        private byte[] ComputeContentHash()
        {
            var w = new CanonicalWriter();
            w.Ascii(Format).Ascii(BundleId ?? string.Empty).Ascii(BaseRulesVersion ?? string.Empty)
             .Ascii(PreviousBundleId ?? string.Empty).Ascii(AsciiOnly(Notes));
            w.U32((uint)_overrides.Count);
            foreach (var kv in _overrides) w.Named(AsciiOnly(kv.Key), kv.Value);
            return w.Sha256();
        }

        private static string AsciiOnly(string s)
        {
            if (s == null) return string.Empty;
            foreach (char ch in s)
                if (ch > 0x7F) return "?non-ascii?";
            return s;
        }

        // ------------------------------------------------------------------ JSON

        /// <summary>Canonical JSON release file.</summary>
        public string ToJson()
        {
            JsonNode overrides = JsonNode.Object();
            foreach (var kv in _overrides) overrides.Add(kv.Key, kv.Value);
            return JsonNode.Object()
                .Add("format", Format)
                .Add("bundle_id", BundleId)
                .Add("base_rules_version", BaseRulesVersion)
                .Add("previous_bundle_id", PreviousBundleId)
                .Add("notes", Notes)
                .Add("overrides", overrides)
                .ToCanonicalString();
        }

        /// <summary>Parses a release file. Throws <see cref="FormatException"/> for malformed text.</summary>
        public static BalanceBundle FromJson(string json)
        {
            JsonNode root = JsonNode.Parse(json);
            if (root.Kind != JsonKind.Object) throw new FormatException("A balance bundle must be a JSON object.");
            string format = root["format"].AsString();
            if (format != Format) throw new FormatException("Unsupported balance bundle format '" + format + "'.");
            JsonNode overridesNode = root["overrides"];
            if (overridesNode.Kind != JsonKind.Object) throw new FormatException("'overrides' must be an object.");
            var overrides = new List<KeyValuePair<string, long>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in overridesNode.Members)
            {
                if (!seen.Add(m.Key)) throw new FormatException("Duplicate override '" + m.Key + "'.");
                overrides.Add(new KeyValuePair<string, long>(m.Key, m.Value.AsLong()));
            }
            return new BalanceBundle(root["bundle_id"].AsString(), root["base_rules_version"].AsString(), overrides,
                root["previous_bundle_id"].AsString(), root["notes"].AsString());
        }

        public override string ToString() => BundleId + " (" + _overrides.Count + " overrides, content " + ContentHashHex.Substring(0, 12) + ")";
    }
}
