using System.Collections.Concurrent;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Bots
{
    /// <summary>An aim solution for one weapon against a target pose.</summary>
    public readonly struct AimSolution
    {
        public readonly int PitchQdeg;
        public readonly int YawQdeg;
        /// <summary>Full (core or full-burst) contacts the pattern makes against the standing baseline target.</summary>
        public readonly int FullContacts;

        public AimSolution(int pitchQdeg, int yawQdeg, int fullContacts)
        {
            PitchQdeg = pitchQdeg;
            YawQdeg = yawQdeg;
            FullContacts = fullContacts;
        }
    }

    /// <summary>
    /// Deterministic aim search using only the public <see cref="FlightSimulator"/> against the
    /// opponent's <b>baseline</b> pose (their visible baseline displacement, no dodge): the bot never
    /// sees a committed dodge. Coarse grid over the weapon's legal central pitch/yaw, then a fine
    /// 0.25° refinement. Results are cached per (weapon, shooter, both baselines); the cache is
    /// thread-safe and its contents depend only on public rules data.
    /// </summary>
    public static class AimSolver
    {
        private static readonly ConcurrentDictionary<(int, PlayerSide, long, long), AimSolution> Cache =
            new ConcurrentDictionary<(int, PlayerSide, long, long), AimSolution>();

        public static AimSolution Solve(int weaponId, PlayerSide shooter, Fixed shooterBaselineRight, Fixed targetBaselineRight)
        {
            var key = (weaponId, shooter, shooterBaselineRight.Raw, targetBaselineRight.Raw);
            return Cache.GetOrAdd(key, k => Search(k.Item1, k.Item2, Fixed.FromRaw(k.Item3), Fixed.FromRaw(k.Item4)));
        }

        private static AimSolution Search(int weaponId, PlayerSide shooter, Fixed shooterBaseline, Fixed targetBaseline)
        {
            WeaponDefinition w = WeaponCatalog.Get(weaponId);
            QdegRange pitch = LaunchProfiles.CentralPitchRange(w);
            QdegRange yaw = LaunchProfiles.CentralYawRange(w);
            PlayerSide target = CombatGeometry.Opponent(shooter);
            var targetPose = new TargetPose(target, targetBaseline, Dodge.None);
            var ownPose = new TargetPose(shooter, shooterBaseline, Dodge.None);
            TargetPose poseA = shooter == PlayerSide.A ? ownPose : targetPose;
            TargetPose poseB = shooter == PlayerSide.B ? ownPose : targetPose;

            long Score(int p, int y, out int full)
            {
                IReadOnlyList<ProjectileSpec> specs = LaunchProfiles.BuildPattern(1, 1, shooter, w, p, y, RulesConstants.MaxPowerPercent,
                    shooterBaseline, jumpPierceActive: false);
                SimulationResult sim = FlightSimulator.Simulate(specs, poseA, poseB);
                full = 0;
                long score = 0;
                foreach (GeometricContact c in sim.Contacts)
                {
                    if (c.Target != target) continue;
                    if (c.Kind == ContactKind.Core || c.Kind == ContactKind.BurstFull)
                    {
                        full++;
                        // Prefer contacts near the capsule axis: robust against small errors.
                        score += 1L << 40;
                        score -= c.Distance.Raw >> 8;
                    }
                }
                return score;
            }

            int bestP = pitch.Clamp(0), bestY = yaw.Clamp(0), bestFull = 0;
            long best = long.MinValue;
            void Try(int p, int y)
            {
                if (!pitch.Contains(p) || !yaw.Contains(y)) return;
                long s = Score(p, y, out int full);
                if (s > best)
                {
                    best = s;
                    bestP = p;
                    bestY = y;
                    bestFull = full;
                }
            }

            // Coarse: yaw at 0 first (most targets sit near z = 0), then a coarse yaw sweep if needed.
            int y0 = yaw.Clamp(0);
            for (int p = pitch.Min; p <= pitch.Max; p += 8) Try(p, y0);
            if (bestFull == 0)
            {
                for (int y = yaw.Min; y <= yaw.Max; y += 8)
                    for (int p = pitch.Min; p <= pitch.Max; p += 8)
                        Try(p, y);
            }
            // Fine refinement around the coarse optimum.
            int cp = bestP, cy = bestY;
            for (int dy = -4; dy <= 4; dy += 2)
                for (int dp = -7; dp <= 7; dp++)
                    Try(cp + dp, cy + dy);
            return new AimSolution(bestP, bestY, bestFull);
        }
    }
}
