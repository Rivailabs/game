// Compile-only stubs of the UnityEditor APIs used by the build and scene scripts. Never executed.
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string itemName) { }
        public MenuItem(string itemName, bool isValidateFunction) { }
        public MenuItem(string itemName, bool isValidateFunction, int priority) { }
    }

    public sealed class EditorApplication
    {
        public static bool isPlaying { get; set; }
        public static void Exit(int returnValue) { }
    }

    public sealed class AssetDatabase
    {
        public static bool IsValidFolder(string path) => throw null;
        public static string CreateFolder(string parentFolder, string newFolderName) => throw null;
        public static void CreateAsset(UnityEngine.Object asset, string path) { }
        public static T LoadAssetAtPath<T>(string assetPath) where T : UnityEngine.Object => throw null;
        public static void SaveAssets() { }
        public static void Refresh() { }
        public static void AddObjectToAsset(UnityEngine.Object objectToAdd, UnityEngine.Object assetObject) { }
        public static bool DeleteAsset(string path) => throw null;
        public static string[] FindAssets(string filter, string[] searchInFolders) => throw null;
        public static string GUIDToAssetPath(string guid) => throw null;
    }

    public class EditorBuildSettingsScene
    {
        public EditorBuildSettingsScene(string path, bool enabled) { }
        public string path { get; set; }
        public bool enabled { get; set; }
    }

    public sealed class EditorBuildSettings : UnityEngine.Object
    {
        public static EditorBuildSettingsScene[] scenes { get; set; }
    }

    public enum BuildTarget
    {
        StandaloneWindows64 = 19,
        Android = 13,
        iOS = 9,
    }

    public enum BuildTargetGroup
    {
        Unknown = 0,
        Standalone = 1,
        iOS = 4,
        Android = 7,
    }

    [Flags]
    public enum BuildOptions
    {
        None = 0,
        Development = 1,
        AutoRunPlayer = 4,
        AllowDebugging = 512,
    }

    public struct BuildPlayerOptions
    {
        public string[] scenes { get; set; }
        public string locationPathName { get; set; }
        public BuildTargetGroup targetGroup { get; set; }
        public BuildTarget target { get; set; }
        public BuildOptions options { get; set; }
    }

    public class BuildPipeline
    {
        public static Build.Reporting.BuildReport BuildPlayer(BuildPlayerOptions buildPlayerOptions) => throw null;
    }

    public enum ScriptingImplementation
    {
        Mono2x = 0,
        IL2CPP = 1,
    }

    public enum UIOrientation
    {
        Portrait = 0,
        PortraitUpsideDown = 1,
        LandscapeRight = 2,
        LandscapeLeft = 3,
        AutoRotation = 4,
    }

    [Flags]
    public enum AndroidArchitecture : uint
    {
        None = 0,
        ARMv7 = 1,
        ARM64 = 2,
        X86_64 = 8,
    }

    public enum AndroidSdkVersions
    {
        AndroidApiLevelAuto = 0,
        AndroidApiLevel23 = 23,
        AndroidApiLevel24 = 24,
        AndroidApiLevel25 = 25,
        AndroidApiLevel26 = 26,
    }

    [Flags]
    public enum AndroidApplicationEntry
    {
        Activity = 1,
        GameActivity = 2,
    }

    public sealed class PlayerSettings : UnityEngine.Object
    {
        public static string companyName { get; set; }
        public static string productName { get; set; }
        public static string bundleVersion { get; set; }
        public static UIOrientation defaultInterfaceOrientation { get; set; }
        public static void SetApplicationIdentifier(Build.NamedBuildTarget buildTarget, string identifier) { }
        public static void SetScriptingBackend(Build.NamedBuildTarget buildTarget, ScriptingImplementation backend) { }

        public sealed class Android
        {
            public static AndroidArchitecture targetArchitectures { get; set; }
            public static AndroidSdkVersions minSdkVersion { get; set; }
            public static AndroidApplicationEntry applicationEntry { get; set; }
            public static int bundleVersionCode { get; set; }
        }
    }

    public class EditorUserBuildSettings
    {
        public static BuildTarget activeBuildTarget => throw null;
        public static bool buildAppBundle { get; set; }
        public static bool SwitchActiveBuildTarget(BuildTargetGroup targetGroup, BuildTarget target) => throw null;
    }
}

namespace UnityEditor.Build
{
    public readonly struct NamedBuildTarget
    {
        public static readonly NamedBuildTarget Android;
        public static readonly NamedBuildTarget Standalone;
        public string TargetName => throw null;
    }
}

namespace UnityEditor.Build.Reporting
{
    public enum BuildResult
    {
        Unknown = 0,
        Succeeded = 1,
        Failed = 2,
        Cancelled = 3,
    }

    public struct BuildSummary
    {
        public BuildResult result => throw null;
        public ulong totalSize => throw null;
        public int totalErrors => throw null;
        public int totalWarnings => throw null;
        public TimeSpan totalTime => throw null;
        public string outputPath => throw null;
    }

    public sealed class BuildReport : UnityEngine.Object
    {
        public BuildSummary summary => throw null;
    }
}

namespace UnityEditor.SceneManagement
{
    public enum NewSceneSetup
    {
        EmptyScene = 0,
        DefaultGameObjects = 1,
    }

    public enum NewSceneMode
    {
        Single = 0,
        Additive = 1,
    }

    public sealed class EditorSceneManager
    {
        public static Scene NewScene(NewSceneSetup setup, NewSceneMode mode = NewSceneMode.Single) => throw null;
        public static bool SaveScene(Scene scene, string dstScenePath = "", bool saveAsCopy = false) => throw null;
        public static bool SaveCurrentModifiedScenesIfUserWantsTo() => throw null;
    }
}
