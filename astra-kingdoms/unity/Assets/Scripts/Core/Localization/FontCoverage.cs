using System;
using System.Collections.Generic;
using System.Globalization;

namespace AstraKingdoms.Client.Localization
{
    /// <summary>A Unicode range that one planned font face must cover.</summary>
    public sealed class FontRange
    {
        public string Name { get; }
        public int First { get; }
        public int Last { get; }
        /// <summary>The planned font face (asset-ledger ID) expected to supply these glyphs.</summary>
        public string PlannedFace { get; }

        public FontRange(string name, int first, int last, string plannedFace)
        {
            Name = name;
            First = first;
            Last = last;
            PlannedFace = plannedFace;
        }

        public bool Contains(int codePoint) => codePoint >= First && codePoint <= Last;
    }

    /// <summary>
    /// Font coverage plan for English, Hindi and Kannada (ticket 48). The pilot UI uses Unity's
    /// legacy uGUI <c>Text</c> with the built-in font, which has no Devanagari or Kannada glyphs
    /// and performs no complex-script shaping (conjuncts, matras, reph). Correct Indic rendering
    /// therefore needs both licensed fonts that cover these ranges <i>and</i> a shaping text path
    /// (TextMesh Pro / Unity Text with HarfBuzz-based shaping, confirmed on the reference phone).
    /// This class lists the code points every string table actually needs, grouped by range, so
    /// the font atlas character sets can be generated from data instead of guessed.
    /// </summary>
    public static class FontCoverage
    {
        public const string LatinFace = "font.noto-sans";
        public const string DevanagariFace = "font.noto-sans-devanagari";
        public const string KannadaFace = "font.noto-sans-kannada";
        public const string SymbolsFace = "font.noto-sans-symbols";

        /// <summary>The planned faces and the ranges each must cover.</summary>
        public static readonly IReadOnlyList<FontRange> Plan = new[]
        {
            new FontRange("Basic Latin", 0x0020, 0x007E, LatinFace),
            new FontRange("Latin-1 punctuation and signs", 0x00A0, 0x00FF, LatinFace),
            new FontRange("General Punctuation", 0x2000, 0x206F, LatinFace),
            new FontRange("Devanagari", 0x0900, 0x097F, DevanagariFace),
            new FontRange("Kannada", 0x0C80, 0x0CFF, KannadaFace),
            new FontRange("Arrows", 0x2190, 0x21FF, SymbolsFace),
            new FontRange("Mathematical operators", 0x2200, 0x22FF, SymbolsFace),
            new FontRange("Geometric shapes", 0x25A0, 0x25FF, SymbolsFace),
            new FontRange("Miscellaneous symbols", 0x2600, 0x26FF, SymbolsFace),
            new FontRange("Dingbats", 0x2700, 0x27BF, SymbolsFace),
        };

        /// <summary>Zero-width joiner/non-joiner and the BOM are shaping controls, not glyphs.</summary>
        public static bool IsFormatControl(int cp) => cp == 0x200C || cp == 0x200D || cp == 0xFEFF;

        /// <summary>Distinct code points used by the table's values (newlines excluded), ascending.</summary>
        public static SortedSet<int> CodePoints(LocalizationTable table)
        {
            var set = new SortedSet<int>();
            foreach (string key in table.Keys)
            {
                table.TryGet(key, out string value);
                AddCodePoints(set, value);
            }
            return set;
        }

        public static void AddCodePoints(ISet<int> set, string value)
        {
            if (value == null) return;
            for (int i = 0; i < value.Length; i++)
            {
                int cp = char.IsSurrogatePair(value, i) ? char.ConvertToUtf32(value, i++) : value[i];
                if (cp == '\n') continue;
                set.Add(cp);
            }
        }

        /// <summary>Code points of a table that no planned range covers (should be empty).</summary>
        public static IReadOnlyList<int> Uncovered(LocalizationTable table)
        {
            var missing = new List<int>();
            foreach (int cp in CodePoints(table))
            {
                if (IsFormatControl(cp)) continue;
                bool covered = false;
                foreach (FontRange r in Plan) covered |= r.Contains(cp);
                if (!covered) missing.Add(cp);
            }
            return missing;
        }

        /// <summary>Planned faces a language needs, from the code points it actually uses.</summary>
        public static IReadOnlyList<string> FacesNeeded(LocalizationTable table)
        {
            var faces = new List<string>();
            foreach (int cp in CodePoints(table))
                foreach (FontRange r in Plan)
                    if (r.Contains(cp) && !faces.Contains(r.PlannedFace)) faces.Add(r.PlannedFace);
            faces.Sort(StringComparer.Ordinal);
            return faces;
        }

        /// <summary>The characters for a font atlas of one face (for TextMesh Pro's "characters from file").</summary>
        public static string AtlasCharacters(IEnumerable<LocalizationTable> tables, string face)
        {
            var set = new SortedSet<int>();
            foreach (LocalizationTable t in tables)
                foreach (int cp in CodePoints(t))
                    foreach (FontRange r in Plan)
                        if (r.PlannedFace == face && r.Contains(cp)) set.Add(cp);
            var chars = new System.Text.StringBuilder();
            foreach (int cp in set) chars.Append(char.ConvertFromUtf32(cp));
            return chars.ToString();
        }

        public static string Describe(int cp) => "U+" + cp.ToString("X4", CultureInfo.InvariantCulture);
    }
}
