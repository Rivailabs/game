using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Server.Matches;
using Microsoft.Extensions.Time.Testing;

namespace AstraKingdoms.Server.Tests;

/// <summary>Ticket 51: private locks, deadlines and reveal. Late or duplicate inputs and disconnects cannot alter an accepted outcome.</summary>
[TestFixture]
public class LockAndDeadlineTests
{
    /// <summary>Both loadouts in, announcement over: the first selection is open and nobody has an autopilot.</summary>
    private static async Task<MatchHost> OpenSelection(ServerHarness h, TestPlayer a, TestPlayer b)
    {
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(a);
        Play.SubmitLoadout(b);
        await Play.ViewAt(host, a, MatchPhase.TerrainAnnounce); // published after the clock is armed
        await Play.ViewAt(host, b, MatchPhase.TerrainAnnounce);
        h.AdvanceToDeadline(host);
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Selection));
        await Play.ViewAt(host, a, MatchPhase.Selection);
        await Play.ViewAt(host, b, MatchPhase.Selection);
        return host;
    }

    [Test]
    public async Task ConcurrentLocksAreBothAcceptedAndResolveOnce()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await OpenSelection(h, a, b);

        LockInputCommand la = Play.LockFor(a.Match.View);
        LockInputCommand lb = Play.LockFor(b.Match.View, seed: 9);
        await Task.WhenAll(Task.Run(() => a.Match.SendCommand(la)), Task.Run(() => b.Match.SendCommand(lb)));

        await ServerHarness.Until(() => a.LockReceipts.Any() && b.LockReceipts.Any(), "both receipts");
        Assert.That(a.LockReceipts.Single().Accepted && b.LockReceipts.Single().Accepted, Is.True, "the first lock does not invalidate the second (same snapshot revision)");
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Resolution));
        Assert.That(host.Engine.GetView(PlayerSide.A).History, Has.Count.EqualTo(1), "resolved exactly once");
        RevealedVolley v = host.Engine.GetView(a.Match.LocalSide).History[0];
        Assert.That(v[a.Match.LocalSide].WeaponId, Is.EqualTo(la.WeaponId));
        Assert.That(v[b.Match.LocalSide].WeaponId, Is.EqualTo(lb.WeaponId));
    }

    [Test]
    public async Task DuplicateLockReturnsTheOriginalReceiptAndCannotBeReplaced()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await OpenSelection(h, a, b);

        string requestId = Guid.NewGuid().ToString("D");
        LockInputCommand first = Play.LockFor(a.Match.View, requestId);
        a.Client.SendRaw(ClientMessages.MatchCommand("rid-1", first));
        a.Client.SendRaw(ClientMessages.MatchCommand("rid-2", first)); // network retry: identical payload
        LockInputCommand changed = new(first.Header, first.VolleyIndex, first.WeaponId, first.PitchQdeg + 4, first.YawQdeg, first.PowerPercent, first.Dodge);
        a.Client.SendRaw(ClientMessages.MatchCommand("rid-3", changed)); // same request ID, different payload
        LockInputCommand second = Play.LockFor(a.Match.View, seed: 77);
        a.Client.SendRaw(ClientMessages.MatchCommand("rid-4", second)); // new request ID: a second lock

        await ServerHarness.Until(() => a.LockReceipts.Count() == 4, "four receipts");
        ReceiptMessage[] r = a.LockReceipts.ToArray();
        Assert.That(r[0].Accepted && r[1].Accepted, Is.True);
        Assert.That(r[1].InputRevision, Is.EqualTo(r[0].InputRevision), "the duplicate returns the same receipt");
        Assert.That(r[2].Code, Is.EqualTo(MatchErrors.RequestIdReused));
        Assert.That(r[3].Code, Is.EqualTo(MatchErrors.AlreadyLocked), "the first accepted lock is immutable");
        Assert.That(host.Engine.GetView(a.Match.LocalSide).OwnLock.PitchQdeg, Is.EqualTo(first.PitchQdeg));
        Assert.That(host.Engine.CommandLog.Count(c => c.Command is LockInputCommand), Is.EqualTo(1));

        // The opponent learns only that A is ready.
        await ServerHarness.Until(() => b.Match.View.OpponentLocked, "ready flag");
        Assert.That(b.Match.View.OwnLock, Is.Null);
        string bobTraffic = string.Join("\n", b.Messages.Select(m => m.ToCanonicalString()));
        Assert.That(bobTraffic, Does.Not.Contain(requestId), "A's request never reaches B");
        Assert.That(bobTraffic, Does.Not.Contain("\"pitch_qdeg\":" + first.PitchQdeg + ",\"yaw_qdeg\":" + first.YawQdeg));
    }

    [Test]
    public async Task LateLockAfterTheDeadlineIsRejectedAndThePassStands()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await OpenSelection(h, a, b);
        PlayerView staleB = b.Match.View;

        a.Match.SendCommand(Play.LockFor(a.Match.View));
        await ServerHarness.Until(() => a.LockReceipts.Any(), "A's receipt");
        h.AdvanceMs(12000);
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Resolution));

        b.Match.SendCommand(Play.LockFor(staleB));
        await ServerHarness.Until(() => b.LockReceipts.Any(), "B's receipt");
        Assert.That(b.LockReceipts.Single().Accepted, Is.False);
        RevealedChoice passB = host.Engine.GetView(PlayerSide.A).History[0][b.Match.LocalSide];
        Assert.That(passB.TimedOut, Is.True);
        Assert.That(passB.WeaponId, Is.EqualTo(RulesConstants.PassWeaponId));
    }

    /// <summary>A clock whose timers can be held back, to deliver a command after the deadline but before the timer ran.</summary>
    private sealed class StallingClock : FakeTimeProvider
    {
        public volatile bool Stall;
        public StallingClock() : base(ServerHarness.Epoch)
        {
        }

        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) =>
            Stall ? new NeverTimer() : base.CreateTimer(callback, state, dueTime, period);

        private sealed class NeverTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose()
            {
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task ReceiveTimeIsAuthoritativeEvenBeforeTheTimerRuns()
    {
        var clock = new StallingClock();
        await using ServerHarness h = ServerHarness.Start(time: clock);
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(a);
        Play.SubmitLoadout(b);
        await Play.ViewAt(host, a, MatchPhase.TerrainAnnounce); // published after the clock is armed
        await Play.ViewAt(host, b, MatchPhase.TerrainAnnounce);
        clock.Stall = true; // the selection timer created next never fires on its own
        h.AdvanceToDeadline(host);
        await Play.ViewAt(host, b, MatchPhase.Selection);

        h.AdvanceMs(12000); // deadline reached, timer silent
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Selection));
        b.Match.SendCommand(Play.LockFor(b.Match.View));
        await ServerHarness.Until(() => b.LockReceipts.Any(), "B's receipt");
        Assert.That(b.LockReceipts.Single().Accepted, Is.False, "a lock received at or after the deadline cannot beat the Pass");
        Assert.That(host.Engine.GetView(PlayerSide.A).History[0][b.Match.LocalSide].TimedOut, Is.True);
    }

    /// <summary>Like real system timers: fires a little before the requested time.</summary>
    private sealed class EarlyClock : FakeTimeProvider
    {
        public EarlyClock() : base(ServerHarness.Epoch)
        {
        }

        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, dueTime > TimeSpan.FromMilliseconds(5) ? dueTime - TimeSpan.FromMilliseconds(3) : dueTime, period);
    }

    [Test]
    public async Task ATimerFiringEarlyIsReArmedNotLost()
    {
        var clock = new EarlyClock();
        await using ServerHarness h = ServerHarness.Start(time: clock);
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(a);
        Play.SubmitLoadout(b);
        await Play.ViewAt(host, a, MatchPhase.TerrainAnnounce);
        h.AdvanceMs(1997); // the early timer fires 3 ms before the deadline
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.TerrainAnnounce), "not before the deadline");
        h.AdvanceMs(3);
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Selection), "the re-armed timer applies the deadline");
    }

    [Test]
    public async Task TwoConsecutiveTimeoutsForfeitOnlineWithoutReward()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice", BotDifficulty.Normal);
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(b);

        await Play.ToEnd(h, host, a, b);

        Assert.That(host.Engine.Result.Reason, Is.EqualTo(TerminalReason.Forfeit));
        Assert.That(host.Engine.Result.ForfeitedBy, Is.EqualTo(b.Match.LocalSide));
        Assert.That(host.Engine.Result.Winner, Is.EqualTo(a.Match.LocalSide));
        Assert.That(host.Outcome, Is.EqualTo(MatchOutcomes.Forfeit));
        Assert.That(h.Grants(host.ResultId), Is.Empty, "a forfeit is not a normally completed match");
        await ServerHarness.Until(() => b.Ends.Any(), "match.end");
        Assert.That(b.Ends.Single().Outcome, Is.EqualTo(MatchOutcomes.Forfeit));
        Assert.That(b.Ends.Single().Result.CellsA + b.Ends.Single().Result.CellsB, Is.EqualTo(RulesConstants.ActiveCells), "no invented land transfer");
    }

    [Test]
    public async Task BothTimingOutTwiceVoidsTheMatch()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(a);
        Play.SubmitLoadout(b);
        await Play.ToEnd(h, host, a, b);
        Assert.That(host.Engine.Result.Reason, Is.EqualTo(TerminalReason.Void));
        Assert.That(host.Outcome, Is.EqualTo(MatchOutcomes.Void));
        Assert.That(h.Grants(host.ResultId), Is.Empty);
    }

    [Test]
    public async Task DisconnectBeforeLockGivesPassAndCannotReopenTheVolley()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await OpenSelection(h, a, b);
        PlayerView bView = b.Match.View;
        await b.DisposeAsync(); // B leaves before locking; the clock keeps running

        a.Match.SendCommand(Play.LockFor(a.Match.View));
        await ServerHarness.Until(() => a.LockReceipts.Any(), "A's receipt");
        h.AdvanceMs(12000);
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Resolution), "a disconnect does not extend the deadline");

        await using TestPlayer b2 = await TestPlayer.ConnectAsync(h, "bob");
        await ServerHarness.Until(() => b2.Match?.View != null && b2.Match.View.History.Count == 1, "snapshot after reconnect");
        Assert.That(b2.Match.View.History[0][b2.Match.LocalSide].TimedOut, Is.True);
        b2.Match.SendCommand(Play.LockFor(bView));
        await ServerHarness.Until(() => b2.LockReceipts.Any(), "late receipt");
        Assert.That(b2.LockReceipts.Single().Accepted, Is.False, "reconnection cannot reopen a resolved volley");
    }

    [Test]
    public async Task DisconnectAfterLockKeepsTheAcceptedChoice()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await OpenSelection(h, a, b);

        LockInputCommand lb = Play.LockFor(b.Match.View, seed: 3);
        b.Match.SendCommand(lb);
        await ServerHarness.Until(() => b.LockReceipts.Any(r => r.Accepted), "B's lock");
        await b.DisposeAsync();
        await ServerHarness.Until(() => !h.Get<Realtime.ConnectionRegistry>().IsConnected(b.Uid), "B gone");

        a.Match.SendCommand(Play.LockFor(a.Match.View));
        await ServerHarness.Until(() => host.Engine.Phase == MatchPhase.Resolution, "resolution");
        RevealedChoice bChoice = host.Engine.GetView(PlayerSide.A).History[0][b.Match.LocalSide];
        Assert.That(bChoice.TimedOut, Is.False);
        Assert.That(bChoice.WeaponId, Is.EqualTo(lb.WeaponId));
        Assert.That(bChoice.PitchQdeg, Is.EqualTo(lb.PitchQdeg));
        await ServerHarness.Until(() => a.Match.View.Phase == MatchPhase.Resolution, "A sees resolution");
        Assert.That(a.Match.OpponentConnected, Is.False, "A's status line shows the opponent offline");
    }

    [Test]
    public async Task DisconnectAroundFinalizationSettlesOnceAndRecoversTheResult()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice", BotDifficulty.Normal, 3);
        TestPlayer b = await TestPlayer.ConnectAsync(h, "bob", BotDifficulty.Normal, 4);
        MatchHost host = await Play.FriendMatch(h, a, b);

        // Play until the last round, then B drops just before the match is finalized.
        for (int guard = 0; guard < 400 && !(host.Engine.RoundIndex == 8 && host.Engine.Phase == MatchPhase.Selection) && !host.IsSettled; guard++)
        {
            await Play.Quiesce(host, a, b);
            if (host.Engine.RoundIndex == 8 && host.Engine.Phase == MatchPhase.Selection) break;
            h.AdvanceToDeadline(host);
        }
        Assume.That(host.IsSettled, Is.False, "this seed should reach round 8");
        await b.DisposeAsync();
        await Play.ToEnd(h, host, a);
        string resultId = host.ResultId;
        int grants = h.Grants(resultId).Count;

        await using TestPlayer b2 = await TestPlayer.ConnectAsync(h, "bob");
        Assert.That(b2.Client.Connection.Welcome.ActiveMatchId, Is.Null, "the settled match is no longer active");
        b2.Client.SendRaw(ClientMessages.MatchResume("again", host.MatchId, 0));
        await ServerHarness.Until(() => b2.Ends.Any(), "match.end after reconnect");
        Assert.That(b2.Ends.Single().ResultId, Is.EqualTo(resultId));
        b2.Client.SendRaw(ClientMessages.MatchResume("again-2", host.MatchId, 0));
        await ServerHarness.Until(() => b2.Ends.Count() == 2, "second match.end");
        Assert.That(h.Grants(resultId), Has.Count.EqualTo(grants), "recovery requests never grant again");
        Assert.That(h.Store.Finalize(h.Stored(host.MatchId), h.Grants(resultId)), Is.Empty, "re-finalizing the same result grants nothing");
    }

    [Test]
    public async Task SetupExpiryIsATechnicalVoidWithoutRewardOrLoss()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(a);
        h.AdvanceMs(60000);
        Assert.That(host.IsSettled, Is.True);
        Assert.That(host.Outcome, Is.EqualTo(MatchOutcomes.TechnicalVoid));
        await ServerHarness.Until(() => a.Ends.Any() && b.Ends.Any(), "match.end");
        Assert.That(a.Ends.Single().Result, Is.Null);
        Assert.That(a.Ends.Single().RewardXp, Is.Zero);
        Assert.That(h.Grants(host.ResultId), Is.Empty);
    }
}
