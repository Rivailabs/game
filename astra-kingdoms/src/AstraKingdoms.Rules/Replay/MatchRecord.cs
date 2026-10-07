using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Replay
{
    /// <summary>An accepted command and its authenticated sender (null = server timer).</summary>
    public sealed class RecordedCommand
    {
        public PlayerSide? Sender { get; }
        public MatchCommand Command { get; }

        public RecordedCommand(PlayerSide? sender, MatchCommand command)
        {
            Sender = sender;
            Command = command ?? throw new ArgumentNullException(nameof(command));
        }
    }

    /// <summary>
    /// Deterministic match record (tickets 10, 20): rules version and hash, seed and commitment,
    /// configuration, the ordered accepted commands, per-round records with stream counters and
    /// state hashes, and the final result. It discloses the seed and every private choice, so a
    /// service publishes it only after the match ends.
    /// </summary>
    public sealed class MatchRecord
    {
        public const string Format = "AK-MATCH-RECORD/1";

        public string RulesVersion;
        public string RulesHashHex;
        /// <summary>
        /// Content hash of the balance bundle the match was pinned to (ticket 24); null for AK-TR-1
        /// itself, whose records stay byte-identical to the compiled engine's. For a tuned match
        /// <see cref="RulesVersion"/> is the bundle ID and <see cref="RulesHashHex"/> its effective
        /// rules hash.
        /// </summary>
        public string BalanceContentHashHex;
        public string MatchId;
        public string SeedHex;
        public string SeedCommitmentHex;
        public MatchConfig Config;
        public PlayerSide FirstAttacker;
        public uint InitiativeStreamCounter;
        public List<RecordedCommand> Commands = new List<RecordedCommand>();
        public List<RoundRecord> Rounds = new List<RoundRecord>();
        /// <summary>Null when the record was taken before the match ended.</summary>
        public MatchResult Result;

        /// <summary>Snapshot of an engine's record (deep copy).</summary>
        public static MatchRecord FromEngine(MatchEngine engine)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            var r = new MatchRecord
            {
                RulesVersion = engine.Config.RulesVersion,
                RulesHashHex = Hex.Encode(engine.RulesHash),
                BalanceContentHashHex = engine.Config.Parameters.IsDefault ? null : engine.Config.Parameters.BalanceContentHashHex,
                MatchId = engine.MatchId,
                SeedHex = Hex.Encode(engine.SeedForRecord),
                SeedCommitmentHex = Hex.Encode(engine.SeedCommitment),
                Config = engine.Config,
                FirstAttacker = engine.FirstAttacker,
                InitiativeStreamCounter = engine.InitiativeStreamCounter,
                Result = engine.Result,
            };
            foreach (var c in engine.CommandLog) r.Commands.Add(new RecordedCommand(c.Sender, c.Command));
            foreach (RoundRecord round in engine.Rounds) r.Rounds.Add(round.Clone());
            return r;
        }

        public string ToJson() => MatchRecordSerializer.Write(this);

        public static MatchRecord FromJson(string json) => MatchRecordSerializer.Read(json);
    }

    /// <summary>Canonical JSON form of <see cref="MatchRecord"/> (hand-written; no external serializer).</summary>
    public static class MatchRecordSerializer
    {
        public static string Write(MatchRecord r) => ToNode(r).ToCanonicalString();

        public static JsonNode ToNode(MatchRecord r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            JsonNode root = JsonNode.Object()
                .Add("format", MatchRecord.Format)
                .Add("rules_version", r.RulesVersion)
                .Add("rules_hash", r.RulesHashHex);
            // Present only for tuned matches, so AK-TR-1 records keep their exact canonical bytes.
            if (r.BalanceContentHashHex != null) root.Add("balance_content_hash", r.BalanceContentHashHex);
            root.Add("match_id", r.MatchId)
                .Add("seed", r.SeedHex)
                .Add("seed_commitment", r.SeedCommitmentHex)
                .Add("config", JsonNode.Object()
                    .Add("rules_version", r.Config.RulesVersion)
                    .Add("mode", r.Config.Mode.ToString())
                    .Add("catalog", r.Config.Catalog.ToString())
                    .Add("card_offers", r.Config.CardOffers.ToString())
                    .Add("terrain_template", r.Config.TerrainTemplateId)
                    .Add("brahmastra", r.Config.BrahmastraEnabled))
                .Add("first_attacker", r.FirstAttacker.ToString())
                .Add("initiative_counter", (ulong)r.InitiativeStreamCounter);

            JsonNode commands = JsonNode.Array();
            foreach (RecordedCommand c in r.Commands) commands.Push(CommandNode(c));
            root.Add("commands", commands);

            JsonNode rounds = JsonNode.Array();
            foreach (RoundRecord round in r.Rounds) rounds.Push(RoundNode(round));
            root.Add("rounds", rounds);

            root.Add("result", r.Result == null ? JsonNode.Null : JsonNode.Object()
                .Add("reason", r.Result.Reason.ToString())
                .Add("winner", r.Result.Winner.HasValue ? r.Result.Winner.Value.ToString() : null)
                .Add("forfeited_by", r.Result.ForfeitedBy.HasValue ? r.Result.ForfeitedBy.Value.ToString() : null)
                .Add("cells_a", r.Result.CellsA)
                .Add("cells_b", r.Result.CellsB)
                .Add("rounds_played", r.Result.RoundsPlayed));
            return root;
        }

        private static JsonNode CommandNode(RecordedCommand rc)
        {
            CommandHeader h = rc.Command.Header;
            JsonNode n = JsonNode.Object()
                .Add("sender", rc.Sender.HasValue ? rc.Sender.Value.ToString() : "server")
                .Add("kind", rc.Command.Kind.ToString())
                .Add("schema_version", h.SchemaVersion)
                .Add("rules_hash", h.RulesHash == null ? null : Hex.Encode(h.RulesHash))
                .Add("match_id", h.MatchId)
                .Add("request_id", h.RequestId)
                .Add("round_index", h.RoundIndex)
                .Add("expected_state_revision", h.ExpectedStateRevision);
            switch (rc.Command)
            {
                case SubmitLoadoutCommand lo:
                    JsonNode weapons = JsonNode.Array();
                    foreach (int w in lo.Weapons) weapons.Push(JsonNode.Of(w));
                    n.Add("weapons", weapons).Add("reserve", lo.Reserve);
                    break;
                case LockInputCommand li:
                    n.Add("volley_index", li.VolleyIndex).Add("weapon_id", li.WeaponId).Add("pitch_qdeg", li.PitchQdeg)
                     .Add("yaw_qdeg", li.YawQdeg).Add("power_percent", li.PowerPercent).Add("dodge", (int)li.Dodge);
                    break;
                case SubmitCutCommand cut:
                    JsonNode vertices = JsonNode.Array();
                    foreach (CellPoint p in cut.Vertices) vertices.Push(JsonNode.Array().Push(JsonNode.Of(p.X)).Push(JsonNode.Of(p.Y)));
                    n.Add("expected_map_revision", cut.ExpectedMapRevision).Add("card_id", (int)cut.CardId)
                     .Add("anchor_cell_id", cut.AnchorCellId).Add("center_x", cut.CenterX).Add("center_y", cut.CenterY)
                     .Add("rotation_index", cut.Rotation).Add("scale_quarters", cut.ScaleQuarters)
                     .Add("mode", cut.Mode.ToString()).Add("vertices", vertices);
                    break;
            }
            return n;
        }

        private static JsonNode RoundNode(RoundRecord r)
        {
            JsonNode volleys = JsonNode.Array();
            foreach (VolleyRecord v in r.Volleys)
            {
                volleys.Push(JsonNode.Object()
                    .Add("volley", v.Volley).Add("weapon_a", v.WeaponA).Add("weapon_b", v.WeaponB)
                    .Add("timeout_a", v.TimeoutA).Add("timeout_b", v.TimeoutB).Add("hp_a", v.HpA).Add("hp_b", v.HpB)
                    .Add("result_after", v.ResultAfter.ToString()).Add("log_hash", v.LogHashHex));
            }
            JsonNode cards = JsonNode.Array();
            foreach (CardId c in r.OfferedCards) cards.Push(JsonNode.Of((int)c));
            return JsonNode.Object()
                .Add("round", r.Round)
                .Add("attacker", r.Attacker.ToString())
                .Add("frontier_cell", r.FrontierCellId)
                .Add("terrain", r.Terrain.ToString())
                .Add("frontier_count", r.FrontierCount)
                .Add("terrain_counter", (ulong)r.TerrainStreamCounter)
                .Add("volleys", volleys)
                .Add("duel_result", r.DuelResult.ToString())
                .Add("hp_a", r.HpA)
                .Add("hp_b", r.HpB)
                .Add("hp_difference", r.HpDifference)
                .Add("offered_cards", cards)
                .Add("cards_counter", (ulong)r.CardsStreamCounter)
                .Add("cut_timed_out", r.CutTimedOut)
                .Add("cells_transferred", r.CellsTransferred)
                .Add("cells_a", r.CellsA)
                .Add("cells_b", r.CellsB)
                .Add("map_revision", r.MapRevision)
                .Add("ownership_hash", r.OwnershipHashHex)
                .Add("state_hash", r.StateHashHex);
        }

        /// <summary>Parses a record. Throws <see cref="FormatException"/> for malformed or unknown formats.</summary>
        public static MatchRecord Read(string json)
        {
            JsonNode root = JsonNode.Parse(json);
            if (root["format"].AsString() != MatchRecord.Format) throw new FormatException("Unknown record format.");
            JsonNode cfg = root["config"];
            var r = new MatchRecord
            {
                RulesVersion = root["rules_version"].AsString(),
                RulesHashHex = root["rules_hash"].AsString(),
                BalanceContentHashHex = HasKey(root, "balance_content_hash") ? root["balance_content_hash"].AsString() : null,
                MatchId = root["match_id"].AsString(),
                SeedHex = root["seed"].AsString(),
                SeedCommitmentHex = root["seed_commitment"].AsString(),
                FirstAttacker = ParseEnum<PlayerSide>(root["first_attacker"]),
                InitiativeStreamCounter = root["initiative_counter"].AsUInt(),
            };
            // The config is reconstructed without validation so that an incompatible rules version
            // can be reported by the replayer rather than failing here.
            r.Config = MatchConfig.CreateUnvalidated(
                ParseEnum<MatchMode>(cfg["mode"]), ParseEnum<CatalogPreset>(cfg["catalog"]), ParseEnum<CardOfferRule>(cfg["card_offers"]),
                cfg["terrain_template"].AsString(), cfg["brahmastra"].AsBool(), cfg["rules_version"].AsString());

            foreach (JsonNode c in root["commands"].AsArray()) r.Commands.Add(ReadCommand(c));
            foreach (JsonNode n in root["rounds"].AsArray()) r.Rounds.Add(ReadRound(n));

            JsonNode res = root["result"];
            if (res.Kind != JsonKind.Null)
            {
                string winner = res["winner"].AsString();
                string forfeited = res["forfeited_by"].AsString();
                r.Result = new MatchResult(ParseEnum<TerminalReason>(res["reason"]),
                    winner == null ? (PlayerSide?)null : ParseEnum<PlayerSide>(winner),
                    forfeited == null ? (PlayerSide?)null : ParseEnum<PlayerSide>(forfeited),
                    res["cells_a"].AsInt(), res["cells_b"].AsInt(), res["rounds_played"].AsInt());
            }
            return r;
        }

        private static RecordedCommand ReadCommand(JsonNode n)
        {
            string senderText = n["sender"].AsString();
            PlayerSide? sender = senderText == "server" ? (PlayerSide?)null : ParseEnum<PlayerSide>(senderText);
            string hash = n["rules_hash"].AsString();
            var header = new CommandHeader(hash == null ? null : Hex.Decode(hash), n["match_id"].AsString(), n["request_id"].AsString(),
                n["round_index"].AsInt(), n["expected_state_revision"].AsULong(), n["schema_version"].AsInt());
            CommandKind kind = ParseEnum<CommandKind>(n["kind"]);
            MatchCommand cmd;
            switch (kind)
            {
                case CommandKind.SubmitLoadout:
                    var weapons = new List<int>();
                    foreach (JsonNode w in n["weapons"].AsArray()) weapons.Add(w.AsInt());
                    cmd = new SubmitLoadoutCommand(header, weapons, n["reserve"].AsInt());
                    break;
                case CommandKind.LockInput:
                    cmd = new LockInputCommand(header, n["volley_index"].AsInt(), n["weapon_id"].AsInt(), n["pitch_qdeg"].AsInt(),
                        n["yaw_qdeg"].AsInt(), n["power_percent"].AsInt(), (Dodge)n["dodge"].AsInt());
                    break;
                case CommandKind.SubmitCut:
                    var vertices = new List<CellPoint>();
                    foreach (JsonNode v in n["vertices"].AsArray())
                    {
                        IReadOnlyList<JsonNode> xy = v.AsArray();
                        if (xy.Count != 2) throw new FormatException("A vertex has two coordinates.");
                        vertices.Add(new CellPoint(xy[0].AsInt(), xy[1].AsInt()));
                    }
                    cmd = new SubmitCutCommand(header, n["expected_map_revision"].AsULong(), (CardId)n["card_id"].AsInt(),
                        n["anchor_cell_id"].AsInt(), n["center_x"].AsInt(), n["center_y"].AsInt(), n["rotation_index"].AsInt(),
                        n["scale_quarters"].AsInt(), ParseEnum<CutMode>(n["mode"]), vertices);
                    break;
                case CommandKind.AdvancePhase:
                    cmd = new AdvancePhaseCommand(header);
                    break;
                default:
                    throw new FormatException("Unknown command kind.");
            }
            return new RecordedCommand(sender, cmd);
        }

        private static RoundRecord ReadRound(JsonNode n)
        {
            var r = new RoundRecord
            {
                Round = n["round"].AsInt(),
                Attacker = ParseEnum<PlayerSide>(n["attacker"]),
                FrontierCellId = n["frontier_cell"].AsInt(),
                Terrain = ParseEnum<TerrainType>(n["terrain"]),
                FrontierCount = n["frontier_count"].AsInt(),
                TerrainStreamCounter = n["terrain_counter"].AsUInt(),
                DuelResult = ParseEnum<DuelResult>(n["duel_result"]),
                HpA = n["hp_a"].AsInt(),
                HpB = n["hp_b"].AsInt(),
                HpDifference = n["hp_difference"].AsInt(),
                CardsStreamCounter = n["cards_counter"].AsUInt(),
                CutTimedOut = n["cut_timed_out"].AsBool(),
                CellsTransferred = n["cells_transferred"].AsInt(),
                CellsA = n["cells_a"].AsInt(),
                CellsB = n["cells_b"].AsInt(),
                MapRevision = n["map_revision"].AsULong(),
                OwnershipHashHex = n["ownership_hash"].AsString(),
                StateHashHex = n["state_hash"].AsString(),
            };
            foreach (JsonNode c in n["offered_cards"].AsArray()) r.OfferedCards.Add((CardId)c.AsInt());
            foreach (JsonNode v in n["volleys"].AsArray())
            {
                r.Volleys.Add(new VolleyRecord
                {
                    Round = r.Round,
                    Volley = v["volley"].AsInt(),
                    WeaponA = v["weapon_a"].AsInt(),
                    WeaponB = v["weapon_b"].AsInt(),
                    TimeoutA = v["timeout_a"].AsBool(),
                    TimeoutB = v["timeout_b"].AsBool(),
                    HpA = v["hp_a"].AsInt(),
                    HpB = v["hp_b"].AsInt(),
                    ResultAfter = ParseEnum<DuelResult>(v["result_after"]),
                    LogHashHex = v["log_hash"].AsString(),
                });
            }
            return r;
        }

        private static bool HasKey(JsonNode obj, string key)
        {
            foreach (var m in obj.Members)
                if (m.Key == key) return true;
            return false;
        }

        private static T ParseEnum<T>(JsonNode n) where T : struct => ParseEnum<T>(n.AsString());

        private static T ParseEnum<T>(string text) where T : struct
        {
            if (text == null || !Enum.TryParse(text, false, out T value) || !Enum.IsDefined(typeof(T), value) || IsNumeric(text))
                throw new FormatException("Unknown " + typeof(T).Name + " '" + text + "'.");
            return value;
        }

        private static bool IsNumeric(string text) => text.Length > 0 && (char.IsDigit(text[0]) || text[0] == '-');
    }
}
