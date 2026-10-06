using AstraKingdoms.Modes.Realtime;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;

namespace AstraKingdoms.Modes.Tests.Realtime;

/// <summary>Plan: "Prototype flying/moving, aiming and shooting against a bot on one phone first ... outside ranked progression".</summary>
public class RealtimePrototypeTests
{
    [Test]
    public void Prototype_IsLabelledBotOnlyAndOutsideRankedAndRewards()
    {
        Assert.That(RealtimePrototypeRules.PrototypeId, Does.Contain("PROTO"));
        Assert.That(RealtimePrototypeRules.IsRanked, Is.False);
        Assert.That(RealtimePrototypeRules.GrantsRewards, Is.False);
        Assert.That(RealtimePrototypeRules.BotOnly, Is.True);
    }

    [Test]
    public void CircleTrig_MatchesTheTableBySymmetry()
    {
        for (int a = -360; a <= 360; a += 7)
        {
            Assert.That(CircleTrig.Sin(a), Is.EqualTo(TrigTable.Sin(a)));
            Assert.That(CircleTrig.Cos(a), Is.EqualTo(TrigTable.Cos(a)));
        }
        Assert.That(CircleTrig.Cos(720), Is.EqualTo(-Fixed.One));
        Assert.That(CircleTrig.Sin(720), Is.EqualTo(Fixed.Zero));
        Assert.That(CircleTrig.Sin(1080), Is.EqualTo(-Fixed.One));
        Assert.That(CircleTrig.Sin(1440 + 100), Is.EqualTo(CircleTrig.Sin(100)));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(200)]
    [TestCase(360)]
    [TestCase(500)]
    [TestCase(719)]
    [TestCase(900)]
    [TestCase(1080)]
    [TestCase(1300)]
    public void Atan2_InvertsTheCircleTrig(int qdeg)
    {
        Fixed r = Fixed.FromInt(10);
        int back = CircleTrig.Atan2Qdeg(CircleTrig.Sin(qdeg) * r, CircleTrig.Cos(qdeg) * r);
        int diff = Math.Abs(back - qdeg);
        Assert.That(Math.Min(diff, 1440 - diff), Is.LessThanOrEqualTo(1));
    }

    [Test]
    public void SweptHitTest_CatchesFastArrowsThatPassThrough()
    {
        var centre = new FixedVector3(Fixed.Zero, Fixed.FromInt(3), Fixed.Zero);
        Fixed r = RealtimePrototypeRules.HitRadius;
        // Both endpoints are outside the sphere but the segment crosses it.
        var a = new FixedVector3(Fixed.FromInt(-2), Fixed.FromInt(3), Fixed.Zero);
        var b = new FixedVector3(Fixed.FromInt(2), Fixed.FromInt(3), Fixed.Zero);
        Assert.That(RealtimeSimulation.SegmentHitsSphere(a, b, centre, r), Is.True);
        var c = new FixedVector3(Fixed.FromInt(-2), Fixed.FromInt(5), Fixed.Zero);
        var d = new FixedVector3(Fixed.FromInt(2), Fixed.FromInt(5), Fixed.Zero);
        Assert.That(RealtimeSimulation.SegmentHitsSphere(c, d, centre, r), Is.False);
    }

    [Test]
    public void Movement_IsClampedToTheArenaAndAltitudeBand()
    {
        var sim = new RealtimeSimulation();
        while (!sim.IsOver) sim.Step(new RealtimeInput(-1, -1, 1, 0, 0, false), new RealtimeInput(1, 1, -1, 0, 0, false));
        Assert.That(sim[0].Position.X, Is.EqualTo(-RealtimePrototypeRules.ArenaHalfWidth));
        Assert.That(sim[0].Position.Y, Is.EqualTo(RealtimePrototypeRules.MaxAltitude));
        Assert.That(sim[1].Position.Z, Is.EqualTo(RealtimePrototypeRules.ArenaHalfWidth));
        Assert.That(sim[1].Position.Y, Is.EqualTo(RealtimePrototypeRules.MinAltitude));
        Assert.That(sim.Outcome, Is.EqualTo(RealtimeOutcome.Draw), "time limit with equal HP is a draw");
        Assert.That(sim.Tick, Is.EqualTo(RealtimePrototypeRules.MaxTicks));
    }

    [Test]
    public void Bot_HitsAStationaryTargetAndWins()
    {
        var idle = new IdleController();
        RealtimeSimulation sim = RealtimeSimulation.Run(idle, new RealtimeBot(BotDifficulty.Hard, 1));
        Assert.That(sim.Outcome, Is.EqualTo(RealtimeOutcome.Archer1Wins));
        Assert.That(sim[1].Hits, Is.GreaterThanOrEqualTo(5));
        Assert.That(sim.Tick, Is.LessThan(RealtimePrototypeRules.MaxTicks));
    }

    [Test]
    public void Bot_LeadsAMovingTarget()
    {
        RealtimeSimulation sim = RealtimeSimulation.Run(new ScriptedDodger(90), new RealtimeBot(BotDifficulty.Hard, 2));
        Assert.That(sim[1].Hits, Is.GreaterThan(0), "lead aim lands hits on a target moving on a fixed pattern");
        Assert.That(sim[1].ShotsFired, Is.GreaterThanOrEqualTo(sim[1].Hits));
        Assert.That(sim.Outcome, Is.EqualTo(RealtimeOutcome.Archer1Wins));
    }

    [Test]
    public void Difficulty_ChangesAccuracyNotDamage()
    {
        int HitsFor(BotDifficulty d)
        {
            int hits = 0;
            for (ulong seed = 1; seed <= 4; seed++)
            {
                RealtimeSimulation s = RealtimeSimulation.Run(new ScriptedDodger(120), new RealtimeBot(d, seed));
                hits += s[1].Hits;
            }
            return hits;
        }
        Assert.That(HitsFor(BotDifficulty.Hard), Is.GreaterThanOrEqualTo(HitsFor(BotDifficulty.Easy)));
    }

    [Test]
    public void BotVsBot_IsDeterministicAndReplaysFromItsInputLog()
    {
        RealtimeSimulation a = RealtimeSimulation.Run(new RealtimeBot(BotDifficulty.Normal, 7), new RealtimeBot(BotDifficulty.Normal, 8));
        RealtimeSimulation b = RealtimeSimulation.Run(new RealtimeBot(BotDifficulty.Normal, 7), new RealtimeBot(BotDifficulty.Normal, 8));
        Assert.That(a.IsOver, Is.True);
        Assert.That(b.StateHashHex(), Is.EqualTo(a.StateHashHex()));
        Assert.That(b.Tick, Is.EqualTo(a.Tick));
        RealtimeSimulation replay = RealtimeSimulation.Replay(a.InputLog);
        Assert.That(replay.StateHashHex(), Is.EqualTo(a.StateHashHex()));
        Assert.That(replay.Outcome, Is.EqualTo(a.Outcome));
        Assert.Throws<InvalidOperationException>(() => a.Step(RealtimeInput.Idle, RealtimeInput.Idle));
    }

    private sealed class IdleController : IRealtimeController
    {
        public RealtimeInput Decide(RealtimeObservation observation) => RealtimeInput.Idle;
    }
}
