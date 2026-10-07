using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Tests.Match;

public class PrivateViewTests
{
    [Test]
    public void OpponentLoadoutAndUnrevealedLock_NeverReachTheView()
    {
        // Two matches identical except for A's private loadout and A's locked choice.
        var h1 = Harness.FullPlain(4);
        var h2 = Harness.FullPlain(4);
        h1.Loadouts(new[] { 1, 10, 19 }, MatchKit.DuelKit);
        h2.Loadouts(new[] { 1, 2, 19 }, MatchKit.DuelKit);
        Assert.That(h1.View(PlayerSide.B).ToCanonicalText(), Is.EqualTo(h2.View(PlayerSide.B).ToCanonicalText()));
        h1.ToSelection();
        h2.ToSelection();

        Assert.That(h1.Lock(PlayerSide.A, MatchKit.Miss).Accepted, Is.True);
        Assert.That(h2.Lock(PlayerSide.A, MatchKit.ThunderHit(PlayerSide.A)).Accepted, Is.True);

        PlayerView b1 = h1.View(PlayerSide.B), b2 = h2.View(PlayerSide.B);
        Assert.That(b1.OpponentLocked, Is.True, "a ready flag is the only lock information");
        Assert.That(b1.ToCanonicalText(), Is.EqualTo(b2.ToCanonicalText()), "B's view cannot depend on A's secret choice");
        Assert.That(b1.OwnLock, Is.Null);
        Assert.That(b1.OpponentLoadoutRevealed, Is.Null);
        Assert.That(h1.View(PlayerSide.A).OwnLock!.WeaponId, Is.EqualTo(1));
        Assert.That(h2.View(PlayerSide.A).OwnLock!.WeaponId, Is.EqualTo(19));
    }

    [Test]
    public void MistVeil_ConcealsNextVolleyFromOpponentUntilMatchEnd()
    {
        var h = Harness.FullPlain(6);
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        AimSolution veilAim = AimSolver.Solve(10, PlayerSide.A, Fixed.Zero, Fixed.Zero);
        Assert.That(veilAim.FullContacts, Is.GreaterThan(0));
        h.Volley(new VolleyInput(10, veilAim.PitchQdeg, veilAim.YawQdeg, 100, Dodge.None), MatchKit.Miss);
        Assert.That(h.E.CurrentDuel.HpUnits(PlayerSide.B), Is.EqualTo(10000 - 3000), "Varuna beats Agni: 20 x 1.5");

        h.Volley(MatchKit.Miss, MatchKit.Miss); // A's volley 2 is veiled from B
        RevealedChoice seenByB = h.View(PlayerSide.B).History[1].A;
        Assert.That(seenByB.Concealed, Is.True);
        Assert.That(seenByB.WeaponId, Is.EqualTo(-1));
        Assert.That(seenByB.Element, Is.EqualTo(Element.Neutral));
        Assert.That(seenByB.PitchQdeg, Is.Zero);
        Assert.That(seenByB.HpAfterUnits, Is.EqualTo(10000), "health stays truthful");
        Assert.That(h.E.IsConcealedFrom(PlayerSide.B, 1, 2), Is.True);
        RevealedChoice seenByA = h.View(PlayerSide.A).History[1].A;
        Assert.That(seenByA.Concealed, Is.False, "the owner sees their own controls");
        Assert.That(seenByA.WeaponId, Is.EqualTo(1));
        Assert.That(h.View(PlayerSide.B).History[0].A.WeaponId, Is.EqualTo(10), "the veiling volley itself is revealed");

        // End the match (B forfeits) and everything is disclosed.
        h.Volley(MatchKit.Miss, null);
        h.FinishDuel(); // A wins 100 v 70
        Assert.That(h.Advance().Accepted, Is.True); // A lets the cut window expire
        h.Volley(MatchKit.Miss, null);
        Assert.That(h.E.Result!.Reason, Is.EqualTo(TerminalReason.Forfeit));
        PlayerView after = h.View(PlayerSide.B);
        Assert.That(after.History[1].A.Concealed, Is.False);
        Assert.That(after.History[1].A.WeaponId, Is.EqualTo(1));
        Assert.That(after.OpponentLoadoutRevealed!.Weapons, Is.EqualTo(MatchKit.DuelKit));
        Assert.That(after.SeedHex, Is.Not.Null);
    }

    [Test]
    public void ViewsShowTruthfulPublicStatus()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        h.Volley(MatchKit.ThunderHit(PlayerSide.A), MatchKit.Miss);
        PlayerView b = h.View(PlayerSide.B);
        Assert.That(b.Self.HpUnits, Is.EqualTo(10000 - 7600));
        Assert.That(b.Foe.HpUnits, Is.EqualTo(10000));
        Assert.That(b.History[0].A.WeaponId, Is.EqualTo(19), "post-lock reveal discloses the weapon actually used");
        Assert.That(b.History[0].B.DirectDamageTakenUnits, Is.EqualTo(7600));
        Assert.That(b.CellsA + b.CellsB, Is.EqualTo(51040));
        Assert.That(b.SeedHex, Is.Null);
        Assert.That(b.SeedCommitmentHex, Has.Length.EqualTo(64));
    }
}
