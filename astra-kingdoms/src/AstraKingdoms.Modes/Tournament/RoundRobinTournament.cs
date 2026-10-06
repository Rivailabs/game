using System;
using System.Collections.Generic;
using System.Text;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Modes.Tournament
{
    public enum TournamentStage : byte
    {
        /// <summary>Registration and check-in (before the operator starts the event).</summary>
        Open = 0,
        Running = 1,
        /// <summary>Standings are final and cosmetics have been awarded.</summary>
        Finalized = 2,
        /// <summary>Fewer than the published number of entrants registered; nothing is played or awarded.</summary>
        CancelledUnderfilled = 3,
    }

    public enum FixtureStatus : byte
    {
        /// <summary>Waiting for a verified result (or a replay after a technical cancellation).</summary>
        Scheduled = 0,
        /// <summary>A verified match result was recorded.</summary>
        Completed = 1,
        /// <summary>One or both entrants were absent; the published absence policy decided the points.</summary>
        Absence = 2,
        /// <summary>Technical replays were used up; the published fallback decided the points.</summary>
        CancellationFallback = 3,
    }

    /// <summary>One scheduled two-player fixture of the round robin.</summary>
    public sealed class Fixture
    {
        public int Index { get; internal set; }
        public int Round { get; internal set; }
        public int Table { get; internal set; }
        /// <summary>Entrant seated as player A of the AK-TR-1 match.</summary>
        public string EntrantA { get; internal set; }
        public string EntrantB { get; internal set; }
        /// <summary>The match ID the fixture must be played under (changes with each technical replay).</summary>
        public string MatchId { get; internal set; }
        public long ScheduledStartMs { get; internal set; }
        public FixtureStatus Status { get; internal set; }
        public string ResultId { get; internal set; }
        public int PointsA { get; internal set; }
        public int PointsB { get; internal set; }
        public int Replays { get; internal set; }
        public List<string> IncidentIds { get; } = new List<string>();
        public string Note { get; internal set; }

        public bool IsResolved => Status != FixtureStatus.Scheduled;

        public bool Involves(string entrant) => EntrantA == entrant || EntrantB == entrant;
    }

    public enum DisputeStatus : byte
    {
        Open = 0,
        Upheld = 1,
        Rejected = 2,
    }

    /// <summary>An audited result dispute handled by the named operator.</summary>
    public sealed class DisputeRecord
    {
        public string Id { get; internal set; }
        public string Entrant { get; internal set; }
        public int FixtureIndex { get; internal set; }
        public string Reason { get; internal set; }
        public long OpenedMs { get; internal set; }
        public DisputeStatus Status { get; internal set; }
        public string DecidedBy { get; internal set; }
        public string DecisionNote { get; internal set; }
        public long DecidedMs { get; internal set; }
        /// <summary>The points before an upheld correction (audit trail).</summary>
        public int PreviousPointsA { get; internal set; }
        public int PreviousPointsB { get; internal set; }
    }

    /// <summary>One standings row. <see cref="Place"/> is shared on exactly equal points.</summary>
    public sealed class StandingRow
    {
        public string Entrant { get; internal set; }
        public int Points { get; internal set; }
        public int Played { get; internal set; }
        public int Wins { get; internal set; }
        public int Draws { get; internal set; }
        public int Losses { get; internal set; }
        public int Place { get; internal set; }

        public override string ToString() => Place + ". " + Entrant + " " + Points + " pts (" + Wins + "-" + Draws + "-" + Losses + ")";
    }

    /// <summary>
    /// The PROPOSED first community tournament format (plan: "Spectators tournaments and platform
    /// expansion"): free entry, four or eight entrants, a scheduled single round robin of established
    /// two-player AK-TR-1 matches, three points for a win, one for a draw, none for a loss, and exact
    /// final ties sharing placement (no tiebreak, no coin flip). The named operator records absences
    /// and technical cancellations and decides disputes; results come only from verified match
    /// records; the verified tournament-result ID awards each cosmetic once.
    /// <para>All public members are thread-safe (one lock).</para>
    /// </summary>
    public sealed class RoundRobinTournament
    {
        public const int WinPoints = 3;
        public const int DrawPoints = 1;
        public const int LossPoints = 0;

        private readonly object _gate = new object();
        private readonly List<string> _registered = new List<string>();
        private readonly HashSet<string> _checkedIn = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Fixture> _fixtures = new List<Fixture>();
        private readonly Dictionary<string, int> _resultIds = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<DisputeRecord> _disputes = new List<DisputeRecord>();
        private readonly byte[] _publicationHash;

        public TournamentPublication Publication { get; }
        public TournamentStage Stage { get; private set; }
        public string TournamentResultId { get; private set; }

        public RoundRobinTournament(TournamentPublication publication)
        {
            Publication = publication ?? throw new ArgumentNullException(nameof(publication));
            string problem = publication.FindProblem();
            if (problem != null) throw new ArgumentException("Incomplete publication: " + problem, nameof(publication));
            _publicationHash = publication.ComputeHash();
            Stage = TournamentStage.Open;
        }

        public string PublicationHashHex => Hex.Encode(_publicationHash);

        public IReadOnlyList<string> Registered
        {
            get { lock (_gate) return _registered.ToArray(); }
        }

        public bool IsCheckedIn(string entrant)
        {
            lock (_gate) return _checkedIn.Contains(entrant);
        }

        public IReadOnlyList<Fixture> Fixtures
        {
            get { lock (_gate) return _fixtures.ToArray(); }
        }

        public IReadOnlyList<DisputeRecord> Disputes
        {
            get { lock (_gate) return _disputes.ToArray(); }
        }

        // ------------------------------------------------------------------ registration

        public ModeReceipt Register(string entrant, long nowMs)
        {
            lock (_gate)
            {
                if (Stage != TournamentStage.Open) return ModeReceipt.Reject("CLOSED", "Registration is closed.");
                if (string.IsNullOrEmpty(entrant)) return ModeReceipt.Reject("ENTRANT", "Entrant ID required.");
                if (nowMs < Publication.RegistrationOpensMs || nowMs >= Publication.RegistrationClosesMs)
                    return ModeReceipt.Reject("REGISTRATION_WINDOW", "Outside the published registration window.");
                if (_registered.Contains(entrant)) return ModeReceipt.Reject("DUPLICATE", "Already registered.");
                if (_registered.Count >= Publication.Capacity) return ModeReceipt.Reject("FULL", "The tournament is full.");
                _registered.Add(entrant);
                return ModeReceipt.Ok;
            }
        }

        public ModeReceipt CheckIn(string entrant, long nowMs)
        {
            lock (_gate)
            {
                if (Stage != TournamentStage.Open) return ModeReceipt.Reject("CLOSED", "Check-in is closed.");
                if (!_registered.Contains(entrant)) return ModeReceipt.Reject("NOT_REGISTERED", "Only registered entrants check in.");
                if (nowMs < Publication.CheckInOpensMs || nowMs >= Publication.CheckInClosesMs)
                    return ModeReceipt.Reject("CHECK_IN_WINDOW", "Outside the published check-in window.");
                _checkedIn.Add(entrant);
                return ModeReceipt.Ok;
            }
        }

        /// <summary>
        /// The operator starts the event after check-in closes. With fewer registrants than the
        /// published capacity the tournament is cancelled (nothing played or awarded). Otherwise the
        /// round-robin schedule is built (circle method over the roster sorted by ordinal ID) and, per
        /// the absence policy, every entrant who missed check-in is absent from all fixtures.
        /// </summary>
        public ModeReceipt Start(string operatorId, long nowMs)
        {
            lock (_gate)
            {
                if (operatorId != Publication.OperatorId) return ModeReceipt.Reject("NOT_OPERATOR", "Only the named operator starts the event.");
                if (Stage != TournamentStage.Open) return ModeReceipt.Reject("WRONG_STAGE", "Already started.");
                if (nowMs < Publication.CheckInClosesMs) return ModeReceipt.Reject("CHECK_IN_OPEN", "Check-in has not closed.");
                if (_registered.Count != Publication.Capacity)
                {
                    Stage = TournamentStage.CancelledUnderfilled;
                    return ModeReceipt.Ok;
                }
                BuildSchedule();
                Stage = TournamentStage.Running;
                if (Publication.Absence.MissedCheckInMeansAbsentForAllFixtures)
                {
                    foreach (Fixture f in _fixtures)
                    {
                        bool aAbsent = !_checkedIn.Contains(f.EntrantA), bAbsent = !_checkedIn.Contains(f.EntrantB);
                        if (aAbsent || bAbsent) ApplyAbsence(f, aAbsent, bAbsent, "missed check-in");
                    }
                }
                return ModeReceipt.Ok;
            }
        }

        private void BuildSchedule()
        {
            var roster = new List<string>(_registered);
            roster.Sort(StringComparer.Ordinal);
            int n = roster.Count;
            var ring = new List<string>(roster);
            int index = 0;
            for (int round = 1; round <= n - 1; round++)
            {
                for (int table = 0; table < n / 2; table++)
                {
                    string x = ring[table], y = ring[n - 1 - table];
                    // Seat balance: rotating players sit A on the left half of the ring and B on the
                    // right; the fixed player's table alternates every round.
                    bool swap = table == 0 && round % 2 == 0;
                    var f = new Fixture
                    {
                        Index = index,
                        Round = round,
                        Table = table + 1,
                        EntrantA = swap ? y : x,
                        EntrantB = swap ? x : y,
                        ScheduledStartMs = Publication.RoundStartMs(round),
                        Status = FixtureStatus.Scheduled,
                    };
                    f.MatchId = DeriveMatchId(index, 0);
                    _fixtures.Add(f);
                    index++;
                }
                // Circle method: keep ring[0], rotate the rest one step clockwise.
                string last = ring[n - 1];
                ring.RemoveAt(n - 1);
                ring.Insert(1, last);
            }
        }

        /// <summary>Deterministic canonical UUID for a fixture (and replay attempt) of this tournament.</summary>
        private string DeriveMatchId(int fixtureIndex, int replay)
        {
            var w = new CanonicalWriter();
            w.Ascii("AK-RR-0/match").Block(_publicationHash).I32(fixtureIndex).I32(replay);
            string h = Hex.Encode(w.Sha256());
            char variant = "89ab"[h[16] % 4];
            return h.Substring(0, 8) + "-" + h.Substring(8, 4) + "-4" + h.Substring(13, 3) + "-" + variant + h.Substring(17, 3) + "-" + h.Substring(20, 12);
        }

        // ------------------------------------------------------------------ results

        /// <summary>
        /// Records a verified result for a fixture. The record must replay, belong to the fixture's
        /// current match ID and be finished. Submitting the same verified result again is idempotent;
        /// a different result for an already resolved fixture needs a dispute.
        /// </summary>
        public ModeReceipt RecordResult(int fixtureIndex, string recordJson, IMatchResultVerifier verifier)
        {
            if (verifier == null) throw new ArgumentNullException(nameof(verifier));
            VerifiedMatchResult v = verifier.Verify(recordJson, out string failure);
            lock (_gate)
            {
                if (Stage != TournamentStage.Running) return ModeReceipt.Reject("WRONG_STAGE", "The tournament is not running.");
                if (v == null) return ModeReceipt.Reject("UNVERIFIED", failure);
                if (_resultIds.TryGetValue(v.ResultId, out int already))
                    return already == fixtureIndex ? ModeReceipt.Ok : ModeReceipt.Reject("RESULT_REUSED", "This result belongs to another fixture.");
                Fixture f = FixtureAt(fixtureIndex);
                if (f == null) return ModeReceipt.Reject("NO_FIXTURE", "Unknown fixture.");
                if (f.IsResolved) return ModeReceipt.Reject("ALREADY_RESOLVED", "Open a dispute to change a recorded result.");
                if (v.MatchId != f.MatchId) return ModeReceipt.Reject("WRONG_MATCH", "The record is not this fixture's match.");
                switch (v.Outcome)
                {
                    case FixtureOutcome.AWins: SetPoints(f, WinPoints, LossPoints); break;
                    case FixtureOutcome.BWins: SetPoints(f, LossPoints, WinPoints); break;
                    case FixtureOutcome.Draw: SetPoints(f, DrawPoints, DrawPoints); break;
                    default:
                        ApplyAbsence(f, true, true, "void match: both seats forfeited");
                        f.ResultId = v.ResultId;
                        _resultIds[v.ResultId] = fixtureIndex;
                        return ModeReceipt.Ok;
                }
                f.Status = FixtureStatus.Completed;
                f.ResultId = v.ResultId;
                f.Note = v.Outcome.ToString();
                _resultIds[v.ResultId] = fixtureIndex;
                return ModeReceipt.Ok;
            }
        }

        /// <summary>The operator records a no-show after the published grace period.</summary>
        public ModeReceipt RecordAbsence(string operatorId, int fixtureIndex, bool aAbsent, bool bAbsent, long nowMs)
        {
            lock (_gate)
            {
                if (operatorId != Publication.OperatorId) return ModeReceipt.Reject("NOT_OPERATOR", "Only the named operator records absences.");
                if (Stage != TournamentStage.Running) return ModeReceipt.Reject("WRONG_STAGE", "The tournament is not running.");
                Fixture f = FixtureAt(fixtureIndex);
                if (f == null) return ModeReceipt.Reject("NO_FIXTURE", "Unknown fixture.");
                if (f.IsResolved) return ModeReceipt.Reject("ALREADY_RESOLVED", "The fixture is resolved.");
                if (!aAbsent && !bAbsent) return ModeReceipt.Reject("NOBODY_ABSENT", "Name at least one absent entrant.");
                if (nowMs < f.ScheduledStartMs + Publication.Absence.NoShowGraceMinutes * 60_000L)
                    return ModeReceipt.Reject("GRACE", "The published no-show grace period has not passed.");
                ApplyAbsence(f, aAbsent, bAbsent, "no-show");
                return ModeReceipt.Ok;
            }
        }

        /// <summary>
        /// The operator records a verified technical incident that cancelled the fixture's match. An
        /// incident ID is counted once. Within the published replay budget the fixture is rescheduled
        /// under a new match ID; afterwards the published fallback applies.
        /// </summary>
        public ModeReceipt RecordTechnicalCancellation(string operatorId, int fixtureIndex, string incidentId)
        {
            lock (_gate)
            {
                if (operatorId != Publication.OperatorId) return ModeReceipt.Reject("NOT_OPERATOR", "Only the named operator records incidents.");
                if (Stage != TournamentStage.Running) return ModeReceipt.Reject("WRONG_STAGE", "The tournament is not running.");
                if (string.IsNullOrEmpty(incidentId)) return ModeReceipt.Reject("INCIDENT", "An incident ID is required.");
                Fixture f = FixtureAt(fixtureIndex);
                if (f == null) return ModeReceipt.Reject("NO_FIXTURE", "Unknown fixture.");
                if (f.IncidentIds.Contains(incidentId)) return ModeReceipt.Ok; // idempotent
                if (f.IsResolved) return ModeReceipt.Reject("ALREADY_RESOLVED", "The fixture is resolved.");
                f.IncidentIds.Add(incidentId);
                if (f.Replays < Publication.Cancellation.MaxReplays)
                {
                    f.Replays++;
                    f.MatchId = DeriveMatchId(f.Index, f.Replays);
                    f.Note = "replay " + f.Replays + " after incident " + incidentId;
                    return ModeReceipt.Ok;
                }
                if (Publication.Cancellation.Fallback == CancellationFallback.RecordedDraw) SetPoints(f, DrawPoints, DrawPoints);
                else SetPoints(f, 0, 0);
                f.Status = FixtureStatus.CancellationFallback;
                f.Note = "technical cancellation fallback: " + Publication.Cancellation.Fallback;
                return ModeReceipt.Ok;
            }
        }

        // ------------------------------------------------------------------ disputes

        public ModeReceipt OpenDispute(string entrant, int fixtureIndex, string reason, long nowMs, out string disputeId)
        {
            disputeId = null;
            lock (_gate)
            {
                if (Stage != TournamentStage.Running) return ModeReceipt.Reject("WRONG_STAGE", "Disputes are accepted while the event runs.");
                Fixture f = FixtureAt(fixtureIndex);
                if (f == null) return ModeReceipt.Reject("NO_FIXTURE", "Unknown fixture.");
                if (!f.Involves(entrant)) return ModeReceipt.Reject("NOT_PARTICIPANT", "Only a fixture's entrants may dispute it.");
                if (string.IsNullOrEmpty(reason)) return ModeReceipt.Reject("REASON", "A reason is required.");
                var d = new DisputeRecord
                {
                    Id = "dispute-" + (_disputes.Count + 1),
                    Entrant = entrant,
                    FixtureIndex = fixtureIndex,
                    Reason = reason,
                    OpenedMs = nowMs,
                    Status = DisputeStatus.Open,
                };
                _disputes.Add(d);
                disputeId = d.Id;
                return ModeReceipt.Ok;
            }
        }

        /// <summary>
        /// The operator decides a dispute. An upheld dispute may correct the fixture's points (the
        /// previous points are kept in the audit record); a rejected one changes nothing.
        /// </summary>
        public ModeReceipt ResolveDispute(string operatorId, string disputeId, bool upheld, int? correctedPointsA, int? correctedPointsB,
            string note, long nowMs)
        {
            lock (_gate)
            {
                if (operatorId != Publication.OperatorId) return ModeReceipt.Reject("NOT_OPERATOR", "Only the named operator decides disputes.");
                DisputeRecord d = _disputes.Find(x => x.Id == disputeId);
                if (d == null) return ModeReceipt.Reject("NO_DISPUTE", "Unknown dispute.");
                if (d.Status != DisputeStatus.Open) return ModeReceipt.Reject("DECIDED", "The dispute is already decided.");
                Fixture f = _fixtures[d.FixtureIndex];
                if (upheld && correctedPointsA.HasValue && correctedPointsB.HasValue)
                {
                    if (!IsLegalPointPair(correctedPointsA.Value, correctedPointsB.Value))
                        return ModeReceipt.Reject("POINTS", "Corrected points must be 3/0, 0/3, 1/1 or 0/0.");
                    d.PreviousPointsA = f.PointsA;
                    d.PreviousPointsB = f.PointsB;
                    SetPoints(f, correctedPointsA.Value, correctedPointsB.Value);
                    if (f.Status == FixtureStatus.Scheduled) f.Status = FixtureStatus.Completed;
                    f.Note = "corrected by " + d.Id;
                }
                d.Status = upheld ? DisputeStatus.Upheld : DisputeStatus.Rejected;
                d.DecidedBy = operatorId;
                d.DecisionNote = note ?? string.Empty;
                d.DecidedMs = nowMs;
                return ModeReceipt.Ok;
            }
        }

        private static bool IsLegalPointPair(int a, int b) =>
            (a == WinPoints && b == LossPoints) || (a == LossPoints && b == WinPoints) || (a == DrawPoints && b == DrawPoints) || (a == 0 && b == 0);

        // ------------------------------------------------------------------ standings and awards

        /// <summary>Current standings: points, then shared place on exactly equal points.</summary>
        public IReadOnlyList<StandingRow> Standings()
        {
            lock (_gate) return ComputeStandings();
        }

        private List<StandingRow> ComputeStandings()
        {
            var rows = new Dictionary<string, StandingRow>(StringComparer.Ordinal);
            foreach (string e in _registered) rows[e] = new StandingRow { Entrant = e };
            foreach (Fixture f in _fixtures)
            {
                if (!f.IsResolved) continue;
                Tally(rows[f.EntrantA], f.PointsA, f.PointsB);
                Tally(rows[f.EntrantB], f.PointsB, f.PointsA);
            }
            var list = new List<StandingRow>(rows.Values);
            foreach (StandingRow r in list)
            {
                int better = 0;
                foreach (StandingRow o in list)
                    if (o.Points > r.Points) better++;
                r.Place = better + 1;
            }
            list.Sort((x, y) => x.Place != y.Place ? x.Place.CompareTo(y.Place) : string.CompareOrdinal(x.Entrant, y.Entrant));
            return list;
        }

        private static void Tally(StandingRow row, int own, int other)
        {
            row.Points += own;
            if (own == 0 && other == 0) return; // double absence or no result: not a played fixture
            row.Played++;
            if (own == WinPoints) row.Wins++;
            else if (own == DrawPoints && other == DrawPoints) row.Draws++;
            else row.Losses++;
        }

        /// <summary>
        /// Finalizes when every fixture is resolved and no dispute is open: computes the verified
        /// tournament-result ID (SHA-256 over the publication hash, every fixture's result and the
        /// standings) and grants each prize's cosmetic once per entrant, keyed by that ID. Calling it
        /// again returns the same ID and grants nothing new.
        /// </summary>
        public ModeReceipt FinalizeAndAward(string operatorId, CosmeticGrantLedger ledger, out string tournamentResultId)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            tournamentResultId = null;
            lock (_gate)
            {
                if (operatorId != Publication.OperatorId) return ModeReceipt.Reject("NOT_OPERATOR", "Only the named operator finalizes.");
                if (Stage == TournamentStage.Finalized)
                {
                    tournamentResultId = TournamentResultId;
                    GrantAll(ledger, ComputeStandings()); // idempotent: grant IDs already exist
                    return ModeReceipt.Ok;
                }
                if (Stage != TournamentStage.Running) return ModeReceipt.Reject("WRONG_STAGE", "The tournament is not running.");
                foreach (Fixture f in _fixtures)
                    if (!f.IsResolved) return ModeReceipt.Reject("UNRESOLVED", "Fixture " + f.Index + " has no result.");
                foreach (DisputeRecord d in _disputes)
                    if (d.Status == DisputeStatus.Open) return ModeReceipt.Reject("OPEN_DISPUTE", d.Id + " is still open.");

                List<StandingRow> standings = ComputeStandings();
                var w = new CanonicalWriter();
                w.Ascii("AK-RR-0/result").Block(_publicationHash);
                foreach (Fixture f in _fixtures)
                    w.I32(f.Index).Ascii(f.MatchId).Ascii(f.ResultId ?? "-").U8((int)f.Status).I32(f.PointsA).I32(f.PointsB);
                foreach (StandingRow r in standings) w.Ascii(r.Entrant).I32(r.Points).I32(r.Place);
                TournamentResultId = Hex.Encode(w.Sha256());
                Stage = TournamentStage.Finalized;
                GrantAll(ledger, standings);
                tournamentResultId = TournamentResultId;
                return ModeReceipt.Ok;
            }
        }

        private void GrantAll(CosmeticGrantLedger ledger, List<StandingRow> standings)
        {
            foreach (StandingRow r in standings)
                foreach (PrizeDescription p in Publication.Prizes)
                    if (p.Covers(r.Place))
                        ledger.Grant(TournamentResultId + "/" + r.Entrant + "/" + p.CosmeticId, r.Entrant, p.CosmeticId);
        }

        // ------------------------------------------------------------------ helpers

        private Fixture FixtureAt(int index) => index >= 0 && index < _fixtures.Count ? _fixtures[index] : null;

        private static void SetPoints(Fixture f, int a, int b)
        {
            f.PointsA = a;
            f.PointsB = b;
        }

        private void ApplyAbsence(Fixture f, bool aAbsent, bool bAbsent, string why)
        {
            if (aAbsent && bAbsent) SetPoints(f, 0, 0);          // DoubleAbsence: no points either
            else if (aAbsent) SetPoints(f, LossPoints, WinPoints); // SingleAbsence: present entrant wins
            else SetPoints(f, WinPoints, LossPoints);
            f.Status = FixtureStatus.Absence;
            var sb = new StringBuilder("absence (").Append(why).Append("): ");
            if (aAbsent) sb.Append(f.EntrantA).Append(' ');
            if (bAbsent) sb.Append(f.EntrantB);
            f.Note = sb.ToString().TrimEnd();
        }
    }
}
