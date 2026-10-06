using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Modes.FourPlayer
{
    /// <summary>
    /// Seed-committed assignment of labels A-D to four entrants (plan: "Assign labels A-D using a
    /// seed committed before private choices").
    /// <para>
    /// The service publishes <see cref="Commitment"/> =
    /// SHA-256(canonical("AK-4P-0/seed", four-player rules hash, seed[32], match UUID, sorted roster))
    /// before any loadout or lock is accepted. The labels themselves are drawn from the
    /// "AK-4P-0/labels" stream: the roster sorted by ordinal ID is drawn without replacement and the
    /// k-th drawn entrant receives label k. When the match ends the seed is disclosed and anyone can
    /// recompute both the commitment and the labels with <see cref="Verify"/>.
    /// </para>
    /// </summary>
    public sealed class LabelAssignment
    {
        private readonly string[] _entrantByLabel;

        /// <summary>Entrant IDs sorted by ordinal comparison (the stable list the draw uses).</summary>
        public IReadOnlyList<string> SortedRoster { get; }

        public byte[] Commitment { get; }

        public string CommitmentHex => Hex.Encode(Commitment);

        /// <summary>Digests consumed from the label stream.</summary>
        public uint StreamCounter { get; }

        private LabelAssignment(string[] entrantByLabel, IReadOnlyList<string> sortedRoster, byte[] commitment, uint counter)
        {
            _entrantByLabel = entrantByLabel;
            SortedRoster = sortedRoster;
            Commitment = commitment;
            StreamCounter = counter;
        }

        public string EntrantOf(Kingdom label) => _entrantByLabel[(int)label];

        /// <summary>The label of an entrant, or null when the entrant is not in this match.</summary>
        public Kingdom? LabelOf(string entrantId)
        {
            for (int i = 0; i < _entrantByLabel.Length; i++)
                if (string.Equals(_entrantByLabel[i], entrantId, StringComparison.Ordinal)) return (Kingdom)i;
            return null;
        }

        /// <summary>Assigns labels. Entrant IDs must be four distinct non-empty ASCII strings.</summary>
        public static LabelAssignment Create(IReadOnlyList<string> entrants, byte[] seed, string matchUuid)
        {
            List<string> sorted = SortRoster(entrants);
            if (seed == null || seed.Length != SeededStream.SeedLength) throw new ArgumentException("Seed must be 32 bytes.", nameof(seed));
            if (!SeededStream.IsCanonicalUuid(matchUuid)) throw new ArgumentException("Match ID must be a canonical lowercase UUID.", nameof(matchUuid));

            var stream = new SeededStream(FourPlayerRules.LabelStream, seed, 0);
            List<string> drawn = stream.DrawWithoutReplacement(sorted, FourPlayerRules.Seats);
            return new LabelAssignment(drawn.ToArray(), sorted, ComputeCommitment(seed, matchUuid, sorted), stream.Counter);
        }

        /// <summary>
        /// Checks a disclosed seed against a published commitment and label list. Returns false when
        /// either the commitment or any label differs.
        /// </summary>
        public static bool Verify(byte[] commitment, byte[] disclosedSeed, string matchUuid, IReadOnlyList<string> entrants,
            IReadOnlyList<string> publishedEntrantByLabel)
        {
            if (commitment == null || publishedEntrantByLabel == null || publishedEntrantByLabel.Count != FourPlayerRules.Seats) return false;
            LabelAssignment recomputed = Create(entrants, disclosedSeed, matchUuid);
            if (!Equal(recomputed.Commitment, commitment)) return false;
            for (int i = 0; i < FourPlayerRules.Seats; i++)
                if (!string.Equals(recomputed._entrantByLabel[i], publishedEntrantByLabel[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static byte[] ComputeCommitment(byte[] seed, string matchUuid, List<string> sorted)
        {
            var w = new CanonicalWriter();
            w.Ascii(FourPlayerRules.SeedCommitmentLabel).Block(FourPlayerRules.Hash).Block(seed).Ascii(matchUuid);
            w.U32((uint)sorted.Count);
            foreach (string id in sorted) w.Ascii(id);
            return w.Sha256();
        }

        private static List<string> SortRoster(IReadOnlyList<string> entrants)
        {
            if (entrants == null || entrants.Count != FourPlayerRules.Seats)
                throw new ArgumentException("A four-player match needs exactly four entrants.", nameof(entrants));
            var sorted = new List<string>(entrants.Count);
            foreach (string id in entrants)
            {
                if (string.IsNullOrEmpty(id)) throw new ArgumentException("Entrant IDs must be non-empty.", nameof(entrants));
                foreach (char c in id)
                    if (c > 0x7F) throw new ArgumentException("Entrant IDs must be ASCII.", nameof(entrants));
                if (sorted.Contains(id)) throw new ArgumentException("Entrant '" + id + "' appears twice.", nameof(entrants));
                sorted.Add(id);
            }
            sorted.Sort(StringComparer.Ordinal);
            return sorted;
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
