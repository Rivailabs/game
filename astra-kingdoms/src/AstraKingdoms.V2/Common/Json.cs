using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.V2.Common
{
    /// <summary>Lenient accessors over the rules engine's integer-only JSON nodes.</summary>
    public static class Json
    {
        public static JsonNode Member(JsonNode obj, string key)
        {
            if (obj == null || obj.Kind != JsonKind.Object) return null;
            foreach (KeyValuePair<string, JsonNode> m in obj.Members)
                if (m.Key == key) return m.Value;
            return null;
        }

        public static string Str(JsonNode obj, string key, string fallback = null)
        {
            JsonNode n = Member(obj, key);
            return n != null && n.Kind == JsonKind.String ? n.Text : fallback;
        }

        public static long Long(JsonNode obj, string key, long fallback = 0)
        {
            JsonNode n = Member(obj, key);
            if (n == null || n.Kind != JsonKind.Number) return fallback;
            try { return n.AsLong(); }
            catch (FormatException) { return fallback; }
        }

        public static bool Bool(JsonNode obj, string key, bool fallback = false)
        {
            JsonNode n = Member(obj, key);
            return n != null && n.Kind == JsonKind.Bool ? n.BoolValue : fallback;
        }

        public static IReadOnlyList<JsonNode> Array(JsonNode obj, string key)
        {
            JsonNode n = Member(obj, key);
            return n != null && n.Kind == JsonKind.Array ? n.Items : (IReadOnlyList<JsonNode>)System.Array.Empty<JsonNode>();
        }

        public static IReadOnlyList<string> Strings(JsonNode obj, string key)
        {
            var list = new List<string>();
            foreach (JsonNode n in Array(obj, key))
                if (n.Kind == JsonKind.String) list.Add(n.Text);
            return list;
        }

        public static JsonNode StringArray(IEnumerable<string> values)
        {
            JsonNode a = JsonNode.Array();
            foreach (string v in values) a.Push(JsonNode.Of(v));
            return a;
        }
    }
}
