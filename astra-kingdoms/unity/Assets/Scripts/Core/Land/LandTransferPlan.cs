using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Client.Land
{
    /// <summary>
    /// Land transfer and "warrior jump" presentation (ticket 40). It is computed from two ownership
    /// snapshots, before and after the accepted cut, so it can only show what the engine actually
    /// changed: the cells whose owner differs, revealed in waves outward from the anchor (BFS
    /// order), while a warrior marker jumps from the cutter's side of the anchor to the centre of
    /// the captured land. At progress 1 the displayed map equals the "after" snapshot exactly.
    /// </summary>
    public sealed class LandTransferPlan
    {
        public const double DefaultSeconds = 1.2;

        private readonly byte[] _before;
        private readonly byte[] _after;
        private readonly int[] _order;
        private readonly int[] _wave;

        public PlayerSide Cutter { get; }
        /// <summary>Changed cells in reveal order.</summary>
        public IReadOnlyList<int> Cells => _order;
        public int WaveCount { get; }
        public int JumpFromCellId { get; }
        public int JumpToCellId { get; }
        public double Seconds { get; }

        public LandTransferPlan(byte[] before, byte[] after, PlayerSide cutter, int anchorCellId, double seconds = DefaultSeconds)
        {
            if (before == null || after == null || before.Length != Board.GridCellCount || after.Length != Board.GridCellCount)
                throw new ArgumentException("Expected two 256x256 ownership snapshots.");
            _before = (byte[])before.Clone();
            _after = (byte[])after.Clone();
            Cutter = cutter;
            Seconds = seconds > 0 ? seconds : DefaultSeconds;

            var changed = new HashSet<int>();
            for (int i = 0; i < _before.Length; i++)
                if (_before[i] != _after[i]) changed.Add(i);

            // BFS from the anchor through changed cells; disconnected leftovers (never expected for a
            // single-component cut) follow in cell-ID order so nothing is skipped.
            var order = new List<int>(changed.Count);
            var wave = new List<int>(changed.Count);
            var seen = new HashSet<int>();
            var queue = new Queue<KeyValuePair<int, int>>();
            if (changed.Contains(anchorCellId))
            {
                queue.Enqueue(new KeyValuePair<int, int>(anchorCellId, 0));
                seen.Add(anchorCellId);
            }
            int maxWave = 0;
            while (true)
            {
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    order.Add(item.Key);
                    wave.Add(item.Value);
                    maxWave = Math.Max(maxWave, item.Value);
                    int x = Board.X(item.Key), y = Board.Y(item.Key);
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + Board.NeighbourDx[k], ny = y + Board.NeighbourDy[k];
                        if (!Board.IsOnGrid(nx, ny)) continue;
                        int nid = Board.CellId(nx, ny);
                        if (!changed.Contains(nid) || !seen.Add(nid)) continue;
                        queue.Enqueue(new KeyValuePair<int, int>(nid, item.Value + 1));
                    }
                }
                int next = -1;
                foreach (int c in changed)
                    if (!seen.Contains(c) && (next < 0 || c < next)) next = c;
                if (next < 0) break;
                seen.Add(next);
                queue.Enqueue(new KeyValuePair<int, int>(next, maxWave + 1));
            }
            _order = order.ToArray();
            _wave = wave.ToArray();
            WaveCount = order.Count == 0 ? 0 : maxWave + 1;

            JumpFromCellId = NearestOwned(_before, cutter, anchorCellId);
            JumpToCellId = Centre(order);
        }

        /// <summary>Number of cells shown as transferred at <paramref name="progress"/> (0..1).</summary>
        public int RevealedCount(double progress)
        {
            if (_order.Length == 0) return 0;
            if (progress >= 1) return _order.Length;
            if (progress <= 0) return 0;
            double wavesShown = progress * WaveCount;
            int n = 0;
            while (n < _order.Length && _wave[n] < wavesShown) n++;
            return n;
        }

        /// <summary>The ownership snapshot to display at <paramref name="progress"/>; at 1 it is exactly the "after" state.</summary>
        public byte[] Frame(double progress)
        {
            if (progress >= 1) return (byte[])_after.Clone();
            var frame = (byte[])_before.Clone();
            int n = RevealedCount(progress);
            for (int i = 0; i < n; i++) frame[_order[i]] = _after[_order[i]];
            return frame;
        }

        /// <summary>Warrior marker position (cell coordinates, y down) and height above the map (0..1) at a progress.</summary>
        public void Warrior(double progress, out double x, out double y, out double height)
        {
            double t = Math.Max(0, Math.Min(1, progress * 1.6)); // the jump lands early, then the wave continues
            double x0 = Board.X(JumpFromCellId) + 0.5, y0 = Board.Y(JumpFromCellId) + 0.5;
            double x1 = Board.X(JumpToCellId) + 0.5, y1 = Board.Y(JumpToCellId) + 0.5;
            x = x0 + (x1 - x0) * t;
            y = y0 + (y1 - y0) * t;
            height = 4 * t * (1 - t);
        }

        private static int NearestOwned(byte[] owners, PlayerSide side, int fromCellId)
        {
            if (!Board.IsValidCellId(fromCellId)) fromCellId = Board.CellId(Board.Size / 2, Board.Size / 2);
            int fx = Board.X(fromCellId), fy = Board.Y(fromCellId);
            int best = fromCellId;
            long bestD = long.MaxValue;
            for (int r = 0; r <= 8 && bestD == long.MaxValue; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int x = fx + dx, y = fy + dy;
                        if (!Board.IsOnGrid(x, y) || owners[Board.CellId(x, y)] != (byte)side) continue;
                        long d = (long)dx * dx + (long)dy * dy;
                        if (d < bestD) { bestD = d; best = Board.CellId(x, y); }
                    }
            return best;
        }

        private static int Centre(List<int> cells)
        {
            if (cells.Count == 0) return Board.CellId(Board.Size / 2, Board.Size / 2);
            long sx = 0, sy = 0;
            foreach (int c in cells) { sx += Board.X(c); sy += Board.Y(c); }
            double mx = (double)sx / cells.Count, my = (double)sy / cells.Count;
            int best = cells[0];
            double bestD = double.MaxValue;
            foreach (int c in cells)
            {
                double d = (Board.X(c) - mx) * (Board.X(c) - mx) + (Board.Y(c) - my) * (Board.Y(c) - my);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }
    }
}
