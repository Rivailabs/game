// Compile-only stubs of UnityEngine core types. Never executed.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name { get; set; }
        public static void Destroy(Object obj) { }
        public static void Destroy(Object obj, float t) { }
        public static void DestroyImmediate(Object obj) { }
        public static void DontDestroyOnLoad(Object target) { }
        public static T FindFirstObjectByType<T>() where T : Object => throw null;
        public static implicit operator bool(Object exists) => exists is object;
        public static bool operator ==(Object x, Object y) => ReferenceEquals(x, y);
        public static bool operator !=(Object x, Object y) => !ReferenceEquals(x, y);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => 0;
    }

    public enum PrimitiveType
    {
        Sphere = 0,
        Capsule = 1,
        Cylinder = 2,
        Cube = 3,
        Plane = 4,
        Quad = 5,
    }

    public sealed class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string name) { }
        public GameObject(string name, params Type[] components) { }
        public Transform transform => throw null;
        public bool activeSelf => throw null;
        public string tag { get; set; }
        public int layer { get; set; }
        public void SetActive(bool value) { }
        public T AddComponent<T>() where T : Component => throw null;
        public Component AddComponent(Type componentType) => throw null;
        public T GetComponent<T>() => throw null;
        public T GetComponentInChildren<T>() => throw null;
        public T[] GetComponentsInChildren<T>(bool includeInactive) => throw null;
        public static GameObject Find(string name) => throw null;
        public static GameObject CreatePrimitive(PrimitiveType type) => throw null;
    }

    public class Component : Object
    {
        public Transform transform => throw null;
        public GameObject gameObject => throw null;
        public string tag { get; set; }
        public T GetComponent<T>() => throw null;
        public T GetComponentInChildren<T>() => throw null;
        public T[] GetComponentsInChildren<T>(bool includeInactive) => throw null;
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; }
        public bool isActiveAndEnabled => throw null;
    }

    public class MonoBehaviour : Behaviour
    {
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject => throw null;
    }

    public class Transform : Component, System.Collections.IEnumerable
    {
        public Vector3 position { get; set; }
        public Vector3 localPosition { get; set; }
        public Quaternion rotation { get; set; }
        public Quaternion localRotation { get; set; }
        public Vector3 localScale { get; set; }
        public Vector3 eulerAngles { get; set; }
        public Vector3 localEulerAngles { get; set; }
        public Vector3 forward { get; set; }
        public Vector3 up { get; set; }
        public Vector3 right { get; set; }
        public Transform parent { get; set; }
        public int childCount => throw null;
        public Transform GetChild(int index) => throw null;
        public void SetParent(Transform parent) { }
        public void SetParent(Transform parent, bool worldPositionStays) { }
        public void LookAt(Vector3 worldPosition) { }
        public void SetAsLastSibling() { }
        public void SetAsFirstSibling() { }
        public System.Collections.IEnumerator GetEnumerator() => throw null;
    }

    public sealed class RectTransform : Transform
    {
        public Vector2 anchorMin { get; set; }
        public Vector2 anchorMax { get; set; }
        public Vector2 offsetMin { get; set; }
        public Vector2 offsetMax { get; set; }
        public Vector2 pivot { get; set; }
        public Vector2 sizeDelta { get; set; }
        public Vector2 anchoredPosition { get; set; }
        public Rect rect => throw null;
    }

    public static class RectTransformUtility
    {
        public static bool ScreenPointToLocalPointInRectangle(RectTransform rect, Vector2 screenPoint, Camera cam, out Vector2 localPoint) => throw null;
    }

    public struct Rect
    {
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
        public float x { get; set; }
        public float y { get; set; }
        public float width { get; set; }
        public float height { get; set; }
        public float xMin => x;
        public float yMin => y;
        public float xMax => x + width;
        public float yMax => y + height;
    }

    public class RectOffset
    {
        public RectOffset() { }
        public RectOffset(int left, int right, int top, int bottom) { }
        public int left { get; set; }
        public int right { get; set; }
        public int top { get; set; }
        public int bottom { get; set; }
    }

    public struct Vector2
    {
        public float x;
        public float y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 one => new Vector2(1, 1);
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.x / d, a.y / d);
        public static float Distance(Vector2 a, Vector2 b) => throw null;
        public float magnitude => throw null;
    }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public Vector3(float x, float y) { this.x = x; this.y = y; z = 0; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static float Distance(Vector3 a, Vector3 b) => throw null;
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => throw null;
        public static Vector3 right => new Vector3(1, 0, 0);
        public static Vector3 forward => new Vector3(0, 0, 1);
        public Vector3 normalized => throw null;
        public float magnitude => throw null;
    }

    public struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;
        public static Quaternion identity => throw null;
        public static Quaternion Euler(float x, float y, float z) => throw null;
        public static Quaternion LookRotation(Vector3 forward) => throw null;
        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards) => throw null;
        public static Quaternion AngleAxis(float angle, Vector3 axis) => throw null;
        public static Quaternion operator *(Quaternion lhs, Quaternion rhs) => throw null;
        public static Vector3 operator *(Quaternion rotation, Vector3 point) => throw null;
    }

    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; a = 1f; }
        public static Color white => new Color(1, 1, 1, 1);
        public static Color black => new Color(0, 0, 0, 1);
        public static Color clear => new Color(0, 0, 0, 0);
        public static implicit operator Color(Color32 c) => throw null;
        public static implicit operator Color32(Color c) => throw null;
    }

    public struct Color32
    {
        public byte r;
        public byte g;
        public byte b;
        public byte a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct Mathf
    {
        public const float PI = 3.14159274f;
        public const float Rad2Deg = 57.29578f;
        public const float Deg2Rad = 0.0174532924f;
        public static float Sin(float f) => throw null;
        public static float Cos(float f) => throw null;
        public static float Atan2(float y, float x) => throw null;
        public static float Abs(float f) => throw null;
        public static int Abs(int value) => throw null;
        public static float Min(float a, float b) => throw null;
        public static int Min(int a, int b) => throw null;
        public static float Max(float a, float b) => throw null;
        public static int Max(int a, int b) => throw null;
        public static float Clamp(float value, float min, float max) => throw null;
        public static int Clamp(int value, int min, int max) => throw null;
        public static float Clamp01(float value) => throw null;
        public static int RoundToInt(float f) => throw null;
        public static float Lerp(float a, float b, float t) => throw null;
        public static bool Approximately(float a, float b) => throw null;
    }

    public enum CameraClearFlags
    {
        Skybox = 1,
        SolidColor = 2,
        Depth = 3,
        Nothing = 4,
    }

    public sealed class Camera : Behaviour
    {
        public static Camera main => throw null;
        public float fieldOfView { get; set; }
        public CameraClearFlags clearFlags { get; set; }
        public Color backgroundColor { get; set; }
        public float nearClipPlane { get; set; }
        public float farClipPlane { get; set; }
        public bool orthographic { get; set; }
        public Vector3 WorldToScreenPoint(Vector3 position) => throw null;
    }

    public enum LightType
    {
        Spot = 0,
        Directional = 1,
        Point = 2,
    }

    public sealed class Light : Behaviour
    {
        public LightType type { get; set; }
        public float intensity { get; set; }
        public Color color { get; set; }
        public LightShadows shadows { get; set; }
    }

    public sealed class AudioListener : Behaviour
    {
    }

    public class Collider : Component
    {
        public bool enabled { get; set; }
    }

    public class Renderer : Component
    {
        public Material material { get; set; }
        public Material sharedMaterial { get; set; }
        public Material[] sharedMaterials { get; set; }
        public bool enabled { get; set; }
    }

    public sealed class MeshRenderer : Renderer
    {
    }

    public sealed class LineRenderer : Renderer
    {
        public int positionCount { get; set; }
        public float startWidth { get; set; }
        public float endWidth { get; set; }
        public Color startColor { get; set; }
        public Color endColor { get; set; }
        public bool useWorldSpace { get; set; }
        public void SetPositions(Vector3[] positions) { }
        public void SetPosition(int index, Vector3 position) { }
    }

    public sealed class Shader : Object
    {
        public static Shader Find(string name) => throw null;
    }

    public class Material : Object
    {
        public Material(Shader shader) { }
        public Material(Material source) { }
        public Color color { get; set; }
        public Shader shader { get; set; }
        public Texture mainTexture { get; set; }
        public int renderQueue { get; set; }
    }

    public enum FilterMode
    {
        Point = 0,
        Bilinear = 1,
        Trilinear = 2,
    }

    public enum TextureWrapMode
    {
        Repeat = 0,
        Clamp = 1,
    }

    public enum TextureFormat
    {
        RGB24 = 3,
        RGBA32 = 4,
    }

    public class Texture : Object
    {
        public FilterMode filterMode { get; set; }
        public TextureWrapMode wrapMode { get; set; }
        public int width => throw null;
        public int height => throw null;
    }

    public sealed class Texture2D : Texture
    {
        public Texture2D(int width, int height) { }
        public Texture2D(int width, int height, TextureFormat textureFormat, bool mipChain) { }
        public void SetPixels32(Color32[] colors) { }
        public Color32[] GetPixels32() => throw null;
        public void Apply() { }
        public void Apply(bool updateMipmaps) { }
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
    }

    public sealed class Font : Object
    {
    }

    public class TextAsset : Object
    {
        public string text => throw null;
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => throw null;
        public static T GetBuiltinResource<T>(string path) where T : Object => throw null;
    }

    public enum RuntimePlatform
    {
        WindowsEditor = 7,
        Android = 11,
        LinuxEditor = 16,
    }

    public static class Application
    {
        public static string persistentDataPath => throw null;
        public static string unityVersion => throw null;
        public static string version => throw null;
        public static RuntimePlatform platform => throw null;
        public static bool isEditor => throw null;
        public static bool isPlaying => throw null;
        public static bool isBatchMode => throw null;
        public static int targetFrameRate { get; set; }
        public static NetworkReachability internetReachability => throw null;
        public static void Quit() { }
        public static void Quit(int exitCode) { }
        public static void OpenURL(string url) { }
    }

    public sealed class SleepTimeout
    {
        public const int NeverSleep = -1;
        public const int SystemSetting = -2;
    }

    public sealed class Screen
    {
        public static int width => throw null;
        public static int height => throw null;
        public static int sleepTimeout { get; set; }
    }

    public class Time
    {
        public static float deltaTime => throw null;
        public static float unscaledDeltaTime => throw null;
        public static float realtimeSinceStartup => throw null;
        public static int frameCount => throw null;
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
        public static void LogException(Exception exception) { }
    }

    public class PlayerPrefs
    {
        public static bool HasKey(string key) => throw null;
        public static float GetFloat(string key, float defaultValue) => throw null;
        public static int GetInt(string key, int defaultValue) => throw null;
        public static string GetString(string key, string defaultValue) => throw null;
        public static void SetFloat(string key, float value) { }
        public static void SetInt(string key, int value) { }
        public static void SetString(string key, string value) { }
        public static void DeleteKey(string key) { }
        public static void Save() { }
    }

    public sealed class AudioClip : Object
    {
        public static AudioClip Create(string name, int lengthSamples, int channels, int frequency, bool stream) => throw null;
        public bool SetData(float[] data, int offsetSamples) => throw null;
        public float length => throw null;
    }

    public sealed class AudioSource : Behaviour
    {
        public AudioClip clip { get; set; }
        public float volume { get; set; }
        public bool loop { get; set; }
        public bool playOnAwake { get; set; }
        public bool isPlaying => throw null;
        public void Play() { }
        public void Stop() { }
        public void PlayOneShot(AudioClip clip, float volumeScale) { }
    }

    public class Handheld
    {
        public static void Vibrate() { }
    }

    public sealed class SystemInfo
    {
        public static string deviceModel => throw null;
        public static int systemMemorySize => throw null;
    }

    public enum RenderMode
    {
        ScreenSpaceOverlay = 0,
        ScreenSpaceCamera = 1,
        WorldSpace = 2,
    }

    public sealed class Canvas : Behaviour
    {
        public RenderMode renderMode { get; set; }
        public int sortingOrder { get; set; }
    }

    public enum TextAnchor
    {
        UpperLeft = 0,
        UpperCenter = 1,
        UpperRight = 2,
        MiddleLeft = 3,
        MiddleCenter = 4,
        MiddleRight = 5,
        LowerLeft = 6,
        LowerCenter = 7,
        LowerRight = 8,
    }

    public enum HorizontalWrapMode
    {
        Wrap = 0,
        Overflow = 1,
    }

    public enum VerticalWrapMode
    {
        Truncate = 0,
        Overflow = 1,
    }

    public enum RuntimeInitializeLoadType
    {
        AfterSceneLoad = 0,
        BeforeSceneLoad = 1,
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { }
    }

    public class QualitySettings : Object
    {
        public static Rendering.RenderPipelineAsset renderPipeline { get; set; }
    }

    public class AndroidJavaObject : IDisposable
    {
        public AndroidJavaObject(string className, params object[] args) { }
        public void Dispose() { }
        public ReturnType Call<ReturnType>(string methodName, params object[] args) => throw null;
        public void Call(string methodName, params object[] args) { }
        public FieldType GetStatic<FieldType>(string fieldName) => throw null;
        public ReturnType CallStatic<ReturnType>(string methodName, params object[] args) => throw null;
    }

    public class AndroidJavaClass : AndroidJavaObject
    {
        public AndroidJavaClass(string className) : base(className) { }
    }
}

namespace UnityEngine.Profiling
{
    public sealed class Profiler
    {
        public static long GetTotalAllocatedMemoryLong() => throw null;
        public static long GetTotalReservedMemoryLong() => throw null;
        public static long GetMonoUsedSizeLong() => throw null;
        public static long GetMonoHeapSizeLong() => throw null;
        public static long GetAllocatedMemoryForGraphicsDriver() => throw null;
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public string path => throw null;
        public string name => throw null;
        public bool IsValid() => throw null;
        public GameObject[] GetRootGameObjects() => throw null;
    }
}

namespace UnityEngine.Rendering
{
    public abstract class RenderPipelineAsset : ScriptableObject
    {
    }

    public sealed class GraphicsSettings : Object
    {
        public static RenderPipelineAsset defaultRenderPipeline { get; set; }
    }
}

namespace UnityEngine.Rendering.Universal
{
    public abstract class ScriptableRendererData : ScriptableObject
    {
    }

    public class UniversalRendererData : ScriptableRendererData
    {
    }

    public class UniversalRenderPipelineAsset : RenderPipelineAsset
    {
        public static UniversalRenderPipelineAsset Create(ScriptableRendererData rendererData = null) => throw null;
    }
}

namespace UnityEngine.TestTools
{
    [AttributeUsage(AttributeTargets.Method)]
    public class UnityTestAttribute : Attribute
    {
    }
}
