using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Match
{
    /// <summary>Authoritative summary of one resolved volley (replay data; contains both choices).</summary>
    public sealed class VolleyRecord
    {
        public int Round;
        public int Volley;
        public int WeaponA;
        public int WeaponB;
        public bool TimeoutA;
        public bool TimeoutB;
        public int HpA;
        public int HpB;
        public DuelResult ResultAfter;
        /// <summary>SHA-256 of the volley's canonical combat event log.</summary>
        public string LogHashHex;

        public VolleyRecord Clone() => (VolleyRecord)MemberwiseClone();
    }

    /// <summary>
    /// One round: initiative, frontier terrain selection, duel, card offer, cut and the resulting
    /// state hash. <see cref="StateHashHex"/> covers the territory ownership hash, both cell
    /// counts, the map revision, the duel result and both final HP values.
    /// </summary>
    public sealed class RoundRecord
    {
        public int Round;
        public PlayerSide Attacker;
        public int FrontierCellId;
        public TerrainType Terrain;
        public int FrontierCount;
        public uint TerrainStreamCounter;
        public List<VolleyRecord> Volleys = new List<VolleyRecord>();
        public DuelResult DuelResult;
        public int HpA;
        public int HpB;
        public int HpDifference;
        public List<CardId> OfferedCards = new List<CardId>();
        public uint CardsStreamCounter;
        /// <summary>True when the cut window ended without an accepted cut (zero transfer).</summary>
        public bool CutTimedOut;
        public int CellsTransferred;
        public int CellsA;
        public int CellsB;
        public ulong MapRevision;
        public string OwnershipHashHex;
        public string StateHashHex;

        public RoundRecord Clone()
        {
            var copy = (RoundRecord)MemberwiseClone();
            copy.Volleys = new List<VolleyRecord>();
            foreach (VolleyRecord v in Volleys) copy.Volleys.Add(v.Clone());
            copy.OfferedCards = new List<CardId>(OfferedCards);
            return copy;
        }
    }

    public enum MatchEventType : byte
    {
        MatchCreated = 1,
        LoadoutSubmitted = 2,
        RoundStarted = 3,
        SelectionOpened = 4,
        PlayerLocked = 5,
        SelectionTimeout = 6,
        VolleyResolved = 7,
        DuelEnded = 8,
        CardsOffered = 9,
        CutApplied = 10,
        CutTimedOut = 11,
        RoundEnded = 12,
        MatchEnded = 13,
    }

    /// <summary>
    /// Public match event. Never carries a secret: PlayerLocked says only that a player is ready;
    /// VolleyResolved carries HP, not choices. Sequence numbers are separate from state revisions.
    /// </summary>
    public sealed class MatchEvent
    {
        public long Sequence { get; }
        public MatchEventType Type { get; }
        public int Round { get; }
        public int Volley { get; }
        public PlayerSide? Player { get; }
        /// <summary>Type-specific amount (cells, HP units, terrain, terminal reason...).</summary>
        public int Amount { get; }
        public int Amount2 { get; }
        public ulong StateRevision { get; }

        internal MatchEvent(long sequence, MatchEventType type, int round, int volley, PlayerSide? player, int amount, int amount2, ulong stateRevision)
        {
            Sequence = sequence;
            Type = type;
            Round = round;
            Volley = volley;
            Player = player;
            Amount = amount;
            Amount2 = amount2;
            StateRevision = stateRevision;
        }

        public override string ToString() =>
            "#" + Sequence + " " + Type + " r" + Round + "v" + Volley + (Player.HasValue ? " " + Player.Value : string.Empty) +
            " " + Amount + "/" + Amount2 + " rev" + StateRevision;
    }
}
