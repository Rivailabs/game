using System;
using System.Collections.Generic;
using System.Text;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Modes.Tournament
{
    /// <summary>What happens to a fixture when one or both entrants are absent.</summary>
    public enum AbsenceOutcome : byte
    {
        /// <summary>The present entrant is awarded the win (3 points); the absent one scores 0.</summary>
        PresentEntrantWins = 0,
        /// <summary>Both entrants absent: the fixture is recorded with no points for either.</summary>
        NoPointsEither = 1,
    }

    /// <summary>Published absence rules (plan: "Publish ... absence rules ... before registration").</summary>
    public sealed class AbsencePolicy
    {
        /// <summary>An entrant who has not checked in by the check-in close is absent from every fixture.</summary>
        public bool MissedCheckInMeansAbsentForAllFixtures { get; }
        /// <summary>Minutes after a fixture's scheduled start before the operator may record an absence.</summary>
        public int NoShowGraceMinutes { get; }
        public AbsenceOutcome SingleAbsence { get; }
        public AbsenceOutcome DoubleAbsence { get; }

        public AbsencePolicy(bool missedCheckInMeansAbsentForAllFixtures, int noShowGraceMinutes)
        {
            if (noShowGraceMinutes < 0) throw new ArgumentOutOfRangeException(nameof(noShowGraceMinutes));
            MissedCheckInMeansAbsentForAllFixtures = missedCheckInMeansAbsentForAllFixtures;
            NoShowGraceMinutes = noShowGraceMinutes;
            SingleAbsence = AbsenceOutcome.PresentEntrantWins;
            DoubleAbsence = AbsenceOutcome.NoPointsEither;
        }

        /// <summary>PROPOSED default: missed check-in = absent everywhere; 10-minute no-show grace.</summary>
        public static AbsencePolicy Default => new AbsencePolicy(true, 10);

        internal void WriteTo(CanonicalWriter w) =>
            w.Ascii("absence").Bool(MissedCheckInMeansAbsentForAllFixtures).I32(NoShowGraceMinutes).U8((int)SingleAbsence).U8((int)DoubleAbsence);
    }

    /// <summary>What a fixture records after its technical-cancellation replays are used up.</summary>
    public enum CancellationFallback : byte
    {
        /// <summary>Recorded as a draw (1 point each).</summary>
        RecordedDraw = 0,
        /// <summary>Recorded with no result and no points.</summary>
        NoResult = 1,
    }

    /// <summary>
    /// Published technical-cancellation rules: a fixture cancelled by a verified technical incident
    /// (service fault, not a player's absence) may be replayed a bounded number of times; afterwards
    /// the published fallback applies. Incident IDs are recorded and counted once.
    /// </summary>
    public sealed class TechnicalCancellationPolicy
    {
        public int MaxReplays { get; }
        public CancellationFallback Fallback { get; }

        public TechnicalCancellationPolicy(int maxReplays, CancellationFallback fallback)
        {
            if (maxReplays < 0 || maxReplays > 3) throw new ArgumentOutOfRangeException(nameof(maxReplays), "0-3 replays.");
            MaxReplays = maxReplays;
            Fallback = fallback;
        }

        /// <summary>PROPOSED default: one replay, then a recorded draw.</summary>
        public static TechnicalCancellationPolicy Default => new TechnicalCancellationPolicy(1, CancellationFallback.RecordedDraw);

        internal void WriteTo(CanonicalWriter w) => w.Ascii("cancellation").I32(MaxReplays).U8((int)Fallback);
    }

    /// <summary>A published, non-transferable cosmetic recognition for a placement range.</summary>
    public sealed class PrizeDescription
    {
        public int BestPlace { get; }
        public int WorstPlace { get; }
        public string CosmeticId { get; }
        public string Description { get; }
        /// <summary>Always false: transferable rewards need a separate product/compliance decision.</summary>
        public bool Transferable => false;

        public PrizeDescription(int bestPlace, int worstPlace, string cosmeticId, string description)
        {
            if (bestPlace < 1 || worstPlace < bestPlace) throw new ArgumentOutOfRangeException(nameof(bestPlace));
            if (string.IsNullOrEmpty(cosmeticId)) throw new ArgumentException("Cosmetic ID required.", nameof(cosmeticId));
            BestPlace = bestPlace;
            WorstPlace = worstPlace;
            CosmeticId = cosmeticId;
            Description = description ?? string.Empty;
        }

        public bool Covers(int place) => place >= BestPlace && place <= WorstPlace;
    }

    /// <summary>
    /// Everything published before registration opens (plan: "Publish catalog, schedule, absence
    /// rules, technical-cancellation policy and prize descriptions before registration. A named
    /// operator manages check-in, reports and result disputes."). It is immutable and hashed; the
    /// tournament refuses registration until it validates.
    /// </summary>
    public sealed class TournamentPublication
    {
        /// <summary>Format identifier of the PROPOSED first tournament format.</summary>
        public const string FormatId = "AK-RR-0-proposed";

        public string TournamentId { get; }
        public string Title { get; }
        /// <summary>Two-player match configuration every fixture uses (the catalog shown before entry).</summary>
        public MatchConfig MatchConfig { get; }
        /// <summary>4 or 8 entrants.</summary>
        public int Capacity { get; }
        /// <summary>Entry fee in any currency. Must be 0: free entry only.</summary>
        public long EntryFee { get; }
        public long RegistrationOpensMs { get; }
        public long RegistrationClosesMs { get; }
        public long CheckInOpensMs { get; }
        public long CheckInClosesMs { get; }
        public long FirstRoundStartMs { get; }
        public long RoundIntervalMs { get; }
        public AbsencePolicy Absence { get; }
        public TechnicalCancellationPolicy Cancellation { get; }
        public IReadOnlyList<PrizeDescription> Prizes { get; }
        public string OperatorId { get; }

        public TournamentPublication(string tournamentId, string title, MatchConfig matchConfig, int capacity, long entryFee,
            long registrationOpensMs, long registrationClosesMs, long checkInOpensMs, long checkInClosesMs,
            long firstRoundStartMs, long roundIntervalMs, AbsencePolicy absence, TechnicalCancellationPolicy cancellation,
            IReadOnlyList<PrizeDescription> prizes, string operatorId)
        {
            TournamentId = tournamentId;
            Title = title;
            MatchConfig = matchConfig;
            Capacity = capacity;
            EntryFee = entryFee;
            RegistrationOpensMs = registrationOpensMs;
            RegistrationClosesMs = registrationClosesMs;
            CheckInOpensMs = checkInOpensMs;
            CheckInClosesMs = checkInClosesMs;
            FirstRoundStartMs = firstRoundStartMs;
            RoundIntervalMs = roundIntervalMs;
            Absence = absence;
            Cancellation = cancellation;
            Prizes = prizes == null ? null : new List<PrizeDescription>(prizes);
            OperatorId = operatorId;
        }

        /// <summary>Rounds in a single round robin: n − 1.</summary>
        public int Rounds => Capacity - 1;

        /// <summary>Published start of a round (1-based).</summary>
        public long RoundStartMs(int round) => FirstRoundStartMs + (round - 1) * RoundIntervalMs;

        /// <summary>Returns the first problem, or null when the publication is complete and legal.</summary>
        public string FindProblem()
        {
            if (string.IsNullOrEmpty(TournamentId)) return "Tournament ID is required.";
            foreach (char c in TournamentId) if (c > 0x7F) return "Tournament ID must be ASCII.";
            if (string.IsNullOrEmpty(Title)) return "Title is required.";
            if (MatchConfig == null) return "The match configuration (catalog) must be published.";
            if (MatchConfig.RulesVersion != RulesConstants.RulesVersion) return "Fixtures use established two-player AK-TR-1 matches.";
            if (Capacity != 4 && Capacity != 8) return "The first format admits exactly four or eight entrants.";
            if (EntryFee != 0) return "Paid competitive entry requires a separate product/compliance decision.";
            if (!(RegistrationOpensMs < RegistrationClosesMs && RegistrationClosesMs <= CheckInOpensMs &&
                  CheckInOpensMs < CheckInClosesMs && CheckInClosesMs <= FirstRoundStartMs))
                return "Schedule times must be ordered: registration, check-in, first round.";
            if (RoundIntervalMs <= 0) return "Round interval must be positive.";
            if (Absence == null) return "An absence policy must be published.";
            if (Cancellation == null) return "A technical-cancellation policy must be published.";
            if (Prizes == null || Prizes.Count == 0) return "Prize descriptions must be published.";
            foreach (PrizeDescription p in Prizes)
                if (p.WorstPlace > Capacity) return "A prize covers a place beyond the capacity.";
            if (string.IsNullOrEmpty(OperatorId)) return "A named operator must manage check-in, reports and disputes.";
            return null;
        }

        /// <summary>SHA-256 over the canonical publication (part of every tournament result ID).</summary>
        public byte[] ComputeHash()
        {
            var w = new CanonicalWriter();
            w.Ascii(FormatId).Ascii(TournamentId).Block(Encoding.UTF8.GetBytes(Title ?? string.Empty));
            MatchConfig.WriteTo(w);
            w.Block(RulesBundle.Hash).I32(Capacity).I64(EntryFee).I64(RegistrationOpensMs).I64(RegistrationClosesMs)
             .I64(CheckInOpensMs).I64(CheckInClosesMs).I64(FirstRoundStartMs).I64(RoundIntervalMs);
            Absence.WriteTo(w);
            Cancellation.WriteTo(w);
            w.U32((uint)Prizes.Count);
            foreach (PrizeDescription p in Prizes) w.I32(p.BestPlace).I32(p.WorstPlace).Ascii(p.CosmeticId).Block(Encoding.UTF8.GetBytes(p.Description)).Bool(p.Transferable);
            w.Ascii(OperatorId);
            return w.Sha256();
        }
    }
}
