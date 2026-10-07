using AstraKingdoms.Client.Land;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Tests;

/// <summary>
/// The shared-phone host clock (ticket 5/9): deadlines from the plan's shared-phone column, private
/// sequential entry, the single handover, entry order alternation, pause rules, and full matches.
/// </summary>
public sealed class HostTests
{
    private static LocalMatchHost NewHost(SeatKind a, SeatKind b, MatchMode mode = MatchMode.SharedPhone, ulong seed = 1, HostTimings timings = null)
    {
        MatchFactory.ForAutoplay(seed, out byte[] s, out string id);
        var host = new LocalMatchHost(MatchConfig.Pilot(mode), s, id, a, b, BotDifficulty.Normal, MatchFactory.DeterministicRequestIds(seed), timings);
        host.Start();
        return host;
    }

    private static void SharedLoadouts(LocalMatchHost host)
    {
        for (int i = 0; i < 2; i++)
        {
            Assert.That(host.Stage, Is.EqualTo(HostStage.LoadoutReady));
            Assert.That(host.CanView(host.StageSide.Value), Is.False, "nothing private before the player taps ready");
            Assert.That(host.ConfirmReady(), Is.True);
            Assert.That(host.Stage, Is.EqualTo(HostStage.LoadoutEntry));
            Assert.That(host.SubmitLoadout(host.StageSide.Value, new[] { 1, 2, 3, 4, 5 }).Accepted, Is.True);
        }
    }

    private static VolleyInput Shot(PlayerView v) => new VolleyInput(v.OwnLoadout.Weapons[0], 80, 0, 100, Dodge.Left);

    [Test]
    public void SharedPhoneDeadlinesFollowThePlan()
    {
        LocalMatchHost host = NewHost(SeatKind.Human, SeatKind.Human);
        SharedLoadouts(host);
        Assert.That(host.Stage, Is.EqualTo(HostStage.TerrainAnnounce));
        Assert.That(host.StageSecondsRemaining, Is.EqualTo(2.0));
        host.Tick(1.99);
        Assert.That(host.Stage, Is.EqualTo(HostStage.TerrainAnnounce));
        host.Tick(0.01);
        Assert.That(host.Stage, Is.EqualTo(HostStage.EntryReady));
        Assert.That(host.StageSecondsRemaining, Is.EqualTo(12.0));
        PlayerSide first = host.StageSide.Value;

        host.Tick(3.0); // the first entrant's 12 s run on the privacy screen too
        Assert.That(host.ConfirmReady(), Is.True);
        Assert.That(host.Stage, Is.EqualTo(HostStage.Entry));
        Assert.That(host.StageSecondsRemaining, Is.EqualTo(9.0).Within(1e-9));
        Assert.That(host.SubmitLock(first, Shot(host.ViewFor(first))).Accepted, Is.True);

        Assert.That(host.Stage, Is.EqualTo(HostStage.Handover));
        Assert.That(host.StageSide, Is.EqualTo(Board.Opponent(first)));
        Assert.That(host.StageSecondsRemaining, Is.EqualTo(6.0));
        Assert.That(host.CanView(first), Is.False, "the first entrant's controls are hidden during handover");
        Assert.That(host.CanView(Board.Opponent(first)), Is.False);
        Assert.Throws<InvalidOperationException>(() => host.ViewFor(first));

        host.Tick(6.0); // handover expires: the second entry window opens anyway
        Assert.That(host.Stage, Is.EqualTo(HostStage.Entry));
        Assert.That(host.StageSecondsRemaining, Is.EqualTo(12.0));
        PlayerView second = host.ViewFor(Board.Opponent(first));
        Assert.That(second.OwnLock, Is.Null);
        Assert.That(second.OpponentLocked, Is.True, "only a ready flag is visible");
        Assert.That(host.SubmitLock(second.Viewer, Shot(second)).Accepted, Is.True);

        Assert.That(host.Stage, Is.EqualTo(HostStage.Resolution));
        Assert.That(host.StageSecondsRemaining, Is.EqualTo(2.5));
        Assert.That(host.LastResolved, Is.Not.Null);
        host.Tick(2.5);
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Selection).Or.EqualTo(MatchPhase.CardAndCut).Or.EqualTo(MatchPhase.TerrainAnnounce));
    }

    [Test]
    public void FirstEntrantTimeoutBecomesPassAtTheSharedDeadline()
    {
        LocalMatchHost host = NewHost(SeatKind.Human, SeatKind.Human);
        SharedLoadouts(host);
        host.Tick(2.0);
        PlayerSide first = host.StageSide.Value;
        host.Tick(12.0);
        Assert.That(host.Stage, Is.EqualTo(HostStage.Handover));
        Assert.Throws<InvalidOperationException>(() => host.SubmitLock(first, VolleyInput.Pass()), "a closed entry cannot be reopened");
        host.ConfirmReady();
        PlayerView second = host.ViewFor(Board.Opponent(first));
        Assert.That(host.SubmitLock(second.Viewer, Shot(second)).Accepted, Is.True);
        Assert.That(host.Stage, Is.EqualTo(HostStage.Resolution));
        VolleyResult r = host.Engine.GetVolleyResult(1, 1);
        Assert.That(r.Explanation[first].IsPass, Is.True);
        Assert.That(r.Explanation[second.Viewer].IsPass, Is.False);
    }

    [Test]
    public void EntryOrderAlternatesByVolley()
    {
        LocalMatchHost host = NewHost(SeatKind.Human, SeatKind.Human);
        SharedLoadouts(host);
        host.Tick(2.0);
        var firsts = new List<PlayerSide>();
        for (int v = 0; v < 3 && host.Engine.Phase == MatchPhase.Selection; v++)
        {
            firsts.Add(host.StageSide.Value);
            host.ConfirmReady();
            host.SubmitLock(host.StageSide.Value, Shot(host.ViewFor(host.StageSide.Value)));
            host.ConfirmReady();
            host.SubmitLock(host.StageSide.Value, new VolleyInput(1, 80, 0, 100, Dodge.Right));
            host.Tick(2.5);
        }
        Assert.That(firsts.Count, Is.GreaterThanOrEqualTo(2));
        for (int i = 1; i < firsts.Count; i++) Assert.That(firsts[i], Is.Not.EqualTo(firsts[i - 1]));
    }

    [Test]
    public void PauseOnlyInPractice()
    {
        LocalMatchHost shared = NewHost(SeatKind.Human, SeatKind.Human);
        Assert.That(shared.SetPaused(true), Is.False);
        LocalMatchHost practice = NewHost(SeatKind.Human, SeatKind.Bot, MatchMode.Practice);
        Assert.That(practice.Stage, Is.EqualTo(HostStage.LoadoutEntry), "no privacy screen against a bot");
        practice.SubmitLoadout(PlayerSide.A, new[] { 1, 2, 3 });
        Assert.That(practice.Stage, Is.EqualTo(HostStage.TerrainAnnounce));
        Assert.That(practice.SetPaused(true), Is.True);
        practice.Tick(100);
        Assert.That(practice.Stage, Is.EqualTo(HostStage.TerrainAnnounce), "a paused clock never advances");
        practice.SetPaused(false);
        practice.Tick(2.0);
        Assert.That(practice.Stage, Is.EqualTo(HostStage.Entry));
        Assert.That(practice.StageSide, Is.EqualTo(PlayerSide.A));
    }

    [Test]
    public void PracticeHumanWhoNeverLocksForfeitsAfterTwoTimeouts()
    {
        LocalMatchHost host = NewHost(SeatKind.Human, SeatKind.Bot, MatchMode.Practice);
        host.SubmitLoadout(PlayerSide.A, new[] { 1 });
        MatchResult ended = null;
        host.MatchEnded += r => ended = r;
        host.Tick(2.0);
        host.Tick(12.0);
        host.Tick(2.5);
        host.Tick(12.0);
        Assert.That(host.Stage, Is.EqualTo(HostStage.MatchOver));
        Assert.That(ended, Is.Not.Null);
        Assert.That(ended.Reason, Is.EqualTo(TerminalReason.Forfeit));
        Assert.That(ended.ForfeitedBy, Is.EqualTo(PlayerSide.A));
    }

    [Test]
    public void LargeTicksNeverSkipADeadline()
    {
        LocalMatchHost host = NewHost(SeatKind.Human, SeatKind.Human);
        SharedLoadouts(host);
        var stages = new List<HostStage>();
        host.StageChanged += s => stages.Add(s);
        host.Tick(2.0 + 12.0 + 6.0 + 12.0 + 1.0);
        Assert.That(stages, Is.EqualTo(new[] { HostStage.EntryReady, HostStage.Handover, HostStage.Entry, HostStage.Resolution }));
    }

    [Test]
    public void TwoBotMatchFinishesAndItsRecordReplays([Values(1UL, 2UL, 20261006UL)] ulong seed)
    {
        LocalMatchHost host = NewHost(SeatKind.Bot, SeatKind.Bot, seed: seed, timings: new HostTimings { BotCutDelaySeconds = 0 });
        int resolved = 0;
        host.VolleyResolved += _ => resolved++;
        for (int i = 0; i < 2000 && host.Stage != HostStage.MatchOver; i++) host.Tick(0.5);
        Assert.That(host.Stage, Is.EqualTo(HostStage.MatchOver));
        MatchResult result = host.Engine.Result;
        Assert.That(result.CellsA + result.CellsB, Is.EqualTo(RulesConstants.ActiveCells));
        Assert.That(resolved, Is.GreaterThan(0));
        ReplayReport replay = Replayer.Verify(MatchRecord.FromJson(host.ToRecord().ToJson()));
        Assert.That(replay.Success, Is.True, replay.ToString());
    }

    [Test]
    public void HumanWinnerCutsThroughPreviewAndSubmit()
    {
        // Practice: the human (A) aims well at a standing bot; play until A wins a duel, then cut.
        LocalMatchHost host = NewHost(SeatKind.Human, SeatKind.Bot, MatchMode.Practice, seed: 7, timings: new HostTimings { BotCutDelaySeconds = 0 });
        host.SubmitLoadout(PlayerSide.A, new[] { 1, 2, 3, 4, 5 });
        bool cut = false;
        for (int step = 0; step < 4000 && host.Stage != HostStage.MatchOver && !cut; step++)
        {
            if (host.Stage == HostStage.Entry && host.StageSide == PlayerSide.A)
            {
                PlayerView v = host.ViewFor(PlayerSide.A);
                AimSolution aim = AimSolver.Solve(4, PlayerSide.A, Fixed.FromRaw(v.Self.BaselineOffsetRightRaw), Fixed.FromRaw(v.Foe.BaselineOffsetRightRaw));
                Assert.That(host.SubmitLock(PlayerSide.A, new VolleyInput(4, aim.PitchQdeg, aim.YawQdeg, 100, Dodge.Left)).Accepted, Is.True);
                continue;
            }
            if (host.Stage == HostStage.CardAndCut && host.StageSide == PlayerSide.A)
            {
                PublicSnapshot s = host.Snapshot();
                Territory t = host.CloneTerritory();
                Assert.That(CutAssist.DefaultPose(t, PlayerSide.A, s.OfferedCards[0], s.OfferedQuotas[0], s.FrontierCellId, out CardPose pose), Is.True);
                int anchor = CutAssist.PickAnchor(t, PlayerSide.A, s.OfferedCards[0], pose, null);
                Assert.That(anchor, Is.GreaterThanOrEqualTo(0));
                CutResult preview = host.PreviewCut(PlayerSide.A, s.OfferedCards[0], pose, anchor, CutMode.Auto, null);
                Assert.That(preview.IsAccepted, Is.True, preview.ToString());
                Assert.That(preview.Cells.Count, Is.InRange(1, s.OfferedQuotas[0]));
                int before = s.CellsA;
                CommandReceipt r = host.SubmitCut(PlayerSide.A, s.OfferedCards[0], pose, anchor, CutMode.Auto, null);
                Assert.That(r.Accepted, Is.True, r.ToString());
                Assert.That(r.CellsTransferred, Is.EqualTo(preview.Cells.Count), "client preview and accepted geometry agree");
                Assert.That(host.Snapshot().CellsA, Is.EqualTo(before + preview.Cells.Count));
                cut = true;
                continue;
            }
            host.Tick(0.5);
        }
        Assert.That(cut, Is.True, "the scripted human never won a duel");
    }

    [Test]
    public void RematchStartsFromCleanState()
    {
        LocalMatchHost first = NewHost(SeatKind.Human, SeatKind.Human);
        SharedLoadouts(first);
        first.Tick(2.0);
        first.ConfirmReady();
        PlayerSide entrant = first.StageSide.Value;
        first.SubmitLock(entrant, Shot(first.ViewFor(entrant)));

        MatchFactory.NewLive(out byte[] seed, out string id);
        var second = new LocalMatchHost(MatchConfig.Pilot(), seed, id, SeatKind.Human, SeatKind.Human);
        second.Start();
        Assert.That(second.Engine.MatchId, Is.Not.EqualTo(first.Engine.MatchId));
        Assert.That(second.Stage, Is.EqualTo(HostStage.LoadoutReady));
        Assert.That(second.Engine.CommandLog, Is.Empty);
        Assert.That(second.Snapshot().CellsA, Is.EqualTo(RulesConstants.InitialCellsPerPlayer));
    }
}
