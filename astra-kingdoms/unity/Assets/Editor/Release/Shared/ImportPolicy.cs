using System;

// Engine-independent import policy for unity/Assets/Art (tickets 69-71; plan "Initial asset budgets":
// textures normally <= 1,024 px with correct compression and colour space; "short mono effects
// where appropriate and streamed music when qualified"). ArtImportPolicy.cs maps these decisions to
// Unity importer settings; the .NET release tool links this file so the policy is unit-tested.
namespace AstraKingdoms.Release.Gates
{
    public enum TextureUse
    {
        /// <summary>Not under a managed folder: the policy leaves it alone.</summary>
        Unmanaged = 0,
        /// <summary>UI/store icons and atlases: sprite, no mipmaps, sRGB, alpha.</summary>
        UiSprite = 1,
        /// <summary>Character, prop and arena colour textures: mipmaps, sRGB.</summary>
        WorldColour = 2,
        /// <summary>Normal maps and packed masks: linear (not sRGB).</summary>
        WorldData = 3,
        /// <summary>Particle flipbooks: mipmaps off, sRGB, alpha.</summary>
        Effect = 4,
    }

    public enum AudioUse
    {
        Unmanaged = 0,
        /// <summary>Short one-shot effects: mono, decompressed on load (ADPCM) for low latency.</summary>
        ShortEffect = 1,
        /// <summary>Longer effects/jingles: mono, compressed in memory (Vorbis).</summary>
        LongEffect = 2,
        /// <summary>Music: Vorbis, streamed, loaded in background.</summary>
        Music = 3,
    }

    /// <summary>Texture importer decision (Unity-independent mirror of the importer fields we set).</summary>
    public sealed class TexturePreset
    {
        public TextureUse Use;
        public bool Sprite;
        public bool SRgb;
        public bool Mipmaps;
        public bool AlphaIsTransparency;
        public int MaxSize;
        /// <summary>Android override format: ASTC block size (4, 6 or 8 -> ASTC_4x4 / 6x6 / 8x8).</summary>
        public int AstcBlock;
    }

    public sealed class AudioPreset
    {
        public AudioUse Use;
        public bool ForceMono;
        /// <summary>DecompressOnLoad, CompressedInMemory or Streaming (UnityEngine.AudioClipLoadType names).</summary>
        public string LoadType = "";
        /// <summary>ADPCM or Vorbis (UnityEngine.AudioCompressionFormat names).</summary>
        public string Compression = "";
        /// <summary>Vorbis quality 0..1 (ignored for ADPCM).</summary>
        public double Quality;
        public bool LoadInBackground;
        public bool PreloadAudioData;
    }

    public static class ImportPolicy
    {
        public const string ArtRoot = "Assets/Art/";
        /// <summary>Effects at or under this length are decompressed on load; longer ones stay compressed.</summary>
        public const double ShortEffectSeconds = 1.0;

        public static TextureUse ClassifyTexture(string assetPath)
        {
            string p = Normalize(assetPath);
            if (!p.StartsWith(ArtRoot, StringComparison.Ordinal)) return TextureUse.Unmanaged;
            if (p.Contains("/Icons/") || p.Contains("/UI/") || p.Contains("/Store/")) return TextureUse.UiSprite;
            if (p.Contains("/VFX/") || p.Contains("/Effects/")) return TextureUse.Effect;
            string name = FileName(p);
            if (name.EndsWith("_n.png", StringComparison.Ordinal) || name.EndsWith("_normal.png", StringComparison.Ordinal) ||
                name.EndsWith("_mask.png", StringComparison.Ordinal) || name.EndsWith("_orm.png", StringComparison.Ordinal))
                return TextureUse.WorldData;
            return TextureUse.WorldColour;
        }

        public static TexturePreset ForTexture(string assetPath)
        {
            TextureUse use = ClassifyTexture(assetPath);
            switch (use)
            {
                case TextureUse.UiSprite:
                    return new TexturePreset { Use = use, Sprite = true, SRgb = true, Mipmaps = false, AlphaIsTransparency = true, MaxSize = 1024, AstcBlock = 4 };
                case TextureUse.Effect:
                    return new TexturePreset { Use = use, Sprite = false, SRgb = true, Mipmaps = false, AlphaIsTransparency = true, MaxSize = 512, AstcBlock = 6 };
                case TextureUse.WorldData:
                    return new TexturePreset { Use = use, Sprite = false, SRgb = false, Mipmaps = true, AlphaIsTransparency = false, MaxSize = 1024, AstcBlock = 6 };
                case TextureUse.WorldColour:
                    return new TexturePreset { Use = use, Sprite = false, SRgb = true, Mipmaps = true, AlphaIsTransparency = false, MaxSize = 1024, AstcBlock = 6 };
                default:
                    return null;
            }
        }

        /// <summary>
        /// Audio decision from the path (Music folder = music) and, for effects, the clip length in
        /// seconds when known (null = not known yet, treated as short).
        /// </summary>
        public static AudioPreset ForAudio(string assetPath, double? lengthSeconds)
        {
            string p = Normalize(assetPath);
            if (!p.StartsWith(ArtRoot, StringComparison.Ordinal)) return null;
            if (p.Contains("/Music/"))
                return new AudioPreset { Use = AudioUse.Music, ForceMono = false, LoadType = "Streaming", Compression = "Vorbis", Quality = 0.5, LoadInBackground = true, PreloadAudioData = false };
            if (lengthSeconds.HasValue && lengthSeconds.Value > ShortEffectSeconds)
                return new AudioPreset { Use = AudioUse.LongEffect, ForceMono = true, LoadType = "CompressedInMemory", Compression = "Vorbis", Quality = 0.6, LoadInBackground = false, PreloadAudioData = true };
            return new AudioPreset { Use = AudioUse.ShortEffect, ForceMono = true, LoadType = "DecompressOnLoad", Compression = "ADPCM", Quality = 1.0, LoadInBackground = false, PreloadAudioData = true };
        }

        private static string Normalize(string path) => (path ?? string.Empty).Replace('\\', '/');

        private static string FileName(string p)
        {
            int i = p.LastIndexOf('/');
            return (i >= 0 ? p.Substring(i + 1) : p).ToLowerInvariant();
        }
    }
}
