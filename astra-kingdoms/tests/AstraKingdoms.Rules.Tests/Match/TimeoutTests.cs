using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Tests.Match;

public class TimeoutTests
{
    private static Harness Started(int seed = 1)
    {
        var h = Harness.FullPlain(seed);
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        return h;
    }

    [Test]
    public void SelectionTimeout_BecomesServerPass()
    {
        var h = Started();
        h.Volley(MatchKit.ThunderHit(PlayerSide.A), null);
        VolleyRecord v = h.E.Rounds[0].Volleys[0];
        Assert.That(v.TimeoutB, Is.True);
        Assert.That(v.WeaponB, Is.EqualTo(RulesConstants.PassWeaponId));
        Assert.That(v.HpB, Is.EqualTo(10000 - 3800), "a Pass is Neutral: Thunder Crown deals plain 38");
        Assert.That(h.View(PlayerSide.B).Self.ConsecutiveTimeouts, Is.EqualTo(1));
        Assert.That(h.E.Events.Any(e => e.Type == MatchEventType.SelectionTimeout && e.Player == PlayerSide.B), Is.True);
    }

    [Test]
    public void TwoConsecutiveTimeouts_ForfeitAcrossADuelBoundary()
    {
        var h = Started();
        h.Volley(MatchKit.Miss, MatchKit.Miss);
        h.Volley(MatchKit.Miss, MatchKit.Miss);
        h.Volley(MatchKit.Miss, null); // B: streak 1 in round 1 volley 3
        Assert.That(h.E.CurrentDuel.Result, Is.EqualTo(DuelResult.Draw));
        h.FinishDuel();
        Assert.That(h.E.RoundIndex, Is.EqualTo(2));
        h.Volley(MatchKit.Miss, null); // B: streak 2 in round 2 volley 1

        MatchResult r = h.E.Result!;
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.MatchOver));
        Assert.That(r.Reason, Is.EqualTo(TerminalReason.Forfeit));
        Assert.That(r.ForfeitedBy, Is.EqualTo(PlayerSide.B));
        Assert.That(r.Winner, Is.EqualTo(PlayerSide.A));
        Assert.That(r.CountsAsCompleted, Is.False, "a forfeit is not a normally completed match");
        Assert.That(r.CellsA, Is.EqualTo(25520), "no land transfer is invented");
        Assert.That(h.E.Rounds[1].Volleys, Is.Empty, "the forfeiting volley does not resolve");
    }

    [Test]
    public void ValidLockResetsTheStreak()
    {
        var h = Started();
        h.Volley(MatchKit.Miss, null);           // B streak 1
        h.Volley(MatchKit.Miss, MatchKit.Miss);  // B locks: streak 0
        h.Volley(MatchKit.Miss, null);           // B streak 1 again
        Assert.That(h.E.IsOver, Is.False);
        h.FinishDuel();
        Assert.That(h.View(PlayerSide.B).Self.ConsecutiveTimeouts, Is.EqualTo(1));
    }

    [Test]
    public void BothReachForfeitInTheSameDeadline_IsVoid()
    {
        var h = Started();
        h.Volley(null, null);
        Assert.That(h.E.IsOver, Is.False);
        h.Volley(null, null);
        MatchResult r = h.E.Result!;
        Assert.That(r.Reason, Is.EqualTo(TerminalReason.Void));
        Assert.That(r.IsVoid, Is.True);
        Assert.That(r.Winner, Is.Null);
        Assert.That(r.WinnerRewardEligible, Is.False);
        Assert.That(r.CountsAsCompleted, Is.False);
    }

    [Test]
    public void OneAtTwoOtherAtOne_IsAForfeitNotVoid()
    {
        var h = Started();
        h.Volley(MatchKit.Miss, null); // B 1
        h.Volley(null, null);          // A 1, B 2
        Assert.That(h.E.Result!.Reason, Is.EqualTo(TerminalReason.Forfeit));
        Assert.That(h.E.Result.ForfeitedBy, Is.EqualTo(PlayerSide.B));
    }

    [Test]
    public void CutTimeout_TransfersZero_AdvancesTheRound_AndIsNotASelectionTimeout()
    {
        var h = Started();
        h.Volley(MatchKit.Miss, null); // B streak 1 before the cut window
        h.Volley(MatchKit.ThunderHit(PlayerSide.A), MatchKit.Miss);
        h.Volley(MatchKit.ThunderHit(PlayerSide.A), MatchKit.Miss);
        Assert.That(h.E.CurrentDuel.Winner, Is.EqualTo(PlayerSide.A));
        h.FinishDuel();
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.CardAndCut));
        PlayerSide attacker = h.E.Attacker;

        Assert.That(h.Advance().Accepted, Is.True); // cut window expires
        RoundRecord r1 = h.E.Rounds[0];
        Assert.That(r1.CutTimedOut, Is.True);
        Assert.That(r1.CellsTransferred, Is.Zero);
        Assert.That(h.E.Cells(PlayerSide.A), Is.EqualTo(25520));
        Assert.That(h.E.MapRevision, Is.Zero);
        Assert.That(h.E.RoundIndex, Is.EqualTo(2));
        Assert.That(h.E.Attacker, Is.Not.EqualTo(attacker), "attacker alternates after a zero cut");
        Assert.That(h.View(PlayerSide.A).Self.ConsecutiveTimeouts, Is.Zero, "the cut timeout is not a selection timeout");
        Assert.That(h.View(PlayerSide.B).Self.ConsecutiveTimeouts, Is.Zero, "B's later valid locks reset its streak");
    }
}
