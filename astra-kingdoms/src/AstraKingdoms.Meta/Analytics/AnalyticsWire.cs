using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Meta.Analytics
{
    /// <summary>
    /// The upload format of an analytics batch (ticket 64), shared by the client sink and the
    /// server's ingestion endpoint (<c>POST /v1/analytics/batch</c>). Integer-only JSON:
    /// <code>
    /// { "v": 1,
    ///   "consent": { "product_analytics": "granted|denied|not_asked", "crash_reports": "..." },
    ///   "events": [ { "event_id": "...", "type": "match_end", "schema_version": 1, "analytics_id": "...",
    ///                 "session_id": "...", "occurred_at_ms": 1791277200000, "flags": 0, "cohort": 0,
    ///                 "properties": { "match_id": "...", "rounds": 8, "test_purchase": false } } ] }
    /// </code>
    /// The consent block is the client's current choice; the server re-applies the consent gate (with
    /// the account's server-side audience) and drops anything the choice does not allow.
    /// </summary>
    public static class AnalyticsWire
    {
        public const int Version = 1;

        public static string ConsentToken(ConsentChoice c) => c == ConsentChoice.Granted ? "granted" : c == ConsentChoice.Denied ? "denied" : "not_asked";

        public static ConsentChoice ParseConsentToken(string s) =>
            s == "granted" ? ConsentChoice.Granted : s == "denied" ? ConsentChoice.Denied : ConsentChoice.NotAsked;

        public static JsonNode Batch(IReadOnlyList<AnalyticsEvent> events, AnalyticsConsent consent)
        {
            consent = consent ?? AnalyticsConsent.NotAsked;
            JsonNode list = JsonNode.Array();
            foreach (AnalyticsEvent e in events ?? Array.Empty<AnalyticsEvent>()) list.Push(Event(e));
            return JsonNode.Object().Add("v", Version)
                .Add("consent", JsonNode.Object().Add("product_analytics", ConsentToken(consent.ProductAnalytics))
                    .Add("crash_reports", ConsentToken(consent.CrashReports)))
                .Add("events", list);
        }

        public static JsonNode Event(AnalyticsEvent e)
        {
            JsonNode props = JsonNode.Object();
            foreach (KeyValuePair<string, object> p in e.Properties.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                switch (p.Value)
                {
                    case bool b: props.Add(p.Key, b); break;
                    case long l: props.Add(p.Key, l); break;
                    case int i: props.Add(p.Key, (long)i); break;
                    default: props.Add(p.Key, p.Value as string); break;
                }
            }
            return JsonNode.Object().Add("event_id", e.EventId).Add("type", AnalyticsSchema.For(e.Type).Name).Add("schema_version", e.SchemaVersion)
                .Add("analytics_id", e.AnalyticsId).Add("session_id", e.SessionId).Add("occurred_at_ms", e.OccurredAt.ToUnixTimeMilliseconds())
                .Add("flags", (long)e.Flags).Add("cohort", (long)e.Cohort).Add("properties", props);
        }

        public static AnalyticsConsent ParseConsent(JsonNode batch)
        {
            JsonNode c = Common.JsonRead.Member(batch, "consent");
            return new AnalyticsConsent(ParseConsentToken(Common.JsonRead.String(c, "product_analytics")),
                ParseConsentToken(Common.JsonRead.String(c, "crash_reports")));
        }

        /// <summary>Parses one event; null with a reason when it is not even well-formed (schema validation is separate).</summary>
        public static AnalyticsEvent ParseEvent(JsonNode n, out string error)
        {
            error = null;
            if (n == null || n.Kind != JsonKind.Object)
            {
                error = "not an object";
                return null;
            }
            string typeName = Common.JsonRead.String(n, "type");
            EventSpec spec = AnalyticsSchema.All.FirstOrDefault(s => s.Name == typeName);
            if (spec == null)
            {
                error = "unknown type";
                return null;
            }
            long? at = Common.JsonRead.Long(n, "occurred_at_ms");
            if (!at.HasValue || at.Value < 0 || at.Value > 253402300799999L)
            {
                error = "occurred_at_ms";
                return null;
            }
            var props = new Dictionary<string, object>(StringComparer.Ordinal);
            JsonNode pn = Common.JsonRead.Member(n, "properties");
            if (pn != null && pn.Kind == JsonKind.Object)
            {
                foreach (KeyValuePair<string, JsonNode> m in pn.Members)
                {
                    switch (m.Value.Kind)
                    {
                        case JsonKind.Bool: props[m.Key] = m.Value.BoolValue; break;
                        case JsonKind.Number: props[m.Key] = m.Value.AsLong(); break;
                        case JsonKind.String: props[m.Key] = m.Value.AsString(); break;
                        default:
                            error = "property " + m.Key;
                            return null;
                    }
                }
            }
            else if (pn != null && pn.Kind != JsonKind.Null)
            {
                error = "properties";
                return null;
            }
            long flags = Common.JsonRead.Long(n, "flags") ?? 0;
            long cohort = Common.JsonRead.Long(n, "cohort") ?? 0;
            if (flags < 0 || flags > 15 || cohort < 0 || cohort > 3)
            {
                error = "flags or cohort";
                return null;
            }
            return new AnalyticsEvent(Common.JsonRead.String(n, "event_id"), spec.Type, Common.JsonRead.String(n, "analytics_id"),
                Common.JsonRead.String(n, "session_id"), DateTimeOffset.FromUnixTimeMilliseconds(at.Value), props, (TrafficFlags)flags,
                (CohortSource)cohort, Common.JsonRead.Int(n, "schema_version") ?? -1);
        }
    }
}
