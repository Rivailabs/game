using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Online.Protocol
{
    /// <summary>
    /// JSON form of the three player commands, with the plan's field names ("Command fields and
    /// units"). There is deliberately no player field: the authenticated connection decides the
    /// seat. AdvancePhase is server-only and cannot be decoded from a client.
    /// <para>
    /// Decoding checks types and coarse bounds only; the rules engine performs every rules check
    /// (ranges, phase, revision, duplicates) and returns its stable rejection codes.
    /// </para>
    /// </summary>
    public static class CommandCodec
    {
        /// <summary>Upper bound on manual cut vertices accepted on the wire (the rules allow 128).</summary>
        public const int MaxWireVertices = 512;

        public const string KindLoadout = "SubmitLoadout";
        public const string KindLock = "LockInput";
        public const string KindCut = "SubmitCut";

        public static JsonNode ToJson(MatchCommand cmd)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            CommandHeader h = cmd.Header;
            JsonNode n = JsonNode.Object()
                .Add("kind", cmd.Kind.ToString())
                .Add("schema_version", h.SchemaVersion)
                .Add("rules_hash", h.RulesHash == null ? null : Hex.Encode(h.RulesHash))
                .Add("match_id", h.MatchId)
                .Add("request_id", h.RequestId)
                .Add("round_index", h.RoundIndex)
                .Add("expected_state_revision", h.ExpectedStateRevision);
            switch (cmd)
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
                     .Add("mode", cut.Mode == CutMode.Auto ? "Auto" : "Manual").Add("vertices", vertices);
                    break;
                default:
                    throw new ArgumentException("Only player commands travel from clients.", nameof(cmd));
            }
            return n;
        }

        /// <summary>Decodes a player command. Throws <see cref="FormatException"/> on malformed input.</summary>
        public static MatchCommand FromJson(JsonNode n)
        {
            if (n == null || n.Kind != JsonKind.Object) throw new FormatException("command must be an object.");
            string hashHex = n.OptString("rules_hash");
            byte[] hash;
            try
            {
                hash = hashHex == null ? null : Hex.Decode(hashHex);
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException)
            {
                throw new FormatException("rules_hash must be hex.");
            }
            var header = new CommandHeader(hash, n.OptString("match_id"), n.OptString("request_id"), n["round_index"].AsInt(),
                n["expected_state_revision"].AsULong(), n["schema_version"].AsInt());
            switch (n.Str("kind"))
            {
                case KindLoadout:
                {
                    IReadOnlyList<JsonNode> items = n["weapons"].AsArray();
                    if (items.Count > 32) throw new FormatException("Too many weapons.");
                    var weapons = new List<int>(items.Count);
                    foreach (JsonNode w in items) weapons.Add(w.AsInt());
                    return new SubmitLoadoutCommand(header, weapons, n["reserve"].AsInt());
                }
                case KindLock:
                    return new LockInputCommand(header, n["volley_index"].AsInt(), n["weapon_id"].AsInt(), n["pitch_qdeg"].AsInt(),
                        n["yaw_qdeg"].AsInt(), n["power_percent"].AsInt(), (Dodge)checked((byte)n["dodge"].AsInt()));
                case KindCut:
                {
                    IReadOnlyList<JsonNode> items = n["vertices"].AsArray();
                    if (items.Count > MaxWireVertices) throw new FormatException("Too many vertices.");
                    var vertices = new List<CellPoint>(items.Count);
                    foreach (JsonNode p in items)
                    {
                        IReadOnlyList<JsonNode> xy = p.AsArray();
                        if (xy.Count != 2) throw new FormatException("A vertex is [x, y].");
                        vertices.Add(new CellPoint(xy[0].AsInt(), xy[1].AsInt()));
                    }
                    string mode = n.Str("mode");
                    CutMode cutMode = mode == "Auto" ? CutMode.Auto : mode == "Manual" ? CutMode.Manual : (CutMode)0xFF;
                    return new SubmitCutCommand(header, n["expected_map_revision"].AsULong(), (CardId)checked((byte)n["card_id"].AsInt()),
                        n["anchor_cell_id"].AsInt(), n["center_x"].AsInt(), n["center_y"].AsInt(), n["rotation_index"].AsInt(),
                        n["scale_quarters"].AsInt(), cutMode, vertices);
                }
                default:
                    throw new FormatException("Unknown command kind.");
            }
        }
    }
}
