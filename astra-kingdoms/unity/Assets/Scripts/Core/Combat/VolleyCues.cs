using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Presentation;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Combat
{
    /// <summary>What a presentation cue shows.</summary>
    public enum CueKind : byte
    {
        /// <summary>A player's projectiles leave the bow (archer release marker).</summary>
        Release = 0,
        /// <summary>The effective dodge pose for the whole resolution window.</summary>
        Dodge = 1,
        /// <summary>Ash Shield raised before flight.</summary>
        ShieldRaised = 2,
        /// <summary>Iron Wall cover raised before flight.</summary>
        WallRaised = 3,
        /// <summary>A projectile survived a mid-air clash (it keeps flying with reduced mass).</summary>
        ClashSurvived = 4,
        /// <summary>A projectile was destroyed in a mid-air clash.</summary>
        ClashCancelled = 5,
        /// <summary>A contact that dealt damage.</summary>
        Impact = 6,
        /// <summary>A contact absorbed by Ash Shield (no damage, no effects).</summary>
        Blocked = 7,
        /// <summary>A projectile ended without contact (ground, out of bounds, burst miss, time limit).</summary>
        Miss = 8,
        /// <summary>Flood Arrow removed the target's cover.</summary>
        CoverRemoved = 9,
        /// <summary>HP before/after for one player once every arrow has landed.</summary>
        HealthChange = 10,
    }

    /// <summary>
    /// One presentation cue derived from the authoritative volley record. Every cue carries a
    /// non-colour glyph and a caption key so the outcome stays understandable without sound or
    /// colour (tickets 31, 33 and 34).
    /// </summary>
    public sealed class VolleyCue
    {
        public CueKind Kind { get; internal set; }
        /// <summary>Authoritative time in sub-ticks since launch.</summary>
        public long TimeSubTicks { get; internal set; }
        /// <summary>Owner of the projectile for flight cues; affected player otherwise.</summary>
        public PlayerSide Side { get; internal set; }
        public bool HasProjectile { get; internal set; }
        public ProjectileId Projectile { get; internal set; }
        public Element Element { get; internal set; }
        /// <summary>World position in rules metres (x along the arena, y up, z lateral).</summary>
        public V3 Position { get; internal set; }
        /// <summary>Damage (Impact), or HP after (HealthChange), in HP units.</summary>
        public int Amount { get; internal set; }
        /// <summary>HP before (HealthChange only).</summary>
        public int AmountBefore { get; internal set; }
        public ContactKind Contact { get; internal set; }
        public Dodge Dodge { get; internal set; }
        /// <summary>The target had cover at contact time (cover factor applied).</summary>
        public bool Covered { get; internal set; }
        public bool ForcedByNet { get; internal set; }
        public bool ReversedByCyclone { get; internal set; }
        public Rational ElementFactor { get; internal set; } = Rational.One;
        /// <summary>Text glyph shown with the effect (shape cue, never colour only).</summary>
        public string Glyph { get; internal set; }
        /// <summary>Localization key of the floating caption.</summary>
        public string CaptionKey { get; internal set; }

        public override string ToString() => Kind + " " + Side + " t=" + TimeSubTicks + (Amount != 0 ? " amount=" + Amount : string.Empty);
    }

    /// <summary>
    /// Turns a resolved <see cref="VolleyResult"/> into an ordered list of presentation cues. Every
    /// cue comes from the engine's tracks, event log or volley report; nothing is simulated or
    /// invented here, so playback agrees with the hit, miss and collision record (ticket 31).
    /// </summary>
    public static class VolleyCueBuilder
    {
        public static readonly IReadOnlyList<CueKind> AllKinds = (CueKind[])Enum.GetValues(typeof(CueKind));

        public static string GlyphFor(CueKind kind, Dodge dodge = Dodge.None)
        {
            switch (kind)
            {
                case CueKind.Release: return "➶";
                case CueKind.Dodge: return dodge == Dodge.Left ? "←" : dodge == Dodge.Right ? "→" : dodge == Dodge.Jump ? "↑" : "■";
                case CueKind.ShieldRaised: return "◯";
                case CueKind.WallRaised: return "▮";
                case CueKind.ClashSurvived: return "✦";
                case CueKind.ClashCancelled: return "✕";
                case CueKind.Impact: return "✸";
                case CueKind.Blocked: return "◎";
                case CueKind.Miss: return "○";
                case CueKind.CoverRemoved: return "▯";
                default: return "✚";
            }
        }

        public static string CaptionKeyFor(CueKind kind, ContactKind contact = ContactKind.Core)
        {
            switch (kind)
            {
                case CueKind.Release: return "cue.release";
                case CueKind.Dodge: return "cue.dodge";
                case CueKind.ShieldRaised: return "cue.shieldRaised";
                case CueKind.WallRaised: return "cue.wallRaised";
                case CueKind.ClashSurvived: return "cue.clashSurvived";
                case CueKind.ClashCancelled: return "cue.clashCancelled";
                case CueKind.Impact: return contact == ContactKind.Graze || contact == ContactKind.BurstGraze ? "cue.graze" : "cue.hit";
                case CueKind.Blocked: return "cue.blocked";
                case CueKind.Miss: return "cue.miss";
                case CueKind.CoverRemoved: return "cue.coverRemoved";
                default: return "cue.health";
            }
        }

        public static List<VolleyCue> Build(VolleyResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var cues = new List<VolleyCue>();
            VolleyExplanation e = result.Explanation;
            IReadOnlyList<ProjectileTrack> tracks = result.Simulation != null ? result.Simulation.Tracks : Array.Empty<ProjectileTrack>();

            // Pre-flight: releases, dodges, shields and walls hold for the whole window.
            foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
            {
                PlayerVolleyReport r = e?[side];
                bool fired = false;
                foreach (ProjectileTrack t in tracks) fired |= t.Spec.Owner == side;
                if (fired || (r != null && r.IsBrahmastra))
                    cues.Add(Cue(CueKind.Release, 0, side, r != null ? r.DefensiveElement : Element.Neutral));
                if (r == null) continue;
                if (r.EffectiveDodge != Dodge.None)
                {
                    VolleyCue d = Cue(CueKind.Dodge, 0, side, r.DefensiveElement);
                    d.Dodge = r.EffectiveDodge;
                    d.ForcedByNet = r.DodgeForcedByNet;
                    d.ReversedByCyclone = r.DodgeReversedByCyclone;
                    d.Glyph = GlyphFor(CueKind.Dodge, r.EffectiveDodge);
                    cues.Add(d);
                }
                if (r.AshShieldRaised) cues.Add(Cue(CueKind.ShieldRaised, 0, side, r.DefensiveElement));
                if (r.IronWallRaised) cues.Add(Cue(CueKind.WallRaised, 0, side, r.DefensiveElement));
            }

            // Flight: clashes survived (non-terminal) and cover removal from the event log.
            if (result.Log != null)
            {
                foreach (CombatEvent ev in result.Log.Events)
                {
                    if (ev.Type == CombatEventType.ClashSurvived && ev.HasProjectile)
                    {
                        VolleyCue c = Cue(CueKind.ClashSurvived, ev.TimeSubTicks, ev.Subject, ElementOf(tracks, ev.Projectile));
                        c.HasProjectile = true;
                        c.Projectile = ev.Projectile;
                        c.Position = TrajectoryPreview.ToV3(ev.Position);
                        cues.Add(c);
                    }
                    else if (ev.Type == CombatEventType.CoverRemoved)
                    {
                        cues.Add(Cue(CueKind.CoverRemoved, ev.TimeSubTicks, ev.Subject, Element.Varuna));
                    }
                    else if (ev.Type == CombatEventType.BrahmastraStrike && e != null)
                    {
                        PlayerSide target = CombatGeometry.Opponent(ev.Subject);
                        foreach (ContactReport cr in e[target].Incoming)
                            if (cr.Kind == ContactKind.Brahmastra && cr.Attacker == ev.Subject)
                                cues.Add(ContactCue(cr, ev.TimeSubTicks, TrajectoryPreview.ToV3(ev.Position), Element.Neutral));
                    }
                }
            }

            // Terminal cue per projectile, from its authoritative track termination.
            foreach (ProjectileTrack t in tracks)
            {
                long time = t.EndTimeSubTicks;
                V3 pos = t.Samples.Count > 0 ? TrajectoryPreview.ToV3(t.Samples[t.Samples.Count - 1].Position) : TrajectoryPreview.ToV3(t.Spec.Position);
                switch (t.Termination)
                {
                    case TerminationReason.ClashDestroyed:
                        VolleyCue c = Cue(CueKind.ClashCancelled, time, t.Spec.Owner, t.Spec.Element);
                        c.HasProjectile = true;
                        c.Projectile = t.Spec.Id;
                        c.Position = pos;
                        cues.Add(c);
                        break;
                    case TerminationReason.BodyContact:
                    case TerminationReason.BurstContact:
                        ContactReport report = FindContact(e, t.Spec.Id);
                        if (report != null) cues.Add(ContactCue(report, time, pos, t.Spec.Element));
                        else cues.Add(MissCue(t, time, pos)); // a contact that qualified for nothing
                        break;
                    default:
                        cues.Add(MissCue(t, time, pos));
                        break;
                }
            }

            // Health after every arrow has landed (the HUD releases its held HP at the same moment).
            long end = 0;
            foreach (VolleyCue c in cues) end = Math.Max(end, c.TimeSubTicks);
            if (e != null)
            {
                foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
                {
                    PlayerVolleyReport r = e[side];
                    VolleyCue h = Cue(CueKind.HealthChange, end, side, r.DefensiveElement);
                    h.AmountBefore = r.HpBeforeUnits;
                    h.Amount = r.HpAfterUnits;
                    cues.Add(h);
                }
            }

            cues.Sort((a, b) => a.TimeSubTicks != b.TimeSubTicks ? a.TimeSubTicks.CompareTo(b.TimeSubTicks) : ((int)a.Kind).CompareTo((int)b.Kind));
            return cues;
        }

        private static VolleyCue Cue(CueKind kind, long time, PlayerSide side, Element element) => new VolleyCue
        {
            Kind = kind,
            TimeSubTicks = time,
            Side = side,
            Element = element,
            Glyph = GlyphFor(kind),
            CaptionKey = CaptionKeyFor(kind),
        };

        private static VolleyCue MissCue(ProjectileTrack t, long time, V3 pos)
        {
            VolleyCue m = Cue(CueKind.Miss, time, t.Spec.Owner, t.Spec.Element);
            m.HasProjectile = true;
            m.Projectile = t.Spec.Id;
            m.Position = pos;
            return m;
        }

        private static VolleyCue ContactCue(ContactReport r, long time, V3 pos, Element element)
        {
            CueKind kind = r.Blocked ? CueKind.Blocked : CueKind.Impact;
            return new VolleyCue
            {
                Kind = kind,
                TimeSubTicks = time,
                Side = r.Target,
                HasProjectile = true,
                Projectile = r.Projectile,
                Element = element,
                Position = pos,
                Amount = r.Blocked ? 0 : r.DamageUnits + r.ChainBonusUnits,
                Contact = r.Kind,
                Covered = r.TargetCovered && r.CoverFactor.Numerator != r.CoverFactor.Denominator,
                ElementFactor = r.ElementFactor,
                Glyph = GlyphFor(kind),
                CaptionKey = CaptionKeyFor(kind, r.Kind),
            };
        }

        private static ContactReport FindContact(VolleyExplanation e, ProjectileId id)
        {
            if (e == null) return null;
            foreach (ContactReport c in e[CombatGeometry.Opponent(id.Owner)].Incoming)
                if (c.Projectile.Equals(id)) return c;
            return null;
        }

        private static Element ElementOf(IReadOnlyList<ProjectileTrack> tracks, ProjectileId id)
        {
            foreach (ProjectileTrack t in tracks)
                if (t.Spec.Id.Equals(id)) return t.Spec.Element;
            return Element.Neutral;
        }
    }

    /// <summary>
    /// Real-time schedule of one volley's presentation inside the resolution window: a short draw
    /// lead-in, the release marker (projectiles spawn), the authoritative flight compressed to fit,
    /// and the explanation hold. The total never exceeds the resolution budget (2.5 s).
    /// </summary>
    public sealed class VolleyPresentationPlan
    {
        public const double DefaultLeadInSeconds = 0.25;

        public double LeadInSeconds { get; }
        public PlaybackTimeline Flight { get; }
        public IReadOnlyList<VolleyCue> Cues { get; }
        public double TotalSeconds => LeadInSeconds + Flight.TotalSeconds;

        public VolleyPresentationPlan(VolleyResult result, double totalBudgetSeconds = RulesConstants.ResolutionReplayMaxMs / 1000.0,
            double holdSeconds = PlaybackTimeline.DefaultHoldSeconds, double leadInSeconds = DefaultLeadInSeconds)
        {
            if (leadInSeconds < 0 || leadInSeconds + holdSeconds >= totalBudgetSeconds) throw new ArgumentOutOfRangeException(nameof(leadInSeconds));
            LeadInSeconds = leadInSeconds;
            Flight = PlaybackTimeline.For(result, totalBudgetSeconds - leadInSeconds, holdSeconds);
            Cues = VolleyCueBuilder.Build(result);
        }

        /// <summary>Real seconds since the window opened at which a cue should appear.</summary>
        public double ShowAt(VolleyCue cue)
        {
            if (cue.Kind == CueKind.Dodge || cue.Kind == CueKind.ShieldRaised || cue.Kind == CueKind.WallRaised) return 0; // visible protection from the start
            if (Flight.SimEndSubTicks <= 0) return LeadInSeconds;
            return LeadInSeconds + Flight.FlightSeconds * cue.TimeSubTicks / Flight.SimEndSubTicks;
        }

        /// <summary>Authoritative flight time to display at a real time (before release: launch time 0).</summary>
        public long SimTimeAt(double playbackSeconds) => Flight.SimTimeAt(playbackSeconds - LeadInSeconds);

        public bool Released(double playbackSeconds) => playbackSeconds >= LeadInSeconds;

        public bool FlightFinished(double playbackSeconds) => playbackSeconds >= LeadInSeconds + Flight.FlightSeconds;
    }
}
