using System;
using AstraKingdoms.Client.Presentation;
using UnityEngine;

namespace AstraKingdoms.Client.Arena
{
    /// <summary>Unity/presentation vector conversions (same frame; presentation only).</summary>
    public static class Vec
    {
        public static V3 ToV3(Vector3 v) => new V3(v.x, v.y, v.z);
        public static Vector3 ToVector3(V3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
    }

    /// <summary>
    /// Drives one archer (tickets 27-28). The engine-independent <see cref="ArcherAnimator"/> is the
    /// state machine; when the rig has an <see cref="Animator"/> with the controller built by
    /// <c>ArcherAnimatorBuilder</c>, triggers are forwarded to it and the clip's
    /// <c>OnReleaseArrow</c> event marks the release. Without one (the placeholder) the poses are
    /// applied procedurally and the model's release marker is used. Either way
    /// <see cref="ReleaseArrow"/> is raised once per shot, which the arena uses to spawn the
    /// projectiles. The bowstring is solved every frame from the grip frame and the draw amount.
    /// </summary>
    public sealed class ArcherPresenter : MonoBehaviour
    {
        /// <summary>Draw amount 0..1; production clips animate this field, placeholders copy it from the pose.</summary>
        public float DrawAmount;

        private readonly ArcherAnimator _model = new ArcherAnimator();
        private ArcherRig _rig;
        private Animator _animator;
        private LineRenderer _string;
        private GameObject _arrow;
        private Transform _follow;
        private BowGeometry _bow = new BowGeometry();
        private double _shotSpeed = 1.0;
        private bool _releasedThisShot = true;

        public ArcherRig Rig => _rig;
        public ArcherClip State => _model.State;
        /// <summary>Playback speed (automation runs faster; 0 pauses).</summary>
        public float Speed { get; set; } = 1f;
        public bool UsesAnimatorEvents => _animator != null && _animator.runtimeAnimatorController != null;

        /// <summary>Raised once per shot at the release marker.</summary>
        public event Action<ArcherPresenter> ReleaseArrow;

        public void Init(ArcherRig rig, Transform follow, Material lineMaterial)
        {
            _rig = rig;
            _follow = follow;
            _animator = rig.GetComponent<Animator>();
            if (_animator != null) _animator.applyRootMotion = false; // root motion comes from the rules' pose
            var go = new GameObject("Bowstring");
            go.transform.SetParent(transform, false);
            _string = go.AddComponent<LineRenderer>();
            _string.useWorldSpace = true;
            _string.positionCount = 3;
            _string.startWidth = 0.012f;
            _string.endWidth = 0.012f;
            _string.startColor = new Color(0.92f, 0.9f, 0.82f);
            _string.endColor = new Color(0.92f, 0.9f, 0.82f);
            if (lineMaterial != null) _string.material = lineMaterial;
            _arrow = GameObject.CreatePrimitive(PrimitiveType.Cube); // 12 triangles: inside the 200-triangle arrow budget
            _arrow.name = "NockedArrow";
            Destroy(_arrow.GetComponent<Collider>());
            _arrow.transform.SetParent(transform, false);
            _arrow.transform.localScale = new Vector3(0.02f, 0.02f, (float)_bow.ArrowLength);
            _arrow.SetActive(false);
        }

        /// <summary>Requests a state change (forwarded to the Animator when present).</summary>
        public bool Trigger(ArcherTrigger trigger)
        {
            if (!_model.Fire(trigger)) return false;
            if (_animator != null && _animator.runtimeAnimatorController != null) _animator.SetTrigger(trigger.ToString());
            return true;
        }

        /// <summary>
        /// Draws and releases so that the release marker lands <paramref name="releaseInSeconds"/>
        /// from now: the clips are time-scaled for this shot only, keeping their shape.
        /// </summary>
        public void BeginShot(float releaseInSeconds)
        {
            if (_model.IsTerminal) return;
            if (_model.State != ArcherClip.Idle && _model.State != ArcherClip.Recover) Trigger(ArcherTrigger.Reset);
            Trigger(ArcherTrigger.Draw);
            Trigger(ArcherTrigger.Release);
            _releasedThisShot = false;
            double natural = ArcherPoseLibrary.Get(ArcherClip.Draw).Seconds +
                             ArcherPoseLibrary.ReleaseMarker * ArcherPoseLibrary.Get(ArcherClip.Release).Seconds;
            _shotSpeed = releaseInSeconds > 0.01f ? natural / releaseInSeconds : 50.0;
            if (_animator != null) _animator.speed = (float)_shotSpeed * Speed;
        }

        /// <summary>Animation event from the production Release clip.</summary>
        public void OnReleaseArrow()
        {
            if (UsesAnimatorEvents) Release();
        }

        private void Release()
        {
            if (_releasedThisShot) return;
            _releasedThisShot = true;
            _shotSpeed = 1.0;
            if (_animator != null) _animator.speed = Speed;
            ReleaseArrow?.Invoke(this);
        }

        private void Update()
        {
            double dt = Time.unscaledDeltaTime * Speed * _shotSpeed;
            ArcherStep step = _model.Advance(dt);
            if (step.ReleaseMarker && !UsesAnimatorEvents) Release();
            if (step.ReleaseMarker && UsesAnimatorEvents && !_releasedThisShot)
            {
                // Safety net: the clip's event should have fired by now; never stall the volley.
                Debug.LogWarning("[Archer] Release clip event missing; using the model's marker.");
                Release();
            }
            if (_model.State != ArcherClip.Draw && _model.State != ArcherClip.Release && _model.State != ArcherClip.Hold) _shotSpeed = 1.0;
        }

        private void LateUpdate()
        {
            if (_rig == null) return;
            if (_follow != null)
            {
                // Feet under the body capsule (its centre is 0.9 m up; a jump raises the whole capsule).
                Vector3 p = _follow.position;
                _rig.transform.position = new Vector3(p.x, p.y - ArenaLayout.CapsuleCentreY, p.z);
            }
            ArcherPose pose = _model.CurrentPose();
            if (_rig.Placeholder) ApplyPlaceholderPose(pose);
            if (!UsesAnimatorEvents) DrawAmount = (float)pose.Draw;
            UpdateString();
        }

        private void ApplyPlaceholderPose(ArcherPose pose)
        {
            if (_rig.Hips != null) _rig.Hips.localPosition = new Vector3(0f, 0.9f + (float)pose.RootDrop, 0f);
            if (_rig.Spine != null) _rig.Spine.localRotation = Quaternion.Euler((float)pose.SpinePitch, 0f, (float)-pose.Lean);
            // Arms point forward at 90 degrees of raise; 0 hangs down.
            if (_rig.BowArm != null) _rig.BowArm.localRotation = Quaternion.Euler(90f - (float)pose.BowArmRaise, 0f, 0f);
            if (_rig.DrawArm != null) _rig.DrawArm.localRotation = Quaternion.Euler(90f - (float)pose.BowArmRaise, 0f, 0f);
            if (_rig.LegLeft != null) _rig.LegLeft.localRotation = Quaternion.Euler(-(float)pose.KneeBend * 0.5f, 0f, 0f);
            if (_rig.LegRight != null) _rig.LegRight.localRotation = Quaternion.Euler((float)pose.KneeBend * 0.3f, 0f, 0f);
        }

        private void UpdateString()
        {
            if (_rig.BowGrip == null || _string == null) return;
            Transform grip = _rig.BowGrip;
            BowstringPoints s = BowstringSolver.Solve(Vec.ToV3(grip.position), Vec.ToV3(grip.forward), Vec.ToV3(grip.up), DrawAmount, _bow);
            Vector3 nock = Vec.ToVector3(s.Nock);
            _string.SetPosition(0, Vec.ToVector3(s.TopTip));
            _string.SetPosition(1, nock);
            _string.SetPosition(2, Vec.ToVector3(s.BottomTip));
            if (_rig.StringNock != null) _rig.StringNock.position = nock;
            if (_rig.Placeholder && _rig.HandRight != null) _rig.HandRight.position = nock; // the hand holds the string
            bool nocked = DrawAmount > 0.05f && !_releasedThisShot;
            _arrow.SetActive(nocked);
            if (nocked)
            {
                _arrow.transform.position = Vec.ToVector3((s.ArrowTail + s.ArrowTip) * 0.5);
                _arrow.transform.rotation = Quaternion.LookRotation(Vec.ToVector3(s.AimDirection), grip.up);
            }
        }
    }
}
