using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Modes.FourPlayer
{
    /// <summary>
    /// Outcome of validating a four-player cut against the wave's frozen board. When accepted,
    /// <see cref="Cells"/> is the exact set that transfers from <see cref="Loser"/> to
    /// <see cref="Winner"/> at wave settlement.
    /// </summary>
    public sealed class FourPlayerCutResult
    {
        private static readonly IReadOnlyList<int> Empty = Array.Empty<int>();

        public CutMode Mode { get; }
        public Kingdom Winner { get; }
        public Kingdom Loser { get; }
        public CardId Card { get; }
        public CardPose Pose { get; }
        public int AnchorCellId { get; }
        public int Quota { get; }
        public long BoardRevision { get; }
        public CutRejection Rejection { get; }
        public bool IsAccepted => Rejection == CutRejection.None;
        public IReadOnlyList<int> Cells => IsAccepted ? PreviewCells : Empty;
        public IReadOnlyList<int> PreviewCells { get; }
        public IReadOnlyList<int> DiscardedCells { get; }

        internal FourPlayerCutResult(CutMode mode, Kingdom winner, Kingdom loser, CardId card, CardPose pose, int anchor, int quota,
            long revision, CutRejection rejection, IReadOnlyList<int> preview, IReadOnlyList<int> discarded)
        {
            Mode = mode;
            Winner = winner;
            Loser = loser;
            Card = card;
            Pose = pose;
            AnchorCellId = anchor;
            Quota = quota;
            BoardRevision = revision;
            Rejection = rejection;
            PreviewCells = preview ?? Empty;
            DiscardedCells = discarded ?? Empty;
        }

        public CellTransfer ToTransfer() => new CellTransfer(Loser, Winner, Cells);

        public override string ToString() =>
            IsAccepted ? Mode + " cut " + Winner + "<-" + Loser + ": " + Cells.Count + " cells (Q=" + Quota + ")" : Mode + " cut rejected: " + Rejection;
    }

    /// <summary>
    /// Card geometry for the four-owner candidate (plan: "Geometry ... require their own approved
    /// geometry fixtures; V1 rules do not change"). It reuses the AK-TR-1 card envelopes, pose
    /// ranges, 2Q envelope limit, polygon validation and 4-connected anchor component, with these
    /// candidate differences, all PROPOSED:
    /// <list type="bullet">
    /// <item>Allowance Q = min(AK-TR-1 card allowance, 5% of the board = 2,552, the loser's area on the
    /// frozen board).</item>
    /// <item>Only the <b>defeated opponent's</b> cells are eligible: a third kingdom's land and locked
    /// neutral land are never part of a candidate, even inside the envelope and polygon.</item>
    /// <item>The anchor need not touch the winner's land, because pairs may share no border; the kept
    /// component may therefore be a pocket disconnected from the winner.</item>
    /// </list>
    /// Validation reads the frozen board only and never mutates it.
    /// </summary>
    public static class FourPlayerCutRules
    {
        /// <summary>The candidate allowance for a card after a won duel with HP difference D.</summary>
        public static int Allowance(int loserCells, int hpDifferenceUnits, CardId card)
        {
            int v1 = LandQuota.Compute(loserCells, hpDifferenceUnits, card);
            return Math.Min(v1, Math.Min(FourPlayerRules.TransferCapCells, loserCells));
        }

        public static CutRejection CheckCommon(FourOwnerTerritory board, Kingdom loser, CardId card, CardPose pose, CellPoint anchor, int quota)
        {
            if (board == null) throw new ArgumentNullException(nameof(board));
            if (quota <= 0) return CutRejection.NoAllowance;
            if (!pose.IsInRange) return CutRejection.InvalidPose;
            if (!CardEnvelope.FitsAllowance(card, pose.ScaleQuarters, pose.Rotation, quota)) return CutRejection.EnvelopeTooLarge;
            if (!Board.IsActive(anchor.X, anchor.Y)) return CutRejection.AnchorNotOnBoard;
            if (!board.IsOwnedBy(anchor.CellId, loser)) return CutRejection.AnchorNotOpponentOwned;
            if (!CardEnvelope.Contains(card, pose, anchor.X, anchor.Y)) return CutRejection.AnchorOutsideEnvelope;
            return CutRejection.None;
        }

        /// <summary>Validates a hand-drawn cut against the frozen board.</summary>
        public static FourPlayerCutResult Validate(FourOwnerTerritory board, Kingdom winner, Kingdom loser, CardId card, CardPose pose,
            CellPoint anchor, IReadOnlyList<CellPoint> vertices, int quota)
        {
            CheckPair(winner, loser);
            int anchorId = anchor.IsOnGrid ? anchor.CellId : -1;
            CutRejection common = CheckCommon(board, loser, card, pose, anchor, quota);
            if (common != CutRejection.None) return Reject(CutMode.Manual, board, winner, loser, card, pose, anchorId, quota, common);

            CutRejection shape = CutPolygon.TryCreate(vertices, out CutPolygon polygon);
            if (shape != CutRejection.None) return Reject(CutMode.Manual, board, winner, loser, card, pose, anchorId, quota, shape);
            if (!polygon.Contains(anchor.X, anchor.Y))
                return Reject(CutMode.Manual, board, winner, loser, card, pose, anchorId, quota, CutRejection.AnchorOutsidePolygon);

            var candidate = new bool[Board.GridCellCount];
            var candidateList = new List<int>();
            foreach (int cell in CardEnvelope.RasterizeOnBoard(card, pose))
            {
                if (!board.IsOwnedBy(cell, loser)) continue;
                if (!polygon.Contains(Board.X(cell), Board.Y(cell))) continue;
                candidate[cell] = true;
                candidateList.Add(cell);
            }

            List<int> component = Flood(candidate, anchorId, int.MaxValue);
            var inComponent = new bool[Board.GridCellCount];
            foreach (int cell in component) inComponent[cell] = true;
            var discarded = new List<int>();
            foreach (int cell in candidateList)
                if (!inComponent[cell]) discarded.Add(cell);
            component.Sort();

            CutRejection final = component.Count == 0 ? CutRejection.AnchorOutsidePolygon
                : component.Count > quota ? CutRejection.ExceedsQuota
                : CutRejection.None;
            return new FourPlayerCutResult(CutMode.Manual, winner, loser, card, pose, anchorId, quota, board.Revision, final, component, discarded);
        }

        /// <summary>
        /// Auto Cut: breadth-first from the anchor through the loser's cells inside the envelope
        /// (neighbour order N, E, S, W), taking the first Q cells in visiting order.
        /// </summary>
        public static FourPlayerCutResult AutoCut(FourOwnerTerritory board, Kingdom winner, Kingdom loser, CardId card, CardPose pose,
            CellPoint anchor, int quota)
        {
            CheckPair(winner, loser);
            int anchorId = anchor.IsOnGrid ? anchor.CellId : -1;
            CutRejection common = CheckCommon(board, loser, card, pose, anchor, quota);
            if (common != CutRejection.None) return Reject(CutMode.Auto, board, winner, loser, card, pose, anchorId, quota, common);

            var eligible = new bool[Board.GridCellCount];
            foreach (int cell in CardEnvelope.RasterizeOnBoard(card, pose))
                if (board.IsOwnedBy(cell, loser)) eligible[cell] = true;
            List<int> cells = Flood(eligible, anchorId, quota);
            cells.Sort();
            return new FourPlayerCutResult(CutMode.Auto, winner, loser, card, pose, anchorId, quota, board.Revision, CutRejection.None, cells, null);
        }

        /// <summary>
        /// The largest scale (quarter cells) at which the card's canonical envelope fits 2Q at the
        /// given rotation, or 0 when even the smallest scale does not fit.
        /// </summary>
        public static int LargestFittingScale(CardId card, int rotation, int quota)
        {
            if (quota <= 0 || !CardEnvelope.FitsAllowance(card, RulesConstants.MinScaleQuarters, rotation, quota)) return 0;
            int lo = RulesConstants.MinScaleQuarters, hi = RulesConstants.MaxScaleQuarters;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (CardEnvelope.FitsAllowance(card, mid, rotation, quota)) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        private static void CheckPair(Kingdom winner, Kingdom loser)
        {
            if (winner == loser) throw new ArgumentException("Winner and loser must differ.");
        }

        private static List<int> Flood(bool[] allowed, int start, int limit)
        {
            var result = new List<int>();
            if (start < 0 || !allowed[start] || limit <= 0) return result;
            var visited = new bool[Board.GridCellCount];
            var queue = new Queue<int>();
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0 && result.Count < limit)
            {
                int cell = queue.Dequeue();
                result.Add(cell);
                int x = Board.X(cell), y = Board.Y(cell);
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Board.NeighbourDx[d], ny = y + Board.NeighbourDy[d];
                    if (!Board.IsOnGrid(nx, ny)) continue;
                    int next = Board.CellId(nx, ny);
                    if (visited[next] || !allowed[next]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
            return result;
        }

        private static FourPlayerCutResult Reject(CutMode mode, FourOwnerTerritory board, Kingdom winner, Kingdom loser, CardId card,
            CardPose pose, int anchorId, int quota, CutRejection reason) =>
            new FourPlayerCutResult(mode, winner, loser, card, pose, anchorId, quota, board.Revision, reason, null, null);
    }
}
