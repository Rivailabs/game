using System;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Presentation
{
    /// <summary>
    /// Element identity by SHAPE, not colour alone (plan: "Art and sound direction"): Agni a flame,
    /// Vayu a spiral, Prithvi a stone, Vidyut a bolt, Varuna a wave; Neutral (Pass) a plain ring.
    /// Produces square coverage masks that the Unity layer turns into icon textures; the same
    /// symbols are used on weapon buttons, explanations and the HUD.
    /// </summary>
    public static class ElementGlyphs
    {
        /// <summary>Row-major mask, rows bottom-up (texture order), true = ink.</summary>
        public static bool[] Rasterize(Element element, int size)
        {
            if (size < 8) throw new ArgumentOutOfRangeException(nameof(size));
            var mask = new bool[size * size];
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    // Pixel centre in [-1, 1], y up.
                    double x = (px + 0.5) / size * 2 - 1;
                    double y = (py + 0.5) / size * 2 - 1;
                    mask[py * size + px] = Inside(element, x, y);
                }
            }
            return mask;
        }

        public static bool Inside(Element element, double x, double y)
        {
            switch (element)
            {
                case Element.Agni: return Flame(x, y);
                case Element.Vayu: return Spiral(x, y);
                case Element.Prithvi: return Stone(x, y);
                case Element.Vidyut: return Bolt(x, y);
                case Element.Varuna: return Wave(x, y);
                default: return Ring(x, y);
            }
        }

        private static readonly double[] FlameOuter = { 0.0, 0.92, 0.38, 0.2, 0.62, -0.28, 0.52, -0.62, 0.0, -0.88, -0.52, -0.62, -0.62, -0.28, -0.38, 0.2 };
        private static readonly double[] StoneShape = { -0.78, -0.5, -0.3, -0.82, 0.48, -0.74, 0.86, -0.18, 0.62, 0.52, 0.04, 0.8, -0.62, 0.42, -0.9, -0.06 };
        private static readonly double[] BoltShape = { 0.18, 0.95, -0.52, 0.02, -0.06, 0.02, -0.3, -0.95, 0.52, 0.12, 0.06, 0.12, 0.42, 0.95 };

        private static bool Flame(double x, double y)
        {
            // Teardrop flame with an inner cut-out tongue.
            if (!InPolygon(FlameOuter, x, y)) return false;
            return !(y > -0.55 && y < 0.05 && Math.Abs(x) < 0.16 * (1 - (y + 0.55) / 0.6));
        }

        private static bool Spiral(double x, double y)
        {
            double r = Math.Sqrt(x * x + y * y);
            if (r > 0.95 || r < 0.06) return false;
            double theta = Math.Atan2(y, x);
            if (theta < 0) theta += 2 * Math.PI;
            const double a = 0.32 / (2 * Math.PI); // radial growth per radian: 0.32 per turn
            for (int k = 0; k < 4; k++)
            {
                double rk = a * (theta + 2 * Math.PI * k) + 0.05;
                if (Math.Abs(r - rk) < 0.08) return true;
            }
            return false;
        }

        private static bool Stone(double x, double y) => InPolygon(StoneShape, x, y) && !(y > 0.15 && y < 0.25 && x > -0.3 && x < 0.25);

        private static bool Bolt(double x, double y) => InPolygon(BoltShape, x, y);

        private static bool Wave(double x, double y)
        {
            if (Math.Abs(x) > 0.92) return false;
            for (int band = 0; band < 3; band++)
            {
                double centre = 0.42 - band * 0.42 + 0.16 * Math.Sin(Math.PI * 1.5 * (x + 1));
                if (Math.Abs(y - centre) < 0.09) return true;
            }
            return false;
        }

        private static bool Ring(double x, double y)
        {
            double r = Math.Sqrt(x * x + y * y);
            return r > 0.6 && r < 0.85;
        }

        /// <summary>Even-odd point-in-polygon on interleaved x,y pairs.</summary>
        private static bool InPolygon(double[] xy, double x, double y)
        {
            bool inside = false;
            int n = xy.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = xy[2 * i], yi = xy[2 * i + 1], xj = xy[2 * j], yj = xy[2 * j + 1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        public static int InkCount(bool[] mask)
        {
            int n = 0;
            foreach (bool b in mask) if (b) n++;
            return n;
        }
    }
}
