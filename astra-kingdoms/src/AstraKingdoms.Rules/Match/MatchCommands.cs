using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Match
{
    /// <summary>Explicit match phases. Every transition publishes a new state revision.</summary>
    public enum MatchPhase : byte
    {
        /// <summary>Both players submit private loadouts.</summary>
        Setup = 0,
        /// <summary>The duel terrain is announced; no locks yet (2 s online).</summary>
        TerrainAnnounce = 1,
        /// <summary>Hidden simultaneous selection for the open volley.</summary>
        Selection = 2,
        /// <summary>The volley has resolved; clients replay its events.</summary>
        Resolution = 3,
        /// <summary>The duel winner chooses a card, poses it and cuts.</summary>
        CardAndCut = 4,
        /// <summary>Terminal.</summary>
        MatchOver = 5,
    }

    public enum CommandKind : byte
    {
        SubmitLoadout = 1,
        LockInput = 2,
        SubmitCut = 3,
        /// <summary>Server-issued: the current phase's timer ended (announcement, deadline, replay or cut window).</summary>
        AdvancePhase = 4,
    }

    /// <summary>
    /// Fields every command carries (plan: "Command fields and units"): schema_version = 1, the
    /// 32-byte rules hash, canonical UUIDs for match and request, the one-based round (0 during
    /// Setup) and the published phase snapshot revision the sender acted on. The player is never a
    /// field: the authenticated connection supplies it.
    /// </summary>
    public sealed class CommandHeader
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; }
        public byte[] RulesHash { get; }
        public string MatchId { get; }
        public string RequestId { get; }
        public int RoundIndex { get; }
        public ulong ExpectedStateRevision { get; }

        public CommandHeader(byte[] rulesHash, string matchId, string requestId, int roundIndex, ulong expectedStateRevision,
            int schemaVersion = CurrentSchemaVersion)
        {
            SchemaVersion = schemaVersion;
            RulesHash = rulesHash == null ? null : (byte[])rulesHash.Clone();
            MatchId = matchId;
            RequestId = requestId;
            RoundIndex = roundIndex;
            ExpectedStateRevision = expectedStateRevision;
        }

        /// <summary>Same header with another request ID (convenience for retries in tests and bots).</summary>
        public CommandHeader WithRequestId(string requestId) =>
            new CommandHeader(RulesHash, MatchId, requestId, RoundIndex, ExpectedStateRevision, SchemaVersion);

        internal void WriteTo(CanonicalWriter w)
        {
            w.I32(SchemaVersion).Block(RulesHash ?? Array.Empty<byte>()).Ascii(MatchId ?? string.Empty)
             .Ascii(RequestId ?? string.Empty).I32(RoundIndex).U64(ExpectedStateRevision);
        }
    }

    /// <summary>Base of all typed match commands.</summary>
    public abstract class MatchCommand
    {
        public CommandHeader Header { get; }
        public abstract CommandKind Kind { get; }

        protected MatchCommand(CommandHeader header)
        {
            Header = header ?? throw new ArgumentNullException(nameof(header));
        }

        internal abstract void WritePayload(CanonicalWriter w);

        /// <summary>Canonical bytes of header and payload; duplicates are compared on these.</summary>
        public byte[] CanonicalBytes()
        {
            var w = new CanonicalWriter();
            w.U8((int)Kind);
            Header.WriteTo(w);
            WritePayload(w);
            return w.ToArray();
        }
    }

    /// <summary>Private loadout for the match (ticket 15). Round index 0.</summary>
    public sealed class SubmitLoadoutCommand : MatchCommand
    {
        private readonly int[] _weapons;

        public IReadOnlyList<int> Weapons => _weapons;
        /// <summary>Reserve weapon ID or 0.</summary>
        public int Reserve { get; }
        public override CommandKind Kind => CommandKind.SubmitLoadout;

        public SubmitLoadoutCommand(CommandHeader header, IReadOnlyList<int> weapons, int reserve = 0) : base(header)
        {
            _weapons = weapons == null ? Array.Empty<int>() : new List<int>(weapons).ToArray();
            Reserve = reserve;
        }

        internal override void WritePayload(CanonicalWriter w)
        {
            w.U32((uint)_weapons.Length);
            foreach (int id in _weapons) w.I32(id);
            w.I32(Reserve);
        }
    }

    /// <summary>LockInput: volley_index 1-3, weapon 1-20 or 1000, pitch/yaw in 0.25°, power 70-100, dodge 0-3.</summary>
    public sealed class LockInputCommand : MatchCommand
    {
        public int VolleyIndex { get; }
        public int WeaponId { get; }
        public int PitchQdeg { get; }
        public int YawQdeg { get; }
        public int PowerPercent { get; }
        public Dodge Dodge { get; }
        public override CommandKind Kind => CommandKind.LockInput;

        public LockInputCommand(CommandHeader header, int volleyIndex, int weaponId, int pitchQdeg, int yawQdeg, int powerPercent, Dodge dodge)
            : base(header)
        {
            VolleyIndex = volleyIndex;
            WeaponId = weaponId;
            PitchQdeg = pitchQdeg;
            YawQdeg = yawQdeg;
            PowerPercent = powerPercent;
            Dodge = dodge;
        }

        public LockInputCommand(CommandHeader header, int volleyIndex, VolleyInput choice)
            : this(header, volleyIndex, choice.WeaponId, choice.PitchQdeg, choice.YawQdeg, choice.PowerPercent, choice.Dodge)
        {
        }

        public VolleyInput ToChoice() => new VolleyInput(WeaponId, PitchQdeg, YawQdeg, PowerPercent, Dodge);

        internal override void WritePayload(CanonicalWriter w) =>
            w.I32(VolleyIndex).I32(WeaponId).I32(PitchQdeg).I32(YawQdeg).I32(PowerPercent).U8((int)Dodge);
    }

    /// <summary>
    /// SubmitCut: expected map revision, card, anchor cell (y×256+x), envelope centre 0-255,
    /// rotation 0-15, scale 1-1024 quarters, Manual or Auto, and the Manual vertex array
    /// (Auto has none).
    /// </summary>
    public sealed class SubmitCutCommand : MatchCommand
    {
        private readonly CellPoint[] _vertices;

        public ulong ExpectedMapRevision { get; }
        public CardId CardId { get; }
        public int AnchorCellId { get; }
        public int CenterX { get; }
        public int CenterY { get; }
        public int Rotation { get; }
        public int ScaleQuarters { get; }
        public CutMode Mode { get; }
        public IReadOnlyList<CellPoint> Vertices => _vertices;
        public override CommandKind Kind => CommandKind.SubmitCut;

        public SubmitCutCommand(CommandHeader header, ulong expectedMapRevision, CardId cardId, int anchorCellId,
            int centerX, int centerY, int rotation, int scaleQuarters, CutMode mode, IReadOnlyList<CellPoint> vertices = null)
            : base(header)
        {
            ExpectedMapRevision = expectedMapRevision;
            CardId = cardId;
            AnchorCellId = anchorCellId;
            CenterX = centerX;
            CenterY = centerY;
            Rotation = rotation;
            ScaleQuarters = scaleQuarters;
            Mode = mode;
            _vertices = vertices == null ? Array.Empty<CellPoint>() : new List<CellPoint>(vertices).ToArray();
        }

        public CardPose Pose => new CardPose(CenterX, CenterY, ScaleQuarters, Rotation);

        internal override void WritePayload(CanonicalWriter w)
        {
            w.U64(ExpectedMapRevision).U8((int)CardId).I32(AnchorCellId).I32(CenterX).I32(CenterY)
             .I32(Rotation).I32(ScaleQuarters).U8((int)Mode).U32((uint)_vertices.Length);
            foreach (CellPoint p in _vertices) w.I32(p.X).I32(p.Y);
        }
    }

    /// <summary>
    /// Server-issued phase advance: the announcement ended, the choice deadline passed (unlocked
    /// players receive Pass), the replay window ended, or the cut window expired (zero transfer).
    /// The expected revision makes a retried timer idempotent: it can never advance twice.
    /// </summary>
    public sealed class AdvancePhaseCommand : MatchCommand
    {
        public override CommandKind Kind => CommandKind.AdvancePhase;

        public AdvancePhaseCommand(CommandHeader header) : base(header)
        {
        }

        internal override void WritePayload(CanonicalWriter w)
        {
        }
    }

    /// <summary>Stable rejection codes. Rules-level codes from <see cref="InputValidator"/> and cut codes ("CUT_*") pass through.</summary>
    public static class MatchErrors
    {
        public const string SchemaVersion = "SCHEMA_VERSION";
        public const string RulesHash = "RULES_HASH";
        public const string MatchId = "MATCH_ID";
        public const string RequestId = "REQUEST_ID";
        /// <summary>A request ID was reused with a different canonical payload or sender.</summary>
        public const string RequestIdReused = "REQUEST_ID_REUSED";
        public const string WrongPhase = "WRONG_PHASE";
        public const string WrongRound = "WRONG_ROUND";
        public const string StaleStateRevision = "STALE_STATE_REVISION";
        public const string StaleMapRevision = "STALE_MAP_REVISION";
        public const string AlreadyLocked = "ALREADY_LOCKED";
        public const string LoadoutAlreadySubmitted = "LOADOUT_ALREADY_SUBMITTED";
        public const string NotDuelWinner = "NOT_DUEL_WINNER";
        public const string CardNotOffered = "CARD_NOT_OFFERED";
        public const string AnchorRange = "ANCHOR_RANGE";
        public const string AutoHasVertices = "AUTO_HAS_VERTICES";
        public const string CutModeInvalid = "CUT_MODE";
        public const string MatchOver = "MATCH_OVER";
        public const string CutPrefix = "CUT_";
    }

    /// <summary>
    /// Result of submitting a command. An accepted receipt is stored: a duplicate with the same
    /// request ID and identical canonical payload returns this very instance.
    /// </summary>
    public sealed class CommandReceipt
    {
        public bool Accepted { get; }
        public string RejectCode { get; }
        public string Message { get; }
        public string MatchId { get; }
        public string RequestId { get; }
        public CommandKind Kind { get; }
        /// <summary>Authenticated sender, or null for a server command.</summary>
        public PlayerSide? Player { get; }
        public int RoundIndex { get; }
        public int VolleyIndex { get; }
        /// <summary>Phase snapshot revision the command was accepted against.</summary>
        public ulong StateRevision { get; }
        /// <summary>One-based position of the accepted command in the match's command log.</summary>
        public ulong InputRevision { get; }
        public byte[] RulesHash { get; }
        /// <summary>Cells moved by an accepted cut (0 otherwise).</summary>
        public int CellsTransferred { get; }
        /// <summary>Map revision after the command.</summary>
        public ulong MapRevision { get; }

        internal CommandReceipt(bool accepted, string code, string message, string matchId, string requestId, CommandKind kind,
            PlayerSide? player, int round, int volley, ulong stateRevision, ulong inputRevision, byte[] rulesHash,
            int cellsTransferred, ulong mapRevision)
        {
            Accepted = accepted;
            RejectCode = code;
            Message = message;
            MatchId = matchId;
            RequestId = requestId;
            Kind = kind;
            Player = player;
            RoundIndex = round;
            VolleyIndex = volley;
            StateRevision = stateRevision;
            InputRevision = inputRevision;
            RulesHash = rulesHash;
            CellsTransferred = cellsTransferred;
            MapRevision = mapRevision;
        }

        public override string ToString() =>
            Accepted ? Kind + " accepted #" + InputRevision + " (round " + RoundIndex + ", volley " + VolleyIndex + ", rev " + StateRevision + ")"
                     : Kind + " rejected: " + RejectCode + (string.IsNullOrEmpty(Message) ? string.Empty : " - " + Message);
    }

    /// <summary>Why the match ended.</summary>
    public enum TerminalReason : byte
    {
        None = 0,
        /// <summary>A player reached at least 45,936 cells immediately after a cut.</summary>
        Territory90 = 1,
        /// <summary>Round eight finished; exact cell counts decide (equal = draw).</summary>
        RoundsComplete = 2,
        /// <summary>One player reached two consecutive selection timeouts.</summary>
        Forfeit = 3,
        /// <summary>Both reached the forfeiture condition in the same deadline transaction.</summary>
        Void = 4,
    }

    /// <summary>Terminal outcome. Rewards are idempotent: read them from here, once.</summary>
    public sealed class MatchResult
    {
        public TerminalReason Reason { get; }
        /// <summary>The winner; null for a draw or void.</summary>
        public PlayerSide? Winner { get; }
        public PlayerSide? ForfeitedBy { get; }
        public int CellsA { get; }
        public int CellsB { get; }
        /// <summary>Rounds whose duel started (1-8).</summary>
        public int RoundsPlayed { get; }

        public bool IsDraw => Reason == TerminalReason.RoundsComplete && Winner == null;
        public bool IsVoid => Reason == TerminalReason.Void;
        /// <summary>A forfeit or void is not a normally completed match.</summary>
        public bool CountsAsCompleted => Reason == TerminalReason.Territory90 || Reason == TerminalReason.RoundsComplete;
        /// <summary>A void grants no winner reward.</summary>
        public bool WinnerRewardEligible => Winner.HasValue && Reason != TerminalReason.Void;

        public MatchResult(TerminalReason reason, PlayerSide? winner, PlayerSide? forfeitedBy, int cellsA, int cellsB, int roundsPlayed)
        {
            Reason = reason;
            Winner = winner;
            ForfeitedBy = forfeitedBy;
            CellsA = cellsA;
            CellsB = cellsB;
            RoundsPlayed = roundsPlayed;
        }

        public override string ToString() =>
            Reason + (Winner.HasValue ? " winner " + Winner.Value : IsVoid ? string.Empty : " draw") +
            " (A " + CellsA + ", B " + CellsB + ", rounds " + RoundsPlayed + ")";
    }
}
