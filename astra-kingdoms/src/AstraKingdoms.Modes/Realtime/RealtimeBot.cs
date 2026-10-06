using System;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;

namespace AstraKingdoms.Modes.Realtime
{
    /// <summary>
    /// PROTOTYPE ONLY — the simple opponent of the real-time discovery prototype. It keeps a preferred
    /// range, strafes and changes altitude on a seeded rhythm, leads its target from the target's
    /// visible velocity and solves the low ballistic arc exactly in fixed point, then adds aim noise
    /// by difficulty. It observes only <see cref="RealtimeObservation"/> (own state and the
    /// opponent's visible motion and HP) — never the opponent's input for the tick.
    /// </summary>
    public sealed class RealtimeBot : IRealtimeController
    {
        private static readonly Fixed PreferredRange = Fixed.FromInt(9);
        private static readonly Fixed RangeSlack = Fixed.FromInt(1);
        private static readonly Fixed MaxShotRange = Fixed.FromInt(20);
        private static readonly Fixed G = Fixed.FromRatio(98, 10);

        private readonly BotRng _rng;
        private readonly int _aimNoiseQdeg;
        private int _strafe = 1;
        private int _strafeTicksLeft;
        private Fixed _altitudeOffset = Fixed.Zero;

        public BotDifficulty Difficulty { get; }

        public RealtimeBot(BotDifficulty difficulty, ulong seed)
        {
            Difficulty = difficulty;
            _rng = new BotRng(seed ^ 0x5EA1_7130UL);
            _aimNoiseQdeg = difficulty == BotDifficulty.Easy ? 24 : difficulty == BotDifficulty.Normal ? 10 : 3;
        }

        public RealtimeInput Decide(RealtimeObservation o)
        {
            ArcherState me = o.Self, foe = o.Opponent;
            if (_strafeTicksLeft <= 0)
            {
                _strafe = _rng.Chance(50) ? 1 : -1;
                _strafeTicksLeft = _rng.Range(45, 120);
                _altitudeOffset = Fixed.FromRatio(_rng.Range(-20, 20), 10);
            }
            _strafeTicksLeft--;

            Fixed dx = foe.Position.X - me.Position.X;
            Fixed dz = foe.Position.Z - me.Position.Z;
            Fixed range = Fixed.Sqrt(dx * dx + dz * dz);
            int approach = range > PreferredRange + RangeSlack ? 1 : range < PreferredRange - RangeSlack ? -1 : 0;
            int moveX = approach * dx.Sign;
            int moveZ = approach * dz.Sign;
            // Strafe sideways relative to the line of sight on whichever axis the approach leaves free.
            if (Fixed.Abs(dx) >= Fixed.Abs(dz)) moveZ = moveZ == 0 ? _strafe : moveZ;
            else moveX = moveX == 0 ? _strafe : moveX;
            Fixed targetAlt = foe.Position.Y + _altitudeOffset;
            int climb = targetAlt > me.Position.Y + Fixed.Half ? 1 : targetAlt < me.Position.Y - Fixed.Half ? -1 : 0;

            AimAt(me, foe, out int yaw, out int pitch, out Fixed distance);
            yaw += _rng.Range(-_aimNoiseQdeg, _aimNoiseQdeg);
            pitch += _rng.Range(-_aimNoiseQdeg, _aimNoiseQdeg);
            bool fire = me.CooldownTicks == 0 && distance <= MaxShotRange && foe.Alive;
            return new RealtimeInput(moveX, moveZ, climb, yaw, pitch, fire);
        }

        /// <summary>Lead-corrected low-arc aim from the shooter's position at the target's predicted position.</summary>
        public static void AimAt(ArcherState me, ArcherState foe, out int yawQdeg, out int pitchQdeg, out Fixed horizontal)
        {
            Fixed v = RealtimePrototypeRules.ArrowSpeed;
            FixedVector3 target = foe.Position;
            horizontal = Fixed.Zero;
            for (int iteration = 0; iteration < 2; iteration++)
            {
                Fixed hx = target.X - me.Position.X, hz = target.Z - me.Position.Z;
                horizontal = Fixed.Sqrt(hx * hx + hz * hz);
                Fixed t = horizontal / v;
                target = foe.Position + new FixedVector3(foe.Velocity.X * t, foe.Velocity.Y * t, foe.Velocity.Z * t);
            }
            Fixed dx = target.X - me.Position.X, dz = target.Z - me.Position.Z, dy = target.Y - me.Position.Y;
            horizontal = Fixed.Sqrt(dx * dx + dz * dz);
            yawQdeg = CircleTrig.Atan2Qdeg(dz, dx);

            // tan θ = (v² − sqrt(v⁴ − g(g R² + 2 h v²))) / (g R): the low arc.
            Fixed v2 = v * v;
            Fixed disc = v2 * v2 - G * (G * horizontal * horizontal + dy.MulInt(2) * v2);
            if (horizontal.Raw == 0)
            {
                pitchQdeg = dy.Sign >= 0 ? 360 : -360;
                return;
            }
            if (disc.Raw < 0)
            {
                pitchQdeg = 180; // out of range: 45° gives maximum reach
                return;
            }
            Fixed numerator = v2 - Fixed.Sqrt(disc);
            int angle = CircleTrig.Atan2Qdeg(numerator, G * horizontal);
            if (angle > CircleTrig.HalfTurnQdeg) angle -= CircleTrig.FullTurnQdeg;
            pitchQdeg = Math.Max(TrigTable.MinQdeg, Math.Min(TrigTable.MaxQdeg, angle));
        }
    }

    /// <summary>A stand-in for the human seat in automated checks: a fixed movement pattern, never fires.</summary>
    public sealed class ScriptedDodger : IRealtimeController
    {
        private readonly int _period;

        public ScriptedDodger(int periodTicks = 90)
        {
            _period = Math.Max(1, periodTicks);
        }

        public RealtimeInput Decide(RealtimeObservation o)
        {
            int phase = (o.Tick / _period) % 4;
            int moveZ = phase == 0 || phase == 1 ? 1 : -1;
            int climb = phase == 1 ? 1 : phase == 3 ? -1 : 0;
            return new RealtimeInput(0, moveZ, climb, 0, 0, false);
        }
    }
}
