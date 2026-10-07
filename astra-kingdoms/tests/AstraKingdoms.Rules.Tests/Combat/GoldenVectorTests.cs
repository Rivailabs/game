using System.Security.Cryptography;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Tests.Combat;

/// <summary>
/// Stored conformance vectors ("coordinates alone do not establish determinism"). Any change to the
/// trig table, launch evaluation order, integration, rounding, collision tie handling or event
/// encoding changes these values and therefore requires a new rules version, not a test update.
/// </summary>
public class GoldenVectorTests
{
    [Test]
    public void LaunchVelocityVector()
    {
        // B, Normal speed at 87% power, pitch 30.75 deg, yaw -4.25 deg.
        var v = LaunchProfiles.LaunchVelocity(PlayerSide.B, LaunchProfiles.LaunchSpeed(SpeedProfile.Normal, 87), 123, -17);
        Assert.That(v.X.Raw, Is.EqualTo(-38429353187L));
        Assert.That(v.Y.Raw, Is.EqualTo(22926105146L));
        Assert.That(v.Z.Raw, Is.EqualTo(2855794252L));
    }

    [Test]
    public void LowestEmberCorePitchAgainstStandingTarget()
    {
        Assert.That(Kit.FindPitch(PlayerSide.A, 1, new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.None), ContactKind.Core), Is.EqualTo(23));
    }

    [Test]
    public void MixedVolleyEventLog()
    {
        // Fire Fan vs Twin Gust on Forest with a pushed baseline, side dodge and jump.
        var state = Kit.State(new[] { 6, 1 }, new[] { 7, 8 }, TerrainType.Forest, defender: PlayerSide.A);
        state.A.BaselineOffsetRight = Kit.M(25);
        var r = VolleyResolver.Resolve(state, new VolleyInput(6, 90, 5, 91, Dodge.Left), new VolleyInput(7, 30, -10, 85, Dodge.Jump));

        string hash = Convert.ToHexString(SHA256.HashData(r.Log.ToCanonicalBytes())).ToLowerInvariant();
        Assert.That(hash, Is.EqualTo("5baa046d434214927a8b036f2a21ea39ec81ac52f700f74fc6b9d966c3c7c55e"), r.Log.ToCanonicalText());

        // Human-checkable parts of the same vector.
        var b1 = r.Log.Events.Single(e => e.Type == CombatEventType.BodyContact && e.Subject == PlayerSide.B);
        Assert.That((b1.Tick, b1.SubTick, b1.Contact), Is.EqualTo((73, 42349, ContactKind.Core)));
        Assert.That(r.Explanation.A.DirectDamageUnits, Is.EqualTo(375));  // 15 x 0.5 (Vayu vs Agni) x Forest 0.5
        Assert.That(r.Explanation.B.DirectDamageUnits, Is.EqualTo(2700)); // 12 x 1.5 core + 12 x 1.5 x 0.5 graze
        Assert.That(r.NewState.A.HpUnits, Is.EqualTo(9625));
        Assert.That(r.NewState.B.HpUnits, Is.EqualTo(7300));
    }
}
