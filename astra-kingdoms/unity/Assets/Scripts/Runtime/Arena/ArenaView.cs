using System.Collections.Generic;
using AstraKingdoms.Client.Combat;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Presentation;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using UnityEngine;

namespace AstraKingdoms.Client.Arena
{
    /// <summary>
    /// Duel presentation (tickets 26-36). Fighters, the ground and the camera come from the
    /// script-built scene (or are created here when missing). Each fighter's capsule stays as the
    /// rules' body volume (hidden behind the placeholder archer). A resolved volley plays from a
    /// <see cref="VolleyPresentationPlan"/>: the archers draw, the release marker spawns the
    /// projectiles, which then follow the engine's tracks compressed into the 2.5 s window, and every
    /// clash, impact, block, miss, dodge and cover cue from <see cref="VolleyCueBuilder"/> is shown
    /// with a pooled element effect and a caption. No physics, colliders or animation callbacks
    /// decide anything; the release marker only decides <i>when</i> the recorded flight starts.
    /// </summary>
    public sealed class ArenaView : MonoBehaviour
    {
        private const int MaxProjectiles = 8;
        private const float FlightStartFallbackSeconds = 0.1f;

        private readonly List<LineRenderer> _previewLines = new List<LineRenderer>();
        private readonly List<GameObject> _previewDots = new List<GameObject>();
        private readonly GameObject[] _projectiles = new GameObject[MaxProjectiles];
        private readonly bool[] _ended = new bool[MaxProjectiles];

        private Transform[] _fighters;
        private Renderer[] _fighterRenderers;
        private ArcherPresenter[] _archers;
        private Camera _camera;
        private Vector3 _cameraHome;
        private Material _lineMaterial;
        private EffectPoolView _effects;
        private CombatFeedbackView _feedback;
        private readonly CameraShake _shake = new CameraShake();
        private GameObject _props;
        private string _variant = ArenaVariants.Courtyard;

        private ResolvedVolley _playing;
        private VolleyPresentationPlan _plan;
        private bool[] _cueShown;
        private float _elapsed;
        private float _flightStart = -1f;
        private bool _flightDone;
        private bool _reducedMotion;

        public bool IsPlaying => _playing != null;
        public bool FlightFinished => _playing == null || _flightDone;
        public float Speed { get; set; } = 1f;
        public string Variant => _variant;
        public Transform Fighter(PlayerSide side) => _fighters[(int)side];
        public ArcherPresenter Archer(PlayerSide side) => _archers[(int)side];
        public EffectPoolView Effects => _effects;

        public event System.Action FlightCompleted;
        public event System.Action<PlayerSide> HitShown;

        /// <summary>Services for captions, sound and haptics (optional; tests run without them).</summary>
        public ClientContext Ctx { get; set; }

        public void Init(bool reducedShake)
        {
            _shake.Reduced = reducedShake;
            _fighters = new Transform[2];
            _fighterRenderers = new Renderer[2];
            _archers = new ArcherPresenter[2];
            _camera = Camera.main;
            if (_camera == null) _camera = ArenaBuilder.CreateCamera(null).GetComponent<Camera>();
            _cameraHome = _camera.transform.position;
            Shader shader = Shader.Find("Sprites/Default");
            for (int i = 0; i < 2; i++)
            {
                var side = (PlayerSide)i;
                GameObject go = GameObject.Find(i == 0 ? ArenaLayout.FighterAName : ArenaLayout.FighterBName);
                if (go == null) go = ArenaBuilder.CreateFighter(side, null);
                _fighters[i] = go.transform;
                _fighterRenderers[i] = go.GetComponent<Renderer>();
            }
            // Sprites/Default is in Unity's always-included shaders; fall back to the fighter's material.
            if (shader != null) _lineMaterial = new Material(shader);
            else if (_fighterRenderers[0] != null && _fighterRenderers[0].sharedMaterial != null) _lineMaterial = new Material(_fighterRenderers[0].sharedMaterial);
            GameObject ground = GameObject.Find("Ground");
            if (ground == null) _props = ArenaVariantBuilder.Build(_variant, null);

            for (int i = 0; i < 2; i++)
            {
                var side = (PlayerSide)i;
                ArcherRig rig = ArcherRigBuilder.CreatePlaceholder(side, transform, side == PlayerSide.A ? UI.UiTheme.PlayerA : UI.UiTheme.PlayerB);
                var presenter = rig.gameObject.AddComponent<ArcherPresenter>();
                presenter.Init(rig, _fighters[i], _lineMaterial);
                presenter.ReleaseArrow += OnReleaseArrow;
                _archers[i] = presenter;
                // The capsule remains the rules' body volume (tests and layout use it) but the archer is what people see.
                if (_fighterRenderers[i] != null) _fighterRenderers[i].enabled = false;
            }

            var effectsGo = new GameObject("Effects");
            effectsGo.transform.SetParent(transform, false);
            _effects = effectsGo.AddComponent<EffectPoolView>();
            _effects.Init(_camera);
            var feedbackGo = new GameObject("Feedback");
            feedbackGo.transform.SetParent(transform, false);
            _feedback = feedbackGo.AddComponent<CombatFeedbackView>();
            _feedback.Init(_camera, UI.UiFactory.BuiltinFont());

            for (int p = 0; p < MaxProjectiles; p++)
            {
                GameObject proj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                proj.name = "Projectile" + p;
                Destroy(proj.GetComponent<Collider>());
                proj.transform.SetParent(transform, false);
                proj.SetActive(false);
                _projectiles[p] = proj;
            }
            ResetPoses(0, 0);
        }

        public void SetReducedShake(bool reduced) => _shake.Reduced = reduced || _reducedMotion;

        /// <summary>Reduced motion: no shake, no rising captions, effects without motion patterns.</summary>
        public void SetReducedMotion(bool reduced)
        {
            _reducedMotion = reduced;
            if (reduced) _shake.Reduced = true;
            if (_effects != null) _effects.ReducedMotion = reduced;
            if (_feedback != null) _feedback.ReducedMotion = reduced;
        }

        public void SetTextScale(float scale)
        {
            if (_feedback != null) _feedback.TextScale = scale;
        }

        /// <summary>Switches the arena treatment (rules, fighters and camera are unchanged).</summary>
        public void SetVariant(string variantId)
        {
            if (variantId == _variant && (_props != null || GameObject.Find("Ground") != null)) return;
            _variant = ArenaVariants.Get(variantId).Id;
            foreach (ArenaVariant v in ArenaVariants.All)
            {
                GameObject existing = GameObject.Find(ArenaVariantBuilder.PropsRootName + "_" + v.Id);
                if (existing != null) Destroy(existing);
            }
            _props = ArenaVariantBuilder.Build(_variant, null);
            _camera.backgroundColor = ArenaVariantBuilder.Sky(_variant);
        }

        /// <summary>Fighters at their baselines (after Gale Push), standing; archers back to idle.</summary>
        public void ResetPoses(long baselineA, long baselineB)
        {
            _fighters[0].position = ArenaLayout.PosedPosition(PlayerSide.A, baselineA, Dodge.None);
            _fighters[1].position = ArenaLayout.PosedPosition(PlayerSide.B, baselineB, Dodge.None);
            SetFighterColor(PlayerSide.A, false);
            SetFighterColor(PlayerSide.B, false);
        }

        /// <summary>New match: archers idle (clears victory/defeat poses).</summary>
        public void ResetArchers()
        {
            foreach (ArcherPresenter a in _archers) a.Trigger(ArcherTrigger.Reset);
        }

        /// <summary>Terminal poses for the result (a draw leaves both standing).</summary>
        public void ShowMatchEnd(PlayerSide? winner)
        {
            if (winner == null) return;
            _archers[(int)winner.Value].Trigger(ArcherTrigger.Victory);
            _archers[(int)Rules.Land.Board.Opponent(winner.Value)].Trigger(ArcherTrigger.Defeat);
        }

        // ------------------------------------------------------------------ aim preview (ticket 29)

        public void ShowPreview(IReadOnlyList<IReadOnlyList<PreviewPoint>> arcs, Color color) => ShowPreview(arcs, color, false);

        /// <summary>
        /// Draws the player's own provisional arc (the rules' launch math, first 0.375 s only) with a
        /// dot every 0.125 s so speed reads as spacing; at a legal limit the line thins and the end
        /// dot grows, a non-colour "you are at the limit" cue.
        /// </summary>
        public void ShowPreview(IReadOnlyList<IReadOnlyList<PreviewPoint>> arcs, Color color, bool atLimit)
        {
            ClearPreview();
            int dot = 0;
            for (int i = 0; i < arcs.Count; i++)
            {
                LineRenderer lr = PreviewLine(i);
                IReadOnlyList<PreviewPoint> arc = arcs[i];
                var pts = new Vector3[arc.Count];
                for (int k = 0; k < arc.Count; k++) pts[k] = ArenaLayout.ToUnity(arc[k]);
                lr.positionCount = pts.Length;
                lr.SetPositions(pts);
                lr.startColor = color;
                lr.endColor = new Color(color.r, color.g, color.b, 0.1f);
                lr.startWidth = atLimit ? 0.03f : 0.06f;
                lr.gameObject.SetActive(true);
                for (int k = 15; k < pts.Length; k += 15) // 15 ticks = 0.125 s
                {
                    GameObject d = PreviewDot(dot++);
                    d.transform.position = pts[k];
                    float s = atLimit && k + 15 >= pts.Length ? 0.16f : 0.08f;
                    d.transform.localScale = new Vector3(s, s, s);
                    d.SetActive(true);
                }
            }
        }

        public void ClearPreview()
        {
            foreach (LineRenderer lr in _previewLines)
            {
                lr.positionCount = 0;
                lr.gameObject.SetActive(false);
            }
            foreach (GameObject d in _previewDots) d.SetActive(false);
        }

        private LineRenderer PreviewLine(int index)
        {
            while (_previewLines.Count <= index)
            {
                var go = new GameObject("AimPreview" + _previewLines.Count);
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.startWidth = 0.06f;
                lr.endWidth = 0.02f;
                if (_lineMaterial != null) lr.material = _lineMaterial;
                _previewLines.Add(lr);
            }
            return _previewLines[index];
        }

        private GameObject PreviewDot(int index)
        {
            while (_previewDots.Count <= index)
            {
                GameObject d = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                d.name = "AimDot" + _previewDots.Count;
                Destroy(d.GetComponent<Collider>());
                d.transform.SetParent(transform, false);
                _previewDots.Add(d);
            }
            return _previewDots[index];
        }

        // ------------------------------------------------------------------ resolution playback (tickets 31-34)

        public void Play(ResolvedVolley volley, float totalBudgetSeconds)
        {
            ClearPreview();
            HideProjectiles();
            _effects.ReleaseAll();
            _feedback.HideAll();
            _playing = volley;
            _plan = new VolleyPresentationPlan(volley.Result, totalBudgetSeconds);
            _cueShown = new bool[_plan.Cues.Count];
            _elapsed = 0f;
            _flightStart = -1f;
            _flightDone = false;
            for (int i = 0; i < _ended.Length; i++) _ended[i] = false;

            VolleyExplanation e = volley.Result.Explanation;
            // A dodge sets the defender's collision pose for the whole resolution window (visible from the start).
            _fighters[0].position = ArenaLayout.PosedPosition(PlayerSide.A, BaselineBefore(volley, PlayerSide.A), e.A.EffectiveDodge);
            _fighters[1].position = ArenaLayout.PosedPosition(PlayerSide.B, BaselineBefore(volley, PlayerSide.B), e.B.EffectiveDodge);
            foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
            {
                ArcherPresenter archer = _archers[(int)side];
                archer.Speed = Speed;
                if (!e[side].IsPass) archer.BeginShot((float)_plan.LeadInSeconds / Mathf.Max(0.01f, Speed));
            }
            Ctx?.Audio?.Play(Audio.AudioCue.Draw);
        }

        private void OnReleaseArrow(ArcherPresenter archer)
        {
            if (_playing == null || _flightStart >= 0f) return;
            StartFlight();
        }

        private void StartFlight()
        {
            _flightStart = _elapsed;
            Ctx?.Audio?.Play(Audio.AudioCue.Release);
            IReadOnlyList<ProjectileTrack> tracks = _playing.Result.Simulation?.Tracks;
            if (tracks == null) return;
            for (int i = 0; i < tracks.Count && i < MaxProjectiles; i++)
            {
                GameObject p = _projectiles[i];
                float d = Mathf.Max(0.12f, (float)tracks[i].Spec.Radius.ToDouble() * 2f);
                p.transform.localScale = new Vector3(d, d, d);
                var r = p.GetComponent<Renderer>();
                if (r != null) r.material.color = UI.ElementIcons.Tint(tracks[i].Spec.Element);
                p.transform.position = ArenaLayout.ToUnity(TrajectorySampler.Sample(tracks[i], 0));
                p.SetActive(true);
                _effects.Spawn(EffectKind.ProjectileTrail, tracks[i].Spec.Element, p.transform.position, p.transform);
            }
        }

        private static long BaselineBefore(ResolvedVolley v, PlayerSide side)
        {
            // The launch snapshot uses the baseline the shooter had before this volley's Push.
            IReadOnlyList<ProjectileTrack> tracks = v.Result.Simulation?.Tracks;
            if (tracks != null)
            {
                foreach (ProjectileTrack t in tracks)
                {
                    if (t.Spec.Owner != side) continue;
                    double worldZ = t.Spec.Position.Z.ToDouble();
                    double right = side == PlayerSide.A ? worldZ : -worldZ;
                    return (long)System.Math.Round(right * Fixed.OneRaw);
                }
            }
            return v.Result.NewState[side].BaselineOffsetRight.Raw;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            V3 offset = _shake.Update(dt);
            if (_camera != null) _camera.transform.position = _cameraHome + Vec.ToVector3(offset);
            if (_playing == null) return;
            _elapsed += dt * Speed;
            if (_flightStart < 0f && _elapsed >= _plan.LeadInSeconds + FlightStartFallbackSeconds) StartFlight(); // nobody released (both passed)
            if (_flightStart < 0f) return;

            double flightTime = _elapsed - _flightStart;
            long t = _plan.Flight.SimTimeAt(flightTime);
            IReadOnlyList<ProjectileTrack> tracks = _playing.Result.Simulation?.Tracks;
            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count && i < MaxProjectiles; i++)
                {
                    ProjectileTrack track = tracks[i];
                    bool alive = TrajectorySampler.IsAlive(track, t) && t < track.EndTimeSubTicks;
                    if (alive) _projectiles[i].transform.position = ArenaLayout.ToUnity(TrajectorySampler.Sample(track, t));
                    if (!alive && !_ended[i] && t >= track.EndTimeSubTicks)
                    {
                        _ended[i] = true;
                        _effects.StopFollowing(_projectiles[i].transform);
                        _projectiles[i].SetActive(false);
                    }
                }
            }

            for (int c = 0; c < _plan.Cues.Count; c++)
            {
                if (_cueShown[c]) continue;
                VolleyCue cue = _plan.Cues[c];
                double at = cue.Kind == CueKind.Dodge || cue.Kind == CueKind.ShieldRaised || cue.Kind == CueKind.WallRaised
                    ? 0 : _plan.ShowAt(cue) - _plan.LeadInSeconds;
                if (flightTime < at) continue;
                _cueShown[c] = true;
                ShowCue(cue);
            }

            if (!_flightDone && _plan.Flight.FlightFinished(flightTime))
            {
                _flightDone = true;
                HideProjectiles();
                FlightCompleted?.Invoke();
            }
        }

        private void ShowCue(VolleyCue cue)
        {
            Vector3 at = cue.HasProjectile ? Vec.ToVector3(new V3(cue.Position.X, cue.Position.Y, -cue.Position.Z)) : Fighter(cue.Side).position + Vector3.up * 1.1f;
            string caption = Caption(cue);
            switch (cue.Kind)
            {
                case CueKind.Dodge:
                    _effects.Spawn(EffectKind.DodgeStreak, Element.Neutral, Fighter(cue.Side).position, null, 0.8f);
                    _archers[(int)cue.Side].Trigger(cue.Dodge == Dodge.Left ? ArcherTrigger.DodgeLeft : cue.Dodge == Dodge.Right ? ArcherTrigger.DodgeRight : ArcherTrigger.Jump);
                    _feedback.Show(Fighter(cue.Side).position + Vector3.up * -0.6f, caption, UI.UiTheme.Text);
                    Ctx?.Audio?.Play(Audio.AudioCue.Dodge);
                    break;
                case CueKind.ShieldRaised:
                    _effects.Spawn(EffectKind.ShieldBlock, cue.Element, Fighter(cue.Side).position, null, 1.2f);
                    _feedback.Show(at, caption, UI.UiTheme.Text);
                    break;
                case CueKind.WallRaised:
                case CueKind.CoverRemoved:
                    _effects.Spawn(EffectKind.CoverDust, cue.Element, Fighter(cue.Side).position, null, 1.0f);
                    _feedback.Show(at, caption, UI.UiTheme.Text);
                    break;
                case CueKind.ClashCancelled:
                case CueKind.ClashSurvived:
                    _effects.Spawn(EffectKind.Clash, cue.Element, at);
                    _feedback.Show(at, caption, UI.UiTheme.Warning);
                    if (cue.Kind == CueKind.ClashCancelled) Ctx?.Audio?.Play(Audio.AudioCue.Clash);
                    break;
                case CueKind.Impact:
                    _effects.Spawn(EffectKind.Impact, cue.Element, at);
                    _feedback.Show(at, caption, UI.UiTheme.Warning, large: true);
                    SetFighterColor(cue.Side, true);
                    _archers[(int)cue.Side].Trigger(ArcherTrigger.Hit);
                    _shake.Kick();
                    Ctx?.Audio?.Play(Audio.AudioCue.Hit);
                    Haptics.Pulse();
                    HitShown?.Invoke(cue.Side);
                    break;
                case CueKind.Blocked:
                    _effects.Spawn(EffectKind.ShieldBlock, cue.Element, at);
                    _feedback.Show(at, caption, UI.UiTheme.Text, large: true);
                    Ctx?.Audio?.Play(Audio.AudioCue.ShieldBlock);
                    break;
                case CueKind.Miss:
                    _effects.Spawn(EffectKind.Miss, cue.Element, at);
                    _feedback.Show(at, caption, UI.UiTheme.TextMuted);
                    Ctx?.Audio?.Play(Audio.AudioCue.Miss);
                    break;
            }
        }

        /// <summary>Glyph + localized caption, e.g. "✸ 30.00 HP" or "✕ Cancelled" (parameters, never concatenated words).</summary>
        private string Caption(VolleyCue cue)
        {
            if (Ctx == null || Ctx.Loc == null) return cue.Glyph;
            switch (cue.Kind)
            {
                case CueKind.Impact:
                    return Ctx.TF(cue.Covered ? cue.CaptionKey + "Covered" : cue.CaptionKey, cue.Glyph, Hp.Format(cue.Amount));
                case CueKind.Dodge:
                    return Ctx.TF(cue.CaptionKey, cue.Glyph, Ctx.T("dodge." + cue.Dodge.ToString().ToLowerInvariant()));
                default:
                    return Ctx.TF(cue.CaptionKey, cue.Glyph);
            }
        }

        /// <summary>Ends playback and restores standing poses at the new baselines.</summary>
        public void Stop()
        {
            if (_playing != null)
            {
                DuelState s = _playing.Result.NewState;
                if (s != null) ResetPoses(s.A.BaselineOffsetRight.Raw, s.B.BaselineOffsetRight.Raw);
            }
            _playing = null;
            HideProjectiles();
            if (_effects != null) _effects.ReleaseAll();
            _shake.Stop();
            if (_camera != null) _camera.transform.position = _cameraHome;
        }

        private void HideProjectiles()
        {
            for (int i = 0; i < _projectiles.Length; i++)
            {
                if (_projectiles[i] == null) continue;
                if (_effects != null) _effects.StopFollowing(_projectiles[i].transform);
                _projectiles[i].SetActive(false);
            }
        }

        private void SetFighterColor(PlayerSide side, bool hit)
        {
            Color baseColor = side == PlayerSide.A ? UI.UiTheme.PlayerA : UI.UiTheme.PlayerB;
            Color c = hit ? new Color(0.9f, 0.15f, 0.15f) : baseColor;
            if (_archers != null && _archers[(int)side] != null) _archers[(int)side].Rig.SetTint(c);
            Renderer r = _fighterRenderers[(int)side];
            if (r != null) r.material.color = c;
        }
    }
}
