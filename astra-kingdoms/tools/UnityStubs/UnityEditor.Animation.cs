// Compile-only stubs of the UnityEditor animation and statistics APIs used by the archer
// animator builder and the asset-budget validator (tickets 26-28). Never executed.
using UnityEngine;

namespace UnityEditor
{
    public sealed class AnimationClipSettings
    {
        public bool loopTime { get; set; }
        public bool loopBlend { get; set; }
        public bool keepOriginalPositionY { get; set; }
    }

    public sealed class AnimationUtility
    {
        public static AnimationClipSettings GetAnimationClipSettings(AnimationClip clip) => throw null;
        public static void SetAnimationClipSettings(AnimationClip clip, AnimationClipSettings srcClipInfo) { }
        public static void SetAnimationEvents(AnimationClip clip, AnimationEvent[] events) { }
    }

    public sealed class Selection
    {
        public static GameObject activeGameObject { get; set; }
    }

    public sealed class UnityStats
    {
        public static int drawCalls => throw null;
        public static int batches => throw null;
        public static int triangles => throw null;
    }

    public class EditorUtility
    {
        public static void SetDirty(UnityEngine.Object target) { }
    }
}

namespace UnityEditor.Animations
{
    public enum AnimatorConditionMode
    {
        If = 1,
        IfNot = 2,
        Greater = 3,
        Less = 4,
        Equals = 6,
        NotEqual = 7,
    }

    public class AnimatorTransitionBase : UnityEngine.Object
    {
        public bool canTransitionToSelf { get; set; }
        public void AddCondition(AnimatorConditionMode mode, float threshold, string parameter) { }
    }

    public sealed class AnimatorStateTransition : AnimatorTransitionBase
    {
        public bool hasExitTime { get; set; }
        public float exitTime { get; set; }
        public float duration { get; set; }
        public bool hasFixedDuration { get; set; }
    }

    public sealed class AnimatorState : UnityEngine.Object
    {
        public Motion motion { get; set; }
        public float speed { get; set; }
        public AnimatorStateTransition AddTransition(AnimatorState destinationState) => throw null;
    }

    public sealed class AnimatorStateMachine : UnityEngine.Object
    {
        public AnimatorState defaultState { get; set; }
        public AnimatorState AddState(string name) => throw null;
        public AnimatorStateTransition AddAnyStateTransition(AnimatorState destinationState) => throw null;
    }

    public sealed class AnimatorControllerLayer
    {
        public string name { get; set; }
        public AnimatorStateMachine stateMachine { get; set; }
    }

    public sealed class AnimatorController : RuntimeAnimatorController
    {
        public AnimatorControllerLayer[] layers { get; set; }
        public static AnimatorController CreateAnimatorControllerAtPath(string path) => throw null;
        public void AddParameter(string name, AnimatorControllerParameterType type) { }
    }
}
