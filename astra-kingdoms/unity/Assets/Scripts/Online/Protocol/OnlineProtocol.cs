using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Online.Protocol
{
    /// <summary>
    /// The realtime protocol between the Unity client and the authoritative match service (online
    /// tickets 49-56). It is plain WebSocket text frames, one JSON object per frame, with a type tag
    /// <c>"t"</c>. JSON is the integer-only subset of <see cref="JsonNode"/>, so revisions and
    /// sequence numbers round-trip exactly and no floating point is parsed.
    /// <para>
    /// <b>Why plain WebSockets, not SignalR.</b> Unity's .NET profile ships
    /// <c>System.Net.WebSockets.ClientWebSocket</c>; a SignalR client would add a third-party
    /// dependency (and its own reflection-heavy JSON stack) that IL2CPP stripping must be taught
    /// about. The game needs only ordered messages on one authenticated connection, which a small
    /// typed schema covers. These source files are compiled into both the client and the server, so
    /// the schema cannot drift.
    /// </para>
    /// </summary>
    public static class OnlineProtocol
    {
        /// <summary>Protocol version announced in <c>hello</c>; the server rejects other versions.</summary>
        public const int Version = 1;

        /// <summary>WebSocket endpoint path. Authentication: <c>Authorization: Bearer &lt;ID token&gt;</c> on the upgrade request.</summary>
        public const string WebSocketPath = "/v1/ws";

        /// <summary>Largest accepted text frame (bytes). Longer frames close the connection.</summary>
        public const int MaxMessageBytes = 32 * 1024;

        /// <summary>Header carrying the client build version on the upgrade request (optional; also sent in hello).</summary>
        public const string ClientVersionHeader = "X-AK-Client-Version";
    }

    /// <summary>Message type tags. Client-to-server tags are verbs; server-to-client tags are nouns.</summary>
    public static class MessageTypes
    {
        // Client -> server
        public const string Hello = "hello";
        public const string Ping = "ping";
        public const string RoomCreate = "room.create";
        public const string RoomConfigure = "room.configure";
        public const string RoomJoin = "room.join";
        public const string RoomConfirm = "room.confirm";
        public const string RoomLeave = "room.leave";
        public const string QueueJoin = "queue.join";
        public const string QueueAcceptBot = "queue.accept_bot";
        public const string QueueKeepWaiting = "queue.keep_waiting";
        public const string QueueCancel = "queue.cancel";
        public const string MatchResume = "match.resume";
        public const string MatchCommand = "match.command";
        public const string MatchAck = "match.ack";

        // Server -> client
        public const string Welcome = "welcome";
        public const string Pong = "pong";
        public const string Error = "error";
        public const string RoomState = "room.state";
        public const string RoomClosed = "room.closed";
        public const string QueueState = "queue.state";
        public const string MatchStart = "match.start";
        public const string MatchUpdate = "match.update";
        public const string MatchReceipt = "match.receipt";
        public const string MatchEnd = "match.end";
        public const string ServerDraining = "server.draining";
    }

    /// <summary>Stable service error codes (rules rejections keep the engine's codes, e.g. STALE_STATE_REVISION).</summary>
    public static class ErrorCodes
    {
        public const string BadMessage = "BAD_MESSAGE";
        public const string UnknownType = "UNKNOWN_TYPE";
        public const string HelloRequired = "HELLO_REQUIRED";
        public const string ProtocolUnsupported = "PROTOCOL_UNSUPPORTED";
        public const string ClientTooOld = "CLIENT_TOO_OLD";
        public const string Unauthenticated = "UNAUTHENTICATED";
        public const string NotParticipant = "NOT_PARTICIPANT";
        public const string MatchNotFound = "MATCH_NOT_FOUND";
        public const string MatchClosed = "MATCH_CLOSED";
        public const string RateLimited = "RATE_LIMITED";
        public const string Busy = "PLAYER_BUSY";
        public const string RoomNotFound = "ROOM_NOT_FOUND";
        public const string RoomFull = "ROOM_FULL";
        public const string RoomOwn = "ROOM_OWN";
        public const string RoomRevision = "ROOM_REVISION";
        public const string NotHost = "NOT_HOST";
        public const string NotInRoom = "NOT_IN_ROOM";
        public const string NotQueued = "NOT_QUEUED";
        public const string NoBotOffer = "NO_BOT_OFFER";
        public const string BadCatalog = "BAD_CATALOG";
        public const string ServerDraining = "SERVER_DRAINING";
        public const string Internal = "INTERNAL";
    }

    /// <summary>Close reasons the server uses (WebSocket close description).</summary>
    public static class CloseReasons
    {
        public const string Superseded = "superseded";
        public const string ShuttingDown = "shutting_down";
        public const string PolicyViolation = "policy_violation";
        public const string ProtocolError = "protocol_error";
    }

    /// <summary>Tolerant readers over <see cref="JsonNode"/> for optional members.</summary>
    public static class Json
    {
        /// <summary>Member or null when absent.</summary>
        public static JsonNode Opt(this JsonNode n, string key)
        {
            if (n == null || n.Kind != JsonKind.Object) return null;
            foreach (KeyValuePair<string, JsonNode> m in n.Members)
                if (m.Key == key) return m.Value;
            return null;
        }

        public static bool Has(this JsonNode n, string key)
        {
            JsonNode v = n.Opt(key);
            return v != null && v.Kind != JsonKind.Null;
        }

        public static string OptString(this JsonNode n, string key)
        {
            JsonNode v = n.Opt(key);
            return v == null || v.Kind == JsonKind.Null ? null : v.AsString();
        }

        public static long OptLong(this JsonNode n, string key, long fallback = 0)
        {
            JsonNode v = n.Opt(key);
            return v == null || v.Kind == JsonKind.Null ? fallback : v.AsLong();
        }

        public static bool OptBool(this JsonNode n, string key, bool fallback = false)
        {
            JsonNode v = n.Opt(key);
            return v == null || v.Kind == JsonKind.Null ? fallback : v.AsBool();
        }

        /// <summary>Required string (throws <see cref="FormatException"/> when absent or not a string).</summary>
        public static string Str(this JsonNode n, string key)
        {
            string s = n.OptString(key);
            if (s == null) throw new FormatException("Missing string '" + key + "'.");
            return s;
        }

        /// <summary>Type tag of a message, or null.</summary>
        public static string Type(this JsonNode n) => n.OptString("t");

        /// <summary>A new message object with its type tag.</summary>
        public static JsonNode Message(string type) => JsonNode.Object().Add("t", type);
    }
}
