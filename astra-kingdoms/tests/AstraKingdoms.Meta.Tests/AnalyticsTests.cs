using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Reporting;
using NUnit.Framework;
using static AstraKingdoms.Meta.Analytics.AnalyticsClient;

namespace AstraKingdoms.Meta.Tests;

/// <summary>Ticket 64: event schema, consent gating, bot/test exclusion, batching.</summary>
public class AnalyticsClientTests
{
    private static readonly Dictionary<AnalyticsEventType, Dictionary<string, object>> Samples = new()
    {
        { AnalyticsEventType.ValidSession, Props("foreground", true, "app_version", "1.0.0", "build", "release") },
        { AnalyticsEventType.TutorialCompletion, Props("tutorial_version", 1L, "steps", 7L) },
        { AnalyticsEventType.MatchStart, Props("match_id", "m-1", "mode", "practice", "catalog", "starter") },
        { AnalyticsEventType.Lock, Props("match_id", "m-1", "round", 1L, "volley", 2L, "timed_out", false) },
        { AnalyticsEventType.Resolution, Props("match_id", "m-1", "round", 1L, "volley", 2L) },
        { AnalyticsEventType.LandCutCompletion, Props("match_id", "m-1", "round", 1L, "cells", 1530L, "method", "drawn") },
        { AnalyticsEventType.MatchEnd, Props("match_id", "m-1", "mode", "practice", "reason", "rounds_complete", "result", "win", "rounds", 8L) },
        { AnalyticsEventType.ReconnectOutcome, Props("match_id", "m-1", "outcome", "restored", "downtime_ms", 1200L) },
        { AnalyticsEventType.PurchaseState, Props("sku", "ak.cosmetic.sunrise_pack", "state", "granted", "test_purchase", false) },
        { AnalyticsEventType.AdRewardState, Props("placement", "rewarded.home", "state", "reward_verified") },
        { AnalyticsEventType.Crash, Props("critical", true, "stage", "startup") },
    };

    private static AnalyticsConsent Both => new(ConsentChoice.Granted, ConsentChoice.Granted);

    [Test]
    public void EveryEventTypeHasASchemaAndAValidSample()
    {
        Assert.That(AnalyticsSchema.All.Select(s => s.Type), Is.EquivalentTo(Enum.GetValues(typeof(AnalyticsEventType))));
        foreach (var kv in Samples)
        {
            var e = new AnalyticsEvent("e1", kv.Key, "aid", "sid", T0.Noon, kv.Value);
            Assert.That(AnalyticsSchema.Validate(e), Is.Empty, kv.Key.ToString());
        }
    }

    [Test]
    public void FreeTextUnknownPropertiesAndBadValuesAreRejected()
    {
        IReadOnlyList<string> Errors(Dictionary<string, object> p) => AnalyticsSchema.Validate(new AnalyticsEvent("e", AnalyticsEventType.MatchEnd, "a", "s", T0.Noon, p));
        Dictionary<string, object> good = Samples[AnalyticsEventType.MatchEnd];
        Assert.That(Errors(new Dictionary<string, object>(good) { ["player_name"] = "Asha" }), Has.Some.Contains("unknown property"));
        Assert.That(Errors(new Dictionary<string, object>(good) { ["match_id"] = "asha@example.com" }), Has.Some.Contains("short token"));
        Assert.That(Errors(new Dictionary<string, object>(good) { ["match_id"] = "has space" }), Has.Some.Contains("short token"));
        Assert.That(Errors(new Dictionary<string, object>(good) { ["reason"] = "rage_quit" }), Has.Some.Contains("not allowed"));
        Assert.That(Errors(new Dictionary<string, object>(good) { ["rounds"] = "eight" }), Has.Some.Contains("expected integer"));
        var missing = new Dictionary<string, object>(good);
        missing.Remove("reason");
        Assert.That(Errors(missing), Has.Some.Contains("missing reason"));
        Assert.That(Samples[AnalyticsEventType.Lock].Keys.Any(k => k.Contains("pitch") || k.Contains("yaw") || k.Contains("weapon")), Is.False,
            "lock events never carry the hidden choice");
    }

    [Test]
    public void NothingIsCollectedBeforeConsent()
    {
        var sink = new ListAnalyticsSink();
        AnalyticsConsent consent = AnalyticsConsent.NotAsked;
        var client = new AnalyticsClient(sink, T0.Clock(), () => consent, () => AudienceProfile.Adult);
        foreach (var kv in Samples) Assert.That(client.Track(kv.Key, kv.Value), Is.False);
        Assert.That(client.Queued, Is.Zero);
        consent = new AnalyticsConsent(ConsentChoice.Denied, ConsentChoice.Granted);
        Assert.That(client.Track(AnalyticsEventType.MatchStart, Samples[AnalyticsEventType.MatchStart]), Is.False);
        Assert.That(client.Track(AnalyticsEventType.Crash, Samples[AnalyticsEventType.Crash]), Is.True, "crash reports have their own consent");
    }

    [Test]
    public void ChildrenAreNotMeasuredUnlessTheAssessmentApproves()
    {
        var sink = new ListAnalyticsSink();
        var policy = new CollectionPolicy();
        var client = new AnalyticsClient(sink, T0.Clock(), () => Both, () => AudienceProfile.Child(ParentalConsent.Verified), policy: policy);
        Assert.That(client.Track(AnalyticsEventType.ValidSession, Samples[AnalyticsEventType.ValidSession]), Is.False);
        Assert.That(client.Track(AnalyticsEventType.Crash, Samples[AnalyticsEventType.Crash]), Is.False);
        policy.ChildCrashReportsApproved = true;
        Assert.That(client.Track(AnalyticsEventType.Crash, Samples[AnalyticsEventType.Crash]), Is.True);
        Assert.That(ConsentGate.Allows(AnalyticsEventType.MatchStart, Both, AudienceProfile.Unknown, policy), Is.False);
    }

    [Test]
    public void WithdrawingConsentDropsTheQueueAndDisablesCrashReporting()
    {
        var sink = new ListAnalyticsSink();
        AnalyticsConsent consent = Both;
        var crash = new RecordingCrashReporter();
        var client = new AnalyticsClient(sink, T0.Clock(), () => consent, () => AudienceProfile.Adult) { CrashReporter = crash };
        client.Track(AnalyticsEventType.MatchStart, Samples[AnalyticsEventType.MatchStart]);
        client.Track(AnalyticsEventType.Crash, Samples[AnalyticsEventType.Crash]);
        Assert.That(crash.Crashes, Is.EqualTo(1));
        consent = new AnalyticsConsent(ConsentChoice.Denied, ConsentChoice.Denied);
        client.OnConsentChanged();
        Assert.That(client.Queued, Is.Zero);
        Assert.That(crash.Enabled, Is.False);
        Assert.That(client.Flush(), Is.Zero);
        Assert.That(sink.Received, Is.Empty);
    }

    [Test]
    public void FlagsAndCohortAreStampedAndBatchesRetry()
    {
        var sink = new ListAnalyticsSink { Accept = false };
        var client = new AnalyticsClient(sink, T0.Clock(), () => Both, () => AudienceProfile.Adult)
        {
            SessionFlags = TrafficFlags.Internal,
            Cohort = CohortSource.FriendsAndColleagues,
        };
        for (int i = 0; i < 120; i++) client.Track(AnalyticsEventType.Resolution, Samples[AnalyticsEventType.Resolution], TrafficFlags.Bot);
        Assert.That(client.Flush(), Is.Zero);
        Assert.That(client.Queued, Is.EqualTo(120));
        sink.Accept = true;
        Assert.That(client.Flush(), Is.EqualTo(120));
        Assert.That(sink.Received.All(e => e.Flags == (TrafficFlags.Internal | TrafficFlags.Bot) && e.Cohort == CohortSource.FriendsAndColleagues), Is.True);
        Assert.That(sink.Received.Select(e => e.EventId).Distinct().Count(), Is.EqualTo(120));
    }

    [Test]
    public void ResetAnalyticsIdRotatesAndClears()
    {
        var client = new AnalyticsClient(new ListAnalyticsSink(), T0.Clock(), () => Both, () => AudienceProfile.Adult);
        string before = client.AnalyticsId;
        client.Track(AnalyticsEventType.MatchStart, Samples[AnalyticsEventType.MatchStart]);
        client.ResetAnalyticsId();
        Assert.That(client.AnalyticsId, Is.Not.EqualTo(before));
        Assert.That(client.Queued, Is.Zero);
    }

    [Test]
    public void QueueIsBounded()
    {
        var client = new AnalyticsClient(new ListAnalyticsSink { Accept = false }, T0.Clock(), () => Both, () => AudienceProfile.Adult);
        for (int i = 0; i < AnalyticsClient.MaxQueue + 10; i++) client.Track(AnalyticsEventType.Resolution, Samples[AnalyticsEventType.Resolution]);
        Assert.That(client.Queued, Is.EqualTo(AnalyticsClient.MaxQueue));
        Assert.That(client.Dropped, Is.EqualTo(10));
    }

    private sealed class RecordingCrashReporter : ICrashReporter
    {
        public bool Enabled = true;
        public int Crashes;
        public void SetCollectionEnabled(bool enabled) => Enabled = enabled;
        public void RecordCrash(bool critical, string stage) => Crashes++;
    }
}

/// <summary>Ticket 64: metric definitions — retention windows, Wilson intervals, completion and crash-free denominators.</summary>
public class MetricTests
{
    private static int _n;

    private static AnalyticsEvent Session(string person, DateTimeOffset at, bool foreground = true, TrafficFlags flags = TrafficFlags.None,
        CohortSource cohort = CohortSource.Organic, string session = null) =>
        new("ev" + Interlocked.Increment(ref _n), AnalyticsEventType.ValidSession, person, session ?? "s" + _n, at,
            Props("foreground", foreground, "app_version", "1.0", "build", "release"), flags, cohort);

    private static AnalyticsEvent Crash(string person, string session, bool critical = false) =>
        new("ev" + Interlocked.Increment(ref _n), AnalyticsEventType.Crash, person, session, T0.Noon, Props("critical", critical, "stage", "startup"));

    private static readonly DateTimeOffset First = T0.Noon;

    [TestCase(6, 50, 5.6, 23.8)]
    [TestCase(60, 500, 9.4, 15.1)]
    [TestCase(120, 1000, 10.1, 14.2)]
    public void WilsonIntervalsMatchThePlan(int k, int n, double low, double high)
    {
        var p = new Proportion(k, n);
        Assert.That(Math.Round(p.Low * 100, 1), Is.EqualTo(low));
        Assert.That(Math.Round(p.High * 100, 1), Is.EqualTo(high));
        Assert.That(p.Rate, Is.EqualTo(0.12).Within(1e-12));
    }

    [Test]
    public void WilsonEdgeCases()
    {
        Assert.That(new Proportion(0, 0).Low, Is.Zero);
        Assert.That(new Proportion(0, 10).Low, Is.Zero);
        Assert.That(new Proportion(10, 10).High, Is.EqualTo(1).Within(1e-12));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new Proportion(11, 10));
    }

    [TestCase(23.99, false)]
    [TestCase(24, true)]
    [TestCase(47.99, true)]
    [TestCase(48, false)]
    public void D1WindowIsHours24To48FromFirstSession(double hours, bool retained)
    {
        var events = new[] { Session("a", First), Session("a", First.AddHours(hours)) };
        RetentionResult r = RetentionCalculator.Compute(events, RetentionWindow.D1, First.AddHours(48), minimumCohort: 1);
        Assert.That(r.Retained.Total, Is.EqualTo(1));
        Assert.That(r.Retained.Successes, Is.EqualTo(retained ? 1 : 0));
    }

    [Test]
    public void D7AndD30Windows()
    {
        var events = new[]
        {
            Session("a", First), Session("a", First.AddHours(168)),
            Session("b", First), Session("b", First.AddHours(192)),
            Session("c", First), Session("c", First.AddHours(720)), Session("c", First.AddHours(743.9)),
        };
        Assert.That(RetentionCalculator.Compute(events, RetentionWindow.D7, First.AddDays(40)).Retained.Successes, Is.EqualTo(1));
        Assert.That(RetentionCalculator.Compute(events, RetentionWindow.D30, First.AddDays(40)).Retained.Successes, Is.EqualTo(1));
    }

    [Test]
    public void OnlyMatureCohortsAreReported()
    {
        var events = new[] { Session("a", First), Session("b", First.AddHours(1)) };
        RetentionResult r = RetentionCalculator.Compute(events, RetentionWindow.D1, First.AddHours(48));
        Assert.That(r.Retained.Total, Is.EqualTo(1));
        Assert.That(r.Immature, Is.EqualTo(1));
        Assert.That(r.InsufficientEvidence, Is.True);
        Assert.That(RetentionWindow.Definition, Does.Contain("elapsed-window"));
    }

    [Test]
    public void BotsTestsBackgroundLaunchesAndDuplicatesNeverCount()
    {
        AnalyticsEvent back = Session("a", First.AddHours(30));
        var events = new List<AnalyticsEvent>
        {
            Session("a", First), Session("a", First.AddHours(30), foreground: false), back, back, // duplicate ingestion of the same event
            Session("bot", First, flags: TrafficFlags.Bot), Session("bot", First.AddHours(30), flags: TrafficFlags.Bot),
            Session("ci", First, flags: TrafficFlags.TestClient), Session("dev", First, flags: TrafficFlags.Internal),
            Session("auto", First, flags: TrafficFlags.Automation),
        };
        RetentionResult r = RetentionCalculator.Compute(events, RetentionWindow.D1, First.AddDays(3), minimumCohort: 1);
        Assert.That(r.Retained.Total, Is.EqualTo(1));
        Assert.That(r.Retained.Successes, Is.EqualTo(1));
    }

    [Test]
    public void CohortsAreFilteredByFirstSessionAndSource()
    {
        var events = new[]
        {
            Session("early", First.AddDays(-2)), Session("in", First), Session("paid", First, cohort: CohortSource.Paid),
        };
        RetentionResult r = RetentionCalculator.Compute(events, RetentionWindow.D1, First.AddDays(5), First.AddDays(-1), First.AddDays(1), CohortSource.Organic, 1);
        Assert.That(r.Retained.Total, Is.EqualTo(1));
    }

    [Test]
    public void CompletionExcludesForfeitsAndAbortsAndSplitsHumanAndBot()
    {
        var records = new[]
        {
            new MatchRecordSummary("1", MatchKind.OnlineHuman, MatchEnding.RoundsComplete, 2, 8, First),
            new MatchRecordSummary("2", MatchKind.OnlineHuman, MatchEnding.EarlyVictory, 2, 5, First),
            new MatchRecordSummary("3", MatchKind.OnlineHuman, MatchEnding.VoluntaryForfeit, 2, 3, First),
            new MatchRecordSummary("4", MatchKind.OnlineHuman, MatchEnding.TimeoutForfeit, 2, 2, First),
            new MatchRecordSummary("5", MatchKind.OnlineBot, MatchEnding.TechnicalAbort, 1, 1, First),
            new MatchRecordSummary("6", MatchKind.Practice, MatchEnding.RoundsComplete, 1, 8, First),
            new MatchRecordSummary("7", MatchKind.Practice, MatchEnding.RoundsComplete, 0, 8, First, isAutomation: true),
            new MatchRecordSummary("8", MatchKind.OnlineHuman, MatchEnding.RoundsComplete, 2, 8, First, isInternal: true),
        };
        var report = CompletionCalculator.Compute(records).ToDictionary(b => b.Segment);
        Assert.That(report["all-human-started"].Completion.Total, Is.EqualTo(6));
        Assert.That(report["all-human-started"].Completion.Successes, Is.EqualTo(3));
        Assert.That(report["human-vs-human"].Completion.ToString(), Does.StartWith("2/4"));
        Assert.That(report["human-vs-bot"].Completion.ToString(), Does.StartWith("1/2"));
        Assert.That(report["all-human-started"].ByEnding[MatchEnding.VoluntaryForfeit], Is.EqualTo(1));
        Assert.That(report["all-human-started"].ByEnding[MatchEnding.TimeoutForfeit], Is.EqualTo(1));
        Assert.That(report["all-human-started"].ByEnding[MatchEnding.TechnicalAbort], Is.EqualTo(1));
    }

    [Test]
    public void CrashFreeDenominatorIncludesSessionsThatCrashBeforeAMatch()
    {
        var events = new List<AnalyticsEvent>
        {
            Session("a", First, session: "s1"), Session("b", First, session: "s2"), Session("c", First, session: "s3"),
            Crash("c", "s3", critical: true),
            Crash("d", "s4"), // crashed during startup before the valid-session event was sent
            Session("bot", First, flags: TrafficFlags.Bot, session: "s5"),
        };
        CrashFreeResult r = CrashFreeCalculator.Compute(events);
        Assert.That(r.CrashFree.Total, Is.EqualTo(4));
        Assert.That(r.CrashFree.Successes, Is.EqualTo(2));
        Assert.That(r.CriticalSessionIds, Is.EqualTo(new[] { "s3" }));
    }

    [Test]
    public void MatchesPerActivePlayerReportsDistribution()
    {
        AnalyticsEvent End(string who, DateTimeOffset at) => new("ev" + Interlocked.Increment(ref _n), AnalyticsEventType.MatchEnd, who, "s", at,
            Props("match_id", "m" + _n, "mode", "practice", "reason", "rounds_complete", "result", "win", "rounds", 8L));
        var events = new List<AnalyticsEvent>
        {
            Session("a", First), End("a", First), End("a", First), End("a", First),
            Session("b", First), // active, no match
            Session("a", First.AddDays(1)), End("a", First.AddDays(1)),
            Session("bot", First, flags: TrafficFlags.Bot),
        };
        var m = MatchesPerActivePlayer.Compute(events, new TimeSpan(5, 30, 0));
        Assert.That(m.PersonDays, Is.EqualTo(3));
        Assert.That(m.Distribution[3], Is.EqualTo(1));
        Assert.That(m.Distribution[0], Is.EqualTo(1));
        Assert.That(m.Distribution[1], Is.EqualTo(1));
        Assert.That(m.Mean, Is.EqualTo(4.0 / 3).Within(1e-9));
    }
}

/// <summary>Ticket 64: permitted events reconcile with authoritative match records.</summary>
public class ReconciliationTests
{
    private static AnalyticsEvent End(string id, string match, string mode, string reason, long rounds, TrafficFlags flags = TrafficFlags.None) =>
        new(id, AnalyticsEventType.MatchEnd, "a", "s", T0.Noon, Props("match_id", match, "mode", mode, "reason", reason, "result", "win", "rounds", rounds), flags);

    private static readonly MatchRecordSummary[] Records =
    {
        new("m1", MatchKind.OnlineHuman, MatchEnding.RoundsComplete, 2, 8, T0.Noon),
        new("m2", MatchKind.Practice, MatchEnding.EarlyVictory, 1, 4, T0.Noon),
        new("m3", MatchKind.OnlineHuman, MatchEnding.TimeoutForfeit, 2, 2, T0.Noon),
        new("auto", MatchKind.Practice, MatchEnding.RoundsComplete, 0, 8, T0.Noon, isAutomation: true),
    };

    [Test]
    public void ConsistentEventsReconcileWithCoverage()
    {
        var events = new[]
        {
            End("e1", "m1", "online_human", "rounds_complete", 8), End("e2", "m1", "online_human", "rounds_complete", 8),
            End("e3", "m2", "practice", "early_victory", 4), End("e3", "m2", "practice", "early_victory", 4),
            End("e4", "auto", "practice", "rounds_complete", 8, TrafficFlags.Automation),
        };
        ReconciliationReport r = EventReconciler.Reconcile(events, Records);
        Assert.That(r.Consistent, Is.True, string.Join("; ", r.Mismatches.Concat(r.EventsWithoutRecord)));
        Assert.That(r.DuplicateEventIds, Is.EqualTo(1));
        Assert.That(r.EventCoverage.Successes, Is.EqualTo(2));
        Assert.That(r.EventCoverage.Total, Is.EqualTo(3), "m3's players did not consent; that is coverage, not an error");
    }

    [Test]
    public void ContradictionsAreReported()
    {
        var events = new[]
        {
            End("e1", "m1", "online_human", "early_victory", 8),
            End("e2", "m2", "online_human", "early_victory", 3),
            End("e3", "m2", "practice", "early_victory", 4),
            End("e4", "ghost", "practice", "rounds_complete", 8),
            End("e5", "auto", "practice", "rounds_complete", 8),
        };
        ReconciliationReport r = EventReconciler.Reconcile(events, Records);
        Assert.That(r.Consistent, Is.False);
        Assert.That(r.Mismatches, Has.Some.Contains("m1: reason early_victory vs record rounds_complete"));
        Assert.That(r.Mismatches, Has.Some.Contains("m2: mode online_human"));
        Assert.That(r.Mismatches, Has.Some.Contains("m2: rounds 3"));
        Assert.That(r.ExcessEndEvents, Has.Some.StartsWith("m2"));
        Assert.That(r.EventsWithoutRecord, Is.EqualTo(new[] { "ghost" }));
        Assert.That(r.UnflaggedTestTraffic, Is.EqualTo(new[] { "auto" }));
    }
}

/// <summary>Plan "Revenue and contribution model".</summary>
public class RevenueModelTests
{
    [Test]
    public void NetIapDeductsTaxFeesAndRefundsWithoutDoubleCounting()
    {
        var lines = new[]
        {
            new IapLine("o1", false, 100m, 15.25m, 12.71m),
            new IapLine("o2", false, 100m, 15.25m, 12.71m),
            new IapLine("o2", true, 100m, 15.25m, 12.71m),
            new IapLine("o2", true, 100m, 15.25m, 12.71m), // repeated refund line in the export
            new IapLine("t1", false, 100m, 0m, 0m, isTestPurchase: true),
        };
        var warnings = new List<string>();
        Assert.That(RevenueModel.NetIap(lines, warnings), Is.EqualTo(72.04m));
        Assert.That(warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void ContributionAndUnitCosts()
    {
        var ads = new AdPeriod { Opportunities = 1000, OptIns = 300, Fills = 280, PaidImpressions = 250, RewardCompletions = 240, RealizedPublisherEcpm = 40m, InvalidTrafficAdjustment = 1m };
        var costs = new CostPeriod { MatchAndProfileServices = 5m, Database = 2m, Bandwidth = 1m, Storage = 0.5m, VariableSupportAndModeration = 1.5m, FixedOperations = 20m, DevelopmentAndContent = 100m, AttributableAcquisition = 3m };
        RevenueReport r = RevenueModel.Compute(new[] { new IapLine("o1", false, 100m, 15m, 15m) }, ads, costs, 100, 400, 50m, 720);
        Assert.That(r.NetAds, Is.EqualTo(9m));
        Assert.That(r.NetIap, Is.EqualTo(70m));
        Assert.That(r.GameNet, Is.EqualTo(79m));
        Assert.That(r.VariableCost, Is.EqualTo(10m));
        Assert.That(r.Contribution, Is.EqualTo(69m));
        Assert.That(r.OperatingResult, Is.EqualTo(-51m));
        Assert.That(r.AcquisitionContribution, Is.EqualTo(66m));
        Assert.That(r.NetRevenuePerMau, Is.EqualTo(0.79m));
        Assert.That(r.BackendCostPerMau, Is.EqualTo(0.1m));
        Assert.That(r.CostPerCompletedMatch, Is.EqualTo(0.025m));
        Assert.That(r.CostPerPlayerHour, Is.EqualTo(0.2m));
        Assert.That(r.AverageConcurrency, Is.EqualTo(50.0 / 720).Within(1e-12));
        Assert.That(r.Warnings, Has.Some.Contains("peak"));
    }
}
