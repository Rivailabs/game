using System;
using System.Collections.Generic;
using System.IO;
using AstraKingdoms.Release.Gates;
using AstraKingdoms.Rules.Replay;
using UnityEditor;
using UnityEngine;

namespace AstraKingdoms.EditorTools.Release
{
    /// <summary>
    /// Ticket 73 (editor side): compares the latest autoplay automation report
    /// (<c>AK-AUTOPLAY-REPORT/1</c>, written by the development automation runner) with the frame and
    /// memory budgets and the stored baseline, using the same <see cref="PerfGate"/> as the
    /// <c>AstraKingdoms.Release perf-compare</c> command. An editor or desktop run is reported as
    /// INCOMPLETE by design: only a registered physical reference phone can pass.
    /// </summary>
    public static class PerfBaselineMenu
    {
        public const string BaselinesPath = "../release/perf/baselines.json";
        public const string DevicesPath = "../release/perf/devices.json";
        public const string ResultPath = "Builds/perf-compare.json";

        [MenuItem("Astra Kingdoms/Release/Compare Latest Autoplay Report With Baseline")]
        public static void CompareLatest()
        {
            string report = Path.Combine(Application.persistentDataPath, "automation", "autoplay-report.json");
            if (!File.Exists(report))
            {
                Debug.LogWarning("[Perf] No autoplay report at " + report + ". Run an autoplay scenario first.");
                return;
            }
            GateReport r = Compare(File.ReadAllText(report), null);
            ReleaseGate.Write(r, ResultPath);
            Debug.Log(r.ToText());
        }

        public static GateReport Compare(string autoplayJson, long? peakPssBytes)
        {
            PerfMeasurement m = FromAutoplayReport(JsonNode.Parse(autoplayJson));
            m.PeakPssBytes = peakPssBytes;
            return PerfGate.Evaluate(m, FindBaseline(m), new PerfTolerance(), RegisteredDevices());
        }

        /// <summary>Maps an AK-AUTOPLAY-REPORT/1 document (see AutomationReport.Build) to a measurement.</summary>
        public static PerfMeasurement FromAutoplayReport(JsonNode root)
        {
            if (root["format"].AsString() != "AK-AUTOPLAY-REPORT/1") throw new FormatException("Not an AK-AUTOPLAY-REPORT/1 document.");
            JsonNode f = root["frames"];
            return new PerfMeasurement
            {
                Scenario = root["scenario"].AsString(),
                DeviceModel = root["device_model"].AsString(),
                Platform = root["platform"].AsString(),
                AppVersion = root["app_version"].AsString(),
                ScenarioPassed = root["passed"].AsBool(),
                Samples = Long(f, "samples"),
                MeanUs = Long(f, "mean_us"),
                P50Us = Long(f, "p50_us"),
                P95Us = Long(f, "p95_us"),
                P99Us = Long(f, "p99_us"),
                MaxUs = Long(f, "max_us"),
                StallsOver100Ms = Long(f, "stalls_over_100ms"),
            };
        }

        private static long Long(JsonNode obj, string key)
        {
            foreach (KeyValuePair<string, JsonNode> kv in obj.Members)
                if (kv.Key == key) return kv.Value.AsLong();
            return 0;
        }

        private static PerfMeasurement FindBaseline(PerfMeasurement m)
        {
            if (!File.Exists(BaselinesPath)) return null;
            JsonNode root = JsonNode.Parse(File.ReadAllText(BaselinesPath));
            foreach (JsonNode e in root["entries"].AsArray())
            {
                if (e["scenario"].AsString() != m.Scenario || e["device_model"].AsString() != m.DeviceModel) continue;
                JsonNode pss = e["peak_pss_bytes"];
                return new PerfMeasurement
                {
                    Scenario = m.Scenario,
                    DeviceModel = m.DeviceModel,
                    P95Us = e["p95_us"].AsLong(),
                    P99Us = e["p99_us"].AsLong(),
                    StallsOver100Ms = e["stalls_over_100ms"].AsLong(),
                    PeakPssBytes = pss.Kind == JsonKind.Null ? (long?)null : pss.AsLong(),
                };
            }
            return null;
        }

        private static List<string> RegisteredDevices()
        {
            var list = new List<string>();
            if (!File.Exists(DevicesPath)) return list;
            foreach (JsonNode d in JsonNode.Parse(File.ReadAllText(DevicesPath))["devices"].AsArray())
                list.Add(d["model"].AsString());
            return list;
        }
    }
}
