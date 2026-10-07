using System.Collections.Generic;
using AstraKingdoms.Client.Presentation;
using AstraKingdoms.Rules.Core;
using UnityEngine;

namespace AstraKingdoms.Client.UI
{
    /// <summary>Element icon textures generated from <see cref="ElementGlyphs"/> shape masks (white ink, tinted by the UI).</summary>
    public static class ElementIcons
    {
        public const int Size = 64;
        private static readonly Dictionary<Element, Texture2D> Cache = new Dictionary<Element, Texture2D>();

        public static Texture2D Get(Element element)
        {
            if (Cache.TryGetValue(element, out Texture2D tex) && tex != null) return tex;
            bool[] mask = ElementGlyphs.Rasterize(element, Size);
            tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.name = "ElementIcon_" + element;
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            var pixels = new Color32[Size * Size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = mask[i] ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            Cache[element] = tex;
            return tex;
        }

        /// <summary>Element tint used together with (never instead of) the shape and label.</summary>
        public static Color Tint(Element element)
        {
            switch (element)
            {
                case Element.Agni: return new Color(1f, 0.45f, 0.2f);
                case Element.Vayu: return new Color(0.7f, 0.95f, 0.85f);
                case Element.Prithvi: return new Color(0.75f, 0.6f, 0.4f);
                case Element.Vidyut: return new Color(1f, 0.95f, 0.35f);
                case Element.Varuna: return new Color(0.35f, 0.65f, 1f);
                default: return new Color(0.85f, 0.85f, 0.85f);
            }
        }
    }
}
