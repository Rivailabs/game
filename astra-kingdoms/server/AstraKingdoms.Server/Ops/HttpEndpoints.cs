using System.Security.Cryptography;
using System.Text.Json;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Server.Identity;
using AstraKingdoms.Server.Lobby;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Realtime;
using AstraKingdoms.Server.Security;
using AstraKingdoms.Server.Storage;
using Microsoft.Extensions.Options;

namespace AstraKingdoms.Server.Ops;

/// <summary>Rate limiters for HTTP: per identity when authenticated, per IP for anonymous intake.</summary>
public sealed class HttpLimiters
{
    public HttpLimiters(TimeProvider time, IOptions<ServerOptions> options)
    {
        RateLimitOptions o = options.Value.RateLimits;
        PerIdentity = new TokenBucketLimiter(time, o.Capacity, o.RefillPerSecond);
        Anonymous = new TokenBucketLimiter(time, o.AnonymousCapacity, o.AnonymousRefillPerMinute / 60.0);
    }

    public TokenBucketLimiter PerIdentity { get; }
    public TokenBucketLimiter Anonymous { get; }
}

/// <summary>Health, readiness, participant-only match reads and the grievance intake.</summary>
public static class HttpEndpoints
{
    public static readonly string[] GrievanceCategories =
        { "match_result", "cheating", "harassment", "account", "payment", "privacy", "other" };

    public static void Map(WebApplication app)
    {
        // ---- health (ticket 56)
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
        app.MapGet("/readyz", (DrainState drain, IMatchRepository repo, MatchRegistry matches, ConnectionRegistry connections, LobbyService lobby,
            ServiceMetrics metrics) =>
        {
            bool storage = repo.Ping();
            var body = new
            {
                status = drain.IsDraining ? "draining" : storage ? "ready" : "storage_unavailable",
                active_matches = matches.ActiveCount,
                connections = connections.Count,
                rooms = lobby.RoomCount,
                queue = lobby.QueueLength,
                rules_hash = Rules.Match.RulesBundle.HashHex,
                commands = metrics.Commands,
                command_ms_p50 = Math.Round(metrics.Quantile(0.50), 2),
                command_ms_p95 = Math.Round(metrics.Quantile(0.95), 2),
                command_ms_p99 = Math.Round(metrics.Quantile(0.99), 2),
            };
            return drain.IsDraining || !storage ? Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable) : Results.Json(body);
        });

        // ---- participant-only reads (ticket 49)
        app.MapGet("/v1/matches/{matchId}/view", async (string matchId, HttpContext http, IIdentityVerifier verifier, HttpLimiters limits,
            MatchRegistry matches, IMatchRepository repo) =>
        {
            VerifiedIdentity id = await Authenticate(http, verifier);
            if (id == null) return Results.Unauthorized();
            if (!limits.PerIdentity.TryTake(id.Uid)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            MatchHost host = matches.Get(matchId);
            if (host == null)
            {
                StoredMatch stored = repo.Get(matchId);
                if (stored == null) return Results.NotFound();
                return stored.IsParticipant(id.Uid) ? Results.StatusCode(StatusCodes.Status410Gone) : Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            Rules.Replay.JsonNode view = host.ViewJson(id.Uid);
            return view == null
                ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : Results.Text(view.ToCanonicalString(), "application/json");
        });

        app.MapGet("/v1/matches/{matchId}/record", async (string matchId, HttpContext http, IIdentityVerifier verifier, HttpLimiters limits,
            IMatchRepository repo) =>
        {
            VerifiedIdentity id = await Authenticate(http, verifier);
            if (id == null) return Results.Unauthorized();
            if (!limits.PerIdentity.TryTake(id.Uid)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            StoredMatch stored = repo.Get(matchId);
            if (stored == null) return Results.NotFound();
            if (!stored.IsParticipant(id.Uid)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            // The record holds the seed and every choice: participants may read it only after the match is settled.
            if (stored.Status != MatchStatus.Finished) return Results.StatusCode(StatusCodes.Status409Conflict);
            return Results.Text(stored.RecordJson, "application/json");
        });

        // ---- grievance contact and intake (India Online Gaming Rules, Rule 20)
        app.MapGet("/v1/grievances/contact", (IOptions<ServerOptions> options) =>
        {
            GrievanceOptions g = options.Value.Grievance;
            return Results.Json(new
            {
                operator_name = g.OperatorName,
                email = g.Email,
                url = g.Url,
                acknowledge_within_hours = g.AcknowledgeWithinHours,
                resolve_within_days = g.ResolveWithinDays,
                categories = GrievanceCategories,
            });
        });

        app.MapPost("/v1/grievances", async (HttpContext http, IIdentityVerifier verifier, HttpLimiters limits, IGrievanceStore store,
            IAuditLog audit, TimeProvider time) =>
        {
            VerifiedIdentity id = BearerPresent(http) ? await Authenticate(http, verifier) : null;
            string key = id?.Uid ?? "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            if (!limits.Anonymous.TryTake(key)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            GrievanceRequest req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<GrievanceRequest>(http.Request.Body, JsonOptions, http.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "malformed" });
            }
            string problem = Validate(req);
            if (problem != null) return Results.BadRequest(new { error = problem });

            DateTimeOffset now = time.GetUtcNow();
            var g = new Grievance
            {
                Id = NewGrievanceId(now),
                ReceivedAt = now,
                ReporterRef = id?.Ref,
                Category = req.Category,
                MatchId = string.IsNullOrEmpty(req.MatchId) ? null : req.MatchId,
                Description = req.Description.Trim(),
                Contact = string.IsNullOrWhiteSpace(req.Contact) ? null : req.Contact.Trim(),
            };
            store.Add(g);
            audit.Append(g.MatchId, id?.Ref ?? "anonymous", "grievance_received", "id=" + g.Id + " category=" + g.Category);
            return Results.Json(new { grievance_id = g.Id, status = g.Status, received_at = g.ReceivedAt }, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/v1/grievances/{grievanceId}", (string grievanceId, IGrievanceStore store) =>
        {
            Grievance g = store.Get(grievanceId);
            return g == null ? Results.NotFound() : Results.Json(new { grievance_id = g.Id, status = g.Status, received_at = g.ReceivedAt });
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public sealed class GrievanceRequest
    {
        public string Category { get; set; }
        public string Description { get; set; }
        public string MatchId { get; set; }
        public string Contact { get; set; }
    }

    private static string Validate(GrievanceRequest r)
    {
        if (r == null) return "missing body";
        if (r.Category == null || Array.IndexOf(GrievanceCategories, r.Category) < 0) return "unknown category";
        if (string.IsNullOrWhiteSpace(r.Description) || r.Description.Trim().Length < 10 || r.Description.Length > 4000) return "description must be 10-4000 characters";
        if (!string.IsNullOrEmpty(r.MatchId) && !Rules.Core.SeededStream.IsCanonicalUuid(r.MatchId)) return "match_id must be a canonical UUID";
        if (r.Contact != null && r.Contact.Length > 200) return "contact is too long";
        return null;
    }

    private static string NewGrievanceId(DateTimeOffset now)
    {
        var chars = new char[8];
        for (int i = 0; i < chars.Length; i++) chars[i] = RoomCodes.Alphabet[RandomNumberGenerator.GetInt32(RoomCodes.Alphabet.Length)];
        return "GRV-" + now.UtcDateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + "-" + new string(chars);
    }

    private static bool BearerPresent(HttpContext http) => RealtimeEndpoint.BearerToken(http.Request) != null;

    private static async Task<VerifiedIdentity> Authenticate(HttpContext http, IIdentityVerifier verifier)
    {
        string token = RealtimeEndpoint.BearerToken(http.Request);
        if (token == null) return null;
        IdentityResult r = await verifier.VerifyAsync(token, http.RequestAborted);
        return r.Identity;
    }
}
