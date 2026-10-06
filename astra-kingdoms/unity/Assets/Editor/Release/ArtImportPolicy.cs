using System.Collections.Generic;
using AstraKingdoms.Release.Gates;
using UnityEditor;
using UnityEngine;

namespace AstraKingdoms.EditorTools.Release
{
    /// <summary>
    /// Import presets for everything under <c>Assets/Art/</c> (tickets 69-71), applied on import so
    /// a newly delivered texture or clip cannot arrive with desktop defaults. The decisions come
    /// from <see cref="ImportPolicy"/> (engine-independent, unit-tested by the release tool):
    /// <list type="bullet">
    /// <item>UI/store icons: Sprite, no mipmaps, sRGB, alpha, <= 1,024 px, Android ASTC 4x4.</item>
    /// <item>World colour/data textures: mipmaps, sRGB (colour) or linear (normal/mask), <= 1,024 px, ASTC 6x6.</item>
    /// <item>Effect flipbooks: no mipmaps, <= 512 px, ASTC 6x6.</item>
    /// <item>Effects <= 1 s: mono, Decompress On Load, ADPCM. Longer effects: mono, Compressed In Memory, Vorbis.</item>
    /// <item>Music: Vorbis 0.5, Streaming, load in background, no preload.</item>
    /// </list>
    /// The Android override is set explicitly so the "Default" platform cannot hide a wrong mobile
    /// format. <see cref="Audit"/> lists importers that differ from the policy (used by the release gate).
    /// </summary>
    public sealed class ArtImportPolicy : AssetPostprocessor
    {
        public const string AndroidPlatform = "Android";

        public override int GetPostprocessOrder() => 10;

        private void OnPreprocessTexture()
        {
            if (assetImporter is TextureImporter ti) ApplyTexture(ti, assetPath);
        }

        private void OnPreprocessAudio()
        {
            if (assetImporter is AudioImporter ai) ApplyAudio(ai, assetPath, null);
        }

        /// <summary>Second pass once the clip length is known: long effects move to compressed-in-memory.</summary>
        private void OnPostprocessAudio(AudioClip clip)
        {
            if (clip == null || !(assetImporter is AudioImporter ai)) return;
            AudioPreset want = ImportPolicy.ForAudio(assetPath, clip.length);
            if (want != null && want.Use == AudioUse.LongEffect && ai.defaultSampleSettings.loadType != AudioClipLoadType.CompressedInMemory)
            {
                ApplyAudio(ai, assetPath, clip.length);
                ai.SaveAndReimport();
            }
        }

        public static bool ApplyTexture(TextureImporter ti, string path)
        {
            TexturePreset p = ImportPolicy.ForTexture(path);
            if (p == null) return false;
            ti.textureType = p.Sprite ? TextureImporterType.Sprite : TextureImporterType.Default;
            ti.sRGBTexture = p.SRgb;
            ti.mipmapEnabled = p.Mipmaps;
            ti.alphaIsTransparency = p.AlphaIsTransparency;
            ti.isReadable = false;
            ti.maxTextureSize = p.MaxSize;
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = AndroidPlatform,
                overridden = true,
                maxTextureSize = p.MaxSize,
                format = Astc(p.AstcBlock),
                compressionQuality = 50,
            });
            return true;
        }

        public static bool ApplyAudio(AudioImporter ai, string path, double? lengthSeconds)
        {
            AudioPreset p = ImportPolicy.ForAudio(path, lengthSeconds);
            if (p == null) return false;
            ai.forceToMono = p.ForceMono;
            ai.loadInBackground = p.LoadInBackground;
            AudioImporterSampleSettings s = Sample(p);
            ai.defaultSampleSettings = s;
            ai.SetOverrideSampleSettings(AndroidPlatform, s);
            return true;
        }

        public static AudioImporterSampleSettings Sample(AudioPreset p) => new AudioImporterSampleSettings
        {
            loadType = p.LoadType == "Streaming" ? AudioClipLoadType.Streaming
                : p.LoadType == "CompressedInMemory" ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad,
            compressionFormat = p.Compression == "Vorbis" ? AudioCompressionFormat.Vorbis : AudioCompressionFormat.ADPCM,
            quality = (float)p.Quality,
            sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate,
            preloadAudioData = p.PreloadAudioData,
        };

        public static TextureImporterFormat Astc(int block)
        {
            switch (block)
            {
                case 4: return TextureImporterFormat.ASTC_4x4;
                case 8: return TextureImporterFormat.ASTC_8x8;
                default: return TextureImporterFormat.ASTC_6x6;
            }
        }

        /// <summary>Importers under Assets/Art whose settings differ from the policy (empty = conforming).</summary>
        public static List<string> Audit()
        {
            var problems = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Art" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TexturePreset p = ImportPolicy.ForTexture(path);
                if (p == null || !(AssetImporter.GetAtPath(path) is TextureImporter ti)) continue;
                TextureImporterPlatformSettings android = ti.GetPlatformTextureSettings(AndroidPlatform);
                if (ti.maxTextureSize > p.MaxSize) problems.Add(path + ": max size " + ti.maxTextureSize + " > " + p.MaxSize);
                if (ti.sRGBTexture != p.SRgb) problems.Add(path + ": sRGB should be " + p.SRgb);
                if (ti.mipmapEnabled != p.Mipmaps) problems.Add(path + ": mipmaps should be " + p.Mipmaps);
                if ((ti.textureType == TextureImporterType.Sprite) != p.Sprite) problems.Add(path + ": sprite import should be " + p.Sprite);
                if (android == null || !android.overridden || android.format != Astc(p.AstcBlock))
                    problems.Add(path + ": Android override must be " + Astc(p.AstcBlock));
            }
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Art" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                AudioPreset p = ImportPolicy.ForAudio(path, clip != null ? clip.length : (double?)null);
                if (p == null || !(AssetImporter.GetAtPath(path) is AudioImporter ai)) continue;
                AudioImporterSampleSettings want = Sample(p), have = ai.GetOverrideSampleSettings(AndroidPlatform);
                if (ai.forceToMono != p.ForceMono) problems.Add(path + ": force-to-mono should be " + p.ForceMono);
                if (have.loadType != want.loadType) problems.Add(path + ": Android load type " + have.loadType + " should be " + want.loadType);
                if (have.compressionFormat != want.compressionFormat) problems.Add(path + ": Android compression " + have.compressionFormat + " should be " + want.compressionFormat);
            }
            return problems;
        }

        [MenuItem("Astra Kingdoms/Release/Reapply Art Import Presets")]
        public static void ReapplyAll()
        {
            int n = 0;
            foreach (string filter in new[] { "t:Texture2D", "t:AudioClip" })
            {
                foreach (string guid in AssetDatabase.FindAssets(filter, new[] { "Assets/Art" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    AssetImporter imp = AssetImporter.GetAtPath(path);
                    bool changed = false;
                    if (imp is TextureImporter ti) changed = ApplyTexture(ti, path);
                    else if (imp is AudioImporter ai)
                    {
                        AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                        changed = ApplyAudio(ai, path, clip != null ? clip.length : (double?)null);
                    }
                    if (changed)
                    {
                        imp.SaveAndReimport();
                        n++;
                    }
                }
            }
            Debug.Log("[Release] Reapplied import presets to " + n + " asset(s) under Assets/Art.");
        }
    }
}
