using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>
/// <c>sustained-collate</c> (ticket 75; plan gate "Sustained play: repeat representative matches for a
/// 20-minute run ... report thermal degradation and crashes" and "Memory: peak total PSS"): collates
/// what the device script <c>ci/device/sustained-run.sh</c> captures during the
/// <c>sustained-20m</c> scenario: periodic <c>dumpsys meminfo</c>, <c>dumpsys thermalservice</c> and
/// <c>dumpsys battery</c> samples (files named <c>&lt;kind&gt;-&lt;epoch seconds&gt;.txt</c>), the logcat
/// and the autoplay report. It reports run length, peak PSS, a PSS trend (leak indicator), thermal
/// status and temperatures, crashes/ANRs, and frame-time degradation against a cold pilot-smoke run.
/// </summary>
public static partial class SustainedCollate
{
    /// <summary>PROPOSED leak warning: PSS growing faster than 1 MB per minute after warm-up.</summary>
    public const double LeakWarnBytesPerMinute = 1_000_000;
    /// <summary>Android thermal status SEVERE (PowerManager.THERMAL_STATUS_SEVERE).</summary>
    public const int ThermalSevere = 3;

    public sealed class Sample<T>
    {
        public long Epoch;
        public T Value;
    }

    public sealed class Thermal
    {
        public int? Status;
        public double? MaxSkinC;
    }

    public sealed class Evidence
    {
        public List<Sample<long>> Pss = new();
        public List<Sample<Thermal>> Thermal = new();
        public List<Sample<double>> BatteryC = new();
        public List<string> Crashes = new();
        public PerfMeasurement Sustained;
        public PerfMeasurement Cold;
        public long ReportWallSeconds;
    }

    [GeneratedRegex(@"Thermal Status:\s*(\d+)")]
    private static partial Regex ThermalStatus();

    [GeneratedRegex(@"Temperature\{mValue=([-\d.]+),\s*mType=(\d+),\s*mName=([^,]*),\s*mStatus=(\d+)\}")]
    private static partial Regex ThermalTemperature();

    [GeneratedRegex(@"^\s*temperature:\s*(-?\d+)", RegexOptions.Multiline)]
    private static partial Regex BatteryTemperature();

    [GeneratedRegex(@"FATAL EXCEPTION|ANR in |Fatal signal \d+|FORGE_SCENARIO_RESULT:\s*FAIL|lowmemorykiller.*kill", RegexOptions.IgnoreCase)]
    private static partial Regex CrashMarker();

    [GeneratedRegex(@"^(meminfo|thermal|battery)-(\d+)\.txt$")]
    private static partial Regex SampleName();

    public static Thermal ParseThermal(string text)
    {
        var t = new Thermal();
        Match s = ThermalStatus().Match(text);
        if (s.Success) t.Status = int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture);
        foreach (Match m in ThermalTemperature().Matches(text))
        {
            // mType 3 = TYPE_SKIN (android.os.Temperature).
            if (m.Groups[2].Value != "3") continue;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            t.MaxSkinC = t.MaxSkinC.HasValue ? Math.Max(t.MaxSkinC.Value, v) : v;
        }
        return t;
    }

    /// <summary>Battery temperature in degrees C (dumpsys reports tenths), or null.</summary>
    public static double? ParseBattery(string text)
    {
        Match m = BatteryTemperature().Match(text);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 10.0 : null;
    }

    public static List<string> FindCrashes(string logcat) =>
        logcat.Replace("\r\n", "\n").Split('\n').Where(l => CrashMarker().IsMatch(l)).Select(l => l.Trim()).Distinct().Take(50).ToList();

    /// <summary>Least-squares slope of PSS over time, in bytes per minute (null with fewer than 3 samples).</summary>
    public static double? SlopeBytesPerMinute(IReadOnlyList<Sample<long>> s)
    {
        if (s.Count < 3) return null;
        double mx = s.Average(x => (double)x.Epoch), my = s.Average(x => (double)x.Value);
        double num = s.Sum(x => (x.Epoch - mx) * (x.Value - my)), den = s.Sum(x => (x.Epoch - mx) * (x.Epoch - mx));
        return den == 0 ? null : num / den * 60.0;
    }

    public static Evidence Load(string samplesDir, string reportPath, string coldReportPath, string logcatPath)
    {
        var ev = new Evidence();
        if (samplesDir != null && Directory.Exists(samplesDir))
        {
            foreach (string f in Directory.EnumerateFiles(samplesDir).OrderBy(x => x, StringComparer.Ordinal))
            {
                Match m = SampleName().Match(Path.GetFileName(f));
                if (!m.Success) continue;
                long epoch = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                string text = File.ReadAllText(f);
                switch (m.Groups[1].Value)
                {
                    case "meminfo":
                        if (PerfCompare.ParseMeminfoPssBytes(text) is long b) ev.Pss.Add(new Sample<long> { Epoch = epoch, Value = b });
                        break;
                    case "thermal":
                        ev.Thermal.Add(new Sample<Thermal> { Epoch = epoch, Value = ParseThermal(text) });
                        break;
                    case "battery":
                        if (ParseBattery(text) is double c) ev.BatteryC.Add(new Sample<double> { Epoch = epoch, Value = c });
                        break;
                }
            }
        }
        if (reportPath != null && File.Exists(reportPath))
        {
            JsonNode report = Util.ReadJson(reportPath);
            ev.Sustained = PerfCompare.FromAutoplayReport(report);
            ev.ReportWallSeconds = (report["matches"]?.AsArray() ?? new JsonArray()).Sum(x => Util.LongOrNull(x, "wall_ms") ?? 0) / 1000;
        }
        if (coldReportPath != null && File.Exists(coldReportPath)) ev.Cold = PerfCompare.FromAutoplayReport(Util.ReadJson(coldReportPath));
        if (logcatPath != null && File.Exists(logcatPath)) ev.Crashes = FindCrashes(File.ReadAllText(logcatPath));
        return ev;
    }

    public static (GateReport Report, JsonObject Summary) Evaluate(Evidence ev, long minSeconds = ReleaseBudgets.SustainedRunSeconds)
    {
        var r = new GateReport("sustained play (" + (ev.Sustained?.DeviceModel is { Length: > 0 } d ? d : "unknown device") + ")");
        long span = 0;
        var epochs = ev.Pss.Select(x => x.Epoch).Concat(ev.Thermal.Select(x => x.Epoch)).ToList();
        if (epochs.Count > 1) span = epochs.Max() - epochs.Min();
        long duration = Math.Max(span, ev.ReportWallSeconds);
        r.Add("duration", duration >= minSeconds ? GateStatus.Pass : GateStatus.Incomplete,
            "run covered " + duration + " s (required " + minSeconds + " s)");

        if (ev.Sustained == null) r.Add("report", GateStatus.Incomplete, "no autoplay report from the sustained scenario");
        else
        {
            if (!ev.Sustained.ScenarioPassed) r.Add("scenario", GateStatus.Fail, "the sustained scenario reported a failed match or replay mismatch");
            if (PerfGate.LooksLikeEmulator(ev.Sustained.DeviceModel) || !string.Equals(ev.Sustained.Platform, "Android", StringComparison.OrdinalIgnoreCase))
                r.Add("device", GateStatus.Incomplete, "not a physical Android phone ('" + ev.Sustained.Platform + "', '" + ev.Sustained.DeviceModel + "')");
            r.Add("frames.p95", ev.Sustained.P95Us <= ReleaseBudgets.P95FrameUs ? GateStatus.Pass : GateStatus.Fail,
                "sustained p95 " + PerfGate.Ms(ev.Sustained.P95Us) + " (budget " + PerfGate.Ms(ReleaseBudgets.P95FrameUs) + "), stalls " + ev.Sustained.StallsOver100Ms);
            if (ev.Cold != null && ev.Cold.P95Us > 0)
                r.Add("degradation", GateStatus.Pass, "p95 cold " + PerfGate.Ms(ev.Cold.P95Us) + " -> sustained " + PerfGate.Ms(ev.Sustained.P95Us) +
                                                      " (x" + (ev.Sustained.P95Us / (double)ev.Cold.P95Us).ToString("0.00", CultureInfo.InvariantCulture) + ", recorded)");
            else r.Add("degradation", GateStatus.Incomplete, "no cold pilot-smoke report to compare thermal degradation against");
        }

        if (ev.Pss.Count == 0) r.Add("memory.pss", GateStatus.Incomplete, "no dumpsys meminfo samples");
        else
        {
            long peak = ev.Pss.Max(x => x.Value);
            r.Add("memory.pss", peak <= ReleaseBudgets.PeakPssBytes ? GateStatus.Pass : GateStatus.Fail,
                "peak PSS " + PerfGate.Mb(peak) + " over " + ev.Pss.Count + " samples (budget " + PerfGate.Mb(ReleaseBudgets.PeakPssBytes) + ")");
            // Skip the first fifth of the run as warm-up (asset loading, first match).
            var settled = ev.Pss.OrderBy(x => x.Epoch).Skip(ev.Pss.Count / 5).ToList();
            double? slope = SlopeBytesPerMinute(settled);
            if (slope == null) r.Add("memory.trend", GateStatus.Incomplete, "too few samples after warm-up for a trend");
            else r.Add("memory.trend", slope.Value <= LeakWarnBytesPerMinute ? GateStatus.Pass : GateStatus.Warn,
                "PSS trend after warm-up " + (slope.Value / 1e6).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " MB/min (proposed leak warning above +1.00)");
        }

        var statuses = ev.Thermal.Where(t => t.Value.Status.HasValue).Select(t => t.Value.Status!.Value).ToList();
        var skins = ev.Thermal.Where(t => t.Value.MaxSkinC.HasValue).Select(t => t.Value.MaxSkinC!.Value).ToList();
        if (statuses.Count == 0 && ev.BatteryC.Count == 0) r.Add("thermal", GateStatus.Incomplete, "no thermal or battery temperature samples");
        else
        {
            int maxStatus = statuses.Count > 0 ? statuses.Max() : -1;
            string temps = (skins.Count > 0 ? "skin max " + skins.Max().ToString("0.0", CultureInfo.InvariantCulture) + " C; " : "") +
                           (ev.BatteryC.Count > 0 ? "battery " + ev.BatteryC.First().Value.ToString("0.0", CultureInfo.InvariantCulture) + " -> max " +
                                                    ev.BatteryC.Max(x => x.Value).ToString("0.0", CultureInfo.InvariantCulture) + " C" : "");
            r.Add("thermal", maxStatus >= ThermalSevere ? GateStatus.Warn : GateStatus.Pass,
                "max thermal status " + (maxStatus < 0 ? "n/a" : maxStatus.ToString(CultureInfo.InvariantCulture)) + "; " + temps.Trim().TrimEnd(';'));
        }

        if (ev.Crashes.Count == 0) r.Add("crashes", GateStatus.Pass, "no crash, ANR, fatal signal or low-memory kill in the logcat");
        foreach (string c in ev.Crashes) r.Add("crashes", GateStatus.Fail, c);

        var summary = new JsonObject
        {
            ["duration_s"] = duration,
            ["pss_samples"] = ev.Pss.Count,
            ["peak_pss_bytes"] = ev.Pss.Count > 0 ? ev.Pss.Max(x => x.Value) : null,
            ["thermal_max_status"] = statuses.Count > 0 ? statuses.Max() : null,
            ["max_skin_c"] = skins.Count > 0 ? skins.Max() : null,
            ["crashes"] = ev.Crashes.Count,
            ["p95_cold_us"] = ev.Cold?.P95Us,
            ["p95_sustained_us"] = ev.Sustained?.P95Us,
        };
        return (r, summary);
    }

    public static int Cli(CliArgs a)
    {
        Evidence ev = Load(a.Get("samples"), a.Get("report"), a.Get("cold-report"), a.Get("logcat"));
        (GateReport r, JsonObject summary) = Evaluate(ev, a.Long("min-seconds") ?? ReleaseBudgets.SustainedRunSeconds);
        return GateJson.Emit(r, a.Get("out"), new JsonObject { ["sustained"] = summary });
    }
}
