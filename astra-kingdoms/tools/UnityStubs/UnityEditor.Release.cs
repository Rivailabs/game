// Compile-only stubs of the UnityEditor importer and build-report APIs used by
// unity/Assets/Editor/Release (V1 tickets 69-76). Signatures follow the Unity 6 scripting reference
// as known when written; they are NOT generated from Unity's assemblies, so the first real editor
// compile must confirm them (listed in astra-kingdoms/release/README.md). Never executed.
using System;

namespace UnityEngine
{
    public enum AudioClipLoadType
    {
        DecompressOnLoad = 0,
        CompressedInMemory = 1,
        Streaming = 2,
    }

    public enum AudioCompressionFormat
    {
        PCM = 0,
        Vorbis = 1,
        ADPCM = 2,
        MP3 = 3,
        AAC = 6,
    }
}

namespace UnityEditor
{
    public class AssetImporter : UnityEngine.Object
    {
        public string assetPath => throw null;
        public void SaveAndReimport() { }
        public static AssetImporter GetAtPath(string path) => throw null;
    }

    public class AssetPostprocessor
    {
        public string assetPath { get; set; }
        public AssetImporter assetImporter => throw null;
        public virtual int GetPostprocessOrder() => throw null;
    }

    public enum TextureImporterType
    {
        Default = 0,
        NormalMap = 1,
        Sprite = 8,
    }

    public enum TextureImporterCompression
    {
        Uncompressed = 0,
        Compressed = 1,
        CompressedHQ = 2,
        CompressedLQ = 3,
    }

    public enum TextureImporterFormat
    {
        Automatic = -1,
        RGBA32 = 4,
        ASTC_4x4 = 48,
        ASTC_5x5 = 49,
        ASTC_6x6 = 50,
        ASTC_8x8 = 51,
        ASTC_10x10 = 52,
        ASTC_12x12 = 53,
    }

    public sealed class TextureImporterPlatformSettings
    {
        public string name { get; set; }
        public bool overridden { get; set; }
        public int maxTextureSize { get; set; }
        public TextureImporterFormat format { get; set; }
        public int compressionQuality { get; set; }
    }

    public sealed class TextureImporter : AssetImporter
    {
        public TextureImporterType textureType { get; set; }
        public bool sRGBTexture { get; set; }
        public bool mipmapEnabled { get; set; }
        public bool alphaIsTransparency { get; set; }
        public bool isReadable { get; set; }
        public int maxTextureSize { get; set; }
        public TextureImporterCompression textureCompression { get; set; }
        public TextureImporterPlatformSettings GetPlatformTextureSettings(string platform) => throw null;
        public void SetPlatformTextureSettings(TextureImporterPlatformSettings platformSettings) { }
    }

    public enum AudioSampleRateSetting
    {
        PreserveSampleRate = 0,
        OptimizeSampleRate = 1,
        OverrideSampleRate = 2,
    }

    public struct AudioImporterSampleSettings
    {
        public UnityEngine.AudioClipLoadType loadType;
        public AudioSampleRateSetting sampleRateSetting;
        public uint sampleRateOverride;
        public UnityEngine.AudioCompressionFormat compressionFormat;
        public float quality;
        public int conversionMode;
        public bool preloadAudioData;
    }

    public sealed class AudioImporter : AssetImporter
    {
        public bool forceToMono { get; set; }
        public bool loadInBackground { get; set; }
        public AudioImporterSampleSettings defaultSampleSettings { get; set; }
        public bool SetOverrideSampleSettings(string platform, AudioImporterSampleSettings settings) => throw null;
        public AudioImporterSampleSettings GetOverrideSampleSettings(string platform) => throw null;
    }
}

namespace UnityEditor.Build
{
    public interface IOrderedCallback
    {
        int callbackOrder { get; }
    }

    public interface IPostprocessBuildWithReport : IOrderedCallback
    {
        void OnPostprocessBuild(Reporting.BuildReport report);
    }
}

namespace UnityEditor.Build.Reporting
{
    public struct BuildFile
    {
        public uint id => throw null;
        public string path => throw null;
        public string role => throw null;
        public ulong size => throw null;
    }

    public struct PackedAssetInfo
    {
        public string sourceAssetPath => throw null;
        public ulong packedSize => throw null;
        public Type type => throw null;
    }

    public sealed class PackedAssets : UnityEngine.Object
    {
        public string shortPath => throw null;
        public ulong overhead => throw null;
        public PackedAssetInfo[] contents => throw null;
    }

    public sealed partial class BuildReport
    {
        public BuildFile[] GetFiles() => throw null;
        public PackedAssets[] packedAssets => throw null;
    }
}
