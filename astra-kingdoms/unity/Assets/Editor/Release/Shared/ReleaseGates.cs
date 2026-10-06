using System;
using System.Collections.Generic;
using System.Globalization;

// Engine-independent release-gate logic shared by the Unity editor (Editor/Release) and the
// AstraKingdoms.Release .NET tool, which compiles these files by link. No UnityEngine references,
// C# 9, netstandard2.1, so the same source builds in both places.
namespace AstraKingdoms.Release.Gates
{
    /// <summary>
    /// The plan's proposed acceptance budgets ("Physical device quality and release gates"). They are
    /// proposals to validate during the pilot, not achieved benchmarks; any revision needs an explicit
    /// scope or quality decision, so they live in one place.
    /// </summary>
    public static class ReleaseBudgets
    {
        public const int TargetFps = 30;
        /// <summary>Mean frame time that corresponds to the 30 fps target (1/30 s, rounded up).</summary>
        public const long TargetMeanFrameUs = 33334;
        /// <summary>Provisional steady-state p95 frame time: at most 35 ms.</summary>
        public const long P95FrameUs = 35000;
        /// <summary>Long-stall threshold used by the autoplay report (frames over 100 ms).</summary>
        public const long LongStallUs = 100000;
        /// <summary>
        /// Peak Android total process PSS: at most 400 MB. Decimal megabytes (400,000,000 bytes) are
        /// used because they are the stricter reading; dumpsys reports kB (KiB).
        /// </summary>
        public const long PeakPssBytes = 400L * 1000 * 1000;
        /// <summary>Defined device-specific compressed initial download: at most 80 MB (decimal, stricter).</summary>
        public const long InitialDownloadBytes = 80L * 1000 * 1000;
        /// <summary>Sustained-play run length: 20 minutes.</summary>
        public const long SustainedRunSeconds = 20 * 60;
    }

    /// <summary>Result of one check. INCOMPLETE never counts as a pass (plan: incomplete evidence is not a pass).</summary>
    public enum GateStatus
    {
        Pass = 0,
        Warn = 1,
        Incomplete = 2,
        Fail = 3,
    }

    public sealed class GateFinding
    {
        public string Id { get; }
        public GateStatus Status { get; }
        public string Message { get; }

        public GateFinding(string id, GateStatus status, string message)
        {
            Id = id;
            Status = status;
            Message = message;
        }

        public override string ToString() => GateReport.Label(Status) + " " + Id + ": " + Message;
    }

    /// <summary>An ordered list of findings with one overall status (worst finding wins).</summary>
    public sealed class GateReport
    {
        private readonly List<GateFinding> _findings = new List<GateFinding>();

        public string Gate { get; }
        public IReadOnlyList<GateFinding> Findings => _findings;

        public GateReport(string gate) => Gate = gate;

        public GateReport Add(string id, GateStatus status, string message)
        {
            _findings.Add(new GateFinding(id, status, message));
            return this;
        }

        public GateStatus Overall
        {
            get
            {
                GateStatus worst = GateStatus.Pass;
                foreach (GateFinding f in _findings)
                    if (f.Status > worst) worst = f.Status;
                return _findings.Count == 0 ? GateStatus.Incomplete : worst;
            }
        }

        /// <summary>PASS, WARN, INCOMPLETE or FAIL: the words Forge's evidence records use.</summary>
        public static string Label(GateStatus s)
        {
            switch (s)
            {
                case GateStatus.Pass: return "PASS";
                case GateStatus.Warn: return "WARN";
                case GateStatus.Incomplete: return "INCOMPLETE";
                default: return "FAIL";
            }
        }

        /// <summary>Process exit code: 0 pass/warn, 1 fail, 3 incomplete (matches Forge's BLOCKED convention).</summary>
        public int ExitCode => Overall == GateStatus.Fail ? 1 : Overall == GateStatus.Incomplete ? 3 : 0;

        public string ToText()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Gate).Append(": ").Append(Label(Overall)).Append('\n');
            foreach (GateFinding f in _findings) sb.Append("  ").Append(f).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>Frame pacing and memory of one scenario run (from the AK-AUTOPLAY-REPORT/1 frames block plus dumpsys PSS).</summary>
    public sealed class PerfMeasurement
    {
        public string Scenario = "";
        public string DeviceModel = "";
        public string Platform = "";
        public string AppVersion = "";
        public long Samples;
        public long MeanUs;
        public long P50Us;
        public long P95Us;
        public long P99Us;
        public long MaxUs;
        public long StallsOver100Ms;
        /// <summary>Peak Android total PSS in bytes from dumpsys meminfo; null when not captured (Profiler counters are not PSS).</summary>
        public long? PeakPssBytes;
        /// <summary>True when video capture ran during the measurement (plan: separate capture condition).</summary>
        public bool VideoCaptureActive;
        public bool ScenarioPassed = true;
    }

    /// <summary>Allowed regression against a stored baseline before the change counts as a regression.</summary>
    public sealed class PerfTolerance
    {
        public double P95Fraction = 0.10;
        public double P99Fraction = 0.15;
        public long ExtraStalls = 2;
        public double PssFraction = 0.05;
    }

    public static class PerfGate
    {
        /// <summary>Model strings that identify an Android emulator (plan: emulators never authorise a release).</summary>
        private static readonly string[] EmulatorMarkers = { "sdk_gphone", "emulator", "android sdk built for", "generic_x86", "goldfish", "ranchu" };

        public static bool LooksLikeEmulator(string deviceModel)
        {
            if (string.IsNullOrEmpty(deviceModel)) return false;
            string m = deviceModel.ToLowerInvariant();
            foreach (string marker in EmulatorMarkers)
                if (m.Contains(marker)) return true;
            return false;
        }

        /// <summary>
        /// Checks one run against the absolute budgets and, when given, a baseline for the same
        /// scenario and device. <paramref name="registeredDevices"/> are the reference phones recorded
        /// in the device register; a run on any other device can never pass.
        /// </summary>
        public static GateReport Evaluate(PerfMeasurement m, PerfMeasurement baseline, PerfTolerance tolerance,
            IReadOnlyCollection<string> registeredDevices)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            tolerance = tolerance ?? new PerfTolerance();
            var r = new GateReport("perf " + (m.Scenario ?? "") + " on " + (string.IsNullOrEmpty(m.DeviceModel) ? "unknown device" : m.DeviceModel));

            if (!m.ScenarioPassed) r.Add("scenario", GateStatus.Fail, "the automation scenario itself failed (match error or replay mismatch)");
            if (!string.Equals(m.Platform, "Android", StringComparison.OrdinalIgnoreCase))
                r.Add("device.platform", GateStatus.Incomplete, "platform '" + m.Platform + "' is not a physical Android run; editor/desktop numbers do not establish phone performance");
            else if (LooksLikeEmulator(m.DeviceModel))
                r.Add("device.emulator", GateStatus.Incomplete, "'" + m.DeviceModel + "' looks like an emulator; Unity does not support Android emulators and they cannot authorise a release");
            else if (registeredDevices == null || !Contains(registeredDevices, m.DeviceModel))
                r.Add("device.register", GateStatus.Incomplete, "'" + m.DeviceModel + "' is not a registered reference phone (release/perf/devices.json)");
            else
                r.Add("device.register", GateStatus.Pass, "registered reference phone");

            if (m.VideoCaptureActive) r.Add("capture", GateStatus.Incomplete, "video capture was active; measure frame pacing in a separate capture condition");
            if (m.Samples <= 0)
            {
                r.Add("frames", GateStatus.Incomplete, "no frame samples");
                return r;
            }

            r.Add("frames.mean", m.MeanUs <= ReleaseBudgets.TargetMeanFrameUs ? GateStatus.Pass : GateStatus.Fail,
                "mean " + Ms(m.MeanUs) + " (30 fps target = " + Ms(ReleaseBudgets.TargetMeanFrameUs) + ")");
            r.Add("frames.p95", m.P95Us <= ReleaseBudgets.P95FrameUs ? GateStatus.Pass : GateStatus.Fail,
                "p95 " + Ms(m.P95Us) + " (budget " + Ms(ReleaseBudgets.P95FrameUs) + ")");
            // p99 and long stalls are recorded separately (plan); they fail only as regressions.
            r.Add("frames.p99", GateStatus.Pass, "p99 " + Ms(m.P99Us) + ", max " + Ms(m.MaxUs) + " (recorded)");
            r.Add("frames.stalls", GateStatus.Pass, m.StallsOver100Ms + " frame(s) over 100 ms (recorded)");

            if (!m.PeakPssBytes.HasValue)
                r.Add("memory.pss", GateStatus.Incomplete, "peak total PSS not captured (dumpsys meminfo); Profiler counters cannot replace it");
            else
                r.Add("memory.pss", m.PeakPssBytes.Value <= ReleaseBudgets.PeakPssBytes ? GateStatus.Pass : GateStatus.Fail,
                    "peak PSS " + Mb(m.PeakPssBytes.Value) + " (budget " + Mb(ReleaseBudgets.PeakPssBytes) + ")");

            if (baseline == null)
            {
                r.Add("baseline", GateStatus.Incomplete, "no stored baseline for this scenario and device; regression not checked");
                return r;
            }
            if (!string.Equals(baseline.Scenario, m.Scenario, StringComparison.Ordinal) ||
                !string.Equals(baseline.DeviceModel, m.DeviceModel, StringComparison.Ordinal))
            {
                r.Add("baseline", GateStatus.Incomplete, "baseline is for " + baseline.Scenario + " on " + baseline.DeviceModel + "; not comparable");
                return r;
            }
            Regress(r, "regression.p95", m.P95Us, baseline.P95Us, tolerance.P95Fraction, Ms);
            Regress(r, "regression.p99", m.P99Us, baseline.P99Us, tolerance.P99Fraction, Ms);
            long allowedStalls = baseline.StallsOver100Ms + tolerance.ExtraStalls;
            r.Add("regression.stalls", m.StallsOver100Ms <= allowedStalls ? GateStatus.Pass : GateStatus.Fail,
                m.StallsOver100Ms + " stalls vs baseline " + baseline.StallsOver100Ms + " (allowed " + allowedStalls + ")");
            if (m.PeakPssBytes.HasValue && baseline.PeakPssBytes.HasValue)
                Regress(r, "regression.pss", m.PeakPssBytes.Value, baseline.PeakPssBytes.Value, tolerance.PssFraction, Mb);
            return r;
        }

        private static void Regress(GateReport r, string id, long now, long was, double fraction, Func<long, string> fmt)
        {
            long allowed = (long)Math.Floor(was * (1.0 + fraction));
            r.Add(id, now <= allowed ? GateStatus.Pass : GateStatus.Fail,
                fmt(now) + " vs baseline " + fmt(was) + " (allowed " + fmt(allowed) + ", +" + Math.Round(fraction * 100) + "%)");
        }

        private static bool Contains(IReadOnlyCollection<string> list, string s)
        {
            foreach (string x in list)
                if (string.Equals(x, s, StringComparison.Ordinal)) return true;
            return false;
        }

        public static string Ms(long us) => (us / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " ms";

        public static string Mb(long bytes) => (bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>
    /// The download/size record (plan: "Record AAB, installed size and optional downloaded content
    /// separately"). Null means "not measured", which is never treated as zero.
    /// </summary>
    public sealed class SizeRecord
    {
        public long? AabBytes;
        public long? ApkBytes;
        /// <summary>Device-specific compressed initial download (bundletool get-size total, MAX for the reference device spec).</summary>
        public long? InitialDownloadBytes;
        public string DeviceSpec = "";
        public long? InstalledBytes;
        public long? OnDemandBytes;
    }

    public static class SizeGate
    {
        public static GateReport Evaluate(SizeRecord s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var r = new GateReport("download/build size");
            if (!s.InitialDownloadBytes.HasValue)
            {
                string hint = s.ApkBytes.HasValue ? " (APK file is " + PerfGate.Mb(s.ApkBytes.Value) + "; a file size is not the defined device-specific download)" : "";
                r.Add("download", GateStatus.Incomplete, "device-specific compressed initial download not measured; run bundletool get-size total for the reference device spec" + hint);
            }
            else
            {
                r.Add("download", s.InitialDownloadBytes.Value <= ReleaseBudgets.InitialDownloadBytes ? GateStatus.Pass : GateStatus.Fail,
                    "initial download " + PerfGate.Mb(s.InitialDownloadBytes.Value) + " for " + (string.IsNullOrEmpty(s.DeviceSpec) ? "unnamed device spec" : s.DeviceSpec) +
                    " (budget " + PerfGate.Mb(ReleaseBudgets.InitialDownloadBytes) + ")");
                if (string.IsNullOrEmpty(s.DeviceSpec)) r.Add("download.spec", GateStatus.Incomplete, "the device spec used for the download size is not named");
            }
            r.Add("aab", s.AabBytes.HasValue ? GateStatus.Pass : GateStatus.Incomplete,
                s.AabBytes.HasValue ? "AAB " + PerfGate.Mb(s.AabBytes.Value) + " (recorded)" : "AAB size not recorded");
            r.Add("installed", s.InstalledBytes.HasValue ? GateStatus.Pass : GateStatus.Warn,
                s.InstalledBytes.HasValue ? "installed " + PerfGate.Mb(s.InstalledBytes.Value) + " (recorded)" : "installed size not recorded yet (needs the reference phone)");
            r.Add("on_demand", GateStatus.Pass, s.OnDemandBytes.HasValue ? "optional downloaded content " + PerfGate.Mb(s.OnDemandBytes.Value) + " (recorded separately)" : "no optional downloaded content declared");
            return r;
        }
    }
}
