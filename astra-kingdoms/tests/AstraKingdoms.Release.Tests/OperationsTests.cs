using System.Text.Json.Nodes;
using AstraKingdoms.Release.Gates;
using NUnit.Framework;

namespace AstraKingdoms.Release.Tests;

[TestFixture]
public class DataSafetyTests
{
    private static JsonNode DataMap => Util.ReadJson(Repo.P("release", "declarations", "privacy-data-map.json"));
    private static JsonNode Sdks => Util.ReadJson(Repo.P("release", "declarations", "sdk-inventory.json"));

    [Test]
    public void CommittedDeclarationsAreConsistentButIncomplete()
    {
        (GateReport r, string md) = DataSafety.Build(DataSafety.LoadRows(DataMap), DataSafety.LoadSdks(Sdks), "test");
        Assert.That(r.With(GateStatus.Fail), Is.Empty, r.ToText());
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Incomplete));
        Assert.That(md, Does.Contain("| Financial info | Purchase history | Yes | No | Optional |"));
        Assert.That(md, Does.Contain("| Location | Precise location | No |"));
        Assert.That(md, Does.Contain("**TBD (vendor)**"));
    }

    private static (List<DataSafety.Row>, List<DataSafety.Sdk>) Complete() =>
    (
        new List<DataSafety.Row>
        {
            new() { Id = "acct", PlayType = "Personal info/User IDs", Collected = true, Status = "server", Purposes = { "App functionality" } },
            new() { Id = "crash", PlayType = "App info and performance/Crash logs", Collected = true, Shared = "yes", Optional = "optional", Status = "implemented", Purposes = { "Analytics" } },
        },
        new List<DataSafety.Sdk>
        {
            new() { Id = "server", Name = "Server", Status = "integrated", TrafficCapture = "cap1.har", DataTypes = { "Personal info/User IDs" }, DataMapRows = { "acct" } },
            new() { Id = "crash", Name = "Crash", Status = "integrated", TrafficCapture = "cap2.har", DataTypes = { "App info and performance/Crash logs" }, DataMapRows = { "crash" } },
        }
    );

    [Test]
    public void IntegratedCapturedAndDeclaredPasses()
    {
        (var rows, var sdks) = Complete();
        (GateReport r, string md) = DataSafety.Build(rows, sdks, "rc1");
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Pass), r.ToText());
        Assert.That(md, Does.Contain("| App info and performance | Crash logs | Yes | Yes | Optional | Analytics | crash |"));
    }

    [Test]
    public void UndeclaredSdkDataAndUnknownTypesFail()
    {
        (var rows, var sdks) = Complete();
        sdks[1].DataTypes.Add("Device or other IDs/Device or other IDs");
        sdks[0].DataTypes.Add("Personal info/Shoe size");
        rows.Add(new DataSafety.Row { Id = "bad", PlayType = "Made up/Thing", Collected = true });
        GateReport r = DataSafety.Build(rows, sdks, "").Report;
        Assert.That(r.With(GateStatus.Fail).Select(f => f.Message), Has.Some.Contains("no collected data-map row declares it"));
        Assert.That(r.With(GateStatus.Fail).Select(f => f.Message), Has.Some.Contains("'Personal info/Shoe size' is not a Play Data safety type"));
        Assert.That(r.Has("data-map", GateStatus.Fail));
    }

    [Test]
    public void MissingTrafficCaptureIsIncomplete()
    {
        (var rows, var sdks) = Complete();
        sdks[0].TrafficCapture = null;
        Assert.That(DataSafety.Build(rows, sdks, "").Report.Has("sdk.server", GateStatus.Incomplete));
    }

    [Test]
    public void CommittedDraftMatchesTheGenerator()
    {
        // release/declarations/data-safety-draft.md is generated; regenerate after editing the inputs.
        string md = DataSafety.Build(DataSafety.LoadRows(DataMap), DataSafety.LoadSdks(Sdks), "").Markdown;
        Assert.That(File.ReadAllText(Repo.P("release", "declarations", "data-safety-draft.md")), Is.EqualTo(md));
    }
}

[TestFixture]
public class SustainedTests
{
    private static string Report(long p95, bool passed = true, string model = "RefPhone", long wallMsPerMatch = 80_000, int matches = 16) =>
        new JsonObject
        {
            ["format"] = "AK-AUTOPLAY-REPORT/1", ["scenario"] = "autoplay", ["device_model"] = model, ["platform"] = "Android", ["passed"] = passed,
            ["matches"] = new JsonArray(Enumerable.Range(0, matches).Select(_ => (JsonNode)new JsonObject { ["wall_ms"] = wallMsPerMatch }).ToArray()),
            ["frames"] = new JsonObject { ["samples"] = 1000, ["mean_us"] = 30000, ["p95_us"] = p95, ["p99_us"] = 40000, ["stalls_over_100ms"] = 0 },
        }.ToJsonString();

    private static string Thermal(int status, double skin) =>
        "Thermal Status: " + status + "\nCurrent temperatures from HAL:\n\tTemperature{mValue=45.1, mType=0, mName=CPU0, mStatus=0}\n" +
        "\tTemperature{mValue=" + skin.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", mType=3, mName=skin, mStatus=0}\n";

    private static void Samples(TempDir t, int count, Func<int, long> pssKb, int thermalStatus = 1)
    {
        for (int i = 0; i < count; i++)
        {
            long epoch = 1_000_000 + i * 30;
            t.File("s/meminfo-" + epoch + ".txt", "TOTAL PSS:   " + pssKb(i) + " TOTAL RSS: 1");
            t.File("s/thermal-" + epoch + ".txt", Thermal(i == count - 1 ? thermalStatus : 0, 33.0 + i * 0.1));
            t.File("s/battery-" + epoch + ".txt", "Current Battery Service state:\n  level: 80\n  temperature: " + (300 + i) + "\n");
        }
    }

    [Test]
    public void ParsesThermalAndBattery()
    {
        SustainedCollate.Thermal th = SustainedCollate.ParseThermal(Thermal(2, 41.5));
        Assert.That(th.Status, Is.EqualTo(2));
        Assert.That(th.MaxSkinC, Is.EqualTo(41.5));
        Assert.That(SustainedCollate.ParseBattery("  temperature: 345\n"), Is.EqualTo(34.5));
    }

    [Test]
    public void SlopeIsInBytesPerMinute()
    {
        var s = Enumerable.Range(0, 5).Select(i => new SustainedCollate.Sample<long> { Epoch = i * 60, Value = 100 + i * 2_000_000L }).ToList();
        Assert.That(SustainedCollate.SlopeBytesPerMinute(s), Is.EqualTo(2_000_000).Within(1e-6));
    }

    [Test]
    public void TwentyMinuteCleanRunPasses()
    {
        using var t = new TempDir();
        Samples(t, 42, _ => 300_000);
        string rep = t.File("r.json", Report(30000)), cold = t.File("c.json", Report(28000));
        string log = t.File("logcat.txt", "I Unity: FORGE_SCENARIO_RESULT: PASS seeds=16\n");
        var ev = SustainedCollate.Load(Path.Combine(t.Path, "s"), rep, cold, log);
        (GateReport r, JsonObject summary) = SustainedCollate.Evaluate(ev);
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Pass), r.ToText());
        Assert.That(summary["duration_s"]!.GetValue<long>(), Is.EqualTo(1280));
        Assert.That(r.Findings.First(f => f.Id == "degradation").Message, Does.Contain("x1.07"));
    }

    [Test]
    public void LeakTrendWarnsCrashesFailShortRunsAreIncomplete()
    {
        using var t = new TempDir();
        Samples(t, 12, i => 250_000 + i * 2_000, thermalStatus: 3); // ~4 MB/min growth, severe throttling at the end
        string rep = t.File("r.json", Report(30000, wallMsPerMatch: 20_000));
        string log = t.File("logcat.txt", "E AndroidRuntime: FATAL EXCEPTION: main\nE ActivityManager: ANR in com.rivailabs.astrakingdoms\n");
        (GateReport r, _) = SustainedCollate.Evaluate(SustainedCollate.Load(Path.Combine(t.Path, "s"), rep, null, log));
        Assert.That(r.Has("duration", GateStatus.Incomplete));
        Assert.That(r.Has("memory.trend", GateStatus.Warn));
        Assert.That(r.Has("thermal", GateStatus.Warn));
        Assert.That(r.With(GateStatus.Fail).Count(f => f.Id == "crashes"), Is.EqualTo(2));
        Assert.That(r.Has("degradation", GateStatus.Incomplete));
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Fail));
    }

    [Test]
    public void PeakPssOverBudgetFailsAndMissingSamplesAreIncomplete()
    {
        using var t = new TempDir();
        Samples(t, 42, i => i == 20 ? 400_000 : 300_000); // 400,000 KiB = 409.6 MB
        (GateReport r, _) = SustainedCollate.Evaluate(SustainedCollate.Load(Path.Combine(t.Path, "s"), t.File("r.json", Report(30000)), null, null));
        Assert.That(r.Has("memory.pss", GateStatus.Fail));
        (GateReport empty, _) = SustainedCollate.Evaluate(SustainedCollate.Load(null, null, null, null));
        Assert.That(empty.Has("memory.pss", GateStatus.Incomplete) && empty.Has("report", GateStatus.Incomplete) && empty.Has("thermal", GateStatus.Incomplete));
    }

    [Test]
    public void SustainedScenarioConfigCoversTwentyMinutesWithKnownExtras()
    {
        JsonNode doc = Util.ReadJson(Repo.P("release", "scenarios", "scenarios.json"));
        var known = new[] { "forge_scenario", "autoplay", "autoplaySpeed", "replay", "quitAfterAutoplay" }; // LaunchArgs.Extras
        foreach (JsonNode s in doc["scenarios"]!.AsArray())
            foreach (KeyValuePair<string, JsonNode> kv in s!["extras"]!.AsObject())
                Assert.That(known, Does.Contain(kv.Key), Util.Str(s, "id"));
        JsonNode sustained = doc["scenarios"]!.AsArray().Single(s => Util.Str(s, "id") == "sustained-20m")!;
        int seeds = Util.Str(sustained["extras"], "autoplay").Split(',').Length;
        Assert.That(seeds * 80, Is.GreaterThanOrEqualTo(ReleaseBudgets.SustainedRunSeconds), "about 80 s per bot match at clock speed 1");
        Assert.That(Util.Str(sustained["extras"], "autoplaySpeed"), Is.EqualTo("1"));
    }
}

[TestFixture]
public class ClosedTestTests
{
    private static string Csv(IEnumerable<string> rows)
    {
        string header = "tester_id,cohort,opted_out_utc," + string.Join(",", Enumerable.Range(1, 14).Select(d => "day" + d.ToString("00")));
        return header + "\n" + string.Join("\n", rows) + "\n";
    }

    private static string Row(string id, string cohort, int days, string optOut = "") =>
        id + "," + cohort + "," + optOut + "," + string.Join(",", Enumerable.Range(1, 14).Select(d => d <= days ? "Y" : ""));

    [Test]
    public void TwelveTestersForFourteenContinuousDaysPass()
    {
        var rows = Enumerable.Range(1, 12).Select(i => Row("T" + i, "recruited", 14)).Append(Row("T13", "friend", 9)).ToList();
        GateReport r = ClosedTestCheck.Evaluate(ClosedTestCheck.Parse(Csv(rows)));
        Assert.That(r.Has("testers", GateStatus.Pass), r.ToText());
        Assert.That(r.Has("tester.T13", GateStatus.Warn));
    }

    [Test]
    public void ElevenTestersOrAnOptOutAreNotEnough()
    {
        var rows = Enumerable.Range(1, 11).Select(i => Row("T" + i, "community", 14)).Append(Row("T12", "colleague", 14, "2026-11-02")).ToList();
        GateReport r = ClosedTestCheck.Evaluate(ClosedTestCheck.Parse(Csv(rows)));
        Assert.That(r.Has("testers", GateStatus.Incomplete), r.ToText());
        Assert.That(r.Findings.Single(f => f.Id == "tester.T12").Message, Does.Contain("opted out"));
    }

    [Test]
    public void CohortMustBeIdentifiable()
    {
        GateReport r = ClosedTestCheck.Evaluate(ClosedTestCheck.Parse(Csv(new[] { Row("T1", "", 3) })));
        Assert.That(r.Has("cohort.T1", GateStatus.Fail));
    }

    [Test]
    public void CommittedTrackerHasTwelveRowsAndFourteenDays()
    {
        string csv = File.ReadAllText(Repo.P("release", "closed-test", "tester-tracker.csv"));
        List<ClosedTestCheck.Tester> testers = ClosedTestCheck.Parse(csv);
        Assert.That(testers, Has.Count.EqualTo(ClosedTestCheck.RequiredTesters));
        Assert.That(ClosedTestCheck.Evaluate(testers).Has("testers", GateStatus.Incomplete));
    }

    [Test]
    public void QuotedCsvCellsAreParsed() =>
        Assert.That(ClosedTestCheck.SplitCsvLine("a,\"b, \"\"c\"\"\",d"), Is.EqualTo(new[] { "a", "b, \"c\"", "d" }));
}

[TestFixture]
public class VersionPolicyTests
{
    private static VersionPolicyCheck.Policy Policy(long min = 1, long rec = 1, long latest = 1, int leadMinutes = 0) => new()
    {
        Changed = DateTimeOffset.Parse("2026-10-06T00:00:00Z"),
        EnforceAfter = DateTimeOffset.Parse("2026-10-06T00:00:00Z").AddMinutes(leadMinutes),
        Minimum = min, Recommended = rec, Latest = latest, GraceMinutes = 60, RulesVersions = { "AK-TR-1" }, CompatibilityDays = 30,
    };

    [Test]
    public void CommittedPolicyIsValid()
    {
        VersionPolicyCheck.Policy p = VersionPolicyCheck.Parse(Util.ReadJson(Repo.P("release", "operations", "minimum-version-policy.json")));
        Assert.That(VersionPolicyCheck.Evaluate(p, null).Overall, Is.EqualTo(GateStatus.Pass));
    }

    [Test]
    public void OrderingAndGraceAreEnforced()
    {
        Assert.That(VersionPolicyCheck.Evaluate(Policy(min: 3, rec: 2, latest: 4), null).Has("order", GateStatus.Fail));
        VersionPolicyCheck.Policy p = Policy();
        p.GraceMinutes = 10;
        Assert.That(VersionPolicyCheck.Evaluate(p, null).Has("grace", GateStatus.Fail));
    }

    [Test]
    public void RaisingTheMinimumMustNotStrandActiveMatches()
    {
        VersionPolicyCheck.Policy old = Policy(1, 1, 1);
        Assert.That(VersionPolicyCheck.Evaluate(Policy(2, 2, 2, leadMinutes: 0), old).Has("transition", GateStatus.Fail));
        Assert.That(VersionPolicyCheck.Evaluate(Policy(2, 2, 2, leadMinutes: 60), old).Has("transition", GateStatus.Pass));
        VersionPolicyCheck.Policy retire = Policy(1, 2, 2, leadMinutes: 5);
        retire.RulesVersions = new List<string> { "AK-TR-2" };
        Assert.That(VersionPolicyCheck.Evaluate(retire, old).Has("transition", GateStatus.Fail));
    }

    [Test]
    public void ClientRollbackIsFlagged() =>
        Assert.That(VersionPolicyCheck.Evaluate(Policy(1, 1, 1), Policy(1, 1, 2)).Has("rollback", GateStatus.Warn));
}
