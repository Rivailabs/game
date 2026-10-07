using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Land
{
    /// <summary>
    /// Why a cut was rejected. Values are stable so clients can map them to localized messages.
    /// Checks run in the declared order and the first failure is reported.
    /// </summary>
    public enum CutRejection : byte
    {
        None = 0,

        /// <summary>Quota is zero or negative (e.g. a draw): nothing may be transferred.</summary>
        NoAllowance = 1,
        /// <summary>Centre off the grid, scale outside 1-1024 quarters or rotation outside 0-15.</summary>
        InvalidPose = 2,
        /// <summary>The canonical envelope at this scale/rotation contains more than 2Q cells.</summary>
        EnvelopeTooLarge = 3,
        /// <summary>The anchor is not an active board cell.</summary>
        AnchorNotOnBoard = 4,
        /// <summary>The anchor is not owned by the duel loser.</summary>
        AnchorNotOpponentOwned = 5,
        /// <summary>The anchor shares no full edge with winner-owned land (diagonal contact is insufficient).</summary>
        AnchorNotOnBorder = 6,
        /// <summary>The posed envelope does not contain the anchor.</summary>
        AnchorOutsideEnvelope = 7,

        /// <summary>The submitted vertex array is longer than 128 entries (never resampled).</summary>
        TooManyVertices = 8,
        /// <summary>A vertex lies outside cell coordinates 0-255.</summary>
        VertexOutOfRange = 9,
        /// <summary>Fewer than three distinct vertices after removing duplicates and the closing vertex.</summary>
        TooFewVertices = 10,
        /// <summary>The closed path encloses zero area (e.g. all vertices collinear).</summary>
        ZeroArea = 11,
        /// <summary>Two nonadjacent edges cross or touch.</summary>
        SelfIntersecting = 12,
        /// <summary>Two edges overlap along a segment (including an adjacent edge doubling back).</summary>
        OverlappingEdges = 13,

        /// <summary>The filled cut polygon does not include the anchor.</summary>
        AnchorOutsidePolygon = 14,
        /// <summary>The connected claim is larger than the allowance Q.</summary>
        ExceedsQuota = 15,
    }

    /// <summary>
    /// Outcome of validating a manual cut or computing an Auto Cut. When accepted,
    /// <see cref="Cells"/> is the exact set that would transfer. For previews, the connected
    /// component and the discarded fragments are reported whenever they were computed, even if
    /// the cut was then rejected (e.g. for exceeding Q).
    /// </summary>
    public sealed class CutResult
    {
        private static readonly IReadOnlyList<int> Empty = Array.Empty<int>();

        public CutMode Mode { get; }
        public PlayerSide Winner { get; }
        public CardId Card { get; }
        public CardPose Pose { get; }
        public int AnchorCellId { get; }
        public int Quota { get; }

        /// <summary>Territory revision the cut was computed against; a transfer requires it to be current.</summary>
        public long TerritoryRevision { get; }

        public CutRejection Rejection { get; }
        public bool IsAccepted => Rejection == CutRejection.None;

        /// <summary>Exact cells that transfer (ascending ID). Empty unless accepted.</summary>
        public IReadOnlyList<int> Cells => IsAccepted ? PreviewCells : Empty;

        /// <summary>The anchor's 4-connected claim (ascending ID), when computed.</summary>
        public IReadOnlyList<int> PreviewCells { get; }

        /// <summary>Eligible cells inside envelope and polygon that are not connected to the anchor (ascending ID).</summary>
        public IReadOnlyList<int> DiscardedCells { get; }

        internal CutResult(CutMode mode, PlayerSide winner, CardId card, CardPose pose, int anchorCellId, int quota,
            long territoryRevision, CutRejection rejection, IReadOnlyList<int> previewCells, IReadOnlyList<int> discardedCells)
        {
            Mode = mode;
            Winner = winner;
            Card = card;
            Pose = pose;
            AnchorCellId = anchorCellId;
            Quota = quota;
            TerritoryRevision = territoryRevision;
            Rejection = rejection;
            PreviewCells = previewCells ?? Empty;
            DiscardedCells = discardedCells ?? Empty;
        }

        public override string ToString() =>
            IsAccepted ? Mode + " cut accepted: " + Cells.Count + " cells (Q=" + Quota + ")" : Mode + " cut rejected: " + Rejection;
    }
}
