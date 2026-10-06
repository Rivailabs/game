using System.Net.WebSockets;
using System.Text;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Identity;
using AstraKingdoms.Server.Lobby;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Ops;
using AstraKingdoms.Server.Security;
using AstraKingdoms.Server.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AstraKingdoms.Server.Realtime;

/// <summary>
/// The WebSocket endpoint (tickets 49, 51, 54, 55). Authentication happens on the HTTP upgrade
/// (<c>Authorization: Bearer</c>), before the socket is accepted, so an unauthenticated client never
/// reaches any match state. The first frame must be <c>hello</c>. Every later frame passes the
/// per-identity token bucket and the size limit, is parsed into the shared schema and routed. Frames
/// are never logged; failures are logged by code and pseudonymous player reference.
/// </summary>
public sealed class RealtimeEndpoint
{
    private readonly IIdentityVerifier _identity;
    private readonly ConnectionRegistry _connections;
    private readonly MatchRegistry _matches;
    private readonly LobbyService _lobby;
    private readonly IMatchRepository _repo;
    private readonly DrainState _drain;
    private readonly ServerOptions _options;
    private readonly ILogger<RealtimeEndpoint> _log;
    private readonly TokenBucketLimiter _limiter;

    public RealtimeEndpoint(IIdentityVerifier identity, ConnectionRegistry connections, MatchRegistry matches, LobbyService lobby,
        IMatchRepository repo, DrainState drain, IOptions<ServerOptions> options, TimeProvider time, ILogger<RealtimeEndpoint> log)
    {
        _identity = identity;
        _connections = connections;
        _matches = matches;
        _lobby = lobby;
        _repo = repo;
        _drain = drain;
        _options = options.Value;
        _log = log;
        _limiter = new TokenBucketLimiter(time, _options.RateLimits.Capacity, _options.RateLimits.RefillPerSecond);
    }

    public TokenBucketLimiter Limiter => _limiter;

    public async Task HandleAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (_drain.IsDraining)
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        string token = BearerToken(http.Request);
        IdentityResult auth = await _identity.VerifyAsync(token, http.RequestAborted).ConfigureAwait(false);
        if (!auth.Ok)
        {
            _log.LogWarning("Realtime upgrade rejected: {Reason}", auth.Failure);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var conn = new ClientConnection(socket, auth.Identity, http.Request.Headers[OnlineProtocol.ClientVersionHeader].ToString());
        using var life = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, conn.Closing);
        Task writer = conn.WriteLoopAsync(life.Token);
        try
        {
            await ReceiveLoopAsync(conn, life.Token).ConfigureAwait(false);
        }
        finally
        {
            conn.RequestClose(conn.CloseReason ?? "bye");
            try
            {
                await writer.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                conn.Abort();
            }
            if (conn.HelloReceived && _connections.Unregister(conn))
            {
                _lobby.OnDisconnected(conn.Identity.Uid);
                _matches.ActiveFor(conn.Identity.Uid)?.PresenceChanged();
                _log.LogInformation("Player {Player} disconnected", conn.Identity.Ref);
            }
        }
    }

    public static string BearerToken(HttpRequest request)
    {
        string header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header.Substring(7).Trim() : null;
    }

    private async Task ReceiveLoopAsync(ClientConnection conn, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var helloTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.Connections.HelloTimeoutSeconds));
        int violations = 0;
        while (!ct.IsCancellationRequested)
        {
            string text;
            using (var linked = conn.HelloReceived ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                                                   : CancellationTokenSource.CreateLinkedTokenSource(ct, helloTimeout.Token))
            {
                try
                {
                    text = await ReadFrameAsync(conn.Socket, buffer, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!conn.HelloReceived && helloTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    Reply(conn, null, ErrorCodes.HelloRequired, "Send hello first.");
                    conn.RequestClose(CloseReasons.ProtocolError);
                    return;
                }
                catch (Exception e) when (e is OperationCanceledException || e is WebSocketException)
                {
                    return;
                }
            }
            if (text == null) return;
            if (text.Length == 0)
            {
                conn.RequestClose(CloseReasons.ProtocolError); // oversized frame
                return;
            }

            if (!_limiter.TryTake(conn.Identity.Uid))
            {
                violations++;
                if (violations == 1 || violations % 10 == 0) Reply(conn, null, ErrorCodes.RateLimited, "Too many messages; slow down.");
                if (violations >= _options.RateLimits.CloseAfterViolations)
                {
                    _log.LogWarning("Player {Player} closed for flooding", conn.Identity.Ref);
                    conn.RequestClose(CloseReasons.PolicyViolation);
                    return;
                }
                continue;
            }
            violations = 0;

            JsonNode msg;
            try
            {
                msg = JsonNode.Parse(text);
                if (msg.Kind != JsonKind.Object || msg.Type() == null) throw new FormatException("not a typed object");
            }
            catch (FormatException)
            {
                Reply(conn, null, ErrorCodes.BadMessage, "Malformed message.");
                continue;
            }

            if (!conn.HelloReceived)
            {
                if (!Hello(conn, msg)) return;
                continue;
            }
            try
            {
                Route(conn, msg);
            }
            catch (Exception e) when (e is FormatException || e is OverflowException || e is InvalidCastException || e is ArgumentException)
            {
                Reply(conn, SafeRid(msg), ErrorCodes.BadMessage, "Malformed " + msg.Type() + ".");
            }
        }
    }

    /// <summary>Reads one text frame; null when closed, empty string when it exceeds the size limit.</summary>
    private static async Task<string> ReadFrameAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            if (r.MessageType == WebSocketMessageType.Binary) return string.Empty;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > OnlineProtocol.MaxMessageBytes) return string.Empty;
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }

    private bool Hello(ClientConnection conn, JsonNode msg)
    {
        if (msg.Type() != MessageTypes.Hello)
        {
            Reply(conn, SafeRid(msg), ErrorCodes.HelloRequired, "Send hello first.");
            return true; // keep waiting for hello until the timeout
        }
        long protocol = msg.OptLong("protocol", -1);
        if (protocol != OnlineProtocol.Version)
        {
            Reply(conn, null, ErrorCodes.ProtocolUnsupported, "This server speaks protocol " + OnlineProtocol.Version + ".");
            conn.RequestClose(CloseReasons.ProtocolError);
            return false;
        }
        string version = msg.OptString("client_version") ?? conn.ClientVersion;
        if (!VersionAtLeast(version, _options.Clients.MinimumVersion))
        {
            Reply(conn, null, ErrorCodes.ClientTooOld, "Please update the game (minimum " + _options.Clients.MinimumVersion + ").");
            conn.RequestClose(CloseReasons.ProtocolError);
            return false;
        }
        conn.ClientVersion = version;
        string uid = conn.Identity.Uid;
        MatchHost active = _matches.ActiveFor(uid);
        conn.Enqueue(new WelcomeMessage
        {
            ServerVersion = _options.Connections.ServerVersion,
            RulesVersion = RulesConstants.RulesVersion,
            RulesHashHex = RulesBundle.HashHex,
            PlayerRef = conn.Identity.Ref,
            ActiveMatchId = active?.MatchId,
        }.ToJson().ToCanonicalString());
        conn.HelloReceived = true;
        _connections.Register(conn);
        _log.LogInformation("Player {Player} connected (client {Version})", conn.Identity.Ref, version);
        _lobby.OnConnected(uid);
        active?.PresenceChanged();
        return true;
    }

    private void Route(ClientConnection conn, JsonNode msg)
    {
        string uid = conn.Identity.Uid;
        string rid = msg.OptString("rid");
        switch (msg.Type())
        {
            case MessageTypes.Hello:
                return; // already greeted
            case MessageTypes.Ping:
                conn.Enqueue(Json.Message(MessageTypes.Pong).Add("rid", rid).ToCanonicalString());
                return;
            case MessageTypes.RoomCreate:
                _lobby.CreateRoom(uid, rid, ClientMessages.ParseCatalog(msg.Str("catalog")));
                return;
            case MessageTypes.RoomConfigure:
                _lobby.ConfigureRoom(uid, rid, ClientMessages.ParseCatalog(msg.Str("catalog")));
                return;
            case MessageTypes.RoomJoin:
                _lobby.JoinRoom(uid, rid, msg.Str("code"));
                return;
            case MessageTypes.RoomConfirm:
                _lobby.ConfirmRoom(uid, rid, msg["revision"].AsLong());
                return;
            case MessageTypes.RoomLeave:
                _lobby.LeaveRoom(uid, rid);
                return;
            case MessageTypes.QueueJoin:
                _lobby.JoinQueue(uid, rid, ClientMessages.ParseCatalog(msg.Str("catalog")));
                return;
            case MessageTypes.QueueAcceptBot:
                _lobby.AcceptBot(uid, rid);
                return;
            case MessageTypes.QueueKeepWaiting:
                _lobby.KeepWaiting(uid, rid);
                return;
            case MessageTypes.QueueCancel:
                _lobby.CancelQueue(uid, rid);
                return;
            case MessageTypes.MatchResume:
                Resume(conn, rid, msg.Str("match_id"), msg.OptLong("ack_seq"));
                return;
            case MessageTypes.MatchCommand:
                Command(conn, rid, msg);
                return;
            case MessageTypes.MatchAck:
                _matches.Get(msg.Str("match_id"))?.Ack(uid, msg["seq"].AsLong());
                return;
            default:
                Reply(conn, rid, ErrorCodes.UnknownType, "Unknown message type.");
                return;
        }
    }

    private void Resume(ClientConnection conn, string rid, string matchId, long ackSeq)
    {
        MatchHost host = _matches.Get(matchId);
        if (host != null)
        {
            host.Resume(conn.Identity.Uid, rid, ackSeq);
            return;
        }
        StoredMatch stored = _repo.Get(matchId);
        if (stored == null) Reply(conn, rid, ErrorCodes.MatchNotFound, "Unknown match.");
        else if (!stored.IsParticipant(conn.Identity.Uid)) Reply(conn, rid, ErrorCodes.NotParticipant, "Not a participant of this match.");
        else Reply(conn, rid, ErrorCodes.MatchClosed, "The match has ended (" + stored.Outcome + ").");
    }

    private void Command(ClientConnection conn, string rid, JsonNode msg)
    {
        string matchId = msg.Str("match_id");
        MatchHost host = _matches.Get(matchId);
        if (host == null)
        {
            StoredMatch stored = _repo.Get(matchId);
            bool member = stored != null && stored.IsParticipant(conn.Identity.Uid);
            Reply(conn, rid, member ? ErrorCodes.MatchClosed : stored == null ? ErrorCodes.MatchNotFound : ErrorCodes.NotParticipant,
                member ? "The match has ended." : "Unknown match.");
            return;
        }
        MatchCommand command = CommandCodec.FromJson(msg["command"]);
        if (command.Header.MatchId != matchId)
        {
            Reply(conn, rid, ErrorCodes.BadMessage, "match_id differs from the command header.");
            return;
        }
        host.HandleCommand(conn.Identity.Uid, rid, command);
    }

    private static string SafeRid(JsonNode msg)
    {
        try
        {
            return msg.OptString("rid");
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void Reply(ClientConnection conn, string rid, string code, string message) =>
        conn.Enqueue(new ErrorMessage { Rid = rid, Code = code, Message = message }.ToJson().ToCanonicalString());

    /// <summary>Dotted numeric comparison ("1.2.10" ≥ "1.2.9"); a non-numeric version never passes a non-zero minimum.</summary>
    public static bool VersionAtLeast(string version, string minimum)
    {
        if (string.IsNullOrEmpty(minimum) || minimum == "0.0.0") return true;
        if (string.IsNullOrEmpty(version)) return false;
        string[] a = version.Split('.');
        string[] b = minimum.Split('.');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            long x = 0, y = 0;
            if (i < a.Length && !long.TryParse(a[i], out x)) return false;
            if (i < b.Length && !long.TryParse(b[i], out y)) return false;
            if (x != y) return x > y;
        }
        return true;
    }
}
