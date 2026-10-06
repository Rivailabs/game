using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>
/// <c>store-text-lint</c> (ticket 78; plan "Launch communication and growth" and "Product promise"):
/// scans store listing text for claims the V1 build cannot honour: four-player or kingdom/world
/// promises, V2+ features, real-money prizes, registration or stability claims, unsupported
/// languages or platforms, and counts above what V1 ships. It also enforces Play's field lengths
/// and refuses untranslated placeholders. Rules live in <c>release/store/store-lint-rules.json</c>
/// so the owner can extend them without code changes; a rule change is a reviewed change.
/// <para>Listing files are Markdown: every <c>## Section</c> is store text except sections whose title
/// starts with "Reviewer notes"; HTML comments are ignored.</para>
/// </summary>
public static class StoreTextLint
{
    public sealed class Rule
    {
        public string Id = "";
        public Regex Pattern;
        public string Message = "";
        public GateStatus Severity = GateStatus.Fail;
    }

    public sealed class Rules
    {
        public List<Rule> Forbidden = new();
        public Dictionary<string, int> MaxCounts = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> FieldLimits = new(StringComparer.OrdinalIgnoreCase);
        public List<string> SupportedLanguages = new();
        public List<string> KnownLanguages = new();
        public Regex Placeholder = new(@"\b(TODO|TBD|PLACEHOLDER|XXX)\b", RegexOptions.CultureInvariant);

        public static Rules Load(string path) => FromJson(Util.ReadJson(path));

        public static Rules FromJson(JsonNode root)
        {
            if (Util.Str(root, "format") != "AK-STORE-LINT-RULES/1") throw new FormatException("Not an AK-STORE-LINT-RULES/1 document.");
            var r = new Rules();
            foreach (JsonNode n in root["forbidden"]!.AsArray())
                r.Forbidden.Add(new Rule
                {
                    Id = Util.Str(n, "id"),
                    Pattern = new Regex(Util.Str(n, "pattern"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    Message = Util.Str(n, "message"),
                    Severity = Util.Str(n, "severity", "fail") == "warn" ? GateStatus.Warn : GateStatus.Fail,
                });
            foreach (KeyValuePair<string, JsonNode> kv in root["max_counts"]!.AsObject()) r.MaxCounts[kv.Key] = kv.Value!.GetValue<int>();
            foreach (KeyValuePair<string, JsonNode> kv in root["field_limits"]!.AsObject()) r.FieldLimits[kv.Key] = kv.Value!.GetValue<int>();
            r.SupportedLanguages.AddRange(root["supported_languages"]!.AsArray().Select(x => x!.GetValue<string>()));
            r.KnownLanguages.AddRange(root["known_languages"]!.AsArray().Select(x => x!.GetValue<string>()));
            return r;
        }
    }

    public sealed class Section
    {
        public string Title = "";
        public int FirstLine;
        public List<(int Line, string Text)> Lines = new();
        public string Text => string.Join("\n", Lines.Select(l => l.Text)).Trim();
    }

    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex CountClaim = new(@"\b(\d{1,4})\s+(?:[\p{L}-]+\s+){0,2}?(weapons?|cards?|terrains?|terrain types|players?|languages?|arenas?|outfits?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Splits a listing into its store-text sections (comments blanked, line numbers kept).</summary>
    public static List<Section> Parse(string text)
    {
        // Blank comments but keep their newlines so line numbers stay true.
        text = Comment.Replace(text, m => new string('\n', m.Value.Count(c => c == '\n')));
        var sections = new List<Section>();
        Section cur = null;
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.StartsWith("## ", StringComparison.Ordinal))
            {
                string title = l[3..].Trim();
                cur = title.StartsWith("Reviewer notes", StringComparison.OrdinalIgnoreCase) ? null : new Section { Title = title, FirstLine = i + 1 };
                if (cur != null) sections.Add(cur);
                continue;
            }
            if (l.StartsWith("# ", StringComparison.Ordinal)) continue;
            cur?.Lines.Add((i + 1, l));
        }
        return sections;
    }

    public static GateReport Lint(IEnumerable<(string Name, string Text)> files, Rules rules)
    {
        var r = new GateReport("store text lint");
        int scanned = 0;
        foreach ((string name, string text) in files)
        {
            List<Section> sections = Parse(text);
            if (sections.Count == 0)
            {
                r.Add("structure", GateStatus.Fail, name + ": no '## ' store-text sections found");
                continue;
            }
            foreach (Section s in sections)
            {
                scanned++;
                string key = s.Title.ToLowerInvariant();
                // A placeholder section is reported as INCOMPLETE below; its length is meaningless.
                if (rules.FieldLimits.TryGetValue(key, out int limit) && !rules.Placeholder.IsMatch(s.Text))
                {
                    int len = new StringInfo(s.Text).LengthInTextElements;
                    if (len == 0) r.Add("field." + key, GateStatus.Fail, name + ": '" + s.Title + "' is empty");
                    else if (len > limit) r.Add("field." + key, GateStatus.Fail, name + ": '" + s.Title + "' has " + len + " characters (Play limit " + limit + ")");
                }
                foreach ((int line, string t) in s.Lines)
                {
                    string where = name + ":" + line;
                    if (rules.Placeholder.IsMatch(t))
                        r.Add("placeholder", GateStatus.Incomplete, where + ": untranslated or unfinished text ('" + rules.Placeholder.Match(t).Value + "')");
                    foreach (Rule rule in rules.Forbidden)
                    {
                        Match m = rule.Pattern.Match(t);
                        if (m.Success) r.Add(rule.Id, rule.Severity, where + ": \"" + m.Value + "\" - " + rule.Message);
                    }
                    foreach (Match m in CountClaim.Matches(t))
                    {
                        string noun = Singular(m.Groups[2].Value.ToLowerInvariant());
                        if (rules.MaxCounts.TryGetValue(noun, out int max) && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > max)
                            r.Add("count." + noun, GateStatus.Fail, where + ": \"" + m.Value + "\" - V1 ships at most " + max + " " + noun + "(s)");
                    }
                    foreach (string lang in rules.KnownLanguages)
                    {
                        if (rules.SupportedLanguages.Contains(lang, StringComparer.OrdinalIgnoreCase)) continue;
                        if (Regex.IsMatch(t, @"\b" + Regex.Escape(lang) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                            r.Add("language", GateStatus.Fail, where + ": mentions " + lang + ", which this candidate does not support");
                    }
                }
            }
        }
        if (scanned > 0 && r.Findings.All(f => f.Status == GateStatus.Pass))
            r.Add("claims", GateStatus.Pass, scanned + " section(s) scanned; no forbidden claims");
        return r;
    }

    private static string Singular(string noun)
    {
        if (noun == "terrain types") return "terrain";
        return noun.EndsWith('s') ? noun[..^1] : noun;
    }

    public static int Cli(CliArgs a)
    {
        Rules rules = Rules.Load(a.Require("rules"));
        foreach (string lang in a.All("supported-language")) rules.SupportedLanguages.Add(lang);
        IReadOnlyList<string> files = a.All("file");
        if (files.Count == 0) throw new ArgumentException("--file is required (repeatable).");
        GateReport r = Lint(files.Select(f => (Path.GetFileName(f), File.ReadAllText(f))), rules);
        return GateJson.Emit(r, a.Get("out"));
    }
}
