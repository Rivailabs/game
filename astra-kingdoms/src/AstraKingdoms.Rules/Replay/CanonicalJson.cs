using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AstraKingdoms.Rules.Replay
{
    public enum JsonKind : byte
    {
        Null = 0,
        Bool = 1,
        Number = 2,
        String = 3,
        Array = 4,
        Object = 5,
    }

    /// <summary>
    /// Minimal JSON tree for match records: objects keep member order, numbers are integers kept as
    /// their exact text (so 64-bit unsigned revisions round-trip), strings are ASCII. No floating
    /// point is ever parsed.
    /// </summary>
    public sealed class JsonNode
    {
        private readonly List<KeyValuePair<string, JsonNode>> _members;
        private readonly List<JsonNode> _items;

        public JsonKind Kind { get; }
        /// <summary>String value, or the integer's decimal text.</summary>
        public string Text { get; }
        public bool BoolValue { get; }

        private JsonNode(JsonKind kind, string text = null, bool boolValue = false)
        {
            Kind = kind;
            Text = text;
            BoolValue = boolValue;
            if (kind == JsonKind.Object) _members = new List<KeyValuePair<string, JsonNode>>();
            if (kind == JsonKind.Array) _items = new List<JsonNode>();
        }

        public static readonly JsonNode Null = new JsonNode(JsonKind.Null);
        public static JsonNode Object() => new JsonNode(JsonKind.Object);
        public static JsonNode Array() => new JsonNode(JsonKind.Array);
        public static JsonNode Of(string value) => value == null ? Null : new JsonNode(JsonKind.String, value);
        public static JsonNode Of(bool value) => new JsonNode(JsonKind.Bool, null, value);
        public static JsonNode Of(long value) => new JsonNode(JsonKind.Number, value.ToString(CultureInfo.InvariantCulture));
        public static JsonNode Of(ulong value) => new JsonNode(JsonKind.Number, value.ToString(CultureInfo.InvariantCulture));

        public IReadOnlyList<KeyValuePair<string, JsonNode>> Members => _members;
        public IReadOnlyList<JsonNode> Items => _items;

        public JsonNode Add(string key, JsonNode value)
        {
            if (Kind != JsonKind.Object) throw new InvalidOperationException("Not an object.");
            _members.Add(new KeyValuePair<string, JsonNode>(key, value ?? Null));
            return this;
        }

        public JsonNode Add(string key, string value) => Add(key, Of(value));
        public JsonNode Add(string key, long value) => Add(key, Of(value));
        public JsonNode Add(string key, ulong value) => Add(key, Of(value));
        public JsonNode Add(string key, bool value) => Add(key, Of(value));

        public JsonNode Push(JsonNode value)
        {
            if (Kind != JsonKind.Array) throw new InvalidOperationException("Not an array.");
            _items.Add(value ?? Null);
            return this;
        }

        /// <summary>Member by key; throws <see cref="FormatException"/> when missing.</summary>
        public JsonNode this[string key]
        {
            get
            {
                if (Kind != JsonKind.Object) throw new FormatException("Expected an object for key '" + key + "'.");
                foreach (var m in _members)
                    if (m.Key == key) return m.Value;
                throw new FormatException("Missing key '" + key + "'.");
            }
        }

        public long AsLong()
        {
            if (Kind != JsonKind.Number || !long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
                throw new FormatException("Expected a signed 64-bit integer.");
            return v;
        }

        public ulong AsULong()
        {
            if (Kind != JsonKind.Number || !ulong.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong v))
                throw new FormatException("Expected an unsigned 64-bit integer.");
            return v;
        }

        public int AsInt() => checked((int)AsLong());
        public uint AsUInt() => checked((uint)AsULong());

        public bool AsBool()
        {
            if (Kind != JsonKind.Bool) throw new FormatException("Expected a boolean.");
            return BoolValue;
        }

        public string AsString()
        {
            if (Kind == JsonKind.Null) return null;
            if (Kind != JsonKind.String) throw new FormatException("Expected a string.");
            return Text;
        }

        public IReadOnlyList<JsonNode> AsArray()
        {
            if (Kind != JsonKind.Array) throw new FormatException("Expected an array.");
            return _items;
        }

        // ---------------- Writer ----------------

        /// <summary>Canonical text: no whitespace, members in insertion order.</summary>
        public string ToCanonicalString()
        {
            var sb = new StringBuilder(4096);
            Write(sb);
            return sb.ToString();
        }

        private void Write(StringBuilder sb)
        {
            switch (Kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(BoolValue ? "true" : "false"); break;
                case JsonKind.Number: sb.Append(Text); break;
                case JsonKind.String: WriteString(sb, Text); break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _items[i].Write(sb);
                    }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < _members.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(sb, _members[i].Key);
                        sb.Append(':');
                        _members[i].Value.Write(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        // ---------------- Parser ----------------

        /// <summary>Parses the integer-only JSON subset. Throws <see cref="FormatException"/> on malformed text.</summary>
        public static JsonNode Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            int pos = 0;
            JsonNode node = ParseValue(text, ref pos, 0);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length) throw new FormatException("Trailing characters at " + pos + ".");
            return node;
        }

        private const int MaxDepth = 64;

        private static JsonNode ParseValue(string t, ref int pos, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("Nesting too deep.");
            SkipWhitespace(t, ref pos);
            if (pos >= t.Length) throw new FormatException("Unexpected end of input.");
            char c = t[pos];
            if (c == '{')
            {
                pos++;
                JsonNode obj = Object();
                SkipWhitespace(t, ref pos);
                if (Peek(t, pos) == '}') { pos++; return obj; }
                while (true)
                {
                    SkipWhitespace(t, ref pos);
                    string key = ParseString(t, ref pos);
                    SkipWhitespace(t, ref pos);
                    Expect(t, ref pos, ':');
                    obj.Add(key, ParseValue(t, ref pos, depth + 1));
                    SkipWhitespace(t, ref pos);
                    if (Peek(t, pos) == ',') { pos++; continue; }
                    Expect(t, ref pos, '}');
                    return obj;
                }
            }
            if (c == '[')
            {
                pos++;
                JsonNode arr = Array();
                SkipWhitespace(t, ref pos);
                if (Peek(t, pos) == ']') { pos++; return arr; }
                while (true)
                {
                    arr.Push(ParseValue(t, ref pos, depth + 1));
                    SkipWhitespace(t, ref pos);
                    if (Peek(t, pos) == ',') { pos++; continue; }
                    Expect(t, ref pos, ']');
                    return arr;
                }
            }
            if (c == '"') return Of(ParseString(t, ref pos));
            if (Match(t, ref pos, "null")) return Null;
            if (Match(t, ref pos, "true")) return Of(true);
            if (Match(t, ref pos, "false")) return Of(false);
            if (c == '-' || (c >= '0' && c <= '9'))
            {
                int start = pos;
                if (c == '-') pos++;
                int digits = 0;
                while (pos < t.Length && t[pos] >= '0' && t[pos] <= '9') { pos++; digits++; }
                if (digits == 0) throw new FormatException("Bad number at " + start + ".");
                if (pos < t.Length && (t[pos] == '.' || t[pos] == 'e' || t[pos] == 'E'))
                    throw new FormatException("Only integers are allowed (at " + start + ").");
                return new JsonNode(JsonKind.Number, t.Substring(start, pos - start));
            }
            throw new FormatException("Unexpected character '" + c + "' at " + pos + ".");
        }

        private static string ParseString(string t, ref int pos)
        {
            Expect(t, ref pos, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= t.Length) throw new FormatException("Unterminated string.");
                char c = t[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (pos >= t.Length) throw new FormatException("Bad escape.");
                char e = t[pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (pos + 4 > t.Length) throw new FormatException("Bad unicode escape.");
                        sb.Append((char)int.Parse(t.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default: throw new FormatException("Bad escape '\\" + e + "'.");
                }
            }
        }

        private static char Peek(string t, int pos) => pos < t.Length ? t[pos] : '\0';

        private static void Expect(string t, ref int pos, char c)
        {
            if (pos >= t.Length || t[pos] != c) throw new FormatException("Expected '" + c + "' at " + pos + ".");
            pos++;
        }

        private static bool Match(string t, ref int pos, string word)
        {
            if (string.CompareOrdinal(t, pos, word, 0, word.Length) != 0) return false;
            pos += word.Length;
            return true;
        }

        private static void SkipWhitespace(string t, ref int pos)
        {
            while (pos < t.Length && (t[pos] == ' ' || t[pos] == '\n' || t[pos] == '\r' || t[pos] == '\t')) pos++;
        }
    }
}
