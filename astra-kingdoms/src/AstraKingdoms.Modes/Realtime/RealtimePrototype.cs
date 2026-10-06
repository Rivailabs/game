using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Modes.Realtime
{
    /// <summary>
    /// PROTOTYPE ONLY — V3 real-time combat discovery (plan: "Real time combat discovery").
    /// <para>
    /// A separate, bot-only, single-device fixed-step simulation of flying/moving archers. It exists
    /// to let the owner measure control comprehension, motion comfort, readable counterplay and
    /// device cost against the existing duel. It is outside ranked progression and every reward
    /// path, has no networking, prediction or reconciliation, and must not be mistaken for a
    /// network design: tick rate, transport and lag handling are for a qualified network engineer
    /// to decide from measurements after the feel gate. Removing it leaves AK-TR-1 untouched.
    /// </para>
    /// <para>
    /// All authoritative arithmetic is Q32.32 <see cref="Fixed"/> with the AK-TR-1 integer trig
    /// table (extended to a full circle by exact symmetry); there is no float and no System.Random.
    /// </para>
    /// </summary>
    public static class RealtimePrototypeRules
    {
        public const string PrototypeId = "AK-RT-PROTO-0";
        public const bool IsRanked = false;
        public const bool GrantsRewards = false;
        public const bool BotOnly = true;

        public const int TicksPerSecond = 60;
        /// <summary>60 s match.</summary>
        public const int MaxTicks = 60 * TicksPerSecond;
        public const int FireCooldownTicks = 36; // 0.6 s
        public const int ArrowLifetimeTicks = 150; // 2.5 s
        public const int ArrowDamageUnits = 20 * RulesConstants.HpUnitsPerHp;
        public const int StartHpUnits = RulesConstants.StartHpUnits;

        public static readonly Fixed ArenaHalfWidth = Fixed.FromInt(15);
        public static readonly Fixed MinAltitude = Fixed.FromInt(1);
        public static readonly Fixed MaxAltitude = Fixed.FromInt(9);
        public static readonly Fixed MoveSpeed = Fixed.FromInt(5);      // m/s horizontal
        public static readonly Fixed ClimbSpeed = Fixed.FromInt(3);     // m/s vertical (flying)
        public static readonly Fixed ArrowSpeed = Fixed.FromInt(22);    // m/s
        public static readonly Fixed Gravity = Fixed.FromRatio(-98, 10); // m/s^2 on arrows only
        public static readonly Fixed HitRadius = Fixed.FromRatio(6, 10);
        public static readonly Fixed MuzzleOffset = Fixed.FromRatio(7, 10);
        public static readonly Fixed DiagonalScale = Fixed.FromRatio(7071, 10000);
    }

    /// <summary>Full-circle integer trig built from the AK-TR-1 quarter-degree table by exact symmetry.</summary>
    public static class CircleTrig
    {
        public const int FullTurnQdeg = 1440;
        public const int HalfTurnQdeg = 720;

        public static int Normalize(int qdeg) => ((qdeg % FullTurnQdeg) + FullTurnQdeg) % FullTurnQdeg;

        public static Fixed Sin(int qdeg)
        {
            int a = Normalize(qdeg);
            if (a <= 360) return TrigTable.Sin(a);
            if (a <= 1080) return TrigTable.Sin(HalfTurnQdeg - a);  // sin(180° − x) = sin x
            return TrigTable.Sin(a - FullTurnQdeg);
        }

        public static Fixed Cos(int qdeg)
        {
            int a = Normalize(qdeg);
            if (a <= 360) return TrigTable.Cos(a);
            if (a <= 1080) return -TrigTable.Cos(HalfTurnQdeg - a); // cos(180° − x) = −cos x
            return TrigTable.Cos(a - FullTurnQdeg);
        }

        /// <summary>
        /// Angle of the vector (x, y) in quarter degrees, 0-1439 (0 = +x, 360 = +y), the nearest table
        /// angle found by exact cross-product comparisons (binary search over 0-90° in one quadrant).
        /// (0, 0) returns 0.
        /// </summary>
        public static int Atan2Qdeg(Fixed y, Fixed x)
        {
            if (x.Raw == 0 && y.Raw == 0) return 0;
            Fixed ax = Fixed.Abs(x), ay = Fixed.Abs(y);
            // Smallest a in [0, 360] with sin(a)·ax ≥ cos(a)·ay (monotonic in a).
            int lo = 0, hi = 360;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (TrigTable.Sin(mid) * ax >= TrigTable.Cos(mid) * ay) hi = mid;
                else lo = mid + 1;
            }
            int first = lo;
            if (first > 0)
            {
                // Pick the nearer of first and first − 1 by the residual |sin·ax − cos·ay|.
                Fixed r1 = Fixed.Abs(TrigTable.Sin(first) * ax - TrigTable.Cos(first) * ay);
                Fixed r0 = Fixed.Abs(TrigTable.Sin(first - 1) * ax - TrigTable.Cos(first - 1) * ay);
                if (r0 < r1) first--;
            }
            if (x.Raw >= 0 && y.Raw >= 0) return first;
            if (x.Raw < 0 && y.Raw >= 0) return HalfTurnQdeg - first;
            if (x.Raw < 0) return Normalize(HalfTurnQdeg + first);
            return Normalize(FullTurnQdeg - first);
        }
    }

    /// <summary>One archer's per-tick control: movement axes (−1, 0, +1), aim and trigger.</summary>
    public readonly struct RealtimeInput
    {
        public readonly sbyte MoveX;
        public readonly sbyte MoveZ;
        public readonly sbyte Climb;
        /// <summary>Heading around the vertical axis, quarter degrees (0 = +x, 360 = +z).</summary>
        public readonly int YawQdeg;
        /// <summary>Elevation, quarter degrees, −360..+360.</summary>
        public readonly int PitchQdeg;
        public readonly bool Fire;

        public RealtimeInput(int moveX, int moveZ, int climb, int yawQdeg, int pitchQdeg, bool fire)
        {
            MoveX = (sbyte)Math.Sign(moveX);
            MoveZ = (sbyte)Math.Sign(moveZ);
            Climb = (sbyte)Math.Sign(climb);
            YawQdeg = CircleTrig.Normalize(yawQdeg);
            PitchQdeg = Math.Max(TrigTable.MinQdeg, Math.Min(TrigTable.MaxQdeg, pitchQdeg));
            Fire = fire;
        }

        public static readonly RealtimeInput Idle = new RealtimeInput(0, 0, 0, 0, 0, false);
    }

    /// <summary>Public, simulated state of one archer.</summary>
    public sealed class ArcherState
    {
        public int Index { get; internal set; }
        public FixedVector3 Position { get; internal set; }
        public FixedVector3 Velocity { get; internal set; }
        public int HpUnits { get; internal set; } = RealtimePrototypeRules.StartHpUnits;
        public int CooldownTicks { get; internal set; }
        public int ShotsFired { get; internal set; }
        public int Hits { get; internal set; }

        public bool Alive => HpUnits > 0;

        internal ArcherState Clone() => (ArcherState)MemberwiseClone();
    }

    /// <summary>An arrow in flight.</summary>
    public sealed class ArrowState
    {
        public int Owner { get; internal set; }
        public FixedVector3 Position { get; internal set; }
        public FixedVector3 Velocity { get; internal set; }
        public int AgeTicks { get; internal set; }
    }

    /// <summary>What a controller may observe each tick: its own state and the opponent's visible motion and HP.</summary>
    public sealed class RealtimeObservation
    {
        public int Tick { get; internal set; }
        public ArcherState Self { get; internal set; }
        public ArcherState Opponent { get; internal set; }
    }

    /// <summary>Produces one archer's input each tick (a scripted human stand-in or the bot).</summary>
    public interface IRealtimeController
    {
        RealtimeInput Decide(RealtimeObservation observation);
    }

    public enum RealtimeOutcome : byte
    {
        InProgress = 0,
        Archer0Wins = 1,
        Archer1Wins = 2,
        Draw = 3,
    }

    /// <summary>
    /// The fixed-step prototype simulation. Each <see cref="Step"/> applies both inputs
    /// simultaneously: archers move (clamped to the arena), fire if their cooldown allows, then every
    /// arrow integrates (semi-implicit Euler, gravity on arrows only) and is tested against the
    /// opponent with an exact swept segment-sphere test, so fast arrows cannot tunnel. Damage from
    /// one tick is applied together; both reaching zero on the same tick is a draw; at the time
    /// limit the higher HP wins and equal HP draws.
    /// </summary>
    public sealed class RealtimeSimulation
    {
        private readonly ArcherState[] _archers = new ArcherState[2];
        private readonly List<ArrowState> _arrows = new List<ArrowState>();
        private readonly List<RealtimeInput[]> _inputLog = new List<RealtimeInput[]>();

        public int Tick { get; private set; }
        public RealtimeOutcome Outcome { get; private set; }
        public bool IsOver => Outcome != RealtimeOutcome.InProgress;
        public IReadOnlyList<ArrowState> Arrows => _arrows;
        public IReadOnlyList<RealtimeInput[]> InputLog => _inputLog;

        public ArcherState this[int index] => _archers[index];

        public RealtimeSimulation()
        {
            Fixed start = Fixed.FromInt(8);
            Fixed alt = Fixed.FromInt(3);
            _archers[0] = new ArcherState { Index = 0, Position = new FixedVector3(-start, alt, Fixed.Zero) };
            _archers[1] = new ArcherState { Index = 1, Position = new FixedVector3(start, alt, Fixed.Zero) };
        }

        public RealtimeObservation ObservationFor(int index) => new RealtimeObservation
        {
            Tick = Tick,
            Self = _archers[index].Clone(),
            Opponent = _archers[1 - index].Clone(),
        };

        /// <summary>Advances one fixed tick with both inputs.</summary>
        public void Step(RealtimeInput input0, RealtimeInput input1)
        {
            if (IsOver) throw new InvalidOperationException("The prototype round is over.");
            _inputLog.Add(new[] { input0, input1 });
            Move(_archers[0], input0);
            Move(_archers[1], input1);
            TryFire(_archers[0], input0);
            TryFire(_archers[1], input1);

            var damage = new int[2];
            for (int i = _arrows.Count - 1; i >= 0; i--)
            {
                ArrowState a = _arrows[i];
                FixedVector3 from = a.Position;
                a.Velocity = new FixedVector3(a.Velocity.X, a.Velocity.Y + RealtimePrototypeRules.Gravity.DivInt(RealtimePrototypeRules.TicksPerSecond), a.Velocity.Z);
                a.Position = a.Position + a.Velocity.DivInt(RealtimePrototypeRules.TicksPerSecond);
                a.AgeTicks++;
                ArcherState target = _archers[1 - a.Owner];
                if (SegmentHitsSphere(from, a.Position, target.Position, RealtimePrototypeRules.HitRadius))
                {
                    damage[target.Index] += RealtimePrototypeRules.ArrowDamageUnits;
                    _archers[a.Owner].Hits++;
                    _arrows.RemoveAt(i);
                    continue;
                }
                if (a.AgeTicks >= RealtimePrototypeRules.ArrowLifetimeTicks || a.Position.Y.Raw < 0 ||
                    Fixed.Abs(a.Position.X) > RealtimePrototypeRules.ArenaHalfWidth.MulInt(2) ||
                    Fixed.Abs(a.Position.Z) > RealtimePrototypeRules.ArenaHalfWidth.MulInt(2))
                    _arrows.RemoveAt(i);
            }
            for (int k = 0; k < 2; k++) _archers[k].HpUnits = Math.Max(0, _archers[k].HpUnits - damage[k]);
            Tick++;

            bool dead0 = !_archers[0].Alive, dead1 = !_archers[1].Alive;
            if (dead0 && dead1) Outcome = RealtimeOutcome.Draw;
            else if (dead0) Outcome = RealtimeOutcome.Archer1Wins;
            else if (dead1) Outcome = RealtimeOutcome.Archer0Wins;
            else if (Tick >= RealtimePrototypeRules.MaxTicks)
            {
                int h0 = _archers[0].HpUnits, h1 = _archers[1].HpUnits;
                Outcome = h0 > h1 ? RealtimeOutcome.Archer0Wins : h1 > h0 ? RealtimeOutcome.Archer1Wins : RealtimeOutcome.Draw;
            }
        }

        private static void Move(ArcherState s, RealtimeInput input)
        {
            Fixed speed = RealtimePrototypeRules.MoveSpeed;
            if (input.MoveX != 0 && input.MoveZ != 0) speed = speed * RealtimePrototypeRules.DiagonalScale;
            var v = new FixedVector3(speed.MulInt(input.MoveX), RealtimePrototypeRules.ClimbSpeed.MulInt(input.Climb), speed.MulInt(input.MoveZ));
            FixedVector3 p = s.Position + v.DivInt(RealtimePrototypeRules.TicksPerSecond);
            Fixed w = RealtimePrototypeRules.ArenaHalfWidth;
            p = new FixedVector3(Fixed.Clamp(p.X, -w, w),
                Fixed.Clamp(p.Y, RealtimePrototypeRules.MinAltitude, RealtimePrototypeRules.MaxAltitude),
                Fixed.Clamp(p.Z, -w, w));
            s.Velocity = v;
            s.Position = p;
            if (s.CooldownTicks > 0) s.CooldownTicks--;
        }

        private void TryFire(ArcherState s, RealtimeInput input)
        {
            if (!input.Fire || s.CooldownTicks > 0 || !s.Alive) return;
            FixedVector3 dir = Direction(input.YawQdeg, input.PitchQdeg);
            Fixed m = RealtimePrototypeRules.MuzzleOffset, v = RealtimePrototypeRules.ArrowSpeed;
            _arrows.Add(new ArrowState
            {
                Owner = s.Index,
                Position = s.Position + new FixedVector3(dir.X * m, dir.Y * m, dir.Z * m),
                Velocity = new FixedVector3(dir.X * v, dir.Y * v, dir.Z * v),
            });
            s.CooldownTicks = RealtimePrototypeRules.FireCooldownTicks;
            s.ShotsFired++;
        }

        /// <summary>Unit aim vector: (cos yaw cos pitch, sin pitch, sin yaw cos pitch).</summary>
        public static FixedVector3 Direction(int yawQdeg, int pitchQdeg)
        {
            Fixed cp = TrigTable.Cos(pitchQdeg);
            return new FixedVector3(CircleTrig.Cos(yawQdeg) * cp, TrigTable.Sin(pitchQdeg), CircleTrig.Sin(yawQdeg) * cp);
        }

        /// <summary>Exact closest-point test of segment [a, b] against a sphere (centre c, radius r).</summary>
        public static bool SegmentHitsSphere(FixedVector3 a, FixedVector3 b, FixedVector3 c, Fixed r)
        {
            FixedVector3 d = b - a;
            FixedVector3 f = c - a;
            Fixed dd = Dot(d, d);
            Fixed t = dd.Raw == 0 ? Fixed.Zero : Fixed.Clamp(Dot(f, d) / dd, Fixed.Zero, Fixed.One);
            FixedVector3 closest = a + new FixedVector3(d.X * t, d.Y * t, d.Z * t);
            FixedVector3 e = c - closest;
            return Dot(e, e) <= r * r;
        }

        private static Fixed Dot(FixedVector3 a, FixedVector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        /// <summary>SHA-256 over tick, outcome, both archers and every arrow (raw Q32.32 values).</summary>
        public string StateHashHex()
        {
            var w = new CanonicalWriter();
            w.Ascii(RealtimePrototypeRules.PrototypeId).I32(Tick).U8((int)Outcome);
            foreach (ArcherState s in _archers)
                w.I64(s.Position.X.Raw).I64(s.Position.Y.Raw).I64(s.Position.Z.Raw).I32(s.HpUnits).I32(s.CooldownTicks).I32(s.ShotsFired).I32(s.Hits);
            w.U32((uint)_arrows.Count);
            foreach (ArrowState a in _arrows)
                w.I32(a.Owner).I64(a.Position.X.Raw).I64(a.Position.Y.Raw).I64(a.Position.Z.Raw)
                 .I64(a.Velocity.X.Raw).I64(a.Velocity.Y.Raw).I64(a.Velocity.Z.Raw).I32(a.AgeTicks);
            return Hex.Encode(w.Sha256());
        }

        /// <summary>Runs a round with two controllers until it ends; returns the finished simulation.</summary>
        public static RealtimeSimulation Run(IRealtimeController archer0, IRealtimeController archer1)
        {
            var sim = new RealtimeSimulation();
            while (!sim.IsOver)
                sim.Step(archer0.Decide(sim.ObservationFor(0)), archer1.Decide(sim.ObservationFor(1)));
            return sim;
        }

        /// <summary>Re-executes a recorded input log; equal logs give equal state hashes.</summary>
        public static RealtimeSimulation Replay(IReadOnlyList<RealtimeInput[]> log)
        {
            var sim = new RealtimeSimulation();
            foreach (RealtimeInput[] inputs in log)
            {
                if (sim.IsOver) break;
                sim.Step(inputs[0], inputs[1]);
            }
            return sim;
        }
    }
}
