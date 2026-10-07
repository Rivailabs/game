using System;
using System.Collections.Generic;
using System.Globalization;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Client.Land
{
    /// <summary>Exact land totals with display percentages that always add up to 100.0%.</summary>
    public readonly struct LandTotals
    {
        public readonly int CellsA;
        public readonly int CellsB;
        /// <summary>Tenths of a percent (largest-remainder rounding so A + B = 1000).</summary>
        public readonly int PermilleA;
        public readonly int PermilleB;

        public LandTotals(int cellsA, int cellsB, int permilleA, int permilleB)
        {
            CellsA = cellsA;
            CellsB = cellsB;
            PermilleA = permilleA;
            PermilleB = permilleB;
        }

        public int Cells(PlayerSide side) => side == PlayerSide.A ? CellsA : CellsB;
        public int Permille(PlayerSide side) => side == PlayerSide.A ? PermilleA : PermilleB;

        /// <summary>"51.2" style text of a permille value (invariant digits).</summary>
        public static string PercentText(int permille) =>
            (permille / 10).ToString(CultureInfo.InvariantCulture) + "." + (permille % 10).ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Totals from exact cell counts. Percentages use largest-remainder rounding to 0.1%, so the
        /// two displayed values always reconcile to 100.0% and to the board's 51,040 cells.
        /// </summary>
        public static LandTotals From(int cellsA, int cellsB)
        {
            int total = cellsA + cellsB;
            if (total <= 0) return new LandTotals(cellsA, cellsB, 0, 0);
            long a = (long)cellsA * 1000, b = (long)cellsB * 1000;
            int pa = (int)(a / total), pb = (int)(b / total);
            long ra = a % total, rb = b % total;
            int missing = 1000 - pa - pb;
            // Ties go to A deterministically; missing is 0 or 1 for two parts.
            if (missing > 0)
            {
                if (ra >= rb) pa += missing;
                else pb += missing;
            }
            return new LandTotals(cellsA, cellsB, pa, pb);
        }
    }

    /// <summary>A labelled terrain region on the land map.</summary>
    public sealed class TerrainLabel
    {
        public TerrainType Terrain { get; internal set; }
        /// <summary>Cell nearest the region's centroid that belongs to the region (label anchor).</summary>
        public int AnchorCellId { get; internal set; }
        public int Cells { get; internal set; }
        public int CellsA { get; internal set; }
        public int CellsB { get; internal set; }

        /// <summary>Owner holding most of the region, or null when evenly split.</summary>
        public PlayerSide? MajorityOwner => CellsA == CellsB ? (PlayerSide?)null : CellsA > CellsB ? PlayerSide.A : PlayerSide.B;
        public string NameKey => "terrain." + Terrain.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Totals, boundaries and terrain labels for the land screen (ticket 41). Regions are 4-connected
    /// components of each special terrain type; plain land is not labelled. Labels anchor on a cell
    /// inside the region, so a label never points at the wrong terrain.
    /// </summary>
    public static class BoardLabels
    {
        /// <summary>Regions smaller than this are not labelled (they still render).</summary>
        public const int MinLabelledCells = 40;

        public static List<TerrainLabel> TerrainRegions(Territory territory)
        {
            if (territory == null) throw new ArgumentNullException(nameof(territory));
            var labels = new List<TerrainLabel>();
            var seen = new bool[Board.GridCellCount];
            var queue = new Queue<int>();
            var members = new List<int>();
            foreach (int start in Board.ActiveCellIds)
            {
                if (seen[start]) continue;
                TerrainType t = territory.TerrainAt(start);
                seen[start] = true;
                if (t == TerrainType.Plain) continue;
                members.Clear();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int id = queue.Dequeue();
                    members.Add(id);
                    int x = Board.X(id), y = Board.Y(id);
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + Board.NeighbourDx[k], ny = y + Board.NeighbourDy[k];
                        if (!Board.IsActive(nx, ny)) continue;
                        int nid = Board.CellId(nx, ny);
                        if (seen[nid] || territory.TerrainAt(nid) != t) continue;
                        seen[nid] = true;
                        queue.Enqueue(nid);
                    }
                }
                if (members.Count < MinLabelledCells) continue;
                long sx = 0, sy = 0;
                int a = 0;
                foreach (int id in members)
                {
                    sx += Board.X(id);
                    sy += Board.Y(id);
                    if (territory.IsOwnedBy(id, PlayerSide.A)) a++;
                }
                double mx = (double)sx / members.Count, my = (double)sy / members.Count;
                int best = members[0];
                double bestD = double.MaxValue;
                foreach (int id in members)
                {
                    double d = (Board.X(id) - mx) * (Board.X(id) - mx) + (Board.Y(id) - my) * (Board.Y(id) - my);
                    if (d < bestD) { bestD = d; best = id; }
                }
                labels.Add(new TerrainLabel { Terrain = t, AnchorCellId = best, Cells = members.Count, CellsA = a, CellsB = members.Count - a });
            }
            return labels;
        }
    }
}
