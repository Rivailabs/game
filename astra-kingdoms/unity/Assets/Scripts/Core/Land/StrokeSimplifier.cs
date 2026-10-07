using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Client.Land
{
    /// <summary>
    /// Client-side stroke reduction. The rules reject arrays longer than 128 vertices and never
    /// resample them, so the client simplifies a long finger stroke itself and shows the simplified
    /// polygon and its exact preview while drawing (plan: "Cut validation and transfer").
    /// <para>
    /// Steps: drop consecutive duplicates and a repeated closing vertex; if more than
    /// <c>maxVertices</c> remain, run Ramer-Douglas-Peucker with the smallest tolerance (binary
    /// searched) that fits; as a last resort keep evenly spaced vertices. The output never exceeds
    /// <c>maxVertices</c>. It can still be an illegal polygon (for example self-intersecting);
    /// the rules' preview then reports the specific reason.
    /// </para>
    /// </summary>
    public static class StrokeSimplifier
    {
        public static List<CellPoint> Simplify(IReadOnlyList<CellPoint> stroke, int maxVertices = RulesConstants.MaxCutVertices)
        {
            if (maxVertices < 3) throw new ArgumentOutOfRangeException(nameof(maxVertices));
            var pts = Dedupe(stroke);
            if (pts.Count <= maxVertices) return pts;

            double lo = 0, hi = MaxSpan(pts);
            List<CellPoint> best = null;
            for (int iter = 0; iter < 24; iter++)
            {
                double eps = (lo + hi) / 2;
                List<CellPoint> candidate = Rdp(pts, eps);
                if (candidate.Count <= maxVertices)
                {
                    best = candidate;
                    hi = eps;
                }
                else
                {
                    lo = eps;
                }
            }
            if (best == null || best.Count > maxVertices) best = Decimate(pts, maxVertices);
            return best;
        }

        /// <summary>Removes consecutive duplicates and a closing vertex equal to the first.</summary>
        public static List<CellPoint> Dedupe(IReadOnlyList<CellPoint> stroke)
        {
            var list = new List<CellPoint>();
            if (stroke == null) return list;
            foreach (CellPoint p in stroke)
                if (list.Count == 0 || list[list.Count - 1] != p) list.Add(p);
            while (list.Count > 1 && list[list.Count - 1] == list[0]) list.RemoveAt(list.Count - 1);
            return list;
        }

        private static double MaxSpan(List<CellPoint> pts)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (CellPoint p in pts)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            return Math.Sqrt((double)(maxX - minX) * (maxX - minX) + (double)(maxY - minY) * (maxY - minY)) + 1;
        }

        private static List<CellPoint> Rdp(List<CellPoint> pts, double eps)
        {
            var keep = new bool[pts.Count];
            keep[0] = true;
            keep[pts.Count - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, pts.Count - 1));
            while (stack.Count > 0)
            {
                (int a, int b) = stack.Pop();
                if (b <= a + 1) continue;
                double maxD = -1;
                int idx = -1;
                for (int i = a + 1; i < b; i++)
                {
                    double d = DistanceToSegment(pts[i], pts[a], pts[b]);
                    if (d > maxD)
                    {
                        maxD = d;
                        idx = i;
                    }
                }
                if (maxD > eps)
                {
                    keep[idx] = true;
                    stack.Push((a, idx));
                    stack.Push((idx, b));
                }
            }
            var result = new List<CellPoint>();
            for (int i = 0; i < pts.Count; i++)
                if (keep[i]) result.Add(pts[i]);
            return result;
        }

        private static List<CellPoint> Decimate(List<CellPoint> pts, int max)
        {
            var result = new List<CellPoint>(max);
            for (int i = 0; i < max; i++)
            {
                CellPoint p = pts[(int)((long)i * (pts.Count - 1) / (max - 1))];
                if (result.Count == 0 || result[result.Count - 1] != p) result.Add(p);
            }
            return result;
        }

        private static double DistanceToSegment(CellPoint p, CellPoint a, CellPoint b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 == 0 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
            double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
            return Math.Sqrt(ex * ex + ey * ey);
        }
    }
}
