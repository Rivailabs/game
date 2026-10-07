using System.Collections.Concurrent;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Tests.Match;

public class CommandContractTests
{
    private static Harness InSelection(int seed = 1)
    {
        var h = Harness.FullPlain(seed);
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        h.ToSelection();
        return h;
    }

    [Test]
    public void HeaderChecks_RejectWithoutChangingState()
    {
        var h = InSelection();
        PlayerView v = h.View(PlayerSide.A);
        ulong rev = h.E.StateRevision;
        CommandHeader good = v.NewHeader(h.Req());

        CommandReceipt Try(CommandHeader header) => h.E.Submit(PlayerSide.A, new LockInputCommand(header, 1, MatchKit.Miss));

        Assert.That(Try(new CommandHeader(good.RulesHash, good.MatchId, h.Req(), 1, rev, schemaVersion: 2)).RejectCode, Is.EqualTo(MatchErrors.SchemaVersion));
        Assert.That(Try(new CommandHeader(new byte[32], good.MatchId, h.Req(), 1, rev)).RejectCode, Is.EqualTo(MatchErrors.RulesHash));
        Assert.That(Try(new CommandHeader(good.RulesHash, MatchKit.MatchId(99), h.Req(), 1, rev)).RejectCode, Is.EqualTo(MatchErrors.MatchId));
        Assert.That(Try(new CommandHeader(good.RulesHash, good.MatchId, "NOT-A-UUID", 1, rev)).RejectCode, Is.EqualTo(MatchErrors.RequestId));
        Assert.That(Try(new CommandHeader(good.RulesHash, good.MatchId, h.Req(), 2, rev)).RejectCode, Is.EqualTo(MatchErrors.WrongRound));
        Assert.That(Try(new CommandHeader(good.RulesHash, good.MatchId, h.Req(), 1, rev - 1)).RejectCode, Is.EqualTo(MatchErrors.StaleStateRevision));
        Assert.That(h.View(PlayerSide.A).OwnLock, Is.Null);
        Assert.That(h.E.StateRevision, Is.EqualTo(rev));
        Assert.That(h.E.CommandLog.Count, Is.EqualTo(2 + 1), "two loadouts and one announcement advance only");
    }

    [Test]
    public void InvalidLock_IsRejectedNotConverted_PlayerMayRetry()
    {
        var h = InSelection();
        Assert.That(h.Lock(PlayerSide.A, new VolleyInput(1, 999, 0, 100, Dodge.None)).RejectCode, Is.EqualTo(InputValidator.PitchRange));
        Assert.That(h.Lock(PlayerSide.A, new VolleyInput(2, 0, 0, 100, Dodge.None)).RejectCode, Is.EqualTo(InputValidator.WeaponNotEquipped));
        Assert.That(h.Lock(PlayerSide.A, new VolleyInput(1, 0, 0, 69, Dodge.None)).RejectCode, Is.EqualTo(InputValidator.PowerRange));
        Assert.That(h.Lock(PlayerSide.A, VolleyInput.Pass()).RejectCode, Is.EqualTo(InputValidator.PassServerOnly));
        Assert.That(h.Lock(PlayerSide.A, VolleyInput.Brahmastra()).RejectCode, Is.EqualTo(InputValidator.BrahmastraDisabled));
        Assert.That(h.Lock(PlayerSide.A, MatchKit.Miss).Accepted, Is.True);
    }

    [Test]
    public void LockDuringAnnouncement_IsWrongPhase()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.TerrainAnnounce));
        Assert.That(h.Lock(PlayerSide.A, MatchKit.Miss).RejectCode, Is.EqualTo(MatchErrors.WrongPhase));
    }

    [Test]
    public void DuplicateLock_SameRequestAndPayload_ReturnsTheSameReceipt()
    {
        var h = InSelection();
        LockInputCommand cmd = h.LockCommand(PlayerSide.A, MatchKit.Miss);
        CommandReceipt first = h.E.Submit(PlayerSide.A, cmd);
        CommandReceipt again = h.E.Submit(PlayerSide.A, cmd);
        Assert.That(first.Accepted, Is.True);
        Assert.That(again, Is.SameAs(first));
        Assert.That(first.RulesHash, Is.EqualTo(RulesBundle.Hash));
        Assert.That(first.VolleyIndex, Is.EqualTo(1));
        Assert.That(first.RoundIndex, Is.EqualTo(1));

        // Same request ID, different payload: rejected; the first lock is immutable.
        var changed = new LockInputCommand(cmd.Header, 1, new VolleyInput(1, 0, 0, 100, Dodge.Left));
        Assert.That(h.E.Submit(PlayerSide.A, changed).RejectCode, Is.EqualTo(MatchErrors.RequestIdReused));
        // Same request ID from the other seat is also a different payload.
        Assert.That(h.E.Submit(PlayerSide.B, cmd).RejectCode, Is.EqualTo(MatchErrors.RequestIdReused));
        // A new request cannot replace an accepted lock.
        Assert.That(h.Lock(PlayerSide.A, new VolleyInput(1, 0, 0, 100, Dodge.Left)).RejectCode, Is.EqualTo(MatchErrors.AlreadyLocked));
        Assert.That(h.View(PlayerSide.A).OwnLock!.ToString(), Is.EqualTo(MatchKit.Miss.ToString()));
    }

    [Test]
    public void ConcurrentLocks_AgainstTheSamePublishedRevision_BothSucceed()
    {
        var h = InSelection();
        // Both clients read the same published snapshot before either submits.
        LockInputCommand a = h.LockCommand(PlayerSide.A, MatchKit.Miss);
        LockInputCommand b = h.LockCommand(PlayerSide.B, MatchKit.Miss);
        Assert.That(a.Header.ExpectedStateRevision, Is.EqualTo(b.Header.ExpectedStateRevision));
        var receipts = new CommandReceipt[2];
        Parallel.Invoke(
            () => receipts[0] = h.E.Submit(PlayerSide.A, a),
            () => receipts[1] = h.E.Submit(PlayerSide.B, b));
        Assert.That(receipts[0].Accepted && receipts[1].Accepted, Is.True, receipts[0] + " / " + receipts[1]);
        Assert.That(receipts[0].StateRevision, Is.EqualTo(receipts[1].StateRevision));
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.Resolution), "both locks resolve the volley exactly once");
        Assert.That(h.E.Rounds[0].Volleys, Has.Count.EqualTo(1));
    }

    [Test]
    public void StaleCommands_FromAnEarlierVolleyOrMapRevision_AreRejected()
    {
        var h = InSelection();
        LockInputCommand lateVolleyOneLock = h.LockCommand(PlayerSide.A, MatchKit.Miss);
        h.Volley(MatchKit.Miss, MatchKit.Miss); // volley 1 resolves with other locks
        h.ToSelection();
        Assert.That(h.E.VolleyIndex, Is.EqualTo(2));
        // The volley-one lock arrives late (new request ID so it is not a duplicate of anything).
        var late = new LockInputCommand(lateVolleyOneLock.Header.WithRequestId(h.Req()), 1, MatchKit.Miss);
        CommandReceipt r = h.E.Submit(PlayerSide.A, late);
        Assert.That(r.RejectCode, Is.EqualTo(MatchErrors.StaleStateRevision));
        Assert.That(h.View(PlayerSide.A).OwnLock, Is.Null, "current inputs are unchanged");

        // Stale map revision on a cut.
        h.Volley(MatchKit.ThunderHit(PlayerSide.A), MatchKit.Miss);
        h.Volley(MatchKit.ThunderHit(PlayerSide.A), MatchKit.Miss);
        h.FinishDuel();
        SubmitCutCommand cut = h.PlannedCut(PlayerSide.A);
        var stale = new SubmitCutCommand(cut.Header.WithRequestId(h.Req()), cut.ExpectedMapRevision + 1, cut.CardId, cut.AnchorCellId,
            cut.CenterX, cut.CenterY, cut.Rotation, cut.ScaleQuarters, cut.Mode);
        Assert.That(h.E.Submit(PlayerSide.A, stale).RejectCode, Is.EqualTo(MatchErrors.StaleMapRevision));
        Assert.That(h.E.Cells(PlayerSide.A), Is.EqualTo(25520), "ownership unchanged");
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.CardAndCut));
    }

    [Test]
    public void ConcurrentDuplicateCut_MutatesOwnershipOnce()
    {
        var h = InSelection();
        h.Volley(MatchKit.ThunderHit(PlayerSide.A), MatchKit.Miss);
        h.Volley(MatchKit.ThunderHit(PlayerSide.A), MatchKit.Miss);
        h.FinishDuel();
        SubmitCutCommand cut = h.PlannedCut(PlayerSide.A);
        int logBefore = h.E.CommandLog.Count;

        var receipts = new ConcurrentBag<CommandReceipt>();
        Parallel.For(0, 8, _ => receipts.Add(h.E.Submit(PlayerSide.A, cut)));

        Assert.That(receipts.All(r => r.Accepted), Is.True);
        Assert.That(receipts.Distinct().Count(), Is.EqualTo(1), "every copy returns the one original receipt");
        Assert.That(h.E.MapRevision, Is.EqualTo(1UL), "exactly one ownership mutation");
        Assert.That(h.E.CommandLog.Count, Is.EqualTo(logBefore + 1), "exactly one settlement in the log");
        int moved = receipts.First().CellsTransferred;
        Assert.That(h.E.Cells(PlayerSide.A), Is.EqualTo(25520 + moved));

        // A different cut after the window closed is stale rather than a second allowance.
        var second = new SubmitCutCommand(cut.Header.WithRequestId(h.Req()), h.E.MapRevision, cut.CardId, cut.AnchorCellId,
            cut.CenterX, cut.CenterY, cut.Rotation, cut.ScaleQuarters, cut.Mode);
        Assert.That(h.E.Submit(PlayerSide.A, second).Accepted, Is.False);
        Assert.That(h.E.Cells(PlayerSide.A), Is.EqualTo(25520 + moved));
    }

    [Test]
    public void DuplicateAdvance_CannotAdvanceTwice()
    {
        var h = Harness.FullPlain();
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        AdvancePhaseCommand adv = h.E.CreateAdvance(h.Req());
        CommandReceipt first = h.E.Advance(adv);
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.Selection));
        Assert.That(h.E.Advance(adv), Is.SameAs(first), "a retried timer returns the original result");
        // A fresh request against the old revision is stale: it cannot expire the new selection.
        var retry = new AdvancePhaseCommand(adv.Header.WithRequestId(h.Req()));
        Assert.That(h.E.Advance(retry).RejectCode, Is.EqualTo(MatchErrors.StaleStateRevision));
        Assert.That(h.E.Phase, Is.EqualTo(MatchPhase.Selection));
        // Players cannot send server commands.
        Assert.That(h.E.Submit(PlayerSide.A, h.E.CreateAdvance(h.Req())).Accepted, Is.False);
    }
}
