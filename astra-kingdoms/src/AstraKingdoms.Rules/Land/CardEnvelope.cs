using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Land
{
    /// <summary>
    /// Placement of a card envelope on the board: centre on a cell centre, uniform scale in
    /// quarter-cell units (1-1024, i.e. 0.25-256 cells per local unit) and one of sixteen 22.5°
    /// rotations.
    /// </summary>
    public readonly struct CardPose : IEquatable<CardPose>
    {
        public readonly int CenterX;
        public readonly int CenterY;
        public readonly int ScaleQuarters;
        public readonly int Rotation;

        public CardPose(int centerX, int centerY, int scaleQuarters, int rotation)
        {
            CenterX = centerX;
            CenterY = centerY;
            ScaleQuarters = scaleQuarters;
            Rotation = rotation;
        }

        public CellPoint Center => new CellPoint(CenterX, CenterY);

        /// <summary>Centre on the 256x256 grid, scale 1-1024 and rotation 0-15.</summary>
        public bool IsInRange =>
            Board.IsOnGrid(CenterX, CenterY) &&
            ScaleQuarters >= RulesConstants.MinScaleQuarters && ScaleQuarters <= RulesConstants.MaxScaleQuarters &&
            Rotation >= 0 && Rotation < RulesConstants.RotationSteps;

        public bool Equals(CardPose o) =>
            CenterX == o.CenterX && CenterY == o.CenterY && ScaleQuarters == o.ScaleQuarters && Rotation == o.Rotation;
        public override bool Equals(object obj) => obj is CardPose p && Equals(p);
        public override int GetHashCode() => ((CenterX * 397 ^ CenterY) * 397 ^ ScaleQuarters) * 397 ^ Rotation;
        public override string ToString() => "centre (" + CenterX + "," + CenterY + ") scale " + ScaleQuarters + "/4 rot " + Rotation;
    }

    /// <summary>
    /// Card envelopes as exact integer membership tests.
    /// <para>
    /// <b>Canonical shapes.</b> Each card is defined in local coordinates (u,v). Every vertex,
    /// radius and centre in the plan is a multiple of 0.05, so the shapes are stored in
    /// "twentieths" (u20 = 20u):
    /// Chakra disk radius 20; Garuda |u20| + 2|v20| &lt;= 40; Suchi triangle (-10,-10),(40,0),(-10,10);
    /// Makara L = rectangles [-20,30]x[-10,10] U [10,30]x[10,30]; Padma five radius-11 disks at
    /// (0,0),(±9,0),(0,±9); Vajra |u20|+|v20| &lt;= 20 U (|u20| &lt;= 36 and |v20| &lt;= 5).
    /// </para>
    /// <para>
    /// <b>Pose.</b> Rotation r (θ = r × 22.5°) maps local to board offsets as
    /// x = u·cosθ − v·sinθ, y = u·sinθ + v·cosθ. Rotation 0 maps u to +x and v to +y; because the
    /// displayed map is y-down, increasing r turns the shape clockwise on screen. Scale s =
    /// ScaleQuarters / 4 cells per local unit. A cell (x,y) belongs to the envelope when its centre
    /// offset (dx,dy) from the pose centre, rotated back and divided by s, lies in the closed shape.
    /// </para>
    /// <para>
    /// <b>Fixed point.</b> cosθ and sinθ come from <see cref="CosTable"/>/<see cref="SinTable"/>,
    /// scaled by K = 2^30. With U = dx·C + dy·S and V = −dx·S + dy·C (both K × the back-rotated
    /// offset), the local point in twentieths is (80U, 80V) / (K·q), q = ScaleQuarters. All shape
    /// inequalities are multiplied through by the positive denominator W = K·q and evaluated with
    /// exact 64-bit (linear tests) or 128-bit (disk tests) integer arithmetic, boundary inclusive.
    /// No floating point is used.
    /// </para>
    /// </summary>
    public static class CardEnvelope
    {
        /// <summary>Fixed-point scale of the rotation table (2^30).</summary>
        public const long TrigScale = 1L << 30;

        /// <summary>Version tag of the rotation table; part of the rules bundle.</summary>
        public const string TrigTableVersion = "AK-TR-1/rot16-q30-v1";

        // round(cos(i*pi/8) * 2^30) and round(sin(i*pi/8) * 2^30) for i = 0..15, generated offline
        // with Python: [round(math.cos(i*math.pi/8)*2**30) for i in range(16)] (and math.sin).
        // The fractional parts of the unrounded values are far from 0.5 (e.g. 992008094.3949,
        // 410903206.6823, 759250124.9940), so any correct double or higher-precision evaluation
        // yields the same integers. The table is exactly quarter-turn symmetric:
        // Cos[(i+4)%16] == -Sin[i] and Sin[(i+4)%16] == Cos[i] (checked by tests).
        public static readonly IReadOnlyList<long> CosTable = new long[]
        {
            1073741824, 992008094, 759250125, 410903207, 0, -410903207, -759250125, -992008094,
            -1073741824, -992008094, -759250125, -410903207, 0, 410903207, 759250125, 992008094,
        };

        public static readonly IReadOnlyList<long> SinTable = new long[]
        {
            0, 410903207, 759250125, 992008094, 1073741824, 992008094, 759250125, 410903207,
            0, -410903207, -759250125, -992008094, -1073741824, -992008094, -759250125, -410903207,
        };

        /// <summary>Ceil of each shape's maximum distance from the local origin, in twentieths.</summary>
        private static int BoundingRadiusTwentieths(CardId card)
        {
            switch (card)
            {
                case CardId.Chakra: return 20;
                case CardId.Garuda: return 40;
                case CardId.Suchi: return 40;
                case CardId.Makara: return 43; // corner (30,30): 42.43
                case CardId.Padma: return 20;  // 9 + 11
                case CardId.Vajra: return 37;  // corner (36,5): 36.35
                default: throw new ArgumentOutOfRangeException(nameof(card));
            }
        }

        /// <summary>
        /// Conservative half-width (in cells) of a square around the centre that contains every
        /// envelope cell for the given scale. One cell of slack absorbs the table's rounding.
        /// </summary>
        public static int BoundingHalfWidth(CardId card, int scaleQuarters)
        {
            int r = BoundingRadiusTwentieths(card);
            return (r * scaleQuarters + 79) / 80 + 1;
        }

        /// <summary>True when the offset (dx,dy) from the envelope centre is inside the closed envelope.</summary>
        public static bool ContainsOffset(CardId card, int scaleQuarters, int rotation, int dx, int dy)
        {
            CheckScaleAndRotation(scaleQuarters, rotation);
            return Test(card, CosTable[rotation], SinTable[rotation], TrigScale * scaleQuarters, dx, dy);
        }

        /// <summary>True when the centre of board cell (x,y) lies inside the posed envelope (no board clipping).</summary>
        public static bool Contains(CardId card, CardPose pose, int x, int y)
        {
            CheckPose(pose);
            return Test(card, CosTable[pose.Rotation], SinTable[pose.Rotation], TrigScale * pose.ScaleQuarters,
                x - pose.CenterX, y - pose.CenterY);
        }

        public static bool Contains(CardId card, CardPose pose, int cellId) =>
            Contains(card, pose, Board.X(cellId), Board.Y(cellId));

        /// <summary>Active board cells whose centres lie inside the posed envelope, in ascending cell ID.</summary>
        public static List<int> RasterizeOnBoard(CardId card, CardPose pose)
        {
            CheckPose(pose);
            long c = CosTable[pose.Rotation];
            long s = SinTable[pose.Rotation];
            long w = TrigScale * pose.ScaleQuarters;
            int half = BoundingHalfWidth(card, pose.ScaleQuarters);
            int x0 = Math.Max(0, pose.CenterX - half);
            int x1 = Math.Min(Board.Size - 1, pose.CenterX + half);
            int y0 = Math.Max(0, pose.CenterY - half);
            int y1 = Math.Min(Board.Size - 1, pose.CenterY + half);

            var cells = new List<int>();
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!Board.IsActive(x, y)) continue;
                    if (Test(card, c, s, w, x - pose.CenterX, y - pose.CenterY)) cells.Add(Board.CellId(x, y));
                }
            }
            return cells;
        }

        private static readonly Dictionary<int, int> CountCache = new Dictionary<int, int>();

        /// <summary>
        /// Number of integer lattice offsets inside the canonical envelope at this scale and
        /// rotation, before board clipping. This is the quantity limited to 2Q.
        /// </summary>
        public static int CountCanonicalCells(CardId card, int scaleQuarters, int rotation)
        {
            CheckScaleAndRotation(scaleQuarters, rotation);
            int key = CacheKey(card, scaleQuarters, rotation);
            lock (CountCache)
            {
                if (CountCache.TryGetValue(key, out int cached)) return cached;
            }
            int count = CountUpTo(card, scaleQuarters, rotation, int.MaxValue);
            lock (CountCache)
            {
                CountCache[key] = count;
            }
            return count;
        }

        /// <summary>
        /// The size constraint: the canonical rasterized envelope (before clipping) may contain at
        /// most 2Q cells. Stops counting as soon as the limit is exceeded.
        /// </summary>
        public static bool FitsAllowance(CardId card, int scaleQuarters, int rotation, int quota)
        {
            if (quota <= 0) return false;
            CheckScaleAndRotation(scaleQuarters, rotation);
            long limit = 2L * quota;
            int key = CacheKey(card, scaleQuarters, rotation);
            lock (CountCache)
            {
                if (CountCache.TryGetValue(key, out int cached)) return cached <= limit;
            }
            int count = CountUpTo(card, scaleQuarters, rotation, limit);
            if (count > limit) return false;
            lock (CountCache)
            {
                CountCache[key] = count; // exact: counting completed below the limit
            }
            return true;
        }

        /// <summary>True when the pose fields are in range (centre on grid, scale 1-1024, rotation 0-15).</summary>
        public static bool IsValidPose(CardPose pose) => pose.IsInRange;

        private static int CountUpTo(CardId card, int scaleQuarters, int rotation, long limit)
        {
            long c = CosTable[rotation];
            long s = SinTable[rotation];
            long w = TrigScale * scaleQuarters;
            int half = BoundingHalfWidth(card, scaleQuarters);
            int count = 0;
            for (int dy = -half; dy <= half; dy++)
            {
                for (int dx = -half; dx <= half; dx++)
                {
                    if (!Test(card, c, s, w, dx, dy)) continue;
                    count++;
                    if (count > limit) return count;
                }
            }
            return count;
        }

        private static int CacheKey(CardId card, int scaleQuarters, int rotation) =>
            ((int)card << 16) | (scaleQuarters << 4) | rotation;

        private static void CheckScaleAndRotation(int scaleQuarters, int rotation)
        {
            if (scaleQuarters < RulesConstants.MinScaleQuarters || scaleQuarters > RulesConstants.MaxScaleQuarters)
                throw new ArgumentOutOfRangeException(nameof(scaleQuarters));
            if (rotation < 0 || rotation >= RulesConstants.RotationSteps)
                throw new ArgumentOutOfRangeException(nameof(rotation));
        }

        private static void CheckPose(CardPose pose)
        {
            if (!pose.IsInRange) throw new ArgumentOutOfRangeException(nameof(pose), "Pose out of range: " + pose);
        }

        /// <summary>
        /// Core membership test. (p, r) / w is the local point in twentieths, with w = K·q &gt; 0.
        /// Magnitudes: |dx|,|dy| &lt;= ~600, so |U|,|V| &lt; 2^40, |p|,|r| &lt; 2^47, w &lt;= 2^40.
        /// </summary>
        private static bool Test(CardId card, long c, long s, long w, long dx, long dy)
        {
            long p = 80 * (dx * c + dy * s);
            long r = 80 * (-dx * s + dy * c);
            switch (card)
            {
                case CardId.Chakra:
                    return WideMath.SumOfSquaresAtMost(p, r, 20 * w);
                case CardId.Garuda:
                    return Math.Abs(p) + 2 * Math.Abs(r) <= 40 * w;
                case CardId.Suchi:
                    // Counter-clockwise (positive area) vertex order; inside when on the left of every edge.
                    return LeftOrOn(-10, -10, 40, 0, p, r, w)
                        && LeftOrOn(40, 0, -10, 10, p, r, w)
                        && LeftOrOn(-10, 10, -10, -10, p, r, w);
                case CardId.Makara:
                    return InRect(p, r, w, -20, 30, -10, 10) || InRect(p, r, w, 10, 30, 10, 30);
                case CardId.Padma:
                    return WideMath.SumOfSquaresAtMost(p, r, 11 * w)
                        || WideMath.SumOfSquaresAtMost(p - 9 * w, r, 11 * w)
                        || WideMath.SumOfSquaresAtMost(p + 9 * w, r, 11 * w)
                        || WideMath.SumOfSquaresAtMost(p, r - 9 * w, 11 * w)
                        || WideMath.SumOfSquaresAtMost(p, r + 9 * w, 11 * w);
                case CardId.Vajra:
                    return Math.Abs(p) + Math.Abs(r) <= 20 * w
                        || (Math.Abs(p) <= 36 * w && Math.Abs(r) <= 5 * w);
                default:
                    throw new ArgumentOutOfRangeException(nameof(card));
            }
        }

        /// <summary>
        /// Cross((b - a), (pt - a)) &gt;= 0 with pt = (p, r) / w and integer a, b in twentieths,
        /// multiplied through by w &gt; 0. |b - a| &lt;= 50 and |p - a·w| &lt; 2^48, so the products fit.
        /// </summary>
        private static bool LeftOrOn(long ax, long ay, long bx, long by, long p, long r, long w)
        {
            return (bx - ax) * (r - ay * w) - (by - ay) * (p - ax * w) >= 0;
        }

        private static bool InRect(long p, long r, long w, long uMin, long uMax, long vMin, long vMax)
        {
            return p >= uMin * w && p <= uMax * w && r >= vMin * w && r <= vMax * w;
        }
    }

    /// <summary>Minimal exact 128-bit helpers for the disk tests (netstandard2.1 has no Int128).</summary>
    internal static class WideMath
    {
        /// <summary>Exact a² + b² &lt;= c² for |a|,|b|,|c| &lt; 2^62.</summary>
        public static bool SumOfSquaresAtMost(long a, long b, long c)
        {
            Square(a, out ulong ah, out ulong al);
            Square(b, out ulong bh, out ulong bl);
            Square(c, out ulong ch, out ulong cl);
            ulong sl = unchecked(al + bl);
            ulong sh = ah + bh + (sl < al ? 1UL : 0UL);
            return sh < ch || (sh == ch && sl <= cl);
        }

        private static void Square(long value, out ulong hi, out ulong lo)
        {
            ulong x = (ulong)(value < 0 ? -value : value);
            Multiply(x, x, out hi, out lo);
        }

        /// <summary>Full 64x64 -> 128-bit unsigned product.</summary>
        public static void Multiply(ulong x, ulong y, out ulong hi, out ulong lo)
        {
            ulong xl = (uint)x, xh = x >> 32, yl = (uint)y, yh = y >> 32;
            ulong ll = xl * yl;
            ulong lh = xl * yh;
            ulong hl = xh * yl;
            ulong hh = xh * yh;
            ulong mid = (ll >> 32) + (uint)lh + (uint)hl;
            lo = (mid << 32) | (uint)ll;
            hi = hh + (lh >> 32) + (hl >> 32) + (mid >> 32);
        }
    }
}
