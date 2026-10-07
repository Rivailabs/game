using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Assets
{
    /// <summary>What kind of asset a ledger entry records.</summary>
    public enum LedgerKind : byte
    {
        Sound = 0,
        Music = 1,
        Font = 2,
        Model = 3,
        Texture = 4,
        Animation = 5,
        Reference = 6,
    }

    /// <summary>Lifecycle of an entry.</summary>
    public enum LedgerStatus : byte
    {
        /// <summary>Generated in code or a Unity built-in used for grey-box play; nothing to license.</summary>
        Placeholder = 0,
        /// <summary>Chosen but not yet imported; the licence is the intended one, not yet verified.</summary>
        Planned = 1,
        /// <summary>Imported and approved: licence, rights holder, source and hash are verified.</summary>
        Approved = 2,
        /// <summary>Rejected; kept for the audit trail and must not be referenced by a build.</summary>
        Rejected = 3,
    }

    /// <summary>One row of the asset ledger.</summary>
    public sealed class LedgerEntry
    {
        public string Id;
        public LedgerKind Kind;
        public LedgerStatus Status;
        /// <summary>Unity asset path, or "generated:&lt;what&gt;" / "builtin:&lt;name&gt;".</summary>
        public string Path;
        public string Purpose;
        /// <summary>Where it came from: vendor/library URL, provider and model, or "in-house".</summary>
        public string Source;
        public string Licence;
        public string RightsHolder;
        public string Attribution;
        /// <summary>Generation or transformation history (provider, model, version, date, inputs).</summary>
        public string Provenance;
        /// <summary>Territory restrictions, or "worldwide".</summary>
        public string Territory;
        /// <summary>SHA-256 (lowercase hex) of the final imported file; required once approved.</summary>
        public string Sha256;
    }

    /// <summary>
    /// The asset ledger (plan: "Keep an asset ledger for every sound, music track, font, model,
    /// texture and reference input"), stored as <c>Resources/Ledger/asset-ledger.json</c> in format
    /// <c>AK-ASSET-LEDGER/1</c>:
    /// <code>
    /// { "format": "AK-ASSET-LEDGER/1",
    ///   "entries": [ { "id": "audio.hit", "kind": "Sound", "status": "Approved",
    ///                  "path": "Assets/Audio/hit.wav", "purpose": "...", "source": "...",
    ///                  "licence": "...", "rights_holder": "...", "attribution": "...",
    ///                  "provenance": "...", "territory": "worldwide", "sha256": "64 hex" } ] }
    /// </code>
    /// Text is ASCII (the rules' canonical JSON subset). Validation enforces that approved entries
    /// carry the full rights record and that placeholders really are generated or built in.
    /// </summary>
    public sealed class AssetLedger
    {
        public const string Format = "AK-ASSET-LEDGER/1";

        private readonly List<LedgerEntry> _entries = new List<LedgerEntry>();

        public IReadOnlyList<LedgerEntry> Entries => _entries;

        public LedgerEntry Find(string id)
        {
            foreach (LedgerEntry e in _entries)
                if (e.Id == id) return e;
            return null;
        }

        public void Add(LedgerEntry e) => _entries.Add(e ?? throw new ArgumentNullException(nameof(e)));

        public static AssetLedger Parse(string json)
        {
            JsonNode root = JsonNode.Parse(json);
            if (root.Kind != JsonKind.Object || root["format"].AsString() != Format)
                throw new FormatException("Not an " + Format + " document.");
            var ledger = new AssetLedger();
            foreach (JsonNode n in root["entries"].AsArray())
            {
                ledger._entries.Add(new LedgerEntry
                {
                    Id = n["id"].AsString(),
                    Kind = ParseEnum<LedgerKind>(n["kind"].AsString()),
                    Status = ParseEnum<LedgerStatus>(n["status"].AsString()),
                    Path = n["path"].AsString(),
                    Purpose = n["purpose"].AsString(),
                    Source = n["source"].AsString(),
                    Licence = n["licence"].AsString(),
                    RightsHolder = n["rights_holder"].AsString(),
                    Attribution = n["attribution"].AsString(),
                    Provenance = n["provenance"].AsString(),
                    Territory = n["territory"].AsString(),
                    Sha256 = n["sha256"].AsString(),
                });
            }
            return ledger;
        }

        public string ToJson()
        {
            JsonNode entries = JsonNode.Array();
            foreach (LedgerEntry e in _entries)
            {
                entries.Push(JsonNode.Object()
                    .Add("id", e.Id).Add("kind", e.Kind.ToString()).Add("status", e.Status.ToString()).Add("path", e.Path)
                    .Add("purpose", e.Purpose).Add("source", e.Source).Add("licence", e.Licence).Add("rights_holder", e.RightsHolder)
                    .Add("attribution", e.Attribution).Add("provenance", e.Provenance).Add("territory", e.Territory).Add("sha256", e.Sha256));
            }
            return JsonNode.Object().Add("format", Format).Add("entries", entries).ToCanonicalString();
        }

        /// <summary>Problems that block a release (empty when the ledger is consistent).</summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (LedgerEntry e in _entries)
            {
                string id = e.Id ?? "(no id)";
                if (string.IsNullOrEmpty(e.Id)) problems.Add("an entry has no id");
                else if (!ids.Add(e.Id)) problems.Add(id + ": duplicate id");
                if (string.IsNullOrEmpty(e.Purpose)) problems.Add(id + ": purpose is required");
                switch (e.Status)
                {
                    case LedgerStatus.Placeholder:
                        if (e.Path == null || !(e.Path.StartsWith("generated:", StringComparison.Ordinal) || e.Path.StartsWith("builtin:", StringComparison.Ordinal)))
                            problems.Add(id + ": a placeholder must be generated in code or a Unity built-in (path 'generated:' or 'builtin:')");
                        break;
                    case LedgerStatus.Planned:
                        if (string.IsNullOrEmpty(e.Source) || string.IsNullOrEmpty(e.Licence))
                            problems.Add(id + ": a planned entry names its intended source and licence");
                        break;
                    case LedgerStatus.Approved:
                        if (string.IsNullOrEmpty(e.Path) || string.IsNullOrEmpty(e.Source) || string.IsNullOrEmpty(e.Licence) ||
                            string.IsNullOrEmpty(e.RightsHolder) || string.IsNullOrEmpty(e.Territory) || string.IsNullOrEmpty(e.Provenance))
                            problems.Add(id + ": an approved entry needs path, source, licence, rights holder, territory and provenance");
                        if (!IsSha256(e.Sha256)) problems.Add(id + ": an approved entry needs the SHA-256 of the imported file");
                        break;
                }
            }
            return problems;
        }

        /// <summary>IDs referenced by the game that are missing, or rejected, in the ledger.</summary>
        public IReadOnlyList<string> UnresolvedReferences(IEnumerable<string> referencedIds)
        {
            var missing = new List<string>();
            foreach (string id in referencedIds)
            {
                if (id == null) continue;
                LedgerEntry e = Find(id);
                if (e == null) missing.Add(id + " (missing)");
                else if (e.Status == LedgerStatus.Rejected) missing.Add(id + " (rejected)");
            }
            return missing;
        }

        private static bool IsSha256(string s)
        {
            if (s == null || s.Length != 64) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static T ParseEnum<T>(string text) where T : struct
        {
            if (text == null || !Enum.TryParse(text, false, out T value) || !Enum.IsDefined(typeof(T), value))
                throw new FormatException("Unknown " + typeof(T).Name + " '" + text + "'.");
            return value;
        }
    }
}
