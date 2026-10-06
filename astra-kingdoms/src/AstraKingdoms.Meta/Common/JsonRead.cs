using System.Globalization;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Meta.Common
{
    /// <summary>
    /// Lenient readers over the rules assembly's integer-only JSON tree, for external payloads where
    /// fields may be missing and 64-bit numbers arrive as strings (Google APIs).
    /// </summary>
    public static class JsonRead
    {
        public static JsonNode Member(JsonNode obj, string key)
        {
            if (obj == null || obj.Kind != JsonKind.Object) return null;
            foreach (var m in obj.Members)
                if (m.Key == key) return m.Value;
            return null;
        }

        public static string String(JsonNode obj, string key)
        {
            JsonNode n = Member(obj, key);
            if (n == null || n.Kind == JsonKind.Null) return null;
            return n.Kind == JsonKind.String || n.Kind == JsonKind.Number ? n.Text : null;
        }

        /// <summary>A number or a numeric string; null when missing or malformed.</summary>
        public static long? Long(JsonNode obj, string key)
        {
            string s = String(obj, key);
            return s != null && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v) ? v : (long?)null;
        }

        public static int? Int(JsonNode obj, string key)
        {
            long? v = Long(obj, key);
            return v.HasValue && v.Value >= int.MinValue && v.Value <= int.MaxValue ? (int)v.Value : (int?)null;
        }
    }
}
