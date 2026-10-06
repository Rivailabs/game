using AstraKingdoms.Modes.FourPlayer;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Modes.Tests.FourPlayer;

/// <summary>Plan rows "Land changes", "Draw", "Idle or absent", "Elimination", "Finish" and replay.</summary>
public class FourPlayerMatchTests
{
    [Test]
    public void Setup_AcceptsOnlySymmetricCatalogLoadoutsOncePerEntrant()
    {
        FourPlayerMatch m = FourKit.NewFull();
        Assert.That(m.Submit(new SubmitLoadout4P(Kingdom.A, new[] { 1, 1 })).Code, Is.EqualTo("LOADOUT_DUPLICATE"));
        Assert.That(m.Submit(new SubmitLoadout4P(Kingdom.A, FourKit.DuelKit)).Accepted, Is.True);
        Assert.That(m.Submit(new SubmitLoadout4P(Kingdom.A, FourKit.DuelKit)).Code, Is.EqualTo("ALREADY_SUBMITTED"));
        Assert.That(m.Submit(new Lock4P(Kingdom.A, 1, 1, FourKit.Miss)).Code, Is.EqualTo("WRONG_PHASE"));
        FourKit.SubmitLoadouts(m);
        Assert.That(m.Phase, Is.EqualTo(FourPlayerPhase.Wave));
        Assert.That(m.Wave, Is.EqualTo(1));
        Assert.That(string.Join("/", m.CurrentPlan.Pairs), Is.EqualTo("A-B/C-D"));
    }

    [Test]
    public void Locks_StayHiddenFromTheOpponentUntilBothLock()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        string before = m.GetView(Kingdom.B).ToCanonicalText();
        VolleyInput secret = FourKit.Hit(PlayerSide.A);
        Assert.That(m.Submit(new Lock4P(Kingdom.A, 1, 1, secret)).Accepted, Is.True);
        Assert.That(m.Submit(new Lock4P(Kingdom.A, 1, 1, FourKit.Miss)).Code, Is.EqualTo("ALREADY_LOCKED"));
        FourPlayerParticipantView b = m.GetView(Kingdom.B);
        Assert.That(b.OpponentLocked, Is.True);
        Assert.That(b.OwnLock, Is.Null);
        // The only difference in B's view is the ready flag.
        Assert.That(b.ToCanonicalText(), Is.EqualTo(before.Replace("oppLocked=False", "oppLocked=True")));
        Assert.That(m.GetView(Kingdom.A).OwnLock, Is.SameAs(secret));
        // Players in the other pair learn nothing about it.
        Assert.That(m.GetView(Kingdom.C).ToCanonicalText(), Does.Not.Contain(secret.ToString()));
        Assert.That(m.PublicEvents.Any(e => e.Type == FourPlayerEventType.VolleyResolved), Is.False, "nothing resolved yet, nothing revealed");
    }

    [Test]
    public void Wave_TransfersApplyTogetherOnlyAfterBothDuelsFinish()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        FourKit.PlayDuel(m, Kingdom.A, Kingdom.A);            // A beats B
        Assert.That(m.Submit(FourKit.AutoCut(m, Kingdom.A, FourKit.Interior(Kingdom.B))).Accepted, Is.True);
        // A's cut is committed but the board is frozen until C-D finishes.
        Assert.That(m.Cells(Kingdom.A), Is.EqualTo(12760));
        Assert.That(m.Cells(Kingdom.B), Is.EqualTo(12760));
        Assert.That(m.SettledWave, Is.Zero);

        FourKit.PlayDuel(m, Kingdom.C, Kingdom.D);             // D beats C
        Assert.That(m.Cells(Kingdom.D), Is.EqualTo(12760), "still frozen while D's cut is pending");
        Assert.That(m.Submit(FourKit.AutoCut(m, Kingdom.D, FourKit.Interior(Kingdom.C))).Accepted, Is.True);

        Assert.That(m.SettledWave, Is.EqualTo(1));
        FourPlayerWaveRecord w1 = m.WaveRecords[0];
        int gainA = w1.Pairs[0].CellsTransferred, gainD = w1.Pairs[1].CellsTransferred;
        Assert.That(gainA, Is.GreaterThan(0).And.LessThanOrEqualTo(2552));
        Assert.That(gainD, Is.GreaterThan(0).And.LessThanOrEqualTo(2552));
        Assert.That(m.Cells(Kingdom.A), Is.EqualTo(12760 + gainA));
        Assert.That(m.Cells(Kingdom.B), Is.EqualTo(12760 - gainA));
        Assert.That(m.Cells(Kingdom.D), Is.EqualTo(12760 + gainD));
        Assert.That(m.Cells(Kingdom.C), Is.EqualTo(12760 - gainD));
        FourKit.AssertConserved(m);
        Assert.That(m.Wave, Is.EqualTo(2));
        Assert.That(string.Join("/", m.CurrentPlan.Pairs), Is.EqualTo("A-C/B-D"));
    }

    [Test]
    public void Cut_IsValidatedAgainstTheFrozenBoardAndOnlyTheDefeatedOpponent()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        FourKit.PlayDuel(m, Kingdom.A, Kingdom.A);
        // Anchoring in C (not A's opponent) is rejected and changes nothing.
        SubmitCut4P wrong = FourKit.AutoCut(m, Kingdom.A, FourKit.Interior(Kingdom.B));
        var atC = CellPoint.FromCellId(FourKit.Interior(Kingdom.C));
        var bad = new SubmitCut4P(Kingdom.A, 1, wrong.Card, new CardPose(atC.X, atC.Y, wrong.Pose.ScaleQuarters, 0), atC);
        Assert.That(m.Submit(bad).Code, Is.EqualTo("CUT_" + CutRejection.AnchorNotOpponentOwned));
        Assert.That(m.Submit(new SubmitCut4P(Kingdom.B, 1, wrong.Card, wrong.Pose, wrong.Anchor)).Code, Is.EqualTo("NOT_CUT_TURN"));
        CardId notOffered = Cards.All.First(c => !m.GetView(Kingdom.A).OfferedCards.Contains(c));
        Assert.That(m.Submit(new SubmitCut4P(Kingdom.A, 1, notOffered, wrong.Pose, wrong.Anchor)).Code, Is.EqualTo("CARD_NOT_OFFERED"));
        Assert.That(m.Submit(wrong).Accepted, Is.True);
        Assert.That(m.Submit(wrong).Code, Is.EqualTo("NOT_CUT_TURN"), "one cut per won duel");
    }

    [Test]
    public void Draw_TransfersNothingButCompletesParticipation()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        FourKit.PlayDuel(m, Kingdom.A, null);
        FourKit.PlayDuel(m, Kingdom.C, null);
        FourPlayerWaveRecord w1 = m.WaveRecords.Single();
        Assert.That(w1.Pairs.All(p => p.DuelResult == DuelResult.Draw && p.CellsTransferred == 0), Is.True);
        foreach (Kingdom k in FourKit.All)
        {
            Assert.That(m.Cells(k), Is.EqualTo(12760));
            Assert.That(m.CompletedDuels(k), Is.EqualTo(1));
        }
        Assert.That(m.Wave, Is.EqualTo(2));
    }

    [Test]
    public void AllDrawsForSixWaves_EveryoneSharesFirstPlace()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        for (int w = 1; w <= 6; w++)
        {
            Assert.That(m.Wave, Is.EqualTo(w));
            foreach (WavePair p in m.CurrentPlan.Pairs) FourKit.PlayDuel(m, p.First, null);
        }
        Assert.That(m.IsFinished, Is.True);
        Assert.That(m.WaveRecords, Has.Count.EqualTo(6));
        Assert.That(m.Standings!.All(s => s.Place == 1 && s.Cells == 12760), Is.True, "identical areas share placement; no coin flip");
    }

    [Test]
    public void Finish_AfterWaveSixMostLandWinsAndExactTiesShare()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        // Wave 1: A beats B, C beats D by the same mirrored cut; everything else draws.
        FourKit.PlayDuel(m, Kingdom.A, Kingdom.A);
        m.Submit(FourKit.AutoCut(m, Kingdom.A, FourKit.Interior(Kingdom.B)));
        FourKit.PlayDuel(m, Kingdom.C, Kingdom.C);
        m.Submit(FourKit.AutoCut(m, Kingdom.C, FourKit.Interior(Kingdom.D)));
        FourPlayerWaveRecord w1 = m.WaveRecords[0];
        for (int w = 2; w <= 6; w++)
            foreach (WavePair p in m.CurrentPlan.Pairs) FourKit.PlayDuel(m, p.First, null);
        Assert.That(m.IsFinished, Is.True);
        var s = m.Standings!.ToDictionary(x => x.Kingdom);
        bool equalGains = w1.Pairs[0].CellsTransferred == w1.Pairs[1].CellsTransferred;
        Assert.That(s[Kingdom.A].Place, Is.EqualTo(1));
        Assert.That(s[Kingdom.C].Place, Is.EqualTo(equalGains ? 1 : s[Kingdom.C].Cells > s[Kingdom.A].Cells ? 1 : 2));
        Assert.That(s[Kingdom.B].Place, Is.GreaterThanOrEqualTo(3));
        Assert.That(s[Kingdom.D].Place, Is.GreaterThanOrEqualTo(3));
        FourKit.AssertConserved(m);
    }

    [Test]
    public void Forfeit_LocksLandAsNeutralWithNoWindfall()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        // D leaves mid-duel: C gains nothing from it.
        Assert.That(m.Submit(new Lock4P(Kingdom.C, 1, 1, FourKit.Hit(PlayerSide.A))).Accepted, Is.True);
        Assert.That(m.Submit(new Forfeit4P(Kingdom.D)).Accepted, Is.True);
        Assert.That(m.StageOf(1), Is.EqualTo(PairStage.Done));
        FourKit.PlayDuel(m, Kingdom.A, null);
        Assert.That(m.SettledWave, Is.EqualTo(1));
        Assert.That(m.Cells(Kingdom.D), Is.Zero);
        Assert.That(m.NeutralCells, Is.EqualTo(12760));
        Assert.That(m.Cells(Kingdom.C), Is.EqualTo(12760), "no automatic windfall");
        Assert.That(m.IsAlive(Kingdom.D), Is.False);
        Assert.That(m.WaveRecords[0].Eliminated, Is.EqualTo(new[] { Kingdom.D }));
        FourKit.AssertConserved(m);
        // Wave 2 has three survivors and one bye.
        Assert.That(m.CurrentPlan.Pairs, Has.Count.EqualTo(1));
        Assert.That(m.CurrentPlan.Bye, Is.Not.Null);
    }

    [Test]
    public void TwoConsecutiveTimeouts_Forfeit()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        // B never locks: two deadlines forfeit B. A's locks are kept and resolve against Pass.
        Assert.That(m.Submit(new Lock4P(Kingdom.A, 1, 1, FourKit.Miss)).Accepted, Is.True);
        Assert.That(m.Submit(new ExpireSelection4P(1, 0)).Accepted, Is.True);
        Assert.That(m.IsAlive(Kingdom.B), Is.True);
        Assert.That(m.Submit(new Lock4P(Kingdom.A, 1, 2, FourKit.Miss)).Accepted, Is.True);
        Assert.That(m.Submit(new ExpireSelection4P(1, 0)).Accepted, Is.True);
        Assert.That(m.StageOf(0), Is.EqualTo(PairStage.Done));
        FourKit.PlayDuel(m, Kingdom.C, null);
        Assert.That(m.IsAlive(Kingdom.B), Is.False);
        Assert.That(m.Standings, Is.Null);
        Assert.That(m.Cells(Kingdom.A), Is.EqualTo(12760));
        Assert.That(m.NeutralCells, Is.EqualTo(12760));
    }

    [Test]
    public void SetupDeadline_ForfeitsAbsentEntrantsAndNoBotReplacesThem()
    {
        FourPlayerMatch m = FourKit.NewFull();
        m.Submit(new SubmitLoadout4P(Kingdom.A, FourKit.DuelKit));
        m.Submit(new SubmitLoadout4P(Kingdom.B, FourKit.DuelKit));
        Assert.That(m.Submit(new ExpireSetup4P()).Accepted, Is.True);
        Assert.That(m.IsAlive(Kingdom.C), Is.False);
        Assert.That(m.IsAlive(Kingdom.D), Is.False);
        Assert.That(m.NeutralCells, Is.EqualTo(2 * 12760));
        Assert.That(m.Phase, Is.EqualTo(FourPlayerPhase.Wave));
        Assert.That(string.Join("/", m.CurrentPlan.Pairs), Is.EqualTo("A-B"), "two survivors duel");
    }

    [Test]
    public void SoleSurvivor_WinsImmediately()
    {
        FourPlayerMatch m = FourKit.NewFull();
        FourKit.SubmitLoadouts(m);
        m.Submit(new Forfeit4P(Kingdom.B));
        m.Submit(new Forfeit4P(Kingdom.C));
        // Both wave-1 pairs ended by forfeit: B and C are eliminated, A and D continue.
        Assert.That(m.IsFinished, Is.False);
        Assert.That(m.Wave, Is.EqualTo(2));
        Assert.That(string.Join("/", m.CurrentPlan.Pairs), Is.EqualTo("A-D"));
        m.Submit(new Forfeit4P(Kingdom.D));
        Assert.That(m.IsFinished, Is.True, "one survivor wins immediately");
        var s = m.Standings!.ToDictionary(x => x.Kingdom);
        Assert.That(s[Kingdom.A].Place, Is.EqualTo(1));
        Assert.That(s[Kingdom.D].Place, Is.EqualTo(2));
        Assert.That(s[Kingdom.B].Place, Is.EqualTo(3), "eliminated in the same wave share a place");
        Assert.That(s[Kingdom.C].Place, Is.EqualTo(3));
        Assert.That(m.Cells(Kingdom.A), Is.EqualTo(12760));
        Assert.That(m.NeutralCells, Is.EqualTo(3 * 12760));
        Assert.That(m.Submit(new Lock4P(Kingdom.A, 1, 1, FourKit.Miss)).Code, Is.EqualTo("MATCH_OVER"));
    }

    [Test]
    public void ZeroLand_EliminatesAndThreeSurvivorPairingFollows()
    {
        // Geometry fixture: D keeps only a 9x9 block; everything else of D belongs to C.
        FourOwnerTerritory board = FourOwnerTerritory.CreateEqualSectors();
        var keep = new HashSet<int>();
        for (int y = 186; y <= 194; y++)
            for (int x = 60; x <= 68; x++) keep.Add(Board.CellId(x, y));
        board.ApplyTransfers(new[] { new CellTransfer(Kingdom.D, Kingdom.C, board.CellsOwnedBy(Kingdom.D).Where(c => !keep.Contains(c)).ToList()) });
        Assert.That(board.CellCount(Kingdom.D), Is.EqualTo(81));

        FourPlayerMatch m = FourPlayerMatch.CreateWithBoard(FourPlayerConfig.Full, FourKit.Entrants, FourKit.Seed(5), FourKit.MatchId(5), board);
        FourKit.SubmitLoadouts(m);
        FourKit.PlayDuel(m, Kingdom.A, null);
        FourKit.PlayDuel(m, Kingdom.C, Kingdom.C);
        FourPlayerParticipantView cv = m.GetView(Kingdom.C);
        Assert.That(cv.OfferedQuotas.All(q => q == 81), Is.True, "capped at the opponent's area");
        Assert.That(m.Submit(FourKit.AutoCut(m, Kingdom.C, Board.CellId(64, 190))).Accepted, Is.True);

        Assert.That(m.Cells(Kingdom.D), Is.Zero);
        Assert.That(m.IsAlive(Kingdom.D), Is.False);
        Assert.That(m.Standings, Is.Null, "three survivors continue");
        FourKit.AssertConserved(m);
        WavePlan w2 = m.CurrentPlan;
        Assert.That(w2.Pairs, Has.Count.EqualTo(1));
        Assert.That(w2.Bye, Is.Not.Null);
        // A bye player is a spectator for the wave.
        FourPlayerParticipantView byeView = m.GetView(w2.Bye!.Value);
        Assert.That(byeView.IsSpectating, Is.True);
        Assert.That(byeView.InDuel, Is.False);
        Assert.That(m.GetView(Kingdom.D).Eliminated, Is.True);
    }

    [Test]
    public void BotMatches_FinishConserveAreaAndHonourTheCapEveryWave()
    {
        for (int n = 1; n <= 3; n++)
        {
            FourPlayerMatch m = FourPlayerBotRunner.Run(n == 1 ? FourPlayerConfig.Starter : FourPlayerConfig.Full, FourKit.Entrants,
                FourKit.Seed(100 + n), FourKit.MatchId(100 + n), BotDifficulty.Hard);
            Assert.That(m.IsFinished, Is.True);
            Assert.That(m.WaveRecords.Count, Is.InRange(1, 6));
            foreach (FourPlayerWaveRecord w in m.WaveRecords)
            {
                Assert.That(w.CellsAfter.Sum(), Is.EqualTo(51040), "wave " + w.Wave);
                Assert.That(w.Pairs.All(p => p.CellsTransferred <= FourPlayerRules.TransferCapCells), Is.True);
                Assert.That(w.Pairs.Count(p => p.Winner.HasValue) >= w.Pairs.Count(p => p.CellsTransferred > 0), Is.True);
            }
            FourKit.AssertConserved(m);
            Assert.That(m.Standings!.Min(s => s.Place), Is.EqualTo(1));
        }
    }

    [Test]
    public void Replay_OfTheCommandLogReproducesEveryWaveHash()
    {
        FourPlayerMatch original = FourPlayerBotRunner.Run(FourPlayerConfig.Full, FourKit.Entrants, FourKit.Seed(7), FourKit.MatchId(7));
        FourPlayerMatch replay = FourPlayerMatch.Create(FourPlayerConfig.Full, FourKit.Entrants, FourKit.Seed(7), FourKit.MatchId(7));
        foreach (FourPlayerCommand c in original.CommandLog) Assert.That(replay.Submit(c).Accepted, Is.True);
        Assert.That(replay.WaveRecords.Select(w => w.StateHashHex), Is.EqualTo(original.WaveRecords.Select(w => w.StateHashHex)));
        Assert.That(replay.Standings!.Select(s => s.ToString()), Is.EqualTo(original.Standings!.Select(s => s.ToString())));
        // And the same seed and bots give the same match again.
        FourPlayerMatch again = FourPlayerBotRunner.Run(FourPlayerConfig.Full, FourKit.Entrants, FourKit.Seed(7), FourKit.MatchId(7));
        Assert.That(again.WaveRecords.Select(w => w.StateHashHex), Is.EqualTo(original.WaveRecords.Select(w => w.StateHashHex)));
    }
}
