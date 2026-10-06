using System;
using System.Collections.Generic;

namespace AstraKingdoms.Client.Presentation
{
    /// <summary>
    /// Named attachment points every archer rig must provide (tickets 26 and 28; plan "Asset
    /// contract": keep character, bow, arrow and procedural bowstring as separate assets joined at
    /// named attachments). Names are the transform names an imported rig must use.
    /// </summary>
    public static class ArcherAttachments
    {
        public const string HandLeft = "hand_l";
        public const string HandRight = "hand_r";
        /// <summary>Where the bow's grip sits (child of the bow hand).</summary>
        public const string BowGrip = "bow_grip";
        /// <summary>The string's nocking point; it follows the draw hand while drawn.</summary>
        public const string StringNock = "string_nock";
        /// <summary>Where a released arrow appears (the arrow rest at the grip).</summary>
        public const string ArrowSpawn = "arrow_spawn";

        public static readonly IReadOnlyList<string> Required = new[] { HandLeft, HandRight, BowGrip, StringNock, ArrowSpawn };

        /// <summary>Required names missing from a rig's transform names (empty = contract met).</summary>
        public static IReadOnlyList<string> Missing(IEnumerable<string> transformNames)
        {
            var have = new HashSet<string>(transformNames ?? Array.Empty<string>(), StringComparer.Ordinal);
            var missing = new List<string>();
            foreach (string n in Required)
                if (!have.Contains(n)) missing.Add(n);
            return missing;
        }
    }

    /// <summary>The required archer clip set (plan: "Initial asset budgets for the Astra template").</summary>
    public enum ArcherClip : byte
    {
        Idle = 0,
        Draw = 1,
        Hold = 2,
        Release = 3,
        Recover = 4,
        Hit = 5,
        DodgeLeft = 6,
        DodgeRight = 7,
        Jump = 8,
        Defeat = 9,
        Victory = 10,
    }

    /// <summary>Requests the presentation sends to the archer.</summary>
    public enum ArcherTrigger : byte
    {
        Draw = 0,
        Release = 1,
        Hit = 2,
        DodgeLeft = 3,
        DodgeRight = 4,
        Jump = 5,
        Defeat = 6,
        Victory = 7,
        /// <summary>Back to Idle immediately (new match or rematch).</summary>
        Reset = 8,
    }

    /// <summary>
    /// A presentation pose of the placeholder archer. Angles are degrees, offsets metres. Root
    /// translation for dodges and jumps is NOT here: the arena places the body at the rules'
    /// collision pose, so the visible protection always matches the resolved dodge.
    /// </summary>
    public struct ArcherPose
    {
        /// <summary>Bow arm elevation: 0 hanging, 90 level aim, 160 raised in victory.</summary>
        public double BowArmRaise;
        /// <summary>String hand pulled back, 0 (rest) to 1 (full draw).</summary>
        public double Draw;
        /// <summary>Forward (+) or backward (-) bend of the spine.</summary>
        public double SpinePitch;
        /// <summary>Sideways lean toward the archer's local right (+) or left (-).</summary>
        public double Lean;
        public double KneeBend;
        /// <summary>Vertical drop of the hips for crouches and falls (negative = lower).</summary>
        public double RootDrop;
        public double HeadTilt;

        public static ArcherPose Lerp(ArcherPose a, ArcherPose b, double t) => new ArcherPose
        {
            BowArmRaise = a.BowArmRaise + (b.BowArmRaise - a.BowArmRaise) * t,
            Draw = a.Draw + (b.Draw - a.Draw) * t,
            SpinePitch = a.SpinePitch + (b.SpinePitch - a.SpinePitch) * t,
            Lean = a.Lean + (b.Lean - a.Lean) * t,
            KneeBend = a.KneeBend + (b.KneeBend - a.KneeBend) * t,
            RootDrop = a.RootDrop + (b.RootDrop - a.RootDrop) * t,
            HeadTilt = a.HeadTilt + (b.HeadTilt - a.HeadTilt) * t,
        };

        /// <summary>Largest body-joint difference, excluding the string draw (which snaps by design at release).</summary>
        public static double MaxBodyDelta(ArcherPose a, ArcherPose b)
        {
            ArcherPose x = a, y = b;
            x.Draw = 0;
            y.Draw = 0;
            return MaxDelta(x, y);
        }

        /// <summary>Largest per-channel difference (angles in degrees; Draw scaled by 90, RootDrop by 100 per metre).</summary>
        public static double MaxDelta(ArcherPose a, ArcherPose b)
        {
            double m = Math.Abs(a.BowArmRaise - b.BowArmRaise);
            m = Math.Max(m, Math.Abs(a.Draw - b.Draw) * 90);
            m = Math.Max(m, Math.Abs(a.SpinePitch - b.SpinePitch));
            m = Math.Max(m, Math.Abs(a.Lean - b.Lean));
            m = Math.Max(m, Math.Abs(a.KneeBend - b.KneeBend));
            m = Math.Max(m, Math.Abs(a.RootDrop - b.RootDrop) * 100);
            return Math.Max(m, Math.Abs(a.HeadTilt - b.HeadTilt));
        }
    }

    /// <summary>A gameplay event marker inside a clip (plan "Motion" contract: gameplay event markers).</summary>
    public sealed class ClipEvent
    {
        public const string ReleaseArrow = "OnReleaseArrow";

        public string FunctionName { get; }
        public double NormalizedTime { get; }

        public ClipEvent(string functionName, double normalizedTime)
        {
            FunctionName = functionName;
            NormalizedTime = normalizedTime;
        }
    }

    /// <summary>Clip metadata and placeholder keyframes (the Motion contract fields that apply).</summary>
    public sealed class ArcherClipSpec
    {
        public ArcherClip Clip { get; }
        public string Name => Clip.ToString();
        public double Seconds { get; }
        public bool Loop { get; }
        /// <summary>Terminal clips hold their last pose and accept only Reset.</summary>
        public bool Terminal { get; }
        /// <summary>All clips are in place: root motion comes from authoritative poses, never from animation.</summary>
        public bool InPlace => true;
        public IReadOnlyList<ClipEvent> Events { get; }
        /// <summary>(normalized time, pose) keys in ascending time; first at 0, last at 1.</summary>
        public IReadOnlyList<KeyValuePair<double, ArcherPose>> Keys { get; }

        public ArcherClipSpec(ArcherClip clip, double seconds, bool loop, bool terminal, IReadOnlyList<KeyValuePair<double, ArcherPose>> keys,
            IReadOnlyList<ClipEvent> events = null)
        {
            Clip = clip;
            Seconds = seconds;
            Loop = loop;
            Terminal = terminal;
            Keys = keys;
            Events = events ?? Array.Empty<ClipEvent>();
        }

        /// <summary>Pose at a normalized time (smoothstep between keys, so speed is zero at every key).</summary>
        public ArcherPose Sample(double normalized)
        {
            if (normalized <= Keys[0].Key) return Keys[0].Value;
            for (int i = 1; i < Keys.Count; i++)
            {
                if (normalized > Keys[i].Key) continue;
                double span = Keys[i].Key - Keys[i - 1].Key;
                double t = span <= 0 ? 1 : (normalized - Keys[i - 1].Key) / span;
                t = t * t * (3 - 2 * t);
                return ArcherPose.Lerp(Keys[i - 1].Value, Keys[i].Value, t);
            }
            return Keys[Keys.Count - 1].Value;
        }

        public ArcherPose Start => Keys[0].Value;
        public ArcherPose End => Keys[Keys.Count - 1].Value;
    }

    /// <summary>
    /// The placeholder pose library for the required clip set. It lets every gameplay state play
    /// before production motion exists (plan: "The pilot can demonstrate every gameplay state with
    /// simple poses and procedural movement"). Production clips replace the keys, not the contract:
    /// names, loop flags, durations, terminal flags and the release marker stay.
    /// </summary>
    public static class ArcherPoseLibrary
    {
        /// <summary>Normalized time of the release marker inside the Release clip.</summary>
        public const double ReleaseMarker = 0.15;

        public static readonly ArcherPose Rest = new ArcherPose { BowArmRaise = 20, KneeBend = 5 };
        public static readonly ArcherPose FullDraw = new ArcherPose { BowArmRaise = 90, Draw = 1, KneeBend = 10, SpinePitch = 3 };

        private static readonly Dictionary<ArcherClip, ArcherClipSpec> Specs = Build();

        public static IReadOnlyCollection<ArcherClipSpec> All => Specs.Values;

        public static ArcherClipSpec Get(ArcherClip clip) => Specs[clip];

        private static KeyValuePair<double, ArcherPose> K(double t, ArcherPose p) => new KeyValuePair<double, ArcherPose>(t, p);

        private static ArcherPose With(ArcherPose p, Func<ArcherPose, ArcherPose> f) => f(p);

        private static Dictionary<ArcherClip, ArcherClipSpec> Build()
        {
            ArcherPose breathe = With(Rest, p => { p.SpinePitch = 2; p.HeadTilt = 1; return p; });
            ArcherPose tremble = With(FullDraw, p => { p.Draw = 0.97; return p; });
            ArcherPose released = With(FullDraw, p => { p.Draw = 0; return p; });
            ArcherPose follow = With(released, p => { p.BowArmRaise = 86; p.SpinePitch = 1; return p; });
            ArcherPose recoil = With(Rest, p => { p.SpinePitch = -15; p.HeadTilt = 10; p.KneeBend = 15; return p; });
            ArcherPose leanLeft = With(Rest, p => { p.Lean = -20; p.KneeBend = 25; p.SpinePitch = 5; return p; });
            ArcherPose leanRight = With(leanLeft, p => { p.Lean = 20; return p; });
            ArcherPose crouch = With(Rest, p => { p.KneeBend = 35; p.RootDrop = -0.1; return p; });
            ArcherPose tuck = With(Rest, p => { p.KneeBend = 40; return p; });
            ArcherPose fallen = new ArcherPose { BowArmRaise = 0, SpinePitch = 60, KneeBend = 70, RootDrop = -0.5, HeadTilt = 20 };
            ArcherPose raised = With(Rest, p => { p.BowArmRaise = 160; p.SpinePitch = -5; p.HeadTilt = -5; return p; });

            var list = new[]
            {
                new ArcherClipSpec(ArcherClip.Idle, 2.0, loop: true, terminal: false, new[] { K(0, Rest), K(0.5, breathe), K(1, Rest) }),
                new ArcherClipSpec(ArcherClip.Draw, 0.35, false, false, new[] { K(0, Rest), K(1, FullDraw) }),
                new ArcherClipSpec(ArcherClip.Hold, 1.0, true, false, new[] { K(0, FullDraw), K(0.5, tremble), K(1, FullDraw) }),
                new ArcherClipSpec(ArcherClip.Release, 0.2, false, false, new[] { K(0, FullDraw), K(ReleaseMarker, released), K(1, follow) },
                    new[] { new ClipEvent(ClipEvent.ReleaseArrow, ReleaseMarker) }),
                new ArcherClipSpec(ArcherClip.Recover, 0.4, false, false, new[] { K(0, follow), K(1, Rest) }),
                new ArcherClipSpec(ArcherClip.Hit, 0.35, false, false, new[] { K(0, Rest), K(0.3, recoil), K(1, Rest) }),
                new ArcherClipSpec(ArcherClip.DodgeLeft, 0.4, false, false, new[] { K(0, Rest), K(0.3, leanLeft), K(0.7, leanLeft), K(1, Rest) }),
                new ArcherClipSpec(ArcherClip.DodgeRight, 0.4, false, false, new[] { K(0, Rest), K(0.3, leanRight), K(0.7, leanRight), K(1, Rest) }),
                new ArcherClipSpec(ArcherClip.Jump, 0.5, false, false, new[] { K(0, Rest), K(0.2, crouch), K(0.5, tuck), K(1, Rest) }),
                new ArcherClipSpec(ArcherClip.Defeat, 0.8, false, true, new[] { K(0, Rest), K(1, fallen) }),
                new ArcherClipSpec(ArcherClip.Victory, 0.8, false, true, new[] { K(0, Rest), K(1, raised) }),
            };
            var d = new Dictionary<ArcherClip, ArcherClipSpec>();
            foreach (ArcherClipSpec s in list) d.Add(s.Clip, s);
            return d;
        }

        /// <summary>Clip that follows a finished non-looping, non-terminal clip.</summary>
        public static ArcherClip Next(ArcherClip clip)
        {
            switch (clip)
            {
                case ArcherClip.Draw: return ArcherClip.Hold;
                case ArcherClip.Release: return ArcherClip.Recover;
                default: return ArcherClip.Idle;
            }
        }

        /// <summary>Cross-fade length for a triggered transition into <paramref name="to"/>.</summary>
        public static double BlendSeconds(ArcherClip to)
        {
            switch (to)
            {
                case ArcherClip.Release: return 0.03;
                case ArcherClip.Hit: return 0.05;
                case ArcherClip.Defeat:
                case ArcherClip.Victory: return 0.15;
                default: return 0.08;
            }
        }
    }

    /// <summary>What happened during one <see cref="ArcherAnimator.Advance"/>.</summary>
    public struct ArcherStep
    {
        /// <summary>The release marker was crossed: spawn the projectile now (exactly once per release).</summary>
        public bool ReleaseMarker;
        /// <summary>A clip entered during the step (the last one if several), or null.</summary>
        public ArcherClip? Entered;
    }

    /// <summary>
    /// Engine-independent archer state machine (ticket 27). The Unity <c>Animator</c> controller
    /// built by the editor script mirrors these states and transitions; this model drives the
    /// placeholder procedural poses and is what the dotnet tests verify: legal transitions, a single
    /// release event per shot, terminal states, and continuous poses across every transition
    /// (automatic transitions join matching keys; triggered ones cross-fade from the current pose).
    /// </summary>
    public sealed class ArcherAnimator
    {
        private ArcherPose _blendFrom;
        private double _blendLeft;
        private double _blendTotal;
        private bool _releaseQueued;
        private bool _releaseFiredThisClip;

        /// <summary>Cross-fade speed limit (degrees per second) used to lengthen blends between distant poses.</summary>
        public const double MaxBlendDegreesPerSecond = 400;

        public ArcherClip State { get; private set; } = ArcherClip.Idle;
        /// <summary>Seconds since the current clip started.</summary>
        public double Time { get; private set; }

        public ArcherClipSpec Spec => ArcherPoseLibrary.Get(State);
        public bool IsTerminal => Spec.Terminal;

        public double Normalized
        {
            get
            {
                double n = Time / Spec.Seconds;
                if (Spec.Loop) return n - Math.Floor(n);
                return n > 1 ? 1 : n;
            }
        }

        /// <summary>Requests a transition; false when it is not legal from the current state.</summary>
        public bool Fire(ArcherTrigger trigger)
        {
            if (trigger == ArcherTrigger.Reset)
            {
                _releaseQueued = false;
                Enter(ArcherClip.Idle, blend: 0);
                return true;
            }
            if (IsTerminal) return false;
            switch (trigger)
            {
                case ArcherTrigger.Draw:
                    if (State != ArcherClip.Idle && State != ArcherClip.Recover) return false;
                    Enter(ArcherClip.Draw, ArcherPoseLibrary.BlendSeconds(ArcherClip.Draw));
                    return true;
                case ArcherTrigger.Release:
                    if (State == ArcherClip.Draw)
                    {
                        _releaseQueued = true; // release as soon as the draw completes
                        return true;
                    }
                    if (State != ArcherClip.Hold) return false;
                    Enter(ArcherClip.Release, ArcherPoseLibrary.BlendSeconds(ArcherClip.Release));
                    return true;
                case ArcherTrigger.Hit:
                    Enter(ArcherClip.Hit, ArcherPoseLibrary.BlendSeconds(ArcherClip.Hit));
                    return true;
                case ArcherTrigger.DodgeLeft:
                case ArcherTrigger.DodgeRight:
                case ArcherTrigger.Jump:
                    if (State == ArcherClip.Draw || State == ArcherClip.Hold || (State == ArcherClip.Release && !_releaseFiredThisClip))
                        return false; // never cancel a shot before its arrow leaves the bow
                    ArcherClip to = trigger == ArcherTrigger.Jump ? ArcherClip.Jump : trigger == ArcherTrigger.DodgeLeft ? ArcherClip.DodgeLeft : ArcherClip.DodgeRight;
                    Enter(to, ArcherPoseLibrary.BlendSeconds(to));
                    return true;
                case ArcherTrigger.Defeat:
                    Enter(ArcherClip.Defeat, ArcherPoseLibrary.BlendSeconds(ArcherClip.Defeat));
                    return true;
                case ArcherTrigger.Victory:
                    Enter(ArcherClip.Victory, ArcherPoseLibrary.BlendSeconds(ArcherClip.Victory));
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Advances time, follows automatic transitions and reports the release marker.</summary>
        public ArcherStep Advance(double seconds)
        {
            var step = new ArcherStep();
            if (seconds < 0) seconds = 0;
            if (_blendLeft > 0) _blendLeft = Math.Max(0, _blendLeft - seconds);
            for (int guard = 0; guard < 16; guard++)
            {
                ArcherClipSpec spec = Spec;
                Time += seconds;
                if (State == ArcherClip.Release && !_releaseFiredThisClip && Time >= ArcherPoseLibrary.ReleaseMarker * spec.Seconds)
                {
                    _releaseFiredThisClip = true;
                    step.ReleaseMarker = true;
                }
                if (spec.Loop || spec.Terminal || Time < spec.Seconds) break;
                // Automatic transition at the clip end; carry the remaining time into the next clip.
                seconds = Time - spec.Seconds;
                Time = spec.Seconds;
                ArcherClip next = State == ArcherClip.Draw && _releaseQueued ? ArcherClip.Release : ArcherPoseLibrary.Next(State);
                if (next == ArcherClip.Release) _releaseQueued = false;
                Enter(next, blend: next == ArcherClip.Release ? ArcherPoseLibrary.BlendSeconds(ArcherClip.Release) : 0);
                step.Entered = next;
            }
            if (Spec.Terminal && Time > Spec.Seconds) Time = Spec.Seconds;
            return step;
        }

        /// <summary>The pose to display now (clip sample, cross-faded from the previous pose).</summary>
        public ArcherPose CurrentPose()
        {
            ArcherPose clip = Spec.Sample(Normalized);
            if (_blendLeft <= 0 || _blendTotal <= 0) return clip;
            double t = 1 - _blendLeft / _blendTotal;
            t = t * t * (3 - 2 * t);
            return ArcherPose.Lerp(_blendFrom, clip, t);
        }

        private void Enter(ArcherClip clip, double blend)
        {
            ArcherPose current = CurrentPose();
            if (blend > 0)
            {
                // Longer cross-fades for bigger pose changes, so no joint moves faster than the limit.
                double distance = ArcherPose.MaxBodyDelta(current, ArcherPoseLibrary.Get(clip).Start);
                blend = Math.Max(blend, distance / MaxBlendDegreesPerSecond);
            }
            _releaseFiredThisClip = false;
            if (clip == ArcherClip.Draw) _releaseQueued = false;
            State = clip;
            Time = 0;
            _blendFrom = current;
            _blendTotal = blend;
            _blendLeft = blend;
        }
    }
}
