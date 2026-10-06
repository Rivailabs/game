using AstraKingdoms.Client.Online;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Server.Matches;

namespace AstraKingdoms.Server.Tests;

/// <summary>Ticket 54: snapshots and events after the acknowledged sequence restore the correct state; retries are idempotent.</summary>
[TestFixture]
public class ReconnectionTests
{
    [Test]
    public async Task DroppedLinkReconnectsAndRestoresTheExactPrivateState()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice", BotDifficulty.Normal, 11);
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob", BotDifficulty.Normal, 12);
        MatchHost host = await Play.FriendMatch(h, a, b);
        int cutsSeenByA = 0;
        a.Match.CutApplied += (_, _) => Interlocked.Increment(ref cutsSeenByA);

        // Play into round 3, then cut A's network link in the middle of a selection.
        for (int guard = 0; guard < 400 && host.Engine.RoundIndex < 3 && !host.IsSettled; guard++)
        {
            await Play.Quiesce(host, a, b);
            h.AdvanceToDeadline(host);
        }
        Assume.That(host.IsSettled, Is.False);
        long seqBefore = a.Match.LastSeq;
        a.Client.Connection.DropLink();
        h.AdvanceToDeadline(host); // the world moves on while A is away
        await ServerHarness.Until(() => a.Client.Connection.ConnectCount >= 2 && a.Client.Status == ConnectionStatus.Connected, "automatic reconnection");
        await Play.Quiesce(host, a, b);

        Assert.That(a.Match.View.ToCanonicalText(), Is.EqualTo(ServerHarness.ServerViewText(host, a.Match.LocalSide)));
        Assert.That(a.Match.LastSeq, Is.GreaterThan(seqBefore));
        Assert.That(a.Match.LastSeq, Is.EqualTo(host.Engine.Events[^1].Sequence), "every event after the acknowledged sequence arrived");
        MatchUpdateMessage snapshot = a.OfType(MessageTypes.MatchUpdate).Select(MatchUpdateMessage.Parse).Last(u => u.Snapshot);
        Assert.That(snapshot.View.OptString("ownership"), Is.Not.Null, "a snapshot carries the ownership map");
        Assert.That(snapshot.Events.All(e => e.Seq > 0), Is.True);

        await Play.ToEnd(h, host, a, b);
        int serverCuts = host.Engine.Events.Count(e => e.Type == MatchEventType.CutApplied);
        await ServerHarness.Until(() => a.Match.IsOver, "A sees the end");
        Assert.That(cutsSeenByA, Is.EqualTo(serverCuts), "no event is applied twice across the reconnection");
    }

    [Test]
    public async Task ResentCommandAfterReconnectIsTheSameAction()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(a);
        Play.SubmitLoadout(b);
        await Play.ViewAt(host, a, MatchPhase.TerrainAnnounce);
        h.AdvanceToDeadline(host);
        await Play.ViewAt(host, a, MatchPhase.Selection);

        LockInputCommand lockA = Play.LockFor(a.Match.View);
        a.Match.SendCommand(lockA);
        await ServerHarness.Until(() => a.LockReceipts.Any(), "first receipt");
        ulong revision = a.LockReceipts.Single().InputRevision;

        // The receipt is "lost": the link drops and the client retries with the same request ID.
        a.Client.Connection.DropLink();
        await ServerHarness.Until(() => a.Client.Connection.ConnectCount >= 2 && a.Client.Status == ConnectionStatus.Connected, "reconnection");
        a.Client.SendRaw(ClientMessages.MatchCommand("retry", lockA));
        await ServerHarness.Until(() => a.LockReceipts.Count() == 2, "retry receipt");
        Assert.That(a.LockReceipts.Last().Accepted, Is.True);
        Assert.That(a.LockReceipts.Last().InputRevision, Is.EqualTo(revision), "the original receipt, not a new action");
        Assert.That(host.Engine.CommandLog.Count(c => c.Command is LockInputCommand), Is.EqualTo(1));
    }

    [Test]
    public async Task CommandIssuedWhileOfflineIsDeliveredOnceAfterReconnect()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(b);
        await ServerHarness.Until(() => a.Match.View.OpponentLoadoutSubmitted, "B ready");

        a.Client.Connection.DropLink();
        Play.SubmitLoadout(a); // stays pending until the link is back
        await ServerHarness.Until(() => a.Client.Connection.ConnectCount >= 2 && a.Match.PendingCount == 0, "pending command answered");
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.TerrainAnnounce));
        Assert.That(host.Engine.CommandLog.Count(c => c.Command is SubmitLoadoutCommand), Is.EqualTo(2));
        Assert.That(a.Rejections, Is.Zero);
    }

    [Test]
    public async Task FreshClientProcessRejoinsItsRunningMatch()
    {
        await using ServerHarness h = ServerHarness.Start();
        TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(a);
        await ServerHarness.Until(() => a.Match.View.OwnLoadout != null, "loadout accepted");
        await a.DisposeAsync(); // app closed

        await using TestPlayer again = await TestPlayer.ConnectAsync(h, "alice");
        Assert.That(again.Client.Connection.Welcome.ActiveMatchId, Is.EqualTo(host.MatchId));
        await ServerHarness.Until(() => again.Match?.View != null, "match.start and snapshot");
        Assert.That(again.Match.MatchId, Is.EqualTo(host.MatchId));
        Assert.That(again.Match.View.OwnLoadout, Is.Not.Null, "the accepted loadout survived");
        Assert.That(again.Match.View.ToCanonicalText(), Is.EqualTo(ServerHarness.ServerViewText(host, again.Match.LocalSide)));
    }

    [Test]
    public async Task SecondDeviceSupersedesTheFirst()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer first = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer second = await TestPlayer.ConnectAsync(h, "alice");
        await ServerHarness.Until(() => first.Client.Status == ConnectionStatus.Failed, "first device closed");
        Assert.That(first.Client.Connection.LastFailure, Does.Contain("another device"));
        Assert.That(second.Client.Status, Is.EqualTo(ConnectionStatus.Connected));
    }
}
