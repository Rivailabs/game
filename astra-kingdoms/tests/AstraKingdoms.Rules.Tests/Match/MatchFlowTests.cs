using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Tests.Match;

public class MatchFlowTests
{
    [Test]
    public void Setup_InitiativeFromSeed_MatchesGoldenVector()
    {
        // Zero seed: initiative round 0 gives index 1 for k = 2, so B attacks first.
        var engine = MatchEngine.Create(MatchConfig.Pilot(), new byte[32], MatchKit.MatchId(1));
        Assert.That(engine.FirstAttacker, Is.EqualTo(PlayerSide.B));
        Assert.That(engine.InitiativeStreamCounter, Is.EqualTo(1u));
        Assert.That(engine.Phase, Is.EqualTo(MatchPhase.Setup));
        Assert.That(engine.SeedCommitment, Is.EqualTo(SeededStream.SeedCommitment(RulesBundle.Hash, new byte[32], MatchKit.MatchId(1))));
        Assert.That(engine.DisclosedSeed, Is.Null, "the seed stays secret until the match ends");
    }

    [Test]
    public void Setup_BothLoadoutsStartRoundOneWithAnnouncedTerrain()
    {
        var h = new Harness(MatchConfig.Pilot());
        ulong rev = h.E.StateRevision;
        Assert.That(h.Loadout(PlayerSide.A, 1, 2, 3).Accepted, Is.True);
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.Setup));
        Assert.That(h.E.StateRevision, Is.EqualTo(rev), "a private loadout does not republish the setup snapshot");
        Assert.That(h.Loadout(PlayerSide.B, 4, 5).Accepted, Is.True);
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.TerrainAnnounce));
        Assert.That(h.E.RoundIndex, Is.EqualTo(1));
        Assert.That(h.E.Attacker, Is.EqualTo(h.E.FirstAttacker));
        Assert.That(h.E.CurrentFrontier.Terrain, Is.EqualTo(TerrainType.Plain), "pilot uses the plain template");
        // The frontier cell is defender-owned and touches attacker land.
        Assert.That(h.E.GetView(PlayerSide.A).DuelTerrain, Is.EqualTo(TerrainType.Plain));
    }

    [Test]
    public void Setup_IllegalLoadoutRejectedWithRulesCode()
    {
        var h = new Harness(MatchConfig.Pilot());
        Assert.That(h.Loadout(PlayerSide.A).RejectCode, Is.EqualTo("LOADOUT_EMPTY"));
        Assert.That(h.Loadout(PlayerSide.A, 1, 6).RejectCode, Is.EqualTo("LOADOUT_NOT_IN_CATALOG"));
        Assert.That(h.Loadout(PlayerSide.A, 1, 1).RejectCode, Is.EqualTo("LOADOUT_DUPLICATE"));
        Assert.That(h.Loadout(PlayerSide.A, 1).Accepted, Is.True);
        Assert.That(h.Loadout(PlayerSide.A, 2).RejectCode, Is.EqualTo(MatchErrors.LoadoutAlreadySubmitted));
    }

    [Test]
    public void Config_RejectsIllegalCombinations()
    {
        Assert.Throws<RulesViolationException>(() => new MatchConfig(MatchMode.Online, CatalogPreset.Starter, CardOfferRule.V1, TerrainTemplates.FullId));
        Assert.Throws<RulesViolationException>(() => new MatchConfig(MatchMode.Online, CatalogPreset.Full, CardOfferRule.Pilot, TerrainTemplates.FullId));
        Assert.Throws<RulesViolationException>(() => new MatchConfig(MatchMode.Online, CatalogPreset.Starter, CardOfferRule.V1, TerrainTemplates.PlainId, brahmastraEnabled: true));
        Assert.Throws<RulesViolationException>(() => new MatchConfig(MatchMode.Online, CatalogPreset.Full, CardOfferRule.V1, TerrainTemplates.FullId, false, "AK-TR-2"));
        Assert.DoesNotThrow(() => MatchConfig.V1Full(brahmastraEnabled: true));
    }

    [Test]
    public void MissShot_IsAMissForBothSeats()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        h.Volley(MatchKit.Miss, MatchKit.Miss);
        Assert.That(h.E.CurrentDuel.HpUnits(PlayerSide.A), Is.EqualTo(10000));
        Assert.That(h.E.CurrentDuel.HpUnits(PlayerSide.B), Is.EqualTo(10000));
    }

    [Test]
    public void DrawnDuel_NoCardNoTransfer_AttackerAlternates()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        PlayerSide first = h.E.Attacker;
        h.DrawRound();
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.TerrainAnnounce), "a draw skips the cut phase");
        Assert.That(h.E.RoundIndex, Is.EqualTo(2));
        Assert.That(h.E.Attacker, Is.EqualTo(Board.Opponent(first)), "attacker alternates after a draw");
        RoundRecord r1 = h.E.Rounds[0];
        Assert.That(r1.DuelResult, Is.EqualTo(DuelResult.Draw));
        Assert.That(r1.OfferedCards, Is.Empty);
        Assert.That(r1.CellsTransferred, Is.Zero);
        Assert.That(r1.MapRevision, Is.Zero);
    }

    [Test]
    public void EightDrawnRounds_EqualTerritory_IsAMatchDraw()
    {
        var h = Harness.FullPlain(3);
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        for (int r = 1; r <= 8; r++)
        {
            Assert.That(h.E.RoundIndex, Is.EqualTo(r));
            Assert.That(h.E.Attacker, Is.EqualTo(h.E.AttackerOf(r)));
            h.DrawRound();
        }
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.MatchOver));
        MatchResult result = h.E.Result!;
        Assert.That(result.Reason, Is.EqualTo(TerminalReason.RoundsComplete));
        Assert.That(result.IsDraw, Is.True);
        Assert.That(result.Winner, Is.Null);
        Assert.That(result.CellsA, Is.EqualTo(25520));
        Assert.That(result.CellsB, Is.EqualTo(25520));
        Assert.That(result.CountsAsCompleted, Is.True);
        Assert.That(result.WinnerRewardEligible, Is.False);
        // Attackers alternate A/B every round, including draws.
        for (int i = 1; i < 8; i++) Assert.That(h.E.Rounds[i].Attacker, Is.Not.EqualTo(h.E.Rounds[i - 1].Attacker));
        Assert.That(h.E.DisclosedSeed, Is.EqualTo(MatchKit.Seed(3)), "the seed is disclosed after settlement");
    }

    [Test]
    public void WonDuel_OffersThreeV1Cards_AndQuotaFollowsFormula()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        h.WinRound(PlayerSide.A);
        PlayerView view = h.View(PlayerSide.A);
        Assert.That(view.IsCutTurn, Is.True);
        Assert.That(view.HpDifferenceUnits, Is.EqualTo(10000));
        Assert.That(view.OfferedCards, Has.Count.EqualTo(3));
        Assert.That(view.OfferedCards, Is.Unique);
        CardOffer expected = CardOffers.V1(MatchKit.Seed(1), 1, 10000);
        Assert.That(view.OfferedCards, Is.EqualTo(expected.Cards));
        for (int i = 0; i < 3; i++)
            Assert.That(view.OfferedQuotas[i], Is.EqualTo(LandQuota.Compute(25520, 10000, view.OfferedCards[i])));
        Assert.That(h.View(PlayerSide.B).IsCutTurn, Is.False);
    }

    [Test]
    public void PilotOffer_IsChakraAndSuchi()
    {
        var h = new Harness(MatchConfig.Pilot(), 5);
        h.Loadouts(new[] { 1 }, new[] { 1 });
        // Pilot has no Thunder Crown: script a KO with Pass timeouts is impossible (forfeit), so win on HP:
        // A hits with an aimed Ember while B misses, three volleys.
        var aim = AimSolver.Solve(1, PlayerSide.A, Fixed.Zero, Fixed.Zero);
        var hit = new VolleyInput(1, aim.PitchQdeg, aim.YawQdeg, 100, Dodge.None);
        for (int v = 0; v < 3; v++) h.Volley(hit, MatchKit.Miss);
        h.FinishDuel();
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.CardAndCut));
        Assert.That(h.View(PlayerSide.A).OfferedCards, Is.EqualTo(new[] { CardId.Chakra, CardId.Suchi }));
    }

    [Test]
    public void AcceptedCut_TransfersExactlyPreviewedCells_AndConservesArea()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        h.WinRound(PlayerSide.B);
        SubmitCutCommand cut = h.PlannedCut(PlayerSide.B);
        CutResult preview = h.E.PreviewCut(PlayerSide.B, cut)!;
        Assert.That(preview.IsAccepted, Is.True);
        CommandReceipt r = h.E.Submit(PlayerSide.B, cut);
        Assert.That(r.Accepted, Is.True, r.ToString());
        Assert.That(r.CellsTransferred, Is.EqualTo(preview.Cells.Count));
        Assert.That(r.CellsTransferred, Is.LessThanOrEqualTo(h.E.Rounds[0].HpDifference > 0 ? preview.Quota : 0));
        Assert.That(h.E.Cells(PlayerSide.B), Is.EqualTo(25520 + preview.Cells.Count));
        Assert.That(h.E.Cells(PlayerSide.A) + h.E.Cells(PlayerSide.B), Is.EqualTo(51040));
        Assert.That(h.E.MapRevision, Is.EqualTo(1UL));
        Assert.That(h.E.RoundIndex, Is.EqualTo(2));
        Assert.That(h.E.Attacker, Is.EqualTo(h.E.AttackerOf(2)));
    }

    [Test]
    public void LoserOrUnofferedCardCannotCut()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        h.WinRound(PlayerSide.A);
        SubmitCutCommand cut = h.PlannedCut(PlayerSide.A);
        Assert.That(h.E.Submit(PlayerSide.B, cut).RejectCode, Is.EqualTo(MatchErrors.NotDuelWinner));
        CardId missing = Cards.All.First(c => !h.View(PlayerSide.A).OfferedCards.Contains(c));
        var other = new SubmitCutCommand(h.View(PlayerSide.A).NewHeader(h.Req()), h.E.MapRevision, missing, cut.AnchorCellId,
            cut.CenterX, cut.CenterY, cut.Rotation, cut.ScaleQuarters, CutMode.Auto);
        Assert.That(h.E.Submit(PlayerSide.A, other).RejectCode, Is.EqualTo(MatchErrors.CardNotOffered));
        var withVertices = new SubmitCutCommand(h.View(PlayerSide.A).NewHeader(h.Req()), h.E.MapRevision, cut.CardId, cut.AnchorCellId,
            cut.CenterX, cut.CenterY, cut.Rotation, cut.ScaleQuarters, CutMode.Auto, new[] { new CellPoint(1, 1) });
        Assert.That(h.E.Submit(PlayerSide.A, withVertices).RejectCode, Is.EqualTo(MatchErrors.AutoHasVertices));
    }

    [Test]
    public void InvalidManualCut_StaysEditable_ThenALegalCutIsAccepted()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        h.WinRound(PlayerSide.A);
        SubmitCutCommand auto = h.PlannedCut(PlayerSide.A);
        // A self-crossing bow-tie stroke.
        var bowTie = new[] { new CellPoint(100, 100), new CellPoint(140, 140), new CellPoint(140, 100), new CellPoint(100, 140) };
        var manual = new SubmitCutCommand(h.View(PlayerSide.A).NewHeader(h.Req()), h.E.MapRevision, auto.CardId, auto.AnchorCellId,
            auto.CenterX, auto.CenterY, auto.Rotation, auto.ScaleQuarters, CutMode.Manual, bowTie);
        CommandReceipt bad = h.E.Submit(PlayerSide.A, manual);
        Assert.That(bad.Accepted, Is.False);
        Assert.That(bad.RejectCode, Does.StartWith(MatchErrors.CutPrefix));
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.CardAndCut), "an invalid cut keeps the window open");
        Assert.That(h.E.MapRevision, Is.Zero);
        Assert.That(h.E.Submit(PlayerSide.A, auto).Accepted, Is.True);
    }

    [Test]
    public void Territory90_EndsTheMatchImmediatelyAfterTheCut()
    {
        var h = Harness.FullPlain(9);
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        while (!h.E.IsOver)
        {
            Assert.That(h.E.RoundIndex, Is.LessThanOrEqualTo(8));
            h.WinRound(PlayerSide.A);
            CommandReceipt r = h.E.Submit(PlayerSide.A, h.PlannedCut(PlayerSide.A));
            Assert.That(r.Accepted, Is.True, r.ToString());
            Assert.That(h.E.Cells(PlayerSide.A) + h.E.Cells(PlayerSide.B), Is.EqualTo(51040));
            if (h.E.Cells(PlayerSide.A) < RulesConstants.VictoryCells) Assert.That(h.E.IsOver, Is.False);
        }
        MatchResult result = h.E.Result!;
        Assert.That(result.Reason, Is.EqualTo(TerminalReason.Territory90));
        Assert.That(result.Winner, Is.EqualTo(PlayerSide.A));
        Assert.That(result.CellsA, Is.GreaterThanOrEqualTo(45936));
        Assert.That(result.RoundsPlayed, Is.LessThan(8), "the shortcut is reachable before round eight");
        Assert.That(h.E.Rounds.Count, Is.EqualTo(result.RoundsPlayed), "no further round started");
        Assert.That(h.E.Rounds[^1].StateHashHex, Is.Not.Null);
    }

    [Test]
    public void StateRevision_ChangesOnlyOnPhaseTransitions()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        h.ToSelection();
        ulong rev = h.E.StateRevision;
        Assert.That(h.Lock(PlayerSide.A, MatchKit.Miss).Accepted, Is.True);
        Assert.That(h.E.StateRevision, Is.EqualTo(rev), "accepting one private lock does not republish the snapshot");
        Assert.That(h.Lock(PlayerSide.B, MatchKit.Miss).Accepted, Is.True);
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.Resolution));
        Assert.That(h.E.StateRevision, Is.EqualTo(rev + 1));
    }

    [Test]
    public void Events_ArePublicAndSequenced()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        h.Volley(MatchKit.Miss, MatchKit.Miss);
        IReadOnlyList<MatchEvent> events = h.E.Events;
        for (int i = 0; i < events.Count; i++) Assert.That(events[i].Sequence, Is.EqualTo(i + 1));
        Assert.That(events.Count(e => e.Type == MatchEventType.PlayerLocked), Is.EqualTo(2));
        Assert.That(events.Single(e => e.Type == MatchEventType.VolleyResolved).Amount, Is.EqualTo(10000));
        Assert.That(h.E.EventsAfter(events[^2].Sequence), Has.Count.EqualTo(1), "reconnect receives events after its acknowledged sequence");
    }
}
