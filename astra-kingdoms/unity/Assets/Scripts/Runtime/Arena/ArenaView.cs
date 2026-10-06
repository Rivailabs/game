using System.Collections.Generic;
using AstraKingdoms.Client.Combat;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using UnityEngine;

namespace AstraKingdoms.Client.Arena
{
    /// <summary>
    /// Grey-box duel presentation. Fighters, the ground and the camera come from the script-built
    /// scene (or are created here when missing). Resolution playback interpolates the engine's
    /// projectile tracks (<see cref="TrajectorySampler"/>) on a <see cref="PlaybackTimeline"/>
    /// compressed to the 2.5 s window; hit flashes come from the tracks' authoritative terminations.
    /// No physics, colliders or animation callbacks decide anything.
    /// </summary>
    public sealed class ArenaView : MonoBehaviour
    {
        private readonly List<GameObject> _projectiles = new List<GameObject>();
        private readonly List<LineRenderer> _previewLines = new List<LineRenderer>();
        private readonly bool[] _flashed = new bool[16];

        private Transform[] _fighters;
        private Renderer[] _fighterRenderers;
        private Camera _camera;
        private Vector3 _cameraHome;
        private Material _lineMaterial;

        private ResolvedVolley _playing;
        private PlaybackTimeline _timeline;
        private float _elapsed;
        private float _shake;
        private bool _reducedShake;

        public bool IsPlaying => _playing != null;
        public bool FlightFinished => _playing == null || _timeline.FlightFinished(_elapsed);
        public float Speed { get; set; } = 1f;
        public Transform Fighter(PlayerSide side) => _fighters[(int)side];

        public event System.Action FlightCompleted;
        public event System.Action<PlayerSide> HitShown;

        public void Init(bool reducedShake)
        {
            _reducedShake = reducedShake;
            _fighters = new Transform[2];
            _fighterRenderers = new Renderer[2];
            for (int i = 0; i < 2; i++)
            {
                var side = (PlayerSide)i;
                GameObject go = GameObject.Find(i == 0 ? ArenaLayout.FighterAName : ArenaLayout.FighterBName);
                if (go == null) go = ArenaBuilder.CreateFighter(side, null);
                _fighters[i] = go.transform;
                _fighterRenderers[i] = go.GetComponent<Renderer>();
            }
            if (GameObject.Find("Ground") == null) ArenaBuilder.CreateGround(null);
            _camera = Camera.main;
            if (_camera == null) _camera = ArenaBuilder.CreateCamera(null).GetComponent<Camera>();
            _cameraHome = _camera.transform.position;
            // Sprites/Default is in Unity's default always-included shaders; fall back to the fighter's
            // pipeline material if a build stripped it.
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null) _lineMaterial = new Material(shader);
            else if (_fighterRenderers[0] != null && _fighterRenderers[0].sharedMaterial != null) _lineMaterial = new Material(_fighterRenderers[0].sharedMaterial);
            ResetPoses(0, 0);
        }

        public void SetReducedShake(bool reduced) => _reducedShake = reduced;

        /// <summary>Fighters at their baselines (after Gale Push), standing.</summary>
        public void ResetPoses(long baselineA, long baselineB)
        {
            _fighters[0].position = ArenaLayout.PosedPosition(PlayerSide.A, baselineA, Dodge.None);
            _fighters[1].position = ArenaLayout.PosedPosition(PlayerSide.B, baselineB, Dodge.None);
            SetFighterColor(PlayerSide.A, false);
            SetFighterColor(PlayerSide.B, false);
        }

        // ------------------------------------------------------------------ aim preview

        public void ShowPreview(IReadOnlyList<IReadOnlyList<PreviewPoint>> arcs, Color color)
        {
            ClearPreview();
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
                lr.gameObject.SetActive(true);
            }
        }

        public void ClearPreview()
        {
            foreach (LineRenderer lr in _previewLines)
            {
                lr.positionCount = 0;
                lr.gameObject.SetActive(false);
            }
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

        // ------------------------------------------------------------------ resolution playback

        public void Play(ResolvedVolley volley, float totalBudgetSeconds)
        {
            ClearPreview();
            ClearProjectiles();
            _playing = volley;
            _timeline = PlaybackTimeline.For(volley.Result, totalBudgetSeconds);
            _elapsed = 0f;
            for (int i = 0; i < _flashed.Length; i++) _flashed[i] = false;

            VolleyExplanation e = volley.Result.Explanation;
            // A dodge sets the defender's collision pose for the whole resolution window.
            _fighters[0].position = ArenaLayout.PosedPosition(PlayerSide.A, BaselineBefore(volley, PlayerSide.A), e.A.EffectiveDodge);
            _fighters[1].position = ArenaLayout.PosedPosition(PlayerSide.B, BaselineBefore(volley, PlayerSide.B), e.B.EffectiveDodge);
            if (volley.Result.Simulation != null)
            {
                foreach (ProjectileTrack track in volley.Result.Simulation.Tracks)
                {
                    GameObject p = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    p.name = "Projectile " + track.Spec.Id;
                    Object.Destroy(p.GetComponent<Collider>());
                    p.transform.SetParent(transform, false);
                    float d = Mathf.Max(0.12f, (float)track.Spec.Radius.ToDouble() * 2f);
                    p.transform.localScale = new Vector3(d, d, d);
                    var r = p.GetComponent<Renderer>();
                    if (r != null) r.material.color = UI.ElementIcons.Tint(track.Spec.Element);
                    p.SetActive(false);
                    _projectiles.Add(p);
                }
            }
            Ctx?.Audio?.Play(Sfx.Release);
        }

        /// <summary>Services for sound/haptics (optional; tests run without them).</summary>
        public ClientContext Ctx { get; set; }

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
            if (_shake > 0f)
            {
                _shake = Mathf.Max(0f, _shake - dt * 2.5f);
                Vector3 offset = _reducedShake ? Vector3.zero : new Vector3(Mathf.Sin(Time.frameCount * 1.7f), Mathf.Cos(Time.frameCount * 2.3f), 0f) * (_shake * 0.08f);
                _camera.transform.position = _cameraHome + offset;
            }
            if (_playing == null) return;
            bool wasFinished = _timeline.FlightFinished(_elapsed);
            _elapsed += dt * Speed;
            long t = _timeline.SimTimeAt(_elapsed);
            IReadOnlyList<ProjectileTrack> tracks = _playing.Result.Simulation?.Tracks;
            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count && i < _projectiles.Count; i++)
                {
                    ProjectileTrack track = tracks[i];
                    bool alive = TrajectorySampler.IsAlive(track, t) && t < track.EndTimeSubTicks;
                    _projectiles[i].SetActive(alive);
                    if (alive) _projectiles[i].transform.position = ArenaLayout.ToUnity(TrajectorySampler.Sample(track, t));
                    if (!alive && t >= track.EndTimeSubTicks && i < _flashed.Length && !_flashed[i])
                    {
                        _flashed[i] = true;
                        OnTrackEnded(track);
                    }
                }
            }
            if (!wasFinished && _timeline.FlightFinished(_elapsed))
            {
                ClearProjectiles();
                FlightCompleted?.Invoke();
            }
        }

        private void OnTrackEnded(ProjectileTrack track)
        {
            PlayerSide target = CombatGeometry.Opponent(track.Spec.Owner);
            switch (track.Termination)
            {
                case TerminationReason.BodyContact:
                case TerminationReason.BurstContact:
                    SetFighterColor(target, true);
                    _shake = 1f;
                    Ctx?.Audio?.Play(Sfx.Hit);
                    Haptics.Pulse();
                    HitShown?.Invoke(target);
                    break;
                case TerminationReason.ClashDestroyed:
                    Ctx?.Audio?.Play(Sfx.Clash);
                    break;
                default:
                    Ctx?.Audio?.Play(Sfx.Miss);
                    break;
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
            ClearProjectiles();
            _camera.transform.position = _cameraHome;
        }

        private void ClearProjectiles()
        {
            foreach (GameObject p in _projectiles) Object.Destroy(p);
            _projectiles.Clear();
        }

        private void SetFighterColor(PlayerSide side, bool hit)
        {
            Renderer r = _fighterRenderers[(int)side];
            if (r == null) return;
            Color baseColor = side == PlayerSide.A ? UI.UiTheme.PlayerA : UI.UiTheme.PlayerB;
            r.material.color = hit ? new Color(0.9f, 0.15f, 0.15f) : baseColor;
        }
    }
}
