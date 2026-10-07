using System;
using System.Collections.Generic;

namespace AstraKingdoms.Client.Localization
{
    /// <summary>How a proper name is carried into another language.</summary>
    public enum GlossaryTreatment : byte
    {
        /// <summary>Written in the target script with the same sound (Agni -> अग्नि).</summary>
        Transliterate = 0,
        /// <summary>Meaning translated (Stone Arrow -> पत्थर बाण).</summary>
        Translate = 1,
        /// <summary>Kept in Latin letters everywhere (brand names).</summary>
        Keep = 2,
    }

    /// <summary>One proper-name decision: the canonical form per language and how it was derived.</summary>
    public sealed class GlossaryEntry
    {
        public string Key { get; }
        public GlossaryTreatment Treatment { get; }
        private readonly Dictionary<string, string> _forms;
        public string Note { get; }

        public GlossaryEntry(string key, GlossaryTreatment treatment, Dictionary<string, string> forms, string note)
        {
            Key = key;
            Treatment = treatment;
            _forms = forms;
            Note = note ?? string.Empty;
        }

        public string Form(string language) => _forms.TryGetValue(language, out string f) ? f : null;
    }

    /// <summary>
    /// The proper-name glossary (plan: "Store proper-name spelling and transliteration decisions in a
    /// glossary"): Resources/Localization/glossary.txt, one entry per line,
    /// <c>key | treatment | en | hi | kn | note</c>. Every string table value for the key must start
    /// with the glossary form of its language, so a translator cannot silently respell a name. The
    /// draft forms still need fluent-speaker review (see the file header).
    /// </summary>
    public sealed class Glossary
    {
        private readonly List<GlossaryEntry> _entries = new List<GlossaryEntry>();
        private readonly List<string> _errors = new List<string>();

        public IReadOnlyList<GlossaryEntry> Entries => _entries;
        public IReadOnlyList<string> Errors => _errors;

        public static Glossary Parse(string text)
        {
            var g = new Glossary();
            if (string.IsNullOrEmpty(text)) return g;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (i == 0 && line.Length > 0 && line[0] == '﻿') line = line.Substring(1);
                if (line.Length == 0 || line[0] == '#') continue;
                string[] cols = line.Split('|');
                if (cols.Length != 6)
                {
                    g._errors.Add("line " + (i + 1) + ": expected 6 '|'-separated columns");
                    continue;
                }
                for (int c = 0; c < cols.Length; c++) cols[c] = cols[c].Trim();
                if (!Enum.TryParse(cols[1], true, out GlossaryTreatment treatment))
                {
                    g._errors.Add("line " + (i + 1) + ": unknown treatment '" + cols[1] + "'");
                    continue;
                }
                if (!seen.Add(cols[0]))
                {
                    g._errors.Add("line " + (i + 1) + ": duplicate key '" + cols[0] + "'");
                    continue;
                }
                var forms = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [Localizer.English] = cols[2],
                    [Localizer.Hindi] = cols[3],
                    [Localizer.Kannada] = cols[4],
                };
                if (treatment == GlossaryTreatment.Keep && (cols[3] != cols[2] || cols[4] != cols[2]))
                    g._errors.Add("line " + (i + 1) + ": a 'keep' entry must use the same form in every language");
                g._entries.Add(new GlossaryEntry(cols[0], treatment, forms, cols[5]));
            }
            return g;
        }

        /// <summary>Table values that do not start with their glossary form (empty when consistent).</summary>
        public IReadOnlyList<string> Inconsistencies(LocalizationTable table)
        {
            var problems = new List<string>();
            foreach (GlossaryEntry e in _entries)
            {
                string form = e.Form(table.LanguageCode);
                if (form == null) continue;
                if (!table.TryGet(e.Key, out string value))
                    problems.Add(table.LanguageCode + ":" + e.Key + " missing");
                else if (!value.StartsWith(form, StringComparison.Ordinal))
                    problems.Add(table.LanguageCode + ":" + e.Key + " is '" + value + "' but the glossary says '" + form + "'");
            }
            return problems;
        }
    }
}
