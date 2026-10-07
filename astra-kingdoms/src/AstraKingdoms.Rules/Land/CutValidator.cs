using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Land
{
    /// <summary>
    /// The plan's "Cut validation and transfer" algorithm for manual cuts, and Auto Cut.
    /// <para>
    /// Both start with the same checks, in order: positive quota; pose in range; canonical
    /// envelope at most 2Q cells; anchor active, loser-owned and sharing a full edge with
    /// winner-owned land; anchor inside the posed envelope. A manual cut then normalizes and
    /// validates the polygon (see <see cref="CutPolygon.TryCreate"/>), forms the candidate
    /// active ∩ loser-owned ∩ envelope ∩ polygon, requires the anchor in it, keeps the anchor's
    /// 4-connected component and rejects it if larger than Q. Nothing here mutates the territory;
    /// apply an accepted result with <see cref="LandTransfer.Apply"/>.
    /// </para>
    /// </summary>
    public static class CutValidator
    {
        /// <summary>Validates a hand-drawn cut. Vertices are snapped integer cell coordinates.</summary>
        public static CutResult Validate(Territory territory, PlayerSide winner, CardId card, CardPose pose,
            CellPoint anchor, IReadOnlyList<CellPoint> vertices, int quota)
        {
            if (territory == null) throw new ArgumentNullException(nameof(territory));
            int anchorId = anchor.IsOnGrid ? anchor.CellId : -1;

            CutRejection common = CheckCommon(territory, winner, card, pose, anchor, quota);
            if (common != CutRejection.None)
                return Reject(CutMode.Manual, territory, winner, card, pose, anchorId, quota, common);

            CutRejection shape = CutPolygon.TryCreate(vertices, out CutPolygon polygon);
            if (shape != CutRejection.None)
                return Reject(CutMode.Manual, territory, winner, card, pose, anchorId, quota, shape);

            if (!polygon.Contains(anchor.X, anchor.Y))
                return Reject(CutMode.Manual, territory, winner, card, pose, anchorId, quota, CutRejection.AnchorOutsidePolygon);

            // Candidate: active ∩ loser-owned ∩ envelope ∩ polygon.
            PlayerSide loser = Board.Opponent(winner);
            var candidate = new bool[Board.GridCellCount];
            var candidateList = new List<int>();
            foreach (int cell in CardEnvelope.RasterizeOnBoard(card, pose))
            {
                if (!territory.IsOwnedBy(cell, loser)) continue;
                if (!polygon.Contains(Board.X(cell), Board.Y(cell))) continue;
                candidate[cell] = true;
                candidateList.Add(cell);
            }

            // Keep only the 4-connected component containing the anchor.
            List<int> component = Flood(candidate, anchorId, int.MaxValue);
            var inComponent = new bool[Board.GridCellCount];
            foreach (int cell in component) inComponent[cell] = true;
            var discarded = new List<int>();
            foreach (int cell in candidateList)
            {
                if (!inComponent[cell]) discarded.Add(cell);
            }
            component.Sort();

            CutRejection final = component.Count == 0 ? CutRejection.AnchorOutsidePolygon
                : component.Count > quota ? CutRejection.ExceedsQuota
                : CutRejection.None;
            return new CutResult(CutMode.Manual, winner, card, pose, anchorId, quota, territory.Revision, final, component, discarded);
        }

        /// <summary>
        /// Auto Cut: breadth-first traversal from the anchor through loser-owned active cells inside
        /// the envelope, visiting neighbours in the fixed order North (y−1), East (x+1), South (y+1),
        /// West (x−1), and taking the first Q cells in visiting order. It never exceeds Q and does
        /// not promise to fill Q when fewer cells are reachable.
        /// </summary>
        public static CutResult AutoCut(Territory territory, PlayerSide winner, CardId card, CardPose pose,
            CellPoint anchor, int quota)
        {
            if (territory == null) throw new ArgumentNullException(nameof(territory));
            int anchorId = anchor.IsOnGrid ? anchor.CellId : -1;

            CutRejection common = CheckCommon(territory, winner, card, pose, anchor, quota);
            if (common != CutRejection.None)
                return Reject(CutMode.Auto, territory, winner, card, pose, anchorId, quota, common);

            PlayerSide loser = Board.Opponent(winner);
            var eligible = new bool[Board.GridCellCount];
            foreach (int cell in CardEnvelope.RasterizeOnBoard(card, pose))
            {
                if (territory.IsOwnedBy(cell, loser)) eligible[cell] = true;
            }

            List<int> cells = Flood(eligible, anchorId, quota);
            cells.Sort();
            return new CutResult(CutMode.Auto, winner, card, pose, anchorId, quota, territory.Revision, CutRejection.None, cells, null);
        }

        /// <summary>The checks shared by manual and Auto cuts, in their fixed order.</summary>
        public static CutRejection CheckCommon(Territory territory, PlayerSide winner, CardId card, CardPose pose,
            CellPoint anchor, int quota)
        {
            if (quota <= 0) return CutRejection.NoAllowance;
            if (!pose.IsInRange) return CutRejection.InvalidPose;
            if (!CardEnvelope.FitsAllowance(card, pose.ScaleQuarters, pose.Rotation, quota)) return CutRejection.EnvelopeTooLarge;
            if (!Board.IsActive(anchor.X, anchor.Y)) return CutRejection.AnchorNotOnBoard;
            if (!territory.IsOwnedBy(anchor.CellId, Board.Opponent(winner))) return CutRejection.AnchorNotOpponentOwned;
            if (!territory.IsBorderAnchor(anchor.CellId, winner)) return CutRejection.AnchorNotOnBorder;
            if (!CardEnvelope.Contains(card, pose, anchor.X, anchor.Y)) return CutRejection.AnchorOutsideEnvelope;
            return CutRejection.None;
        }

        /// <summary>
        /// Breadth-first flood from <paramref name="start"/> within <paramref name="allowed"/>,
        /// neighbour order N, E, S, W, stopping after <paramref name="limit"/> cells. Returns cells
        /// in visiting order (empty when the start is not allowed).
        /// </summary>
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
                int x = Board.X(cell);
                int y = Board.Y(cell);
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Board.NeighbourDx[d];
                    int ny = y + Board.NeighbourDy[d];
                    if (!Board.IsOnGrid(nx, ny)) continue;
                    int next = Board.CellId(nx, ny);
                    if (visited[next] || !allowed[next]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
            return result;
        }

        private static CutResult Reject(CutMode mode, Territory territory, PlayerSide winner, CardId card, CardPose pose,
            int anchorId, int quota, CutRejection reason) =>
            new CutResult(mode, winner, card, pose, anchorId, quota, territory.Revision, reason, null, null);
    }

    /// <summary>Why an accepted-looking cut could not be applied.</summary>
    public enum TransferFailure : byte
    {
        None = 0,
        /// <summary>The cut result was a rejection.</summary>
        CutNotAccepted = 1,
        /// <summary>The territory changed since the cut was validated (stale command).</summary>
        StaleRevision = 2,
    }

    /// <summary>Result of applying a cut: counts after the transfer and the immediate victory check.</summary>
    public readonly struct TransferOutcome
    {
        public readonly TransferFailure Failure;
        public readonly int CellsTransferred;
        public readonly int WinnerCells;
        public readonly int LoserCells;
        public readonly long Revision;

        /// <summary>True when the winner now holds at least 45,936 cells: the match ends immediately.</summary>
        public readonly bool WinnerReachedVictory;

        public TransferOutcome(TransferFailure failure, int cellsTransferred, int winnerCells, int loserCells, long revision, bool victory)
        {
            Failure = failure;
            CellsTransferred = cellsTransferred;
            WinnerCells = winnerCells;
            LoserCells = loserCells;
            Revision = revision;
            WinnerReachedVictory = victory;
        }

        public bool Applied => Failure == TransferFailure.None;
    }

    /// <summary>Applies accepted cuts atomically and enforces the post-transfer invariants.</summary>
    public static class LandTransfer
    {
        /// <summary>
        /// Transfers exactly <see cref="CutResult.Cells"/> from the loser to the winner, then checks
        /// disjoint ownership, coverage of the active board, unchanged terrain, nonnegative counts
        /// and area(A) + area(B) = 51,040, and compares the winner's count with 45,936. Rejected or
        /// stale results change nothing.
        /// </summary>
        public static TransferOutcome Apply(Territory territory, CutResult cut)
        {
            if (territory == null) throw new ArgumentNullException(nameof(territory));
            if (cut == null) throw new ArgumentNullException(nameof(cut));
            PlayerSide winner = cut.Winner;
            PlayerSide loser = Board.Opponent(winner);

            TransferFailure failure = !cut.IsAccepted ? TransferFailure.CutNotAccepted
                : cut.TerritoryRevision != territory.Revision ? TransferFailure.StaleRevision
                : TransferFailure.None;
            if (failure != TransferFailure.None)
            {
                return new TransferOutcome(failure, 0, territory.CellCount(winner), territory.CellCount(loser),
                    territory.Revision, territory.HasReachedVictory(winner));
            }

            territory.Transfer(cut.Cells as IReadOnlyCollection<int> ?? new List<int>(cut.Cells), loser);
            territory.CheckInvariants();
            return new TransferOutcome(TransferFailure.None, cut.Cells.Count, territory.CellCount(winner),
                territory.CellCount(loser), territory.Revision, territory.HasReachedVictory(winner));
        }
    }
}
