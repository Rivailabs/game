using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Meta.Server
{
    /// <summary>
    /// <see cref="IPurchaseVerifier"/> over the Google Play Developer API v3:
    /// <c>purchases.products.get</c>, <c>purchases.products.acknowledge</c> and
    /// <c>purchases.voidedpurchases.list</c>, authorised with a service-account bearer token.
    /// <para>Status mapping: 200 found; 400/404/410 not found (bad or foreign token); 401 refreshes
    /// the token once, then configuration error; 403 configuration error (the service account lacks
    /// "View financial data / Manage orders" in Play Console); 0/408/429/5xx transient.</para>
    /// <para>UNVERIFIED against the live API in this environment: request shapes follow Google's
    /// published reference; run the closed-track licence-tester purchase checklist before release.</para>
    /// </summary>
    public sealed class GooglePlayPurchaseVerifier : IPurchaseVerifier
    {
        public const string DefaultBaseUrl = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/";

        private readonly string _packageName;
        private readonly IAccessTokenProvider _tokens;
        private readonly IHttpTransport _http;
        private readonly string _baseUrl;

        public GooglePlayPurchaseVerifier(string packageName, IAccessTokenProvider tokens, IHttpTransport http, string baseUrl = DefaultBaseUrl)
        {
            if (string.IsNullOrEmpty(packageName)) throw new ArgumentException("package name required", nameof(packageName));
            _packageName = packageName;
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _baseUrl = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
        }

        private string ProductUrl(string productId, string token) =>
            _baseUrl + Uri.EscapeDataString(_packageName) + "/purchases/products/" + Uri.EscapeDataString(productId) + "/tokens/" + Uri.EscapeDataString(token);

        public async Task<PurchaseVerification> GetProductPurchaseAsync(string productId, string purchaseToken, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(productId) || string.IsNullOrEmpty(purchaseToken)) return new PurchaseVerification(VerificationOutcome.NotFound);
            HttpReply reply = await SendAuthorisedAsync("GET", ProductUrl(productId, purchaseToken), null, ct).ConfigureAwait(false);
            VerificationOutcome? failure = Classify(reply);
            if (failure.HasValue) return new PurchaseVerification(failure.Value, error: "HTTP " + reply.Status);
            try
            {
                return new PurchaseVerification(VerificationOutcome.Found, ParseProductPurchase(reply.Body));
            }
            catch (FormatException ex)
            {
                return new PurchaseVerification(VerificationOutcome.TransientError, error: "unreadable response: " + ex.Message);
            }
        }

        public async Task<AcknowledgeOutcome> AcknowledgeAsync(string productId, string purchaseToken, CancellationToken ct = default)
        {
            HttpReply reply = await SendAuthorisedAsync("POST", ProductUrl(productId, purchaseToken) + ":acknowledge", "{}", ct).ConfigureAwait(false);
            if (reply.IsSuccess) return AcknowledgeOutcome.Acknowledged;
            return reply.IsTransient ? AcknowledgeOutcome.TransientError : AcknowledgeOutcome.Failed;
        }

        public async Task<VoidedPurchasesPage> ListVoidedPurchasesAsync(DateTimeOffset startTime, string pageToken, CancellationToken ct = default)
        {
            string url = _baseUrl + Uri.EscapeDataString(_packageName) + "/purchases/voidedpurchases?type=0&maxResults=1000&startTime=" +
                         Invariant.S(startTime.ToUnixTimeMilliseconds());
            if (!string.IsNullOrEmpty(pageToken)) url += "&token=" + Uri.EscapeDataString(pageToken);
            HttpReply reply = await SendAuthorisedAsync("GET", url, null, ct).ConfigureAwait(false);
            VerificationOutcome? failure = Classify(reply);
            if (failure.HasValue) return new VoidedPurchasesPage(failure.Value, null, null);
            try
            {
                JsonNode root = JsonNode.Parse(reply.Body);
                var items = new List<VoidedPurchase>();
                JsonNode list = JsonRead.Member(root, "voidedPurchases");
                if (list != null && list.Kind == JsonKind.Array)
                {
                    foreach (JsonNode v in list.Items)
                    {
                        items.Add(new VoidedPurchase(JsonRead.String(v, "purchaseToken"), JsonRead.String(v, "orderId"),
                            DateTimeOffset.FromUnixTimeMilliseconds(JsonRead.Long(v, "voidedTimeMillis") ?? 0),
                            JsonRead.Int(v, "voidedSource") ?? 0, JsonRead.Int(v, "voidedReason") ?? 0));
                    }
                }
                string next = JsonRead.String(JsonRead.Member(root, "tokenPagination"), "nextPageToken");
                return new VoidedPurchasesPage(VerificationOutcome.Found, items, next);
            }
            catch (FormatException)
            {
                return new VoidedPurchasesPage(VerificationOutcome.TransientError, null, null);
            }
        }

        /// <summary>Parses a <c>purchases.products</c> resource. Public for tests.</summary>
        public static ProductPurchase ParseProductPurchase(string body)
        {
            JsonNode root = JsonNode.Parse(body);
            int state = JsonRead.Int(root, "purchaseState") ?? throw new FormatException("purchaseState missing");
            if (state < 0 || state > 2) throw new FormatException("unknown purchaseState " + state);
            long millis = JsonRead.Long(root, "purchaseTimeMillis") ?? throw new FormatException("purchaseTimeMillis missing");
            return new ProductPurchase(
                (PlayPurchaseState)state,
                (JsonRead.Int(root, "acknowledgementState") ?? 0) == 1 ? AcknowledgementState.Acknowledged : AcknowledgementState.NotAcknowledged,
                JsonRead.Int(root, "consumptionState") ?? 0,
                JsonRead.String(root, "orderId"),
                DateTimeOffset.FromUnixTimeMilliseconds(millis),
                JsonRead.Int(root, "purchaseType"),
                JsonRead.String(root, "obfuscatedExternalAccountId"),
                JsonRead.String(root, "regionCode"),
                JsonRead.Int(root, "quantity") ?? 1);
        }

        private static VerificationOutcome? Classify(HttpReply reply)
        {
            if (reply.IsSuccess) return null;
            if (reply.IsTransient) return VerificationOutcome.TransientError;
            if (reply.Status == 401 || reply.Status == 403) return VerificationOutcome.ConfigurationError;
            return VerificationOutcome.NotFound;
        }

        private async Task<HttpReply> SendAuthorisedAsync(string method, string url, string body, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                AccessToken token = await _tokens.GetTokenAsync(ct).ConfigureAwait(false);
                if (!token.Ok) return new HttpReply(token.Status == 0 || token.Status >= 500 || token.Status == 429 ? token.Status : 401, "token exchange failed");
                var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + token.Value } };
                HttpReply reply = await _http.SendAsync(new HttpCall(method, url, headers, body, body != null ? "application/json" : null), ct).ConfigureAwait(false);
                if (reply.Status != 401 || attempt == 1) return reply;
                _tokens.Invalidate();
            }
            throw new InvalidOperationException("unreachable");
        }
    }
}
