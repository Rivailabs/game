using System;
using System.Security.Cryptography;
using System.Text;

namespace AstraKingdoms.V2.Common
{
    /// <summary>
    /// Small deterministic pseudo-random source (SplitMix64). Used for friend-code generation (with
    /// a server-held seed) and by the season rehearsal harness, never by match rules. System.Random
    /// is avoided because its sequence is not specified across runtimes.
    /// </summary>
    public sealed class DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(ulong seed) => _state = seed;

        /// <summary>A seed derived from text (SHA-256 of the UTF-8 bytes, first 8 bytes little-endian).</summary>
        public static DeterministicRandom FromText(string seedText)
        {
            byte[] h = StableHash.Sha256(seedText ?? string.Empty);
            ulong s = 0;
            for (int i = 7; i >= 0; i--) s = (s << 8) | h[i];
            return new DeterministicRandom(s);
        }

        public ulong NextULong()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform integer in [0, maxExclusive) without modulo bias.</summary>
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            ulong bound = (ulong)maxExclusive;
            ulong limit = ulong.MaxValue - (ulong.MaxValue % bound);
            ulong v;
            do v = NextULong(); while (v >= limit);
            return (int)(v % bound);
        }

        /// <summary>True with probability numerator/denominator.</summary>
        public bool Chance(int numerator, int denominator) => NextInt(denominator) < numerator;
    }

    /// <summary>SHA-256 helpers for stable, non-reversible identifiers (clip ids, data-map digests).</summary>
    public static class StableHash
    {
        public static byte[] Sha256(string text)
        {
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        public static string Sha256Hex(string text)
        {
            byte[] h = Sha256(text);
            var sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
