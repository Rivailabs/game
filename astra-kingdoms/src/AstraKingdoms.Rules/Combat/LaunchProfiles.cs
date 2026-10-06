using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>Inclusive integer range of quarter-degree angles.</summary>
    public readonly struct QdegRange
    {
        public readonly int Min;
        public readonly int Max;

        public QdegRange(int min, int max)
        {
            Min = min;
            Max = max;
        }

        public bool Contains(int qdeg) => qdeg >= Min && qdeg <= Max;
        public int Clamp(int qdeg) => qdeg < Min ? Min : qdeg > Max ? Max : qdeg;
        public override string ToString() => "[" + Min + ", " + Max + "] qdeg";
    }

    /// <summary>
    /// Launch profiles of the numerical combat contract: speeds, pitch ranges, gravity and the
    /// intrinsic Fire Fan / Twin Gust patterns, including the narrowed central-angle ranges that
    /// guarantee no spawned projectile needs silent clamping.
    /// </summary>
    public static class LaunchProfiles
    {
        /// <summary>Standard gravity, −9.8 m/s² (rounded once to Q32.32).</summary>
        public static readonly Fixed Gravity = Fixed.FromRatio(-98, 10);

        /// <summary>Twin Gust second projectile: lateral acceleration along the shooter's local Right.</summary>
        public static readonly Fixed TwinCurveAccelRight = Fixed.FromInt(-2);
        /// <summary>Twin Gust curve applies on ticks 1..29 inclusive.</summary>
        public const int TwinCurveLastTick = 29;
        /// <summary>Twin Gust second projectile yaw offset (+4 deg).</summary>
        public const int TwinYawOffsetQdeg = 16;

        /// <summary>Fire Fan offsets for projectile indices 0, 1, 2 (pitch −3/0/+3 deg, yaw −1.5/0/+1.5 deg).</summary>
        private static readonly int[] FanPitchOffsetsQdeg = { -12, 0, 12 };
        private static readonly int[] FanYawOffsetsQdeg = { -6, 0, 6 };

        public static readonly QdegRange YawRange = new QdegRange(RulesConstants.MinYawQdeg, RulesConstants.MaxYawQdeg);

        public static Fixed BaseSpeed(SpeedProfile speed)
        {
            switch (speed)
            {
                case SpeedProfile.Normal: return Fixed.FromInt(12);
                case SpeedProfile.Fast: return Fixed.FromInt(15);
                case SpeedProfile.Slow: return Fixed.FromInt(11);
                case SpeedProfile.VerySlow: return Fixed.FromRatio(105, 10);
                case SpeedProfile.Direct: return Fixed.FromInt(16);
                default: throw new ArgumentOutOfRangeException(nameof(speed));
            }
        }

        /// <summary>Allowed pitch of a trajectory label for any single projectile.</summary>
        public static QdegRange PitchRange(TrajectoryProfile trajectory)
        {
            switch (trajectory)
            {
                case TrajectoryProfile.Normal: return new QdegRange(0, 65 * 4);
                case TrajectoryProfile.Flat: return new QdegRange(-5 * 4, 20 * 4);
                case TrajectoryProfile.High: return new QdegRange(25 * 4, 75 * 4);
                case TrajectoryProfile.VeryHigh: return new QdegRange(45 * 4, 80 * 4);
                case TrajectoryProfile.Direct: return new QdegRange(-10 * 4, 10 * 4);
                default: throw new ArgumentOutOfRangeException(nameof(trajectory));
            }
        }

        /// <summary>Direct trajectories fly with zero gravity; every other label uses standard gravity.</summary>
        public static bool HasGravity(TrajectoryProfile trajectory) => trajectory != TrajectoryProfile.Direct;

        /// <summary>Permitted submitted (central) pitch for a weapon, narrowed for Fire Fan.</summary>
        public static QdegRange CentralPitchRange(WeaponDefinition weapon)
        {
            QdegRange r = PitchRange(weapon.Trajectory);
            if (weapon.Ability == WeaponAbility.FanSpread)
                return new QdegRange(r.Min - FanPitchOffsetsQdeg[0], r.Max - FanPitchOffsetsQdeg[2]);
            return r;
        }

        /// <summary>Permitted submitted (central) yaw for a weapon, narrowed for Fire Fan and Twin Gust.</summary>
        public static QdegRange CentralYawRange(WeaponDefinition weapon)
        {
            if (weapon.Ability == WeaponAbility.FanSpread)
                return new QdegRange(YawRange.Min - FanYawOffsetsQdeg[0], YawRange.Max - FanYawOffsetsQdeg[2]);
            if (weapon.Ability == WeaponAbility.TwinCurve)
                return new QdegRange(YawRange.Min, YawRange.Max - TwinYawOffsetQdeg);
            return YawRange;
        }

        /// <summary>Launch speed = base speed x power% (rounded once, ties to even).</summary>
        public static Fixed LaunchSpeed(SpeedProfile speed, int powerPercent) =>
            BaseSpeed(speed).MulInt(powerPercent).DivInt(100);

        /// <summary>
        /// World launch velocity for a shooter. Yaw rotates towards the shooter's local Right.
        /// Evaluation order (each product rounded once): h = s·cos(pitch); forward = h·cos(yaw);
        /// right = h·sin(yaw); up = s·sin(pitch).
        /// </summary>
        public static FixedVector3 LaunchVelocity(PlayerSide shooter, Fixed speed, int pitchQdeg, int yawQdeg)
        {
            Fixed h = speed * TrigTable.Cos(pitchQdeg);
            Fixed forward = h * TrigTable.Cos(yawQdeg);
            Fixed right = h * TrigTable.Sin(yawQdeg);
            Fixed up = speed * TrigTable.Sin(pitchQdeg);
            Fixed vx = CombatGeometry.ForwardSign(shooter) > 0 ? forward : -forward;
            return new FixedVector3(vx, up, CombatGeometry.LocalRightToWorldZ(shooter, right));
        }

        /// <summary>
        /// Emits the canonical projectiles of one weapon's initial pattern, all launched together
        /// from the snapshot launch point. Validation of the central angles is the caller's job;
        /// this method throws if a spawned angle would leave its permitted range.
        /// </summary>
        public static IReadOnlyList<ProjectileSpec> BuildPattern(int round, int volley, PlayerSide shooter, WeaponDefinition weapon,
            int pitchQdeg, int yawQdeg, int powerPercent, Fixed baselineOffsetRight, bool jumpPierceActive)
        {
            if (weapon == null) throw new ArgumentNullException(nameof(weapon));
            var list = new List<ProjectileSpec>(weapon.ProjectileCount);
            Fixed speed = LaunchSpeed(weapon.Speed, powerPercent);
            FixedVector3 origin = CombatGeometry.LaunchPoint(shooter, baselineOffsetRight);
            QdegRange pitchRange = PitchRange(weapon.Trajectory);
            bool gravity = HasGravity(weapon.Trajectory);
            bool burst = weapon.Ability == WeaponAbility.GroundBurst;

            for (int i = 0; i < weapon.ProjectileCount; i++)
            {
                int p = pitchQdeg, y = yawQdeg;
                Fixed curve = Fixed.Zero;
                int curveLast = 0;
                if (weapon.Ability == WeaponAbility.FanSpread)
                {
                    p += FanPitchOffsetsQdeg[i];
                    y += FanYawOffsetsQdeg[i];
                }
                else if (weapon.Ability == WeaponAbility.TwinCurve && i == 1)
                {
                    y += TwinYawOffsetQdeg;
                    curve = CombatGeometry.LocalRightToWorldZ(shooter, TwinCurveAccelRight);
                    curveLast = TwinCurveLastTick;
                }
                if (!pitchRange.Contains(p) || !YawRange.Contains(y))
                    throw new RulesViolationException("PATTERN_ANGLE", "Spawned projectile " + i + " of " + weapon.Name + " leaves its angle range.");

                list.Add(new ProjectileSpec(
                    new ProjectileId(round, volley, shooter, i), weapon.Id, weapon.Element, weapon.DamagePerProjectileUnits,
                    weapon.MassPerProjectile, Fixed.FromMillimetres(weapon.RadiusMm), origin,
                    LaunchVelocity(shooter, speed, p, y), gravity, canClash: !burst, groundBurst: burst,
                    jumpPierce: jumpPierceActive, curveAccelZ: curve, curveLastTick: curveLast));
            }
            return list;
        }
    }

    /// <summary>Projectile identity: round, volley, owner and projectile index within the weapon pattern.</summary>
    public readonly struct ProjectileId : IEquatable<ProjectileId>, IComparable<ProjectileId>
    {
        public readonly int Round;
        public readonly int Volley;
        public readonly PlayerSide Owner;
        public readonly int Index;

        public ProjectileId(int round, int volley, PlayerSide owner, int index)
        {
            Round = round;
            Volley = volley;
            Owner = owner;
            Index = index;
        }

        /// <summary>Canonical order: owner, then index (round and volley are equal within a volley).</summary>
        public int CompareTo(ProjectileId other)
        {
            int c = Round.CompareTo(other.Round);
            if (c != 0) return c;
            c = Volley.CompareTo(other.Volley);
            if (c != 0) return c;
            c = ((int)Owner).CompareTo((int)other.Owner);
            return c != 0 ? c : Index.CompareTo(other.Index);
        }

        public bool Equals(ProjectileId other) => Round == other.Round && Volley == other.Volley && Owner == other.Owner && Index == other.Index;
        public override bool Equals(object obj) => obj is ProjectileId p && Equals(p);
        public override int GetHashCode() => ((Round * 31 + Volley) * 31 + (int)Owner) * 31 + Index;
        public override string ToString() => "R" + Round + "V" + Volley + ":" + Owner + "#" + Index;
    }

    /// <summary>Immutable launch description of one projectile.</summary>
    public sealed class ProjectileSpec
    {
        public ProjectileId Id { get; }
        public PlayerSide Owner => Id.Owner;
        public int Index => Id.Index;
        public int WeaponId { get; }
        public Element Element { get; }
        public int DamageUnits { get; }
        public int Mass { get; }
        public Fixed Radius { get; }
        public FixedVector3 Position { get; }
        public FixedVector3 Velocity { get; }
        /// <summary>False for Direct trajectories (zero gravity).</summary>
        public bool Gravity { get; }
        /// <summary>Whether opposing clash-enabled projectiles can collide with this one (false for Boulder).</summary>
        public bool CanClash { get; }
        /// <summary>Boulder: no body-plane contact; bursts at first ground contact.</summary>
        public bool GroundBurst { get; }
        /// <summary>Stone Arrow's unsuppressed Jump Pierce.</summary>
        public bool JumpPierce { get; }
        /// <summary>Additional world-z acceleration (m/s²) for ticks 1..<see cref="CurveLastTick"/>.</summary>
        public Fixed CurveAccelZ { get; }
        public int CurveLastTick { get; }

        public ProjectileSpec(ProjectileId id, int weaponId, Element element, int damageUnits, int mass, Fixed radius,
            FixedVector3 position, FixedVector3 velocity, bool gravity, bool canClash, bool groundBurst, bool jumpPierce,
            Fixed curveAccelZ, int curveLastTick)
        {
            if (mass <= 0) throw new ArgumentOutOfRangeException(nameof(mass));
            if (radius.Raw <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
            Id = id;
            WeaponId = weaponId;
            Element = element;
            DamageUnits = damageUnits;
            Mass = mass;
            Radius = radius;
            Position = position;
            Velocity = velocity;
            Gravity = gravity;
            CanClash = canClash;
            GroundBurst = groundBurst;
            JumpPierce = jumpPierce;
            CurveAccelZ = curveAccelZ;
            CurveLastTick = curveLastTick;
        }

        public override string ToString() => Id + " w" + WeaponId + " m" + Mass + " p" + Position + " v" + Velocity;
    }
}
