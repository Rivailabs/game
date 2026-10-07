using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>Why a projectile stopped flying.</summary>
    public enum TerminationReason : byte
    {
        None = 0,
        BodyContact = 1,
        BurstContact = 2,
        BurstMiss = 3,
        Ground = 4,
        ClashDestroyed = 5,
        OutOfBounds = 6,
        TickLimit = 7,
    }

    /// <summary>Experimental Brahmastra: a homing strike resolved at a fixed tick with no flight geometry.</summary>
    public readonly struct ScheduledStrike
    {
        public const int BrahmastraTick = 60;
        public const int BrahmastraDamageUnits = 60 * RulesConstants.HpUnitsPerHp;

        public readonly ProjectileId Id;
        public readonly int Tick;

        public ScheduledStrike(ProjectileId id, int tick)
        {
            Id = id;
            Tick = tick;
        }
    }

    /// <summary>A qualifying geometric contact (core, graze, burst or Brahmastra) before shields, cover and damage.</summary>
    public sealed class GeometricContact
    {
        public ProjectileId Projectile { get; }
        public PlayerSide Attacker => Projectile.Owner;
        public PlayerSide Target => CombatGeometry.Opponent(Projectile.Owner);
        public ContactKind Kind { get; }
        public int Tick { get; }
        public int SubTick { get; }
        public FixedVector3 Position { get; }
        public Fixed Distance { get; }

        public GeometricContact(ProjectileId projectile, ContactKind kind, int tick, int subTick, FixedVector3 position = default, Fixed distance = default)
        {
            if (kind == ContactKind.Miss) throw new ArgumentException("A geometric contact cannot be a miss.", nameof(kind));
            Projectile = projectile;
            Kind = kind;
            Tick = tick;
            SubTick = subTick;
            Position = position;
            Distance = distance;
        }

        /// <summary>Absolute time in sub-ticks since launch.</summary>
        public long TimeSubTicks => (long)(Tick - 1) * CombatGeometry.SubTicks + SubTick;

        public override string ToString() => Projectile + " " + Kind + " @" + Tick + ":" + SubTick;
    }

    /// <summary>One playback sample: absolute time (sub-ticks since launch) and authoritative position.</summary>
    public readonly struct TrackSample
    {
        public readonly long TimeSubTicks;
        public readonly FixedVector3 Position;

        public TrackSample(long timeSubTicks, FixedVector3 position)
        {
            TimeSubTicks = timeSubTicks;
            Position = position;
        }
    }

    /// <summary>
    /// Playback track of one projectile: the launch point, every tick-end position and every event
    /// point (clash, contact, termination). Motion inside a tick is linear, so piecewise-linear
    /// interpolation of these samples reproduces the simulated path.
    /// </summary>
    public sealed class ProjectileTrack
    {
        private readonly List<TrackSample> _samples = new List<TrackSample>();

        public ProjectileSpec Spec { get; }
        public IReadOnlyList<TrackSample> Samples => _samples;
        public TerminationReason Termination { get; internal set; }
        /// <summary>Body-plane outcome: Core/Graze for a contact; Miss when it crossed or never reached the plane.</summary>
        public ContactKind Contact { get; internal set; }
        public bool CrossedTargetPlane { get; internal set; }
        public int FinalMass { get; internal set; }
        public long EndTimeSubTicks => _samples.Count == 0 ? 0 : _samples[_samples.Count - 1].TimeSubTicks;

        internal ProjectileTrack(ProjectileSpec spec)
        {
            Spec = spec;
            FinalMass = spec.Mass;
        }

        internal void Add(long time, FixedVector3 position)
        {
            if (_samples.Count > 0 && _samples[_samples.Count - 1].TimeSubTicks == time)
                _samples[_samples.Count - 1] = new TrackSample(time, position);
            else
                _samples.Add(new TrackSample(time, position));
        }
    }

    /// <summary>Presentation helper: interpolates a track at any time for animation.</summary>
    public static class TrajectorySampler
    {
        /// <summary>True while the projectile exists at <paramref name="timeSubTicks"/>.</summary>
        public static bool IsAlive(ProjectileTrack track, long timeSubTicks) =>
            track.Samples.Count > 0 && timeSubTicks >= 0 && timeSubTicks <= track.EndTimeSubTicks;

        /// <summary>Position at a time, clamped to the track's lifetime, linearly interpolated in Q32.32.</summary>
        public static FixedVector3 Sample(ProjectileTrack track, long timeSubTicks)
        {
            var s = track.Samples;
            if (s.Count == 0) throw new InvalidOperationException("Empty track.");
            if (timeSubTicks <= s[0].TimeSubTicks) return s[0].Position;
            if (timeSubTicks >= s[s.Count - 1].TimeSubTicks) return s[s.Count - 1].Position;
            int lo = 0, hi = s.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (s[mid].TimeSubTicks <= timeSubTicks) lo = mid;
                else hi = mid;
            }
            long span = s[hi].TimeSubTicks - s[lo].TimeSubTicks;
            long t = timeSubTicks - s[lo].TimeSubTicks;
            return new FixedVector3(
                Lerp(s[lo].Position.X, s[hi].Position.X, t, span),
                Lerp(s[lo].Position.Y, s[hi].Position.Y, t, span),
                Lerp(s[lo].Position.Z, s[hi].Position.Z, t, span));
        }

        /// <summary>Samples a track at every whole tick from launch to its end (inclusive).</summary>
        public static IReadOnlyList<FixedVector3> SampleEveryTick(ProjectileTrack track)
        {
            var list = new List<FixedVector3>();
            long end = track.EndTimeSubTicks;
            for (long t = 0; t < end; t += CombatGeometry.SubTicks) list.Add(Sample(track, t));
            list.Add(Sample(track, end));
            return list;
        }

        private static Fixed Lerp(Fixed a, Fixed b, long t, long span)
        {
            // a + (b - a) * t / span, rounded ties-even with an exact 128-bit intermediate.
            return a + Fixed.FromRaw(Fixed.MulDivRoundHalfEven((b - a).Raw, t, span));
        }
    }

    /// <summary>Output of one volley's flight simulation.</summary>
    public sealed class SimulationResult
    {
        public CombatEventLog Log { get; }
        /// <summary>Qualifying contacts in authoritative processing order (time, type priority, projectile id).</summary>
        public IReadOnlyList<GeometricContact> Contacts { get; }
        public IReadOnlyList<ProjectileTrack> Tracks { get; }
        /// <summary>Number of ticks simulated (last tick with any activity).</summary>
        public int TicksSimulated { get; }

        internal SimulationResult(CombatEventLog log, IReadOnlyList<GeometricContact> contacts, IReadOnlyList<ProjectileTrack> tracks, int ticks)
        {
            Log = log;
            Contacts = contacts;
            Tracks = tracks;
            TicksSimulated = ticks;
        }
    }

    /// <summary>
    /// Deterministic swept flight simulation of one volley (step 6). 120 ticks per second, at most
    /// 360 ticks. Each tick applies its acceleration impulse once (v' = v + round(a/120)), then moves
    /// linearly (p' = p + round(v'/120)). Clashes, body-plane crossings and ground crossings are found
    /// analytically on that segment with exact integer arithmetic at 1/65,536-tick resolution and
    /// quantized upward, so fast projectiles cannot tunnel. Equal-time events resolve as clashes, then
    /// body contacts, then ground contacts, then expiry. No health changes happen here.
    /// </summary>
    public static class FlightSimulator
    {
        private const long S = CombatGeometry.SubTicks;

        private sealed class Flight
        {
            public ProjectileSpec Spec;
            public ProjectileTrack Track;
            public FixedVector3 P;   // position at tick start
            public FixedVector3 V;   // velocity (after this tick's impulse during the tick)
            public int Mass;
            public bool Active;
            public bool Crossed;
            // In-tick affine motion: scaled position (raw x 65,536) at sub-tick s is C + E*s.
            public long Cx, Cy, Cz, Ex, Ey, Ez;

            public long X(long s) => Cx + Ex * s;
            public long Y(long s) => Cy + Ey * s;
            public long Z(long s) => Cz + Ez * s;

            public FixedVector3 PositionAt(long s) => new FixedVector3(
                Fixed.FromRaw(Fixed.DivRoundHalfEven(X(s), S)),
                Fixed.FromRaw(Fixed.DivRoundHalfEven(Y(s), S)),
                Fixed.FromRaw(Fixed.DivRoundHalfEven(Z(s), S)));
        }

        /// <summary>Simulates projectiles of both players against the two defender poses.</summary>
        public static SimulationResult Simulate(IReadOnlyList<ProjectileSpec> projectiles, TargetPose poseA, TargetPose poseB,
            IReadOnlyList<ScheduledStrike> strikes = null)
        {
            var log = new CombatEventLog();
            return Simulate(projectiles, poseA, poseB, strikes, log);
        }

        internal static SimulationResult Simulate(IReadOnlyList<ProjectileSpec> projectiles, TargetPose poseA, TargetPose poseB,
            IReadOnlyList<ScheduledStrike> strikes, CombatEventLog log)
        {
            if (projectiles == null) throw new ArgumentNullException(nameof(projectiles));
            if (poseA.Side != PlayerSide.A || poseB.Side != PlayerSide.B) throw new ArgumentException("Poses must be for A and B respectively.");

            // Canonical order: owner, then index.
            var specs = new List<ProjectileSpec>(projectiles);
            specs.Sort((a, b) => a.Id.CompareTo(b.Id));
            for (int i = 1; i < specs.Count; i++)
                if (specs[i].Id.Equals(specs[i - 1].Id)) throw new ArgumentException("Duplicate projectile id " + specs[i].Id);

            var pending = new List<ScheduledStrike>();
            if (strikes != null) pending.AddRange(strikes);
            pending.Sort((a, b) => a.Id.CompareTo(b.Id));

            var flights = new List<Flight>(specs.Count);
            var tracks = new List<ProjectileTrack>(specs.Count);
            var contacts = new List<GeometricContact>();
            foreach (var spec in specs)
            {
                var f = new Flight { Spec = spec, Track = new ProjectileTrack(spec), P = spec.Position, V = spec.Velocity, Mass = spec.Mass, Active = true };
                f.Track.Add(0, f.P);
                flights.Add(f);
                tracks.Add(f.Track);
                log.Add(new CombatEvent(CombatEventType.Launch, spec.Owner, true, spec.Id, 0, 0,
                    position: spec.Position, velocity: spec.Velocity, mass: spec.Mass));
            }

            Fixed gravityPerTick = LaunchProfiles.Gravity.DivInt(RulesConstants.TicksPerSecond);
            int lastTick = 0;
            for (int tick = 1; tick <= RulesConstants.MaxTicksPerVolley; tick++)
            {
                bool any = pending.Count > 0;
                foreach (var f in flights) any |= f.Active;
                if (!any) break;
                lastTick = tick;

                // 1. Impulse once per tick, then the linear segment for this tick.
                foreach (var f in flights)
                {
                    if (!f.Active) continue;
                    Fixed dvy = f.Spec.Gravity ? gravityPerTick : Fixed.Zero;
                    Fixed dvz = tick <= f.Spec.CurveLastTick ? f.Spec.CurveAccelZ.DivInt(RulesConstants.TicksPerSecond) : Fixed.Zero;
                    f.V = new FixedVector3(f.V.X, f.V.Y + dvy, f.V.Z + dvz);
                    SetSegment(f, 0, f.P);
                }

                // 2. Process events inside the tick in time order.
                long sNow = 0;
                while (true)
                {
                    long best = long.MaxValue;
                    foreach (var pair in OpposingPairs(flights))
                        best = Math.Min(best, Positive(FirstClash(pair.Item1, pair.Item2, sNow)));
                    foreach (var f in flights)
                    {
                        if (!f.Active) continue;
                        if (!f.Spec.GroundBurst && !f.Crossed) best = Math.Min(best, Positive(PlaneCrossing(f, sNow)));
                        best = Math.Min(best, Positive(GroundCrossing(f, sNow)));
                    }
                    foreach (var st in pending)
                        if (st.Tick == tick) best = Math.Min(best, S);
                    if (best == long.MaxValue) break;
                    sNow = best;

                    // Clashes first: connected components of opposing projectiles in contact at sNow.
                    var touching = new List<Tuple<Flight, Flight>>();
                    foreach (var pair in OpposingPairs(flights))
                        if (InContact(pair.Item1, pair.Item2, sNow)) touching.Add(pair);
                    if (touching.Count > 0)
                    {
                        ResolveClashComponents(flights, touching, tick, sNow, log);
                        continue; // recompute pending contacts with the new velocities
                    }

                    // Body contacts (and scheduled strikes) at sNow, ascending projectile id.
                    var bodyBatch = new List<Tuple<ProjectileId, Flight>>();
                    foreach (var f in flights)
                        if (f.Active && !f.Spec.GroundBurst && !f.Crossed && PlaneCrossing(f, sNow) == sNow)
                            bodyBatch.Add(Tuple.Create(f.Spec.Id, f));
                    if (sNow == S)
                        foreach (var st in pending)
                            if (st.Tick == tick) bodyBatch.Add(Tuple.Create(st.Id, (Flight)null));
                    bodyBatch.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                    foreach (var item in bodyBatch)
                    {
                        if (item.Item2 == null)
                        {
                            log.Add(new CombatEvent(CombatEventType.BrahmastraStrike, item.Item1.Owner, true, item.Item1, tick, (int)sNow, ContactKind.Brahmastra));
                            contacts.Add(new GeometricContact(item.Item1, ContactKind.Brahmastra, tick, (int)sNow));
                            pending.RemoveAll(p => p.Id.Equals(item.Item1));
                            continue;
                        }
                        ResolveBodyCrossing(item.Item2, item.Item2.Spec.Owner == PlayerSide.A ? poseB : poseA, tick, sNow, log, contacts);
                    }

                    // Ground contacts at sNow.
                    foreach (var f in flights)
                    {
                        if (!f.Active || GroundCrossing(f, sNow) != sNow) continue;
                        ResolveGround(f, f.Spec.Owner == PlayerSide.A ? poseB : poseA, tick, sNow, log, contacts);
                    }
                }

                // 3. End of tick: commit positions, then expiry (bounds, then tick limit).
                foreach (var f in flights)
                {
                    if (!f.Active) continue;
                    f.P = f.PositionAt(S);
                    long t = (long)tick * S;
                    f.Track.Add(t, f.P);
                    if (OutOfBounds(f.P))
                        Terminate(f, TerminationReason.OutOfBounds, CombatEventType.OutOfBounds, tick, S, f.P, log);
                    else if (tick == RulesConstants.MaxTicksPerVolley)
                        Terminate(f, TerminationReason.TickLimit, CombatEventType.TickLimit, tick, S, f.P, log);
                }
            }

            return new SimulationResult(log, contacts, tracks, lastTick);
        }

        // ------------------------------------------------------------------ event handlers

        private static void ResolveBodyCrossing(Flight f, TargetPose pose, int tick, long s, CombatEventLog log, List<GeometricContact> contacts)
        {
            ContactKind kind = CombatGeometry.ClassifyBodyScaled(f.Y(s), f.Z(s), f.Spec.Radius, pose, f.Spec.JumpPierce);
            FixedVector3 at = f.PositionAt(s);
            Fixed distance = CombatGeometry.BodyAxisDistance(at.Y, at.Z, pose, f.Spec.JumpPierce);
            f.Crossed = true;
            f.Track.CrossedTargetPlane = true;
            if (kind == ContactKind.Miss)
            {
                log.Add(new CombatEvent(CombatEventType.BodyMiss, f.Spec.Owner, true, f.Spec.Id, tick, (int)s,
                    position: at, velocity: f.V, mass: f.Mass, distance: distance));
                return;
            }
            f.Track.Contact = kind;
            log.Add(new CombatEvent(CombatEventType.BodyContact, f.Spec.Owner, true, f.Spec.Id, tick, (int)s, kind,
                position: at, velocity: f.V, mass: f.Mass, distance: distance));
            contacts.Add(new GeometricContact(f.Spec.Id, kind, tick, (int)s, at, distance));
            f.Active = false;
            f.Track.Termination = TerminationReason.BodyContact;
            f.Track.Add(TimeOf(tick, s), at);
        }

        private static void ResolveGround(Flight f, TargetPose pose, int tick, long s, CombatEventLog log, List<GeometricContact> contacts)
        {
            FixedVector3 at = f.PositionAt(s);
            if (!f.Spec.GroundBurst)
            {
                Terminate(f, TerminationReason.Ground, CombatEventType.GroundImpact, tick, s, at, log);
                return;
            }
            ContactKind kind = CombatGeometry.ClassifyBurstScaled(f.X(s), f.Y(s), f.Z(s), pose);
            Fixed distance = CombatGeometry.BurstAxisDistance(at, pose);
            f.Active = false;
            f.Track.Add(TimeOf(tick, s), at);
            if (kind == ContactKind.Miss)
            {
                f.Track.Termination = TerminationReason.BurstMiss;
                log.Add(new CombatEvent(CombatEventType.BurstMiss, f.Spec.Owner, true, f.Spec.Id, tick, (int)s,
                    position: at, velocity: f.V, mass: f.Mass, distance: distance));
                return;
            }
            f.Track.Termination = TerminationReason.BurstContact;
            f.Track.Contact = kind;
            log.Add(new CombatEvent(CombatEventType.BurstContact, f.Spec.Owner, true, f.Spec.Id, tick, (int)s, kind,
                position: at, velocity: f.V, mass: f.Mass, distance: distance));
            contacts.Add(new GeometricContact(f.Spec.Id, kind, tick, (int)s, at, distance));
        }

        private static void Terminate(Flight f, TerminationReason reason, CombatEventType type, int tick, long s, FixedVector3 at, CombatEventLog log)
        {
            f.Active = false;
            f.Track.Termination = reason;
            f.Track.Add(TimeOf(tick, s), at);
            log.Add(new CombatEvent(type, f.Spec.Owner, true, f.Spec.Id, tick, (int)s, position: at, velocity: f.V, mass: f.Mass));
        }

        /// <summary>
        /// Simultaneous clash rule: connected components of contacting opposing projectiles. Equal side
        /// totals remove the component; otherwise one projectile of the heavier side survives (greatest
        /// remaining mass, then smallest index) with the side-total difference and its velocity halved once.
        /// </summary>
        private static void ResolveClashComponents(List<Flight> flights, List<Tuple<Flight, Flight>> touching, int tick, long s, CombatEventLog log)
        {
            // Union-find over the canonical flight order.
            var index = new Dictionary<Flight, int>();
            for (int i = 0; i < flights.Count; i++) index[flights[i]] = i;
            var parent = new int[flights.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            var involved = new bool[flights.Count];
            foreach (var pair in touching)
            {
                int a = index[pair.Item1], b = index[pair.Item2];
                involved[a] = involved[b] = true;
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }

            for (int root = 0; root < flights.Count; root++)
            {
                if (!involved[root] || Find(root) != root) continue;
                var members = new List<Flight>();
                int massA = 0, massB = 0;
                for (int i = 0; i < flights.Count; i++)
                {
                    if (!involved[i] || Find(i) != root) continue;
                    members.Add(flights[i]);
                    if (flights[i].Spec.Owner == PlayerSide.A) massA += flights[i].Mass;
                    else massB += flights[i].Mass;
                }

                Flight survivor = null;
                if (massA != massB)
                {
                    PlayerSide heavy = massA > massB ? PlayerSide.A : PlayerSide.B;
                    foreach (var m in members)
                    {
                        if (m.Spec.Owner != heavy) continue;
                        if (survivor == null || m.Mass > survivor.Mass ||
                            (m.Mass == survivor.Mass && m.Spec.Index < survivor.Spec.Index))
                            survivor = m;
                    }
                }

                foreach (var m in members)
                {
                    FixedVector3 at = m.PositionAt(s);
                    if (m == survivor)
                    {
                        m.Mass = Math.Abs(massA - massB);
                        m.Track.FinalMass = m.Mass;
                        m.V = m.V.DivInt(2);
                        // Continue the remainder of this tick from the clash point with the new velocity;
                        // gravity and curvature are not applied a second time.
                        SetSegment(m, s, null);
                        m.Track.Add(TimeOf(tick, s), at);
                        log.Add(new CombatEvent(CombatEventType.ClashSurvived, m.Spec.Owner, true, m.Spec.Id, tick, (int)s,
                            position: at, velocity: m.V, mass: m.Mass));
                    }
                    else
                    {
                        m.Track.FinalMass = 0;
                        m.Mass = 0;
                        Terminate(m, TerminationReason.ClashDestroyed, CombatEventType.ClashDestroyed, tick, s, at, log);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ swept geometry

        /// <summary>
        /// Sets the in-tick motion so that the scaled position at sub-tick <paramref name="from"/> is
        /// unchanged (or equals <paramref name="start"/> at s = 0) and the slope is round(V/120).
        /// </summary>
        private static void SetSegment(Flight f, long from, FixedVector3? start)
        {
            FixedVector3 e = f.V.DivInt(RulesConstants.TicksPerSecond);
            if (start.HasValue)
            {
                f.Cx = CombatGeometry.Scale(start.Value.X);
                f.Cy = CombatGeometry.Scale(start.Value.Y);
                f.Cz = CombatGeometry.Scale(start.Value.Z);
            }
            else
            {
                f.Cx = checked(f.Cx + (f.Ex - e.X.Raw) * from);
                f.Cy = checked(f.Cy + (f.Ey - e.Y.Raw) * from);
                f.Cz = checked(f.Cz + (f.Ez - e.Z.Raw) * from);
            }
            f.Ex = e.X.Raw;
            f.Ey = e.Y.Raw;
            f.Ez = e.Z.Raw;
        }

        /// <summary>First sub-tick ≥ sNow at which the centre reaches the defender plane, or −1.</summary>
        private static long PlaneCrossing(Flight f, long sNow)
        {
            PlayerSide target = CombatGeometry.Opponent(f.Spec.Owner);
            long plane = CombatGeometry.Scale(CombatGeometry.PlaneX(target));
            int dir = CombatGeometry.ForwardSign(f.Spec.Owner);
            long remaining = dir > 0 ? plane - f.X(sNow) : f.X(sNow) - plane;
            if (remaining <= 0) return sNow;
            long speed = dir > 0 ? f.Ex : -f.Ex;
            if (speed <= 0) return -1;
            long s = sNow + CeilDiv(remaining, speed);
            return s <= S ? s : -1;
        }

        /// <summary>First sub-tick ≥ sNow at which the centre reaches y ≤ 0, or −1.</summary>
        private static long GroundCrossing(Flight f, long sNow)
        {
            long height = f.Y(sNow);
            if (height <= 0) return sNow;
            if (f.Ey >= 0) return -1;
            long s = sNow + CeilDiv(height, -f.Ey);
            return s <= S ? s : -1;
        }

        /// <summary>
        /// First sub-tick ≥ sNow at which two projectiles' centres are within the sum of their radii,
        /// or −1. Uses the exact convex quadratic |D0 + E s|² − R² on integer sub-ticks.
        /// </summary>
        private static long FirstClash(Flight a, Flight b, long sNow)
        {
            long r = CombatGeometry.Scale(a.Spec.Radius + b.Spec.Radius);
            Int128Lite r2 = Int128Lite.Mul(r, r);
            long dx = a.Cx - b.Cx, dy = a.Cy - b.Cy, dz = a.Cz - b.Cz;
            long ex = a.Ex - b.Ex, ey = a.Ey - b.Ey, ez = a.Ez - b.Ez;

            bool Inside(long s)
            {
                long x = dx + ex * s, y = dy + ey * s, z = dz + ez * s;
                return Int128Lite.Mul(x, x) + Int128Lite.Mul(y, y) + Int128Lite.Mul(z, z) <= r2;
            }
            bool Rising(long s) // derivative sign: (D0 + E s) . E >= 0
            {
                long x = dx + ex * s, y = dy + ey * s, z = dz + ez * s;
                return (Int128Lite.Mul(x, ex) + Int128Lite.Mul(y, ey) + Int128Lite.Mul(z, ez)).CompareTo(Int128Lite.Zero) >= 0;
            }

            if (Inside(sNow)) return sNow;
            if (ex == 0 && ey == 0 && ez == 0) return -1;

            // sm: first sub-tick where the distance stops decreasing.
            long sm;
            if (!Rising(S)) sm = S + 1;
            else
            {
                long lo = sNow, hi = S;
                while (lo < hi)
                {
                    long mid = lo + (hi - lo) / 2;
                    if (Rising(mid)) hi = mid;
                    else lo = mid + 1;
                }
                sm = lo;
            }

            long lastDecreasing = sm - 1;
            if (lastDecreasing >= sNow && Inside(lastDecreasing))
            {
                long lo = sNow, hi = lastDecreasing; // monotone decreasing distance on [lo, hi]
                while (lo < hi)
                {
                    long mid = lo + (hi - lo) / 2;
                    if (Inside(mid)) hi = mid;
                    else lo = mid + 1;
                }
                return lo;
            }
            if (sm <= S && sm > sNow && Inside(sm)) return sm;
            return -1;
        }

        private static bool InContact(Flight a, Flight b, long s)
        {
            long r = CombatGeometry.Scale(a.Spec.Radius + b.Spec.Radius);
            long x = a.X(s) - b.X(s), y = a.Y(s) - b.Y(s), z = a.Z(s) - b.Z(s);
            return Int128Lite.Mul(x, x) + Int128Lite.Mul(y, y) + Int128Lite.Mul(z, z) <= Int128Lite.Mul(r, r);
        }

        private static IEnumerable<Tuple<Flight, Flight>> OpposingPairs(List<Flight> flights)
        {
            for (int i = 0; i < flights.Count; i++)
            {
                var a = flights[i];
                if (!a.Active || !a.Spec.CanClash || a.Spec.Owner != PlayerSide.A) continue;
                for (int j = 0; j < flights.Count; j++)
                {
                    var b = flights[j];
                    if (!b.Active || !b.Spec.CanClash || b.Spec.Owner != PlayerSide.B) continue;
                    yield return Tuple.Create(a, b);
                }
            }
        }

        private static bool OutOfBounds(FixedVector3 p) =>
            p.X < CombatGeometry.MinX || p.X > CombatGeometry.MaxX || p.Y > CombatGeometry.MaxY ||
            p.Z < CombatGeometry.MinZ || p.Z > CombatGeometry.MaxZ;

        private static long TimeOf(int tick, long s) => (long)(tick - 1) * S + s;

        private static long Positive(long s) => s < 0 ? long.MaxValue : s;

        private static long CeilDiv(long num, long den) => (num + den - 1) / den;
    }
}
