using AstraKingdoms.Client.Localization;
using UnityEngine;

namespace AstraKingdoms.Client.Meta
{
    /// <summary>
    /// String table for the meta screens (Resources/MetaLocalization/{code}.txt). Uses the client's
    /// localization classes; only English exists so far, other languages fall back to it.
    /// Screens call <see cref="L"/>/<see cref="LF"/>; tests check every literal key exists.
    /// </summary>
    public sealed class MetaText
    {
        public const string ResourceFolder = "MetaLocalization/";

        private readonly Localizer _loc;

        public MetaText(Localizer loc) => _loc = loc;

        public static MetaText Load(string language)
        {
            var english = LoadTable(Localizer.English);
            var loc = new Localizer(english);
            foreach (string code in Localizer.SupportedLanguages)
            {
                if (code == Localizer.English) continue;
                LocalizationTable t = LoadTable(code);
                if (t.Count > 0) loc.AddLanguage(t);
            }
            loc.Language = loc.HasLanguage(language) ? language : Localizer.English;
            return new MetaText(loc);
        }

        private static LocalizationTable LoadTable(string code)
        {
            var asset = Resources.Load<TextAsset>(ResourceFolder + code);
            return LocalizationTable.Parse(code, asset == null ? string.Empty : asset.text);
        }

        public string L(string key) => _loc.Get(key);
        public string LF(string key, params object[] args) => _loc.Format(key, args);
    }
}
