extern alias clientcore;
using System.Text.Json.Nodes;
using AstraKingdoms.Release.Gates;
using NUnit.Framework;
using AutomationReport = clientcore::AstraKingdoms.Client.Automation.AutomationReport;
using AutoplayMatchResult = clientcore::AstraKingdoms.Client.Automation.AutoplayMatchResult;
using FrameStats = clientcore::AstraKingdoms.Client.Automation.FrameStats;

namespace AstraKingdoms.Release.Tests;

[TestFixture]
public class PerfTests
{
    private const string Phone = "RefPhone-2GB";
    private static readonly string[] Registered = { Phone };

    private static PerfMeasurement Good() => new()
    {
        Scenario = "frame-pacing", DeviceModel = Phone, Platform = "Android", Samples = 9000,
        MeanUs = 30000, P50Us = 29000, P95Us = 33000, P99Us = 45000, MaxUs = 120000, StallsOver100Ms = 1,
        PeakPssBytes = 350_000_000,
    };

    private static PerfMeasurement Baseline() => new()
    {
        Scenario = "frame-pacing", DeviceModel = Phone, P95Us = 32000, P99Us = 44000, StallsOver100Ms = 1, PeakPssBytes = 345_000_000,
    };

    [Test]
    public void BudgetsAreThePlansProposedValues()
    {
        Assert.That(ReleaseBudgets.TargetFps, Is.EqualTo(30));
        Assert.That(ReleaseBudgets.P95FrameUs, Is.EqualTo(35_000));
        Assert.That(ReleaseBudgets.PeakPssBytes, Is.EqualTo(400_000_000));
        Assert.That(ReleaseBudgets.InitialDownloadBytes, Is.EqualTo(80_000_000));
        Assert.That(ReleaseBudgets.SustainedRunSeconds, Is.EqualTo(1200));
    }

    [Test]
    public void RegisteredPhoneWithinBudgetsAndBaselinePasses()
    {
        GateReport r = PerfGate.Evaluate(Good(), Baseline(), new PerfTolerance(), Registered);
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Pass), r.ToText());
        Assert.That(r.ExitCode, Is.EqualTo(0));
    }

    [Test]
    public void WithoutBaselineTheRunIsIncompleteNotPass()
    {
        GateReport r = PerfGate.Evaluate(Good(), null, null, Registered);
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Incomplete));
        Assert.That(r.ExitCode, Is.EqualTo(3));
    }

    [TestCase("sdk_gphone64_arm64")]
    [TestCase("Android SDK built for x86")]
    public void EmulatorNeverPasses(string model)
    {
        PerfMeasurement m = Good();
        m.DeviceModel = model;
        GateReport r = PerfGate.Evaluate(m, Baseline(), null, new[] { model });
        Assert.That(r.Has("device.emulator", GateStatus.Incomplete), r.ToText());
        Assert.That(r.Overall, Is.Not.EqualTo(GateStatus.Pass));
    }

    [Test]
    public void EditorAndUnregisteredDevicesAreIncomplete()
    {
        PerfMeasurement editor = Good();
        editor.Platform = "LinuxEditor";
        Assert.That(PerfGate.Evaluate(editor, Baseline(), null, Registered).Has("device.platform", GateStatus.Incomplete));
        Assert.That(PerfGate.Evaluate(Good(), Baseline(), null, Array.Empty<string>()).Has("device.register", GateStatus.Incomplete));
    }

    [Test]
    public void P95AndMeanOverBudgetFail()
    {
        PerfMeasurement m = Good();
        m.P95Us = 35_001;
        Assert.That(PerfGate.Evaluate(m, null, null, Registered).Has("frames.p95", GateStatus.Fail));
        m = Good();
        m.MeanUs = 34_000;
        Assert.That(PerfGate.Evaluate(m, null, null, Registered).Has("frames.mean", GateStatus.Fail));
    }

    [Test]
    public void PssMissingIsIncompleteAndOverBudgetFails()
    {
        PerfMeasurement m = Good();
        m.PeakPssBytes = null;
        Assert.That(PerfGate.Evaluate(m, Baseline(), null, Registered).Has("memory.pss", GateStatus.Incomplete));
        m.PeakPssBytes = 400_000_001;
        Assert.That(PerfGate.Evaluate(m, Baseline(), null, Registered).Has("memory.pss", GateStatus.Fail));
    }

    [Test]
    public void RegressionAgainstBaselineFails()
    {
        PerfMeasurement m = Good();
        m.P95Us = 34_000; // within budget and within +10 % of the 32,000 baseline (35,200)
        Assert.That(PerfGate.Evaluate(m, Baseline(), null, Registered).Has("regression.p95", GateStatus.Pass));
        m.P99Us = 52_000; // > 44,000 * 1.15 = 50,600
        m.StallsOver100Ms = 4; // > 1 + 2
        m.PeakPssBytes = 370_000_000; // > 345 MB * 1.05 = 362.25 MB
        GateReport r = PerfGate.Evaluate(m, Baseline(), null, Registered);
        Assert.That(r.Has("regression.p99", GateStatus.Fail));
        Assert.That(r.Has("regression.stalls", GateStatus.Fail));
        Assert.That(r.Has("regression.pss", GateStatus.Fail));
    }

    [Test]
    public void BaselineForAnotherDeviceIsNotCompared()
    {
        PerfMeasurement b = Baseline();
        b.DeviceModel = "Other";
        Assert.That(PerfGate.Evaluate(Good(), b, null, Registered).Has("baseline", GateStatus.Incomplete));
    }

    [Test]
    public void VideoCaptureMakesTheMeasurementIncomplete()
    {
        PerfMeasurement m = Good();
        m.VideoCaptureActive = true;
        Assert.That(PerfGate.Evaluate(m, Baseline(), null, Registered).Has("capture", GateStatus.Incomplete));
    }

    [Test]
    public void ReadsTheRealClientAutomationReport()
    {
        var frames = new FrameStats();
        for (int i = 0; i < 1000; i++) frames.AddFrame(i % 100 == 0 ? 120.0 : 30.0);
        var match = new AutoplayMatchResult { SeedNumber = 1, MatchId = "m1", ReplayVerified = true };
        string json = AutomationReport.Build("pilot-smoke", "0.1.0", "6000.0.23f1", "Android", "Pixel 3a", 8.0,
            new[] { match }, frames).ToCanonicalString();
        PerfMeasurement m = PerfCompare.FromAutoplayReport(JsonNode.Parse(json));
        Assert.Multiple(() =>
        {
            Assert.That(m.Scenario, Is.EqualTo("pilot-smoke"));
            Assert.That(m.DeviceModel, Is.EqualTo("Pixel 3a"));
            Assert.That(m.Platform, Is.EqualTo("Android"));
            Assert.That(m.Samples, Is.EqualTo(frames.Count));
            Assert.That(m.P95Us, Is.EqualTo((long)Math.Round(frames.Percentile(95) * 1000)));
            Assert.That(m.StallsOver100Ms, Is.EqualTo(frames.CountAbove(FrameStats.LongStallMs)));
            // A match without a MatchResult is not a passed match: the scenario failed.
            Assert.That(m.ScenarioPassed, Is.False);
        });
    }

    [Test]
    public void ParsesDumpsysMeminfoInBothLayouts()
    {
        const string modern = "App Summary\n   Java Heap:  12000\n           TOTAL PSS:   312456            TOTAL RSS:   400000       TOTAL SWAP PSS:       10\n";
        const string legacy = "  Native Heap  2000\n        TOTAL   298765   250000    1200\n";
        Assert.That(PerfCompare.ParseMeminfoPssBytes(modern), Is.EqualTo(312456L * 1024));
        Assert.That(PerfCompare.ParseMeminfoPssBytes(legacy), Is.EqualTo(298765L * 1024));
        Assert.That(PerfCompare.ParseMeminfoPssBytes("No process found for: x"), Is.Null);
    }

    [Test]
    public void CliComparesUpdatesBaselineOnlyWithApproverAndScenarioOverride()
    {
        using var t = new TempDir();
        var report = new JsonObject
        {
            ["format"] = "AK-AUTOPLAY-REPORT/1", ["scenario"] = "autoplay", ["device_model"] = Phone, ["platform"] = "Android",
            ["app_version"] = "0.1.0", ["passed"] = true,
            ["frames"] = new JsonObject { ["samples"] = 5000, ["mean_us"] = 30000, ["p50_us"] = 29000, ["p95_us"] = 33000, ["p99_us"] = 40000, ["max_us"] = 90000, ["stalls_over_100ms"] = 0 },
        };
        string reportPath = t.File("autoplay-report.json", report.ToJsonString());
        t.File("samples/meminfo-100.txt", "TOTAL PSS:   300000 TOTAL RSS: 1");
        t.File("samples/meminfo-130.txt", "TOTAL PSS:   310000 TOTAL RSS: 1");
        string devices = t.File("devices.json", "{\"format\":\"AK-DEVICE-REGISTER/1\",\"devices\":[{\"model\":\"" + Phone + "\"}]}");
        string baseline = t.File("baselines.json", "{\"format\":\"AK-PERF-BASELINE/1\",\"entries\":[]}");

        (int code, string output) = Cli.Run("perf-compare", "--report", reportPath, "--scenario", "frame-pacing", "--meminfo-dir", Path.Combine(t.Path, "samples"),
            "--devices", devices, "--baseline", baseline, "--update-baseline", "--out", Path.Combine(t.Path, "out.json"));
        Assert.That(code, Is.Not.EqualTo(0), output);
        Assert.That(output, Does.Contain("baseline NOT updated")); // no approver: refused
        Assert.That(File.ReadAllText(baseline), Does.Not.Contain("frame-pacing"));

        (code, output) = Cli.Run("perf-compare", "--report", reportPath, "--scenario", "frame-pacing", "--meminfo-dir", Path.Combine(t.Path, "samples"),
            "--devices", devices, "--baseline", baseline, "--update-baseline", "--approved-by", "owner", "--out", Path.Combine(t.Path, "out.json"));
        Assert.That(code, Is.EqualTo(3), output); // first run: baseline missing -> INCOMPLETE, but recorded
        JsonNode stored = JsonNode.Parse(File.ReadAllText(baseline))!["entries"]![0]!;
        Assert.That(stored["scenario"]!.GetValue<string>(), Is.EqualTo("frame-pacing"));
        Assert.That(stored["peak_pss_bytes"]!.GetValue<long>(), Is.EqualTo(310000L * 1024));
        Assert.That(stored["approved_by"]!.GetValue<string>(), Is.EqualTo("owner"));

        (code, output) = Cli.Run("perf-compare", "--report", reportPath, "--scenario", "frame-pacing", "--meminfo-dir", Path.Combine(t.Path, "samples"),
            "--devices", devices, "--baseline", baseline, "--out", Path.Combine(t.Path, "out.json"));
        Assert.That(code, Is.EqualTo(0), output);
        JsonNode result = JsonNode.Parse(File.ReadAllText(Path.Combine(t.Path, "out.json")))!;
        Assert.That(result["format"]!.GetValue<string>(), Is.EqualTo("AK-GATE-RESULT/1"));
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("PASS"));
    }

    [Test]
    public void CommittedRegisterAndBaselineFilesAreValidAndEmpty()
    {
        Assert.That(PerfCompare.LoadDevices(Repo.P("release", "perf", "devices.json")), Is.Empty);
        JsonNode b = Util.ReadJson(Repo.P("release", "perf", "baselines.json"));
        Assert.That(PerfCompare.FindBaseline(b, "frame-pacing", "x"), Is.Null);
    }
}
