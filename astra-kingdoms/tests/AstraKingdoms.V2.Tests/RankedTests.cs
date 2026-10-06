using System.Reflection;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Rules.Balance;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.V2.Integration;
using AstraKingdoms.V2.Ranked;
using AstraKingdoms.V2.Seasons;
using NUnit.Framework;

namespace AstraKingdoms.V2.Tests;

/// <summary>V2 ranked seasons: expected score, bounds, reset rules, exclusions, idempotency, matchmaking.</summary>
public class RankedTests
{
    private static readonly RankedRules R = RankedRules.Default;

    [Test]
    public void TheExpectedScoreTableMatchesTheClosedForm()
    {
        for (int d = 0; d <= 800; d += 25)
        {
            double exact = 1e6 / (1 + Math.Pow(10, d / 400.0));
            Assert.That(EloRating.LowerExpectedPpm[d / 25], Is.EqualTo(exact).Within(0.51), "d=" + d);
        }
        for (int d = 0; d <= 1000; d += 7)
        {
            double exact = 1e6 / (1 + Math.Pow(10, Math.Min(d, 800) / 400.0));
            Assert.That(EloRating.ExpectedPpm(1000, 1000 + d), Is.EqualTo(exact).Within(1500), "interpolated d=" + d);
            Assert.That(EloRating.ExpectedPpm(1000 + d, 1000) + EloRating.ExpectedPpm(1000, 1000 + d), Is.EqualTo(EloRating.Scale), "symmetric");
        }
        Assert.That(EloRating.ExpectedPpm(1200, 1200), Is.EqualTo(500000));
    }

    [Test]
    public void DeltasAreBoundedAndDecisive()
    {
        for (int a = 0; a <= 2400; a += 37)
            for (int b = 0; b <= 2400; b += 53)
                foreach (RatedOutcome o in Enum.GetValues<RatedOutcome>())
                    foreach (int k in new[] { R.K, R.PlacementK, 500 })
                    {
                        int d = EloRating.Delta(a, b, o, k, R);
                        Assert.That(Math.Abs(d), Is.LessThanOrEqualTo(R.MaxDeltaPerMatch));
                        if (o == RatedOutcome.Win) Assert.That(d, Is.GreaterThanOrEqualTo(R.MinDecisiveDelta));
                        if (o == RatedOutcome.Loss) Assert.That(d, Is.LessThanOrEqualTo(-R.MinDecisiveDelta));
                    }
        Assert.That(EloRating.Delta(1000, 1000, RatedOutcome.Win, 24, R), Is.EqualTo(12));
        Assert.That(EloRating.Delta(1000, 1000, RatedOutcome.Draw, 24, R), Is.EqualTo(0));
        Assert.That(EloRating.Delta(1400, 1000, RatedOutcome.Win, 24, R), Is.EqualTo(2), "expected 0.909 -> 24*0.091 = 2.2");
        Assert.That(EloRating.Delta(1000, 1400, RatedOutcome.Win, 24, R), Is.EqualTo(22));
    }

    [Test]
    public void LeaguesAndSoftResetMoveDownAtMostOne()
    {
        Assert.That(R.LeagueFor(0), Is.EqualTo(League.Bronze));
        Assert.That(R.LeagueFor(1100), Is.EqualTo(League.Silver));
        Assert.That(R.LeagueFor(1250), Is.EqualTo(League.Gold));
        Assert.That(R.LeagueFor(5000), Is.EqualTo(League.Diamond));
        for (int rating = 0; rating <= 3000; rating++)
        {
            int reset = R.SoftResetRating(rating);
            League before = R.LeagueFor(rating), after = R.LeagueFor(reset);
            Assert.That(before - after, Is.InRange(0, 1), "rating " + rating);
            if (before != League.Bronze) Assert.That(after, Is.EqualTo((League)(before - 1)));
            Assert.That(reset, Is.LessThanOrEqualTo(rating));
        }
        Assert.That(R.SoftResetRating(1500), Is.EqualTo(1250));
        Assert.That(R.SoftResetRating(1120), Is.EqualTo(1000));
        Assert.That(R.SoftResetRating(950), Is.EqualTo(950));
    }

    private static (V2Environment env, ManualClock clock, RankedMatchmaker mm, SeasonDefinition s) Open()
    {
        V2Environment env = TestUtil.Env(out ManualClock clock);
        SeasonDefinition s = env.Calendar.Seasons[0];
        clock.Set(s.StartsAt + TimeSpan.FromHours(1));
        return (env, clock, env.Matchmaker(s.Id), s);
    }

    /// <summary>Queues two players and waits two minutes so the skill window (350) admits them.</summary>
    private static RankedMatchTicket Pair(V2Environment env, RankedMatchmaker mm, string a, string b)
    {
        string snap = env.Snapshots.Current(mm.Season.Id).SnapshotId;
        Assert.That(mm.Enqueue(a, snap), Is.EqualTo(EnqueueStatus.Queued));
        Assert.That(mm.Enqueue(b, snap), Is.EqualTo(EnqueueStatus.Queued));
        ((ManualClock)env.Clock).Advance(TimeSpan.FromMinutes(2));
        return mm.Tick().Single();
    }

    private static RankedMatchResult Result(RankedMatchTicket t, PlayerSide? winner, DateTimeOffset at, MatchEnding ending = MatchEnding.RoundsComplete, QueueKind q = QueueKind.Ranked) =>
        new(t.MatchId, t.SeasonId, q, t.SnapshotId, t.PlayerA, t.PlayerB, winner, ending, at);

    [Test]
    public void OnlyRankedResultsCountAndEachMatchCountsOnce()
    {
        var (env, clock, mm, s) = Open();
        RankedMatchTicket t = Pair(env, mm, "a", "b");
        Assert.That(env.Ranked.Record(Result(t, PlayerSide.A, clock.UtcNow, q: QueueKind.Casual)).Status, Is.EqualTo(ResultStatus.ExcludedNotRanked));
        Assert.That(env.Ranked.Record(Result(t, PlayerSide.A, clock.UtcNow, q: QueueKind.Practice)).Status, Is.EqualTo(ResultStatus.ExcludedNotRanked));
        Assert.That(env.Ranked.Record(Result(t, PlayerSide.A, clock.UtcNow, q: QueueKind.FriendRoom)).Status, Is.EqualTo(ResultStatus.ExcludedNotRanked));
        Assert.That(env.Ranked.Skill("a").Rating, Is.EqualTo(R.StartRating));
        ProcessedResult first = env.Ranked.Record(Result(t, PlayerSide.A, clock.UtcNow));
        Assert.That(first.Status, Is.EqualTo(ResultStatus.Rated));
        Assert.That(first.DeltaA, Is.EqualTo(24), "placement K 48 at equal ratings");
        ProcessedResult again = env.Ranked.Record(Result(t, PlayerSide.B, clock.UtcNow));
        Assert.That(again.Status, Is.EqualTo(ResultStatus.Duplicate));
        Assert.That(again.DeltaA, Is.EqualTo(first.DeltaA), "a duplicate returns the original outcome");
        Assert.That(env.Ranked.Skill("a").Rating, Is.EqualTo(R.StartRating + 24));
        Assert.That(env.Ranked.Standing(s.Id, "a").Matches, Is.EqualTo(1));
    }

    [Test]
    public void ConcurrentDuplicateDeliveriesApplyOnce()
    {
        var (env, clock, mm, _) = Open();
        RankedMatchTicket t = Pair(env, mm, "a", "b");
        var statuses = new System.Collections.Concurrent.ConcurrentBag<ResultStatus>();
        Parallel.For(0, 64, _ => statuses.Add(env.Ranked.Record(Result(t, PlayerSide.A, clock.UtcNow)).Status));
        Assert.That(statuses.Count(x => x == ResultStatus.Rated), Is.EqualTo(1));
        Assert.That(env.Ranked.Skill("a").RankedMatches, Is.EqualTo(1));
    }

    [Test]
    public void ForgedMismatchedAndAbortedResultsDoNotRate()
    {
        var (env, clock, mm, s) = Open();
        RankedMatchTicket t = Pair(env, mm, "a", "b");
        Assert.That(env.Ranked.Record(new RankedMatchResult("forged", s.Id, QueueKind.Ranked, t.SnapshotId, "a", "b", PlayerSide.A, MatchEnding.RoundsComplete, clock.UtcNow)).Status,
            Is.EqualTo(ResultStatus.UnknownMatch));
        Assert.That(env.Ranked.Record(new RankedMatchResult(t.MatchId, s.Id, QueueKind.Ranked, t.SnapshotId, "a", "c", PlayerSide.A, MatchEnding.RoundsComplete, clock.UtcNow)).Status,
            Is.EqualTo(ResultStatus.TicketMismatch));
        Assert.That(env.Ranked.Record(new RankedMatchResult(t.MatchId, s.Id, QueueKind.Ranked, "S1.snap9", "a", "b", PlayerSide.A, MatchEnding.RoundsComplete, clock.UtcNow)).Status,
            Is.EqualTo(ResultStatus.TicketMismatch));
        Assert.That(env.Ranked.Record(new RankedMatchResult(t.MatchId, "S7", QueueKind.Ranked, t.SnapshotId, "a", "b", PlayerSide.A, MatchEnding.RoundsComplete, clock.UtcNow)).Status,
            Is.EqualTo(ResultStatus.UnknownSeason));
        Assert.That(env.Ranked.Record(Result(t, PlayerSide.A, clock.UtcNow, MatchEnding.TechnicalAbort)).Status, Is.EqualTo(ResultStatus.Cancelled));
        Assert.That(env.Ranked.Skill("a").Rating, Is.EqualTo(R.StartRating));
        Assert.That(env.RankedStore.Unresolved(s.Id), Is.Empty, "a cancelled match is resolved");
        RankedMatchTicket t2 = Pair(env, mm, "a", "b");
        Assert.That(env.Ranked.Record(new RankedMatchResult(t2.MatchId, s.Id, QueueKind.Ranked, t2.SnapshotId, "a", "b", PlayerSide.A, MatchEnding.RoundsComplete, clock.UtcNow, isValid: false)).Status,
            Is.EqualTo(ResultStatus.Cancelled));
    }

    [Test]
    public void ForfeitsAreRatedAsLossesForTheForfeitingSide()
    {
        var (env, clock, mm, _) = Open();
        RankedMatchTicket t = Pair(env, mm, "a", "b");
        ProcessedResult r = env.Ranked.Record(Result(t, PlayerSide.B, clock.UtcNow, MatchEnding.TimeoutForfeit));
        Assert.That(r.Status, Is.EqualTo(ResultStatus.Rated));
        Assert.That(r.DeltaA, Is.Negative);
    }

    [Test]
    public void TheRatingInputCarriesNoDamageMoneyOrAdData()
    {
        string[] forbidden = { "Damage", "Hp", "Coin", "Money", "Price", "Purchase", "Cells", "Spend", "Ads", "Advert", "Reward" };
        foreach (PropertyInfo p in typeof(RankedMatchResult).GetProperties())
            foreach (string f in forbidden)
                Assert.That(p.Name, Does.Not.Contain(f), "rating input property " + p.Name);
        Assert.That(typeof(EloRating).GetMethod(nameof(EloRating.Delta))!.GetParameters().Select(p => p.ParameterType),
            Is.EqualTo(new[] { typeof(int), typeof(int), typeof(RatedOutcome), typeof(int), typeof(RankedRules) }));
    }

    [Test]
    public void PlacementHidesTheLeagueForFiveMatchesAndThereIsNoDecay()
    {
        var (env, clock, mm, s) = Open();
        for (int i = 0; i < 5; i++)
        {
            Assert.That(env.Ranked.Standing(s.Id, "a").VisibleLeague(R), Is.Null);
            RankedMatchTicket t = Pair(env, mm, "a", "b" + i);
            env.Ranked.Record(Result(t, PlayerSide.A, clock.UtcNow));
        }
        SeasonStanding st = env.Ranked.Standing(s.Id, "a");
        Assert.That(st.PlacementDone(R), Is.True);
        Assert.That(st.VisibleLeague(R), Is.Not.Null);
        int rating = st.SeasonRating, skill = env.Ranked.Skill("a").Rating;
        clock.Advance(TimeSpan.FromDays(16));
        Assert.That(env.Ranked.Standing(s.Id, "a").SeasonRating, Is.EqualTo(rating), "no absence decay");
        Assert.That(env.Ranked.Skill("a").Rating, Is.EqualTo(skill));
    }

    [Test]
    public void TheNextSeasonStartsFromTheSoftResetAndKeepsSkill()
    {
        V2Environment env = TestUtil.Env(out ManualClock clock, seasons: 3);
        SeasonDefinition s1 = env.Calendar.Seasons[0], s2 = env.Calendar.Seasons[1], s3 = env.Calendar.Seasons[2];
        clock.Set(s1.StartsAt + TimeSpan.FromHours(1));
        RankedMatchmaker mm = env.Matchmaker(s1.Id);
        // Push "a" into Gold by beating many fresh accounts.
        for (int i = 0; env.Ranked.Standing(s1.Id, "a").SeasonRating < R.GoldFrom; i++)
            env.Ranked.Record(Result(Pair(env, mm, "a", "x" + i), PlayerSide.A, clock.UtcNow));
        int s1Rating = env.Ranked.Standing(s1.Id, "a").SeasonRating;
        int skill = env.Ranked.Skill("a").Rating;
        Assert.That(R.LeagueFor(s1Rating), Is.EqualTo(League.Gold));
        SeasonStanding s2Start = env.Ranked.Standing(s2.Id, "a");
        Assert.That(s2Start.SeasonRating, Is.EqualTo(R.SilverFrom), "Gold -> Silver lower bound");
        Assert.That(env.Ranked.Skill("a").Rating, Is.EqualTo(skill), "hidden skill retained for matchmaking");
        // Skipping season 2 entirely: season 3 resets from season 1 once, not twice.
        Assert.That(env.Ranked.Standing(s3.Id, "a").SeasonRating, Is.EqualTo(R.SilverFrom));
        Assert.That(env.Ranked.Standing(s2.Id, "new").SeasonRating, Is.EqualTo(R.StartRating));
    }

    [Test]
    public void MatchmakingClosesBeforeTheBoundaryByTheMeasuredMaximum()
    {
        var (env, clock, mm, s) = Open();
        Assert.That(s.EndsAt - s.MatchmakingClosesAt, Is.EqualTo(TimeSpan.FromMinutes(8) + SeasonRules.Default.MatchmakingSafetyMargin));
        Assert.That(s.EndsAt - s.StartsAt, Is.EqualTo(TimeSpan.FromDays(18)));
        string snap = env.Snapshots.Current(s.Id).SnapshotId;
        clock.Set(s.MatchmakingClosesAt - TimeSpan.FromSeconds(1));
        Assert.That(mm.Enqueue("a", snap), Is.EqualTo(EnqueueStatus.Queued));
        clock.Set(s.MatchmakingClosesAt);
        Assert.That(mm.Enqueue("b", snap), Is.EqualTo(EnqueueStatus.QueueClosed));
        Assert.That(mm.Tick(out IReadOnlyList<string> closedOut), Is.Empty);
        Assert.That(closedOut, Is.EqualTo(new[] { "a" }), "queued players are told the queue closed");
        // Without a measurement the rules-clock bound applies.
        var unmeasured = new SeasonDefinition(1, TestUtil.Start, SeasonRules.Default, null);
        Assert.That(unmeasured.EndsAt - unmeasured.MatchmakingClosesAt, Is.EqualTo(MatchDurationBudget.RulesClockUpperBound() + SeasonRules.Default.MatchmakingSafetyMargin));
        Assert.That(MatchDurationBudget.RulesClockUpperBound(), Is.EqualTo(TimeSpan.FromSeconds(460)));
    }

    [Test]
    public void EntryRequiresTheCurrentSnapshotToBeAcknowledged()
    {
        var (env, clock, mm, s) = Open();
        Assert.That(mm.Enqueue("a", "S1.snap0"), Is.EqualTo(EnqueueStatus.SnapshotNotAcknowledged));
        RankedEntryView view = RankedEntryView.Build(mm, env.Ranked, env.Snapshots, "a", clock.UtcNow);
        Assert.That(view.Snapshot.EligibleWeaponIds, Has.Count.EqualTo(20), "normalised catalogue: everyone gets the same eligible weapons");
        Assert.That(view.Snapshot.MaxSlots, Is.EqualTo(RulesConstants.FullMaxSlots));
        Assert.That(view.Snapshot.ShortHash, Has.Length.EqualTo(12));
        Assert.That(view.QueueOpen, Is.True);
        Assert.That(mm.Enqueue("a", view.Snapshot.SnapshotId), Is.EqualTo(EnqueueStatus.Queued));
        Assert.That(mm.Enqueue("a", view.Snapshot.SnapshotId), Is.EqualTo(EnqueueStatus.AlreadyQueued));
    }

    [Test]
    public void SnapshotsAreFrozenExceptThroughADocumentedEmergencyChange()
    {
        var (env, clock, mm, s) = Open();
        SeasonBalanceSnapshot first = env.Snapshots.Current(s.Id);
        Assert.That(first.Validate(), Is.Empty);
        var tuned = new BalanceBundle("AK-TR-1.b9", "AK-TR-1", null, "AK-TR-1", "exploit fix");
        var replacement = new SeasonBalanceSnapshot("S1.snap2", s.Id, tuned, R, clock.UtcNow);
        Assert.That(env.Snapshots.Freeze(replacement), Is.EqualTo(SnapshotChangeStatus.Frozen));
        Assert.That(env.Snapshots.ApplyEmergencyChange(replacement, new EmergencyChangeRecord("INC-1", "owner", "exploit", "", first.SnapshotId, "S1.snap2", clock.UtcNow)),
            Is.EqualTo(SnapshotChangeStatus.InvalidRecord), "a player explanation is required");
        RankedMatchTicket before = Pair(env, mm, "a", "b");
        Assert.That(env.Snapshots.ApplyEmergencyChange(replacement, new EmergencyChangeRecord("INC-1", "owner", "exploit", "We fixed an exploit in ...", first.SnapshotId, "S1.snap2", clock.UtcNow)),
            Is.EqualTo(SnapshotChangeStatus.Applied));
        Assert.That(env.Snapshots.Current(s.Id).SnapshotId, Is.EqualTo("S1.snap2"));
        Assert.That(env.Audit.Entries.Any(e => e.Action == "snapshot.emergency" && e.Detail.Contains("INC-1")), Is.True);
        Assert.That(env.Ranked.Record(Result(before, PlayerSide.A, clock.UtcNow)).Status, Is.EqualTo(ResultStatus.Rated), "a match created under the earlier snapshot still counts");
        Assert.That(mm.Enqueue("c", first.SnapshotId), Is.EqualTo(EnqueueStatus.SnapshotNotAcknowledged), "players must review the changed snapshot");
        Assert.That(RankedEntryView.Build(mm, env.Ranked, env.Snapshots, "c", clock.UtcNow).EmergencyChanges, Has.Count.EqualTo(1));
    }

    [Test]
    public void CoordinatedGroupsNeverMeetSoloPlayersOrEachOther()
    {
        var (env, clock, mm, s) = Open();
        string snap = env.Snapshots.Current(s.Id).SnapshotId;
        mm.Enqueue("solo1", snap);
        mm.Enqueue("g1a", snap, "g1");
        mm.Enqueue("g1b", snap, "g1");
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.That(mm.Tick(), Is.Empty, "the only possible partners are the solo player or a group mate");
        mm.Enqueue("solo2", snap);
        mm.Enqueue("g2a", snap, "g2");
        IReadOnlyList<RankedMatchTicket> made = mm.Tick();
        Assert.That(made, Has.Count.EqualTo(2));
        RankedMatchTicket solo = made.Single(t => !t.GroupMatch), group = made.Single(t => t.GroupMatch);
        Assert.That(new[] { solo.PlayerA, solo.PlayerB }, Is.EquivalentTo(new[] { "solo1", "solo2" }));
        Assert.That(new[] { group.PlayerA, group.PlayerB }, Does.Contain("g2a"));
    }

    [Test]
    public void TheSkillWindowWidensWithWaitingAndBlockedPairsAreNeverMatched()
    {
        V2Environment env = TestUtil.Env(out ManualClock clock);
        SeasonDefinition s = env.Calendar.Seasons[0];
        clock.Set(s.StartsAt + TimeSpan.FromHours(1));
        env.RankedStore.SetSkill(new PlayerSkill("hi", 1300, 50));
        var relations = new Blocks(("x", "y"));
        var mm = new RankedMatchmaker(s, env.Ranked, env.Snapshots, clock, null, relations);
        string snap = env.Snapshots.Current(s.Id).SnapshotId;
        mm.Enqueue("hi", snap);
        mm.Enqueue("lo", snap);
        Assert.That(mm.Tick(), Is.Empty, "300 apart");
        clock.Advance(TimeSpan.FromSeconds(100));
        Assert.That(mm.Tick(), Has.Count.EqualTo(1), "window 50 + 10 steps x 25 = 300");
        mm.Enqueue("x", snap);
        mm.Enqueue("y", snap);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.That(mm.Tick(), Is.Empty);
        Assert.That(new MatchmakingRules(50, 25, TimeSpan.FromSeconds(10), 400).WindowAfter(TimeSpan.FromHours(1)), Is.EqualTo(400));
    }

    private sealed class Blocks : Common.ISocialRelations
    {
        private readonly (string, string)[] _pairs;
        public Blocks(params (string, string)[] pairs) => _pairs = pairs;
        public bool AreFriends(string a, string b) => false;
        public bool AreClanmates(string a, string b) => false;
        public bool IsBlockedEitherWay(string a, string b) => _pairs.Any(p => (p.Item1 == a && p.Item2 == b) || (p.Item1 == b && p.Item2 == a));
    }
}
