using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Identity;
using AstraKingdoms.Server.Matches;

namespace AstraKingdoms.Server.Realtime;

/// <summary>
/// One authenticated WebSocket. Outgoing messages go through a bounded channel drained by a single
/// writer, so sends from match timers, lobby events and replies never interleave or block callers.
/// </summary>
public sealed class ClientConnection
{
    private readonly Channel<string> _out = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly CancellationTokenSource _closing = new();
    private int _dropped;

    public ClientConnection(WebSocket socket, VerifiedIdentity identity, string clientVersion)
    {
        Socket = socket;
        Identity = identity;
        ClientVersion = clientVersion;
    }

    public WebSocket Socket { get; }
    public VerifiedIdentity Identity { get; }
    public string ClientVersion { get; set; }
    public bool HelloReceived { get; set; }
    public string CloseReason { get; private set; }
    public CancellationToken Closing => _closing.Token;
    /// <summary>Messages dropped because the client stopped reading (it recovers with a snapshot on resume).</summary>
    public int Dropped => _dropped;

    public bool Enqueue(string text)
    {
        if (_closing.IsCancellationRequested) return false;
        if (_out.Writer.TryWrite(text)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Ends the connection with a reason (sent as the close description once queued messages are flushed).</summary>
    public void RequestClose(string reason)
    {
        CloseReason ??= reason;
        _out.Writer.TryComplete();
    }

    public void Abort() => _closing.Cancel();

    /// <summary>Single writer: sends queued messages in order until completed, then closes politely.</summary>
    public async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (string text in _out.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            if (Socket.State == WebSocketState.Open || Socket.State == WebSocketState.CloseReceived)
            {
                WebSocketCloseStatus status = CloseReason == CloseReasons.PolicyViolation || CloseReason == CloseReasons.ProtocolError
                    ? WebSocketCloseStatus.PolicyViolation
                    : CloseReason == CloseReasons.ShuttingDown ? WebSocketCloseStatus.EndpointUnavailable : WebSocketCloseStatus.NormalClosure;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Socket.CloseOutputAsync(status, CloseReason ?? "bye", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException || e is WebSocketException || e is IOException || e is ObjectDisposedException)
        {
            // The peer is gone; the receive loop ends and the connection is unregistered.
        }
        finally
        {
            _closing.Cancel();
        }
    }
}

/// <summary>
/// Live connections by account (the <see cref="IPlayerChannel"/> matches and the lobby write to).
/// A new connection for the same account supersedes the old one, which is closed with
/// "superseded": one device at a time controls a seat.
/// </summary>
public sealed class ConnectionRegistry : IPlayerChannel
{
    private readonly ConcurrentDictionary<string, ClientConnection> _byUid = new(StringComparer.Ordinal);

    public int Count => _byUid.Count;

    /// <summary>Registers a connection and returns the one it replaced, if any.</summary>
    public ClientConnection Register(ClientConnection c)
    {
        ClientConnection previous = null;
        _byUid.AddOrUpdate(c.Identity.Uid, c, (_, old) =>
        {
            previous = old;
            return c;
        });
        if (previous != null && previous != c) previous.RequestClose(CloseReasons.Superseded);
        return previous;
    }

    /// <summary>Removes the connection if it is still the current one for its account. True when it was.</summary>
    public bool Unregister(ClientConnection c) =>
        _byUid.TryRemove(new KeyValuePair<string, ClientConnection>(c.Identity.Uid, c));

    public bool Send(string uid, JsonNode message)
    {
        if (uid == null || !_byUid.TryGetValue(uid, out ClientConnection c) || !c.HelloReceived) return false;
        return c.Enqueue(message.ToCanonicalString());
    }

    public bool IsConnected(string uid) => uid != null && _byUid.TryGetValue(uid, out ClientConnection c) && c.HelloReceived;

    public IReadOnlyList<ClientConnection> All => _byUid.Values.ToList();
}
