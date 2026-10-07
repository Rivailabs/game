using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Tests.Combat;

public class FlightSimulatorTests
{
    private static readonly TargetPose StandA = new(PlayerSide.A, Fixed.Zero, Dodge.None);
    private static readonly TargetPose StandB = new(PlayerSide.B, Fixed.Zero, Dodge.None);

    private static ProjectileSpec Custom(PlayerSide owner, int index, int mass, int radiusMm, FixedVector3 pos, FixedVector3 vel,
        bool gravity = false, bool canClash = true, bool burst = false, int damage = 3000) =>
        new(new ProjectileId(1, 1, owner, index), 1, Element.Agni, damage, mass, Fixed.FromMillimetres(radiusMm), pos, vel,
            gravity, canClash, burst, jumpPierce: false, curveAccelZ: Fixed.Zero, curveLastTick: 0);

    private static FixedVector3 V(int xCm, int yCm, int zCm) => new(Kit.M(xCm), Kit.M(yCm), Kit.M(zCm));

    private static List<CombatEvent> Of(SimulationResult r, CombatEventType type) =>
        r.Log.Events.Where(e => e.Type == type).ToList();

    // Acceptance: "Heavy clash" — mass 3 contacts mass 1.
    [Test]
    public void HeavyClash_Mass3SurvivesWithMass2AndHalfVelocity()
    {
        var heavy = Custom(PlayerSide.A, 0, 3, 60, V(200, 100, 0), V(1000, 0, 0), damage: 3500);
        var light = Custom(PlayerSide.B, 0, 1, 40, V(600, 100, 0), V(-1000, 0, 0));
        var r = FlightSimulator.Simulate(new[] { heavy, light }, StandA, StandB);

        var survived = Of(r, CombatEventType.ClashSurvived).Single();
        Assert.That(survived.Projectile, Is.EqualTo(heavy.Id));
        Assert.That(survived.Mass, Is.EqualTo(2));
        Assert.That(survived.Velocity, Is.EqualTo(V(500, 0, 0)), "entire velocity vector halved");
        Assert.That(Of(r, CombatEventType.ClashDestroyed).Single().Projectile, Is.EqualTo(light.Id));

        // Damage and element are unchanged; the survivor continues and still reaches B's plane.
        var track = r.Tracks.Single(t => t.Spec.Id.Equals(heavy.Id));
        Assert.That(track.FinalMass, Is.EqualTo(2));
        Assert.That(track.Spec.DamageUnits, Is.EqualTo(3500));
        var contact = r.Contacts.Single();
        Assert.That(contact.Projectile, Is.EqualTo(heavy.Id));
        Assert.That(contact.Kind, Is.EqualTo(ContactKind.Core));
        // After the clash it moves at 5 m/s, so it arrives later than it would have at 10 m/s.
        Assert.That(contact.TimeSubTicks, Is.GreaterThan(survived.TimeSubTicks));
    }

    [Test]
    public void SimultaneousComponent_EqualSideTotalsRemoveEverything()
    {
        // One mass-3 A projectile touches three mass-1 B projectiles at exactly the same sub-tick.
        var heavy = Custom(PlayerSide.A, 0, 3, 60, V(200, 100, 0), V(1000, 0, 0));
        var b0 = Custom(PlayerSide.B, 0, 1, 40, V(600, 100, 5), V(-1000, 0, 0));
        var b1 = Custom(PlayerSide.B, 1, 1, 40, V(600, 100, -5), V(-1000, 0, 0));
        var b2 = Custom(PlayerSide.B, 2, 1, 40, V(600, 105, 0), V(-1000, 0, 0));
        var r = FlightSimulator.Simulate(new[] { b2, heavy, b0, b1 }, StandA, StandB);

        var destroyed = Of(r, CombatEventType.ClashDestroyed);
        Assert.That(destroyed, Has.Count.EqualTo(4));
        Assert.That(destroyed.Select(e => e.TimeSubTicks).Distinct().Count(), Is.EqualTo(1), "one simultaneous component");
        Assert.That(Of(r, CombatEventType.ClashSurvived), Is.Empty);
        Assert.That(r.Contacts, Is.Empty);
    }

    [Test]
    public void SimultaneousComponent_HeavierSideKeepsOneProjectileWithMassDifference()
    {
        // Two light A projectiles (masses 2 and 2, indices 0/1) touch one mass-1 B projectile at the same time.
        var a0 = Custom(PlayerSide.A, 0, 2, 40, V(200, 100, 5), V(1000, 0, 0));
        var a1 = Custom(PlayerSide.A, 1, 2, 40, V(200, 100, -5), V(1000, 0, 0));
        var b0 = Custom(PlayerSide.B, 0, 1, 40, V(600, 100, 0), V(-1000, 0, 0));
        var r = FlightSimulator.Simulate(new[] { a1, b0, a0 }, StandA, StandB);

        var survived = Of(r, CombatEventType.ClashSurvived).Single();
        Assert.That(survived.Projectile.Index, Is.EqualTo(0), "equal masses: smallest index survives");
        Assert.That(survived.Mass, Is.EqualTo(3), "side totals 4 - 1");
        Assert.That(survived.Velocity, Is.EqualTo(V(500, 0, 0)), "velocity halved once");
        Assert.That(Of(r, CombatEventType.ClashDestroyed).Select(e => e.Projectile), Is.EquivalentTo(new[] { a1.Id, b0.Id }));
    }

    [Test]
    public void FriendlyProjectilesNeverCollide()
    {
        var a0 = Custom(PlayerSide.A, 0, 1, 40, V(200, 100, 0), V(1000, 0, 0));
        var a1 = Custom(PlayerSide.A, 1, 1, 40, V(200, 100, 0), V(1000, 0, 0));
        var r = FlightSimulator.Simulate(new[] { a0, a1 }, StandA, StandB);
        Assert.That(Of(r, CombatEventType.ClashDestroyed), Is.Empty);
        Assert.That(r.Contacts, Has.Count.EqualTo(2));
    }

    [Test]
    public void NoTunnelling_HeadOnSunLancesClashAt32MetresPerSecond()
    {
        // Relative motion is 0.267 m per tick, far more than the 0.06 m collision distance.
        var lance = WeaponCatalog.Get(16);
        var specs = LaunchProfiles.BuildPattern(1, 1, PlayerSide.A, lance, 0, 0, 100, Fixed.Zero, false)
            .Concat(LaunchProfiles.BuildPattern(1, 1, PlayerSide.B, lance, 0, 0, 100, Fixed.Zero, false)).ToList();
        var r = FlightSimulator.Simulate(specs, StandA, StandB);
        var destroyed = Of(r, CombatEventType.ClashDestroyed);
        Assert.That(destroyed, Has.Count.EqualTo(2), "equal masses cancel");
        Assert.That(r.Contacts, Is.Empty);
        // They meet near the midpoint x = 4 (within 0.06 m on each side).
        foreach (var e in destroyed)
            Assert.That(Math.Abs(e.Position.X.ToDouble() - 4.0), Is.LessThan(0.04));
    }

    [Test]
    public void NoTunnelling_FastArrowHitsBodyPlaneExactly()
    {
        var lance = WeaponCatalog.Get(16);
        var specs = LaunchProfiles.BuildPattern(1, 1, PlayerSide.A, lance, 0, 0, 100, Fixed.Zero, false);
        var r = FlightSimulator.Simulate(specs, StandA, StandB);
        var hit = Of(r, CombatEventType.BodyContact).Single();
        Assert.That(hit.Contact, Is.EqualTo(ContactKind.Core));
        // Quantized upward to the first 1/65536 tick at or after the crossing: at most one sub-tick past the plane.
        Fixed maxStep = Fixed.FromRatio(16, 120 * 65536) + Fixed.FromRaw(1);
        Assert.That(hit.Position.X >= Fixed.FromInt(8), Is.True);
        Assert.That(hit.Position.X - Fixed.FromInt(8) <= maxStep, Is.True);
    }

    [Test]
    public void Boulder_NeverClashesNorMakesBodyContact()
    {
        // A burst projectile flying straight through B's body height, head-on with an opposing arrow.
        var boulder = Custom(PlayerSide.A, 0, 4, 120, V(35, 100, 0), V(1000, 0, 0), canClash: false, burst: true);
        var arrow = Custom(PlayerSide.B, 0, 1, 40, V(765, 100, 0), V(-1000, 0, 0));
        var r = FlightSimulator.Simulate(new[] { boulder, arrow }, StandA, StandB);
        Assert.That(Of(r, CombatEventType.ClashDestroyed), Is.Empty);
        Assert.That(Of(r, CombatEventType.ClashSurvived), Is.Empty);
        Assert.That(r.Contacts.Any(c => c.Projectile.Equals(boulder.Id)), Is.False);
        Assert.That(r.Tracks.Single(t => t.Spec.Id.Equals(boulder.Id)).Termination, Is.EqualTo(TerminationReason.OutOfBounds));
        // The arrow still hits A.
        Assert.That(r.Contacts.Single().Projectile, Is.EqualTo(arrow.Id));
    }

    [Test]
    public void Boulder_BurstsAtFirstGroundContact()
    {
        var boulder = WeaponCatalog.Get(8);
        var range = LaunchProfiles.CentralPitchRange(boulder);
        int bursts = 0;
        for (int p = range.Min; p <= range.Max; p++)
        {
            var specs = LaunchProfiles.BuildPattern(1, 1, PlayerSide.A, boulder, p, 0, 100, Fixed.Zero, false);
            var r = FlightSimulator.Simulate(specs, StandA, StandB);
            var track = r.Tracks.Single();
            Assert.That(track.Termination, Is.AnyOf(TerminationReason.BurstContact, TerminationReason.BurstMiss, TerminationReason.OutOfBounds));
            Assert.That(Of(r, CombatEventType.BodyContact), Is.Empty);
            if (track.Termination == TerminationReason.BurstContact) bursts++;
        }
        Assert.That(bursts, Is.GreaterThan(0), "some Boulder pitch lands a burst on a standing B");
    }

    [Test]
    public void EmberAimedAtStandingB_FindsACoreHit()
    {
        int? pitch = Kit.FindPitch(PlayerSide.A, 1, StandB, ContactKind.Core);
        Assert.That(pitch, Is.Not.Null);
        // B is symmetric: the same pitch from B hits a standing A.
        Assert.That(Kit.FindPitch(PlayerSide.B, 1, StandA, ContactKind.Core), Is.EqualTo(pitch));
    }

    [Test]
    public void FlatEmberHitsGroundBeforeThePlane()
    {
        var specs = LaunchProfiles.BuildPattern(1, 1, PlayerSide.A, WeaponCatalog.Get(1), 0, 0, 100, Fixed.Zero, false);
        var r = FlightSimulator.Simulate(specs, StandA, StandB);
        var ground = Of(r, CombatEventType.GroundImpact).Single();
        Assert.That(ground.Position.Y <= Fixed.Zero, Is.True);
        Assert.That(ground.Position.X < Fixed.FromInt(8), Is.True);
    }

    [Test]
    public void HighLanceMissesAndLeavesBounds()
    {
        var specs = LaunchProfiles.BuildPattern(1, 1, PlayerSide.A, WeaponCatalog.Get(16), 40, 0, 100, Fixed.Zero, false);
        var r = FlightSimulator.Simulate(specs, StandA, StandB);
        Assert.That(Of(r, CombatEventType.BodyMiss), Has.Count.EqualTo(1));
        Assert.That(r.Tracks.Single().Termination, Is.EqualTo(TerminationReason.OutOfBounds));
        Assert.That(r.Tracks.Single().CrossedTargetPlane, Is.True);
    }

    [Test]
    public void StationaryProjectileExpiresAtTick360()
    {
        var still = Custom(PlayerSide.A, 0, 1, 40, V(400, 300, 0), V(0, 0, 0));
        var r = FlightSimulator.Simulate(new[] { still }, StandA, StandB);
        var e = Of(r, CombatEventType.TickLimit).Single();
        Assert.That(e.Tick, Is.EqualTo(360));
        Assert.That(r.TicksSimulated, Is.EqualTo(360));
    }

    [Test]
    public void TwinGustSecondProjectileCurvesForTicks1To29()
    {
        var twin = WeaponCatalog.Get(7);
        var specs = LaunchProfiles.BuildPattern(1, 1, PlayerSide.B, twin, 40, 0, 100, Fixed.Zero, false);
        Assert.That(specs, Has.Count.EqualTo(2));
        // B's local Right is world -z: yaw +4 deg launches towards -z; the curve accelerates towards +z.
        Assert.That(specs[0].Velocity.Z, Is.EqualTo(Fixed.Zero));
        Assert.That(specs[1].Velocity.Z < Fixed.Zero, Is.True);
        Assert.That(specs[1].CurveAccelZ, Is.EqualTo(Fixed.FromInt(2)));

        var r = FlightSimulator.Simulate(specs, StandA, StandB);
        var last = r.Log.Events.Last(e => e.HasProjectile && e.Projectile.Index == 1 && e.Type != CombatEventType.Launch);
        Assert.That(last.Tick, Is.GreaterThan(29));
        Fixed expected = specs[1].Velocity.Z + Fixed.FromInt(2).DivInt(120).MulInt(29);
        Assert.That(last.Velocity.Z, Is.EqualTo(expected));
    }

    [Test]
    public void FireFanSpawnsThreeOffsetProjectiles()
    {
        var fan = WeaponCatalog.Get(6);
        var specs = LaunchProfiles.BuildPattern(1, 1, PlayerSide.A, fan, 100, 0, 100, Fixed.Zero, false);
        Assert.That(specs.Select(s => s.Index), Is.EqualTo(new[] { 0, 1, 2 }));
        Assert.That(specs[0].Velocity.Y < specs[1].Velocity.Y && specs[1].Velocity.Y < specs[2].Velocity.Y, Is.True);
        Assert.That(specs[0].Velocity.Z < Fixed.Zero && specs[2].Velocity.Z > Fixed.Zero, Is.True);
        Assert.Throws<RulesViolationException>(() =>
            LaunchProfiles.BuildPattern(1, 1, PlayerSide.A, fan, 0, 0, 100, Fixed.Zero, false), "pattern must not need clamping");
    }

    [Test]
    public void SimulationIsDeterministic()
    {
        var w6 = WeaponCatalog.Get(6);
        var w7 = WeaponCatalog.Get(7);
        byte[] Run()
        {
            var specs = LaunchProfiles.BuildPattern(2, 3, PlayerSide.A, w6, 70, 5, 93, Kit.M(25), false)
                .Concat(LaunchProfiles.BuildPattern(2, 3, PlayerSide.B, w7, 30, -7, 88, Fixed.Zero, false)).ToList();
            return FlightSimulator.Simulate(specs, new TargetPose(PlayerSide.A, Kit.M(25), Dodge.Left),
                new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Jump)).Log.ToCanonicalBytes();
        }
        byte[] first = Run();
        byte[] second = Run();
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void TrajectorySamplerReproducesTickEndPositions()
    {
        var specs = LaunchProfiles.BuildPattern(1, 1, PlayerSide.A, WeaponCatalog.Get(3), 160, 0, 100, Fixed.Zero, false);
        var r = FlightSimulator.Simulate(specs, StandA, StandB);
        var track = r.Tracks.Single();
        Assert.That(TrajectorySampler.Sample(track, 0), Is.EqualTo(specs[0].Position));
        foreach (var s in track.Samples)
            Assert.That(TrajectorySampler.Sample(track, s.TimeSubTicks), Is.EqualTo(s.Position));
        var perTick = TrajectorySampler.SampleEveryTick(track);
        Assert.That(perTick[^1], Is.EqualTo(track.Samples[^1].Position));
        Assert.That(TrajectorySampler.IsAlive(track, track.EndTimeSubTicks + 1), Is.False);
        // Midway between two tick ends the sample lies between them.
        var mid = TrajectorySampler.Sample(track, 65536 + 32768);
        Assert.That(mid.X > track.Samples[1].Position.X && mid.X < track.Samples[2].Position.X, Is.True);
    }
}
