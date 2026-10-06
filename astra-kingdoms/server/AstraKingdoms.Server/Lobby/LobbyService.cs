using System.Security.Cryptography;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Identity;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AstraKingdoms.Server.Lobby;

/// <summary>Friend-room codes: an alphabet without look-alikes (no 0/O, 1/I/L), drawn from a CSPRNG.</summary>
public static class RoomCodes
{
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string New(int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Uppercases and drops spaces and dashes; null when any other character is outside the alphabet.</summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrEmpty(input) || input.Length > 32) return null;
        var chars = new List<char>(input.Length);
        foreach (char raw in input)
        {
            if (raw == ' ' || raw == '-') continue;
            char c = char.ToUpperInvariant(raw);
            if (Alphabet.IndexOf(c) < 0) return null;
            chars.Add(c);
        }
        return new string(chars.ToArray());
    }
}

/// <summary>
/// Friend rooms (ticket 52) and the public queue with the optional labelled bot match (ticket 53).
/// One lock covers both, so a person is in at most one room or queue entry at a time and can never
/// end up in two matches. Timers (room expiry, host grace, bot offer) use <see cref="TimeProvider"/>.
/// </summary>
public sealed class LobbyService
{
    private sealed class Room
    {
        public string Code;
        public string HostUid;
        public string GuestUid;
        public CatalogPreset Catalog;
        public long Revision = 1;
        public DateTimeOffset ExpiresAt;
        public readonly HashSet<string> Confirmed = new(StringComparer.Ordinal);
        public ITimer ExpiryTimer;
        public ITimer HostGraceTimer;
        public ITimer GuestGraceTimer;

        public bool Has(string uid) => uid == HostUid || uid == GuestUid;
    }

    private sealed class QueueEntry
    {
        public string Uid;
        public CatalogPreset Catalog;
        public DateTimeOffset JoinedAt;
        public bool BotOffered;
        public ITimer OfferTimer;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Room> _roomByUid = new(StringComparer.Ordinal);
    private readonly List<QueueEntry> _queue = new();
    private readonly MatchRegistry _matches;
    private readonly IPlayerChannel _channel;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ServerOptions _options;
    private readonly ILogger<LobbyService> _log;

    public LobbyService(MatchRegistry matches, IPlayerChannel channel, IAuditLog audit, TimeProvider time, IOptions<ServerOptions> options,
        ILogger<LobbyService> log)
    {
        _matches = matches;
        _channel = channel;
        _audit = audit;
        _time = time;
        _options = options.Value;
        _log = log;
    }

    /// <summary>Set during shutdown: new rooms and queue entries are refused.</summary>
    public bool Draining { get; set; }

    public int RoomCount
    {
        get { lock (_gate) return _rooms.Count; }
    }

    public int QueueLength
    {
        get { lock (_gate) return _queue.Count; }
    }

    /// <summary>The rules both players see before confirming (catalog, terrain, mode, rules hash, timers, unranked).</summary>
    public MatchRules RulesFor(CatalogPreset catalog) => new()
    {
        Config = ConfigFor(catalog),
        RulesHashHex = RulesBundle.HashHex,
        Timings = _options.Timings.ToWire(),
        Ranked = false,
    };

    public static MatchConfig ConfigFor(CatalogPreset catalog) =>
        catalog == CatalogPreset.Full ? MatchConfig.V1Full(MatchMode.Online) : MatchConfig.V1Starter(MatchMode.Online);

    // ------------------------------------------------------------------ rooms

    public void CreateRoom(string uid, string rid, CatalogPreset catalog)
    {
        lock (_gate)
        {
            if (!CheckFree(uid, rid)) return;
            string code;
            int guard = 0;
            do code = RoomCodes.New(_options.Rooms.CodeLength);
            while (_rooms.ContainsKey(code) && ++guard < 100);
            var room = new Room
            {
                Code = code,
                HostUid = uid,
                Catalog = catalog,
                ExpiresAt = _time.GetUtcNow().AddSeconds(_options.Rooms.ExpirySeconds),
            };
            room.ExpiryTimer = _time.CreateTimer(_ => Expire(room), null, TimeSpan.FromSeconds(_options.Rooms.ExpirySeconds), Timeout.InfiniteTimeSpan);
            _rooms[code] = room;
            _roomByUid[uid] = room;
            _audit.Append(null, PlayerRef.Of(uid), "room_created", "code=" + code + " catalog=" + catalog);
            SendRoom(room);
        }
    }

    public void ConfigureRoom(string uid, string rid, CatalogPreset catalog)
    {
        lock (_gate)
        {
            if (!_roomByUid.TryGetValue(uid, out Room room))
            {
                Error(uid, rid, ErrorCodes.NotInRoom, "You are not in a room.");
                return;
            }
            if (room.HostUid != uid)
            {
                Error(uid, rid, ErrorCodes.NotHost, "Only the host chooses the catalog.");
                return;
            }
            room.Catalog = catalog;
            room.Revision++;
            room.Confirmed.Clear(); // everyone must confirm what they now see
            SendRoom(room);
        }
    }

    public void JoinRoom(string uid, string rid, string rawCode)
    {
        lock (_gate)
        {
            string code = RoomCodes.Normalize(rawCode);
            if (code == null || !_rooms.TryGetValue(code, out Room room))
            {
                Error(uid, rid, ErrorCodes.RoomNotFound, "No open room has that code (it may have expired).");
                return;
            }
            if (room.HostUid == uid)
            {
                Error(uid, rid, ErrorCodes.RoomOwn, "This is your own room.");
                return;
            }
            if (room.GuestUid == uid)
            {
                SendRoom(room); // idempotent re-join
                return;
            }
            if (room.GuestUid != null)
            {
                Error(uid, rid, ErrorCodes.RoomFull, "The room already has two players.");
                return;
            }
            if (!CheckFree(uid, rid)) return;
            room.GuestUid = uid;
            room.Revision++;
            room.Confirmed.Clear();
            _roomByUid[uid] = room;
            _audit.Append(null, PlayerRef.Of(uid), "room_joined", "code=" + code);
            SendRoom(room);
        }
    }

    /// <summary>Confirms the room at the revision the player saw. The match starts when both have confirmed.</summary>
    public void ConfirmRoom(string uid, string rid, long revision)
    {
        lock (_gate)
        {
            if (!_roomByUid.TryGetValue(uid, out Room room))
            {
                Error(uid, rid, ErrorCodes.NotInRoom, "You are not in a room.");
                return;
            }
            if (revision != room.Revision)
            {
                Error(uid, rid, ErrorCodes.RoomRevision, "The room changed; review it and confirm again.");
                SendRoom(room);
                return;
            }
            room.Confirmed.Add(uid);
            if (room.GuestUid != null && room.Confirmed.Contains(room.HostUid) && room.Confirmed.Contains(room.GuestUid))
            {
                MatchHost match = _matches.Create(ConfigFor(room.Catalog), SeatSpec.Human(room.HostUid, MatchRegistry.LabelFor(MatchOrigins.FriendRoom)),
                    SeatSpec.Human(room.GuestUid, MatchRegistry.LabelFor(MatchOrigins.FriendRoom)), MatchOrigins.FriendRoom);
                if (match == null)
                {
                    Error(uid, rid, ErrorCodes.Busy, "A player is already in a match.");
                    return;
                }
                CloseRoom(room, RoomCloseReasons.Started, notify: false);
                return;
            }
            SendRoom(room);
        }
    }

    public void LeaveRoom(string uid, string rid)
    {
        lock (_gate)
        {
            if (!_roomByUid.TryGetValue(uid, out Room room))
            {
                Error(uid, rid, ErrorCodes.NotInRoom, "You are not in a room.");
                return;
            }
            LeaveLocked(room, uid, RoomCloseReasons.HostLeft);
        }
    }

    private void LeaveLocked(Room room, string uid, string hostReason)
    {
        if (room.HostUid == uid)
        {
            CloseRoom(room, hostReason, notify: true);
            return;
        }
        room.GuestUid = null;
        room.GuestGraceTimer?.Dispose();
        room.GuestGraceTimer = null;
        room.Revision++;
        room.Confirmed.Clear();
        _roomByUid.Remove(uid);
        _channel.Send(uid, new RoomClosedMessage { Code = room.Code, Reason = RoomCloseReasons.Left }.ToJson());
        SendRoom(room);
    }

    private void Expire(Room room)
    {
        lock (_gate)
        {
            if (_rooms.TryGetValue(room.Code, out Room current) && current == room) CloseRoom(room, RoomCloseReasons.Expired, notify: true);
        }
    }

    private void CloseRoom(Room room, string reason, bool notify)
    {
        room.ExpiryTimer?.Dispose();
        room.HostGraceTimer?.Dispose();
        room.GuestGraceTimer?.Dispose();
        _rooms.Remove(room.Code);
        foreach (string uid in new[] { room.HostUid, room.GuestUid })
        {
            if (uid == null) continue;
            _roomByUid.Remove(uid);
            if (notify) _channel.Send(uid, new RoomClosedMessage { Code = room.Code, Reason = reason }.ToJson());
        }
        _audit.Append(null, PlayerRef.Of(room.HostUid), "room_closed", "code=" + room.Code + " reason=" + reason);
    }

    private void SendRoom(Room room)
    {
        long expiresIn = Math.Max(0, (long)(room.ExpiresAt - _time.GetUtcNow()).TotalMilliseconds);
        foreach (string uid in new[] { room.HostUid, room.GuestUid })
        {
            if (uid == null) continue;
            string other = uid == room.HostUid ? room.GuestUid : room.HostUid;
            _channel.Send(uid, new RoomStateMessage
            {
                Code = room.Code,
                Revision = room.Revision,
                Status = room.GuestUid == null ? RoomStatus.Open : RoomStatus.Full,
                IsHost = uid == room.HostUid,
                Members = room.GuestUid == null ? 1 : 2,
                ExpiresInMs = expiresIn,
                Rules = RulesFor(room.Catalog),
                ConfirmedSelf = room.Confirmed.Contains(uid),
                ConfirmedOpponent = other != null && room.Confirmed.Contains(other),
            }.ToJson());
        }
    }

    // ------------------------------------------------------------------ queue

    public void JoinQueue(string uid, string rid, CatalogPreset catalog)
    {
        lock (_gate)
        {
            if (_queue.Any(e => e.Uid == uid))
            {
                SendQueue(_queue.First(e => e.Uid == uid), QueueStatus.Waiting); // idempotent
                return;
            }
            if (!CheckFree(uid, rid)) return;
            QueueEntry partner = _queue.FirstOrDefault(e => e.Catalog == catalog);
            if (partner != null)
            {
                RemoveEntry(partner);
                string label = MatchRegistry.LabelFor(MatchOrigins.Queue);
                MatchHost match = _matches.Create(ConfigFor(catalog), SeatSpec.Human(partner.Uid, label), SeatSpec.Human(uid, label), MatchOrigins.Queue);
                if (match == null)
                {
                    Error(uid, rid, ErrorCodes.Busy, "A player is already in a match.");
                    return;
                }
                SendQueue(partner, QueueStatus.Matched);
                SendQueue(new QueueEntry { Uid = uid, Catalog = catalog, JoinedAt = _time.GetUtcNow() }, QueueStatus.Matched);
                return;
            }
            var entry = new QueueEntry { Uid = uid, Catalog = catalog, JoinedAt = _time.GetUtcNow() };
            entry.OfferTimer = _time.CreateTimer(_ => OfferBot(entry), null, TimeSpan.FromSeconds(_options.Queue.BotOfferAfterSeconds),
                Timeout.InfiniteTimeSpan);
            _queue.Add(entry);
            SendQueue(entry, QueueStatus.Waiting);
        }
    }

    private void OfferBot(QueueEntry entry)
    {
        lock (_gate)
        {
            if (!_queue.Contains(entry)) return;
            entry.BotOffered = true;
            SendQueue(entry, QueueStatus.BotOffer);
        }
    }

    /// <summary>Consent to the offered bot match. Only valid after the offer; a second consent cannot create a second match.</summary>
    public void AcceptBot(string uid, string rid)
    {
        lock (_gate)
        {
            QueueEntry entry = _queue.FirstOrDefault(e => e.Uid == uid);
            if (entry == null)
            {
                Error(uid, rid, ErrorCodes.NotQueued, "You are not waiting in the queue.");
                return;
            }
            if (!entry.BotOffered)
            {
                Error(uid, rid, ErrorCodes.NoBotOffer, "No bot match has been offered yet.");
                return;
            }
            RemoveEntry(entry);
            MatchHost match = _matches.Create(ConfigFor(entry.Catalog), SeatSpec.Human(uid, MatchRegistry.LabelFor(MatchOrigins.Queue)),
                SeatSpec.ForBot(_options.Queue.BotDifficulty), MatchOrigins.QueueBot);
            if (match == null)
            {
                Error(uid, rid, ErrorCodes.Busy, "You are already in a match.");
                return;
            }
            _audit.Append(match.MatchId, PlayerRef.Of(uid), "bot_match_consented", "unranked=true difficulty=" + _options.Queue.BotDifficulty);
            SendQueue(entry, QueueStatus.Matched);
        }
    }

    /// <summary>Declines the bot offer for now and keeps waiting for a person (the offer stays available).</summary>
    public void KeepWaiting(string uid, string rid)
    {
        lock (_gate)
        {
            QueueEntry entry = _queue.FirstOrDefault(e => e.Uid == uid);
            if (entry == null)
            {
                Error(uid, rid, ErrorCodes.NotQueued, "You are not waiting in the queue.");
                return;
            }
            SendQueue(entry, entry.BotOffered ? QueueStatus.BotOffer : QueueStatus.Waiting);
        }
    }

    public void CancelQueue(string uid, string rid)
    {
        lock (_gate)
        {
            QueueEntry entry = _queue.FirstOrDefault(e => e.Uid == uid);
            if (entry == null)
            {
                Error(uid, rid, ErrorCodes.NotQueued, "You are not waiting in the queue.");
                return;
            }
            RemoveEntry(entry);
            SendQueue(entry, QueueStatus.Cancelled);
        }
    }

    private void RemoveEntry(QueueEntry entry)
    {
        entry.OfferTimer?.Dispose();
        _queue.Remove(entry);
    }

    private void SendQueue(QueueEntry entry, string status) =>
        _channel.Send(entry.Uid, new QueueStateMessage
        {
            Status = status,
            WaitedMs = (long)(_time.GetUtcNow() - entry.JoinedAt).TotalMilliseconds,
            BotOfferAfterMs = _options.Queue.BotOfferAfterSeconds * 1000L,
            Catalog = entry.Catalog.ToString(),
            BotLabel = entry.BotOffered || status == QueueStatus.BotOffer ? SeatSpec.BotLabel(_options.Queue.BotDifficulty) : null,
        }.ToJson());

    // ------------------------------------------------------------------ presence

    /// <summary>
    /// A player's last connection closed. Queue entries are dropped at once (nobody is waiting on
    /// the other end); room members keep their seat for the host grace period.
    /// </summary>
    public void OnDisconnected(string uid)
    {
        lock (_gate)
        {
            QueueEntry entry = _queue.FirstOrDefault(e => e.Uid == uid);
            if (entry != null) RemoveEntry(entry);
            if (_roomByUid.TryGetValue(uid, out Room room))
            {
                TimeSpan grace = TimeSpan.FromSeconds(_options.Rooms.HostGraceSeconds);
                if (room.HostUid == uid)
                {
                    room.HostGraceTimer?.Dispose();
                    room.HostGraceTimer = _time.CreateTimer(_ => GraceExpired(room, uid), null, grace, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    room.GuestGraceTimer?.Dispose();
                    room.GuestGraceTimer = _time.CreateTimer(_ => GraceExpired(room, uid), null, grace, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>A player reconnected: cancel their grace timer and resend room state.</summary>
    public void OnConnected(string uid)
    {
        lock (_gate)
        {
            if (!_roomByUid.TryGetValue(uid, out Room room)) return;
            if (room.HostUid == uid)
            {
                room.HostGraceTimer?.Dispose();
                room.HostGraceTimer = null;
            }
            else
            {
                room.GuestGraceTimer?.Dispose();
                room.GuestGraceTimer = null;
            }
            SendRoom(room);
        }
    }

    private void GraceExpired(Room room, string uid)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(room.Code, out Room current) || current != room || !room.Has(uid)) return;
            if (_channel.IsConnected(uid)) return;
            _log.LogInformation("Room {Code}: {Player} did not return", room.Code, PlayerRef.Of(uid));
            LeaveLocked(room, uid, RoomCloseReasons.HostDisconnected);
        }
    }

    /// <summary>Shutdown: close every room and queue entry with a clear reason.</summary>
    public void CloseAll()
    {
        lock (_gate)
        {
            Draining = true;
            foreach (Room room in _rooms.Values.ToList()) CloseRoom(room, RoomCloseReasons.ServerShutdown, notify: true);
            foreach (QueueEntry e in _queue.ToList())
            {
                RemoveEntry(e);
                SendQueue(e, QueueStatus.Cancelled);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A person may hold one room seat, one queue entry or one active match at a time.</summary>
    private bool CheckFree(string uid, string rid)
    {
        if (Draining)
        {
            Error(uid, rid, ErrorCodes.ServerDraining, "The service is restarting; try again shortly.");
            return false;
        }
        if (_matches.ActiveFor(uid) != null || _roomByUid.ContainsKey(uid) || _queue.Any(e => e.Uid == uid))
        {
            Error(uid, rid, ErrorCodes.Busy, "Leave your current room, queue or match first.");
            return false;
        }
        return true;
    }

    private void Error(string uid, string rid, string code, string message) =>
        _channel.Send(uid, new ErrorMessage { Rid = rid, Code = code, Message = message }.ToJson());
}
