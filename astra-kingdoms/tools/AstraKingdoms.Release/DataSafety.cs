using System.Text;
using System.Text.Json.Nodes;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>
/// <c>data-safety</c> (ticket 77; plan "Store Data safety declarations reflect actual SDKs and data
/// flows"): generates the Google Play Data safety form **draft** from the privacy data map
/// (<c>AK-PRIVACY-DATA-MAP/1</c>) and the SDK inventory (<c>AK-SDK-INVENTORY/1</c>), and checks that
/// the two agree: every data type an SDK sends is declared by a data-map row; every SDK in the
/// build is chosen and has a release-build traffic capture; no row is still vendor-TBD. Until then the
/// result is INCOMPLETE, never PASS. The draft needs legal review before submission.
/// </summary>
public static class DataSafety
{
    /// <summary>Play Data safety categories and types (as listed in Play Console's form).</summary>
    public static readonly (string Category, string[] Types)[] PlayTypes =
    {
        ("Location", new[] { "Approximate location", "Precise location" }),
        ("Personal info", new[] { "Name", "Email address", "User IDs", "Address", "Phone number", "Race and ethnicity", "Political or religious beliefs", "Sexual orientation", "Other info" }),
        ("Financial info", new[] { "User payment info", "Purchase history", "Credit score", "Other financial info" }),
        ("Health and fitness", new[] { "Health info", "Fitness info" }),
        ("Messages", new[] { "Emails", "SMS or MMS", "Other in-app messages" }),
        ("Photos and videos", new[] { "Photos", "Videos" }),
        ("Audio files", new[] { "Voice or sound recordings", "Music files", "Other audio files" }),
        ("Files and docs", new[] { "Files and docs" }),
        ("Calendar", new[] { "Calendar events" }),
        ("Contacts", new[] { "Contacts" }),
        ("App activity", new[] { "App interactions", "In-app search history", "Installed apps", "Other user-generated content", "Other actions" }),
        ("Web browsing", new[] { "Web browsing history" }),
        ("App info and performance", new[] { "Crash logs", "Diagnostics", "Other app performance data" }),
        ("Device or other IDs", new[] { "Device or other IDs" }),
    };

    public sealed class Row
    {
        public string Id = "", Data = "", PlayType, Shared = "no", Optional = "required", Status = "", Destination = "";
        public bool Collected;
        public List<string> Purposes = new();
    }

    public sealed class Sdk
    {
        public string Id = "", Name = "", Vendor = "", Status = "", TrafficCapture;
        public List<string> DataTypes = new(), DataMapRows = new();
    }

    public static List<Row> LoadRows(JsonNode root)
    {
        if (Util.Str(root, "format") != "AK-PRIVACY-DATA-MAP/1") throw new FormatException("Not an AK-PRIVACY-DATA-MAP/1 document.");
        return root["rows"]!.AsArray().Select(n => new Row
        {
            Id = Util.Str(n, "id"),
            Data = Util.Str(n, "data"),
            PlayType = n["play_type"] is JsonValue v && v.TryGetValue(out string s) ? s : null,
            Collected = Util.Bool(n, "collected"),
            Shared = Util.Str(n, "shared", "no"),
            Optional = Util.Str(n, "optional", "required"),
            Status = Util.Str(n, "status"),
            Destination = Util.Str(n, "destination"),
            Purposes = (n["purposes"]?.AsArray() ?? new JsonArray()).Select(p => p!.GetValue<string>()).ToList(),
        }).ToList();
    }

    public static List<Sdk> LoadSdks(JsonNode root)
    {
        if (Util.Str(root, "format") != "AK-SDK-INVENTORY/1") throw new FormatException("Not an AK-SDK-INVENTORY/1 document.");
        return root["sdks"]!.AsArray().Select(n => new Sdk
        {
            Id = Util.Str(n, "id"),
            Name = Util.Str(n, "name"),
            Vendor = Util.Str(n, "vendor"),
            Status = Util.Str(n, "status"),
            TrafficCapture = n["traffic_capture"] is JsonValue v && v.TryGetValue(out string s) ? s : null,
            DataTypes = (n["data_types"]?.AsArray() ?? new JsonArray()).Select(p => p!.GetValue<string>()).ToList(),
            DataMapRows = (n["data_map_rows"]?.AsArray() ?? new JsonArray()).Select(p => p!.GetValue<string>()).ToList(),
        }).ToList();
    }

    public static (GateReport Report, string Markdown) Build(List<Row> rows, List<Sdk> sdks, string candidate)
    {
        var r = new GateReport("data safety declarations");
        var validTypes = PlayTypes.SelectMany(c => c.Types.Select(t => c.Category + "/" + t)).ToHashSet(StringComparer.Ordinal);
        var rowIds = rows.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

        foreach (Row x in rows.Where(x => x.PlayType != null && !validTypes.Contains(x.PlayType)))
            r.Add("data-map", GateStatus.Fail, x.Id + ": '" + x.PlayType + "' is not a Play Data safety type");
        foreach (Row x in rows.Where(x => x.Status == "vendor-tbd" || x.Shared == "vendor-tbd"))
            r.Add("data-map", GateStatus.Incomplete, x.Id + ": recipient is vendor-TBD; the 'shared' answer cannot be given");
        foreach (Sdk s in sdks)
        {
            if (s.Status == "not-chosen") r.Add("sdk." + s.Id, GateStatus.Incomplete, s.Name + ": vendor not chosen");
            else if (s.Status == "planned") r.Add("sdk." + s.Id, GateStatus.Incomplete, s.Name + ": planned, not integrated; declarations must describe the shipped build");
            else if (string.IsNullOrEmpty(s.TrafficCapture))
                r.Add("sdk." + s.Id, GateStatus.Incomplete, s.Name + ": no release-build traffic capture showing its actual network behaviour");
            else r.Add("sdk." + s.Id, GateStatus.Pass, s.Name + ": behaviour captured in " + s.TrafficCapture);
            foreach (string id in s.DataMapRows.Where(id => !rowIds.Contains(id)))
                r.Add("sdk." + s.Id, GateStatus.Fail, s.Name + " refers to unknown data-map row '" + id + "'");
            foreach (string t in s.DataTypes)
            {
                if (!validTypes.Contains(t)) r.Add("sdk." + s.Id, GateStatus.Fail, s.Name + ": '" + t + "' is not a Play Data safety type");
                else if (!rows.Any(x => x.Collected && x.PlayType == t && (s.DataMapRows.Count == 0 || s.DataMapRows.Contains(x.Id))))
                    r.Add("sdk." + s.Id, GateStatus.Fail, s.Name + " sends '" + t + "' but no collected data-map row declares it");
            }
        }
        if (r.Findings.All(f => f.Status == GateStatus.Pass))
            r.Add("consistency", GateStatus.Pass, "SDK inventory and data map agree");

        var md = new StringBuilder();
        md.Append("# Google Play Data safety: generated draft\n\n");
        md.Append("> **DRAFT generated by `AstraKingdoms.Release data-safety`. Needs legal review. Do not submit while the gate is ")
          .Append(GateReport.Label(r.Overall)).Append(".**\n");
        md.Append("> Candidate: ").Append(string.IsNullOrEmpty(candidate) ? "(none named)" : candidate)
          .Append(". Inputs: `release/declarations/privacy-data-map.json`, `release/declarations/sdk-inventory.json`.\n\n");
        md.Append("## Answers by data type\n\n| Category | Type | Collected | Shared | Optional | Purposes | Source rows |\n| --- | --- | --- | --- | --- | --- | --- |\n");
        foreach ((string cat, string[] types) in PlayTypes)
            foreach (string t in types)
            {
                var hits = rows.Where(x => x.Collected && x.PlayType == cat + "/" + t).ToList();
                if (hits.Count == 0)
                {
                    md.Append("| ").Append(cat).Append(" | ").Append(t).Append(" | No | - | - | - | - |\n");
                    continue;
                }
                string shared = hits.Any(h => h.Shared == "vendor-tbd") ? "**TBD (vendor)**" : hits.Any(h => h.Shared == "yes") ? "Yes" : "No";
                string optional = hits.All(h => h.Optional == "optional") ? "Optional" : "Required";
                string purposes = string.Join(", ", hits.SelectMany(h => h.Purposes).Distinct(StringComparer.Ordinal));
                md.Append("| ").Append(cat).Append(" | ").Append(t).Append(" | Yes | ").Append(shared).Append(" | ").Append(optional).Append(" | ")
                  .Append(purposes).Append(" | ").Append(string.Join(", ", hits.Select(h => h.Id))).Append(" |\n");
            }
        md.Append("\n## SDKs in the build\n\n| SDK | Vendor | Status | Data types | Traffic capture |\n| --- | --- | --- | --- | --- |\n");
        foreach (Sdk s in sdks)
            md.Append("| ").Append(s.Name).Append(" | ").Append(s.Vendor).Append(" | ").Append(s.Status).Append(" | ")
              .Append(s.DataTypes.Count == 0 ? "none declared" : string.Join(", ", s.DataTypes)).Append(" | ")
              .Append(s.TrafficCapture ?? "**missing**").Append(" |\n");
        md.Append("\n## Security practices (to verify on the release build)\n\n");
        md.Append("- Data encrypted in transit: **verify** with a traffic capture of the release build (HTTPS only).\n");
        md.Append("- Users can request deletion: in-app (Home > Profile and shop > Privacy and account > Delete my data) and the web URL in `PrivacyLinks` (**placeholder until set**).\n");
        md.Append("- Families policy: applies if children are in the target audience (see `docs/privacy-data-map.md` section 4).\n");
        md.Append("\n## Gate findings\n\n");
        foreach (GateFinding f in r.Findings) md.Append("- ").Append(GateReport.Label(f.Status)).Append(" `").Append(f.Id).Append("`: ").Append(f.Message).Append('\n');
        return (r, md.ToString());
    }

    public static int Cli(CliArgs a)
    {
        (GateReport r, string md) = Build(LoadRows(Util.ReadJson(a.Require("data-map"))), LoadSdks(Util.ReadJson(a.Require("sdk-inventory"))), a.Get("candidate", ""));
        if (a.Get("md-out") is string mdOut)
        {
            File.WriteAllText(mdOut, md);
            Console.WriteLine("wrote " + mdOut);
        }
        return GateJson.Emit(r, a.Get("out"));
    }
}
