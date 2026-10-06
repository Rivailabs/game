using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Combat
{
    /// <summary>Touch feedback produced by one aim change (ticket 29).</summary>
    public struct AimFeedback
    {
        /// <summary>The value moved by at least one whole degree since the last tick: a light haptic/visual tick.</summary>
        public bool DegreeTick;
        /// <summary>The request was clamped at the weapon's legal pitch limit.</summary>
        public bool PitchAtLimit;
        /// <summary>The request was clamped at the weapon's legal yaw limit.</summary>
        public bool YawAtLimit;
        /// <summary>Anything about the choice changed (the preview must be recomputed).</summary>
        public bool Changed;
    }

    /// <summary>
    /// The player's own provisional volley choice and its aim/power preview parameters (ticket 29).
    /// The same clamped values feed both <see cref="TrajectoryPreview"/> and the
    /// <see cref="VolleyInput"/> that Lock submits, so the preview can never show parameters that
    /// differ from what is sent. Drags accumulate fractional quarter-degrees, steps snap to the
    /// rules' 0.25° grid, and limits report "at limit" so the screen can show (not only buzz) it.
    /// </summary>
    public sealed class AimController
    {
        /// <summary>Quarter-degrees per pixel of drag at sensitivity 1.</summary>
        public const double DefaultQdegPerPixel = 0.5;
        /// <summary>Default opening pitch: a moderate arc of 20 degrees (clamped per weapon).</summary>
        public const int DefaultPitchQdeg = 20 * RulesConstants.QuarterDegreesPerDegree;

        private double _accPitch;
        private double _accYaw;
        private int _lastTickPitchDeg;
        private int _lastTickYawDeg;

        public int WeaponId { get; private set; }
        public int PitchQdeg { get; private set; }
        public int YawQdeg { get; private set; }
        public int PowerPercent { get; private set; } = RulesConstants.MaxPowerPercent;
        public Dodge Dodge { get; private set; } = Dodge.None;
        public bool Locked { get; private set; }
        public double Sensitivity { get; set; } = 1.0;

        public QdegRange PitchRange => WeaponCatalog.IsRegularId(WeaponId) ? LaunchProfiles.CentralPitchRange(WeaponCatalog.Get(WeaponId)) : default;
        public QdegRange YawRange => WeaponCatalog.IsRegularId(WeaponId) ? LaunchProfiles.CentralYawRange(WeaponCatalog.Get(WeaponId)) : default;

        /// <summary>Clean defaults for a fresh entry: first weapon, moderate arc, full power, no dodge.</summary>
        public void Reset(int firstWeaponId)
        {
            Locked = false;
            WeaponId = 0;
            PowerPercent = RulesConstants.MaxPowerPercent;
            Dodge = Dodge.None;
            YawQdeg = 0;
            PitchQdeg = DefaultPitchQdeg;
            _accPitch = _accYaw = 0;
            SelectWeapon(firstWeaponId);
        }

        public AimFeedback SelectWeapon(int weaponId)
        {
            if (Locked || !WeaponCatalog.IsRegularId(weaponId)) return default;
            bool changed = weaponId != WeaponId;
            WeaponId = weaponId;
            AimFeedback f = Clamp(PitchQdeg, YawQdeg);
            f.Changed |= changed;
            return f;
        }

        /// <summary>Applies a drag in pixels (x = yaw, y = pitch).</summary>
        public AimFeedback Drag(double dxPixels, double dyPixels)
        {
            if (Locked) return default;
            _accPitch += dyPixels * DefaultQdegPerPixel * Sensitivity;
            _accYaw += dxPixels * DefaultQdegPerPixel * Sensitivity;
            int dp = (int)Math.Truncate(_accPitch);
            int dy = (int)Math.Truncate(_accYaw);
            _accPitch -= dp;
            _accYaw -= dy;
            if (dp == 0 && dy == 0) return default;
            return Nudge(dp, dy);
        }

        /// <summary>Steps by whole quarter-degrees (the fine-step buttons use ±4 = one degree).</summary>
        public AimFeedback Nudge(int pitchQdeg, int yawQdeg)
        {
            if (Locked) return default;
            return Clamp(PitchQdeg + pitchQdeg, YawQdeg + yawQdeg);
        }

        public AimFeedback SetPower(int percent)
        {
            if (Locked) return default;
            int p = Math.Max(RulesConstants.MinPowerPercent, Math.Min(RulesConstants.MaxPowerPercent, percent));
            bool changed = p != PowerPercent;
            PowerPercent = p;
            return new AimFeedback { Changed = changed, DegreeTick = changed };
        }

        public bool SetDodge(Dodge dodge)
        {
            if (Locked) return false;
            Dodge = dodge;
            return true;
        }

        /// <summary>Locks the choice and returns the exact input to submit (null when no weapon or already locked).</summary>
        public VolleyInput Lock()
        {
            if (Locked || !WeaponCatalog.IsRegularId(WeaponId)) return null;
            Locked = true;
            return CurrentInput();
        }

        /// <summary>The engine rejected the lock: allow editing again.</summary>
        public void Unlock() => Locked = false;

        public VolleyInput CurrentInput() => new VolleyInput(WeaponId, PitchQdeg, YawQdeg, PowerPercent, Dodge);

        /// <summary>The preview arcs for the current choice, using the rules' own launch math.</summary>
        public IReadOnlyList<IReadOnlyList<PreviewPoint>> Preview(PlayerSide shooter, long ownBaselineRaw, long targetBaselineRaw,
            int maxTicks = TrajectoryPreview.DefaultMaxTicks) =>
            TrajectoryPreview.Compute(shooter, WeaponId, PitchQdeg, YawQdeg, PowerPercent, ownBaselineRaw, targetBaselineRaw, maxTicks);

        private AimFeedback Clamp(int pitch, int yaw)
        {
            var f = new AimFeedback();
            QdegRange pr = PitchRange, yr = YawRange;
            int cp = pr.Clamp(pitch), cy = yr.Clamp(yaw);
            f.PitchAtLimit = cp != pitch || cp == pr.Min || cp == pr.Max;
            f.YawAtLimit = cy != yaw || cy == yr.Min || cy == yr.Max;
            f.Changed = cp != PitchQdeg || cy != YawQdeg;
            PitchQdeg = cp;
            YawQdeg = cy;
            int pd = FloorDiv(cp, RulesConstants.QuarterDegreesPerDegree), yd = FloorDiv(cy, RulesConstants.QuarterDegreesPerDegree);
            if (pd != _lastTickPitchDeg || yd != _lastTickYawDeg)
            {
                f.DegreeTick = true;
                _lastTickPitchDeg = pd;
                _lastTickYawDeg = yd;
            }
            if (cp != pitch) _accPitch = 0;
            if (cy != yaw) _accYaw = 0;
            return f;
        }

        private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
    }
}
