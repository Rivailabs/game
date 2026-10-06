using AstraKingdoms.Modes.FourPlayer;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Modes.Tests.FourPlayer;

/// <summary>Plan: "Spectator messages contain only explicitly public states and resolved events ... at least one completed wave behind".</summary>
public class SpectatorFeedTests
{
    private static string Text(SpectatorSnapshot s) => string.Join("\n", s.Events.Select(e => e.ToString()));

    [Test]
    public void Feed_TrailsActivePlayByAtLeastOneCompletedWave()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        for (int w = 1; w <= 3; w++)
        {
            // During wave w, spectators see at most wave w-2.
            SpectatorSnapshot s = SpectatorFeed.Build(m);
            Assert.That(s.VisibleThroughWave, Is.EqualTo(Math.Max(0, w - 2)));
            Assert.That(s.Events.All(e => e.Wave <= w - 2 || e.Wave == 0), Is.True);
            Assert.That(s.Events.Any(e => e.Wave == w - 1 && w - 1 > 0), Is.False, "the last completed wave is still hidden");
            foreach (WavePair p in m.CurrentPlan.Pairs) FourKit.PlayDuel(m, p.First, null);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => SpectatorFeed.Build(m, 0), "delay cannot be removed");
    }

    [Test]
    public void Feed_NeverContainsUnrevealedLocks()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        for (int w = 1; w <= 3; w++)
            foreach (WavePair p in m.CurrentPlan.Pairs) FourKit.PlayDuel(m, p.First, null);
        // Wave 4 in progress: lock something distinctive.
        string before = Text(SpectatorFeed.Build(m));
        var secret = new VolleyInput(19, FourKit.Hit(PlayerSide.A).PitchQdeg, 3, 77, Dodge.Jump);
        Assert.That(m.Submit(new Lock4P(m.CurrentPlan.Pairs[0].First, 4, 1, secret)).Accepted, Is.True);
        string after = Text(SpectatorFeed.Build(m));
        Assert.That(after, Is.EqualTo(before), "a lock changes nothing a spectator can see");
        // The engine's whole public log has no lock or ready-flag event at all.
        Assert.That(m.PublicEvents.All(e => SpectatorFeed.IsPublicType(e.Type)), Is.True);
        Assert.That(Enum.GetNames<FourPlayerEventType>().Any(n => n.Contains("Lock") || n.Contains("Ready")), Is.False);
    }

    [Test]
    public void FinishedMatch_ShowsEverythingPublicIncludingTheSeed()
    {
        FourPlayerMatch m = FourPlayerBotRunner.Run(FourPlayerConfig.Starter, FourKit.Entrants, FourKit.Seed(9), FourKit.MatchId(9));
        SpectatorSnapshot s = SpectatorFeed.Build(m);
        Assert.That(s.MatchFinished, Is.True);
        Assert.That(s.Events.Count, Is.EqualTo(m.PublicEvents.Count));
        Assert.That(s.Events.Last().Type, Is.EqualTo(FourPlayerEventType.MatchFinished));
        Assert.That(s.Board, Is.EqualTo(m.SettledBoard(m.SettledWave)));
    }

    [Test]
    public void SpectatorBoard_IsTheSettledBoardOfTheVisibleWave()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        FourKit.PlayDuel(m, Kingdom.A, Kingdom.A);
        m.Submit(FourKit.AutoCut(m, Kingdom.A, FourKit.Interior(Kingdom.B)));
        FourKit.PlayDuel(m, Kingdom.C, null);
        // Wave 1 settled with a transfer, but spectators still see the starting board.
        SpectatorSnapshot s = SpectatorFeed.Build(m);
        Assert.That(s.Board, Is.EqualTo(m.SettledBoard(0)));
        Assert.That(s.Board, Is.Not.EqualTo(m.SettledBoard(1)));
    }
}
