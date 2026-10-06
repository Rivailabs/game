using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Reporting;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using NUnit.Framework;

namespace AstraKingdoms.Meta.Tests;

/// <summary>Real rules-engine records flow into grants, summaries and analytics events that reconcile.</summary>
public class MatchReportBuilderTests
{
    private static MatchRecord PlayBotMatch(long n)
    {
        BotMatchRunner.SeedFor(20261006, n, out byte[] seed, out string id);
        MatchEngine e = BotMatchRunner.Run(MatchConfig.V1Full(MatchMode.Practice), seed, id,
            BotPlayer.Create(PlayerSide.A, BotDifficulty.Normal, seed), BotPlayer.Create(PlayerSide.B, BotDifficulty.Normal, seed));
        return MatchRecord.FromEngine(e);
    }

    [Test]
    public void RecordProducesAGrantWithTheWeaponsActuallyLocked()
    {
        MatchRecord record = PlayBotMatch(1);
        MatchOutcomeReport report = MatchReportBuilder.FromRecord(record, "guest", PlayerSide.A, MatchKind.Practice, T0.Noon);
        Assert.That(report.MatchResultId, Is.EqualTo(record.MatchId));
        Assert.That(report.WeaponsUsed, Is.Not.Empty);
        Assert.That(report.WeaponsUsed.All(WeaponCatalog.IsRegularId), Is.True);
        Assert.That(report.Catalog, Is.EqualTo(CatalogPreset.Full));
        var locks = record.Commands.Where(c => c.Sender == PlayerSide.A && c.Command is LockInputCommand).Select(c => ((LockInputCommand)c.Command).WeaponId);
        Assert.That(report.WeaponsUsed, Is.EquivalentTo(locks.Where(WeaponCatalog.IsRegularId).Distinct()));

        var svc = new ProgressionService(new InMemoryRewardLedgerStore(), T0.Clock());
        MatchGrantResult g = svc.GrantForMatch(report);
        Assert.That(g.Status, Is.EqualTo(report.IsNormallyCompleted ? GrantStatus.Granted : GrantStatus.Ineligible));
        Assert.That(svc.GrantForMatch(report).Status, Is.Not.EqualTo(GrantStatus.Granted));
    }

    [Test]
    public void SharedPhoneOutcomeIsUnattributedAndAutomationIsFlagged()
    {
        MatchRecord record = PlayBotMatch(2);
        Assert.That(MatchReportBuilder.FromRecord(record, "g", PlayerSide.A, MatchKind.LocalSharedPhone, T0.Noon, attributeOutcome: false).Outcome,
            Is.EqualTo(PlayerOutcome.Unattributed));
        MatchOutcomeReport auto = MatchReportBuilder.FromRecord(record, "g", PlayerSide.A, MatchKind.Practice, T0.Noon, isAutomation: true);
        Assert.That(RewardCalculator.Compute(auto).Eligibility, Is.EqualTo(GrantEligibility.Automation));
    }

    [Test]
    public void EventsBuiltFromRecordsValidateAndReconcile()
    {
        var records = new List<MatchRecordSummary>();
        var events = new List<AnalyticsEvent>();
        for (int i = 0; i < 4; i++)
        {
            MatchRecord rec = PlayBotMatch(10 + i);
            records.Add(MatchReportBuilder.Summary(rec, MatchKind.Practice, 1, T0.Noon));
            MatchOutcomeReport rep = MatchReportBuilder.FromRecord(rec, "g", PlayerSide.A, MatchKind.Practice, T0.Noon);
            events.Add(new AnalyticsEvent("s" + i, AnalyticsEventType.MatchStart, "aid", "sid", T0.Noon, AnalyticsEvents.MatchStart(rec.MatchId, MatchKind.Practice, rec.Config.Catalog)));
            events.Add(new AnalyticsEvent("e" + i, AnalyticsEventType.MatchEnd, "aid", "sid", T0.Noon, AnalyticsEvents.MatchEnd(rep, rec.Result.RoundsPlayed)));
        }
        foreach (AnalyticsEvent e in events) Assert.That(AnalyticsSchema.Validate(e), Is.Empty);
        ReconciliationReport r = EventReconciler.Reconcile(events, records);
        Assert.That(r.Consistent, Is.True);
        Assert.That(r.EventCoverage.Rate, Is.EqualTo(1.0));
        Assert.That(AnalyticsSchema.Validate(new AnalyticsEvent("v", AnalyticsEventType.ValidSession, "a", "s", T0.Noon, AnalyticsEvents.ValidSession("0.1.0", true))), Is.Empty);
        Assert.That(AnalyticsSchema.Validate(new AnalyticsEvent("p", AnalyticsEventType.PurchaseState, "a", "s", T0.Noon, AnalyticsEvents.PurchaseState("ak.cosmetic.sunrise_pack", "granted"))), Is.Empty);
        Assert.That(AnalyticsSchema.Validate(new AnalyticsEvent("ad", AnalyticsEventType.AdRewardState, "a", "s", T0.Noon, AnalyticsEvents.AdRewardState("rewarded.home", "declined"))), Is.Empty);
    }
}
