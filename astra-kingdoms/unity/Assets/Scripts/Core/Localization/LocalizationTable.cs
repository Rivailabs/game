using System;
using System.Collections.Generic;
using System.Text;

namespace AstraKingdoms.Client.Localization
{
    /// <summary>
    /// One language's strings, parsed from a plain "key = value" text file
    /// (Assets/Resources/Localization/&lt;code&gt;.txt). Lines starting with '#' are comments.
    /// Values use positional parameters {0}, {1}... and the escape \n for a line break.
    /// A key that appears twice is a file error (reported in <see cref="Errors"/>), never a silent override.
    /// </summary>
    public sealed class LocalizationTable
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _errors = new List<string>();
        private readonly List<string> _keysInOrder = new List<string>();

        public string LanguageCode { get; }
        /// <summary>Header comment lines (for example a "needs fluent-speaker review" status line).</summary>
        public IReadOnlyList<string> HeaderComments { get; }
        public IReadOnlyList<string> Errors => _errors;
        public IReadOnlyList<string> Keys => _keysInOrder;
        public int Count => _values.Count;

        private LocalizationTable(string languageCode, List<string> header)
        {
            LanguageCode = languageCode;
            HeaderComments = header;
        }

        public static LocalizationTable Parse(string languageCode, string text)
        {
            var header = new List<string>();
            var table = new LocalizationTable(languageCode, header);
            if (string.IsNullOrEmpty(text)) return table;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inHeader = true;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (i == 0 && line.Length > 0 && line[0] == '﻿') line = line.Substring(1);
                if (line.Length == 0) continue;
                if (line[0] == '#')
                {
                    if (inHeader) header.Add(line.Substring(1).Trim());
                    continue;
                }
                inHeader = false;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    table._errors.Add("line " + (i + 1) + ": expected 'key = value'");
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                string value = Unescape(line.Substring(eq + 1).Trim());
                if (table._values.ContainsKey(key))
                {
                    table._errors.Add("line " + (i + 1) + ": duplicate key '" + key + "'");
                    continue;
                }
                table._values.Add(key, value);
                table._keysInOrder.Add(key);
            }
            return table;
        }

        public bool TryGet(string key, out string value) => _values.TryGetValue(key, out value);

        public bool Contains(string key) => _values.ContainsKey(key);

        /// <summary>Number of distinct {n} parameters used by a value (highest index + 1).</summary>
        public static int ParameterCount(string value)
        {
            int max = -1;
            if (value == null) return 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '{') continue;
                int j = i + 1;
                int n = 0;
                bool digits = false;
                while (j < value.Length && value[j] >= '0' && value[j] <= '9')
                {
                    n = n * 10 + (value[j] - '0');
                    digits = true;
                    j++;
                }
                if (digits && j < value.Length && value[j] == '}' && n > max) max = n;
            }
            return max + 1;
        }

        private static string Unescape(string raw)
        {
            if (raw.IndexOf('\\') < 0) return raw;
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char n = raw[i + 1];
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
