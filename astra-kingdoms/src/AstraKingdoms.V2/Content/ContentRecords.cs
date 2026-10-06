using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Content
{
    public enum ContentKind : byte
    {
        Weapon = 0,
        Card = 1,
        Terrain = 2,
    }

    /// <summary>Review state of a content record. Only Approved records may carry final statistics and ship.</summary>
    public enum ContentStatus : byte
    {
        /// <summary>An idea with role and counterplay; statistics deliberately empty.</summary>
        Proposed = 0,
        InReview = 1,
        Approved = 2,
        Retired = 3,
    }

    /// <summary>
    /// V2 content ceilings (plan: "40 weapons, ten formation cards and six total terrain categories").
    /// Ceilings are limits, not promises to fill a catalogue.
    /// </summary>
    public sealed class ContentLimits
    {
        public const string CurrentVersion = "AK-CONTENT-V2";

        public string Version { get; } = CurrentVersion;
        public int MaxWeapons { get; set; } = 40;
        public int MaxCards { get; set; } = 10;
        public int MaxTerrains { get; set; } = 6;

        public static readonly ContentLimits V2 = new ContentLimits();

        public int Max(ContentKind kind) => kind == ContentKind.Weapon ? MaxWeapons : kind == ContentKind.Card ? MaxCards : MaxTerrains;
    }

    /// <summary>
    /// One reviewed (or proposed) content record. The role is a tag from a controlled vocabulary;
    /// together with the element (weapons) it forms the record's role signature, which must differ from
    /// every existing item so new content is a readable alternative rather than a stronger replacement.
    /// Counterplay names concrete answers (a dodge, a weapon, an ability, an element, a card or a
    /// terrain) that already exist.
    /// </summary>
    public sealed class ContentRecord
    {
        public ContentKind Kind { get; }
        public string Id { get; }
        public ContentStatus Status { get; }
        public string Role { get; }
        /// <summary>Weapons only: the element (Agni, Vayu, Prithvi, Vidyut, Varuna).</summary>
        public string Element { get; }
        public IReadOnlyList<string> Counterplay { get; }
        public string DistinctionNote { get; }
        public string AssetId { get; }
        /// <summary>Name of the automated test that covers this record (must exist before approval).</summary>
        public string TestCoverageHook { get; }
        /// <summary>Final statistics (integer values, units as in the rules bundle). Empty unless approved.</summary>
        public IReadOnlyDictionary<string, long> Stats { get; }
        public string ReviewedBy { get; }
        public string ReviewReference { get; }

        public ContentRecord(ContentKind kind, string id, ContentStatus status, string role, string element, IEnumerable<string> counterplay,
            string distinctionNote, string assetId, string testCoverageHook, IDictionary<string, long> stats = null, string reviewedBy = null, string reviewReference = null)
        {
            Kind = kind;
            Id = id;
            Status = status;
            Role = role;
            Element = element;
            Counterplay = (counterplay ?? Array.Empty<string>()).ToArray();
            DistinctionNote = distinctionNote;
            AssetId = assetId;
            TestCoverageHook = testCoverageHook;
            Stats = new SortedDictionary<string, long>(stats ?? new Dictionary<string, long>(), StringComparer.Ordinal);
            ReviewedBy = reviewedBy;
            ReviewReference = reviewReference;
        }

        public string Signature => Kind == ContentKind.Weapon ? "weapon|" + (Element ?? "?") + "|" + Role : Kind.ToString().ToLowerInvariant() + "|" + Role;
        public bool Counts => Status != ContentStatus.Retired;
    }

    /// <summary>The shipped V1 content expressed as role signatures (the baseline new records are validated against).</summary>
    public static class V1ContentBaseline
    {
        /// <summary>The tactical role each V1 weapon ability represents.</summary>
        public static string RoleOf(WeaponAbility a)
        {
            switch (a)
            {
                case WeaponAbility.Burn: return "lingering-damage";
                case WeaponAbility.Push: return "displacement";
                case WeaponAbility.JumpPierce: return "anti-jump";
                case WeaponAbility.Shock: return "ability-denial";
                case WeaponAbility.Cleanse: return "status-removal";
                case WeaponAbility.FanSpread: return "spread";
                case WeaponAbility.TwinCurve: return "curving-pair";
                case WeaponAbility.GroundBurst: return "ground-burst";
                case WeaponAbility.ChainBonus: return "follow-up-bonus";
                case WeaponAbility.Veil: return "concealment";
                case WeaponAbility.AshShield: return "shield";
                case WeaponAbility.ReverseDodge: return "dodge-reversal";
                case WeaponAbility.IronWall: return "wall";
                case WeaponAbility.Net: return "movement-lock";
                case WeaponAbility.RemoveCover: return "cover-removal";
                case WeaponAbility.StraightLance: return "straight-line";
                case WeaponAbility.IgnoreCover: return "cover-bypass";
                case WeaponAbility.Quake: return "quake";
                case WeaponAbility.ThunderAdvantage: return "element-amplify";
                default: return "heal";
            }
        }

        public static readonly IReadOnlyDictionary<CardId, string> CardRoles = new Dictionary<CardId, string>
        {
            { CardId.Chakra, "wheel" }, { CardId.Garuda, "wing" }, { CardId.Suchi, "needle" },
            { CardId.Makara, "jaw" }, { CardId.Padma, "lotus" }, { CardId.Vajra, "thunderbolt" },
        };

        public static readonly IReadOnlyDictionary<TerrainType, string> TerrainRoles = new Dictionary<TerrainType, string>
        {
            { TerrainType.Plain, "neutral" }, { TerrainType.Fort, "cover" }, { TerrainType.River, "flow" },
            { TerrainType.Forest, "concealment" }, { TerrainType.Armoury, "arms" },
        };

        public static IReadOnlyList<string> Signatures()
        {
            var list = WeaponCatalog.All.Select(w => "weapon|" + w.Element + "|" + RoleOf(w.Ability)).ToList();
            list.AddRange(CardRoles.Values.Select(r => "card|" + r));
            list.AddRange(TerrainRoles.Values.Select(r => "terrain|" + r));
            return list;
        }

        public static int Count(ContentKind kind) =>
            kind == ContentKind.Weapon ? RulesConstants.RegularWeaponCount : kind == ContentKind.Card ? CardRoles.Count : TerrainRoles.Count;

        /// <summary>Tokens counterplay may reference: dodges, V1 weapons, abilities, elements, cards and terrains.</summary>
        public static ISet<string> CounterplayTokens()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dodge d in Enum.GetValues(typeof(Dodge))) if (d != Dodge.None) set.Add("dodge." + d.ToString().ToLowerInvariant());
            foreach (WeaponDefinition w in WeaponCatalog.All) set.Add("weapon." + w.Id.ToString(CultureInfo.InvariantCulture));
            foreach (WeaponAbility a in Enum.GetValues(typeof(WeaponAbility))) set.Add("ability." + a);
            foreach (Element e in Enum.GetValues(typeof(Element))) if (e != Element.Neutral) set.Add("element." + e);
            foreach (CardId c in Enum.GetValues(typeof(CardId))) set.Add("card." + c);
            foreach (TerrainType t in Enum.GetValues(typeof(TerrainType))) set.Add("terrain." + t);
            set.Add("tactic.spacing");
            set.Add("tactic.power-control");
            return set;
        }
    }

    /// <summary>
    /// Validates V2 content records against the V1 baseline and the ceilings. This is the extension
    /// point: new weapons, cards and terrain enter as records, and only Approved records with final
    /// statistics, review references and an existing test can be promoted into a rules bundle.
    /// </summary>
    public static class ContentRegistry
    {
        /// <summary>Statistics an approved weapon must specify (the rules bundle's weapon row fields).</summary>
        public static readonly IReadOnlyList<string> RequiredWeaponStats = new[] { "damageUnits", "projectileCount", "speed", "trajectory", "mass", "radiusMm", "unlockLevel" };
        public static readonly IReadOnlyList<string> RequiredCardStats = new[] { "capPercent" };
        public static readonly IReadOnlyList<string> RequiredTerrainStats = new[] { "templateRevision" };

        public static readonly IReadOnlyCollection<string> Elements = new HashSet<string>(StringComparer.Ordinal) { "Agni", "Vayu", "Prithvi", "Vidyut", "Varuna" };

        public static IReadOnlyList<string> Validate(IReadOnlyList<ContentRecord> records, ContentLimits limits = null)
        {
            limits = limits ?? ContentLimits.V2;
            var errors = new List<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var signatures = new HashSet<string>(V1ContentBaseline.Signatures(), StringComparer.Ordinal);
            ISet<string> tokens = V1ContentBaseline.CounterplayTokens();
            foreach (ContentRecord r in records) if (r.Counts) tokens.Add(r.Id);

            foreach (ContentKind kind in Enum.GetValues(typeof(ContentKind)))
            {
                int total = V1ContentBaseline.Count(kind) + records.Count(r => r.Kind == kind && r.Counts);
                if (total > limits.Max(kind)) errors.Add(kind + ": " + total + " exceeds the V2 ceiling of " + limits.Max(kind));
            }
            foreach (ContentRecord r in records)
            {
                string p = r.Id + ": ";
                if (string.IsNullOrWhiteSpace(r.Id) || !ids.Add(r.Id)) errors.Add(p + "missing or duplicate id");
                string idProblem = IdProblem(r);
                if (idProblem != null) errors.Add(p + idProblem);
                if (string.IsNullOrWhiteSpace(r.Role)) errors.Add(p + "role is required");
                if (r.Kind == ContentKind.Weapon && !Elements.Contains(r.Element ?? string.Empty)) errors.Add(p + "weapon needs an element");
                if (r.Counts && !signatures.Add(r.Signature)) errors.Add(p + "role signature " + r.Signature + " duplicates existing content (new items must be alternatives, not replacements)");
                if (r.Counterplay.Count == 0) errors.Add(p + "counterplay is required");
                foreach (string c in r.Counterplay)
                    if (!tokens.Contains(c) || c == r.Id) errors.Add(p + "counterplay '" + c + "' does not name existing content or a known tactic");
                if (string.IsNullOrWhiteSpace(r.DistinctionNote)) errors.Add(p + "explain how it differs from existing content");
                if (r.AssetId == null || !r.AssetId.StartsWith("asset." + r.Kind.ToString().ToLowerInvariant() + ".", StringComparison.Ordinal))
                    errors.Add(p + "asset id must look like asset." + r.Kind.ToString().ToLowerInvariant() + ".<name>");
                if (string.IsNullOrWhiteSpace(r.TestCoverageHook)) errors.Add(p + "test coverage hook is required");
                if (r.Status == ContentStatus.Approved)
                {
                    if (string.IsNullOrWhiteSpace(r.ReviewedBy) || string.IsNullOrWhiteSpace(r.ReviewReference)) errors.Add(p + "approval needs reviewer and review reference");
                    IReadOnlyList<string> required = r.Kind == ContentKind.Weapon ? RequiredWeaponStats : r.Kind == ContentKind.Card ? RequiredCardStats : RequiredTerrainStats;
                    foreach (string s in required) if (!r.Stats.ContainsKey(s)) errors.Add(p + "approved record lacks stat '" + s + "'");
                }
                else if (r.Stats.Count > 0 && r.Status == ContentStatus.Proposed)
                {
                    errors.Add(p + "proposed records carry no statistics (final values come from review)");
                }
            }
            return errors;
        }

        private static string IdProblem(ContentRecord r)
        {
            if (r.Id == null) return "no id";
            switch (r.Kind)
            {
                case ContentKind.Weapon:
                    return r.Id.StartsWith("weapon.", StringComparison.Ordinal) && int.TryParse(r.Id.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out int w) &&
                           w > RulesConstants.RegularWeaponCount && w <= ContentLimits.V2.MaxWeapons
                        ? null : "weapon ids are weapon.21 .. weapon.40";
                case ContentKind.Card:
                    return r.Id.StartsWith("card.", StringComparison.Ordinal) && int.TryParse(r.Id.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out int c) &&
                           c > (int)CardId.Vajra && c <= ContentLimits.V2.MaxCards
                        ? null : "card ids are card.7 .. card.10";
                default:
                    return r.Id.StartsWith("terrain.", StringComparison.Ordinal) && r.Id.Length > 8 && !Enum.GetNames(typeof(TerrainType)).Any(n => "terrain." + n == r.Id)
                        ? null : "terrain ids are terrain.<new name>";
            }
        }

        /// <summary>Approved records whose coverage hook names a test that does not exist.</summary>
        public static IReadOnlyList<string> MissingCoverage(IEnumerable<ContentRecord> records, ICollection<string> existingTests) =>
            records.Where(r => r.Status == ContentStatus.Approved && !existingTests.Contains(r.TestCoverageHook)).Select(r => r.Id).ToArray();
    }

    /// <summary>JSON form of a record set (see Content/Records/v2-proposed-template.json).</summary>
    public static class ContentRecordSet
    {
        public const string Format = "AK-CONTENT-RECORDS/1";

        public static IReadOnlyList<ContentRecord> Parse(string json)
        {
            JsonNode root = JsonNode.Parse(json);
            if (Json.Str(root, "format") != Format) throw new FormatException("expected format " + Format);
            var list = new List<ContentRecord>();
            foreach (JsonNode n in Json.Array(root, "records"))
            {
                if (!Enum.TryParse(Json.Str(n, "kind", string.Empty), true, out ContentKind kind)) throw new FormatException("bad kind");
                if (!Enum.TryParse(Json.Str(n, "status", string.Empty), true, out ContentStatus status)) throw new FormatException("bad status");
                var stats = new Dictionary<string, long>(StringComparer.Ordinal);
                JsonNode s = Json.Member(n, "stats");
                if (s != null && s.Kind == JsonKind.Object)
                    foreach (KeyValuePair<string, JsonNode> kv in s.Members) stats[kv.Key] = kv.Value.AsLong();
                list.Add(new ContentRecord(kind, Json.Str(n, "id"), status, Json.Str(n, "role"), Json.Str(n, "element"), Json.Strings(n, "counterplay"),
                    Json.Str(n, "distinction"), Json.Str(n, "assetId"), Json.Str(n, "testHook"), stats, Json.Str(n, "reviewedBy"), Json.Str(n, "reviewRef")));
            }
            return list;
        }
    }
}
