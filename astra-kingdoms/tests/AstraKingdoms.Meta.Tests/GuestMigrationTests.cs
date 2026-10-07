using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using NUnit.Framework;

namespace AstraKingdoms.Meta.Tests;

/// <summary>Ticket 57: guest/offline progress moves once, capped, recomputed by the server.</summary>
public class GuestMigrationTests
{
    private static List<LocalMatchSummary> Wins(int n, DateTimeOffset at) =>
        Enumerable.Range(0, n).Select(i => new LocalMatchSummary("local-" + i, MatchKind.OnlineHuman, PlayerOutcome.Win, MatchEnding.RoundsComplete,
            at.AddMinutes(-i))).ToList();

    [Test]
    public void RecomputesFromSummariesAndAppliesTheCap()
    {
        var ledger = new InMemoryRewardLedgerStore();
        var svc = new GuestMigrationService(ledger, T0.Clock());
        MigrationResult r = svc.Migrate("acct", "guest-1", Wins(1000, T0.Noon));
        Assert.That(r.Status, Is.EqualTo(MigrationStatus.Granted));
        Assert.That(r.MatchesCounted, Is.EqualTo(200), "only the newest 200 summaries are considered");
        Assert.That(r.XpRecalculated, Is.EqualTo(200 * 125));
        Assert.That(r.XpGranted, Is.EqualTo(1200));
        Assert.That(r.CoinsGranted, Is.EqualTo(150));
        Assert.That(ledger.Totals("acct").Xp, Is.EqualTo(1200));
        Assert.That(svc.Policy.PublishedRule, Does.Contain("1200 XP").And.Contain("150 coins"));
    }

    [Test]
    public void SmallProgressMovesUncappedAndInvalidSummariesAreIgnored()
    {
        var svc = new GuestMigrationService(new InMemoryRewardLedgerStore(), T0.Clock());
        var list = new List<LocalMatchSummary>
        {
            new("a", MatchKind.Practice, PlayerOutcome.Win, MatchEnding.RoundsComplete, T0.Noon.AddHours(-1)),
            new("a", MatchKind.Practice, PlayerOutcome.Win, MatchEnding.RoundsComplete, T0.Noon.AddHours(-1)), // duplicate id
            new("b", MatchKind.OnlineHuman, PlayerOutcome.Win, MatchEnding.VoluntaryForfeit, T0.Noon.AddHours(-1)), // not completed
            new("c", MatchKind.Practice, PlayerOutcome.Win, MatchEnding.RoundsComplete, T0.Noon.AddHours(-1), isAutomation: true),
            new("d", MatchKind.Practice, PlayerOutcome.Win, MatchEnding.RoundsComplete, T0.Noon.AddDays(-91)), // too old
            new("e", MatchKind.Practice, PlayerOutcome.Win, MatchEnding.RoundsComplete, T0.Noon.AddDays(1)), // future-dated
            new("f", MatchKind.LocalSharedPhone, PlayerOutcome.Unattributed, MatchEnding.EarlyVictory, T0.Noon.AddHours(-2)),
        };
        MigrationResult r = svc.Migrate("acct", "guest", list);
        Assert.That(r.MatchesCounted, Is.EqualTo(2));
        Assert.That(r.XpGranted, Is.EqualTo(150));
        Assert.That(r.CoinsGranted, Is.EqualTo(20));
    }

    [Test]
    public void OncePerAccountAndOncePerGuestProfile()
    {
        var ledger = new InMemoryRewardLedgerStore();
        var svc = new GuestMigrationService(ledger, T0.Clock());
        Assert.That(svc.Migrate("acct-1", "guest-1", Wins(3, T0.Noon)).Status, Is.EqualTo(MigrationStatus.Granted));
        Assert.That(svc.Migrate("acct-1", "guest-1", Wins(3, T0.Noon)).Status, Is.EqualTo(MigrationStatus.AccountAlreadyMigrated));
        Assert.That(svc.Migrate("acct-1", "guest-2", Wins(3, T0.Noon)).Status, Is.EqualTo(MigrationStatus.AccountAlreadyMigrated));
        Assert.That(svc.Migrate("acct-2", "guest-1", Wins(3, T0.Noon)).Status, Is.EqualTo(MigrationStatus.GuestAlreadyMigrated));
        Assert.That(ledger.Totals("acct-1").Xp, Is.EqualTo(375));
        Assert.That(ledger.Totals("acct-2").Xp, Is.Zero);
        // acct-1's refusal did not consume guest-2, which can still seed a different account.
        Assert.That(svc.Migrate("acct-3", "guest-2", Wins(1, T0.Noon)).Status, Is.EqualTo(MigrationStatus.Granted));
    }

    [Test]
    public void ConcurrentMigrationsGrantOnce()
    {
        var ledger = new InMemoryRewardLedgerStore();
        var svc = new GuestMigrationService(ledger, T0.Clock());
        var results = new MigrationResult[16];
        Parallel.For(0, results.Length, i => results[i] = svc.Migrate("acct", "guest", Wins(5, T0.Noon)));
        Assert.That(results.Count(r => r.Status == MigrationStatus.Granted), Is.EqualTo(1));
        Assert.That(ledger.Totals("acct").Xp, Is.EqualTo(625));
    }

    [Test]
    public void NothingEligibleMeansNothingToMigrate()
    {
        var svc = new GuestMigrationService(new InMemoryRewardLedgerStore(), T0.Clock());
        Assert.That(svc.Migrate("acct", "guest", new List<LocalMatchSummary>()).Status, Is.EqualTo(MigrationStatus.NothingToMigrate));
        Assert.That(svc.Migrate("acct", "guest", null).Status, Is.EqualTo(MigrationStatus.NothingToMigrate));
    }
}
