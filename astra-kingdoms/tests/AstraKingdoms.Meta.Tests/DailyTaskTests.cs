using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Economy;
using AstraKingdoms.Meta.Progression;
using NUnit.Framework;

namespace AstraKingdoms.Meta.Tests;

/// <summary>Ticket 58: daily cosmetic tasks — claim, expiry and retry cannot duplicate rewards.</summary>
public class DailyTaskTests
{
    private ManualClock _clock;
    private InMemoryRewardLedgerStore _ledger;
    private DailyTaskService _svc;

    // 2026-10-06 18:30 UTC = 2026-10-07 00:00 IST.
    private static readonly DateTimeOffset IstMidnight = new DateTimeOffset(2026, 10, 6, 18, 30, 0, TimeSpan.Zero);

    [SetUp]
    public void SetUp()
    {
        _clock = T0.Clock();
        _ledger = new InMemoryRewardLedgerStore();
        _svc = new DailyTaskService(_ledger, new InMemoryDailyTaskProgressStore(), _clock);
    }

    private DailyTaskView Task(string id) => _svc.GetTasks("p").Single(t => t.Definition.Id == id);

    [Test]
    public void ThreeOptionalTasksWorthTwentyCoinsAndNoneNeedAdsOrPurchases()
    {
        Assert.That(DailyTaskCatalog.Default, Has.Count.EqualTo(3));
        Assert.That(DailyTaskCatalog.Default.All(t => t.RewardCoins == 20), Is.True);
        string[] kinds = Enum.GetNames(typeof(TaskRequirement));
        Assert.That(kinds.Any(k => k.Contains("Ad", StringComparison.Ordinal) || k.Contains("Purchase", StringComparison.Ordinal) || k.Contains("Buy", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public void FinishTwoMatchesClaimOnceRetryIsIdempotent()
    {
        _svc.RecordMatch(T0.Human("m1", "p", PlayerOutcome.Loss, T0.Noon));
        string day = Task("finish-two-matches").DayKey;
        Assert.That(_svc.Claim("p", "finish-two-matches", day).Status, Is.EqualTo(ClaimStatus.NotComplete));
        _svc.RecordMatch(T0.Human("m1", "p", PlayerOutcome.Loss, T0.Noon)); // duplicate report
        Assert.That(Task("finish-two-matches").Progress, Is.EqualTo(1));
        _svc.RecordMatch(T0.Practice("m2", "p", T0.Noon));
        Assert.That(Task("finish-two-matches").Claimable, Is.True);

        ClaimResult first = _svc.Claim("p", "finish-two-matches", day);
        ClaimResult retry = _svc.Claim("p", "finish-two-matches", day);
        Assert.That(first.Status, Is.EqualTo(ClaimStatus.Claimed));
        Assert.That(first.Coins, Is.EqualTo(20));
        Assert.That(retry.Status, Is.EqualTo(ClaimStatus.AlreadyClaimed));
        Assert.That(retry.Coins, Is.EqualTo(20), "a retry reports the original grant");
        Assert.That(_ledger.Totals("p").Coins, Is.EqualTo(20));
        Assert.That(Task("finish-two-matches").Claimed, Is.True);
    }

    [Test]
    public void IneligibleMatchesDoNotCount()
    {
        _svc.RecordMatch(T0.Human("f", "p", PlayerOutcome.Loss, T0.Noon, MatchEnding.VoluntaryForfeit, 1));
        _svc.RecordMatch(new MatchOutcomeReport("auto", "p", MatchKind.Practice, PlayerOutcome.Win, MatchEnding.RoundsComplete, T0.Noon, new[] { 2 }, isAutomation: true));
        Assert.That(Task("finish-two-matches").Progress, Is.Zero);
        Assert.That(Task("two-elements").Progress, Is.Zero);
    }

    [Test]
    public void TwoDifferentElementsAcrossMatches()
    {
        _svc.RecordMatch(T0.Human("a", "p", PlayerOutcome.Loss, T0.Noon, weapons: new[] { 1, 6 })); // Agni twice
        Assert.That(Task("two-elements").Progress, Is.EqualTo(1));
        _svc.RecordMatch(T0.Human("b", "p", PlayerOutcome.Loss, T0.Noon, weapons: new[] { 5 })); // Varuna
        Assert.That(Task("two-elements").Complete, Is.True);
    }

    [Test]
    public void PracticeExerciseOrPracticeMatchCountsOnce()
    {
        Assert.That(_svc.RecordPracticeExercise("p", "ex-1", T0.Noon), Is.True);
        Assert.That(_svc.RecordPracticeExercise("p", "ex-1", T0.Noon), Is.False);
        Assert.That(Task("practice-exercise").Complete, Is.True);
        var other = new DailyTaskService(_ledger, new InMemoryDailyTaskProgressStore(), _clock);
        other.RecordMatch(T0.Practice("pm", "q", T0.Noon));
        Assert.That(other.GetTasks("q").Single(t => t.Definition.Id == "practice-exercise").Complete, Is.True);
    }

    [Test]
    public void DayBoundaryUsesIndiaMidnight()
    {
        DailyResetPolicy ist = DailyResetPolicy.IndiaStandardTime;
        Assert.That(ist.DayKey(IstMidnight.AddTicks(-1)), Is.EqualTo("2026-10-06"));
        Assert.That(ist.DayKey(IstMidnight), Is.EqualTo("2026-10-07"));
        Assert.That(ist.DayEnd("2026-10-06"), Is.EqualTo(IstMidnight));

        _svc.RecordMatch(T0.Practice("late", "p", IstMidnight.AddTicks(-1)));
        _svc.RecordMatch(T0.Practice("early", "p", IstMidnight));
        _clock.Set(IstMidnight.AddTicks(-1));
        Assert.That(Task("finish-two-matches").Progress, Is.EqualTo(1));
        _clock.Set(IstMidnight);
        Assert.That(Task("finish-two-matches").Progress, Is.EqualTo(1));
        Assert.That(Task("finish-two-matches").DayKey, Is.EqualTo("2026-10-07"));
    }

    [Test]
    public void UnclaimedTasksExpireAtDayEndButRetriesOfClaimedOnesStillSucceed()
    {
        _svc.RecordPracticeExercise("p", "ex", T0.Noon);
        _svc.RecordMatch(T0.Practice("a", "p", T0.Noon));
        _svc.RecordMatch(T0.Practice("b", "p", T0.Noon));
        Assert.That(_svc.Claim("p", "practice-exercise", "2026-10-06").Status, Is.EqualTo(ClaimStatus.Claimed));

        _clock.Set(IstMidnight.AddTicks(-1));
        Assert.That(Task("finish-two-matches").ExpiresAt, Is.EqualTo(IstMidnight));
        _clock.Set(IstMidnight);
        Assert.That(_svc.Claim("p", "finish-two-matches", "2026-10-06").Status, Is.EqualTo(ClaimStatus.Expired));
        Assert.That(_svc.Claim("p", "practice-exercise", "2026-10-06").Status, Is.EqualTo(ClaimStatus.AlreadyClaimed));
        Assert.That(_ledger.Totals("p").Coins, Is.EqualTo(20));
        // The new day starts empty.
        Assert.That(Task("finish-two-matches").Progress, Is.Zero);
        Assert.That(Task("practice-exercise").Claimed, Is.False);
    }

    [Test]
    public void ClaimJustBeforeMidnightSucceeds()
    {
        _svc.RecordPracticeExercise("p", "ex", T0.Noon);
        _clock.Set(IstMidnight.AddTicks(-1));
        Assert.That(_svc.Claim("p", "practice-exercise", "2026-10-06").Status, Is.EqualTo(ClaimStatus.Claimed));
    }

    [Test]
    public void FutureMalformedAndUnknownClaimsAreRefused()
    {
        Assert.That(_svc.Claim("p", "practice-exercise", "2026-10-07").Status, Is.EqualTo(ClaimStatus.NotYetAvailable));
        Assert.That(_svc.Claim("p", "practice-exercise", "06/10/2026").Status, Is.EqualTo(ClaimStatus.UnknownTask));
        Assert.That(_svc.Claim("p", "watch-an-ad", "2026-10-06").Status, Is.EqualTo(ClaimStatus.UnknownTask));
    }

    [Test]
    public void ConcurrentClaimsGrantOnce()
    {
        _svc.RecordPracticeExercise("p", "ex", T0.Noon);
        var results = new ClaimResult[32];
        Parallel.For(0, results.Length, i => results[i] = _svc.Claim("p", "practice-exercise", "2026-10-06"));
        Assert.That(results.Count(r => r.Status == ClaimStatus.Claimed), Is.EqualTo(1));
        Assert.That(results.All(r => r.Coins == 20), Is.True);
        Assert.That(_ledger.Totals("p").Coins, Is.EqualTo(20));
    }

    [Test]
    public void ConcurrentProgressUpdatesAreNotLost()
    {
        Parallel.For(0, 50, i => _svc.RecordMatch(T0.Practice("m" + i, "p", T0.Noon)));
        var store = new InMemoryDailyTaskProgressStore();
        var svc = new DailyTaskService(_ledger, store, _clock);
        Parallel.For(0, 50, i => svc.RecordMatch(T0.Practice("m" + i, "p", T0.Noon)));
        Assert.That(store.Get("p", "2026-10-06").CountedMatches, Has.Count.EqualTo(50));
    }
}
