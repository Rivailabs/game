using System;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>Geometric outcome of a projectile reaching a defender (or of a scheduled strike).</summary>
    public enum ContactKind : byte
    {
        /// <summary>No contact: outside every applicable radius.</summary>
        Miss = 0,
        /// <summary>Within 0.25 m + projectile radius of the (displaced) capsule axis.</summary>
        Core = 1,
        /// <summary>Core miss within 0.50 m + radius while the defender performs a legal dodge; half damage.</summary>
        Graze = 2,
        /// <summary>Boulder ground burst within 0.55 m of the displaced axis; full damage.</summary>
        BurstFull = 3,
        /// <summary>Boulder ground burst in (0.55, 0.80] m during a dodge; half damage.</summary>
        BurstGraze = 4,
        /// <summary>Experimental Brahmastra homing strike (no geometry, never a graze).</summary>
        Brahmastra = 5,
    }

    /// <summary>
    /// Fixed world geometry of the duel arena (metres, Q32.32). A stands at x = 0 facing +x with
    /// local Right = +z; B stands at x = 8 facing −x with local Right = −z.
    /// </summary>
    public static class CombatGeometry
    {
        /// <summary>Sub-tick resolution: event times are quantized upward to 1/65,536 tick.</summary>
        public const int SubTicks = RulesConstants.SubTicksPerTick;
        internal const int SubTickBits = 16;

        public static readonly Fixed PlaneXA = Fixed.Zero;
        public static readonly Fixed PlaneXB = Fixed.FromInt(8);
        public static readonly Fixed LaunchForward = Fixed.FromRatio(35, 100);
        public static readonly Fixed LaunchHeight = Fixed.FromRatio(135, 100);

        public static readonly Fixed CapsuleBottom = Fixed.FromRatio(35, 100);
        public static readonly Fixed CapsuleTop = Fixed.FromRatio(145, 100);
        public static readonly Fixed CoreRadius = Fixed.FromRatio(25, 100);
        public static readonly Fixed GrazeRadius = Fixed.FromRatio(50, 100);
        public static readonly Fixed SideDodgeShift = Fixed.FromRatio(45, 100);
        public static readonly Fixed JumpRaise = Fixed.FromRatio(50, 100);
        public static readonly Fixed BurstFullRadius = Fixed.FromRatio(55, 100);
        public static readonly Fixed BurstGrazeRadius = Fixed.FromRatio(80, 100);

        /// <summary>Gale Push baseline step towards the target's local Right.</summary>
        public static readonly Fixed PushStep = Fixed.FromRatio(25, 100);
        /// <summary>Maximum absolute baseline displacement.</summary>
        public static readonly Fixed MaxBaselineOffset = Fixed.FromRatio(50, 100);

        // Flight bounds: leaving x in [-2,10], y in [0,12], z in [-3,3] terminates a projectile.
        public static readonly Fixed MinX = Fixed.FromInt(-2);
        public static readonly Fixed MaxX = Fixed.FromInt(10);
        public static readonly Fixed MaxY = Fixed.FromInt(12);
        public static readonly Fixed MinZ = Fixed.FromInt(-3);
        public static readonly Fixed MaxZ = Fixed.FromInt(3);

        public static PlayerSide Opponent(PlayerSide side) => side == PlayerSide.A ? PlayerSide.B : PlayerSide.A;

        /// <summary>x-plane on which the given fighter stands.</summary>
        public static Fixed PlaneX(PlayerSide side) => side == PlayerSide.A ? PlaneXA : PlaneXB;

        /// <summary>+1 when the fighter's forward is world +x, −1 otherwise.</summary>
        public static int ForwardSign(PlayerSide side) => side == PlayerSide.A ? 1 : -1;

        /// <summary>+1 when the fighter's local Right is world +z, −1 otherwise.</summary>
        public static int RightSign(PlayerSide side) => side == PlayerSide.A ? 1 : -1;

        /// <summary>Converts a displacement along a fighter's local Right axis into world z.</summary>
        public static Fixed LocalRightToWorldZ(PlayerSide side, Fixed localRight) => side == PlayerSide.A ? localRight : -localRight;

        /// <summary>Launch point: 0.35 m forward of the shooter, 1.35 m high, at the current baseline.</summary>
        public static FixedVector3 LaunchPoint(PlayerSide shooter, Fixed baselineOffsetRight)
        {
            Fixed x = shooter == PlayerSide.A ? PlaneXA + LaunchForward : PlaneXB - LaunchForward;
            return new FixedVector3(x, LaunchHeight, LocalRightToWorldZ(shooter, baselineOffsetRight));
        }

        // ------------------------------------------------------------------
        // Contact classification.
        // All decisions compare exact squared distances in "scaled raw" units, i.e. the Q32.32 raw
        // value multiplied by 65,536 so that sub-tick interpolated positions stay integral. No
        // square root or rounding participates in a hit decision.
        // ------------------------------------------------------------------

        /// <summary>
        /// Classifies a projectile centre at (y, z) on the defender's plane against the capsule.
        /// With <paramref name="jumpPierce"/> (Stone Arrow, unsuppressed) a jumping defender is tested
        /// against the standing silhouette and loses the jump graze region; side dodges are unaffected.
        /// </summary>
        public static ContactKind ClassifyBodyCrossing(Fixed y, Fixed z, Fixed radius, TargetPose pose, bool jumpPierce)
        {
            return ClassifyBodyScaled(Scale(y), Scale(z), radius, pose, jumpPierce);
        }

        /// <summary>
        /// Classifies a Boulder ground-burst point against the displaced capsule axis in three
        /// dimensions: ≤ 0.55 m full, during a dodge ≤ 0.80 m graze, otherwise miss.
        /// </summary>
        public static ContactKind ClassifyBurst(FixedVector3 point, TargetPose pose)
        {
            return ClassifyBurstScaled(Scale(point.X), Scale(point.Y), Scale(point.Z), pose);
        }

        /// <summary>
        /// Distance from (y, z) on the defender plane to the capsule axis segment that applies to the
        /// projectile (presentation value, rounded; decisions use exact squared distances).
        /// </summary>
        public static Fixed BodyAxisDistance(Fixed y, Fixed z, TargetPose pose, bool jumpPierce)
        {
            Fixed raise = EffectiveRaise(pose, jumpPierce);
            Fixed dy = AxisGap(y, CapsuleBottom + raise, CapsuleTop + raise);
            Fixed dz = z - pose.AxisZ;
            return Fixed.Sqrt(dy * dy + dz * dz);
        }

        /// <summary>Three-dimensional distance from a point to the defender's displaced capsule axis.</summary>
        public static Fixed BurstAxisDistance(FixedVector3 point, TargetPose pose)
        {
            Fixed dx = point.X - pose.PlaneX;
            Fixed dy = AxisGap(point.Y, CapsuleBottom + pose.Raise, CapsuleTop + pose.Raise);
            Fixed dz = point.Z - pose.AxisZ;
            return Fixed.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        internal static ContactKind ClassifyBodyScaled(long yS, long zS, Fixed radius, TargetPose pose, bool jumpPierce)
        {
            bool pierce = jumpPierce && pose.Dodge == Dodge.Jump;
            Fixed raise = EffectiveRaise(pose, jumpPierce);
            long dy = AxisGapScaled(yS, Scale(CapsuleBottom + raise), Scale(CapsuleTop + raise));
            long dz = zS - Scale(pose.AxisZ);
            Int128Lite d2 = Int128Lite.Mul(dy, dy) + Int128Lite.Mul(dz, dz);
            if (WithinScaled(d2, CoreRadius + radius)) return ContactKind.Core;
            if (pose.GrazeAllowed && !pierce && WithinScaled(d2, GrazeRadius + radius)) return ContactKind.Graze;
            return ContactKind.Miss;
        }

        internal static ContactKind ClassifyBurstScaled(long xS, long yS, long zS, TargetPose pose)
        {
            long dx = xS - Scale(pose.PlaneX);
            long dy = AxisGapScaled(yS, Scale(CapsuleBottom + pose.Raise), Scale(CapsuleTop + pose.Raise));
            long dz = zS - Scale(pose.AxisZ);
            Int128Lite d2 = Int128Lite.Mul(dx, dx) + Int128Lite.Mul(dy, dy) + Int128Lite.Mul(dz, dz);
            if (WithinScaled(d2, BurstFullRadius)) return ContactKind.BurstFull;
            if (pose.GrazeAllowed && WithinScaled(d2, BurstGrazeRadius)) return ContactKind.BurstGraze;
            return ContactKind.Miss;
        }

        /// <summary>True when a scaled squared distance is at most <paramref name="radius"/> squared.</summary>
        internal static bool WithinScaled(Int128Lite distanceSquaredScaled, Fixed radius)
        {
            long r = Scale(radius);
            return distanceSquaredScaled <= Int128Lite.Mul(r, r);
        }

        internal static long Scale(Fixed v) => checked(v.Raw * SubTicks);

        private static Fixed EffectiveRaise(TargetPose pose, bool jumpPierce) =>
            jumpPierce && pose.Dodge == Dodge.Jump ? Fixed.Zero : pose.Raise;

        private static Fixed AxisGap(Fixed v, Fixed lo, Fixed hi) => v < lo ? lo - v : v > hi ? v - hi : Fixed.Zero;

        private static long AxisGapScaled(long v, long lo, long hi) => v < lo ? lo - v : v > hi ? v - hi : 0;
    }

    /// <summary>
    /// The defender's collision pose for one volley: fixed x-plane, lateral axis position (baseline
    /// plus side dodge, in world z), jump raise and whether the outer graze region applies.
    /// </summary>
    public readonly struct TargetPose
    {
        public readonly PlayerSide Side;
        public readonly Fixed PlaneX;
        /// <summary>World z of the capsule axis after baseline displacement and side dodge.</summary>
        public readonly Fixed AxisZ;
        /// <summary>Effective dodge after Net/Cyclone transformations.</summary>
        public readonly Dodge Dodge;

        public TargetPose(PlayerSide side, Fixed baselineOffsetRight, Dodge effectiveDodge)
        {
            Side = side;
            PlaneX = CombatGeometry.PlaneX(side);
            Dodge = effectiveDodge;
            Fixed lateral = baselineOffsetRight;
            if (effectiveDodge == Dodge.Left) lateral -= CombatGeometry.SideDodgeShift;
            else if (effectiveDodge == Dodge.Right) lateral += CombatGeometry.SideDodgeShift;
            AxisZ = CombatGeometry.LocalRightToWorldZ(side, lateral);
        }

        /// <summary>Vertical raise of the capsule axis (0.50 m while jumping).</summary>
        public Fixed Raise => Dodge == Dodge.Jump ? CombatGeometry.JumpRaise : Fixed.Zero;

        /// <summary>The outer graze region exists only during a legal dodge.</summary>
        public bool GrazeAllowed => Dodge != Dodge.None;

        public override string ToString() => Side + " plane x=" + PlaneX + " axis z=" + AxisZ + " dodge=" + Dodge;
    }
}
