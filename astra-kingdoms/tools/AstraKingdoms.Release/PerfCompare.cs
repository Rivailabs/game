using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>
/// <c>perf-compare</c> (ticket 73, memory part of 75): reads the development build's
/// <c>AK-AUTOPLAY-REPORT/1</c> and the device service's <c>dumpsys meminfo</c> capture, then applies
/// <see cref="PerfGate"/>: 30 fps target, p95 &lt;= 35 ms, peak total PSS &lt;= 400 MB, p99 and long stalls
/// recorded, regression against the stored baseline for the same scenario and registered phone.
/// <c>--update-baseline</c> records a new baseline only from a fully passing run on a registered
/// phone and only with a named approver (a threshold or baseline change is a reviewed change).
/// </summary>
public static partial class PerfCompare
{
    public const string BaselineFormat = "AK-PERF-BASELINE/1";
    public const string DeviceFormat = "AK-DEVICE-REGISTER/1";

    public static PerfMeasurement FromAutoplayReport(JsonNode root)
    {
        if (Util.Str(root, "format") != "AK-AUTOPLAY-REPORT/1") throw new FormatException("Not an AK-AUTOPLAY-REPORT/1 document.");
        JsonNode f = root["frames"];
        return new PerfMeasurement
        {
            Scenario = Util.Str(root, "scenario"),
            DeviceModel = Util.Str(root, "device_model"),
            Platform = Util.Str(root, "platform"),
            AppVersion = Util.Str(root, "app_version"),
            ScenarioPassed = Util.Bool(root, "passed"),
            Samples = Util.LongOrNull(f, "samples") ?? 0,
            MeanUs = Util.LongOrNull(f, "mean_us") ?? 0,
            P50Us = Util.LongOrNull(f, "p50_us") ?? 0,
            P95Us = Util.LongOrNull(f, "p95_us") ?? 0,
            P99Us = Util.LongOrNull(f, "p99_us") ?? 0,
            MaxUs = Util.LongOrNull(f, "max_us") ?? 0,
            StallsOver100Ms = Util.LongOrNull(f, "stalls_over_100ms") ?? 0,
        };
    }

    [GeneratedRegex(@"TOTAL\s+PSS:\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalPssSummary();

    [GeneratedRegex(@"^\s*TOTAL\s+(\d+)\b", RegexOptions.Multiline)]
    private static partial Regex TotalRow();

    /// <summary>Total PSS in bytes from one <c>adb shell dumpsys meminfo &lt;package&gt;</c> output (kB = KiB), or null.</summary>
    public static long? ParseMeminfoPssBytes(string text)
    {
        Match m = TotalPssSummary().Match(text);
        if (!m.Success) m = TotalRow().Match(text);
        return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1024 : null;
    }

    public static List<string> LoadDevices(string path)
    {
        if (path == null || !File.Exists(path)) return new List<string>();
        JsonNode root = Util.ReadJson(path);
        if (Util.Str(root, "format") != DeviceFormat) throw new FormatException(path + " is not " + DeviceFormat);
        return root["devices"]!.AsArray().Select(d => Util.Str(d, "model")).Where(s => s.Length > 0).ToList();
    }

    public static PerfMeasurement FindBaseline(JsonNode baselines, string scenario, string device)
    {
        if (baselines == null) return null;
        if (Util.Str(baselines, "format") != BaselineFormat) throw new FormatException("Not an " + BaselineFormat + " document.");
        foreach (JsonNode e in baselines["entries"]!.AsArray())
        {
            if (Util.Str(e, "scenario") != scenario || Util.Str(e, "device_model") != device) continue;
            return new PerfMeasurement
            {
                Scenario = scenario,
                DeviceModel = device,
                P95Us = Util.LongOrNull(e, "p95_us") ?? 0,
                P99Us = Util.LongOrNull(e, "p99_us") ?? 0,
                StallsOver100Ms = Util.LongOrNull(e, "stalls_over_100ms") ?? 0,
                PeakPssBytes = Util.LongOrNull(e, "peak_pss_bytes"),
            };
        }
        return null;
    }

    public static JsonObject BaselineEntry(PerfMeasurement m, string approvedBy, string commit, DateTimeOffset now) => new()
    {
        ["scenario"] = m.Scenario,
        ["device_model"] = m.DeviceModel,
        ["app_version"] = m.AppVersion,
        ["commit"] = commit ?? "",
        ["mean_us"] = m.MeanUs,
        ["p50_us"] = m.P50Us,
        ["p95_us"] = m.P95Us,
        ["p99_us"] = m.P99Us,
        ["max_us"] = m.MaxUs,
        ["stalls_over_100ms"] = m.StallsOver100Ms,
        ["peak_pss_bytes"] = m.PeakPssBytes,
        ["approved_by"] = approvedBy,
        ["recorded_utc"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    };

    public static int Cli(CliArgs a)
    {
        PerfMeasurement m = FromAutoplayReport(Util.ReadJson(a.Require("report")));
        // Plain autoplay runs report the scenario as "autoplay"; the device script knows the real
        // scenario id, and baselines are keyed by it.
        if (a.Get("scenario") is string scenario) m.Scenario = scenario;
        var pssSamples = new List<long>();
        var meminfoFiles = a.All("meminfo").ToList();
        foreach (string dir in a.All("meminfo-dir"))
            meminfoFiles.AddRange(Directory.GetFiles(dir, "meminfo-*.txt").OrderBy(x => x, StringComparer.Ordinal));
        foreach (string f in meminfoFiles)
            if (ParseMeminfoPssBytes(File.ReadAllText(f)) is long b) pssSamples.Add(b);
        if (a.Long("pss-kb") is long kb) pssSamples.Add(kb * 1024);
        if (pssSamples.Count > 0) m.PeakPssBytes = pssSamples.Max();
        m.VideoCaptureActive = a.Flag("video-capture");
        string baselinePath = a.Get("baseline");
        JsonNode baselines = baselinePath != null && File.Exists(baselinePath) ? Util.ReadJson(baselinePath) : null;
        PerfMeasurement baseline = FindBaseline(baselines, m.Scenario, m.DeviceModel);
        GateReport r = PerfGate.Evaluate(m, baseline, new PerfTolerance(), LoadDevices(a.Get("devices")));
        var extra = new JsonObject { ["measurement"] = BaselineEntry(m, "", a.Get("commit"), DateTimeOffset.UtcNow) };
        int code = GateJson.Emit(r, a.Get("out"), extra);

        if (a.Flag("update-baseline"))
        {
            string approver = a.Get("approved-by");
            bool clean = r.Findings.All(f => f.Status == GateStatus.Pass || f.Id == "baseline");
            if (string.IsNullOrWhiteSpace(approver) || !clean || baselinePath == null)
            {
                Console.Error.WriteLine("baseline NOT updated: needs --baseline, --approved-by and a run whose only non-pass finding is the missing baseline");
                return code == 0 ? 2 : code;
            }
            var root = baselines?.AsObject() ?? new JsonObject { ["format"] = BaselineFormat, ["entries"] = new JsonArray() };
            JsonArray entries = root["entries"]!.AsArray();
            foreach (JsonNode old in entries.Where(e => Util.Str(e, "scenario") == m.Scenario && Util.Str(e, "device_model") == m.DeviceModel).ToList())
                entries.Remove(old);
            entries.Add(BaselineEntry(m, approver, a.Get("commit"), DateTimeOffset.UtcNow));
            Util.WriteJson(baselinePath, root);
            Console.WriteLine("baseline updated for " + m.Scenario + " on " + m.DeviceModel + " (approved by " + approver + ")");
        }
        return code;
    }
}
