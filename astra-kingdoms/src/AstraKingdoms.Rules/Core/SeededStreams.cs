using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AstraKingdoms.Rules.Core
{
    /// <summary>
    /// The four frozen ASCII stream labels of AK-TR-1. Each label owns an independent sequence of
    /// SHA-256 candidates, so drawing from one stream never shifts another.
    /// </summary>
    public static class StreamLabels
    {
        public const string Initiative = "AK-TR-1/initiative";
        public const string Map = "AK-TR-1/map";
        public const string Terrain = "AK-TR-1/terrain";
        public const string Cards = "AK-TR-1/cards";

        /// <summary>Label of the match seed commitment (not a selection stream).</summary>
        public const string SeedCommitment = "AK-TR-1/seed";
    }

    /// <summary>
    /// One deterministic selection stream for a (label, seed, round) triple.
    /// <para>
    /// A candidate is SHA-256(ASCII label || 0x00 || seed[32] || round_u32_be || counter_u32_be);
    /// its first eight bytes are read as an unsigned big-endian integer. The counter starts at
    /// zero (or at a recorded value when resuming a replay) and increments after every digest,
    /// including digests rejected by the modulo-bias filter. Round is zero for initiative/map and
    /// 1-8 for terrain/cards.
    /// </para>
    /// <para>
    /// Uniform selection from a list of length k rejects candidates at or above
    /// floor(2^64 / k) * k and otherwise returns candidate % k. No runtime System.Random and no
    /// floating point are involved, so every platform produces identical choices.
    /// </para>
    /// </summary>
    public sealed class SeededStream
    {
        public const int SeedLength = 32;

        private readonly byte[] _prefix; // label || 0x00 || seed || round_be
        private uint _counter;

        public string Label { get; }
        public uint Round { get; }

        /// <summary>The counter value the next digest will use. Replays record this.</summary>
        public uint Counter => _counter;

        public SeededStream(string label, byte[] seed, uint round, uint startCounter = 0)
        {
            if (label == null) throw new ArgumentNullException(nameof(label));
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            if (seed.Length != SeedLength) throw new ArgumentException("Seed must be exactly 32 bytes.", nameof(seed));
            byte[] labelBytes = Ascii(label, nameof(label));

            Label = label;
            Round = round;
            _counter = startCounter;

            _prefix = new byte[labelBytes.Length + 1 + SeedLength + 4];
            Buffer.BlockCopy(labelBytes, 0, _prefix, 0, labelBytes.Length);
            _prefix[labelBytes.Length] = 0x00;
            Buffer.BlockCopy(seed, 0, _prefix, labelBytes.Length + 1, SeedLength);
            WriteUInt32BigEndian(_prefix, labelBytes.Length + 1 + SeedLength, round);
        }

        /// <summary>Convenience factories for the four frozen streams.</summary>
        public static SeededStream Initiative(byte[] seed) => new SeededStream(StreamLabels.Initiative, seed, 0);
        public static SeededStream Map(byte[] seed) => new SeededStream(StreamLabels.Map, seed, 0);
        public static SeededStream Terrain(byte[] seed, uint round) => new SeededStream(StreamLabels.Terrain, seed, round);
        public static SeededStream Cards(byte[] seed, uint round) => new SeededStream(StreamLabels.Cards, seed, round);

        /// <summary>Returns the full 32-byte digest for the current counter, then increments the counter.</summary>
        public byte[] NextDigest()
        {
            byte[] message = new byte[_prefix.Length + 4];
            Buffer.BlockCopy(_prefix, 0, message, 0, _prefix.Length);
            WriteUInt32BigEndian(message, _prefix.Length, _counter);
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = sha.ComputeHash(message);
            }
            _counter = checked(_counter + 1);
            return digest;
        }

        /// <summary>Returns the next raw candidate (first eight digest bytes, big-endian).</summary>
        public ulong NextCandidate() => ReadUInt64BigEndian(NextDigest(), 0);

        /// <summary>
        /// Uniformly selects an index in [0, k) with rejection sampling. Every digest, accepted or
        /// rejected, advances the counter.
        /// </summary>
        public int NextIndex(int k)
        {
            if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), "List length must be positive.");
            while (true)
            {
                ulong candidate = NextCandidate();
                if (IsAccepted(candidate, (ulong)k)) return (int)(candidate % (ulong)k);
            }
        }

        /// <summary>Selects one element of a list that the caller has already sorted by stable ID.</summary>
        public T Select<T>(IReadOnlyList<T> sortedList)
        {
            if (sortedList == null) throw new ArgumentNullException(nameof(sortedList));
            return sortedList[NextIndex(sortedList.Count)];
        }

        /// <summary>
        /// Draws <paramref name="count"/> elements without replacement. Each draw selects from the
        /// remaining list (original stable order preserved), removes the chosen element and
        /// continues with the same counter. Results are in draw order.
        /// </summary>
        public List<T> DrawWithoutReplacement<T>(IReadOnlyList<T> sortedList, int count)
        {
            if (sortedList == null) throw new ArgumentNullException(nameof(sortedList));
            if (count < 0 || count > sortedList.Count) throw new ArgumentOutOfRangeException(nameof(count));
            var remaining = new List<T>(sortedList);
            var drawn = new List<T>(count);
            for (int i = 0; i < count; i++)
            {
                int index = NextIndex(remaining.Count);
                drawn.Add(remaining[index]);
                remaining.RemoveAt(index);
            }
            return drawn;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> is below floor(2^64 / k) * k, i.e. not rejected by
        /// the modulo-bias filter. Computed without 128-bit arithmetic.
        /// </summary>
        public static bool IsAccepted(ulong candidate, ulong k)
        {
            if (k == 0) throw new ArgumentOutOfRangeException(nameof(k));
            // 2^64 mod k == (2^64 - k) mod k, which is (0 - k) mod k in wrapping unsigned arithmetic.
            ulong remainder = unchecked(0UL - k) % k;
            // floor(2^64/k)*k == 2^64 - remainder. With remainder 0 (k a power of two) every value
            // is accepted; otherwise the limit 2^64 - remainder fits in a ulong.
            return remainder == 0 || candidate < unchecked(0UL - remainder);
        }

        /// <summary>Single candidate for an explicit counter (stateless form, used by tests and replay checks).</summary>
        public static ulong Candidate(string label, byte[] seed, uint round, uint counter)
        {
            return new SeededStream(label, seed, round, counter).NextCandidate();
        }

        /// <summary>
        /// Match seed commitment published before play:
        /// SHA-256(ASCII("AK-TR-1/seed") || 0x00 || rules_hash[32] || seed[32] || canonical_match_UUID_ASCII[36]).
        /// The canonical UUID is the 36-character lowercase hyphenated form.
        /// </summary>
        public static byte[] SeedCommitment(byte[] rulesHash, byte[] seed, string matchUuid)
        {
            if (rulesHash == null || rulesHash.Length != 32) throw new ArgumentException("Rules hash must be 32 bytes.", nameof(rulesHash));
            if (seed == null || seed.Length != SeedLength) throw new ArgumentException("Seed must be 32 bytes.", nameof(seed));
            if (!IsCanonicalUuid(matchUuid)) throw new ArgumentException("Match UUID must be canonical lowercase 8-4-4-4-12 form.", nameof(matchUuid));

            byte[] label = Ascii(StreamLabels.SeedCommitment, "label");
            byte[] uuid = Ascii(matchUuid, nameof(matchUuid));
            byte[] message = new byte[label.Length + 1 + 32 + SeedLength + 36];
            int offset = 0;
            Buffer.BlockCopy(label, 0, message, offset, label.Length); offset += label.Length;
            message[offset++] = 0x00;
            Buffer.BlockCopy(rulesHash, 0, message, offset, 32); offset += 32;
            Buffer.BlockCopy(seed, 0, message, offset, SeedLength); offset += SeedLength;
            Buffer.BlockCopy(uuid, 0, message, offset, 36);
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(message);
            }
        }

        /// <summary>True for a 36-character lowercase hyphenated UUID (8-4-4-4-12 hex digits).</summary>
        public static bool IsCanonicalUuid(string uuid)
        {
            if (uuid == null || uuid.Length != 36) return false;
            for (int i = 0; i < 36; i++)
            {
                char c = uuid[i];
                if (i == 8 || i == 13 || i == 18 || i == 23)
                {
                    if (c != '-') return false;
                }
                else if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        public static ulong ReadUInt64BigEndian(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++) value = (value << 8) | bytes[offset + i];
            return value;
        }

        private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static byte[] Ascii(string text, string paramName)
        {
            foreach (char c in text)
            {
                if (c > 0x7F) throw new ArgumentException("Text must be ASCII.", paramName);
            }
            return Encoding.ASCII.GetBytes(text);
        }
    }
}
