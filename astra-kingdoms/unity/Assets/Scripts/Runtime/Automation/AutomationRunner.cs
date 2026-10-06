#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using UnityEngine;
using UnityEngine.Profiling;

namespace AstraKingdoms.Client.Automation
{
    /// <summary>
    /// Development-only automation/replay interface (ticket 10; plan: "A development-only replay/test
    /// interface drives game actions by semantic identifiers. Remove or disable that interface in
    /// distribution builds."). It plays complete matches with both seats as labelled bots through the
    /// same host, screens and arena as people use, at a chosen clock speed, then for every seed writes
    /// the replay record and verifies it with <see cref="Replayer.Verify"/>. It records frame times
    /// (p50/p95/p99, stalls) and Profiler memory peaks, writes a JSON report to
    /// persistentDataPath/automation, and prints one <c>FORGE_SCENARIO_RESULT: PASS|FAIL</c> line that
    /// the Forge device service reads from logcat. It is not compiled into release builds.
    /// <para>
    /// This drives semantic commands, not touches: separate touch-input scenarios are still needed to
    /// test the UI path (plan: "Add actual touch-input scenarios").
    /// </para>
    /// </summary>
    public sealed class AutomationRunner : MonoBehaviour
    {
        /// <summary>Per-match wall-clock limit in seconds at speed 1 (scaled by the clock speed).</summary>
        public const double MatchTimeoutSeconds = 1500;

        private readonly FrameStats _frames = new FrameStats();
        private readonly List<AutoplayMatchResult> _results = new List<AutoplayMatchResult>();
        private LaunchOptions _options;
        private GameFlow _flow;
        private int _index = -1;
        private LocalMatchHost _host;
        private AutoplayMatchResult _current;
        private float _matchStart;
        private bool _finished;
        private bool _matchDone;
        private int _frameCounter;

        public bool Finished => _finished;
        public IReadOnlyList<AutoplayMatchResult> Results => _results;
        public FrameStats Frames => _frames;
        public string ReportPath { get; private set; }
        public bool Passed { get; private set; }

        public void Run(LaunchOptions options, GameFlow flow)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _flow = flow ?? throw new ArgumentNullException(nameof(flow));
            _flow.MatchFinished += OnMatchFinished;
            Debug.Log("[Automation] Starting: seeds=" + string.Join(",", options.AutoplaySeeds) + " speed=" + options.AutoplaySpeed +
                      " scenario=" + (options.ForgeScenario ?? "-"));
            NextMatch();
        }

        private void NextMatch()
        {
            _index++;
            if (_index >= _options.AutoplaySeeds.Count)
            {
                Finish();
                return;
            }
            ulong seedNumber = _options.AutoplaySeeds[_index];
            MatchFactory.ForAutoplay(seedNumber, out byte[] seed, out string matchId);
            _current = new AutoplayMatchResult { SeedNumber = seedNumber, MatchId = matchId };
            _matchDone = false;
            _matchStart = Time.realtimeSinceStartup;
            Debug.Log("[Automation] " + _flow.Context.TF("auto.running", seedNumber, _index + 1, _options.AutoplaySeeds.Count));
            try
            {
                _host = _flow.StartAutomationMatch(seed, matchId, MatchFactory.DeterministicRequestIds(seedNumber),
                    (float)_options.AutoplaySpeed, BotDifficulty.Normal);
            }
            catch (Exception ex)
            {
                _current.Error = ex.GetType().Name + ": " + ex.Message;
                Debug.LogException(ex);
                CompleteCurrent();
            }
        }

        private void OnMatchFinished(LocalMatchHost host, MatchResult result)
        {
            if (host != _host || _current == null) return;
            _current.Result = result;
            _matchDone = true;
        }

        private void Update()
        {
            if (_finished) return;
            _frames.AddFrame(Time.unscaledDeltaTime * 1000.0);
            if (++_frameCounter % 15 == 0)
                _frames.AddMemory(Profiler.GetTotalAllocatedMemoryLong(), Profiler.GetTotalReservedMemoryLong(),
                    Profiler.GetMonoUsedSizeLong(), Profiler.GetAllocatedMemoryForGraphicsDriver());
            if (_current == null) return;
            if (_matchDone)
            {
                CompleteCurrent();
                return;
            }
            if (Time.realtimeSinceStartup - _matchStart > MatchTimeoutSeconds / Math.Max(1.0, _options.AutoplaySpeed))
            {
                _current.Error = "Timed out in stage " + (_host != null ? _host.Stage.ToString() : "?");
                CompleteCurrent();
            }
        }

        private void CompleteCurrent()
        {
            AutoplayMatchResult r = _current;
            _current = null;
            r.WallSeconds = Time.realtimeSinceStartup - _matchStart;
            if (_host != null && r.Error == null)
            {
                try
                {
                    MatchRecord record = _host.ToRecord();
                    string dir = MatchFlowPaths.AutomationDirectory;
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir, "match-" + r.SeedNumber + ".json");
                    File.WriteAllText(file, record.ToJson());
                    File.WriteAllText(Path.Combine(MatchFlowPaths.AutomationDirectory, "latest-record.json"), record.ToJson());
                    r.RecordFile = file;
                    ReplayReport replay = Replayer.Verify(record);
                    r.ReplayVerified = replay.Success;
                    r.ReplayDetail = replay.ToString();
                }
                catch (Exception ex)
                {
                    r.Error = "Record/replay: " + ex.Message;
                }
            }
            _results.Add(r);
            Debug.Log("[Automation] Seed " + r.SeedNumber + ": " + (r.Passed ? "PASS" : "FAIL") + " " +
                      (r.Result != null ? r.Result.ToString() : string.Empty) + " " + (r.Error ?? r.ReplayDetail));
            NextMatch();
        }

        private void Finish()
        {
            _finished = true;
            JsonNode report = AutomationReport.Build(_options.ForgeScenario ?? "autoplay", Application.version, Application.unityVersion,
                Application.platform.ToString(), SystemInfo.deviceModel, _options.AutoplaySpeed, _results, _frames);
            Passed = report["passed"].AsBool();
            try
            {
                Directory.CreateDirectory(MatchFlowPaths.AutomationDirectory);
                ReportPath = Path.Combine(MatchFlowPaths.AutomationDirectory, "autoplay-report.json");
                File.WriteAllText(ReportPath, report.ToCanonicalString());
            }
            catch (Exception ex)
            {
                Passed = false;
                Debug.LogError("[Automation] Could not write the report: " + ex.Message);
            }
            string summary = "seeds=" + _results.Count + " p95_ms=" + (_frames.Percentile(95)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                             " p99_ms=" + _frames.Percentile(99).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                             " stalls=" + _frames.CountAbove(FrameStats.LongStallMs) +
                             " peak_alloc_mb=" + (_frames.PeakAllocatedBytes / (1024 * 1024)) +
                             " report=" + ReportPath;
            Debug.Log("FORGE_EVIDENCE_DIR: " + MatchFlowPaths.AutomationDirectory);
            Debug.Log("FORGE_SCENARIO_RESULT: " + (Passed ? "PASS" : "FAIL") + " " + summary);
            if (_options.QuitAfterAutoplay || !string.IsNullOrEmpty(_options.ForgeScenario))
            {
#if UNITY_EDITOR
                if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(Passed ? 0 : 1);
#else
                Application.Quit(Passed ? 0 : 1);
#endif
            }
            else
            {
                _flow.ShowHome();
            }
        }
    }
}
#endif
