using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Server.Lobby;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Storage;

namespace AstraKingdoms.Server.Tests;

/// <summary>Ticket 52: two-player friend rooms. Codes expire; capacity, joining, leaving and host disappearance work.</summary>
[TestFixture]
public class FriendRoomTests
{
    [Test]
    public void CodesUseAnUnambiguousAlphabet()
    {
        Assert.That(RoomCodes.Alphabet, Does.Not.Contain("0").And.Not.Contain("O").And.Not.Contain("1").And.Not.Contain("I").And.Not.Contain("L"));
        for (int i = 0; i < 2000; i++)
        {
            string code = RoomCodes.New(6);
            Assert.That(code, Has.Length.EqualTo(6));
            Assert.That(code.All(c => RoomCodes.Alphabet.Contains(c)), Is.True, code);
        }
        Assert.That(RoomCodes.Normalize("ab-c d23"), Is.EqualTo("ABCD23"));
        Assert.That(RoomCodes.Normalize("AB0CD2"), Is.Null, "a look-alike is rejected, not guessed");
    }

    [Test]
    public async Task BothPlayersSeeCatalogTerrainAndModeBeforeConfirming()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer host = await TestPlayer.ConnectAsync(h, "host");
        await using TestPlayer guest = await TestPlayer.ConnectAsync(h, "guest");

        host.Client.CreateRoom(CatalogPreset.Full);
        await ServerHarness.Until(() => host.Client.Room != null, "room");
        string code = host.Client.Room.Code;
        Assert.That(host.Client.Room.IsHost && host.Client.Room.Status == RoomStatus.Open, Is.True);
        Assert.That(host.Client.Room.ExpiresInMs, Is.EqualTo(600_000));
        guest.Client.JoinRoom(code.ToLowerInvariant()); // codes are case-insensitive
        await ServerHarness.Until(() => guest.Client.Room != null && host.Client.Room.Members == 2, "join");

        foreach (RoomStateMessage seen in new[] { host.Client.Room, guest.Client.Room })
        {
            Assert.That(seen.Rules.Config.Catalog, Is.EqualTo(CatalogPreset.Full));
            Assert.That(seen.Rules.Config.TerrainTemplateId, Is.EqualTo(TerrainTemplates.FullId));
            Assert.That(seen.Rules.Config.Mode, Is.EqualTo(MatchMode.Online));
            Assert.That(seen.Rules.Ranked, Is.False);
            Assert.That(seen.Rules.RulesHashHex, Is.EqualTo(RulesBundle.HashHex));
        }

        // Confirming a revision the player has not seen fails; a change by the host clears confirmations.
        guest.Client.SendRaw(ClientMessages.RoomConfirm("stale", guest.Client.Room.Revision - 1));
        await ServerHarness.Until(() => guest.HasError(ErrorCodes.RoomRevision, "stale"), "stale confirmation");
        guest.Client.ConfirmRoom();
        await ServerHarness.Until(() => host.Client.Room.ConfirmedOpponent, "guest confirmed");
        host.Client.ConfigureRoom(CatalogPreset.Starter);
        await ServerHarness.Until(() => guest.Client.Room.Rules.Config.Catalog == CatalogPreset.Starter, "reconfigured");
        Assert.That(guest.Client.Room.ConfirmedSelf, Is.False, "a changed room needs a new confirmation");
        Assert.That(h.Matches.ActiveFor(host.Uid), Is.Null);

        host.Client.ConfirmRoom();
        await ServerHarness.Until(() => guest.Client.Room.ConfirmedOpponent, "host confirmed");
        guest.Client.ConfirmRoom();
        MatchHost match = await Play.Started(h, host, guest);
        Assert.That(match.Engine.Config.Catalog, Is.EqualTo(CatalogPreset.Starter), "the match uses exactly what both confirmed");
        Assert.That(match.Origin, Is.EqualTo(MatchOrigins.FriendRoom));
        Assert.That(host.Client.Room, Is.Null);
        Assert.That(h.Lobby.RoomCount, Is.Zero);
    }

    [Test]
    public async Task CapacityOwnRoomAndBusyPlayersAreEnforced()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer host = await TestPlayer.ConnectAsync(h, "host");
        await using TestPlayer guest = await TestPlayer.ConnectAsync(h, "guest");
        await using TestPlayer third = await TestPlayer.ConnectAsync(h, "third");

        host.Client.CreateRoom(CatalogPreset.Starter);
        await ServerHarness.Until(() => host.Client.Room != null, "room");
        string code = host.Client.Room.Code;
        host.Client.SendRaw(ClientMessages.RoomJoin("own", code));
        await ServerHarness.Until(() => host.HasError(ErrorCodes.RoomOwn, "own"), "own room");
        host.Client.SendRaw(ClientMessages.RoomCreate("second", CatalogPreset.Starter));
        await ServerHarness.Until(() => host.HasError(ErrorCodes.Busy, "second"), "one room at a time");

        guest.Client.JoinRoom(code);
        await ServerHarness.Until(() => guest.Client.Room != null, "guest joined");
        third.Client.SendRaw(ClientMessages.RoomJoin("full", code));
        await ServerHarness.Until(() => third.HasError(ErrorCodes.RoomFull, "full"), "capacity two");
        third.Client.SendRaw(ClientMessages.RoomJoin("nope", "ZZZZZZ"));
        await ServerHarness.Until(() => third.HasError(ErrorCodes.RoomNotFound, "nope"), "unknown code");
        guest.Client.SendRaw(ClientMessages.QueueJoin("q", CatalogPreset.Starter));
        await ServerHarness.Until(() => guest.HasError(ErrorCodes.Busy, "q"), "a room member cannot queue");
    }

    [Test]
    public async Task CodesExpire()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer host = await TestPlayer.ConnectAsync(h, "host");
        await using TestPlayer late = await TestPlayer.ConnectAsync(h, "late");
        host.Client.CreateRoom(CatalogPreset.Starter);
        await ServerHarness.Until(() => host.Client.Room != null, "room");
        string code = host.Client.Room.Code;

        h.Advance(TimeSpan.FromSeconds(599));
        Assert.That(h.Lobby.RoomCount, Is.EqualTo(1));
        h.Advance(TimeSpan.FromSeconds(1));
        await ServerHarness.Until(() => host.RoomClosures.Any(), "expiry notice");
        Assert.That(host.RoomClosures.Single().Reason, Is.EqualTo(RoomCloseReasons.Expired));
        late.Client.SendRaw(ClientMessages.RoomJoin("late", code));
        await ServerHarness.Until(() => late.HasError(ErrorCodes.RoomNotFound, "late"), "expired code");
    }

    [Test]
    public async Task GuestLeavingReopensTheRoomAndHostLeavingClosesIt()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer host = await TestPlayer.ConnectAsync(h, "host");
        await using TestPlayer guest = await TestPlayer.ConnectAsync(h, "guest");
        host.Client.CreateRoom(CatalogPreset.Starter);
        await ServerHarness.Until(() => host.Client.Room != null, "room");
        guest.Client.JoinRoom(host.Client.Room.Code);
        await ServerHarness.Until(() => host.Client.Room.Members == 2, "join");

        guest.Client.LeaveRoom();
        await ServerHarness.Until(() => host.Client.Room.Members == 1 && guest.Client.Room == null, "guest left");
        Assert.That(host.Client.Room.Status, Is.EqualTo(RoomStatus.Open));

        guest.Client.JoinRoom(host.Client.Room.Code);
        await ServerHarness.Until(() => host.Client.Room.Members == 2, "rejoin");
        host.Client.LeaveRoom();
        await ServerHarness.Until(() => guest.RoomClosures.Any(c => c.Reason == RoomCloseReasons.HostLeft), "host left");
        Assert.That(h.Lobby.RoomCount, Is.Zero);
    }

    [Test]
    public async Task HostDisappearanceClosesTheRoomAfterTheGracePeriod()
    {
        await using ServerHarness h = ServerHarness.Start();
        TestPlayer host = await TestPlayer.ConnectAsync(h, "host");
        await using TestPlayer guest = await TestPlayer.ConnectAsync(h, "guest");
        host.Client.CreateRoom(CatalogPreset.Starter);
        await ServerHarness.Until(() => host.Client.Room != null, "room");
        string code = host.Client.Room.Code;
        guest.Client.JoinRoom(code);
        await ServerHarness.Until(() => guest.Client.Room != null, "join");

        // A brief drop within the grace period keeps the room.
        await host.DisposeAsync();
        await ServerHarness.Until(() => !h.Get<Realtime.ConnectionRegistry>().IsConnected(host.Uid), "host offline");
        h.Advance(TimeSpan.FromSeconds(20));
        await using TestPlayer back = await TestPlayer.ConnectAsync(h, "host");
        await ServerHarness.Until(() => back.Client.Room?.Code == code, "room state resent on return");
        h.Advance(TimeSpan.FromSeconds(30));
        Assert.That(h.Lobby.RoomCount, Is.EqualTo(1));

        // Gone for good: the guest is told and the room closes.
        await back.DisposeAsync();
        await ServerHarness.Until(() => !h.Get<Realtime.ConnectionRegistry>().IsConnected(back.Uid), "host offline again");
        h.Advance(TimeSpan.FromSeconds(30));
        await ServerHarness.Until(() => guest.RoomClosures.Any(), "closure");
        Assert.That(guest.RoomClosures.Single().Reason, Is.EqualTo(RoomCloseReasons.HostDisconnected));
        Assert.That(h.Lobby.RoomCount, Is.Zero);
    }
}

/// <summary>Ticket 53: after 20 s, player consent starts an unranked, labelled bot match; waiting or cancelling creates no duplicate.</summary>
[TestFixture]
public class QueueTests
{
    [Test]
    public async Task BotOfferAppearsAfterTwentySecondsAndNeedsConsent()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        a.Client.JoinQueue(CatalogPreset.Starter);
        await ServerHarness.Until(() => a.LastQueue?.Status == QueueStatus.Waiting, "waiting");

        a.Client.SendRaw(ClientMessages.QueueAcceptBot("early"));
        await ServerHarness.Until(() => a.HasError(ErrorCodes.NoBotOffer, "early"), "no offer yet");
        h.Advance(TimeSpan.FromMilliseconds(19_999));
        Assert.That(a.LastQueue.Status, Is.EqualTo(QueueStatus.Waiting));
        h.Advance(TimeSpan.FromMilliseconds(1));
        await ServerHarness.Until(() => a.LastQueue.Status == QueueStatus.BotOffer, "offer");
        Assert.That(a.LastQueue.BotLabel, Does.Contain("Bot").And.Contain("unranked"));
        h.Advance(TimeSpan.FromMinutes(5));
        Assert.That(h.Matches.ActiveFor(a.Uid), Is.Null, "no bot match without consent");

        a.Client.AcceptBotMatch();
        a.Client.SendRaw(ClientMessages.QueueAcceptBot("twice")); // double tap
        MatchHost match = await Play.Started(h, a);
        await ServerHarness.Until(() => a.HasError(ErrorCodes.NotQueued, "twice"), "second consent refused");
        MatchStartMessage start = a.Starts.Single();
        Assert.That(start.OpponentIsBot, Is.True);
        Assert.That(start.OpponentLabel, Does.StartWith("Bot"));
        Assert.That(start.Origin, Is.EqualTo(MatchOrigins.QueueBot));
        Assert.That(start.Rules.Ranked, Is.False);
        Assert.That(h.Matches.Active.Count(m => m.HumanUids.Contains(a.Uid)), Is.EqualTo(1));
        StoredMatch stored = h.Stored(match.MatchId);
        Assert.That(stored.KindB, Is.EqualTo(OpponentKinds.Bot));
        Assert.That(h.Audit(match.MatchId).Any(e => e.Action == "bot_match_consented"), Is.True);
    }

    [Test]
    public async Task KeepWaitingLetsAPersonArriveAndNoDuplicateIsCreated()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        a.Client.JoinQueue(CatalogPreset.Starter);
        await ServerHarness.Until(() => a.LastQueue?.Status == QueueStatus.Waiting, "waiting");
        h.Advance(TimeSpan.FromSeconds(20));
        await ServerHarness.Until(() => a.LastQueue.Status == QueueStatus.BotOffer, "offer");
        a.Client.KeepWaiting();
        await ServerHarness.Until(() => a.OfType(MessageTypes.QueueState).Count() >= 3, "still queued");

        b.Client.JoinQueue(CatalogPreset.Starter);
        MatchHost match = await Play.Started(h, a, b);
        Assert.That(match.Origin, Is.EqualTo(MatchOrigins.Queue));
        Assert.That(a.Starts.Single().OpponentKind, Is.EqualTo(OpponentKinds.Human));
        a.Client.SendRaw(ClientMessages.QueueAcceptBot("late"));
        await ServerHarness.Until(() => a.HasError(ErrorCodes.NotQueued, "late"), "offer gone");
        Assert.That(h.Matches.Active, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DifferentCatalogsDoNotPairAndCancellingLeavesNothingBehind()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        a.Client.JoinQueue(CatalogPreset.Starter);
        b.Client.JoinQueue(CatalogPreset.Full);
        await ServerHarness.Until(() => a.LastQueue != null && b.LastQueue != null, "both waiting");
        Assert.That(h.Lobby.QueueLength, Is.EqualTo(2));

        a.Client.CancelQueue();
        await ServerHarness.Until(() => a.LastQueue.Status == QueueStatus.Cancelled, "cancelled");
        h.Advance(TimeSpan.FromSeconds(60));
        Assert.That(a.LastQueue.Status, Is.EqualTo(QueueStatus.Cancelled), "no bot offer after cancelling");
        Assert.That(h.Matches.Active, Is.Empty);
        await b.Client.CloseAsync(); // disconnecting drops the queue entry too
        await ServerHarness.Until(() => h.Lobby.QueueLength == 0, "queue emptied");
    }

    [Test]
    public async Task ConsentRacingAHumanArrivalCreatesExactlyOneMatch()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        a.Client.JoinQueue(CatalogPreset.Starter);
        await ServerHarness.Until(() => a.LastQueue != null, "waiting");
        h.Advance(TimeSpan.FromSeconds(20));
        await ServerHarness.Until(() => a.LastQueue.Status == QueueStatus.BotOffer, "offer");

        await Task.WhenAll(Task.Run(() => a.Client.AcceptBotMatch()), Task.Run(() => b.Client.JoinQueue(CatalogPreset.Starter)));
        await ServerHarness.Until(() => a.Match != null, "a match for alice");
        await ServerHarness.Until(() => b.LastQueue != null || b.Match != null, "bob answered");
        Assert.That(h.Matches.Active.Count(m => m.HumanUids.Contains(a.Uid)), Is.EqualTo(1));
        Assert.That(a.Starts.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task LabelledBotMatchPlaysToTheEndUnrankedWithPracticeReward()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice", BotDifficulty.Hard, 21);
        a.Client.JoinQueue(CatalogPreset.Starter);
        await ServerHarness.Until(() => a.LastQueue != null, "waiting");
        h.Advance(TimeSpan.FromSeconds(20));
        await ServerHarness.Until(() => a.LastQueue.Status == QueueStatus.BotOffer, "offer");
        a.Client.AcceptBotMatch();
        MatchHost match = await Play.Started(h, a);

        await Play.ToEnd(h, match, a);
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcomes.Completed));
        IReadOnlyList<RewardGrant> grants = h.Grants(match.ResultId);
        Assert.That(grants, Has.Count.EqualTo(1), "only the person is granted");
        Assert.That(grants[0].Xp, Is.EqualTo(50));
        Assert.That(h.Stored(match.MatchId).Ranked, Is.False);
        Assert.That(h.Audit(match.MatchId).Count(e => e.ActorRef == "bot"), Is.GreaterThan(0), "the bot acted through the engine like any seat");
    }
}
