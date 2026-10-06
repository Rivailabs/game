using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Rules.Match
{
    /// <summary>
    /// Wire form of a <see cref="PlayerView"/> (online tickets 50, 51, 54). The authoritative match
    /// service encodes the private view of one player; that player's client decodes it into an
    /// ordinary <see cref="PlayerView"/>, so the same view-driven screens serve the shared-phone and
    /// online flows.
    /// <para>
    /// The encoder adds nothing the view does not already hold, so the view's privacy guarantees
    /// (no unrevealed opponent choice or loadout, Mist Veil concealment) carry over to the wire.
    /// The public ownership map is optional: it is 51,040 bits (one per active cell, set = owned by
    /// B) and only needs to travel when the map revision changes. A decoder without it reuses the
    /// territory it already holds. Decoding never trusts its input: malformed text throws
    /// <see cref="FormatException"/> and an illegal config throws <see cref="RulesViolationException"/>.
    /// </para>
    /// </summary>
    public static class PlayerViewCodec
    {
        /// <summary>Format tag carried in every encoded view.</summary>
        public const string Format = "AK-PLAYER-VIEW/1";

        /// <summary>Encodes a view. <paramref name="includeOwnership"/> adds the public ownership map.</summary>
        public static JsonNode ToJson(PlayerView v, bool includeOwnership)
        {
            if (v == null) throw new ArgumentNullException(nameof(v));
            JsonNode n = JsonNode.Object()
                .Add("format", Format)
                .Add("viewer", (long)v.Viewer)
                .Add("match_id", v.MatchId)
                .Add("config", ConfigNode(v.Config))
                .Add("rules_hash", Hex.Encode(v.RulesHashBytes))
                .Add("seed_commitment", v.SeedCommitmentHex)
                .Add("seed", v.SeedHex)
                .Add("phase", (long)v.Phase)
                .Add("state_revision", v.StateRevision)
                .Add("map_revision", v.MapRevision)
                .Add("round", v.RoundIndex)
                .Add("volley", v.VolleyIndex)
                .Add("volleys_resolved", v.VolleysResolved)
                .Add("first_attacker", (long)v.FirstAttacker)
                .Add("attacker", (long)v.Attacker)
                .Add("terrain", (long)v.DuelTerrain)
                .Add("frontier_cell", v.FrontierCellId)
                .Add("fort_cover", v.FortCoverActive)
                .Add("forest_charge", v.ForestChargeAvailable)
                .Add("own_loadout", LoadoutNode(v.OwnLoadout))
                .Add("opponent_loadout_submitted", v.OpponentLoadoutSubmitted)
                .Add("opponent_loadout", LoadoutNode(v.OpponentLoadoutRevealed))
                .Add("reserve_eligible", v.ReserveEligible)
                .Add("own_lock", InputNode(v.OwnLock))
                .Add("opponent_locked", v.OpponentLocked)
                .Add("self", StatusNode(v.Self))
                .Add("foe", StatusNode(v.Foe))
                .Add("cells_a", v.CellsA)
                .Add("cells_b", v.CellsB)
                .Add("duel_winner", v.DuelWinner.HasValue ? JsonNode.Of((long)v.DuelWinner.Value) : JsonNode.Null)
                .Add("hp_difference", v.HpDifferenceUnits);

            JsonNode cards = JsonNode.Array();
            foreach (CardId c in v.OfferedCards) cards.Push(JsonNode.Of((long)c));
            JsonNode quotas = JsonNode.Array();
            foreach (int q in v.OfferedQuotas) quotas.Push(JsonNode.Of(q));
            n.Add("offered_cards", cards).Add("offered_quotas", quotas);

            JsonNode history = JsonNode.Array();
            foreach (RevealedVolley rv in v.History) history.Push(VolleyNode(rv));
            n.Add("history", history);
            n.Add("result", ResultNode(v.Result));
            n.Add("ownership", includeOwnership ? JsonNode.Of(EncodeOwnership(v.CloneTerritory())) : JsonNode.Null);
            return n;
        }

        /// <summary>
        /// Decodes a view. When the encoded view carries no ownership map, <paramref name="knownTerritory"/>
        /// (the territory decoded earlier at the same map revision) supplies it; it is copied, never shared.
        /// </summary>
        public static PlayerView FromJson(JsonNode n, Territory knownTerritory)
        {
            if (n == null) throw new ArgumentNullException(nameof(n));
            if (n["format"].AsString() != Format) throw new FormatException("Unknown player view format.");
            MatchConfig config = ParseConfig(n["config"]);

            Territory territory;
            string ownership = n["ownership"].AsString();
            if (ownership != null) territory = DecodeOwnership(ownership, config.Template);
            else if (knownTerritory != null) territory = knownTerritory.Clone();
            else throw new FormatException("The view has no ownership map and no known territory was supplied.");
            Territory snapshot = territory;

            var v = new PlayerView(() => snapshot.Clone())
            {
                Viewer = Side(n["viewer"]),
                MatchId = n["match_id"].AsString(),
                Config = config,
                RulesHashBytes = Hex.Decode(n["rules_hash"].AsString()),
                SeedCommitmentHex = n["seed_commitment"].AsString(),
                SeedHex = n["seed"].AsString(),
                Phase = Enum<MatchPhase>(n["phase"]),
                StateRevision = n["state_revision"].AsULong(),
                MapRevision = n["map_revision"].AsULong(),
                RoundIndex = n["round"].AsInt(),
                VolleyIndex = n["volley"].AsInt(),
                VolleysResolved = n["volleys_resolved"].AsInt(),
                FirstAttacker = Side(n["first_attacker"]),
                Attacker = Side(n["attacker"]),
                DuelTerrain = Enum<TerrainType>(n["terrain"]),
                FrontierCellId = n["frontier_cell"].AsInt(),
                FortCoverActive = n["fort_cover"].AsBool(),
                ForestChargeAvailable = n["forest_charge"].AsBool(),
                OwnLoadout = ParseLoadout(n["own_loadout"], config.Catalog),
                OpponentLoadoutSubmitted = n["opponent_loadout_submitted"].AsBool(),
                OpponentLoadoutRevealed = ParseLoadout(n["opponent_loadout"], config.Catalog),
                ReserveEligible = n["reserve_eligible"].AsBool(),
                OwnLock = ParseInput(n["own_lock"]),
                OpponentLocked = n["opponent_locked"].AsBool(),
                Self = ParseStatus(n["self"]),
                Foe = ParseStatus(n["foe"]),
                CellsA = n["cells_a"].AsInt(),
                CellsB = n["cells_b"].AsInt(),
                DuelWinner = n["duel_winner"].Kind == JsonKind.Null ? (PlayerSide?)null : Side(n["duel_winner"]),
                HpDifferenceUnits = n["hp_difference"].AsInt(),
                Result = ParseResult(n["result"]),
            };
            if (v.RulesHashBytes.Length != 32) throw new FormatException("rules_hash must be 32 bytes.");

            var cards = new List<CardId>();
            foreach (JsonNode c in n["offered_cards"].AsArray()) cards.Add(Enum<CardId>(c));
            var quotas = new List<int>();
            foreach (JsonNode q in n["offered_quotas"].AsArray()) quotas.Add(q.AsInt());
            if (cards.Count != quotas.Count) throw new FormatException("offered_cards and offered_quotas differ in length.");
            v.OfferedCards = cards;
            v.OfferedQuotas = quotas;

            var history = new List<RevealedVolley>();
            foreach (JsonNode h in n["history"].AsArray()) history.Add(ParseVolley(h));
            v.History = history;
            return v;
        }

        /// <summary>Packs the ownership map: one bit per active cell in cell-ID order, set when B owns it (Base64).</summary>
        public static string EncodeOwnership(Territory territory)
        {
            if (territory == null) throw new ArgumentNullException(nameof(territory));
            var bits = new byte[(RulesConstants.ActiveCells + 7) / 8];
            int k = 0;
            for (int id = 0; id < Board.GridCellCount; id++)
            {
                if (!Board.IsActive(id)) continue;
                if (territory.OwnerOf(id) == PlayerSide.B) bits[k >> 3] |= (byte)(1 << (k & 7));
                k++;
            }
            return Convert.ToBase64String(bits);
        }

        /// <summary>Rebuilds a territory on <paramref name="template"/> from <see cref="EncodeOwnership"/> text.</summary>
        public static Territory DecodeOwnership(string base64, TerrainTemplate template)
        {
            if (base64 == null) throw new ArgumentNullException(nameof(base64));
            byte[] bits;
            try
            {
                bits = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw new FormatException("Ownership map is not valid Base64.");
            }
            if (bits.Length != (RulesConstants.ActiveCells + 7) / 8) throw new FormatException("Ownership map has the wrong length.");

            Territory t = Territory.CreateInitial(template);
            var toB = new List<int>();
            var toA = new List<int>();
            int k = 0;
            for (int id = 0; id < Board.GridCellCount; id++)
            {
                if (!Board.IsActive(id)) continue;
                PlayerSide owner = (bits[k >> 3] & (1 << (k & 7))) != 0 ? PlayerSide.B : PlayerSide.A;
                PlayerSide initial = t.OwnerOf(id);
                if (owner != initial) (owner == PlayerSide.B ? toB : toA).Add(id);
                k++;
            }
            t.Transfer(toB, PlayerSide.A);
            t.Transfer(toA, PlayerSide.B);
            t.CheckInvariants();
            return t;
        }

        // ------------------------------------------------------------------ encoders

        /// <summary>Config as readable names (the same shape as the match record).</summary>
        public static JsonNode ConfigNode(MatchConfig c) => JsonNode.Object()
            .Add("rules_version", c.RulesVersion)
            .Add("mode", c.Mode.ToString())
            .Add("catalog", c.Catalog.ToString())
            .Add("card_offers", c.CardOffers.ToString())
            .Add("terrain_template", c.TerrainTemplateId)
            .Add("brahmastra", c.BrahmastraEnabled);

        /// <summary>Parses and validates a config node written by <see cref="ConfigNode"/>.</summary>
        public static MatchConfig ParseConfig(JsonNode n) => new MatchConfig(
            EnumName<MatchMode>(n["mode"]), EnumName<CatalogPreset>(n["catalog"]), EnumName<CardOfferRule>(n["card_offers"]),
            n["terrain_template"].AsString(), n["brahmastra"].AsBool(), n["rules_version"].AsString());

        private static JsonNode LoadoutNode(Loadout l)
        {
            if (l == null) return JsonNode.Null;
            JsonNode weapons = JsonNode.Array();
            foreach (int w in l.Weapons) weapons.Push(JsonNode.Of(w));
            return JsonNode.Object().Add("weapons", weapons).Add("reserve", l.Reserve);
        }

        private static JsonNode InputNode(VolleyInput i) => i == null ? JsonNode.Null : JsonNode.Object()
            .Add("weapon_id", i.WeaponId).Add("pitch_qdeg", i.PitchQdeg).Add("yaw_qdeg", i.YawQdeg)
            .Add("power_percent", i.PowerPercent).Add("dodge", (long)i.Dodge);

        private static JsonNode StatusNode(PlayerStatus s) => s == null ? JsonNode.Null : JsonNode.Object()
            .Add("hp", s.HpUnits).Add("burn_due", s.BurnDue).Add("shock_due", s.ShockDue).Add("net_due", s.NetDue)
            .Add("quake_due", s.QuakeDue).Add("iron_wall", s.IronWallActive).Add("baseline_right_raw", s.BaselineOffsetRightRaw)
            .Add("brahmastra", s.BrahmastraAvailable).Add("timeouts", s.ConsecutiveTimeouts).Add("cells", s.Cells);

        private static JsonNode VolleyNode(RevealedVolley v) => JsonNode.Object()
            .Add("round", v.Round).Add("volley", v.Volley).Add("defender", (long)v.Defender).Add("terrain", (long)v.Terrain)
            .Add("a", ChoiceNode(v.A)).Add("b", ChoiceNode(v.B)).Add("result_after", (long)v.ResultAfter);

        private static JsonNode ChoiceNode(RevealedChoice c) => JsonNode.Object()
            .Add("side", (long)c.Side).Add("concealed", c.Concealed).Add("weapon_id", c.WeaponId).Add("element", (long)c.Element)
            .Add("pitch_qdeg", c.PitchQdeg).Add("yaw_qdeg", c.YawQdeg).Add("power_percent", c.PowerPercent)
            .Add("submitted_dodge", (long)c.SubmittedDodge).Add("effective_dodge", (long)c.EffectiveDodge)
            .Add("timed_out", c.TimedOut).Add("landed_hit", c.LandedHit).Add("hp_before", c.HpBeforeUnits)
            .Add("hp_after", c.HpAfterUnits).Add("direct_taken", c.DirectDamageTakenUnits).Add("burn_taken", c.BurnTakenUnits)
            .Add("healed", c.HealedUnits);

        /// <summary>Terminal result node (also used by the online service's match.end message).</summary>
        public static JsonNode ResultNode(MatchResult r) => r == null ? JsonNode.Null : JsonNode.Object()
            .Add("reason", r.Reason.ToString())
            .Add("winner", r.Winner.HasValue ? JsonNode.Of(r.Winner.Value.ToString()) : JsonNode.Null)
            .Add("forfeited_by", r.ForfeitedBy.HasValue ? JsonNode.Of(r.ForfeitedBy.Value.ToString()) : JsonNode.Null)
            .Add("cells_a", r.CellsA).Add("cells_b", r.CellsB).Add("rounds_played", r.RoundsPlayed);

        // ------------------------------------------------------------------ decoders

        /// <summary>Parses a node written by <see cref="ResultNode"/> (null for JSON null).</summary>
        public static MatchResult ParseResult(JsonNode n)
        {
            if (n.Kind == JsonKind.Null) return null;
            PlayerSide? winner = n["winner"].Kind == JsonKind.Null ? (PlayerSide?)null : EnumName<PlayerSide>(n["winner"]);
            PlayerSide? forfeit = n["forfeited_by"].Kind == JsonKind.Null ? (PlayerSide?)null : EnumName<PlayerSide>(n["forfeited_by"]);
            return new MatchResult(EnumName<TerminalReason>(n["reason"]), winner, forfeit, n["cells_a"].AsInt(), n["cells_b"].AsInt(),
                n["rounds_played"].AsInt());
        }

        private static Loadout ParseLoadout(JsonNode n, CatalogPreset preset)
        {
            if (n.Kind == JsonKind.Null) return null;
            var weapons = new List<int>();
            foreach (JsonNode w in n["weapons"].AsArray()) weapons.Add(w.AsInt());
            try
            {
                return Loadout.Create(preset, weapons, n["reserve"].AsInt());
            }
            catch (RulesViolationException e)
            {
                throw new FormatException("Illegal loadout in view: " + e.Message);
            }
        }

        private static VolleyInput ParseInput(JsonNode n) => n.Kind == JsonKind.Null ? null :
            new VolleyInput(n["weapon_id"].AsInt(), n["pitch_qdeg"].AsInt(), n["yaw_qdeg"].AsInt(), n["power_percent"].AsInt(),
                Enum<Dodge>(n["dodge"]));

        private static PlayerStatus ParseStatus(JsonNode n) => n.Kind == JsonKind.Null ? null : new PlayerStatus
        {
            HpUnits = n["hp"].AsInt(),
            BurnDue = n["burn_due"].AsBool(),
            ShockDue = n["shock_due"].AsBool(),
            NetDue = n["net_due"].AsBool(),
            QuakeDue = n["quake_due"].AsBool(),
            IronWallActive = n["iron_wall"].AsBool(),
            BaselineOffsetRightRaw = n["baseline_right_raw"].AsLong(),
            BrahmastraAvailable = n["brahmastra"].AsBool(),
            ConsecutiveTimeouts = n["timeouts"].AsInt(),
            Cells = n["cells"].AsInt(),
        };

        private static RevealedVolley ParseVolley(JsonNode n) => new RevealedVolley
        {
            Round = n["round"].AsInt(),
            Volley = n["volley"].AsInt(),
            Defender = Side(n["defender"]),
            Terrain = Enum<TerrainType>(n["terrain"]),
            A = ParseChoice(n["a"]),
            B = ParseChoice(n["b"]),
            ResultAfter = Enum<DuelResult>(n["result_after"]),
        };

        private static RevealedChoice ParseChoice(JsonNode n) => new RevealedChoice
        {
            Side = Side(n["side"]),
            Concealed = n["concealed"].AsBool(),
            WeaponId = n["weapon_id"].AsInt(),
            Element = Enum<Element>(n["element"]),
            PitchQdeg = n["pitch_qdeg"].AsInt(),
            YawQdeg = n["yaw_qdeg"].AsInt(),
            PowerPercent = n["power_percent"].AsInt(),
            SubmittedDodge = Enum<Dodge>(n["submitted_dodge"]),
            EffectiveDodge = Enum<Dodge>(n["effective_dodge"]),
            TimedOut = n["timed_out"].AsBool(),
            LandedHit = n["landed_hit"].AsBool(),
            HpBeforeUnits = n["hp_before"].AsInt(),
            HpAfterUnits = n["hp_after"].AsInt(),
            DirectDamageTakenUnits = n["direct_taken"].AsInt(),
            BurnTakenUnits = n["burn_taken"].AsInt(),
            HealedUnits = n["healed"].AsInt(),
        };

        private static PlayerSide Side(JsonNode n) => Enum<PlayerSide>(n);

        private static T Enum<T>(JsonNode n) where T : struct
        {
            long value = n.AsLong();
            object boxed = System.Enum.ToObject(typeof(T), value);
            if (!System.Enum.IsDefined(typeof(T), boxed)) throw new FormatException("Unknown " + typeof(T).Name + " value " + value + ".");
            return (T)boxed;
        }

        private static T EnumName<T>(JsonNode n) where T : struct
        {
            string s = n.AsString();
            if (s == null || !System.Enum.TryParse(s, false, out T value) || !System.Enum.IsDefined(typeof(T), value))
                throw new FormatException("Unknown " + typeof(T).Name + " '" + s + "'.");
            return value;
        }
    }
}
