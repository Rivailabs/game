using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using NUnit.Framework;

namespace AstraKingdoms.Meta.Tests;

/// <summary>Ticket 57: XP rules, levels, unlocks, symmetric catalogues, one grant per match.</summary>
public class ProgressionTests
{
    [TestCase(MatchKind.OnlineHuman, PlayerOutcome.Loss, 100, 10)]
    [TestCase(MatchKind.OnlineHuman, PlayerOutcome.Win, 125, 15)]
    [TestCase(MatchKind.OnlineHuman, PlayerOutcome.Draw, 110, 10)]
    [TestCase(MatchKind.LocalSharedPhone, PlayerOutcome.Unattributed, 100, 10)]
    [TestCase(MatchKind.Practice, PlayerOutcome.Win, 50, 10)]
    [TestCase(MatchKind.Practice, PlayerOutcome.Loss, 50, 10)]
    [TestCase(MatchKind.OnlineBot, PlayerOutcome.Win, 50, 10)]
    public void RewardsFollowThePlanTable(MatchKind kind, PlayerOutcome outcome, int xp, int coins)
    {
        var r = RewardCalculator.Compute(new MatchOutcomeReport("m", "p", kind, outcome, MatchEnding.RoundsComplete, T0.Noon));
        Assert.That(r.Eligibility, Is.EqualTo(GrantEligibility.Eligible));
        Assert.That(r.Xp, Is.EqualTo(xp));
        Assert.That(r.Coins, Is.EqualTo(coins));
    }

    [Test]
    public void EarlyVictoryIsACompletedMatch()
    {
        var r = RewardCalculator.Compute(T0.Human("m", "p", PlayerOutcome.Win, T0.Noon, MatchEnding.EarlyVictory));
        Assert.That(r.Xp, Is.EqualTo(125));
    }

    [TestCase(true, false, true, MatchEnding.RoundsComplete, GrantEligibility.Automation)]
    [TestCase(false, true, true, MatchEnding.RoundsComplete, GrantEligibility.DeveloperTest)]
    [TestCase(false, false, false, MatchEnding.RoundsComplete, GrantEligibility.InvalidMatch)]
    [TestCase(false, false, true, MatchEnding.VoluntaryForfeit, GrantEligibility.NotCompleted)]
    [TestCase(false, false, true, MatchEnding.TimeoutForfeit, GrantEligibility.NotCompleted)]
    [TestCase(false, false, true, MatchEnding.TechnicalAbort, GrantEligibility.NotCompleted)]
    public void IneligibleMatchesGrantNothing(bool automation, bool dev, bool valid, MatchEnding ending, GrantEligibility expected)
    {
        var report = new MatchOutcomeReport("m", "p", MatchKind.OnlineHuman, PlayerOutcome.Win, ending, T0.Noon,
            isAutomation: automation, isDeveloperTest: dev, isValid: valid);
        var r = RewardCalculator.Compute(report);
        Assert.That(r.Eligibility, Is.EqualTo(expected));
        Assert.That(r.Xp, Is.Zero);
        Assert.That(r.Coins, Is.Zero);

        var ledger = new InMemoryRewardLedgerStore();
        var svc = new ProgressionService(ledger, T0.Clock());
        Assert.That(svc.GrantForMatch(report).Status, Is.EqualTo(GrantStatus.Ineligible));
        Assert.That(ledger.Entries("p"), Is.Empty);
    }

    [TestCase(0, 1)]
    [TestCase(299, 1)]
    [TestCase(300, 2)]
    [TestCase(599, 2)]
    [TestCase(600, 3)]
    [TestCase(5399, 18)]
    [TestCase(5700, 20)]
    [TestCase(1_000_000, 20)]
    public void LevelsNeed300XpEachUpTo20(long xp, int level)
    {
        Assert.That(ProgressionRules.LevelFor(xp), Is.EqualTo(level));
    }

    [Test]
    public void XpToNextLevel()
    {
        Assert.That(ProgressionRules.XpToNextLevel(0), Is.EqualTo(300));
        Assert.That(ProgressionRules.XpToNextLevel(250), Is.EqualTo(50));
        Assert.That(ProgressionRules.XpToNextLevel(5699), Is.EqualTo(1));
        Assert.That(ProgressionRules.XpToNextLevel(5700), Is.EqualTo(0));
        Assert.That(ProgressionRules.XpIntoLevel(650), Is.EqualTo(50));
    }

    [Test]
    public void UnlockTableIsValidAndUsesWeaponUnlockLevels()
    {
        Assert.That(UnlockTable.Validate(), Is.Empty);
        for (int level = 2; level <= 16; level++)
        {
            var weapons = UnlockTable.AtLevel(level).Where(u => u.Kind == UnlockKind.Weapon).ToList();
            Assert.That(weapons, Has.Count.EqualTo(1), "level " + level);
            Assert.That(WeaponCatalog.Get(weapons[0].WeaponId).UnlockLevel, Is.EqualTo(level));
        }
        for (int level = 17; level <= 20; level++)
        {
            Assert.That(UnlockTable.AtLevel(level).Any(u => u.Kind == UnlockKind.Weapon), Is.False);
            Assert.That(UnlockTable.AtLevel(level).Any(u => u.Kind == UnlockKind.Cosmetic), Is.True);
        }
        Assert.That(UnlockTable.All.Count(u => u.Kind == UnlockKind.Weapon), Is.EqualTo(15));
    }

    [Test]
    public void OwnedWeaponsGrowWithLevel()
    {
        Assert.That(WeaponAccess.Owned(1), Has.Count.EqualTo(5));
        Assert.That(WeaponAccess.Owned(2).Select(w => w.Id), Does.Contain(6));
        Assert.That(WeaponAccess.Owned(16), Has.Count.EqualTo(20));
        Assert.That(WeaponAccess.Owned(20), Has.Count.EqualTo(20));
    }

    [Test]
    public void CompetitiveRoomCataloguesAreSymmetricAndIgnoreProgression()
    {
        // RoomCatalog takes no account data at all; check that both presets are exactly the rules' presets.
        Assert.That(WeaponAccess.RoomCatalog(CatalogPreset.Starter).Select(w => w.Id), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
        Assert.That(WeaponAccess.RoomCatalog(CatalogPreset.Full).Select(w => w.Id), Is.EqualTo(Enumerable.Range(1, 20)));
        var method = typeof(WeaponAccess).GetMethod(nameof(WeaponAccess.RoomCatalog));
        Assert.That(method.GetParameters().Select(p => p.ParameterType), Is.EqualTo(new[] { typeof(CatalogPreset) }));
        // A level-1 and a level-20 player see the same Full room; the level-1 player's extra weapons are loans.
        Assert.That(WeaponAccess.IsLoaned(20, 1, CatalogPreset.Full), Is.True);
        Assert.That(WeaponAccess.IsLoaned(20, 16, CatalogPreset.Full), Is.False);
        Assert.That(WeaponAccess.IsLoaned(20, 1, CatalogPreset.Starter), Is.False);
        Assert.That(WeaponAccess.Practice(1, CatalogPreset.Full), Has.Count.EqualTo(20));
        Assert.That(WeaponAccess.Practice(1), Has.Count.EqualTo(5));
    }

    [Test]
    public void OneMatchGrantsOnce()
    {
        var ledger = new InMemoryRewardLedgerStore();
        var svc = new ProgressionService(ledger, T0.Clock());
        var report = T0.Human("match-1", "alice", PlayerOutcome.Win, T0.Noon, weapons: new[] { 1, 6 });
        MatchGrantResult first = svc.GrantForMatch(report);
        MatchGrantResult again = svc.GrantForMatch(report);
        Assert.That(first.Status, Is.EqualTo(GrantStatus.Granted));
        Assert.That(first.XpGranted, Is.EqualTo(125));
        Assert.That(again.Status, Is.EqualTo(GrantStatus.AlreadyGranted));
        Assert.That(again.XpGranted, Is.Zero);
        Assert.That(svc.GetProfile("alice").TotalXp, Is.EqualTo(125));
        Assert.That(svc.GetProfile("alice").EarnedCoins, Is.EqualTo(15));
        // The same match grants the opponent separately.
        Assert.That(svc.GrantForMatch(T0.Human("match-1", "bob", PlayerOutcome.Loss, T0.Noon)).Status, Is.EqualTo(GrantStatus.Granted));
    }

    [Test]
    public void ARetryWithATamperedOutcomeStillGrantsNothing()
    {
        var ledger = new InMemoryRewardLedgerStore();
        var svc = new ProgressionService(ledger, T0.Clock());
        svc.GrantForMatch(T0.Human("m", "p", PlayerOutcome.Loss, T0.Noon));
        Assert.That(svc.GrantForMatch(T0.Human("m", "p", PlayerOutcome.Win, T0.Noon)).Status, Is.EqualTo(GrantStatus.AlreadyGranted));
        Assert.That(svc.GetProfile("p").TotalXp, Is.EqualTo(100));
        var dup = ledger.TryAppend(new RewardLedgerEntry(ProgressionService.MatchKey("m", "p"), "p", LedgerSource.Match, 125, 15, T0.Noon));
        Assert.That(dup.Status, Is.EqualTo(AppendStatus.Duplicate));
        Assert.That(dup.PayloadMismatch, Is.True);
    }

    [Test]
    public void ConcurrentDuplicateGrantsApplyExactlyOnce()
    {
        var ledger = new InMemoryRewardLedgerStore();
        var svc = new ProgressionService(ledger, T0.Clock());
        var report = T0.Human("race", "p", PlayerOutcome.Win, T0.Noon);
        var results = new MatchGrantResult[64];
        Parallel.For(0, results.Length, i => results[i] = svc.GrantForMatch(report));
        Assert.That(results.Count(r => r.Status == GrantStatus.Granted), Is.EqualTo(1));
        Assert.That(results.Count(r => r.Status == GrantStatus.AlreadyGranted), Is.EqualTo(63));
        Assert.That(svc.GetProfile("p").TotalXp, Is.EqualTo(125));
    }

    [Test]
    public void ConcurrentDistinctMatchesAllApply()
    {
        var ledger = new InMemoryRewardLedgerStore();
        var svc = new ProgressionService(ledger, T0.Clock());
        Parallel.For(0, 40, i => svc.GrantForMatch(T0.Human("m" + i, "p", PlayerOutcome.Loss, T0.Noon)));
        Assert.That(svc.GetProfile("p").TotalXp, Is.EqualTo(4000));
        Assert.That(svc.GetProfile("p").Level, Is.EqualTo(14));
    }

    [Test]
    public void LevelUpReportsTheWeaponUnlocked()
    {
        var svc = new ProgressionService(new InMemoryRewardLedgerStore(), T0.Clock());
        svc.GrantForMatch(T0.Human("a", "p", PlayerOutcome.Win, T0.Noon)); // 125
        svc.GrantForMatch(T0.Human("b", "p", PlayerOutcome.Loss, T0.Noon)); // 225
        MatchGrantResult r = svc.GrantForMatch(T0.Human("c", "p", PlayerOutcome.Draw, T0.Noon)); // 335
        Assert.That(r.LevelBefore, Is.EqualTo(1));
        Assert.That(r.LevelAfter, Is.EqualTo(2));
        Assert.That(r.NewUnlocks.Single().WeaponId, Is.EqualTo(6)); // Fire Fan
        Assert.That(svc.GetProfile("p").OwnedWeaponIds, Does.Contain(6));
    }

    [Test]
    public void ReversalIsAppendOnlyAndHappensOnce()
    {
        var ledger = new InMemoryRewardLedgerStore();
        var svc = new ProgressionService(ledger, T0.Clock());
        svc.GrantForMatch(T0.Human("bad", "p", PlayerOutcome.Win, T0.Noon, weapons: new[] { 3 }));
        string key = ProgressionService.MatchKey("bad", "p");
        Assert.That(svc.Reverse(key, "invalid replay").Status, Is.EqualTo(AppendStatus.Appended));
        Assert.That(svc.Reverse(key, "again").Status, Is.EqualTo(AppendStatus.Duplicate));
        Assert.That(ledger.Entries("p"), Has.Count.EqualTo(2), "the original entry stays");
        Assert.That(svc.GetProfile("p").TotalXp, Is.Zero);
        Assert.That(svc.GetProfile("p").EarnedCoins, Is.Zero);
        Assert.That(ledger.Totals("p").WeaponUses.ContainsKey(3), Is.False);
        Assert.That(svc.GrantForMatch(T0.Human("bad", "p", PlayerOutcome.Win, T0.Noon)).Status, Is.EqualTo(GrantStatus.AlreadyGranted));
    }

    [Test]
    public void MasteryCountsCompletedMatchesPerWeapon()
    {
        var svc = new ProgressionService(new InMemoryRewardLedgerStore(), T0.Clock());
        for (int i = 0; i < Mastery.BronzeUses; i++) svc.GrantForMatch(T0.Human("m" + i, "p", PlayerOutcome.Loss, T0.Noon, weapons: new[] { 2, 2, 4 }));
        svc.GrantForMatch(T0.Human("forfeit", "p", PlayerOutcome.Loss, T0.Noon, MatchEnding.VoluntaryForfeit, 4));
        var mastery = svc.GetProfile("p").Mastery;
        Assert.That(mastery[2], Is.EqualTo(MasteryTier.Bronze));
        Assert.That(mastery[4], Is.EqualTo(MasteryTier.Bronze));
        Assert.That(Mastery.TierFor(49), Is.EqualTo(MasteryTier.Silver));
        Assert.That(Mastery.TierFor(50), Is.EqualTo(MasteryTier.Gold));
    }

    [Test]
    public void RulesResultsMapToEndingsAndOutcomes()
    {
        var early = new MatchResult(TerminalReason.Territory90, PlayerSide.A, null, 46000, 5040, 4);
        var draw = new MatchResult(TerminalReason.RoundsComplete, null, null, 25520, 25520, 8);
        var forfeit = new MatchResult(TerminalReason.Forfeit, PlayerSide.B, PlayerSide.A, 25520, 25520, 2);
        var voided = new MatchResult(TerminalReason.Void, null, null, 25520, 25520, 2);
        Assert.That(MatchOutcomeReport.EndingFrom(early), Is.EqualTo(MatchEnding.EarlyVictory));
        Assert.That(MatchOutcomeReport.OutcomeFrom(early, PlayerSide.A), Is.EqualTo(PlayerOutcome.Win));
        Assert.That(MatchOutcomeReport.OutcomeFrom(early, PlayerSide.B), Is.EqualTo(PlayerOutcome.Loss));
        Assert.That(MatchOutcomeReport.OutcomeFrom(draw, PlayerSide.B), Is.EqualTo(PlayerOutcome.Draw));
        Assert.That(MatchOutcomeReport.EndingFrom(forfeit), Is.EqualTo(MatchEnding.TimeoutForfeit));
        Assert.That(MatchOutcomeReport.EndingFrom(voided), Is.EqualTo(MatchEnding.TechnicalAbort));
    }
}
