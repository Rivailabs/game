using System;
using System.Collections.Generic;
using System.Globalization;

namespace AstraKingdoms.Client.Localization
{
    /// <summary>
    /// Key-based string lookup with a safe fallback chain: current language, then English, then a
    /// neutral placeholder. A raw key is never shown to players (plan: "Language and accessibility").
    /// English is the complete reference; Hindi and Kannada files carry the same keys and are marked
    /// for fluent-speaker review.
    /// </summary>
    public sealed class Localizer
    {
        public const string English = "en";
        public const string Hindi = "hi";
        public const string Kannada = "kn";
        /// <summary>Shown only if a key is missing even from English (tests prevent this).</summary>
        public const string MissingPlaceholder = "…";

        public static readonly IReadOnlyList<string> SupportedLanguages = new[] { English, Hindi, Kannada };

        private readonly Dictionary<string, LocalizationTable> _tables = new Dictionary<string, LocalizationTable>(StringComparer.Ordinal);
        private string _language = English;

        /// <summary>Raised once per (language, key) lookup that had to fall back.</summary>
        public event Action<string, string> MissingKey;

        public Localizer(LocalizationTable english)
        {
            if (english == null) throw new ArgumentNullException(nameof(english));
            _tables[English] = english;
        }

        public LocalizationTable EnglishTable => _tables[English];

        public void AddLanguage(LocalizationTable table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            _tables[table.LanguageCode] = table;
        }

        public bool HasLanguage(string code) => code != null && _tables.ContainsKey(code);

        /// <summary>Current language; unknown codes fall back to English.</summary>
        public string Language
        {
            get => _language;
            set => _language = HasLanguage(value) ? value : English;
        }

        /// <summary>Language names written in their own script (never translated).</summary>
        public static string Endonym(string code)
        {
            switch (code)
            {
                case Hindi: return "हिन्दी";
                case Kannada: return "ಕನ್ನಡ";
                default: return "English";
            }
        }

        public string Get(string key)
        {
            if (key == null) return MissingPlaceholder;
            if (_language != English && _tables.TryGetValue(_language, out LocalizationTable t) && t.TryGet(key, out string v)) return v;
            if (_language != English) MissingKey?.Invoke(_language, key);
            if (_tables[English].TryGet(key, out string en)) return en;
            MissingKey?.Invoke(English, key);
            return MissingPlaceholder;
        }

        /// <summary>Looks up a template and fills positional parameters (invariant number formatting).</summary>
        public string Format(string key, params object[] args)
        {
            string template = Get(key);
            if (args == null || args.Length == 0) return template;
            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, args);
            }
            catch (FormatException)
            {
                // A malformed translation must not crash the match screen; English is the safe form.
                string en;
                if (_tables[English].TryGet(key, out en))
                {
                    try { return string.Format(CultureInfo.InvariantCulture, en, args); }
                    catch (FormatException) { }
                }
                return template;
            }
        }

        /// <summary>Keys present in English but missing from <paramref name="code"/> (empty for English).</summary>
        public IReadOnlyList<string> MissingKeys(string code)
        {
            var missing = new List<string>();
            if (!_tables.TryGetValue(code, out LocalizationTable t)) return _tables[English].Keys;
            foreach (string key in _tables[English].Keys)
                if (!t.Contains(key)) missing.Add(key);
            return missing;
        }
    }
}
