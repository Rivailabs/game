using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Kingdom
{
    /// <summary>How a stored layout was read.</summary>
    public enum LayoutLoadStatus : byte
    {
        /// <summary>Current schema, every placement valid.</summary>
        Ok = 0,
        /// <summary>An older schema was upgraded (the caller should save the migrated layout).</summary>
        Migrated = 1,
        /// <summary>Some placements were invalid and were dropped; the rest were kept.</summary>
        Repaired = 2,
        /// <summary>The data was unreadable (corrupt, truncated, unknown future schema); the safe default layout is used.</summary>
        RecoveredDefault = 3,
    }

    public sealed class LayoutLoadResult
    {
        public HomelandLayout Layout { get; }
        public LayoutLoadStatus Status { get; }
        public IReadOnlyList<string> Notes { get; }

        public LayoutLoadResult(HomelandLayout layout, LayoutLoadStatus status, IReadOnlyList<string> notes)
        {
            Layout = layout;
            Status = status;
            Notes = notes;
        }
    }

    /// <summary>
    /// Versioned layout persistence. Schema 2 (current):
    /// <c>{"schema":2,"revision":n,"catalog":v,"placements":[{"plot":p,"slot":"main"|"accent","id":"decor.x","rot":r}]}</c>.
    /// Schema 1 (the first V2 prototype) stored one decoration per plot:
    /// <c>{"schema":1,"revision":n,"plots":[{"plot":p,"decor":"decor.x"}]}</c> and migrates into the main
    /// slot. Loading never throws: unreadable data yields the safe default layout, so a corrupt save,
    /// an interrupted write or a restored backup from another version always shows a usable homeland.
    /// </summary>
    public static class LayoutSerializer
    {
        public static string Write(HomelandLayout layout)
        {
            JsonNode root = JsonNode.Object()
                .Add("schema", HomelandLayout.SchemaVersion)
                .Add("revision", layout.Revision)
                .Add("catalog", layout.CatalogVersion);
            JsonNode list = JsonNode.Array();
            foreach (Placement p in layout.Placements)
                list.Push(JsonNode.Object().Add("plot", p.Plot).Add("slot", p.Slot == PlotSlot.Main ? "main" : "accent")
                    .Add("id", p.DecorationId).Add("rot", p.Rotation));
            root.Add("placements", list);
            return root.ToCanonicalString();
        }

        public static LayoutLoadResult Read(string json, HomelandRules rules, DecorationCatalog catalog)
        {
            var notes = new List<string>();
            if (string.IsNullOrEmpty(json))
                return new LayoutLoadResult(HomelandLayout.Empty(catalog.Version), LayoutLoadStatus.Ok, notes);
            JsonNode root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (FormatException ex)
            {
                notes.Add("unreadable layout: " + ex.Message);
                return new LayoutLoadResult(HomelandLayout.Empty(catalog.Version), LayoutLoadStatus.RecoveredDefault, notes);
            }
            long schema = Json.Long(root, "schema", -1);
            long revision = Math.Max(0, Json.Long(root, "revision", 0));
            var raw = new List<Placement>();
            LayoutLoadStatus status = LayoutLoadStatus.Ok;
            int catalogVersion;
            if (schema == 1)
            {
                status = LayoutLoadStatus.Migrated;
                catalogVersion = 1;
                foreach (JsonNode p in Json.Array(root, "plots"))
                {
                    string id = Json.Str(p, "decor");
                    if (id != null) raw.Add(new Placement((int)Json.Long(p, "plot", -1), PlotSlot.Main, id, 0));
                }
                notes.Add("migrated schema 1 -> " + HomelandLayout.SchemaVersion);
            }
            else if (schema == HomelandLayout.SchemaVersion)
            {
                catalogVersion = (int)Json.Long(root, "catalog", catalog.Version);
                foreach (JsonNode p in Json.Array(root, "placements"))
                {
                    string id = Json.Str(p, "id");
                    string slot = Json.Str(p, "slot");
                    if (id == null || (slot != "main" && slot != "accent"))
                    {
                        notes.Add("dropped a malformed placement");
                        status = LayoutLoadStatus.Repaired;
                        continue;
                    }
                    raw.Add(new Placement((int)Json.Long(p, "plot", -1), slot == "main" ? PlotSlot.Main : PlotSlot.Accent, id, (int)Json.Long(p, "rot", 0)));
                }
            }
            else
            {
                notes.Add("unknown layout schema " + schema);
                return new LayoutLoadResult(HomelandLayout.Empty(catalog.Version), LayoutLoadStatus.RecoveredDefault, notes);
            }

            var kept = new List<Placement>();
            foreach (Placement p in raw)
            {
                string problem = StructuralProblem(p, rules, catalog);
                if (problem == null && kept.Any(k => k.Plot == p.Plot && k.Slot == p.Slot)) problem = "two placements in one slot";
                if (problem != null)
                {
                    notes.Add("dropped " + p + ": " + problem);
                    if (status == LayoutLoadStatus.Ok) status = LayoutLoadStatus.Repaired;
                    continue;
                }
                kept.Add(p);
            }
            return new LayoutLoadResult(new HomelandLayout(revision, Math.Max(1, catalogVersion), kept), status, notes);
        }

        /// <summary>
        /// Structural validity independent of ownership and plot unlocks: plot range, rotation, and a
        /// known item in the right slot. Unknown ids are kept only if they look like decorations (a
        /// newer catalogue); they display as the safe default.
        /// </summary>
        public static string StructuralProblem(Placement p, HomelandRules rules, DecorationCatalog catalog)
        {
            if (p.Plot < 0 || p.Plot >= rules.PlotCount) return "no such plot";
            if (p.Rotation < 0 || p.Rotation > 3) return "rotation out of range";
            DecorationItem item = catalog.Get(p.DecorationId);
            if (item == null) return p.DecorationId.StartsWith("decor.", StringComparison.Ordinal) ? null : "not a decoration";
            if (item.Slot != p.Slot) return "wrong slot for a " + item.Kind;
            return null;
        }
    }
}
