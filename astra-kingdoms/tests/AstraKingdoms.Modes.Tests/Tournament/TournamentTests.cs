using AstraKingdoms.Modes.Tournament;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Modes.Tests.Tournament;

/// <summary>
/// Plan: "free-entry community tournaments ... four or eight entrants in a scheduled round-robin
/// using established two-player matches, scoring three points for a win and one for a draw. Exact
/// final ties share placement. ... verified tournament-result IDs award cosmetics once."
/// </summary>
public class TournamentTests
{
    private const string Operator = "op-nisha";
    private const long Hour = 3_600_000L;
    private const long T0 = 1_790_000_000_000L;

    private static TournamentPublication Pub(int capacity = 4, long fee = 0, string op = Operator, AbsencePolicy? absence = null,
        TechnicalCancellationPolicy? cancel = null, IReadOnlyList<PrizeDescription>? prizes = null) =>
        new("cup-01", "Community Cup", MatchConfig.Pilot(MatchMode.Online), capacity, fee,
            T0, T0 + 24 * Hour, T0 + 24 * Hour, T0 + 25 * Hour, T0 + 26 * Hour, Hour,
            absence ?? AbsencePolicy.Default, cancel ?? TechnicalCancellationPolicy.Default,
            prizes ?? new[] { new PrizeDescription(1, 1, "banner-gold", "Champion banner"), new PrizeDescription(1, 4, "badge-cup-01", "Participant badge") },
            op);

    private static string E(int i) => "player-" + i;

    /// <summary>Registers and checks in <paramref name="n"/> entrants, then starts.</summary>
    private static RoundRobinTournament Running(int capacity = 4, IEnumerable<int>? skipCheckIn = null, TechnicalCancellationPolicy? cancel = null)
    {
        var t = new RoundRobinTournament(Pub(capacity, cancel: cancel,
            prizes: new[] { new PrizeDescription(1, 1, "banner-gold", "Champion banner"), new PrizeDescription(1, capacity, "badge", "Participant") }));
        var skip = new HashSet<int>(skipCheckIn ?? Array.Empty<int>());
        for (int i = 0; i < capacity; i++) Assert.That(t.Register(E(i), T0 + Hour).Accepted, Is.True);
        for (int i = 0; i < capacity; i++)
            if (!skip.Contains(i)) Assert.That(t.CheckIn(E(i), T0 + 24 * Hour + 1).Accepted, Is.True);
        Assert.That(t.Start(Operator, T0 + 25 * Hour).Accepted, Is.True);
        Assert.That(t.Stage, Is.EqualTo(TournamentStage.Running));
        return t;
    }

    /// <summary>Test verifier: the "record" text names the match, the outcome and a nonce.</summary>
    private sealed class ScriptedVerifier : IMatchResultVerifier
    {
        public VerifiedMatchResult Verify(string recordJson, out string failure)
        {
            failure = null!;
            string[] parts = recordJson.Split('|');
            if (parts.Length != 3)
            {
                failure = "bad";
                return null!;
            }
            return new VerifiedMatchResult("rid-" + recordJson, parts[0], Enum.Parse<FixtureOutcome>(parts[1]));
        }
    }

    private static string Rec(Fixture f, FixtureOutcome o, string nonce = "1") => f.MatchId + "|" + o + "|" + nonce;

    [Test]
    public void Publication_MustBeCompleteFreeAndCosmeticBeforeRegistration()
    {
        Assert.That(Pub().FindProblem(), Is.Null);
        Assert.That(Pub(capacity: 6).FindProblem(), Does.Contain("four or eight"));
        Assert.That(Pub(fee: 100).FindProblem(), Does.Contain("Paid competitive entry"));
        Assert.That(Pub(op: "").FindProblem(), Does.Contain("named operator"));
        Assert.That(Pub(prizes: Array.Empty<PrizeDescription>()).FindProblem(), Does.Contain("Prize"));
        Assert.That(Pub(prizes: new[] { new PrizeDescription(1, 5, "x", "x") }).FindProblem(), Does.Contain("beyond"));
        Assert.Throws<ArgumentException>(() => new RoundRobinTournament(Pub(fee: 1)));
        Assert.That(new PrizeDescription(1, 1, "x", "x").Transferable, Is.False);
        byte[] h1 = Pub().ComputeHash(), h2 = Pub().ComputeHash();
        Assert.That(h2, Is.EqualTo(h1), "stable");
        Assert.That(Pub(capacity: 8).ComputeHash(), Is.Not.EqualTo(Pub().ComputeHash()));
    }

    [Test]
    public void RegistrationAndCheckIn_FollowThePublishedWindows()
    {
        var t = new RoundRobinTournament(Pub());
        Assert.That(t.Register(E(0), T0 - 1).Code, Is.EqualTo("REGISTRATION_WINDOW"));
        Assert.That(t.Register(E(0), T0).Accepted, Is.True);
        Assert.That(t.Register(E(0), T0).Code, Is.EqualTo("DUPLICATE"));
        for (int i = 1; i < 4; i++) t.Register(E(i), T0);
        Assert.That(t.Register(E(9), T0).Code, Is.EqualTo("FULL"));
        Assert.That(t.CheckIn(E(0), T0 + Hour).Code, Is.EqualTo("CHECK_IN_WINDOW"));
        Assert.That(t.CheckIn(E(9), T0 + 24 * Hour).Code, Is.EqualTo("NOT_REGISTERED"));
        Assert.That(t.CheckIn(E(0), T0 + 24 * Hour).Accepted, Is.True);
        Assert.That(t.Start("someone", T0 + 25 * Hour).Code, Is.EqualTo("NOT_OPERATOR"));
        Assert.That(t.Start(Operator, T0 + 24 * Hour).Code, Is.EqualTo("CHECK_IN_OPEN"));
    }

    [Test]
    public void Underfilled_IsCancelledWithNothingPlayed()
    {
        var t = new RoundRobinTournament(Pub());
        t.Register(E(0), T0);
        t.Register(E(1), T0);
        Assert.That(t.Start(Operator, T0 + 25 * Hour).Accepted, Is.True);
        Assert.That(t.Stage, Is.EqualTo(TournamentStage.CancelledUnderfilled));
        Assert.That(t.Fixtures, Is.Empty);
        Assert.That(t.FinalizeAndAward(Operator, new CosmeticGrantLedger(), out _).Accepted, Is.False);
    }

    [TestCase(4)]
    [TestCase(8)]
    public void Schedule_IsASingleRoundRobin(int n)
    {
        RoundRobinTournament t = Running(n);
        IReadOnlyList<Fixture> f = t.Fixtures;
        Assert.That(f, Has.Count.EqualTo(n * (n - 1) / 2));
        var pairs = new HashSet<string>();
        foreach (Fixture x in f)
        {
            Assert.That(x.EntrantA, Is.Not.EqualTo(x.EntrantB));
            Assert.That(pairs.Add(string.Join("+", new[] { x.EntrantA, x.EntrantB }.OrderBy(s => s, StringComparer.Ordinal))), Is.True, "each pair meets once");
            Assert.That(SeededStream.IsCanonicalUuid(x.MatchId), Is.True);
            Assert.That(x.ScheduledStartMs, Is.EqualTo(t.Publication.RoundStartMs(x.Round)));
        }
        for (int r = 1; r <= n - 1; r++)
        {
            var inRound = f.Where(x => x.Round == r).SelectMany(x => new[] { x.EntrantA, x.EntrantB }).ToList();
            Assert.That(inRound.Distinct().Count(), Is.EqualTo(n), "everyone plays once per round");
        }
        for (int i = 0; i < n; i++)
        {
            int seatA = f.Count(x => x.EntrantA == E(i));
            Assert.That(seatA, Is.InRange((n - 1) / 2, n / 2), "seat A is balanced");
        }
        Assert.That(f.Select(x => x.MatchId).Distinct().Count(), Is.EqualTo(f.Count));
    }

    [Test]
    public void Points_AreThreeOneZero_AndExactTiesSharePlace()
    {
        RoundRobinTournament t = Running(4);
        var v = new ScriptedVerifier();
        // Everything draws: everyone has 3 points and shares first place.
        foreach (Fixture f in t.Fixtures) Assert.That(t.RecordResult(f.Index, Rec(f, FixtureOutcome.Draw), v).Accepted, Is.True);
        IReadOnlyList<StandingRow> s = t.Standings();
        Assert.That(s.All(r => r.Points == 3 && r.Draws == 3 && r.Place == 1), Is.True);

        RoundRobinTournament u = Running(4);
        // Seat A always wins.
        foreach (Fixture f in u.Fixtures) u.RecordResult(f.Index, Rec(f, FixtureOutcome.AWins), v);
        foreach (StandingRow r in u.Standings())
        {
            Assert.That(r.Points, Is.EqualTo(3 * r.Wins));
            Assert.That(r.Wins + r.Losses, Is.EqualTo(3));
            Assert.That(r.Place, Is.EqualTo(1 + u.Standings().Count(o => o.Points > r.Points)));
        }
    }

    [Test]
    public void VerifiedResults_AreIdempotentAndBoundToTheFixture()
    {
        RoundRobinTournament t = Running(4);
        var v = new ScriptedVerifier();
        Fixture f0 = t.Fixtures[0], f1 = t.Fixtures[1];
        Assert.That(t.RecordResult(f0.Index, Rec(f0, FixtureOutcome.BWins), v).Accepted, Is.True);
        Assert.That(t.RecordResult(f0.Index, Rec(f0, FixtureOutcome.BWins), v).Accepted, Is.True, "same verified result again is a no-op");
        Assert.That(t.RecordResult(f0.Index, Rec(f0, FixtureOutcome.AWins, "2"), v).Code, Is.EqualTo("ALREADY_RESOLVED"));
        Assert.That(t.RecordResult(f1.Index, Rec(f0, FixtureOutcome.BWins), v).Code, Is.EqualTo("RESULT_REUSED"));
        Assert.That(t.RecordResult(f1.Index, Rec(f0, FixtureOutcome.AWins, "3"), v).Code, Is.EqualTo("WRONG_MATCH"));
        Assert.That(t.RecordResult(f1.Index, "garbage", v).Code, Is.EqualTo("UNVERIFIED"));
        Assert.That(t.Fixtures[0].PointsB, Is.EqualTo(3));
        Assert.That(t.Standings().Single(r => r.Entrant == f0.EntrantB).Points, Is.EqualTo(3));
    }

    [Test]
    public void ReplayVerifier_AcceptsARealRecordAndRejectsATamperedOne()
    {
        RoundRobinTournament t = Running(4);
        Fixture f = t.Fixtures[0];
        BotMatchRunner.SeedFor(77, 1, out byte[] seed, out _);
        MatchEngine engine = BotMatchRunner.Run(t.Publication.MatchConfig, seed, f.MatchId,
            BotPlayer.Create(PlayerSide.A, BotDifficulty.Normal, seed), BotPlayer.Create(PlayerSide.B, BotDifficulty.Normal, seed));
        string json = MatchRecord.FromEngine(engine).ToJson();
        var verifier = new ReplayMatchResultVerifier();
        VerifiedMatchResult? ok = verifier.Verify(json, out string failure);
        Assert.That(ok, Is.Not.Null, failure);
        Assert.That(ok!.MatchId, Is.EqualTo(f.MatchId));
        Assert.That(verifier.Verify(json, out _)!.ResultId, Is.EqualTo(ok.ResultId), "stable result ID");

        string tampered = json.Replace("\"cells_a\":", "\"cells_a\":1");
        Assert.That(verifier.Verify(tampered, out string why), Is.Null);
        Assert.That(why, Is.Not.Empty);
        Assert.That(t.RecordResult(f.Index, tampered, verifier).Code, Is.EqualTo("UNVERIFIED"));
        Assert.That(t.RecordResult(f.Index, json, verifier).Accepted, Is.True);
        int expectedA = engine.Result.Winner == PlayerSide.A ? 3 : engine.Result.Winner == PlayerSide.B ? 0 : 1;
        Assert.That(t.Fixtures[0].PointsA, Is.EqualTo(expectedA));
    }

    [Test]
    public void MissedCheckIn_IsAbsenceFromEveryFixture()
    {
        RoundRobinTournament t = Running(4, skipCheckIn: new[] { 2 });
        foreach (Fixture f in t.Fixtures.Where(x => x.Involves(E(2))))
        {
            Assert.That(f.Status, Is.EqualTo(FixtureStatus.Absence));
            Assert.That(f.EntrantA == E(2) ? f.PointsB : f.PointsA, Is.EqualTo(3), "present entrant wins");
        }
        Assert.That(t.Standings().Single(r => r.Entrant == E(2)).Points, Is.Zero);
    }

    [Test]
    public void NoShow_AfterGraceFollowsTheAbsencePolicy()
    {
        RoundRobinTournament t = Running(4);
        Fixture f = t.Fixtures[0];
        Assert.That(t.RecordAbsence(Operator, f.Index, true, false, f.ScheduledStartMs + 60_000).Code, Is.EqualTo("GRACE"));
        Assert.That(t.RecordAbsence("x", f.Index, true, false, f.ScheduledStartMs + Hour).Code, Is.EqualTo("NOT_OPERATOR"));
        Assert.That(t.RecordAbsence(Operator, f.Index, true, false, f.ScheduledStartMs + 10 * 60_000).Accepted, Is.True);
        Assert.That((t.Fixtures[0].PointsA, t.Fixtures[0].PointsB), Is.EqualTo((0, 3)));
        Fixture g = t.Fixtures[1];
        Assert.That(t.RecordAbsence(Operator, g.Index, true, true, g.ScheduledStartMs + Hour).Accepted, Is.True);
        Assert.That((t.Fixtures[1].PointsA, t.Fixtures[1].PointsB), Is.EqualTo((0, 0)), "double absence: no points");
    }

    [Test]
    public void TechnicalCancellation_ReplaysOnceThenAppliesTheFallback()
    {
        RoundRobinTournament t = Running(4);
        Fixture f = t.Fixtures[0];
        string firstId = f.MatchId;
        Assert.That(t.RecordTechnicalCancellation(Operator, f.Index, "inc-1").Accepted, Is.True);
        Assert.That(t.Fixtures[0].Status, Is.EqualTo(FixtureStatus.Scheduled));
        Assert.That(t.Fixtures[0].MatchId, Is.Not.EqualTo(firstId), "the replay is a new match");
        Assert.That(t.RecordTechnicalCancellation(Operator, f.Index, "inc-1").Accepted, Is.True, "same incident counted once");
        Assert.That(t.Fixtures[0].Replays, Is.EqualTo(1));
        // A result for the old match ID is no longer accepted.
        Assert.That(t.RecordResult(f.Index, firstId + "|AWins|1", new ScriptedVerifier()).Code, Is.EqualTo("WRONG_MATCH"));
        Assert.That(t.RecordTechnicalCancellation(Operator, f.Index, "inc-2").Accepted, Is.True);
        Assert.That(t.Fixtures[0].Status, Is.EqualTo(FixtureStatus.CancellationFallback));
        Assert.That((t.Fixtures[0].PointsA, t.Fixtures[0].PointsB), Is.EqualTo((1, 1)), "published fallback: recorded draw");

        RoundRobinTournament u = Running(4, cancel: new TechnicalCancellationPolicy(0, CancellationFallback.NoResult));
        u.RecordTechnicalCancellation(Operator, 0, "inc-9");
        Assert.That((u.Fixtures[0].PointsA, u.Fixtures[0].PointsB), Is.EqualTo((0, 0)));
    }

    [Test]
    public void Disputes_AreAuditedAndBlockFinalization()
    {
        RoundRobinTournament t = Running(4);
        var v = new ScriptedVerifier();
        foreach (Fixture f in t.Fixtures) t.RecordResult(f.Index, Rec(f, FixtureOutcome.AWins), v);
        Fixture target = t.Fixtures[0];
        string outsider = Enumerable.Range(0, 4).Select(E).First(e => !target.Involves(e));
        Assert.That(t.OpenDispute(outsider, 0, "unfair", T0, out _).Code, Is.EqualTo("NOT_PARTICIPANT"));
        Assert.That(t.OpenDispute(target.EntrantB, 0, "disconnect during volley 2", T0, out string id).Accepted, Is.True);
        Assert.That(t.FinalizeAndAward(Operator, new CosmeticGrantLedger(), out _).Code, Is.EqualTo("OPEN_DISPUTE"));
        Assert.That(t.ResolveDispute("x", id, true, 1, 1, "n", T0).Code, Is.EqualTo("NOT_OPERATOR"));
        Assert.That(t.ResolveDispute(Operator, id, true, 2, 2, "n", T0).Code, Is.EqualTo("POINTS"));
        Assert.That(t.ResolveDispute(Operator, id, true, 1, 1, "verified service fault; recorded draw", T0 + 5).Accepted, Is.True);
        DisputeRecord d = t.Disputes.Single();
        Assert.That(d.Status, Is.EqualTo(DisputeStatus.Upheld));
        Assert.That((d.PreviousPointsA, d.PreviousPointsB), Is.EqualTo((3, 0)), "audit keeps the previous result");
        Assert.That((t.Fixtures[0].PointsA, t.Fixtures[0].PointsB), Is.EqualTo((1, 1)));
        Assert.That(t.ResolveDispute(Operator, id, false, null, null, "again", T0).Code, Is.EqualTo("DECIDED"));
        Assert.That(t.FinalizeAndAward(Operator, new CosmeticGrantLedger(), out _).Accepted, Is.True);
    }

    [Test]
    public void Finalize_AwardsEachCosmeticOnceUnderTheVerifiedResultId()
    {
        RoundRobinTournament t = Running(4);
        var v = new ScriptedVerifier();
        foreach (Fixture f in t.Fixtures) t.RecordResult(f.Index, Rec(f, FixtureOutcome.Draw), v);
        var ledger = new CosmeticGrantLedger();
        Assert.That(t.FinalizeAndAward("x", ledger, out _).Code, Is.EqualTo("NOT_OPERATOR"));
        Assert.That(t.FinalizeAndAward(Operator, ledger, out string id1).Accepted, Is.True);
        Assert.That(id1, Has.Length.EqualTo(64));
        // All four share first place, so all four get the champion banner and the badge.
        Assert.That(ledger.All, Has.Count.EqualTo(8));
        Assert.That(ledger.All.All(g => g.GrantId.StartsWith(id1, StringComparison.Ordinal)), Is.True);
        Assert.That(t.FinalizeAndAward(Operator, ledger, out string id2).Accepted, Is.True);
        Assert.That(id2, Is.EqualTo(id1));
        Assert.That(ledger.All, Has.Count.EqualTo(8), "no double grants");
        Assert.That(t.RecordResult(0, "late|AWins|1", v).Code, Is.EqualTo("WRONG_STAGE"));
    }

    [Test]
    public void Finalize_RequiresEveryFixtureResolved()
    {
        RoundRobinTournament t = Running(4);
        Assert.That(t.FinalizeAndAward(Operator, new CosmeticGrantLedger(), out _).Code, Is.EqualTo("UNRESOLVED"));
    }

    [Test]
    public void Ledger_IsIdempotentUnderConcurrency()
    {
        var ledger = new CosmeticGrantLedger();
        int created = 0;
        Parallel.For(0, 1000, i =>
        {
            if (ledger.Grant("grant-" + (i % 10), "acct", "c")) Interlocked.Increment(ref created);
        });
        Assert.That(created, Is.EqualTo(10));
        Assert.That(ledger.All, Has.Count.EqualTo(10));
    }
}
