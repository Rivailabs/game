using System.Globalization;
using System.Text;
using System.Text.Json;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Client;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Economy;
using AstraKingdoms.Meta.Privacy;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Server;
using AstraKingdoms.Meta.Shop;
using AstraKingdoms.Server.Identity;
using AstraKingdoms.Server.Ops;
using AstraKingdoms.Server.Realtime;
using Microsoft.Extensions.Options;
using RulesJson = AstraKingdoms.Rules.Replay.JsonNode;

namespace AstraKingdoms.Server.MetaHost;

/// <summary>
/// HTTP surface of the hosted meta services (V1 tickets 57-64), mirroring
/// <c>AstraKingdoms.Meta.Client.IMetaBackend</c> so an HTTP client can replace the offline
/// <c>LocalMetaBackend</c>. Every player endpoint authenticates with the same identity verifier as
/// the realtime socket and is rate limited per identity; the player id always comes from the
/// verified token, never from the body. Responses are snake_case JSON; enum values are snake_case
/// tokens (for example <c>already_owned</c>).
/// <para>Unauthenticated: the rewarded-ad SSV callback (signature-verified), the web deletion form
/// (rate limited per IP; it cannot delete anything until the requester signs in).</para>
/// </summary>
public static class MetaEndpoints
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static void Map(WebApplication app)
    {
        RouteGroupBuilder meta = app.MapGroup("/v1/meta");

        // ------------------------------------------------------------ profile and audience (ticket 57)
        meta.MapGet("/profile", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            await Task.CompletedTask;
            return Results.Json(Profile(id, m));
        }));

        meta.MapPost("/age", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            AgeRequest req = await Body<AgeRequest>(http);
            if (req == null) return Bad("missing body");
            AudienceProfile prior = m.Audience(id.Uid);
            AgeGroup g = m.AudiencePolicy.Classify(req.Age);
            var next = new AudienceProfile(g, g == AgeGroup.Child ? prior.ParentalConsent : ParentalConsent.NotRequested);
            m.Store.SetAudience(id.Uid, next, m.Clock.UtcNow);
            return Results.Json(AudienceJson(next));
        }));

        meta.MapPost("/practice-exercises", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            EventRequest req = await Body<EventRequest>(http);
            if (req == null || !AnalyticsSchema.IsWellFormedToken(req.EventId)) return Bad("event_id must be a short token");
            bool counted = m.DailyTasks.RecordPracticeExercise(id.Uid, req.EventId, m.Clock.UtcNow);
            return Results.Json(new { counted });
        }));

        // ------------------------------------------------------------ daily tasks (ticket 58)
        meta.MapGet("/daily-tasks", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<DailyTaskView> tasks = m.DailyTasks.GetTasks(id.Uid);
            return Results.Json(new
            {
                day = tasks.Count > 0 ? tasks[0].DayKey : m.DailyTasks.Reset.DayKey(m.Clock.UtcNow),
                tasks = tasks.Select(t => new
                {
                    id = t.Definition.Id,
                    text_key = t.Definition.TextKey,
                    english = t.Definition.EnglishText,
                    target = t.Definition.Target,
                    progress = t.Progress,
                    complete = t.Complete,
                    claimed = t.Claimed,
                    claimable = t.Claimable,
                    reward_coins = t.Definition.RewardCoins,
                    expires_at = t.ExpiresAt,
                }),
            });
        }));

        meta.MapPost("/daily-tasks/{taskId}/claim", (HttpContext http, string taskId, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            DayRequest req = await Body<DayRequest>(http);
            if (req == null || string.IsNullOrEmpty(req.Day)) return Bad("day is required");
            ClaimResult r = m.DailyTasks.Claim(id.Uid, taskId, req.Day);
            int code = r.Status switch
            {
                ClaimStatus.Claimed or ClaimStatus.AlreadyClaimed => StatusCodes.Status200OK,
                ClaimStatus.UnknownTask => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status409Conflict,
            };
            return Results.Json(new { status = Token(r.Status), coins = r.Coins }, statusCode: code);
        }));

        // ------------------------------------------------------------ cosmetics, locker and coin shop (tickets 59, 60)
        meta.MapGet("/locker", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            await Task.CompletedTask;
            CosmeticOwnership own = m.Ownership(id.Uid);
            CosmeticAppearance look = m.Equipment.Appearance(id.Uid, own);
            return Results.Json(new
            {
                entries = m.Cosmetics.Items.Select(i => new
                {
                    id = i.Id,
                    slot = Token(i.Slot),
                    name = i.EnglishName,
                    name_key = i.NameKey,
                    owned = own.Owns(i.Id),
                    equipped = look.Items.TryGetValue(i.Slot, out CosmeticItem eq) && eq.Id == i.Id,
                    owned_via = own.SourceOf(i.Id) is CosmeticSource s ? Token(s) : null,
                    asset_key = i.Visual?.AssetKey,
                }),
                appearance = look.Items.ToDictionary(kv => Token(kv.Key), kv => kv.Value.Id),
            });
        }));

        meta.MapPost("/equip", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            CosmeticRequest req = await Body<CosmeticRequest>(http);
            if (req == null || string.IsNullOrEmpty(req.CosmeticId)) return Bad("cosmetic_id is required");
            EquipResult r = m.Equipment.Equip(id.Uid, req.CosmeticId, m.Ownership(id.Uid));
            int code = r == EquipResult.Equipped ? 200 : r == EquipResult.UnknownItem ? 404 : 409;
            return Results.Json(new { status = Token(r) }, statusCode: code);
        }));

        meta.MapPost("/shop", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            ShopRequest req = await Body<ShopRequest>(http) ?? new ShopRequest();
            PlayerTotals totals = m.Store.Totals(id.Uid);
            IReadOnlyList<ShopOfferView> shelf = ShopPresenter.BuildShelf(m.Catalog, m.Cosmetics, m.Ownership(id.Uid), m.Entitlements(id.Uid),
                totals.Coins, m.Audience(id.Uid), req.Prices ?? new Dictionary<string, string>(), m.ShopPolicy);
            return Results.Json(new
            {
                coins = totals.Coins,
                offers = shelf.Select(o => new
                {
                    currency = Token(o.Currency),
                    id = o.Id,
                    title = o.Title,
                    description = o.Description,
                    contents = o.ContentNames,
                    price = o.PriceText,
                    price_provisional = o.PriceIsProvisional,
                    terms = o.Terms,
                    state = Token(o.State),
                    can_buy = o.CanBuy,
                }),
            });
        }));

        meta.MapPost("/coin-purchase", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            CosmeticRequest req = await Body<CosmeticRequest>(http);
            if (req == null || string.IsNullOrEmpty(req.CosmeticId)) return Bad("cosmetic_id is required");
            CoinPurchaseStatus s = m.CoinShop.Buy(id.Uid, req.CosmeticId);
            int code = s switch
            {
                CoinPurchaseStatus.Purchased or CoinPurchaseStatus.AlreadyOwned => 200,
                CoinPurchaseStatus.NotForCoins => 404,
                _ => 409,
            };
            return Results.Json(new { status = Token(s), coins = m.Store.Totals(id.Uid).Coins }, statusCode: code);
        }));

        // ------------------------------------------------------------ Play billing (tickets 60, 61)
        meta.MapPost("/purchases/authorize", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            SkuRequest req = await Body<SkuRequest>(http);
            if (req == null || string.IsNullOrEmpty(req.Sku)) return Bad("sku is required");
            if (string.IsNullOrEmpty(m.AccountIdSalt)) return Results.Json(new { state = Token(OfferState.PriceUnavailable), obfuscated_account_id = (string)null });
            PurchaseAuthorization a = ShopPresenter.AuthorizePaid(id.Uid, req.Sku, m.Catalog, m.Entitlements(id.Uid), m.Audience(id.Uid), m.ShopPolicy,
                m.AccountIdSalt);
            return Results.Json(new { state = Token(a.State), obfuscated_account_id = a.ObfuscatedAccountId, allowed = a.Allowed });
        }));

        meta.MapPost("/purchases/verify", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            PurchaseRequest req = await Body<PurchaseRequest>(http);
            if (req == null || string.IsNullOrEmpty(req.Sku) || string.IsNullOrEmpty(req.Token)) return Bad("sku and token are required");
            if (req.Token.Length > 4096) return Bad("token is too long");
            PurchaseResult r = await m.Purchases.HandlePurchaseAsync(id.Uid, req.Sku, req.Token, http.RequestAborted);
            return Results.Json(PurchaseJson(r));
        }));

        meta.MapPost("/purchases/restore", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            RestoreRequest req = await Body<RestoreRequest>(http) ?? new RestoreRequest();
            List<PurchaseRequest> items = req.Purchases ?? new List<PurchaseRequest>();
            if (items.Count > 50) return Bad("at most 50 purchases per restore");
            var pairs = items.Where(p => !string.IsNullOrEmpty(p?.Sku) && !string.IsNullOrEmpty(p.Token) && p.Token.Length <= 4096)
                .Select(p => new KeyValuePair<string, string>(p.Sku, p.Token)).ToList();
            PlayerEntitlements owned = await m.Purchases.RestoreAsync(id.Uid, pairs, http.RequestAborted);
            return Results.Json(new { entitlements = owned.ActiveSkus.OrderBy(s => s, StringComparer.Ordinal) });
        }));

        // ------------------------------------------------------------ rewarded ads (tickets 62, 63)
        meta.MapPost("/ads/offer", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            OfferRequest req = await Body<OfferRequest>(http);
            if (req == null || !TryParseEnum(req.Context, out ScreenContext context)) return Bad("context must be home, shop, profile, daily_tasks or match_result");
            OfferResult r = m.Ads.IssueOffer(id.Uid, context, m.Audience(id.Uid), req.PersonalisedAds);
            AdOfferTicket t = r.Ticket;
            return Results.Json(new
            {
                decision = Token(r.Decision),
                ticket_id = t?.TicketId,
                expires_at = t?.ExpiresAt,
                // The client passes these to the ad SDK; the ticket id travels as the SSV custom data and the user id as the SSV user id.
                ssv_user_id = t == null ? null : id.Uid,
                request = t == null ? null : new
                {
                    non_personalized = t.RequestOptions.NonPersonalized,
                    child_directed = t.RequestOptions.TagForChildDirectedTreatment,
                    under_age_of_consent = t.RequestOptions.TagForUnderAgeOfConsent,
                    max_ad_content_rating = t.RequestOptions.MaxAdContentRating,
                },
            });
        }));

        meta.MapGet("/ads/ticket/{ticketId}", (HttpContext http, string ticketId, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            await Task.CompletedTask;
            return Results.Json(new { status = Token(m.Ads.GetTicketStatus(id.Uid, ticketId)) });
        }));

        // ------------------------------------------------------------ guest migration (ticket 57)
        meta.MapPost("/guest-migration", (HttpContext http, MetaServices svc) => Player(http, svc, async (id, m) =>
        {
            MigrationRequest req = await Body<MigrationRequest>(http);
            if (req == null || !AnalyticsSchema.IsWellFormedToken(req.GuestProfileId)) return Bad("guest_profile_id is required");
            List<SummaryDto> raw = req.Summaries ?? new List<SummaryDto>();
            if (raw.Count > 1000) return Bad("too many summaries");
            var summaries = new List<LocalMatchSummary>();
            foreach (SummaryDto s in raw)
            {
                if (s == null || string.IsNullOrEmpty(s.MatchResultId) || !TryParseEnum(s.Kind, out MatchKind kind) ||
                    !TryParseEnum(s.Outcome, out PlayerOutcome outcome) || !TryParseEnum(s.Ending, out MatchEnding ending))
                    return Bad("malformed summary");
                summaries.Add(new LocalMatchSummary(s.MatchResultId, kind, outcome, ending, DateTimeOffset.FromUnixTimeMilliseconds(s.CompletedAtMs),
                    s.Automation, s.DeveloperTest));
            }
            MigrationResult r = m.GuestMigration.Migrate(id.Uid, req.GuestProfileId, summaries);
            return Results.Json(new
            {
                status = Token(r.Status),
                xp_granted = r.XpGranted,
                coins_granted = r.CoinsGranted,
                xp_recalculated = r.XpRecalculated,
                coins_recalculated = r.CoinsRecalculated,
                matches_counted = r.MatchesCounted,
                rule = m.GuestMigration.Policy.PublishedRule,
            });
        }));

        // ------------------------------------------------------------ account deletion, in app (plan: store deletion readiness)
        meta.MapPost("/account/deletion", async (HttpContext http, AccountDeletionProcessor deletion) =>
        {
            (VerifiedIdentity id, IResult fail) = await Authenticate(http);
            if (fail != null) return fail;
            StoredDeletionRequest r = deletion.Request(id.Uid, DeletionChannel.InApp, verified: true);
            r = await deletion.ProcessAsync(r.RequestId, http.RequestAborted) ?? r;
            return Results.Json(DeletionJson(r, deletion), statusCode: r.State == DeletionState.Completed ? 200 : 202);
        });

        meta.MapGet("/account/deletion/{requestId}", async (HttpContext http, string requestId, AccountDeletionProcessor deletion) =>
        {
            (VerifiedIdentity id, IResult fail) = await Authenticate(http);
            if (fail != null) return fail;
            StoredDeletionRequest r = deletion.Get(requestId);
            if (r == null || (r.Account != id.Uid && r.AccountRef != id.Ref)) return Results.NotFound();
            return Results.Json(DeletionJson(r, deletion));
        });

        // ------------------------------------------------------------ account deletion, external web resource
        app.MapPost("/v1/account/deletion-request", async (HttpContext http, IIdentityVerifier verifier, HttpLimiters limits,
            AccountDeletionProcessor deletion) =>
        {
            VerifiedIdentity id = await OptionalIdentity(http, verifier);
            string key = id?.Uid ?? "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            if (!limits.Anonymous.TryTake(key)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            WebDeletionRequest req = await Body<WebDeletionRequest>(http) ?? new WebDeletionRequest();
            if (id == null && (string.IsNullOrWhiteSpace(req.Contact) || req.Contact.Trim().Length < 3 || req.Contact.Length > 200))
                return Bad("sign in, or give a contact (3-200 characters) so we can verify the account with you");
            StoredDeletionRequest r = deletion.Request(id?.Uid, DeletionChannel.Web, verified: id != null, contact: req.Contact);
            if (id != null) r = await deletion.ProcessAsync(r.RequestId, http.RequestAborted) ?? r;
            return Results.Json(DeletionJson(r, deletion), statusCode: StatusCodes.Status202Accepted);
        });

        app.MapPost("/v1/account/deletion-request/{requestId}/confirm", async (HttpContext http, string requestId, AccountDeletionProcessor deletion) =>
        {
            (VerifiedIdentity id, IResult fail) = await Authenticate(http);
            if (fail != null) return fail;
            StoredDeletionRequest r = deletion.ConfirmOwnership(requestId, id.Uid);
            if (r == null) return Results.NotFound();
            r = await deletion.ProcessAsync(r.RequestId, http.RequestAborted) ?? r;
            return Results.Json(DeletionJson(r, deletion));
        });

        // ------------------------------------------------------------ rewarded-ad server-side verification callback
        app.MapGet("/v1/ads/ssv", async (HttpContext http, MetaServices m, HttpLimiters limits, ILoggerFactory loggers) =>
        {
            string ip = "ssv:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            if (!limits.PerIdentity.TryTake(ip)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            string raw = http.Request.QueryString.HasValue ? http.Request.QueryString.Value : string.Empty;
            SsvVerification v = await m.Ssv.VerifyAsync(raw, http.RequestAborted);
            if (v.Status != SsvVerificationStatus.Verified)
            {
                loggers.CreateLogger("AstraKingdoms.Server.MetaHost.Ssv").LogWarning("SSV callback refused: {Status}", v.Status);
                return Results.Json(new { status = Token(v.Status) }, statusCode: StatusCodes.Status400BadRequest);
            }
            CallbackOutcome o = m.Ads.HandleVerifiedCallback(v.Callback);
            // Verified callbacks always answer 200 (including duplicates) so the network stops retrying.
            return Results.Json(new { status = Token(o) });
        });

        // ------------------------------------------------------------ analytics ingestion (ticket 64)
        app.MapPost("/v1/analytics/batch", async (HttpContext http, AnalyticsIngestion ingestion) =>
        {
            (VerifiedIdentity id, IResult fail) = await Authenticate(http);
            if (fail != null) return fail;
            string text = await ReadLimited(http, 256 * 1024);
            if (text == null) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            RulesJson batch;
            try
            {
                batch = RulesJson.Parse(text);
            }
            catch (FormatException)
            {
                return Bad("malformed");
            }
            IngestionResult r = ingestion.Ingest(id, batch);
            if (r.Error != null) return Results.Json(new { error = r.Error }, statusCode: r.ErrorStatus);
            return Results.Json(new
            {
                accepted = r.Accepted,
                duplicates = r.Duplicates,
                rejected = r.Rejected.Select(x => new { event_id = x.EventId, reason = x.Reason }),
            });
        });

        // ------------------------------------------------------------ development only: create a fake Play purchase for this player
        app.MapPost("/v1/dev/purchases", async (HttpContext http, IPurchaseVerifier verifier, MetaServices m, IHostEnvironment env) =>
        {
            if (verifier is not FakePurchaseVerifier fake || !env.IsDevelopment()) return Results.NotFound();
            (VerifiedIdentity id, IResult fail) = await Authenticate(http);
            if (fail != null) return fail;
            DevPurchaseRequest req = await Body<DevPurchaseRequest>(http);
            if (req == null || m.Catalog.Find(req.Sku) == null) return Bad("unknown sku");
            string token = "fake-" + Guid.NewGuid().ToString("N");
            fake.AddPurchase(req.Sku, token, new ProductPurchase(req.Pending ? PlayPurchaseState.Pending : PlayPurchaseState.Purchased,
                AcknowledgementState.NotAcknowledged, 0, "GPA.DEV-" + token[5..13], m.Clock.UtcNow, req.Test ? 0 : null,
                ObfuscatedAccountId.For(id.Uid, m.AccountIdSalt), "IN", 1));
            return Results.Json(new { sku = req.Sku, token });
        });
    }

    // ================================================================== JSON shapes

    public static object Profile(VerifiedIdentity id, MetaServices m)
    {
        ProgressionProfile p = m.Progression.GetProfile(id.Uid);
        IReadOnlyList<LevelUnlock> next = p.Level >= ProgressionRules.MaxLevel ? Array.Empty<LevelUnlock>() : UnlockTable.AtLevel(p.Level + 1);
        return new
        {
            player_ref = id.Ref,
            level = p.Level,
            total_xp = p.TotalXp,
            xp_into_level = p.XpIntoLevel,
            xp_to_next_level = p.XpToNextLevel,
            coins = p.EarnedCoins,
            owned_weapon_ids = p.OwnedWeaponIds,
            mastery = p.Mastery.OrderBy(kv => kv.Key).Select(kv => new { weapon_id = kv.Key, tier = Token(kv.Value) }),
            next_unlocks = next.Select(u => new { level = u.Level, kind = Token(u.Kind), weapon_id = u.WeaponId, cosmetic_id = u.CosmeticId }),
            entitlements = m.Entitlements(id.Uid).ActiveSkus.OrderBy(s => s, StringComparer.Ordinal),
            audience = AudienceJson(m.Audience(id.Uid)),
        };
    }

    private static object AudienceJson(AudienceProfile a) => new { age_group = Token(a.AgeGroup), parental_consent = Token(a.ParentalConsent) };

    private static object PurchaseJson(PurchaseResult r) => new
    {
        status = Token(r.Status),
        sku = r.Sku,
        acknowledged = r.Acknowledged,
        client_may_finish = r.ClientMayFinishTransaction,
        test_purchase = r.IsTestPurchase,
    };

    private static object DeletionJson(StoredDeletionRequest r, AccountDeletionProcessor deletion) => new
    {
        request_id = r.RequestId,
        state = Token(r.State),
        channel = Token(r.Channel),
        received_at = r.ReceivedAt,
        due_by = r.DueBy,
        completed_at = r.CompletedAt,
        steps = r.Steps.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => Token(kv.Value)),
        retained = deletion == null ? null : DeletionRetained(r, deletion),
    };

    private static object DeletionRetained(StoredDeletionRequest r, AccountDeletionProcessor deletion) =>
        deletion.RetainedFor(r.RequestId).Select(x => new { store = x.Store, category = x.Category, rows = x.Rows, justification = x.Justification });

    // ================================================================== plumbing

    /// <summary>Authenticated, rate-limited player handler.</summary>
    private static async Task<IResult> Player(HttpContext http, MetaServices meta, Func<VerifiedIdentity, MetaServices, Task<IResult>> handler)
    {
        (VerifiedIdentity id, IResult fail) = await Authenticate(http);
        if (fail != null) return fail;
        try
        {
            return await handler(id, meta);
        }
        catch (JsonException)
        {
            return Bad("malformed");
        }
    }

    private static async Task<(VerifiedIdentity, IResult)> Authenticate(HttpContext http)
    {
        IIdentityVerifier verifier = http.RequestServices.GetRequiredService<IIdentityVerifier>();
        HttpLimiters limits = http.RequestServices.GetRequiredService<HttpLimiters>();
        string token = RealtimeEndpoint.BearerToken(http.Request);
        if (token == null) return (null, Results.Unauthorized());
        IdentityResult r = await verifier.VerifyAsync(token, http.RequestAborted);
        if (!r.Ok) return (null, Results.Unauthorized());
        if (!limits.PerIdentity.TryTake("meta:" + r.Identity.Uid)) return (null, Results.StatusCode(StatusCodes.Status429TooManyRequests));
        return (r.Identity, null);
    }

    private static async Task<VerifiedIdentity> OptionalIdentity(HttpContext http, IIdentityVerifier verifier)
    {
        string token = RealtimeEndpoint.BearerToken(http.Request);
        if (token == null) return null;
        IdentityResult r = await verifier.VerifyAsync(token, http.RequestAborted);
        return r.Identity;
    }

    private static async Task<T> Body<T>(HttpContext http) where T : class
    {
        if (http.Request.ContentLength == 0) return null;
        string text = await ReadLimited(http, 64 * 1024);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    /// <summary>Reads the body up to <paramref name="limit"/> bytes; null when it is longer.</summary>
    private static async Task<string> ReadLimited(HttpContext http, int limit)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[8192];
        int n;
        while ((n = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > limit) return null;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static IResult Bad(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>"AlreadyOwned" → "already_owned".</summary>
    public static string Token<TEnum>(TEnum value) where TEnum : struct, Enum => SnakeCase(value.ToString());

    public static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool TryParseEnum<TEnum>(string token, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrEmpty(token)) return false;
        foreach (TEnum v in Enum.GetValues<TEnum>())
        {
            if (Token(v) != token) continue;
            value = v;
            return true;
        }
        return false;
    }

    // ================================================================== request bodies

    public sealed class AgeRequest
    {
        public int? Age { get; set; }
    }

    public sealed class EventRequest
    {
        public string EventId { get; set; }
    }

    public sealed class DayRequest
    {
        public string Day { get; set; }
    }

    public sealed class CosmeticRequest
    {
        public string CosmeticId { get; set; }
    }

    public sealed class ShopRequest
    {
        public Dictionary<string, string> Prices { get; set; }
    }

    public sealed class SkuRequest
    {
        public string Sku { get; set; }
    }

    public sealed class PurchaseRequest
    {
        public string Sku { get; set; }
        public string Token { get; set; }
    }

    public sealed class RestoreRequest
    {
        public List<PurchaseRequest> Purchases { get; set; }
    }

    public sealed class OfferRequest
    {
        public string Context { get; set; }
        public bool PersonalisedAds { get; set; }
    }

    public sealed class MigrationRequest
    {
        public string GuestProfileId { get; set; }
        public List<SummaryDto> Summaries { get; set; }
    }

    public sealed class SummaryDto
    {
        public string MatchResultId { get; set; }
        public string Kind { get; set; }
        public string Outcome { get; set; }
        public string Ending { get; set; }
        public long CompletedAtMs { get; set; }
        public bool Automation { get; set; }
        public bool DeveloperTest { get; set; }
    }

    public sealed class WebDeletionRequest
    {
        public string Contact { get; set; }
    }

    public sealed class DevPurchaseRequest
    {
        public string Sku { get; set; }
        public bool Pending { get; set; }
        public bool Test { get; set; }
    }
}
