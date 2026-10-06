using System.Globalization;
using System.Text.Json.Nodes;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>
/// <c>size-check</c> (ticket 74): records AAB, APK, device-specific compressed initial download,
/// installed size and optional downloaded content **separately** (plan) and applies the 80 MB
/// initial-download budget through <see cref="SizeGate"/>. The download figure must come from
/// <c>bundletool get-size total --apks=&lt;apks&gt; --device-spec=&lt;reference.json&gt;</c> (MAX column); a
/// file size is never substituted for it. The Unity build report (<c>AK-BUILD-REPORT/1</c>) supplies
/// the largest contributors so a regression can be traced to assets.
/// </summary>
public static class SizeCheck
{
    /// <summary>MAX (or the only) value from bundletool's get-size CSV; null if unreadable.</summary>
    public static long? ParseBundletoolSize(string csv)
    {
        string[] lines = csv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return null;
        string[] header = lines[0].Split(',');
        int col = Array.FindIndex(header, h => h.Trim().Equals("MAX", StringComparison.OrdinalIgnoreCase));
        long? max = null;
        foreach (string row in lines.Skip(1))
        {
            string[] cells = row.Split(',');
            int c = col >= 0 && col < cells.Length ? col : cells.Length - 1;
            if (long.TryParse(cells[c].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                max = max.HasValue ? Math.Max(max.Value, v) : v;
        }
        return max;
    }

    /// <summary>Top contributors and per-type totals from an AK-BUILD-REPORT/1 export.</summary>
    public static JsonObject SummariseBuildReport(JsonNode report, int top = 15)
    {
        if (Util.Str(report, "format") != "AK-BUILD-REPORT/1") throw new FormatException("Not an AK-BUILD-REPORT/1 document.");
        var topAssets = new JsonArray();
        foreach (JsonNode a in (report["top_assets"]?.AsArray() ?? new JsonArray()).Take(top))
            topAssets.Add(new JsonObject { ["path"] = Util.Str(a, "path"), ["packed_size"] = Util.LongOrNull(a, "packed_size") });
        var types = new JsonArray();
        foreach (JsonNode t in (report["packed_by_type"]?.AsArray() ?? new JsonArray())
                     .OrderByDescending(t => Util.LongOrNull(t, "packed_size") ?? 0))
            types.Add(new JsonObject { ["type"] = Util.Str(t, "type"), ["packed_size"] = Util.LongOrNull(t, "packed_size") });
        return new JsonObject
        {
            ["result"] = Util.Str(report, "result"),
            ["platform"] = Util.Str(report, "platform"),
            ["total_size"] = Util.LongOrNull(report, "total_size"),
            ["top_assets"] = topAssets,
            ["packed_by_type"] = types,
        };
    }

    public static JsonObject RecordJson(SizeRecord s) => new()
    {
        ["format"] = "AK-SIZE-RECORD/1",
        ["aab_bytes"] = s.AabBytes,
        ["apk_bytes"] = s.ApkBytes,
        ["initial_download_bytes"] = s.InitialDownloadBytes,
        ["device_spec"] = s.DeviceSpec,
        ["installed_bytes"] = s.InstalledBytes,
        ["on_demand_bytes"] = s.OnDemandBytes,
        ["budget_initial_download_bytes"] = ReleaseBudgets.InitialDownloadBytes,
    };

    public static int Cli(CliArgs a)
    {
        var s = new SizeRecord
        {
            AabBytes = a.Get("aab") is string aab ? new FileInfo(aab).Length : null,
            ApkBytes = a.Get("apk") is string apk ? new FileInfo(apk).Length : null,
            InitialDownloadBytes = a.Get("bundletool-size") is string csv ? ParseBundletoolSize(File.ReadAllText(csv)) : a.Long("download-bytes"),
            DeviceSpec = a.Get("device-spec", ""),
            InstalledBytes = a.Long("installed-bytes"),
            OnDemandBytes = a.Long("on-demand-bytes"),
        };
        GateReport r = SizeGate.Evaluate(s);
        var extra = new JsonObject { ["size_record"] = RecordJson(s) };
        if (a.Get("build-report") is string br)
        {
            if (File.Exists(br)) extra["build_report"] = SummariseBuildReport(Util.ReadJson(br));
            else r.Add("build_report", GateStatus.Warn, br + " not found (export it with the editor's BuildReportExporter)");
        }
        return GateJson.Emit(r, a.Get("out"), extra);
    }
}
