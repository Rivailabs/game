using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Modes.FourPlayer
{
    public enum FourPlayerPhase : byte
    {
        /// <summary>Entrants submit private loadouts.</summary>
        Setup = 0,
        /// <summary>A wave is in progress: pairs duel and winners cut against the frozen board.</summary>
        Wave = 1,
        /// <summary>Terminal: placements are final and the seed is disclosed.</summary>
        Finished = 2,
    }

    /// <summary>Stage of one pair inside a wave.</summary>
    public enum PairStage : byte
    {
        Selection = 0,
        Cut = 1,
        Done = 2,
    }

    public enum EliminationReason : byte
    {
        None = 0,
        /// <summary>Owned zero cells after a wave settled.</summary>
        NoLand = 1,
        /// <summary>Left the match or reached two consecutive selection timeouts.</summary>
        Forfeit = 2,
    }

    /// <summary>Public event types. None of them carries an unresolved hidden choice.</summary>
    public enum FourPlayerEventType : byte
    {
        MatchCreated = 1,
        WaveStarted = 2,
        VolleyResolved = 3,
        DuelEnded = 4,
        CutCommitted = 5,
        CutTimedOut = 6,
        PlayerForfeited = 7,
        CutApplied = 8,
        WaveSettled = 9,
        Eliminated = 10,
        MatchFinished = 11,
    }

    /// <summary>
    /// A public, resolved event. The engine never emits an event for a lock (not even a ready
    /// flag), so these events are safe inputs to the spectator stream; the spectator feed still
    /// delays them (see <see cref="SpectatorFeed"/>).
    /// </summary>
    public sealed class FourPlayerEvent
    {
        public long Sequence { get; }
        public FourPlayerEventType Type { get; }
        public int Wave { get; }
        public int PairSlot { get; }
        public Kingdom? Kingdom { get; }
        public Kingdom? Other { get; }
        public int Volley { get; }
        public int Amount { get; }
        public int Amount2 { get; }
        /// <summary>Public detail text (resolved weapons, pairings, hashes).</summary>
        public string Detail { get; }

        internal FourPlayerEvent(long sequence, FourPlayerEventType type, int wave, int pairSlot, Kingdom? kingdom, Kingdom? other,
            int volley, int amount, int amount2, string detail)
        {
            Sequence = sequence;
            Type = type;
            Wave = wave;
            PairSlot = pairSlot;
            Kingdom = kingdom;
            Other = other;
            Volley = volley;
            Amount = amount;
            Amount2 = amount2;
            Detail = detail ?? string.Empty;
        }

        public override string ToString() =>
            "#" + Sequence.ToString(CultureInfo.InvariantCulture) + " " + Type + " w" + Wave + " p" + PairSlot +
            (Kingdom.HasValue ? " " + Kingdom.Value : string.Empty) + (Other.HasValue ? "/" + Other.Value : string.Empty) +
            " v" + Volley + " " + Amount + "/" + Amount2 + (Detail.Length > 0 ? " " + Detail : string.Empty);
    }

    /// <summary>How one pair's duel and cut ended in a wave.</summary>
    public sealed class PairOutcome
    {
        public int Slot { get; internal set; }
        public WavePair Pair { get; internal set; }
        public DuelResult DuelResult { get; internal set; }
        public int HpFirst { get; internal set; }
        public int HpSecond { get; internal set; }
        public Kingdom? Winner { get; internal set; }
        public int HpDifference { get; internal set; }
        /// <summary>True when a forfeit ended the pair before the duel finished.</summary>
        public bool EndedByForfeit { get; internal set; }
        public bool CutTimedOut { get; internal set; }
        public int CellsTransferred { get; internal set; }
    }

    /// <summary>A settled wave: pairs, transfers, eliminations and the resulting state hash.</summary>
    public sealed class FourPlayerWaveRecord
    {
        public int Wave { get; internal set; }
        public WavePlan Plan { get; internal set; }
        public List<PairOutcome> Pairs { get; } = new List<PairOutcome>();
        /// <summary>Cells of A, B, C, D and locked neutral after settlement.</summary>
        public int[] CellsAfter { get; internal set; }
        public List<Kingdom> Eliminated { get; } = new List<Kingdom>();
        public string OwnershipHashHex { get; internal set; }
        public string StateHashHex { get; internal set; }
    }

    /// <summary>Final placement of one kingdom. Exact ties share a place; no coin flip.</summary>
    public sealed class FourPlayerStanding
    {
        public Kingdom Kingdom { get; internal set; }
        public string EntrantId { get; internal set; }
        /// <summary>Competition ranking: 1 + number of kingdoms placed strictly better.</summary>
        public int Place { get; internal set; }
        public int Cells { get; internal set; }
        /// <summary>Wave in which the kingdom was eliminated (0 = survived; −1 = before wave 1).</summary>
        public int EliminatedWave { get; internal set; }
        public EliminationReason Reason { get; internal set; }

        public override string ToString() => Place + ". " + Kingdom + " (" + EntrantId + ") " + Cells + " cells" +
            (Reason != EliminationReason.None ? " eliminated w" + EliminatedWave + " " + Reason : string.Empty);
    }

    /// <summary>One resolved volley of the viewer's own duel.</summary>
    public sealed class ResolvedVolley4P
    {
        public int Volley { get; internal set; }
        public int OwnWeaponId { get; internal set; }
        /// <summary>The opponent's weapon, or −1 while their Mist Veil conceals it.</summary>
        public int OpponentWeaponId { get; internal set; }
        public Dodge OpponentEffectiveDodge { get; internal set; }
        public int OwnHpAfter { get; internal set; }
        public int OpponentHpAfter { get; internal set; }
    }

    /// <summary>
    /// The private view of one kingdom. It holds the viewer's own loadout and provisional lock, only a
    /// ready flag for the opponent, public HP/baselines of the viewer's own duel, the settled board
    /// (frozen for the wave) and the viewer's own cut offer. Other pairs' unfinished duels are not in
    /// it; a bye or eliminated player is a spectator for the wave and uses the spectator feed.
    /// </summary>
    public sealed class FourPlayerParticipantView
    {
        private readonly Func<FourOwnerTerritory> _board;

        internal FourPlayerParticipantView(Func<FourOwnerTerritory> board)
        {
            _board = board;
        }

        public Kingdom Viewer { get; internal set; }
        public string MatchId { get; internal set; }
        public string RulesId { get; internal set; }
        public string SeedCommitmentHex { get; internal set; }
        /// <summary>Disclosed after the match finishes; null before.</summary>
        public string SeedHex { get; internal set; }
        public FourPlayerPhase Phase { get; internal set; }
        public int Wave { get; internal set; }
        public bool Eliminated { get; internal set; }
        /// <summary>True for a bye or an eliminated player: only spectator information is available.</summary>
        public bool IsSpectating { get; internal set; }
        /// <summary>Settled cells of A, B, C, D and neutral (as of the current wave's start).</summary>
        public IReadOnlyList<int> Cells { get; internal set; } = Array.Empty<int>();

        public Loadout OwnLoadout { get; internal set; }
        public bool InDuel { get; internal set; }
        public int PairSlot { get; internal set; } = -1;
        public Kingdom? Opponent { get; internal set; }
        public PlayerSide DuelSide { get; internal set; }
        public PairStage Stage { get; internal set; }
        public int Volley { get; internal set; }
        public int OwnHp { get; internal set; }
        public int OpponentHp { get; internal set; }
        public long OwnBaselineRaw { get; internal set; }
        public long OpponentBaselineRaw { get; internal set; }
        public bool OwnQuakeDue { get; internal set; }
        public bool OwnNetDue { get; internal set; }
        public VolleyInput OwnLock { get; internal set; }
        public bool OpponentLocked { get; internal set; }
        public IReadOnlyList<ResolvedVolley4P> DuelHistory { get; internal set; } = Array.Empty<ResolvedVolley4P>();

        public bool IsCutTurn { get; internal set; }
        public IReadOnlyList<CardId> OfferedCards { get; internal set; } = Array.Empty<CardId>();
        public IReadOnlyList<int> OfferedQuotas { get; internal set; } = Array.Empty<int>();

        /// <summary>A private copy of the board frozen at the wave start (for cut previews).</summary>
        public FourOwnerTerritory CloneBoard() => _board();

        /// <summary>Deterministic text of every field; equal text means equal information.</summary>
        public string ToCanonicalText()
        {
            var sb = new StringBuilder(512);
            void F(string k, object v) => sb.Append(k).Append('=').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append(';');
            F("viewer", Viewer); F("match", MatchId); F("rules", RulesId); F("commit", SeedCommitmentHex); F("seed", SeedHex);
            F("phase", Phase); F("wave", Wave); F("elim", Eliminated); F("spect", IsSpectating); F("cells", string.Join(",", Cells));
            F("own", OwnLoadout == null ? "-" : string.Join(",", OwnLoadout.Weapons) + "+" + OwnLoadout.Reserve);
            F("inDuel", InDuel); F("slot", PairSlot); F("opp", Opponent.HasValue ? Opponent.Value.ToString() : "-"); F("side", DuelSide);
            F("stage", Stage); F("volley", Volley); F("hp", OwnHp); F("oppHp", OpponentHp); F("base", OwnBaselineRaw);
            F("oppBase", OpponentBaselineRaw); F("quake", OwnQuakeDue); F("net", OwnNetDue);
            F("ownLock", OwnLock == null ? "-" : OwnLock.ToString()); F("oppLocked", OpponentLocked);
            foreach (ResolvedVolley4P h in DuelHistory)
                sb.Append('h').Append(h.Volley).Append('[').Append(h.OwnWeaponId).Append(',').Append(h.OpponentWeaponId).Append(',')
                  .Append((int)h.OpponentEffectiveDodge).Append(',').Append(h.OwnHpAfter).Append(',').Append(h.OpponentHpAfter).Append("];");
            F("cut", IsCutTurn); F("cards", string.Join(",", OfferedCards)); F("quotas", string.Join(",", OfferedQuotas));
            return sb.ToString();
        }
    }
}
