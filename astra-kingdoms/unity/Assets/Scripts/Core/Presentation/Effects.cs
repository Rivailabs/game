using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Presentation
{
    /// <summary>Which moment an effect marks.</summary>
    public enum EffectKind : byte
    {
        ProjectileTrail = 0,
        Impact = 1,
        Clash = 2,
        ShieldBlock = 3,
        Miss = 4,
        DodgeStreak = 5,
        CoverDust = 6,
        StatusAura = 7,
    }

    /// <summary>How an element's particles move (identity cue independent of colour).</summary>
    public enum MotionPattern : byte
    {
        Steady = 0,
        /// <summary>Agni: rising, flickering sparks.</summary>
        Flicker = 1,
        /// <summary>Vayu: particles orbit the path.</summary>
        Swirl = 2,
        /// <summary>Prithvi: heavy chunks tumble and drop.</summary>
        Tumble = 3,
        /// <summary>Vidyut: jagged zigzag jumps.</summary>
        Zigzag = 4,
        /// <summary>Varuna: smooth sine ripple.</summary>
        Ripple = 5,
    }

    /// <summary>Particle silhouette (identity cue independent of colour).</summary>
    public enum ParticleShape : byte
    {
        Ring = 0,
        Flame = 1,
        Spiral = 2,
        Stone = 3,
        Bolt = 4,
        Wave = 5,
    }

    /// <summary>Visual identity of one element's effects: shape + motion + trail spacing, then colour.</summary>
    public sealed class ElementEffectStyle
    {
        public Element Element { get; }
        public ParticleShape Shape { get; }
        public MotionPattern Motion { get; }
        /// <summary>Trail spacing in metres between emitted glyphs (dashed vs dense reads differently).</summary>
        public double TrailSpacing { get; }
        /// <summary>Impact burst pattern: number of radial spokes (0 = ring only).</summary>
        public int ImpactSpokes { get; }

        public ElementEffectStyle(Element element, ParticleShape shape, MotionPattern motion, double trailSpacing, int impactSpokes)
        {
            Element = element;
            Shape = shape;
            Motion = motion;
            TrailSpacing = trailSpacing;
            ImpactSpokes = impactSpokes;
        }

        private static readonly ElementEffectStyle[] Styles =
        {
            new ElementEffectStyle(Element.Neutral, ParticleShape.Ring, MotionPattern.Steady, 0.40, 0),
            new ElementEffectStyle(Element.Agni, ParticleShape.Flame, MotionPattern.Flicker, 0.15, 8),
            new ElementEffectStyle(Element.Vayu, ParticleShape.Spiral, MotionPattern.Swirl, 0.25, 3),
            new ElementEffectStyle(Element.Prithvi, ParticleShape.Stone, MotionPattern.Tumble, 0.35, 5),
            new ElementEffectStyle(Element.Vidyut, ParticleShape.Bolt, MotionPattern.Zigzag, 0.20, 4),
            new ElementEffectStyle(Element.Varuna, ParticleShape.Wave, MotionPattern.Ripple, 0.30, 0),
        };

        public static IReadOnlyList<ElementEffectStyle> All => Styles;

        public static ElementEffectStyle For(Element e)
        {
            foreach (ElementEffectStyle s in Styles)
                if (s.Element == e) return s;
            return Styles[0];
        }

        /// <summary>Particle offset from the path at time t (seconds) for particle index i (presentation only).</summary>
        public V3 Offset(int i, double t)
        {
            double phase = i * 2.399963; // golden angle spreads particles evenly
            switch (Motion)
            {
                case MotionPattern.Flicker: return new V3(0, 0.08 * ((i % 3) + Frac(t * 3 + i * 0.37)), 0.05 * Math.Sin(t * 20 + phase));
                case MotionPattern.Swirl: return new V3(0, 0.12 * Math.Sin(t * 12 + phase), 0.12 * Math.Cos(t * 12 + phase));
                case MotionPattern.Tumble: return new V3(0, -0.15 * Frac(t * 1.5 + i * 0.2), 0.04 * Math.Sin(phase));
                case MotionPattern.Zigzag: return new V3(0, ((int)Math.Floor(t * 16 + i) % 2 == 0 ? 0.1 : -0.1), 0);
                case MotionPattern.Ripple: return new V3(0, 0.08 * Math.Sin(t * 8 + i * 0.6), 0);
                default: return V3.Zero;
            }
        }

        private static double Frac(double x) => x - Math.Floor(x);
    }

    /// <summary>Default particle counts per effect kind (all within the per-emitter ceiling).</summary>
    public static class EffectParticles
    {
        public static int For(EffectKind kind)
        {
            switch (kind)
            {
                case EffectKind.ProjectileTrail: return 16;
                case EffectKind.Impact: return 32;
                case EffectKind.Clash: return 24;
                case EffectKind.ShieldBlock: return 20;
                case EffectKind.Miss: return 12;
                case EffectKind.DodgeStreak: return 8;
                case EffectKind.CoverDust: return 12;
                default: return 10;
            }
        }

        /// <summary>Higher wins when the budget is full: outcomes (impact, clash, shield) outrank decoration.</summary>
        public static int Priority(EffectKind kind)
        {
            switch (kind)
            {
                case EffectKind.Impact:
                case EffectKind.Clash:
                case EffectKind.ShieldBlock: return 3;
                case EffectKind.Miss:
                case EffectKind.DodgeStreak:
                case EffectKind.CoverDust: return 2;
                case EffectKind.ProjectileTrail: return 1;
                default: return 0;
            }
        }
    }

    /// <summary>A live effect slot handed out by <see cref="EffectBudgetPool"/>.</summary>
    public sealed class EffectSlot
    {
        public int Index { get; }
        public EffectKind Kind { get; internal set; }
        public Element Element { get; internal set; }
        public int Particles { get; internal set; }
        public double StartedAt { get; internal set; }
        public bool Active { get; internal set; }
        /// <summary>Bumped on every reuse so stale handles can be detected.</summary>
        public int Generation { get; internal set; }

        internal EffectSlot(int index)
        {
            Index = index;
        }
    }

    /// <summary>
    /// Pooled effect slots under the plan's effect budget (ticket 32): at most 12 active emitters
    /// and 64 live particles per emitter. Slots are preallocated and reused (no per-shot
    /// allocation). When full, a request steals the oldest active effect of lower or equal
    /// priority; if every active effect outranks it, the request is refused. Outcome effects
    /// (impact, clash, shield) therefore always show.
    /// </summary>
    public sealed class EffectBudgetPool
    {
        private readonly EffectSlot[] _slots;

        public int Capacity => _slots.Length;
        public int MaxParticles { get; }
        public int ActiveCount { get; private set; }
        public int PeakActive { get; private set; }
        public int Refused { get; private set; }
        public int Stolen { get; private set; }

        /// <summary>Raised when an active slot is reclaimed for a more important effect (the view must stop it).</summary>
        public event Action<EffectSlot> SlotStolen;

        public EffectBudgetPool(int capacity = AssetBudgets.ActiveEmitters, int maxParticles = AssetBudgets.ParticlesPerEmitter)
        {
            if (capacity <= 0 || capacity > AssetBudgets.ActiveEmitters) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (maxParticles <= 0 || maxParticles > AssetBudgets.ParticlesPerEmitter) throw new ArgumentOutOfRangeException(nameof(maxParticles));
            MaxParticles = maxParticles;
            _slots = new EffectSlot[capacity];
            for (int i = 0; i < capacity; i++) _slots[i] = new EffectSlot(i);
        }

        public IReadOnlyList<EffectSlot> Slots => _slots;

        /// <summary>Starts an effect, or returns null when the budget is full of more important effects.</summary>
        public EffectSlot Acquire(EffectKind kind, Element element, double now, int particles = -1)
        {
            EffectSlot slot = null;
            foreach (EffectSlot s in _slots)
            {
                if (s.Active) continue;
                slot = s;
                break;
            }
            if (slot == null)
            {
                int priority = EffectParticles.Priority(kind);
                foreach (EffectSlot s in _slots)
                {
                    if (EffectParticles.Priority(s.Kind) > priority) continue;
                    if (slot == null || s.StartedAt < slot.StartedAt) slot = s;
                }
                if (slot == null)
                {
                    Refused++;
                    return null;
                }
                Stolen++;
                SlotStolen?.Invoke(slot);
                ActiveCount--;
            }
            int n = particles < 0 ? EffectParticles.For(kind) : particles;
            slot.Kind = kind;
            slot.Element = element;
            slot.Particles = Math.Min(Math.Max(0, n), MaxParticles);
            slot.StartedAt = now;
            slot.Active = true;
            slot.Generation++;
            ActiveCount++;
            PeakActive = Math.Max(PeakActive, ActiveCount);
            return slot;
        }

        public void Release(EffectSlot slot)
        {
            if (slot == null || !slot.Active) return;
            slot.Active = false;
            ActiveCount--;
        }

        public void ReleaseAll()
        {
            foreach (EffectSlot s in _slots) Release(s);
        }

        /// <summary>Total live particles across active slots.</summary>
        public int LiveParticles
        {
            get
            {
                int n = 0;
                foreach (EffectSlot s in _slots)
                    if (s.Active) n += s.Particles;
                return n;
            }
        }
    }
}
