using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Tests.Combat;

public class GeometryTests
{
    private static readonly Fixed NormalRadius = Fixed.FromMillimetres(40);
    private static readonly Fixed StoneRadius = Fixed.FromMillimetres(60);

    // Acceptance: "Low-shot Jump" — normal-radius arrow at standing y=0.40, z=0; Jump.
    [Test]
    public void LowShotJump_IsGrazeAtShiftedAxisDistance045()
    {
        var pose = new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Jump);
        Fixed y = Kit.M(40), z = Fixed.Zero;
        // Reported distance is 0.45 m up to the rounding of the decimal constants (a couple of 2^-32 m units).
        Fixed d = CombatGeometry.BodyAxisDistance(y, z, pose, jumpPierce: false);
        Assert.That(Math.Abs(d.Raw - Kit.M(45).Raw), Is.LessThanOrEqualTo(2));
        Assert.That(CombatGeometry.ClassifyBodyCrossing(y, z, NormalRadius, pose, jumpPierce: false), Is.EqualTo(ContactKind.Graze));
    }

    // Acceptance: "Low-shot Jump" — Stone Jump Pierce instead tests the standing core.
    [Test]
    public void LowShotJump_StoneJumpPierceTestsStandingCore()
    {
        var pose = new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Jump);
        Fixed y = Kit.M(40);
        Assert.That(CombatGeometry.BodyAxisDistance(y, Fixed.Zero, pose, jumpPierce: true), Is.EqualTo(Fixed.Zero));
        Assert.That(CombatGeometry.ClassifyBodyCrossing(y, Fixed.Zero, StoneRadius, pose, jumpPierce: true), Is.EqualTo(ContactKind.Core));
    }

    [Test]
    public void JumpPierce_RemovesJumpGrazeButKeepsSideDodges()
    {
        // Standing core top is 1.45 + 0.31 = 1.76; a shot at 1.90 would graze a jumper but Pierce removes that.
        var jump = new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Jump);
        Assert.That(CombatGeometry.ClassifyBodyCrossing(Kit.M(190), Fixed.Zero, StoneRadius, jump, jumpPierce: false), Is.EqualTo(ContactKind.Core));
        Assert.That(CombatGeometry.ClassifyBodyCrossing(Kit.M(200), Fixed.Zero, StoneRadius, jump, jumpPierce: true), Is.EqualTo(ContactKind.Miss));
        // Side dodge with Pierce: graze still available.
        var side = new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Left);
        // A shot on the original baseline is 0.45 m from the shifted axis: still a graze.
        Assert.That(CombatGeometry.ClassifyBodyCrossing(Kit.M(100), Fixed.Zero, StoneRadius, side, jumpPierce: true), Is.EqualTo(ContactKind.Graze));
    }

    // Acceptance: "Torso-shot Jump".
    [Test]
    public void TorsoShotJump_StillCoreHit()
    {
        var pose = new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Jump);
        Assert.That(CombatGeometry.ClassifyBodyCrossing(Kit.M(100), Fixed.Zero, NormalRadius, pose, false), Is.EqualTo(ContactKind.Core));
    }

    [Test]
    public void CoreAndGrazeBoundariesAreInclusive()
    {
        var none = new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.None);
        Fixed core = CombatGeometry.CoreRadius + NormalRadius;
        Assert.That(CombatGeometry.ClassifyBodyCrossing(Kit.M(100), core, NormalRadius, none, false), Is.EqualTo(ContactKind.Core));
        Assert.That(CombatGeometry.ClassifyBodyCrossing(Kit.M(100), core + Fixed.FromRaw(1), NormalRadius, none, false), Is.EqualTo(ContactKind.Miss),
            "Without a dodge the outer graze region does not apply.");

        var jump = new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Jump);
        Fixed graze = CombatGeometry.GrazeRadius + NormalRadius;
        Assert.That(CombatGeometry.ClassifyBodyCrossing(Kit.M(100), graze, NormalRadius, jump, false), Is.EqualTo(ContactKind.Graze));
        Assert.That(CombatGeometry.ClassifyBodyCrossing(Kit.M(100), graze + Fixed.FromRaw(1), NormalRadius, jump, false), Is.EqualTo(ContactKind.Miss));
    }

    [Test]
    public void SideDodgeIsRelativeToDefenderFacing()
    {
        // A's local Right is +z; B's local Right is -z.
        Assert.That(new TargetPose(PlayerSide.A, Fixed.Zero, Dodge.Right).AxisZ, Is.EqualTo(Kit.M(45)));
        Assert.That(new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Right).AxisZ, Is.EqualTo(-Kit.M(45)));
        Assert.That(new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Left).AxisZ, Is.EqualTo(Kit.M(45)));
        // Baseline push (local Right) composes with the dodge.
        Assert.That(new TargetPose(PlayerSide.B, Kit.M(25), Dodge.Right).AxisZ, Is.EqualTo(-Kit.M(70)));
    }

    [Test]
    public void BoulderBurst_FullGrazeMiss()
    {
        var none = new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.None);
        // Ground point at the foot of the axis: vertical gap 0.35 m -> full burst.
        Assert.That(CombatGeometry.ClassifyBurst(new FixedVector3(Fixed.FromInt(8), Fixed.Zero, Fixed.Zero), none), Is.EqualTo(ContactKind.BurstFull));
        // 0.35 vertical and 0.50 horizontal: 0.61 m -> miss without dodge, graze with a dodge.
        var p = new FixedVector3(Fixed.FromInt(8) + Kit.M(50), Fixed.Zero, Fixed.Zero);
        Assert.That(CombatGeometry.ClassifyBurst(p, none), Is.EqualTo(ContactKind.Miss));
        var left = new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Left);
        var pLeft = new FixedVector3(Fixed.FromInt(8) + Kit.M(50), Fixed.Zero, left.AxisZ);
        Assert.That(CombatGeometry.ClassifyBurst(pLeft, left), Is.EqualTo(ContactKind.BurstGraze));
        // A jumper's axis starts 0.85 m up, beyond the 0.80 m graze radius.
        var jump = new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.Jump);
        Assert.That(CombatGeometry.ClassifyBurst(new FixedVector3(Fixed.FromInt(8), Fixed.Zero, Fixed.Zero), jump), Is.EqualTo(ContactKind.Miss));
    }
}
