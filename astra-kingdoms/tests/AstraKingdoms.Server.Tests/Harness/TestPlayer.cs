using System.Collections.Concurrent;
using AstraKingdoms.Client.Online;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Matches;

namespace AstraKingdoms.Server.Tests.Harness;

/// <summary>
/// A player using the real Unity online client library (<see cref="OnlineClient"/>) over the
/// in-process server. Messages are dispatched on the receive thread. An optional autopilot plays
/// with the rules assembly's <see cref="BotPlayer"/> on the decoded private view, exactly as a
/// person's client would submit commands.
/// </summary>
internal sealed class TestPlayer : IAsyncDisposable
{
    private readonly ConcurrentQueue<JsonNode> _inbox = new();
    private readonly object _gate = new();
    private BotPlayer _autopilot;
    private BotDifficulty? _autopilotLevel;
    private string _lastActedKey;
    private int _autopilotSeed;

    public string Name { get; }
    public string Uid { get; private set; }
    public string Token { get; private set; }
    public OnlineClient Client { get; }
    public OnlineMatchSession Match => Client.Match;
    /// <summary>Key (revision/phase) of a view for which the autopilot decided not to act (a declined cut).</summary>
    public string DeclinedKey { get; private set; }
    public int Rejections;

    private TestPlayer(ServerHarness h, string name, string token = null, string uid = null)
    {
        Name = name;
        Token = token ?? "dev:" + name;
        Uid = uid ?? "dev-" + name;
        Client = new OnlineClient(new OnlineClientOptions
        {
            ServerUri = new Uri("ws://localhost"),
            ClientVersion = "1.0.0",
            TokenProvider = _ => Task.FromResult(Token),
            DispatchOnReceiveThread = true,
            ReconnectDelaysMs = new[] { 20, 50, 100 },
            TransportFactory = () => new WebSocketTransport((uri, headers, ct) =>
            {
                var client = h.Server.CreateWebSocketClient();
                client.ConfigureRequest = r =>
                {
                    foreach (KeyValuePair<string, string> kv in headers) r.Headers[kv.Key] = kv.Value;
                };
                return client.ConnectAsync(uri, ct);
            }),
        });
        Client.Connection.MessageReceived += n => _inbox.Enqueue(n);
        Client.MatchStarted += OnMatchStarted;
    }

    public static async Task<TestPlayer> ConnectAsync(ServerHarness h, string name, BotDifficulty? autopilot = null, int autopilotSeed = 1)
    {
        var p = new TestPlayer(h, name) { _autopilotLevel = autopilot, _autopilotSeed = autopilotSeed };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await p.Client.ConnectAsync(cts.Token);
        return p;
    }

    /// <summary>Connects with an arbitrary bearer token (Firebase tests); <paramref name="uid"/> is the expected account ID.</summary>
    public static async Task<TestPlayer> ConnectWithTokenAsync(ServerHarness h, string name, string token, string uid)
    {
        var p = new TestPlayer(h, name, token, uid);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await p.Client.ConnectAsync(cts.Token);
        return p;
    }

    public IReadOnlyList<JsonNode> Messages => _inbox.ToArray();
    public IEnumerable<JsonNode> OfType(string type) => Messages.Where(m => m.Type() == type);
    public IEnumerable<ErrorMessage> Errors => OfType(MessageTypes.Error).Select(ErrorMessage.Parse);
    public bool HasError(string code, string rid = null) => Errors.Any(e => e.Code == code && (rid == null || e.Rid == rid));
    public RoomStateMessage LastRoom => OfType(MessageTypes.RoomState).Select(RoomStateMessage.Parse).LastOrDefault();
    public QueueStateMessage LastQueue => OfType(MessageTypes.QueueState).Select(QueueStateMessage.Parse).LastOrDefault();
    public IEnumerable<RoomClosedMessage> RoomClosures => OfType(MessageTypes.RoomClosed).Select(RoomClosedMessage.Parse);
    public IEnumerable<MatchStartMessage> Starts => OfType(MessageTypes.MatchStart).Select(MatchStartMessage.Parse);
    public IEnumerable<MatchEndMessage> Ends => OfType(MessageTypes.MatchEnd).Select(MatchEndMessage.Parse);
    public IEnumerable<ReceiptMessage> Receipts => OfType(MessageTypes.MatchReceipt).Select(ReceiptMessage.Parse);
    /// <summary>Receipts for LockInput commands only (loadout and cut receipts excluded).</summary>
    public IEnumerable<ReceiptMessage> LockReceipts => Receipts.Where(r => r.Kind == nameof(Rules.Match.CommandKind.LockInput));

    /// <summary>Turns the autopilot on (also for a match already running).</summary>
    public void EnableAutopilot(BotDifficulty level = BotDifficulty.Normal)
    {
        _autopilotLevel = level;
        if (Match != null) Attach(Match);
    }

    public void DisableAutopilot()
    {
        lock (_gate) _autopilot = null;
        _autopilotLevel = null;
    }

    private void OnMatchStarted(OnlineMatchSession session)
    {
        session.CommandRejected += _ => Interlocked.Increment(ref Rejections);
        if (_autopilotLevel.HasValue) Attach(session);
    }

    private void Attach(OnlineMatchSession session)
    {
        lock (_gate)
        {
            _autopilot = new BotPlayer(session.LocalSide, new BotPolicy(_autopilotLevel.Value, new BotRng((ulong)_autopilotSeed * 7919UL)),
                new BotRng((ulong)_autopilotSeed * 104729UL + (ulong)session.LocalSide));
        }
        session.ViewChanged += v => Act(session, v);
        if (session.View != null) Act(session, session.View);
    }

    public static string KeyOf(PlayerView v) => v.StateRevision + "/" + v.Phase + "/" + (v.OwnLoadout != null) + "/" + (v.OwnLock != null);

    private void Act(OnlineMatchSession session, PlayerView view)
    {
        lock (_gate)
        {
            if (_autopilot == null || !ServerHarness.HasWork(view)) return;
            string key = KeyOf(view);
            if (key == _lastActedKey) return; // already acted on this snapshot; the receipt is on its way
            MatchCommand cmd = _autopilot.Decide(view);
            _lastActedKey = key;
            if (cmd == null)
            {
                DeclinedKey = key;
                return;
            }
            session.SendCommand(cmd);
        }
    }

    /// <summary>True when this client shows exactly the server's current private view and has nothing in flight.</summary>
    public bool InSyncWith(MatchHost host)
    {
        OnlineMatchSession m = Match;
        if (m == null || m.View == null || m.PendingCount != 0) return false;
        PlayerView server = host.Engine.GetView(m.LocalSide);
        if (m.View.ToCanonicalText() != server.ToCanonicalText()) return false;
        if (_autopilotLevel == null) return true;
        return !ServerHarness.HasWork(server) || DeclinedKey == KeyOf(server);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.CloseAsync();
        Client.Dispose();
    }
}

/// <summary>Match-level helpers shared by the tests.</summary>
internal static class Play
{
    /// <summary>Waits until every listed client mirrors the server exactly and has acted where it will.</summary>
    public static Task Quiesce(MatchHost host, params TestPlayer[] players) =>
        ServerHarness.Until(() => host.IsSettled || players.All(p => p.InSyncWith(host)), "clients to sync with " + host.Engine.Phase);

    /// <summary>Plays a match to its end: clients act through their autopilots, the fake clock runs each phase out.</summary>
    public static async Task ToEnd(ServerHarness h, MatchHost host, params TestPlayer[] players)
    {
        for (int guard = 0; guard < 400 && !host.IsSettled; guard++)
        {
            await Quiesce(host, players);
            if (host.IsSettled) break;
            h.AdvanceToDeadline(host);
        }
        Assert.That(host.IsSettled, Is.True, "match did not settle");
    }

    /// <summary>Waits for both players' match.start and returns the hosted match.</summary>
    public static async Task<MatchHost> Started(ServerHarness h, TestPlayer a, TestPlayer b = null)
    {
        await ServerHarness.Until(() => a.Match != null && (b == null || b.Match != null), "match.start");
        MatchHost host = h.Matches.Get(a.Match.MatchId);
        Assert.That(host, Is.Not.Null);
        await ServerHarness.Until(() => a.Match.View != null && (b == null || b.Match.View != null), "first view");
        return host;
    }

    /// <summary>Submits a legal Starter loadout for a player without an autopilot.</summary>
    public static void SubmitLoadout(TestPlayer p) => p.Match.SubmitLoadout(p.Match.LocalSide, new[] { 1, 2, 3 }, 0);

    /// <summary>A legal lock for the open volley of <paramref name="v"/> (chosen by a rules bot), with an optional request ID.</summary>
    public static LockInputCommand LockFor(PlayerView v, string requestId = null, int seed = 5)
    {
        var bot = new BotPlayer(v.Viewer, new BotPolicy(BotDifficulty.Normal, new BotRng((ulong)seed)), new BotRng((ulong)seed + 1));
        var cmd = (LockInputCommand)bot.Decide(v);
        return requestId == null ? cmd : new LockInputCommand(cmd.Header.WithRequestId(requestId), cmd.VolleyIndex, cmd.ToChoice());
    }

    /// <summary>Waits until the player's view shows <paramref name="phase"/> at the server's current revision.</summary>
    public static Task ViewAt(MatchHost host, TestPlayer p, MatchPhase phase) =>
        ServerHarness.Until(() => p.Match?.View != null && p.Match.View.Phase == phase && p.Match.View.StateRevision == host.Engine.StateRevision,
            p.Name + " to see " + phase);

    /// <summary>A friend room created by <paramref name="host"/> and joined/confirmed by both: the match starts.</summary>
    public static async Task<MatchHost> FriendMatch(ServerHarness h, TestPlayer host, TestPlayer guest, CatalogPreset catalog = CatalogPreset.Starter)
    {
        host.Client.CreateRoom(catalog);
        await ServerHarness.Until(() => host.Client.Room != null, "room.state for host");
        guest.Client.JoinRoom(host.Client.Room.Code);
        await ServerHarness.Until(() => guest.Client.Room != null && host.Client.Room?.Members == 2, "room full");
        host.Client.ConfirmRoom();
        await ServerHarness.Until(() => guest.Client.Room?.ConfirmedOpponent == true, "host confirmation");
        guest.Client.ConfirmRoom();
        return await Started(h, host, guest);
    }
}
