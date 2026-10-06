using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Server.Identity;
using Microsoft.Extensions.Options;
using RulesJson = AstraKingdoms.Rules.Replay.JsonNode;

namespace AstraKingdoms.Server.MetaHost;

public sealed record RejectedEvent(string EventId, string Reason);

public sealed class IngestionResult
{
    public int Accepted { get; init; }
    public int Duplicates { get; init; }
    public IReadOnlyList<RejectedEvent> Rejected { get; init; } = Array.Empty<RejectedEvent>();
    public string Error { get; init; }
    public int ErrorStatus { get; init; } = StatusCodes.Status400BadRequest;
}

/// <summary>
/// Analytics batch ingestion (ticket 64). The batch carries the client's consent choice
/// (<see cref="AnalyticsWire"/>); the server re-applies <see cref="ConsentGate"/> with the
/// account's <b>server-side</b> audience (unknown age is treated as a child) and the owner's
/// <see cref="CollectionPolicy"/>, validates every event against the fixed schema (short tokens,
/// integers and booleans only, so no free text can enter), de-duplicates on <c>event_id</c> (clients
/// retry batches), flags Development-mode identities as internal, and links the account to the
/// analytics ids it used so account deletion can erase them. Raw events are swept after 90 days.
/// </summary>
public sealed class AnalyticsIngestion
{
    private readonly MetaServices _meta;
    private readonly TimeProvider _time;
    private readonly MetaAnalyticsOptions _options;

    public AnalyticsIngestion(MetaServices meta, TimeProvider time, IOptions<ServerOptions> options)
    {
        _meta = meta;
        _time = time;
        _options = options.Value.Meta.Analytics;
    }

    public IngestionResult Ingest(VerifiedIdentity id, RulesJson batch)
    {
        if (batch == null || batch.Kind != AstraKingdoms.Rules.Replay.JsonKind.Object) return new IngestionResult { Error = "not an object" };
        if (JsonRead.Int(batch, "v") != AnalyticsWire.Version) return new IngestionResult { Error = "unsupported batch version" };
        RulesJson events = JsonRead.Member(batch, "events");
        if (events == null || events.Kind != AstraKingdoms.Rules.Replay.JsonKind.Array) return new IngestionResult { Error = "events must be an array" };
        if (events.Items.Count > _options.MaxBatch)
            return new IngestionResult { Error = "at most " + _options.MaxBatch + " events per batch", ErrorStatus = StatusCodes.Status413PayloadTooLarge };

        AnalyticsConsent consent = AnalyticsWire.ParseConsent(batch);
        AudienceProfile audience = _meta.Audience(id.Uid);
        DateTimeOffset now = _time.GetUtcNow();
        bool internalIdentity = id.Issuer == "dev";
        var rejected = new List<RejectedEvent>();
        var accepted = new List<StoredAnalyticsEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RulesJson node in events.Items)
        {
            AnalyticsEvent e = AnalyticsWire.ParseEvent(node, out string parseError);
            string eventId = JsonRead.String(node, "event_id");
            if (e == null)
            {
                rejected.Add(new RejectedEvent(eventId, parseError));
                continue;
            }
            if (!ConsentGate.Allows(e.Type, consent, audience, _meta.Collection))
            {
                rejected.Add(new RejectedEvent(eventId, "consent"));
                continue;
            }
            IReadOnlyList<string> errors = AnalyticsSchema.Validate(e);
            if (errors.Count > 0)
            {
                rejected.Add(new RejectedEvent(eventId, "schema: " + errors[0]));
                continue;
            }
            if (e.OccurredAt > now.AddMinutes(5) || now - e.OccurredAt > TimeSpan.FromDays(_options.MaxEventAgeDays))
            {
                rejected.Add(new RejectedEvent(eventId, "occurred_at out of range"));
                continue;
            }
            if (!seen.Add(e.EventId)) continue; // repeated inside one batch
            TrafficFlags flags = e.Flags | (internalIdentity ? TrafficFlags.Internal : TrafficFlags.None);
            accepted.Add(new StoredAnalyticsEvent(e.EventId, (int)e.Type, e.SchemaVersion, e.AnalyticsId, e.SessionId, e.OccurredAt, (int)flags,
                (int)e.Cohort, JsonRead.Member(AnalyticsWire.Event(e), "properties").ToCanonicalString()));
        }
        (int inserted, int duplicates) = accepted.Count == 0 ? (0, 0) : _meta.Store.InsertAnalytics(id.Uid, accepted, now);
        return new IngestionResult { Accepted = inserted, Duplicates = duplicates, Rejected = rejected };
    }
}
