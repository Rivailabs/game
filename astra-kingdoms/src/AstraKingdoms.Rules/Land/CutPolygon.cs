using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Land
{
    /// <summary>
    /// A normalized, validated cut polygon in integer cell-centre coordinates, plus the exact
    /// geometric predicates the rules use. All arithmetic is on small integers (coordinates 0-255).
    /// </summary>
    public sealed class CutPolygon
    {
        private readonly CellPoint[] _v;

        /// <summary>Distinct-consecutive vertices of the closed path (closing edge implied).</summary>
        public IReadOnlyList<CellPoint> Vertices => _v;

        private CutPolygon(CellPoint[] vertices)
        {
            _v = vertices;
        }

        /// <summary>
        /// Snaps a pointer coordinate to the nearest cell-centre index. The coordinate is
        /// <paramref name="numerator"/> / <paramref name="denominator"/> in cell-centre space, where
        /// cell index i has its centre at exactly i (a client whose origin is a cell corner subtracts
        /// half a cell first). Exact half ties choose the lower integer; the result is clamped to 0-255.
        /// </summary>
        public static int SnapToCellIndex(long numerator, long denominator)
        {
            if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
            // Nearest integer with ties down = ceil(p - 1/2) = ceil((2n - d) / 2d).
            long index = CeilDiv(checked(2 * numerator - denominator), checked(2 * denominator));
            if (index < 0) return 0;
            if (index > Board.Size - 1) return Board.Size - 1;
            return (int)index;
        }

        /// <summary>Snaps a fixed-point pointer position (shared denominator) to a cell-centre vertex.</summary>
        public static CellPoint SnapPoint(long xNumerator, long yNumerator, long denominator) =>
            new CellPoint(SnapToCellIndex(xNumerator, denominator), SnapToCellIndex(yNumerator, denominator));

        /// <summary>
        /// Normalizes and validates a submitted vertex array: at most 128 entries, every vertex on
        /// the grid, consecutive duplicates and one optional repeated closing vertex removed, at least
        /// three distinct vertices, nonzero area, no nonadjacent crossings or touches and no
        /// overlapping edges. Returns <see cref="CutRejection.None"/> and the polygon when valid.
        /// </summary>
        public static CutRejection TryCreate(IReadOnlyList<CellPoint> submitted, out CutPolygon polygon)
        {
            polygon = null;
            if (submitted == null || submitted.Count == 0) return CutRejection.TooFewVertices;
            if (submitted.Count > RulesConstants.MaxCutVertices) return CutRejection.TooManyVertices;

            var list = new List<CellPoint>(submitted.Count);
            foreach (CellPoint p in submitted)
            {
                if (!p.IsOnGrid) return CutRejection.VertexOutOfRange;
                if (list.Count == 0 || list[list.Count - 1] != p) list.Add(p);
            }
            if (list.Count > 1 && list[list.Count - 1] == list[0]) list.RemoveAt(list.Count - 1);

            var distinct = new HashSet<CellPoint>(list);
            if (distinct.Count < 3) return CutRejection.TooFewVertices;

            CellPoint[] v = list.ToArray();
            // A path whose vertices are all collinear encloses nothing; report that before its
            // (inevitable) overlapping edges.
            if (AllCollinear(v)) return CutRejection.ZeroArea;

            CutRejection edges = CheckEdges(v);
            if (edges != CutRejection.None) return edges;

            // A simple polygon with three non-collinear vertices always has positive area; kept as
            // a defensive check so a zero-area path can never be accepted.
            if (TwiceSignedArea(v) == 0) return CutRejection.ZeroArea;

            polygon = new CutPolygon(v);
            return CutRejection.None;
        }

        /// <summary>
        /// Even-odd inclusion of the point (x,y), with points exactly on an edge (or vertex) included.
        /// </summary>
        public bool Contains(int x, int y)
        {
            int n = _v.Length;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                CellPoint a = _v[j];
                CellPoint b = _v[i];
                if (OnSegment(a, b, x, y)) return true;
                // Half-open rule on y so a vertex is counted once per crossing.
                if ((a.Y > y) != (b.Y > y))
                {
                    // Is x strictly left of the edge's intersection with the horizontal line at y?
                    // x < a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y), multiplied through by (b.Y - a.Y).
                    long lhs = (long)(x - a.X) * (b.Y - a.Y);
                    long rhs = (long)(y - a.Y) * (b.X - a.X);
                    bool left = b.Y > a.Y ? lhs < rhs : lhs > rhs;
                    if (left) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>Inclusive bounding box of the vertices.</summary>
        public void GetBounds(out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = minY = int.MaxValue;
            maxX = maxY = int.MinValue;
            foreach (CellPoint p in _v)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        private static bool AllCollinear(CellPoint[] v)
        {
            for (int i = 2; i < v.Length; i++)
            {
                if (Cross(v[0], v[1], v[i]) != 0) return false;
            }
            return true;
        }

        private static long TwiceSignedArea(CellPoint[] v)
        {
            long sum = 0;
            for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
            {
                sum += (long)v[j].X * v[i].Y - (long)v[i].X * v[j].Y;
            }
            return sum;
        }

        private static CutRejection CheckEdges(CellPoint[] v)
        {
            int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                CellPoint a = v[i];
                CellPoint b = v[(i + 1) % n];
                for (int j = i + 1; j < n; j++)
                {
                    CellPoint c = v[j];
                    CellPoint d = v[(j + 1) % n];
                    bool adjacentForward = j == i + 1;          // edge i ends where edge j starts
                    bool adjacentWrap = i == 0 && j == n - 1;   // edge j ends where edge i starts
                    if (adjacentForward || adjacentWrap)
                    {
                        // Two edges sharing one endpoint can only meet elsewhere by doubling back.
                        CellPoint prevStart = adjacentForward ? a : c;
                        CellPoint shared = adjacentForward ? b : a;
                        CellPoint nextEnd = adjacentForward ? d : b;
                        if (Cross(prevStart, shared, nextEnd) == 0 && Dot(prevStart, shared, nextEnd) < 0)
                            return CutRejection.OverlappingEdges;
                        continue;
                    }

                    if (!SegmentsIntersect(a, b, c, d)) continue;
                    bool collinear = Cross(a, b, c) == 0 && Cross(a, b, d) == 0;
                    if (collinear && CollinearOverlapHasLength(a, b, c, d)) return CutRejection.OverlappingEdges;
                    return CutRejection.SelfIntersecting;
                }
            }
            return CutRejection.None;
        }

        /// <summary>Cross product of (b - a) and (c - a).</summary>
        private static long Cross(CellPoint a, CellPoint b, CellPoint c) =>
            (long)(b.X - a.X) * (c.Y - a.Y) - (long)(b.Y - a.Y) * (c.X - a.X);

        /// <summary>Dot product of (b - a) and (c - b): negative when the path reverses at b.</summary>
        private static long Dot(CellPoint a, CellPoint b, CellPoint c) =>
            (long)(b.X - a.X) * (c.X - b.X) + (long)(b.Y - a.Y) * (c.Y - b.Y);

        private static bool OnSegment(CellPoint a, CellPoint b, int x, int y)
        {
            long cross = (long)(b.X - a.X) * (y - a.Y) - (long)(b.Y - a.Y) * (x - a.X);
            if (cross != 0) return false;
            return x >= Math.Min(a.X, b.X) && x <= Math.Max(a.X, b.X) && y >= Math.Min(a.Y, b.Y) && y <= Math.Max(a.Y, b.Y);
        }

        /// <summary>Closed-segment intersection test (touching counts).</summary>
        private static bool SegmentsIntersect(CellPoint a, CellPoint b, CellPoint c, CellPoint d)
        {
            long d1 = Cross(c, d, a);
            long d2 = Cross(c, d, b);
            long d3 = Cross(a, b, c);
            long d4 = Cross(a, b, d);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            if (d1 == 0 && OnSegment(c, d, a.X, a.Y)) return true;
            if (d2 == 0 && OnSegment(c, d, b.X, b.Y)) return true;
            if (d3 == 0 && OnSegment(a, b, c.X, c.Y)) return true;
            if (d4 == 0 && OnSegment(a, b, d.X, d.Y)) return true;
            return false;
        }

        /// <summary>For collinear segments: true when they share more than a single point.</summary>
        private static bool CollinearOverlapHasLength(CellPoint a, CellPoint b, CellPoint c, CellPoint d)
        {
            // Project onto the dominant axis of ab.
            bool useX = Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y);
            int a0 = useX ? a.X : a.Y, a1 = useX ? b.X : b.Y;
            int c0 = useX ? c.X : c.Y, c1 = useX ? d.X : d.Y;
            int lo = Math.Max(Math.Min(a0, a1), Math.Min(c0, c1));
            int hi = Math.Min(Math.Max(a0, a1), Math.Max(c0, c1));
            return hi > lo;
        }

        private static long FloorDiv(long a, long b)
        {
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        private static long CeilDiv(long a, long b) => -FloorDiv(-a, b);
    }
}
