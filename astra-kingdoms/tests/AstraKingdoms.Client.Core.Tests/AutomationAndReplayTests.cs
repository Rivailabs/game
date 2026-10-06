using AstraKingdoms.Client.Automation;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Replay;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Tests;

public sealed class AutomationAndReplayTests
{
    [Test]
    public void PercentilesUseNearestRank()
    {
        var f = new FrameStats();
        for (int i = 1; i <= 100; i++) f.AddFrame(i);
        f.AddFrame(5000, loading: true);
        Assert.That(f.Count, Is.EqualTo(100));
        Assert.That(f.ExcludedLoadingFrames, Is.EqualTo(1));
        Assert.That(f.Percentile(50), Is.EqualTo(50));
        Assert.That(f.Percentile(95), Is.EqualTo(95));
        Assert.That(f.Percentile(99), Is.EqualTo(99));
        Assert.That(f.Percentile(100), Is.EqualTo(100));
        Assert.That(f.Max, Is.EqualTo(100));
        Assert.That(f.CountAbove(98), Is.EqualTo(2));
        Assert.That(new FrameStats().Percentile(95), Is.EqualTo(0));
    }

    [Test]
    public void MemoryPeaksAreMaxima()
    {
        var f = new FrameStats();
        f.AddMemory(10, 20, 3, 4);
        f.AddMemory(5, 30, 2, 9);
        Assert.That(f.PeakAllocatedBytes, Is.EqualTo(10));
        Assert.That(f.PeakReservedBytes, Is.EqualTo(30));
        Assert.That(f.PeakManagedBytes, Is.EqualTo(3));
        Assert.That(f.PeakGraphicsDriverBytes, Is.EqualTo(9));
    }

    [Test]
    public void LaunchOptionsParse()
    {
        LaunchOptions o = LaunchOptions.Parse(new[] { "Astra.exe", "-autoplay", "7,8", "-autoplaySpeed", "4", "-quitAfterAutoplay" });
        Assert.That(o.AutoplaySeeds, Is.EqualTo(new ulong[] { 7, 8 }));
        Assert.That(o.AutoplaySpeed, Is.EqualTo(4.0));
        Assert.That(o.QuitAfterAutoplay, Is.True);

        LaunchOptions smoke = LaunchOptions.Parse(new[] { "-forge_scenario", "pilot-smoke" });
        Assert.That(smoke.AutoplaySeeds, Is.EqualTo(LaunchOptions.ApprovedSeeds));
        Assert.That(smoke.AutoplaySpeed, Is.EqualTo(LaunchOptions.PilotSmokeSpeed));

        Assert.That(LaunchOptions.Parse(new[] { "-autoplay" }).AutoplaySeeds, Is.EqualTo(LaunchOptions.ApprovedSeeds));
        Assert.That(LaunchOptions.Parse(new[] { "-replay", "/tmp/r.json" }).ReplayPath, Is.EqualTo("/tmp/r.json"));
        Assert.That(LaunchOptions.Parse(Array.Empty<string>()).AutoplayRequested, Is.False);
        Assert.That(LaunchOptions.Parse(new[] { "-autoplaySpeed", "1000" }).AutoplaySpeed, Is.EqualTo(1.0), "out-of-range speed ignored");
    }

    private static LocalMatchHost PlayBots(ulong seed)
    {
        MatchFactory.ForAutoplay(seed, out byte[] s, out string id);
        var host = new LocalMatchHost(MatchConfig.Pilot(), s, id, SeatKind.Bot, SeatKind.Bot, BotDifficulty.Normal,
            MatchFactory.DeterministicRequestIds(seed), new HostTimings { BotCutDelaySeconds = 0 });
        host.Start();
        for (int i = 0; i < 4000 && host.Stage != HostStage.MatchOver; i++) host.Tick(0.25);
        Assert.That(host.Stage, Is.EqualTo(HostStage.MatchOver));
        return host;
    }

    [Test]
    public void AutoplayIsDeterministicPerSeed()
    {
        string a = PlayBots(2).ToRecord().ToJson();
        string b = PlayBots(2).ToRecord().ToJson();
        Assert.That(a, Is.EqualTo(b), "same seed, same record (request IDs are seeded too)");
    }

    [Test]
    public void ReportJsonRoundTrips()
    {
        LocalMatchHost host = PlayBots(1);
        var frames = new FrameStats();
        frames.AddFrame(33.3);
        var r = new AutoplayMatchResult { SeedNumber = 1, MatchId = host.Engine.MatchId, Result = host.Engine.Result, ReplayVerified = true, ReplayDetail = "ok" };
        JsonNode report = AutomationReport.Build("pilot-smoke", "0.1.0", "6000.0.x", "Android", "Test", 4, new[] { r }, frames);
        JsonNode parsed = JsonNode.Parse(report.ToCanonicalString());
        Assert.That(parsed["format"].AsString(), Is.EqualTo(AutomationReport.Format));
        Assert.That(parsed["passed"].AsBool(), Is.True);
        Assert.That(parsed["rules_hash"].AsString(), Is.EqualTo(RulesBundle.HashHex));
        Assert.That(parsed["frames"]["p95_us"].AsLong(), Is.EqualTo(33300));
        Assert.That(parsed["matches"].AsArray()[0]["cells_a"].AsInt() + parsed["matches"].AsArray()[0]["cells_b"].AsInt(), Is.EqualTo(RulesConstants.ActiveCells));

        r.ReplayVerified = false;
        Assert.That(AutomationReport.Build("x", "", "", "", "", 1, new[] { r }, frames)["passed"].AsBool(), Is.False);
        Assert.That(AutomationReport.Build("x", "", "", "", "", 1, Array.Empty<AutoplayMatchResult>(), frames)["passed"].AsBool(), Is.False, "no evidence is not a pass");
    }

    [Test]
    public void ReplayStepperReproducesEveryVolleyAndTheResult()
    {
        LocalMatchHost host = PlayBots(20261006);
        MatchRecord record = MatchRecord.FromJson(host.ToRecord().ToJson());
        var stepper = new ReplayStepper(record);
        int volleys = 0, cuts = 0;
        while (!stepper.Finished)
        {
            ReplayStep step = stepper.Step();
            if (step.ResolvedVolley != null)
            {
                volleys++;
                Assert.That(step.ResolvedVolley.Log.ToCanonicalText(),
                    Is.EqualTo(host.Engine.GetVolleyResult(step.ResolvedRound, step.ResolvedVolleyIndex).Log.ToCanonicalText()));
            }
            if (step.Kind == ReplayStepKind.Cut) cuts++;
        }
        int expected = record.Rounds.Sum(r => r.Volleys.Count);
        Assert.That(volleys, Is.EqualTo(expected));
        Assert.That(cuts, Is.EqualTo(record.Rounds.Count(r => r.CellsTransferred > 0 || (r.OfferedCards.Count > 0 && !r.CutTimedOut))));
        Assert.That(stepper.Engine.Result.ToString(), Is.EqualTo(host.Engine.Result.ToString()));
    }

    [Test]
    public void ReplayStepperRefusesOtherRulesHashes()
    {
        MatchRecord record = PlayBots(1).ToRecord();
        record.RulesHashHex = new string('0', 64);
        Assert.Throws<InvalidOperationException>(() => new ReplayStepper(record));
    }
}
