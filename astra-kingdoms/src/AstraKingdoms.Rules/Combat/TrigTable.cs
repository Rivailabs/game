using System;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>
    /// Versioned integer sine/cosine lookup in 0.25 degree steps ("qdeg"). The committed table
    /// (<c>TrigTable.Generated.cs</c>, produced by <c>tools/gen_trig_table.py</c>) stores
    /// sin(0..90 deg); every other value in −90..+90 deg is derived by exact symmetry, so all
    /// executors produce bit-identical launch vectors. The runtime never calls Math.Sin/Cos.
    /// </summary>
    public static partial class TrigTable
    {
        /// <summary>Smallest supported angle in quarter degrees (−90 deg).</summary>
        public const int MinQdeg = -360;
        /// <summary>Largest supported angle in quarter degrees (+90 deg).</summary>
        public const int MaxQdeg = 360;

        /// <summary>sin(qdeg x 0.25 deg) as Q32.32.</summary>
        public static Fixed Sin(int qdeg)
        {
            Check(qdeg);
            long raw = SinQuarterDegreeRaw[qdeg < 0 ? -qdeg : qdeg];
            return Fixed.FromRaw(qdeg < 0 ? -raw : raw);
        }

        /// <summary>cos(qdeg x 0.25 deg) as Q32.32, via cos(a) = sin(90 deg − |a|).</summary>
        public static Fixed Cos(int qdeg)
        {
            Check(qdeg);
            return Fixed.FromRaw(SinQuarterDegreeRaw[MaxQdeg - (qdeg < 0 ? -qdeg : qdeg)]);
        }

        /// <summary>Number of stored entries (0..90 deg inclusive).</summary>
        public static int EntryCount => SinQuarterDegreeRaw.Length;

        /// <summary>
        /// The exact table bytes for inclusion in the rules hash: each entry as a little-endian
        /// signed 64-bit integer in ascending index order. SHA-256 of this equals <see cref="Sha256Hex"/>.
        /// </summary>
        public static byte[] GetTableBytes()
        {
            var bytes = new byte[SinQuarterDegreeRaw.Length * 8];
            for (int i = 0; i < SinQuarterDegreeRaw.Length; i++)
            {
                ulong v = unchecked((ulong)SinQuarterDegreeRaw[i]);
                for (int b = 0; b < 8; b++) bytes[i * 8 + b] = (byte)(v >> (8 * b));
            }
            return bytes;
        }

        private static void Check(int qdeg)
        {
            if (qdeg < MinQdeg || qdeg > MaxQdeg)
                throw new ArgumentOutOfRangeException(nameof(qdeg), "Angle outside the -90..+90 degree lookup range: " + qdeg + " qdeg.");
        }
    }
}
