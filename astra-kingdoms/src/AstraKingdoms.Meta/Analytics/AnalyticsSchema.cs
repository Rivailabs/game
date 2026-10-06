using System;
using System.Collections.Generic;
using System.Linq;

namespace AstraKingdoms.Meta.Analytics
{
    /// <summary>
    /// The plan's minimal event set (plan: "Measurement and stage decisions") plus the crash signal
    /// that the crash-free-session metric needs. Nothing else is collected.
    /// </summary>
    public enum AnalyticsEventType : byte
    {
        ValidSession = 0,
        TutorialCompletion = 1,
        MatchStart = 2,
        Lock = 3,
        Resolution = 4,
        LandCutCompletion = 5,
        MatchEnd = 6,
        ReconnectOutcome = 7,
        PurchaseState = 8,
        AdRewardState = 9,
        Crash = 10,
    }

    /// <summary>How a session was produced; anything but a real person is excluded from people metrics.</summary>
    [Flags]
    public enum TrafficFlags : byte
    {
        None = 0,
        /// <summary>The acting seat was a bot (never counts as a retained or paying user).</summary>
        Bot = 1,
        /// <summary>Automation interface / simulator / scripted client.</summary>
        Automation = 2,
        /// <summary>Internal development session or employee/contributor account.</summary>
        Internal = 4,
        /// <summary>Automated test client (CI, device lab smoke).</summary>
        TestClient = 8,
    }

    /// <summary>Recruitment source, kept separate in every comparison (plan: organic vs paid vs friends/colleagues).</summary>
    public enum CohortSource : byte
    {
        Organic = 0,
        Paid = 1,
        FriendsAndColleagues = 2,
        RecruitedTester = 3,
    }

    public enum PropertyType : byte
    {
        Token = 0,
        Integer = 1,
        Boolean = 2,
    }

    public sealed class PropertySpec
    {
        public string Name { get; }
        public PropertyType Type { get; }
        public bool Required { get; }
        /// <summary>Allowed values for an enumerated token; null = any well-formed token.</summary>
        public IReadOnlyList<string> Allowed { get; }

        public PropertySpec(string name, PropertyType type, bool required, params string[] allowed)
        {
            Name = name;
            Type = type;
            Required = required;
            Allowed = allowed != null && allowed.Length > 0 ? allowed : null;
        }
    }

    public sealed class EventSpec
    {
        public AnalyticsEventType Type { get; }
        public string Name { get; }
        public IReadOnlyList<PropertySpec> Properties { get; }
        /// <summary>Which consent this event needs.</summary>
        public ConsentPurpose Purpose { get; }

        public EventSpec(AnalyticsEventType type, string name, ConsentPurpose purpose, params PropertySpec[] properties)
        {
            Type = type;
            Name = name;
            Purpose = purpose;
            Properties = properties;
        }
    }

    /// <summary>
    /// Versioned schema. Values are short tokens or integers only: no free text, so no names, chat,
    /// e-mail addresses or aim inputs can enter analytics by accident.
    /// </summary>
    public static class AnalyticsSchema
    {
        public const int Version = 1;
        public const int MaxTokenLength = 64;

        public static readonly string[] Modes = { "online_human", "online_bot", "shared_phone", "practice" };
        public static readonly string[] EndReasons = { "early_victory", "rounds_complete", "voluntary_forfeit", "timeout_forfeit", "technical_abort" };
        public static readonly string[] Results = { "win", "loss", "draw", "unattributed" };
        public static readonly string[] AdStates = { "opportunity", "opted_in", "filled", "impression", "declined", "failed", "reward_verified", "reward_rejected" };
        public static readonly string[] PurchaseStates = { "started", "granted", "already_owned", "pending", "retry_later", "canceled", "revoked", "invalid_token", "unknown_product", "account_mismatch", "test_purchase_not_accepted", "configuration_error", "failed" };

        private static readonly EventSpec[] Specs =
        {
            new EventSpec(AnalyticsEventType.ValidSession, "valid_session", ConsentPurpose.ProductAnalytics,
                new PropertySpec("foreground", PropertyType.Boolean, true),
                new PropertySpec("app_version", PropertyType.Token, true),
                new PropertySpec("build", PropertyType.Token, true, "release", "development")),
            new EventSpec(AnalyticsEventType.TutorialCompletion, "tutorial_completion", ConsentPurpose.ProductAnalytics,
                new PropertySpec("tutorial_version", PropertyType.Integer, true),
                new PropertySpec("steps", PropertyType.Integer, true)),
            new EventSpec(AnalyticsEventType.MatchStart, "match_start", ConsentPurpose.ProductAnalytics,
                new PropertySpec("match_id", PropertyType.Token, true),
                new PropertySpec("mode", PropertyType.Token, true, Modes),
                new PropertySpec("catalog", PropertyType.Token, true, "starter", "full")),
            new EventSpec(AnalyticsEventType.Lock, "lock", ConsentPurpose.ProductAnalytics,
                new PropertySpec("match_id", PropertyType.Token, true),
                new PropertySpec("round", PropertyType.Integer, true),
                new PropertySpec("volley", PropertyType.Integer, true),
                new PropertySpec("timed_out", PropertyType.Boolean, true)),
            new EventSpec(AnalyticsEventType.Resolution, "resolution", ConsentPurpose.ProductAnalytics,
                new PropertySpec("match_id", PropertyType.Token, true),
                new PropertySpec("round", PropertyType.Integer, true),
                new PropertySpec("volley", PropertyType.Integer, true)),
            new EventSpec(AnalyticsEventType.LandCutCompletion, "land_cut_completion", ConsentPurpose.ProductAnalytics,
                new PropertySpec("match_id", PropertyType.Token, true),
                new PropertySpec("round", PropertyType.Integer, true),
                new PropertySpec("cells", PropertyType.Integer, true),
                new PropertySpec("method", PropertyType.Token, true, "drawn", "auto", "timeout")),
            new EventSpec(AnalyticsEventType.MatchEnd, "match_end", ConsentPurpose.ProductAnalytics,
                new PropertySpec("match_id", PropertyType.Token, true),
                new PropertySpec("mode", PropertyType.Token, true, Modes),
                new PropertySpec("reason", PropertyType.Token, true, EndReasons),
                new PropertySpec("result", PropertyType.Token, true, Results),
                new PropertySpec("rounds", PropertyType.Integer, true)),
            new EventSpec(AnalyticsEventType.ReconnectOutcome, "reconnect_outcome", ConsentPurpose.ProductAnalytics,
                new PropertySpec("match_id", PropertyType.Token, true),
                new PropertySpec("outcome", PropertyType.Token, true, "restored", "failed", "abandoned"),
                new PropertySpec("downtime_ms", PropertyType.Integer, false)),
            new EventSpec(AnalyticsEventType.PurchaseState, "purchase_state", ConsentPurpose.ProductAnalytics,
                new PropertySpec("sku", PropertyType.Token, true),
                new PropertySpec("state", PropertyType.Token, true, PurchaseStates),
                new PropertySpec("test_purchase", PropertyType.Boolean, false)),
            new EventSpec(AnalyticsEventType.AdRewardState, "ad_reward_state", ConsentPurpose.ProductAnalytics,
                new PropertySpec("placement", PropertyType.Token, true),
                new PropertySpec("state", PropertyType.Token, true, AdStates)),
            new EventSpec(AnalyticsEventType.Crash, "crash", ConsentPurpose.CrashReports,
                new PropertySpec("critical", PropertyType.Boolean, true),
                new PropertySpec("stage", PropertyType.Token, true, "startup", "menu", "match", "background")),
        };

        public static IReadOnlyList<EventSpec> All => Specs;

        public static EventSpec For(AnalyticsEventType type) => Specs.First(s => s.Type == type);

        public static bool IsWellFormedToken(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > MaxTokenLength) return false;
            foreach (char c in s)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.' || c == ':';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Schema errors for an event (empty = valid).</summary>
        public static IReadOnlyList<string> Validate(AnalyticsEvent e)
        {
            var errors = new List<string>();
            if (e == null) return new[] { "null event" };
            EventSpec spec = For(e.Type);
            if (e.SchemaVersion != Version) errors.Add("schema version " + e.SchemaVersion);
            if (!IsWellFormedToken(e.EventId)) errors.Add("event_id");
            if (!IsWellFormedToken(e.AnalyticsId)) errors.Add("analytics_id");
            if (!IsWellFormedToken(e.SessionId)) errors.Add("session_id");
            foreach (KeyValuePair<string, object> p in e.Properties)
            {
                PropertySpec ps = spec.Properties.FirstOrDefault(x => x.Name == p.Key);
                if (ps == null)
                {
                    errors.Add(spec.Name + ": unknown property " + p.Key);
                    continue;
                }
                switch (ps.Type)
                {
                    case PropertyType.Boolean:
                        if (!(p.Value is bool)) errors.Add(p.Key + ": expected boolean");
                        break;
                    case PropertyType.Integer:
                        if (!(p.Value is long || p.Value is int)) errors.Add(p.Key + ": expected integer");
                        break;
                    default:
                        if (!(p.Value is string s) || !IsWellFormedToken(s)) errors.Add(p.Key + ": expected a short token");
                        else if (ps.Allowed != null && !ps.Allowed.Contains(s)) errors.Add(p.Key + ": value '" + s + "' not allowed");
                        break;
                }
            }
            foreach (PropertySpec ps in spec.Properties.Where(x => x.Required))
                if (!e.Properties.ContainsKey(ps.Name)) errors.Add(spec.Name + ": missing " + ps.Name);
            return errors;
        }
    }

    /// <summary>One analytics event (envelope + typed properties).</summary>
    public sealed class AnalyticsEvent
    {
        /// <summary>Random id; ingestion de-duplicates on it (clients retry batches).</summary>
        public string EventId { get; }
        public AnalyticsEventType Type { get; }
        public int SchemaVersion { get; }
        /// <summary>Pseudonymous analytics id (random per install, reset on request); never the account id.</summary>
        public string AnalyticsId { get; }
        public string SessionId { get; }
        /// <summary>When it happened (UTC), as stored consistently by ingestion.</summary>
        public DateTimeOffset OccurredAt { get; }
        public TrafficFlags Flags { get; }
        public CohortSource Cohort { get; }
        public IReadOnlyDictionary<string, object> Properties { get; }

        public AnalyticsEvent(string eventId, AnalyticsEventType type, string analyticsId, string sessionId, DateTimeOffset occurredAt,
            IReadOnlyDictionary<string, object> properties, TrafficFlags flags = TrafficFlags.None, CohortSource cohort = CohortSource.Organic,
            int schemaVersion = AnalyticsSchema.Version)
        {
            EventId = eventId;
            Type = type;
            AnalyticsId = analyticsId;
            SessionId = sessionId;
            OccurredAt = occurredAt;
            Properties = properties ?? new Dictionary<string, object>();
            Flags = flags;
            Cohort = cohort;
            SchemaVersion = schemaVersion;
        }

        public bool IsRealPerson => Flags == TrafficFlags.None;

        public string Token(string name) => Properties.TryGetValue(name, out object v) ? v as string : null;
        public long? Integer(string name) => Properties.TryGetValue(name, out object v) ? v is long l ? l : v is int i ? i : (long?)null : null;
        public bool? Boolean(string name) => Properties.TryGetValue(name, out object v) && v is bool b ? b : (bool?)null;
    }
}
