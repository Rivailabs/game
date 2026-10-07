using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Arena;
using AstraKingdoms.Client.Presentation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace AstraKingdoms.EditorTools
{
    /// <summary>
    /// Ticket 27: builds the archer Animator controller from the engine-independent contract in
    /// <see cref="ArcherPoseLibrary"/> — the required clip set (idle, draw, hold, release, recover,
    /// hit, left/right dodge, jump, defeat, victory), loop flags, the <c>OnReleaseArrow</c> event
    /// marker on Release, and the same transitions and triggers as <see cref="ArcherAnimator"/>.
    /// The clips are placeholder poses keyed on the placeholder rig's joint names (Spine, BowArm,
    /// DrawArm, LegL, LegR, Hips) plus <see cref="ArcherPresenter.DrawAmount"/>, so the controller
    /// works before art exists; production clips replace the clip assets and keep names and markers.
    /// <para>Menu: Astra Kingdoms/Build Archer Animator.
    /// Batch: <c>-executeMethod AstraKingdoms.EditorTools.ArcherAnimatorBuilder.BuildFromCommandLine</c></para>
    /// </summary>
    public static class ArcherAnimatorBuilder
    {
        public const string Folder = "Assets/Generated/Archer";
        public const string ControllerPath = Folder + "/ArcherPlaceholder.controller";
        public const float FrameRate = 30f;

        [MenuItem("Astra Kingdoms/Build Archer Animator")]
        public static void BuildFromMenu() => Debug.Log("[Archer] Built " + Build());

        public static void BuildFromCommandLine()
        {
            try
            {
                Build();
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(1);
            }
        }

        public static string Build()
        {
            GreyBoxSceneBuilder.EnsureFolder(Folder);
            AssetDatabase.DeleteAsset(ControllerPath);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            foreach (ArcherTrigger t in (ArcherTrigger[])Enum.GetValues(typeof(ArcherTrigger)))
                controller.AddParameter(t.ToString(), AnimatorControllerParameterType.Trigger);

            AnimatorStateMachine sm = controller.layers[0].stateMachine;
            var states = new Dictionary<ArcherClip, AnimatorState>();
            foreach (ArcherClipSpec spec in ArcherPoseLibrary.All)
            {
                AnimationClip clip = BuildClip(spec);
                AssetDatabase.AddObjectToAsset(clip, controller);
                AnimatorState state = sm.AddState(spec.Name);
                state.motion = clip;
                states[spec.Clip] = state;
            }
            sm.defaultState = states[ArcherClip.Idle];

            // Triggered transitions (mirror ArcherAnimator.Fire).
            Trigger(states[ArcherClip.Idle], states[ArcherClip.Draw], ArcherTrigger.Draw);
            Trigger(states[ArcherClip.Recover], states[ArcherClip.Draw], ArcherTrigger.Draw);
            Trigger(states[ArcherClip.Hold], states[ArcherClip.Release], ArcherTrigger.Release);
            // A release requested during the draw waits for the draw to finish.
            AnimatorStateTransition queued = states[ArcherClip.Draw].AddTransition(states[ArcherClip.Release]);
            queued.hasExitTime = true;
            queued.exitTime = 1f;
            queued.duration = (float)ArcherPoseLibrary.BlendSeconds(ArcherClip.Release);
            queued.hasFixedDuration = true;
            queued.AddCondition(AnimatorConditionMode.If, 0f, ArcherTrigger.Release.ToString());
            foreach (ArcherClip from in new[] { ArcherClip.Idle, ArcherClip.Recover, ArcherClip.Release })
            {
                Trigger(states[from], states[ArcherClip.DodgeLeft], ArcherTrigger.DodgeLeft);
                Trigger(states[from], states[ArcherClip.DodgeRight], ArcherTrigger.DodgeRight);
                Trigger(states[from], states[ArcherClip.Jump], ArcherTrigger.Jump);
            }
            foreach (ArcherClip to in new[] { ArcherClip.Hit, ArcherClip.Defeat, ArcherClip.Victory })
            {
                AnimatorStateTransition any = sm.AddAnyStateTransition(states[to]);
                any.canTransitionToSelf = false;
                any.duration = (float)ArcherPoseLibrary.BlendSeconds(to);
                any.hasFixedDuration = true;
                any.AddCondition(AnimatorConditionMode.If, 0f, (to == ArcherClip.Hit ? ArcherTrigger.Hit : to == ArcherClip.Defeat ? ArcherTrigger.Defeat : ArcherTrigger.Victory).ToString());
            }
            AnimatorStateTransition reset = sm.AddAnyStateTransition(states[ArcherClip.Idle]);
            reset.duration = 0f;
            reset.AddCondition(AnimatorConditionMode.If, 0f, ArcherTrigger.Reset.ToString());

            // Automatic transitions at clip end (terminal clips have none and hold their last pose).
            foreach (ArcherClipSpec spec in ArcherPoseLibrary.All)
            {
                if (spec.Loop || spec.Terminal) continue;
                AnimatorStateTransition exit = states[spec.Clip].AddTransition(states[ArcherPoseLibrary.Next(spec.Clip)]);
                exit.hasExitTime = true;
                exit.exitTime = 1f;
                exit.duration = 0f;
            }
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return ControllerPath;
        }

        private static void Trigger(AnimatorState from, AnimatorState to, ArcherTrigger trigger)
        {
            AnimatorStateTransition t = from.AddTransition(to);
            t.hasExitTime = false;
            t.duration = (float)ArcherPoseLibrary.BlendSeconds((ArcherClip)Enum.Parse(typeof(ArcherClip), to.name));
            t.hasFixedDuration = true;
            t.AddCondition(AnimatorConditionMode.If, 0f, trigger.ToString());
        }

        /// <summary>Keyframes every placeholder joint channel from the clip's pose keys.</summary>
        public static AnimationClip BuildClip(ArcherClipSpec spec)
        {
            var clip = new AnimationClip { name = spec.Name, frameRate = FrameRate };
            float length = (float)spec.Seconds;
            Curve(clip, spec, "Hips", typeof(Transform), "localPosition.y", p => 0.9f + (float)p.RootDrop, length);
            Curve(clip, spec, "Hips/Spine", typeof(Transform), "localEulerAnglesRaw.x", p => (float)p.SpinePitch, length);
            Curve(clip, spec, "Hips/Spine", typeof(Transform), "localEulerAnglesRaw.z", p => (float)-p.Lean, length);
            Curve(clip, spec, "Hips/Spine/BowArm", typeof(Transform), "localEulerAnglesRaw.x", p => 90f - (float)p.BowArmRaise, length);
            Curve(clip, spec, "Hips/Spine/DrawArm", typeof(Transform), "localEulerAnglesRaw.x", p => 90f - (float)p.BowArmRaise, length);
            Curve(clip, spec, "Hips/LegL", typeof(Transform), "localEulerAnglesRaw.x", p => -(float)p.KneeBend * 0.5f, length);
            Curve(clip, spec, "Hips/LegR", typeof(Transform), "localEulerAnglesRaw.x", p => (float)p.KneeBend * 0.3f, length);
            Curve(clip, spec, string.Empty, typeof(ArcherPresenter), nameof(ArcherPresenter.DrawAmount), p => (float)p.Draw, length);

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = spec.Loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            var events = new List<AnimationEvent>();
            foreach (ClipEvent e in spec.Events)
                events.Add(new AnimationEvent { functionName = e.FunctionName, time = (float)(e.NormalizedTime * spec.Seconds) });
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            return clip;
        }

        private static void Curve(AnimationClip clip, ArcherClipSpec spec, string path, Type type, string property, Func<ArcherPose, float> value, float length)
        {
            var keys = new List<Keyframe>();
            foreach (KeyValuePair<double, ArcherPose> k in spec.Keys) keys.Add(new Keyframe((float)k.Key * length, value(k.Value)));
            clip.SetCurve(path, type, property, new AnimationCurve(keys.ToArray()));
        }
    }
}
