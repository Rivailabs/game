using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Match
{
    /// <summary>
    /// One side of a resolved volley as a given viewer may see it. When the owner's Mist Veil
    /// conceals this volley from the viewer (until the match ends), the weapon label, element and
    /// exact submitted controls are withheld; movement (effective dodge), damage and HP stay truthful.
    /// </summary>
    public sealed class RevealedChoice
    {
        public PlayerSide Side { get; internal set; }
        public bool Concealed { get; internal set; }
        /// <summary>Weapon ID, 0 for a timeout Pass, 1000 for Brahmastra, −1 when concealed.</summary>
        public int WeaponId { get; internal set; }
        /// <summary>Defensive element; Neutral when concealed (see <see cref="Concealed"/>).</summary>
        public Element Element { get; internal set; }
        public int PitchQdeg { get; internal set; }
        public int YawQdeg { get; internal set; }
        public int PowerPercent { get; internal set; }
        public Dodge SubmittedDodge { get; internal set; }
        /// <summary>Visible movement after Net/Cyclone transformations.</summary>
        public Dodge EffectiveDodge { get; internal set; }
        public bool TimedOut { get; internal set; }
        public bool LandedHit { get; internal set; }
        public int HpBeforeUnits { get; internal set; }
        public int HpAfterUnits { get; internal set; }
        public int DirectDamageTakenUnits { get; internal set; }
        public int BurnTakenUnits { get; internal set; }
        public int HealedUnits { get; internal set; }

        internal void Append(StringBuilder sb)
        {
            sb.Append(Side).Append(':').Append(Concealed ? 1 : 0).Append(',').Append(WeaponId).Append(',').Append((int)Element)
              .Append(',').Append(PitchQdeg).Append(',').Append(YawQdeg).Append(',').Append(PowerPercent)
              .Append(',').Append((int)SubmittedDodge).Append(',').Append((int)EffectiveDodge).Append(',').Append(TimedOut ? 1 : 0)
              .Append(',').Append(LandedHit ? 1 : 0).Append(',').Append(HpBeforeUnits).Append(',').Append(HpAfterUnits)
              .Append(',').Append(DirectDamageTakenUnits).Append(',').Append(BurnTakenUnits).Append(',').Append(HealedUnits);
        }
    }

    /// <summary>A resolved volley in a viewer's history.</summary>
    public sealed class RevealedVolley
    {
        public int Round { get; internal set; }
        public int Volley { get; internal set; }
        public PlayerSide Defender { get; internal set; }
        public TerrainType Terrain { get; internal set; }
        public RevealedChoice A { get; internal set; }
        public RevealedChoice B { get; internal set; }
        public DuelResult ResultAfter { get; internal set; }

        public RevealedChoice this[PlayerSide side] => side == PlayerSide.A ? A : B;
    }

    /// <summary>Decision-relevant, truthful status indicators of one player in the current duel.</summary>
    public sealed class PlayerStatus
    {
        public int HpUnits { get; internal set; }
        public bool BurnDue { get; internal set; }
        public bool ShockDue { get; internal set; }
        public bool NetDue { get; internal set; }
        public bool QuakeDue { get; internal set; }
        public bool IronWallActive { get; internal set; }
        /// <summary>Gale Push baseline displacement along the player's local Right, in Q32.32 raw metres.</summary>
        public long BaselineOffsetRightRaw { get; internal set; }
        public bool BrahmastraAvailable { get; internal set; }
        public int ConsecutiveTimeouts { get; internal set; }
        public int Cells { get; internal set; }
    }

    /// <summary>
    /// The private view of one player (plan: "Online ownership and hidden choices"). It is built
    /// from authoritative state but contains only what that player may know: their own loadout and
    /// provisional lock, the opponent's ready flag (never the opponent's unrevealed choice or
    /// loadout), public board/HP/status data, and resolved history with Mist Veil concealment.
    /// After the match ends the seed and all remaining private information are disclosed.
    /// </summary>
    public sealed class PlayerView
    {
        private readonly Func<Territory> _territoryCloner;

        internal PlayerView(Func<Territory> territoryCloner)
        {
            _territoryCloner = territoryCloner;
        }

        public PlayerSide Viewer { get; internal set; }
        public PlayerSide Opponent => Board.Opponent(Viewer);
        public string MatchId { get; internal set; }
        public MatchConfig Config { get; internal set; }
        internal byte[] RulesHashBytes;
        public byte[] RulesHash => (byte[])RulesHashBytes.Clone();
        public string SeedCommitmentHex { get; internal set; }
        /// <summary>Disclosed only after the match ends; null before.</summary>
        public string SeedHex { get; internal set; }

        public MatchPhase Phase { get; internal set; }
        public ulong StateRevision { get; internal set; }
        public ulong MapRevision { get; internal set; }
        /// <summary>One-based round, 0 during Setup.</summary>
        public int RoundIndex { get; internal set; }
        /// <summary>Open (or just resolved) volley, 0 outside a duel.</summary>
        public int VolleyIndex { get; internal set; }
        /// <summary>Total volleys resolved so far in the match (drives shared-phone entry order).</summary>
        public int VolleysResolved { get; internal set; }
        public PlayerSide FirstAttacker { get; internal set; }
        public PlayerSide Attacker { get; internal set; }
        public PlayerSide Defender => Board.Opponent(Attacker);
        public bool IsAttacker => Attacker == Viewer;
        public TerrainType DuelTerrain { get; internal set; }
        public int FrontierCellId { get; internal set; }
        public bool FortCoverActive { get; internal set; }
        public bool ForestChargeAvailable { get; internal set; }

        /// <summary>The viewer's own loadout (null until submitted).</summary>
        public Loadout OwnLoadout { get; internal set; }
        public bool OpponentLoadoutSubmitted { get; internal set; }
        /// <summary>Opponent's loadout, disclosed only after the match ends.</summary>
        public Loadout OpponentLoadoutRevealed { get; internal set; }
        /// <summary>The viewer may use their Armoury reserve in the open volley.</summary>
        public bool ReserveEligible { get; internal set; }

        /// <summary>The viewer's accepted lock for the open volley, or null.</summary>
        public VolleyInput OwnLock { get; internal set; }
        /// <summary>Only a ready flag: the opponent's choice is never part of this view.</summary>
        public bool OpponentLocked { get; internal set; }

        public PlayerStatus Self { get; internal set; }
        public PlayerStatus Foe { get; internal set; }
        public int CellsA { get; internal set; }
        public int CellsB { get; internal set; }

        /// <summary>Winner of the duel just finished (null for a draw or while in progress).</summary>
        public PlayerSide? DuelWinner { get; internal set; }
        public int HpDifferenceUnits { get; internal set; }
        /// <summary>Cards offered to the duel winner (empty otherwise), in offer order.</summary>
        public IReadOnlyList<CardId> OfferedCards { get; internal set; } = Array.Empty<CardId>();
        /// <summary>Maximum allowance Q per offered card, parallel to <see cref="OfferedCards"/>.</summary>
        public IReadOnlyList<int> OfferedQuotas { get; internal set; } = Array.Empty<int>();
        public bool IsCutTurn => Phase == MatchPhase.CardAndCut && DuelWinner == Viewer;

        public IReadOnlyList<RevealedVolley> History { get; internal set; } = Array.Empty<RevealedVolley>();
        public MatchResult Result { get; internal set; }

        /// <summary>
        /// Shared-phone entry order alternates by volley: the attacker enters first on even counts of
        /// resolved volleys, the defender on odd counts.
        /// </summary>
        public PlayerSide FirstEntrant => VolleysResolved % 2 == 0 ? Attacker : Board.Opponent(Attacker);

        /// <summary>A private copy of the public ownership map (for cut previews and bot planning).</summary>
        public Territory CloneTerritory() => _territoryCloner();

        /// <summary>Header for the next command against this snapshot.</summary>
        public CommandHeader NewHeader(string requestId) =>
            new CommandHeader(RulesHashBytes, MatchId, requestId, RoundIndex, StateRevision);

        public int OwnCells => Viewer == PlayerSide.A ? CellsA : CellsB;

        /// <summary>
        /// Deterministic text of every field in the view. Two views with equal text expose exactly the
        /// same information; tests use this to prove the opponent's unrevealed choice cannot leak.
        /// </summary>
        public string ToCanonicalText()
        {
            var sb = new StringBuilder(1024);
            void F(string k, object v) => sb.Append(k).Append('=').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append(';');
            F("viewer", Viewer); F("match", MatchId); F("config", Config); F("rules", Hex.Encode(RulesHashBytes));
            F("commit", SeedCommitmentHex); F("seed", SeedHex); F("phase", Phase); F("rev", StateRevision); F("map", MapRevision);
            F("round", RoundIndex); F("volley", VolleyIndex); F("resolved", VolleysResolved); F("first", FirstAttacker);
            F("attacker", Attacker); F("terrain", DuelTerrain); F("frontier", FrontierCellId); F("fort", FortCoverActive);
            F("forest", ForestChargeAvailable);
            F("own", OwnLoadout == null ? "-" : string.Join(",", OwnLoadout.Weapons) + "+" + OwnLoadout.Reserve);
            F("oppSubmitted", OpponentLoadoutSubmitted);
            F("oppLoadout", OpponentLoadoutRevealed == null ? "-" : string.Join(",", OpponentLoadoutRevealed.Weapons) + "+" + OpponentLoadoutRevealed.Reserve);
            F("reserve", ReserveEligible); F("ownLock", OwnLock == null ? "-" : OwnLock.ToString()); F("oppLocked", OpponentLocked);
            AppendStatus(sb, "self", Self); AppendStatus(sb, "foe", Foe);
            F("cellsA", CellsA); F("cellsB", CellsB); F("duelWinner", DuelWinner.HasValue ? DuelWinner.Value.ToString() : "-");
            F("diff", HpDifferenceUnits); F("cards", string.Join(",", OfferedCards)); F("quotas", string.Join(",", OfferedQuotas));
            foreach (RevealedVolley v in History)
            {
                sb.Append("h").Append(v.Round).Append('.').Append(v.Volley).Append('[').Append(v.Defender).Append(',').Append(v.Terrain).Append(',');
                v.A.Append(sb);
                sb.Append('|');
                v.B.Append(sb);
                sb.Append(',').Append(v.ResultAfter).Append("];");
            }
            F("result", Result == null ? "-" : Result.ToString());
            return sb.ToString();
        }

        private static void AppendStatus(StringBuilder sb, string key, PlayerStatus s)
        {
            sb.Append(key).Append('=');
            if (s == null)
            {
                sb.Append("-;");
                return;
            }
            sb.Append(s.HpUnits).Append(',').Append(s.BurnDue ? 1 : 0).Append(s.ShockDue ? 1 : 0).Append(s.NetDue ? 1 : 0)
              .Append(s.QuakeDue ? 1 : 0).Append(s.IronWallActive ? 1 : 0).Append(',').Append(s.BaselineOffsetRightRaw)
              .Append(',').Append(s.BrahmastraAvailable ? 1 : 0).Append(',').Append(s.ConsecutiveTimeouts).Append(',').Append(s.Cells).Append(';');
        }
    }
}
