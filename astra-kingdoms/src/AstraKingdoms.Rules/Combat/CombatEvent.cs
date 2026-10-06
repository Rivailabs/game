using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>Typed combat events. Flight events come from the simulator; the rest from volley resolution.</summary>
    public enum CombatEventType : byte
    {
        // ---- Pre-flight (steps 2-5) ----
        Cleansed = 1,
        AbilitySuppressed = 2,
        QuakePitchShift = 3,
        AshShieldRaised = 4,
        IronWallRaised = 5,
        DodgeForcedByNet = 6,
        DodgeReversedByCyclone = 7,
        ForestChargeSpent = 8,
        BrahmastraSpent = 9,

        // ---- Flight (step 6) ----
        Launch = 20,
        ClashSurvived = 21,
        ClashDestroyed = 22,
        BodyContact = 23,
        BodyMiss = 24,
        GroundImpact = 25,
        BurstContact = 26,
        BurstMiss = 27,
        OutOfBounds = 28,
        TickLimit = 29,
        BrahmastraStrike = 30,

        // ---- Contact defence and hit effects (steps 7-8) ----
        ShieldBlock = 40,
        CoverRemoved = 41,
        Damage = 42,
        ChainBonus = 43,
        StatusQueued = 44,

        // ---- Health and settle (steps 9-10) ----
        BurnDamage = 60,
        Heal = 61,
        HealthUpdate = 62,
        BaselinePushed = 63,
        StatusExpired = 64,
        DuelEnded = 65,
    }

    /// <summary>Status kinds tracked per player within a duel.</summary>
    public enum StatusKind : byte
    {
        None = 0,
        Burn = 1,
        Shock = 2,
        Net = 3,
        Quake = 4,
        Veil = 5,
        IronWall = 6,
        Push = 7,
    }

    /// <summary>Source of healing in the simultaneous health batch.</summary>
    public enum HealSource : byte
    {
        None = 0,
        Ocean = 1,
        River = 2,
    }

    /// <summary>
    /// One authoritative combat event. Projectile identity uses (round, volley, owner, index); each
    /// event adds its type, tick/sub-tick time and a sequence number. Rendering interpolates these
    /// events; it never decides damage itself.
    /// </summary>
    public sealed class CombatEvent
    {
        /// <summary>Authoritative sequence number within the volley's log (0-based).</summary>
        public int Sequence { get; internal set; }
        public CombatEventType Type { get; }
        /// <summary>Player the event concerns: projectile owner for flight events, affected player otherwise.</summary>
        public PlayerSide Subject { get; }
        public bool HasProjectile { get; }
        public ProjectileId Projectile { get; }
        /// <summary>Tick 1..360 during which the event occurred (0 before flight).</summary>
        public int Tick { get; }
        /// <summary>Sub-tick position 0..65,536 within <see cref="Tick"/> (quantized upward).</summary>
        public int SubTick { get; }
        public ContactKind Contact { get; }
        public StatusKind Status { get; }
        public HealSource HealSource { get; }
        public FixedVector3 Position { get; }
        public FixedVector3 Velocity { get; }
        public int Mass { get; }
        /// <summary>HP units (damage, healing, new HP) or another integer payload described by the type.</summary>
        public int Amount { get; }
        /// <summary>Distance to the relevant capsule axis for contact/miss events (presentation value).</summary>
        public Fixed Distance { get; }

        internal CombatEvent(CombatEventType type, PlayerSide subject, bool hasProjectile, ProjectileId projectile,
            int tick, int subTick, ContactKind contact = ContactKind.Miss, StatusKind status = StatusKind.None,
            HealSource healSource = HealSource.None, FixedVector3 position = default, FixedVector3 velocity = default,
            int mass = 0, int amount = 0, Fixed distance = default)
        {
            Type = type;
            Subject = subject;
            HasProjectile = hasProjectile;
            Projectile = projectile;
            Tick = tick;
            SubTick = subTick;
            Contact = contact;
            Status = status;
            HealSource = healSource;
            Position = position;
            Velocity = velocity;
            Mass = mass;
            Amount = amount;
            Distance = distance;
        }

        /// <summary>Absolute event time in sub-ticks since launch: (tick − 1) x 65,536 + sub-tick.</summary>
        public long TimeSubTicks => Tick <= 0 ? 0 : (long)(Tick - 1) * CombatGeometry.SubTicks + SubTick;

        /// <summary>Canonical single-line encoding (raw fixed-point integers, invariant culture).</summary>
        public string ToCanonicalString()
        {
            var sb = new StringBuilder(160);
            sb.Append(Sequence.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(Type).Append('|').Append(Subject).Append('|');
            if (HasProjectile) sb.Append(Projectile.ToString());
            sb.Append('|').Append(Tick.ToString(CultureInfo.InvariantCulture))
              .Append(':').Append(SubTick.ToString(CultureInfo.InvariantCulture))
              .Append('|').Append(Contact).Append('|').Append(Status).Append('|').Append(HealSource)
              .Append('|').Append(Raw(Position)).Append('|').Append(Raw(Velocity))
              .Append('|').Append(Mass.ToString(CultureInfo.InvariantCulture))
              .Append('|').Append(Amount.ToString(CultureInfo.InvariantCulture))
              .Append('|').Append(Distance.Raw.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public override string ToString()
        {
            string id = HasProjectile ? " " + Projectile : string.Empty;
            return "#" + Sequence + " t" + Tick + ":" + SubTick + " " + Type + " " + Subject + id +
                   (Contact != ContactKind.Miss ? " " + Contact : string.Empty) +
                   (Status != StatusKind.None ? " " + Status : string.Empty) +
                   (Amount != 0 ? " amount=" + Amount : string.Empty);
        }

        private static string Raw(FixedVector3 v) =>
            v.X.Raw.ToString(CultureInfo.InvariantCulture) + "," + v.Y.Raw.ToString(CultureInfo.InvariantCulture) + "," +
            v.Z.Raw.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Ordered, append-only event log with canonical serialization for determinism checks.</summary>
    public sealed class CombatEventLog
    {
        private readonly List<CombatEvent> _events = new List<CombatEvent>();

        public IReadOnlyList<CombatEvent> Events => _events;

        public int Count => _events.Count;

        internal CombatEvent Add(CombatEvent e)
        {
            e.Sequence = _events.Count;
            _events.Add(e);
            return e;
        }

        internal void AddRange(IEnumerable<CombatEvent> events)
        {
            foreach (var e in events) Add(e);
        }

        /// <summary>Canonical text: one <see cref="CombatEvent.ToCanonicalString"/> line per event.</summary>
        public string ToCanonicalText()
        {
            var sb = new StringBuilder();
            foreach (var e in _events) sb.Append(e.ToCanonicalString()).Append('\n');
            return sb.ToString();
        }

        /// <summary>UTF-8 bytes of <see cref="ToCanonicalText"/>; identical inputs give identical bytes.</summary>
        public byte[] ToCanonicalBytes() => Encoding.UTF8.GetBytes(ToCanonicalText());
    }
}
