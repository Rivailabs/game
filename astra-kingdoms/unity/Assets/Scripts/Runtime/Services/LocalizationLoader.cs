using AstraKingdoms.Client.Localization;
using UnityEngine;

namespace AstraKingdoms.Client.Services
{
    /// <summary>Loads Resources/Localization/{en,hi,kn}.txt into a <see cref="Localizer"/>.</summary>
    public static class LocalizationLoader
    {
        public const string ResourceFolder = "Localization/";

        public static Localizer Load(string language)
        {
            LocalizationTable english = LoadTable(Localizer.English);
            var loc = new Localizer(english);
            foreach (string code in Localizer.SupportedLanguages)
            {
                if (code == Localizer.English) continue;
                LocalizationTable t = LoadTable(code);
                if (t.Count > 0) loc.AddLanguage(t);
            }
            loc.Language = language;
            loc.MissingKey += (lang, key) => Debug.LogWarning("[Localization] '" + key + "' missing in " + lang + "; using fallback.");
            foreach (string error in english.Errors) Debug.LogError("[Localization] en.txt " + error);
            return loc;
        }

        public static LocalizationTable LoadTable(string code)
        {
            var asset = Resources.Load<TextAsset>(ResourceFolder + code);
            if (asset == null)
            {
                Debug.LogError("[Localization] Missing Resources/" + ResourceFolder + code + ".txt");
                return LocalizationTable.Parse(code, string.Empty);
            }
            return LocalizationTable.Parse(code, asset.text);
        }
    }
}
