using System;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Modes.FourPlayer
{
    /// <summary>
    /// The four kingdom labels of a four-player match. Labels are assigned to entrants from the
    /// committed seed before any private choice (see <see cref="LabelAssignment"/>). Each label
    /// starts with one equal quarter of the board: A north-west, B north-east, C south-east,
    /// D south-west (y grows downward). A-B, B-C, C-D and D-A share an edge; A-C and B-D touch only
    /// diagonally at the centre, so the candidate's "pairs without a shared border" case is real.
    /// </summary>
    public enum Kingdom : byte
    {
        A = 0,
        B = 1,
        C = 2,
        D = 3,
    }

    /// <summary>
    /// Frozen constants of the PROPOSED four-player territory mode, ruleset
    /// <see cref="RulesId"/> (plan: "Proposed four player territory mode"). Every number here is a
    /// candidate requiring preproduction approval; changing one produces a new rules ID and hash.
    /// The two-player AK-TR-1 duel is reused unchanged for every pair; nothing in this class (or
    /// this assembly) alters an AK-TR-1 match, which keeps its own rules version, hash and queue.
    /// </summary>
    public static class FourPlayerRules
    {
        /// <summary>Separate rules identifier; never equal to <see cref="RulesConstants.RulesVersion"/>.</summary>
        public const string RulesId = "AK-4P-0-proposed";

        /// <summary>Separate matchmaking queue; four-player entrants never share the AK-TR-1 queue.</summary>
        public const string QueueId = "queue/four-player/AK-4P-0-proposed";

        /// <summary>Seats in a match.</summary>
        public const int Seats = 4;

        /// <summary>At most six waves (PROPOSED).</summary>
        public const int MaxWaves = 6;

        /// <summary>A winner's transfer is capped at 5% of the total board area (PROPOSED).</summary>
        public const int TransferCapPercent = 5;

        /// <summary>floor(51,040 x 5 / 100) = 2,552 cells.</summary>
        public const int TransferCapCells = RulesConstants.ActiveCells * TransferCapPercent / 100;

        /// <summary>Each kingdom's equal starting sector: 51,040 / 4 = 12,760 cells.</summary>
        public const int InitialCellsPerKingdom = RulesConstants.ActiveCells / Seats;

        /// <summary>Cards offered to a pair's duel winner (same size as the V1 offer).</summary>
        public const int CardOfferSize = 3;

        /// <summary>Two consecutive selection timeouts forfeit, as in AK-TR-1.</summary>
        public const int ConsecutiveTimeoutsToForfeit = RulesConstants.ConsecutiveTimeoutsToForfeit;

        /// <summary>Spectators trail active play by at least this many completed waves (PROPOSED default).</summary>
        public const int SpectatorDelayWaves = 1;

        // Seeded-stream labels. They are distinct from the four frozen AK-TR-1 labels, so a
        // four-player match never shifts (or reuses) a two-player stream.
        public const string LabelStream = "AK-4P-0/labels";
        public const string CardStream = "AK-4P-0/cards";
        public const string SeedCommitmentLabel = "AK-4P-0/seed";

        private static readonly Lazy<byte[]> HashLazy = new Lazy<byte[]>(ComputeHash);

        /// <summary>
        /// SHA-256 over the rules ID, the embedded AK-TR-1 rules hash (the duel rules every pair uses),
        /// every constant above and the stream labels.
        /// </summary>
        public static byte[] Hash => (byte[])HashLazy.Value.Clone();

        public static string HashHex => Hex.Encode(HashLazy.Value);

        private static byte[] ComputeHash()
        {
            var w = new CanonicalWriter();
            w.Ascii("AK-4P-RULES/1").Ascii(RulesId).Ascii(QueueId).Block(RulesBundle.Hash);
            w.Named("Seats", Seats).Named("MaxWaves", MaxWaves).Named("TransferCapPercent", TransferCapPercent)
             .Named("TransferCapCells", TransferCapCells).Named("InitialCellsPerKingdom", InitialCellsPerKingdom)
             .Named("CardOfferSize", CardOfferSize).Named("ConsecutiveTimeoutsToForfeit", ConsecutiveTimeoutsToForfeit)
             .Named("SpectatorDelayWaves", SpectatorDelayWaves);
            w.Ascii(LabelStream).Ascii(CardStream).Ascii(SeedCommitmentLabel);
            return w.Sha256();
        }
    }
}
