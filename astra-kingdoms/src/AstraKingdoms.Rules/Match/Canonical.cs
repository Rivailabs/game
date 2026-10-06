using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AstraKingdoms.Rules.Match
{
    /// <summary>Lowercase hexadecimal helpers (netstandard2.1 has no Convert.ToHexString).</summary>
    public static class Hex
    {
        private const string Digits = "0123456789abcdef";

        public static string Encode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[2 * i] = Digits[bytes[i] >> 4];
                chars[2 * i + 1] = Digits[bytes[i] & 0xF];
            }
            return new string(chars);
        }

        /// <summary>Decodes lowercase or uppercase hex. Throws <see cref="FormatException"/> on bad input.</summary>
        public static byte[] Decode(string hex)
        {
            if (hex == null) throw new ArgumentNullException(nameof(hex));
            if (hex.Length % 2 != 0) throw new FormatException("Hex text must have an even length.");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)((Nibble(hex[2 * i]) << 4) | Nibble(hex[2 * i + 1]));
            return bytes;
        }

        private static int Nibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            throw new FormatException("Invalid hex digit '" + c + "'.");
        }
    }

    /// <summary>
    /// Append-only canonical binary encoder used for hashes: big-endian fixed-width integers and
    /// u32-length-prefixed ASCII strings / byte blocks. The same values always give the same bytes.
    /// </summary>
    public sealed class CanonicalWriter
    {
        private readonly List<byte> _bytes = new List<byte>(4096);

        public int Length => _bytes.Count;

        public CanonicalWriter U8(int value)
        {
            _bytes.Add((byte)value);
            return this;
        }

        public CanonicalWriter Bool(bool value) => U8(value ? 1 : 0);

        public CanonicalWriter U32(uint value)
        {
            for (int s = 24; s >= 0; s -= 8) _bytes.Add((byte)(value >> s));
            return this;
        }

        public CanonicalWriter I32(int value) => U32(unchecked((uint)value));

        public CanonicalWriter U64(ulong value)
        {
            for (int s = 56; s >= 0; s -= 8) _bytes.Add((byte)(value >> s));
            return this;
        }

        public CanonicalWriter I64(long value) => U64(unchecked((ulong)value));

        public CanonicalWriter Ascii(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            foreach (char c in text)
                if (c > 0x7F) throw new ArgumentException("Canonical strings must be ASCII.", nameof(text));
            U32((uint)text.Length);
            _bytes.AddRange(Encoding.ASCII.GetBytes(text));
            return this;
        }

        public CanonicalWriter Block(byte[] block)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            U32((uint)block.Length);
            _bytes.AddRange(block);
            return this;
        }

        /// <summary>A named signed integer: the name (length-prefixed) followed by an int64.</summary>
        public CanonicalWriter Named(string name, long value) => Ascii(name).I64(value);

        public byte[] ToArray() => _bytes.ToArray();

        public byte[] Sha256() => Hashing.Sha256(ToArray());
    }

    internal static class Hashing
    {
        public static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        public static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
