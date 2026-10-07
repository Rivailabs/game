using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Combat
{
    /// <summary>A preview point in rules world metres (x along the arena, y up, z lateral).</summary>
    public readonly struct PreviewPoint
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public PreviewPoint(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>
    /// Aim preview for the player's own provisional choice. It builds the weapon's launch pattern with
    /// the rules' launch-profile math (<see cref="LaunchProfiles.BuildPattern"/>) and flies it with the
    /// rules' simulator against a standing target, then returns only the first <c>maxTicks</c> of each
    /// arc. It is presentation only: it is never submitted and decides nothing; the authoritative
    /// result comes from the engine after both players lock (with the real dodges and clashes).
    /// </summary>
    public static class TrajectoryPreview
    {
        /// <summary>Default visible arc length: 0.375 s of flight, enough to read the angle without solving the shot.</summary>
        public const int DefaultMaxTicks = 45;

        public static IReadOnlyList<IReadOnlyList<PreviewPoint>> Compute(PlayerSide shooter, int weaponId, int pitchQdeg, int yawQdeg,
            int powerPercent, long shooterBaselineRightRaw, long targetBaselineRightRaw, int maxTicks = DefaultMaxTicks)
        {
            if (!WeaponCatalog.IsRegularId(weaponId)) return Array.Empty<IReadOnlyList<PreviewPoint>>();
            WeaponDefinition weapon = WeaponCatalog.Get(weaponId);
            pitchQdeg = LaunchProfiles.CentralPitchRange(weapon).Clamp(pitchQdeg);
            yawQdeg = LaunchProfiles.CentralYawRange(weapon).Clamp(yawQdeg);
            if (powerPercent < RulesConstants.MinPowerPercent) powerPercent = RulesConstants.MinPowerPercent;
            if (powerPercent > RulesConstants.MaxPowerPercent) powerPercent = RulesConstants.MaxPowerPercent;

            Fixed ownBaseline = Fixed.FromRaw(shooterBaselineRightRaw);
            IReadOnlyList<ProjectileSpec> specs = LaunchProfiles.BuildPattern(1, 1, shooter, weapon, pitchQdeg, yawQdeg, powerPercent,
                ownBaseline, jumpPierceActive: false);
            PlayerSide target = CombatGeometry.Opponent(shooter);
            var ownPose = new TargetPose(shooter, ownBaseline, Dodge.None);
            var targetPose = new TargetPose(target, Fixed.FromRaw(targetBaselineRightRaw), Dodge.None);
            SimulationResult sim = FlightSimulator.Simulate(specs,
                shooter == PlayerSide.A ? ownPose : targetPose,
                shooter == PlayerSide.B ? ownPose : targetPose);

            var arcs = new List<IReadOnlyList<PreviewPoint>>(sim.Tracks.Count);
            long limit = (long)Math.Max(1, maxTicks) * CombatGeometry.SubTicks;
            foreach (ProjectileTrack track in sim.Tracks)
            {
                var pts = new List<PreviewPoint>();
                long end = Math.Min(limit, track.EndTimeSubTicks);
                for (long t = 0; t < end; t += CombatGeometry.SubTicks) pts.Add(ToPoint(TrajectorySampler.Sample(track, t)));
                pts.Add(ToPoint(TrajectorySampler.Sample(track, end)));
                arcs.Add(pts);
            }
            return arcs;
        }

        public static PreviewPoint ToPoint(FixedVector3 v) => new PreviewPoint(v.X.ToDouble(), v.Y.ToDouble(), v.Z.ToDouble());

        /// <summary>Rules fixed-point position as a presentation vector (rules frame, metres).</summary>
        public static Presentation.V3 ToV3(FixedVector3 v) => new Presentation.V3(v.X.ToDouble(), v.Y.ToDouble(), v.Z.ToDouble());
    }
}
