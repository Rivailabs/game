using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Automation
{
    /// <summary>One automated match's outcome.</summary>
    public sealed class AutoplayMatchResult
    {
        public ulong SeedNumber;
        public string MatchId;
        public MatchResult Result;
        public bool ReplayVerified;
        public string ReplayDetail;
        public string RecordFile;
        public double WallSeconds;
        public string Error;

        public bool Passed => Error == null && Result != null && ReplayVerified;
    }

    /// <summary>
    /// Evidence JSON written by the development automation interface for the Forge device service:
    /// build identity, every seed's result and replay verification, frame pacing and memory peaks.
    /// Durations are integer microseconds and sizes integer bytes (canonical JSON has no floats).
    /// </summary>
    public static class AutomationReport
    {
        public const string Format = "AK-AUTOPLAY-REPORT/1";

        public static JsonNode Build(string scenario, string appVersion, string unityVersion, string platform, string deviceModel,
            double speed, IReadOnlyList<AutoplayMatchResult> matches, FrameStats frames)
        {
            var root = JsonNode.Object();
            root.Add("format", Format);
            root.Add("scenario", scenario ?? "autoplay");
            root.Add("rules_version", Rules.Core.RulesConstants.RulesVersion);
            root.Add("rules_hash", RulesBundle.HashHex);
            root.Add("app_version", appVersion ?? string.Empty);
            root.Add("unity_version", unityVersion ?? string.Empty);
            root.Add("platform", platform ?? string.Empty);
            root.Add("device_model", deviceModel ?? string.Empty);
            root.Add("clock_speed_x100", (long)Math.Round(speed * 100));

            var list = JsonNode.Array();
            bool all = matches != null && matches.Count > 0;
            if (matches != null)
            {
                foreach (AutoplayMatchResult m in matches)
                {
                    all &= m.Passed;
                    var n = JsonNode.Object();
                    n.Add("seed", m.SeedNumber);
                    n.Add("match_id", m.MatchId ?? string.Empty);
                    n.Add("passed", m.Passed);
                    n.Add("reason", m.Result == null ? "none" : m.Result.Reason.ToString());
                    n.Add("winner", m.Result == null || !m.Result.Winner.HasValue ? "none" : m.Result.Winner.Value.ToString());
                    n.Add("cells_a", m.Result == null ? 0 : m.Result.CellsA);
                    n.Add("cells_b", m.Result == null ? 0 : m.Result.CellsB);
                    n.Add("rounds", m.Result == null ? 0 : m.Result.RoundsPlayed);
                    n.Add("replay_verified", m.ReplayVerified);
                    n.Add("replay_detail", m.ReplayDetail ?? string.Empty);
                    n.Add("record_file", m.RecordFile ?? string.Empty);
                    n.Add("wall_ms", (long)Math.Round(m.WallSeconds * 1000));
                    n.Add("error", m.Error ?? string.Empty);
                    list.Push(n);
                }
            }
            root.Add("matches", list);
            root.Add("passed", all);

            var f = JsonNode.Object();
            if (frames != null)
            {
                f.Add("samples", frames.Count);
                f.Add("excluded_loading_frames", frames.ExcludedLoadingFrames);
                f.Add("mean_us", Us(frames.Mean));
                f.Add("p50_us", Us(frames.Percentile(50)));
                f.Add("p95_us", Us(frames.Percentile(95)));
                f.Add("p99_us", Us(frames.Percentile(99)));
                f.Add("max_us", Us(frames.Max));
                f.Add("stalls_over_100ms", frames.CountAbove(FrameStats.LongStallMs));
                f.Add("budget_p95_us", 35000);
                f.Add("peak_allocated_bytes", frames.PeakAllocatedBytes);
                f.Add("peak_reserved_bytes", frames.PeakReservedBytes);
                f.Add("peak_managed_bytes", frames.PeakManagedBytes);
                f.Add("peak_graphics_driver_bytes", frames.PeakGraphicsDriverBytes);
                f.Add("note", "Profiler counters only; Android total PSS must be captured by the device service (dumpsys meminfo).");
            }
            root.Add("frames", f);
            return root;
        }

        private static long Us(double ms) => (long)Math.Round(ms * 1000.0);
    }
}
