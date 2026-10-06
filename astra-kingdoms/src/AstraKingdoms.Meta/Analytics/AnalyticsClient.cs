using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Common;

namespace AstraKingdoms.Meta.Analytics
{
    public enum ConsentPurpose : byte
    {
        /// <summary>Optional product analytics (the decision metrics).</summary>
        ProductAnalytics = 0,
        /// <summary>Crash and stability diagnostics.</summary>
        CrashReports = 1,
    }

    public enum ConsentChoice : byte
    {
        NotAsked = 0,
        Granted = 1,
        Denied = 2,
    }

    /// <summary>The player's choices, both off until asked (privacy by default).</summary>
    public sealed class AnalyticsConsent
    {
        public ConsentChoice ProductAnalytics { get; }
        public ConsentChoice CrashReports { get; }

        public AnalyticsConsent(ConsentChoice productAnalytics, ConsentChoice crashReports)
        {
            ProductAnalytics = productAnalytics;
            CrashReports = crashReports;
        }

        public static readonly AnalyticsConsent NotAsked = new AnalyticsConsent(ConsentChoice.NotAsked, ConsentChoice.NotAsked);

        public ConsentChoice For(ConsentPurpose purpose) => purpose == ConsentPurpose.CrashReports ? CrashReports : ProductAnalytics;
    }

    /// <summary>
    /// Owner decisions about measuring children (plan: child measurement requires its own applicable-law
    /// assessment; consent does not automatically permit behavioural tracking). Both default to off.
    /// </summary>
    public sealed class CollectionPolicy
    {
        /// <summary>Product analytics for children/unknown age, only after the assessment approves it.</summary>
        public bool ChildProductAnalyticsApproved { get; set; }
        /// <summary>Crash diagnostics (no identifiers beyond random ids) for children/unknown age.</summary>
        public bool ChildCrashReportsApproved { get; set; }
    }

    public static class ConsentGate
    {
        /// <summary>True when this event may be collected for this player now.</summary>
        public static bool Allows(AnalyticsEventType type, AnalyticsConsent consent, AudienceProfile audience, CollectionPolicy policy)
        {
            policy = policy ?? new CollectionPolicy();
            ConsentPurpose purpose = AnalyticsSchema.For(type).Purpose;
            if ((consent ?? AnalyticsConsent.NotAsked).For(purpose) != ConsentChoice.Granted) return false;
            bool childSafe = audience == null || audience.NeedsChildSafeTreatment;
            if (!childSafe) return true;
            return purpose == ConsentPurpose.CrashReports ? policy.ChildCrashReportsApproved : policy.ChildProductAnalyticsApproved;
        }
    }

    /// <summary>Transport for event batches (HTTPS to the collection endpoint, or a test list).</summary>
    public interface IAnalyticsSink
    {
        /// <summary>Returns true when the batch was accepted; false keeps it queued for a retry.</summary>
        bool Send(IReadOnlyList<AnalyticsEvent> batch);
    }

    public sealed class ListAnalyticsSink : IAnalyticsSink
    {
        public readonly List<AnalyticsEvent> Received = new List<AnalyticsEvent>();
        public bool Accept { get; set; } = true;

        public bool Send(IReadOnlyList<AnalyticsEvent> batch)
        {
            if (!Accept) return false;
            Received.AddRange(batch);
            return true;
        }
    }

    /// <summary>Crash-reporting SDK boundary (the client adapter forwards to the chosen crash reporter only when allowed).</summary>
    public interface ICrashReporter
    {
        void SetCollectionEnabled(bool enabled);
        void RecordCrash(bool critical, string stage);
    }

    /// <summary>
    /// Client analytics (ticket 64): schema-checked, consent-gated, flagged for bots/automation/internal
    /// sessions, batched, and cleared immediately when consent is withdrawn.
    /// </summary>
    public sealed class AnalyticsClient
    {
        public const int MaxQueue = 500;
        public const int BatchSize = 50;

        private readonly object _gate = new object();
        private readonly List<AnalyticsEvent> _queue = new List<AnalyticsEvent>();
        private readonly IAnalyticsSink _sink;
        private readonly IClock _clock;
        private readonly Func<AnalyticsConsent> _consent;
        private readonly Func<AudienceProfile> _audience;
        private readonly CollectionPolicy _policy;

        /// <summary>Pseudonymous install-scoped id; <see cref="ResetAnalyticsId"/> replaces it.</summary>
        public string AnalyticsId { get; private set; }
        public string SessionId { get; private set; }
        public TrafficFlags SessionFlags { get; set; }
        public CohortSource Cohort { get; set; }
        public int Dropped { get; private set; }
        public IReadOnlyList<string> LastErrors { get; private set; } = Array.Empty<string>();
        public ICrashReporter CrashReporter { get; set; }

        public AnalyticsClient(IAnalyticsSink sink, IClock clock, Func<AnalyticsConsent> consent, Func<AudienceProfile> audience,
            string analyticsId = null, CollectionPolicy policy = null)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _consent = consent ?? (() => AnalyticsConsent.NotAsked);
            _audience = audience ?? (() => AudienceProfile.Unknown);
            _policy = policy ?? new CollectionPolicy();
            AnalyticsId = string.IsNullOrEmpty(analyticsId) ? NewId() : analyticsId;
            SessionId = NewId();
        }

        public int Queued
        {
            get { lock (_gate) return _queue.Count; }
        }

        public static string NewId() => Guid.NewGuid().ToString("N");

        public void StartSession() => SessionId = NewId();

        /// <summary>Replaces the analytics id and drops anything queued (player-requested reset or deletion).</summary>
        public void ResetAnalyticsId()
        {
            lock (_gate) _queue.Clear();
            AnalyticsId = NewId();
        }

        /// <summary>Re-applies consent: queued events no longer allowed are discarded and the crash reporter is toggled.</summary>
        public void OnConsentChanged()
        {
            AnalyticsConsent consent = _consent();
            AudienceProfile audience = _audience();
            lock (_gate) _queue.RemoveAll(e => !ConsentGate.Allows(e.Type, consent, audience, _policy));
            CrashReporter?.SetCollectionEnabled(ConsentGate.Allows(AnalyticsEventType.Crash, consent, audience, _policy));
        }

        /// <summary>Queues an event if consent and schema allow it. Returns false when dropped.</summary>
        public bool Track(AnalyticsEventType type, IReadOnlyDictionary<string, object> properties, TrafficFlags extraFlags = TrafficFlags.None)
        {
            if (!ConsentGate.Allows(type, _consent(), _audience(), _policy)) return false;
            var e = new AnalyticsEvent(NewId(), type, AnalyticsId, SessionId, _clock.UtcNow, properties, SessionFlags | extraFlags, Cohort);
            IReadOnlyList<string> errors = AnalyticsSchema.Validate(e);
            if (errors.Count > 0)
            {
                LastErrors = errors;
                Dropped++;
                return false;
            }
            lock (_gate)
            {
                _queue.Add(e);
                if (_queue.Count > MaxQueue)
                {
                    _queue.RemoveAt(0);
                    Dropped++;
                }
            }
            if (type == AnalyticsEventType.Crash) CrashReporter?.RecordCrash(e.Boolean("critical") ?? false, e.Token("stage"));
            return true;
        }

        /// <summary>Sends queued events in batches; returns the number delivered.</summary>
        public int Flush()
        {
            int sent = 0;
            while (true)
            {
                List<AnalyticsEvent> batch;
                lock (_gate) batch = _queue.Take(BatchSize).ToList();
                if (batch.Count == 0) return sent;
                if (!_sink.Send(batch)) return sent;
                lock (_gate)
                {
                    HashSet<string> ids = new HashSet<string>(batch.Select(b => b.EventId));
                    _queue.RemoveAll(q => ids.Contains(q.EventId));
                }
                sent += batch.Count;
            }
        }

        // Convenience builders for the event set -------------------------------------------------

        public static Dictionary<string, object> Props(params object[] keyValues)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < keyValues.Length; i += 2) d[(string)keyValues[i]] = keyValues[i + 1];
            return d;
        }
    }
}
