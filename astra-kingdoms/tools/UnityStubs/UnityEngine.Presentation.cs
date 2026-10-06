// Compile-only stubs of the Unity APIs used by the V1 duel/land presentation (tickets 26-48):
// animation, meshes and skinning, transforms, cameras, lights, network reachability.
// Signatures follow the Unity 6 scripting reference as known; bodies are placeholders and never run.
using System;

namespace UnityEngine
{
    public class Motion : Object
    {
    }

    public enum WrapMode
    {
        Once = 1,
        Loop = 2,
        PingPong = 4,
        Default = 0,
        ClampForever = 8,
    }

    public sealed class AnimationClip : Motion
    {
        public AnimationClip() { }
        public float frameRate { get; set; }
        public float length => throw null;
        public bool legacy { get; set; }
        public WrapMode wrapMode { get; set; }
        public AnimationEvent[] events { get; set; }
        public void SetCurve(string relativePath, Type type, string propertyName, AnimationCurve curve) { }
    }

    public sealed class AnimationEvent
    {
        public AnimationEvent() { }
        public string functionName { get; set; }
        public float time { get; set; }
    }

    public struct Keyframe
    {
        public Keyframe(float time, float value) { this.time = time; this.value = value; }
        public float time { get; set; }
        public float value { get; set; }
    }

    public class AnimationCurve
    {
        public AnimationCurve() { }
        public AnimationCurve(params Keyframe[] keys) { }
        public Keyframe[] keys { get; set; }
        public int AddKey(float time, float value) => throw null;
    }

    public class RuntimeAnimatorController : Object
    {
        public AnimationClip[] animationClips => throw null;
    }

    public enum AnimatorControllerParameterType
    {
        Float = 1,
        Int = 3,
        Bool = 4,
        Trigger = 9,
    }

    public sealed class Animator : Behaviour
    {
        public RuntimeAnimatorController runtimeAnimatorController { get; set; }
        public float speed { get; set; }
        public bool applyRootMotion { get; set; }
        public void SetTrigger(string name) { }
        public void ResetTrigger(string name) { }
        public void SetFloat(string name, float value) { }
        public void SetBool(string name, bool value) { }
        public void Play(string stateName) { }
        public void Rebind() { }
    }

    public struct BoneWeight
    {
        public float weight0 { get; set; }
        public float weight1 { get; set; }
        public float weight2 { get; set; }
        public float weight3 { get; set; }
        public int boneIndex0 { get; set; }
        public int boneIndex1 { get; set; }
        public int boneIndex2 { get; set; }
        public int boneIndex3 { get; set; }
    }

    public sealed class Mesh : Object
    {
        public Mesh() { }
        public int vertexCount => throw null;
        public int subMeshCount => throw null;
        public int[] triangles { get; set; }
        public BoneWeight[] boneWeights { get; set; }
        public int[] GetTriangles(int submesh) => throw null;
    }

    public sealed class MeshFilter : Component
    {
        public Mesh sharedMesh { get; set; }
        public Mesh mesh { get; set; }
    }

    public sealed class SkinnedMeshRenderer : Renderer
    {
        public Mesh sharedMesh { get; set; }
        public Transform[] bones { get; set; }
        public Transform rootBone { get; set; }
    }

    public enum LightShadows
    {
        None = 0,
        Hard = 1,
        Soft = 2,
    }

    public enum NetworkReachability
    {
        NotReachable = 0,
        ReachableViaCarrierDataNetwork = 1,
        ReachableViaLocalAreaNetwork = 2,
    }
}

namespace UnityEngine.SceneManagement
{
    public static class SceneManager
    {
        public static Scene GetActiveScene() => throw null;
    }
}
