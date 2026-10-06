using System;

namespace AstraKingdoms.Client.Presentation
{
    /// <summary>
    /// Minimal double-precision vector for engine-independent presentation math (bowstring, camera,
    /// arena layout checks). Presentation only: authoritative combat uses the rules' fixed point.
    /// </summary>
    public readonly struct V3 : IEquatable<V3>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public V3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly V3 Zero = new V3(0, 0, 0);
        public static readonly V3 Up = new V3(0, 1, 0);
        public static readonly V3 Forward = new V3(0, 0, 1);
        public static readonly V3 Right = new V3(1, 0, 0);

        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator -(V3 a) => new V3(-a.X, -a.Y, -a.Z);
        public static V3 operator *(V3 a, double s) => new V3(a.X * s, a.Y * s, a.Z * s);
        public static V3 operator *(double s, V3 a) => a * s;

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public V3 Normalized
        {
            get
            {
                double l = Length;
                return l < 1e-12 ? Zero : this * (1.0 / l);
            }
        }

        public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static V3 Cross(V3 a, V3 b) => new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static double Distance(V3 a, V3 b) => (a - b).Length;

        public static V3 Lerp(V3 a, V3 b, double t) => a + (b - a) * t;

        /// <summary>Distance from <paramref name="p"/> to the infinite line through <paramref name="a"/> and <paramref name="b"/>.</summary>
        public static double DistanceToLine(V3 p, V3 a, V3 b)
        {
            V3 d = (b - a).Normalized;
            V3 ap = p - a;
            return (ap - d * Dot(ap, d)).Length;
        }

        public bool Equals(V3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is V3 v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2);
        public override string ToString() => "(" + X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                                             Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                                             Z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }
}
