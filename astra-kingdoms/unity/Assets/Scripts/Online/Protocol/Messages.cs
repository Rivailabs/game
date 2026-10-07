using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Online.Protocol
{
    /// <summary>Online phase timers in milliseconds (plan: "Commit reveal and clock behaviour", online column).</summary>
    public sealed class PhaseTimings
    {
        public long AnnounceMs = RulesConstants.TerrainAnnouncementMs;
        public long ChoiceMs = RulesConstants.ChoiceDeadlineMs;
        public long ReplayMs = RulesConstants.ResolutionReplayMaxMs;
        public long CutMs = RulesConstants.OnlineCutWindowMs;
        /// <summary>Private loadout window before round one (service policy; expiry is a technical void).</summary>
        public long SetupMs = 60000;

        public JsonNode ToJson() => JsonNode.Object().Add("announce_ms", AnnounceMs).Add("choice_ms", ChoiceMs)
            .Add("replay_ms", ReplayMs).Add("cut_ms", CutMs).Add("setup_ms", SetupMs);

        public static PhaseTimings Parse(JsonNode n) => new PhaseTimings
        {
            AnnounceMs = n["announce_ms"].AsLong(),
            ChoiceMs = n["choice_ms"].AsLong(),
            ReplayMs = n["replay_ms"].AsLong(),
            CutMs = n["cut_ms"].AsLong(),
            SetupMs = n["setup_ms"].AsLong(),
        };
    }

    /// <summary>Everything a player must see before agreeing to a match: catalog, terrain, mode, rules version and timers.</summary>
    public sealed class MatchRules
    {
        public MatchConfig Config;
        public string RulesHashHex;
        public PhaseTimings Timings = new PhaseTimings();
        /// <summary>V1 public play is unranked; always false in V1.</summary>
        public bool Ranked;

        public JsonNode ToJson() => JsonNode.Object().Add("config", PlayerViewCodec.ConfigNode(Config)).Add("rules_hash", RulesHashHex)
            .Add("timings", Timings.ToJson()).Add("ranked", Ranked);

        public static MatchRules Parse(JsonNode n) => new MatchRules
        {
            Config = PlayerViewCodec.ParseConfig(n["config"]),
            RulesHashHex = n.Str("rules_hash"),
            Timings = PhaseTimings.Parse(n["timings"]),
            Ranked = n["ranked"].AsBool(),
        };
    }

    public static class RoomStatus
    {
        /// <summary>Waiting for a guest.</summary>
        public const string Open = "open";
        /// <summary>Two members; waiting for both confirmations.</summary>
        public const string Full = "full";
    }

    public sealed class RoomStateMessage
    {
        public string Code;
        public long Revision;
        public string Status;
        public bool IsHost;
        public int Members;
        public long ExpiresInMs;
        public MatchRules Rules;
        public bool ConfirmedSelf;
        public bool ConfirmedOpponent;

        public JsonNode ToJson() => Json.Message(MessageTypes.RoomState).Add("code", Code).Add("revision", Revision)
            .Add("status", Status).Add("is_host", IsHost).Add("members", Members).Add("expires_in_ms", ExpiresInMs)
            .Add("rules", Rules.ToJson()).Add("confirmed_self", ConfirmedSelf).Add("confirmed_opponent", ConfirmedOpponent);

        public static RoomStateMessage Parse(JsonNode n) => new RoomStateMessage
        {
            Code = n.Str("code"),
            Revision = n["revision"].AsLong(),
            Status = n.Str("status"),
            IsHost = n["is_host"].AsBool(),
            Members = n["members"].AsInt(),
            ExpiresInMs = n["expires_in_ms"].AsLong(),
            Rules = MatchRules.Parse(n["rules"]),
            ConfirmedSelf = n["confirmed_self"].AsBool(),
            ConfirmedOpponent = n["confirmed_opponent"].AsBool(),
        };
    }

    public static class RoomCloseReasons
    {
        public const string Expired = "expired";
        public const string HostLeft = "host_left";
        public const string HostDisconnected = "host_disconnected";
        public const string Left = "left";
        public const string Started = "started";
        public const string ServerShutdown = "server_shutdown";
    }

    public sealed class RoomClosedMessage
    {
        public string Code;
        public string Reason;

        public JsonNode ToJson() => Json.Message(MessageTypes.RoomClosed).Add("code", Code).Add("reason", Reason);

        public static RoomClosedMessage Parse(JsonNode n) => new RoomClosedMessage { Code = n.Str("code"), Reason = n.Str("reason") };
    }

    public static class QueueStatus
    {
        public const string Waiting = "waiting";
        /// <summary>The wait passed the threshold: a clearly labelled, unranked bot match is offered (consent required).</summary>
        public const string BotOffer = "bot_offer";
        public const string Cancelled = "cancelled";
        public const string Matched = "matched";
    }

    public sealed class QueueStateMessage
    {
        public string Status;
        public long WaitedMs;
        public long BotOfferAfterMs;
        public string Catalog;
        /// <summary>Label of the offered bot (e.g. "Bot - Normal"), when an offer is open.</summary>
        public string BotLabel;

        public JsonNode ToJson() => Json.Message(MessageTypes.QueueState).Add("status", Status).Add("waited_ms", WaitedMs)
            .Add("bot_offer_after_ms", BotOfferAfterMs).Add("catalog", Catalog).Add("bot_label", BotLabel);

        public static QueueStateMessage Parse(JsonNode n) => new QueueStateMessage
        {
            Status = n.Str("status"),
            WaitedMs = n["waited_ms"].AsLong(),
            BotOfferAfterMs = n["bot_offer_after_ms"].AsLong(),
            Catalog = n.OptString("catalog"),
            BotLabel = n.OptString("bot_label"),
        };
    }

    public static class OpponentKinds
    {
        public const string Human = "human";
        public const string Bot = "bot";
    }

    public static class MatchOrigins
    {
        public const string FriendRoom = "friend_room";
        public const string Queue = "queue";
        public const string QueueBot = "queue_bot";
    }

    public sealed class MatchStartMessage
    {
        public string MatchId;
        public PlayerSide Side;
        /// <summary><see cref="OpponentKinds"/>; a bot is always labelled as a bot.</summary>
        public string OpponentKind;
        public string OpponentLabel;
        public string Origin;
        public MatchRules Rules;

        public bool OpponentIsBot => OpponentKind == OpponentKinds.Bot;

        public JsonNode ToJson() => Json.Message(MessageTypes.MatchStart).Add("match_id", MatchId).Add("side", Side.ToString())
            .Add("opponent", JsonNode.Object().Add("kind", OpponentKind).Add("label", OpponentLabel))
            .Add("origin", Origin).Add("rules", Rules.ToJson());

        public static MatchStartMessage Parse(JsonNode n)
        {
            string side = n.Str("side");
            if (side != "A" && side != "B") throw new FormatException("side must be A or B.");
            JsonNode opp = n["opponent"];
            return new MatchStartMessage
            {
                MatchId = n.Str("match_id"),
                Side = side == "A" ? PlayerSide.A : PlayerSide.B,
                OpponentKind = opp.Str("kind"),
                OpponentLabel = opp.OptString("label"),
                Origin = n.Str("origin"),
                Rules = MatchRules.Parse(n["rules"]),
            };
        }
    }

    /// <summary>A public, secret-free match event (mirrors <see cref="MatchEvent"/>).</summary>
    public sealed class WireEvent
    {
        public long Seq;
        public string Type;
        public int Round;
        public int Volley;
        /// <summary>"A", "B" or null.</summary>
        public string Player;
        public int Amount;
        public int Amount2;
        public ulong StateRevision;

        public static WireEvent From(MatchEvent e) => new WireEvent
        {
            Seq = e.Sequence,
            Type = e.Type.ToString(),
            Round = e.Round,
            Volley = e.Volley,
            Player = e.Player.HasValue ? e.Player.Value.ToString() : null,
            Amount = e.Amount,
            Amount2 = e.Amount2,
            StateRevision = e.StateRevision,
        };

        public PlayerSide? Side => Player == "A" ? PlayerSide.A : Player == "B" ? PlayerSide.B : (PlayerSide?)null;

        public JsonNode ToJson() => JsonNode.Object().Add("seq", Seq).Add("type", Type).Add("round", Round).Add("volley", Volley)
            .Add("player", Player).Add("amount", Amount).Add("amount2", Amount2).Add("rev", StateRevision);

        public static WireEvent Parse(JsonNode n) => new WireEvent
        {
            Seq = n["seq"].AsLong(),
            Type = n.Str("type"),
            Round = n["round"].AsInt(),
            Volley = n["volley"].AsInt(),
            Player = n.OptString("player"),
            Amount = n["amount"].AsInt(),
            Amount2 = n["amount2"].AsInt(),
            StateRevision = n["rev"].AsULong(),
        };
    }

    /// <summary>
    /// The recipient's private view plus the public events it has not seen. A snapshot (reconnect)
    /// always carries the ownership map and every event after the acknowledged sequence.
    /// </summary>
    public sealed class MatchUpdateMessage
    {
        public string MatchId;
        public bool Snapshot;
        /// <summary>Encoded <see cref="PlayerView"/> (see <see cref="PlayerViewCodec"/>).</summary>
        public JsonNode View;
        public List<WireEvent> Events = new List<WireEvent>();
        public long LastSeq;
        /// <summary>Milliseconds left on the server's phase clock at send time; -1 when no clock runs.</summary>
        public long DeadlineRemainingMs = -1;
        public bool OpponentConnected;

        public JsonNode ToJson()
        {
            JsonNode events = JsonNode.Array();
            foreach (WireEvent e in Events) events.Push(e.ToJson());
            return Json.Message(MessageTypes.MatchUpdate).Add("match_id", MatchId).Add("snapshot", Snapshot).Add("view", View)
                .Add("events", events).Add("last_seq", LastSeq).Add("deadline_remaining_ms", DeadlineRemainingMs)
                .Add("opponent_connected", OpponentConnected);
        }

        public static MatchUpdateMessage Parse(JsonNode n)
        {
            var m = new MatchUpdateMessage
            {
                MatchId = n.Str("match_id"),
                Snapshot = n["snapshot"].AsBool(),
                View = n["view"],
                LastSeq = n["last_seq"].AsLong(),
                DeadlineRemainingMs = n["deadline_remaining_ms"].AsLong(),
                OpponentConnected = n.OptBool("opponent_connected"),
            };
            foreach (JsonNode e in n["events"].AsArray()) m.Events.Add(WireEvent.Parse(e));
            return m;
        }
    }

    public sealed class ReceiptMessage
    {
        public string Rid;
        public string MatchId;
        public string RequestId;
        public string Kind;
        public bool Accepted;
        public string Code;
        public string Message;
        public ulong InputRevision;
        public ulong StateRevision;
        public int CellsTransferred;

        public static ReceiptMessage From(string rid, CommandReceipt r) => new ReceiptMessage
        {
            Rid = rid,
            MatchId = r.MatchId,
            RequestId = r.RequestId,
            Kind = r.Kind.ToString(),
            Accepted = r.Accepted,
            Code = r.RejectCode,
            Message = r.Message,
            InputRevision = r.InputRevision,
            StateRevision = r.StateRevision,
            CellsTransferred = r.CellsTransferred,
        };

        public JsonNode ToJson() => Json.Message(MessageTypes.MatchReceipt).Add("rid", Rid).Add("match_id", MatchId)
            .Add("request_id", RequestId).Add("kind", Kind).Add("accepted", Accepted).Add("code", Code).Add("message", Message)
            .Add("input_revision", InputRevision).Add("state_revision", StateRevision).Add("cells_transferred", CellsTransferred);

        public static ReceiptMessage Parse(JsonNode n) => new ReceiptMessage
        {
            Rid = n.OptString("rid"),
            MatchId = n.OptString("match_id"),
            RequestId = n.OptString("request_id"),
            Kind = n.OptString("kind"),
            Accepted = n["accepted"].AsBool(),
            Code = n.OptString("code"),
            Message = n.OptString("message"),
            InputRevision = n["input_revision"].AsULong(),
            StateRevision = n["state_revision"].AsULong(),
            CellsTransferred = n["cells_transferred"].AsInt(),
        };
    }

    /// <summary>How the service settled a match.</summary>
    public static class MatchOutcomes
    {
        /// <summary>90% territory or round eight finished (a normally completed match).</summary>
        public const string Completed = "completed";
        /// <summary>Two consecutive selection timeouts by one player; not a normally completed match.</summary>
        public const string Forfeit = "forfeit";
        /// <summary>Both players reached the forfeit condition together; no winner reward.</summary>
        public const string Void = "void";
        /// <summary>The service could not resolve the match (setup expiry, shutdown, failure): no reward and no loss.</summary>
        public const string TechnicalVoid = "technical_void";
    }

    public sealed class MatchEndMessage
    {
        public string MatchId;
        public string Outcome;
        /// <summary>Null for a technical void.</summary>
        public MatchResult Result;
        /// <summary>Unique result ID: rewards are granted at most once per (result ID, player).</summary>
        public string ResultId;
        public string Detail;
        public int RewardXp;
        public int RewardCoins;

        public JsonNode ToJson() => Json.Message(MessageTypes.MatchEnd).Add("match_id", MatchId).Add("outcome", Outcome)
            .Add("result", PlayerViewCodec.ResultNode(Result)).Add("result_id", ResultId).Add("detail", Detail)
            .Add("reward", JsonNode.Object().Add("xp", RewardXp).Add("coins", RewardCoins));

        public static MatchEndMessage Parse(JsonNode n)
        {
            JsonNode reward = n.Opt("reward");
            return new MatchEndMessage
            {
                MatchId = n.Str("match_id"),
                Outcome = n.Str("outcome"),
                Result = PlayerViewCodec.ParseResult(n["result"]),
                ResultId = n.OptString("result_id"),
                Detail = n.OptString("detail"),
                RewardXp = reward == null ? 0 : (int)reward.OptLong("xp"),
                RewardCoins = reward == null ? 0 : (int)reward.OptLong("coins"),
            };
        }
    }

    public sealed class WelcomeMessage
    {
        public int Protocol = OnlineProtocol.Version;
        public string ServerVersion;
        public string RulesVersion;
        public string RulesHashHex;
        /// <summary>Pseudonymous player reference (never the raw account ID).</summary>
        public string PlayerRef;
        /// <summary>The match this player is still part of, for reconnection; null otherwise.</summary>
        public string ActiveMatchId;

        public JsonNode ToJson() => Json.Message(MessageTypes.Welcome).Add("protocol", Protocol).Add("server_version", ServerVersion)
            .Add("rules_version", RulesVersion).Add("rules_hash", RulesHashHex).Add("player_ref", PlayerRef)
            .Add("active_match_id", ActiveMatchId);

        public static WelcomeMessage Parse(JsonNode n) => new WelcomeMessage
        {
            Protocol = n["protocol"].AsInt(),
            ServerVersion = n.OptString("server_version"),
            RulesVersion = n.OptString("rules_version"),
            RulesHashHex = n.OptString("rules_hash"),
            PlayerRef = n.OptString("player_ref"),
            ActiveMatchId = n.OptString("active_match_id"),
        };
    }

    public sealed class ErrorMessage
    {
        public string Rid;
        public string Code;
        public string Message;

        public JsonNode ToJson() => Json.Message(MessageTypes.Error).Add("rid", Rid).Add("code", Code).Add("message", Message);

        public static ErrorMessage Parse(JsonNode n) => new ErrorMessage { Rid = n.OptString("rid"), Code = n.Str("code"), Message = n.OptString("message") };
    }

    /// <summary>Builders for every client-to-server message.</summary>
    public static class ClientMessages
    {
        public static JsonNode Hello(string clientVersion) =>
            Json.Message(MessageTypes.Hello).Add("protocol", OnlineProtocol.Version).Add("client_version", clientVersion);

        public static JsonNode Ping(string rid) => Json.Message(MessageTypes.Ping).Add("rid", rid);

        public static JsonNode RoomCreate(string rid, CatalogPreset catalog) =>
            Json.Message(MessageTypes.RoomCreate).Add("rid", rid).Add("catalog", catalog.ToString());

        public static JsonNode RoomConfigure(string rid, CatalogPreset catalog) =>
            Json.Message(MessageTypes.RoomConfigure).Add("rid", rid).Add("catalog", catalog.ToString());

        public static JsonNode RoomJoin(string rid, string code) => Json.Message(MessageTypes.RoomJoin).Add("rid", rid).Add("code", code);

        public static JsonNode RoomConfirm(string rid, long revision) =>
            Json.Message(MessageTypes.RoomConfirm).Add("rid", rid).Add("revision", revision);

        public static JsonNode RoomLeave(string rid) => Json.Message(MessageTypes.RoomLeave).Add("rid", rid);

        public static JsonNode QueueJoin(string rid, CatalogPreset catalog) =>
            Json.Message(MessageTypes.QueueJoin).Add("rid", rid).Add("catalog", catalog.ToString());

        public static JsonNode QueueAcceptBot(string rid) => Json.Message(MessageTypes.QueueAcceptBot).Add("rid", rid);

        public static JsonNode QueueKeepWaiting(string rid) => Json.Message(MessageTypes.QueueKeepWaiting).Add("rid", rid);

        public static JsonNode QueueCancel(string rid) => Json.Message(MessageTypes.QueueCancel).Add("rid", rid);

        public static JsonNode MatchResume(string rid, string matchId, long ackSeq) =>
            Json.Message(MessageTypes.MatchResume).Add("rid", rid).Add("match_id", matchId).Add("ack_seq", ackSeq);

        public static JsonNode MatchCommand(string rid, MatchCommand command) =>
            Json.Message(MessageTypes.MatchCommand).Add("rid", rid).Add("match_id", command.Header.MatchId).Add("command", CommandCodec.ToJson(command));

        public static JsonNode MatchAck(string matchId, long seq) => Json.Message(MessageTypes.MatchAck).Add("match_id", matchId).Add("seq", seq);

        /// <summary>Parses a catalog name ("Starter" or "Full"); throws <see cref="FormatException"/> otherwise.</summary>
        public static CatalogPreset ParseCatalog(string s)
        {
            if (s == "Starter") return CatalogPreset.Starter;
            if (s == "Full") return CatalogPreset.Full;
            throw new FormatException("catalog must be Starter or Full.");
        }
    }
}
