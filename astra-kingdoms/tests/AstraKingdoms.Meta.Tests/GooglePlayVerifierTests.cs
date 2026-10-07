using System.Security.Cryptography;
using System.Text;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Server;
using AstraKingdoms.Rules.Replay;
using NUnit.Framework;

namespace AstraKingdoms.Meta.Tests;

/// <summary>A scripted HTTP transport: records calls and answers from a queue or a handler.</summary>
internal sealed class ScriptedHttp : IHttpTransport
{
    public readonly List<HttpCall> Calls = new();
    public Func<HttpCall, HttpReply> Handler = _ => new HttpReply(500, "no handler");

    public Task<HttpReply> SendAsync(HttpCall call, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add(call);
        return Task.FromResult(Handler(call));
    }
}

/// <summary>
/// Ticket 61: the Google Play Developer API verifier and its service-account JWT exchange, with a
/// locally generated RSA key and a scripted transport. This checks our request construction and
/// response handling; it does not prove the live Google API accepts them (not reachable here).
/// </summary>
public class GooglePlayVerifierTests
{
    private RSA _rsa;
    private ServiceAccountKey _key;
    private ManualClock _clock;
    private ScriptedHttp _http;
    private int _tokenExchanges;

    [SetUp]
    public void SetUp()
    {
        _rsa = RSA.Create(2048);
        string pem = Pem.Encode(_rsa.ExportPkcs8PrivateKey(), "PRIVATE KEY");
        string json = "{\"type\":\"service_account\",\"client_email\":\"verifier@astra.iam.gserviceaccount.com\",\"private_key_id\":\"k1\"," +
                      "\"private_key\":\"" + pem.Replace("\n", "\\n") + "\",\"token_uri\":\"https://oauth2.googleapis.com/token\"}";
        _key = ServiceAccountKey.Parse(json);
        _clock = T0.Clock();
        _http = new ScriptedHttp();
        _tokenExchanges = 0;
    }

    [TearDown]
    public void TearDown() => _rsa.Dispose();

    private HttpReply TokenEndpoint(HttpCall c)
    {
        _tokenExchanges++;
        return new HttpReply(200, "{\"access_token\":\"ya29.token" + _tokenExchanges + "\",\"expires_in\":3599,\"token_type\":\"Bearer\"}");
    }

    private GooglePlayPurchaseVerifier Verifier(Func<HttpCall, HttpReply> api)
    {
        _http.Handler = c => c.Url.StartsWith("https://oauth2.googleapis.com/token", StringComparison.Ordinal) ? TokenEndpoint(c) : api(c);
        return new GooglePlayPurchaseVerifier("com.rivailabs.astrakingdoms", new GoogleServiceAccountTokenProvider(_key, _http, _clock), _http);
    }

    [Test]
    public void AssertionIsAValidRs256JwtWithTheRequiredClaims()
    {
        var provider = new GoogleServiceAccountTokenProvider(_key, _http, _clock);
        string jwt = provider.CreateAssertion(_clock.UtcNow);
        string[] parts = jwt.Split('.');
        Assert.That(parts, Has.Length.EqualTo(3));
        byte[] sig = Base64Url.Decode(parts[2]);
        Assert.That(_rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), Is.True);
        JsonNode header = JsonNode.Parse(Encoding.UTF8.GetString(Base64Url.Decode(parts[0])));
        JsonNode claims = JsonNode.Parse(Encoding.UTF8.GetString(Base64Url.Decode(parts[1])));
        Assert.That(JsonRead.String(header, "alg"), Is.EqualTo("RS256"));
        Assert.That(JsonRead.String(header, "kid"), Is.EqualTo("k1"));
        Assert.That(JsonRead.String(claims, "iss"), Is.EqualTo("verifier@astra.iam.gserviceaccount.com"));
        Assert.That(JsonRead.String(claims, "scope"), Is.EqualTo("https://www.googleapis.com/auth/androidpublisher"));
        Assert.That(JsonRead.String(claims, "aud"), Is.EqualTo("https://oauth2.googleapis.com/token"));
        Assert.That(JsonRead.Long(claims, "exp") - JsonRead.Long(claims, "iat"), Is.EqualTo(3600));
        Assert.That(JsonRead.Long(claims, "iat"), Is.EqualTo(_clock.UtcNow.ToUnixTimeSeconds()));
    }

    [Test]
    public async Task TokenExchangeIsFormEncodedJwtBearerAndCached()
    {
        var provider = new GoogleServiceAccountTokenProvider(_key, _http, _clock);
        _http.Handler = TokenEndpoint;
        AccessToken t1 = await provider.GetTokenAsync();
        AccessToken t2 = await provider.GetTokenAsync();
        Assert.That(t1.Value, Is.EqualTo("ya29.token1"));
        Assert.That(t2.Value, Is.EqualTo("ya29.token1"));
        Assert.That(_http.Calls, Has.Count.EqualTo(1));
        HttpCall call = _http.Calls[0];
        Assert.That(call.Method, Is.EqualTo("POST"));
        Assert.That(call.ContentType, Is.EqualTo("application/x-www-form-urlencoded"));
        Assert.That(call.Body, Does.StartWith("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer&assertion="));
        _clock.Advance(TimeSpan.FromSeconds(3599 - 59));
        Assert.That((await provider.GetTokenAsync()).Value, Is.EqualTo("ya29.token2"), "refreshed shortly before expiry");
    }

    [Test]
    public async Task FailedTokenExchangeIsReported()
    {
        var provider = new GoogleServiceAccountTokenProvider(_key, _http, _clock);
        _http.Handler = _ => new HttpReply(400, "{\"error\":\"invalid_grant\"}");
        AccessToken t = await provider.GetTokenAsync();
        Assert.That(t.Ok, Is.False);
        Assert.That(t.Status, Is.EqualTo(400));
    }

    [Test]
    public async Task ProductsGetBuildsTheDocumentedRequestAndParsesTheResource()
    {
        GooglePlayPurchaseVerifier v = Verifier(c => new HttpReply(200,
            "{\"kind\":\"androidpublisher#productPurchase\",\"purchaseTimeMillis\":\"1791268200000\",\"purchaseState\":0,\"consumptionState\":0," +
            "\"developerPayload\":\"\",\"orderId\":\"GPA.3333-4444\",\"purchaseType\":0,\"acknowledgementState\":0," +
            "\"obfuscatedExternalAccountId\":\"abc\",\"regionCode\":\"IN\",\"quantity\":1}"));
        PurchaseVerification r = await v.GetProductPurchaseAsync("ak.cosmetic.sunrise_pack", "tok/with+chars");
        Assert.That(r.Outcome, Is.EqualTo(VerificationOutcome.Found));
        HttpCall api = _http.Calls.Last();
        Assert.That(api.Method, Is.EqualTo("GET"));
        Assert.That(api.Url, Is.EqualTo("https://androidpublisher.googleapis.com/androidpublisher/v3/applications/com.rivailabs.astrakingdoms/purchases/products/ak.cosmetic.sunrise_pack/tokens/tok%2Fwith%2Bchars"));
        Assert.That(api.Headers["Authorization"], Is.EqualTo("Bearer ya29.token1"));
        Assert.That(r.Purchase.PurchaseState, Is.EqualTo(PlayPurchaseState.Purchased));
        Assert.That(r.Purchase.IsTestPurchase, Is.True);
        Assert.That(r.Purchase.OrderId, Is.EqualTo("GPA.3333-4444"));
        Assert.That(r.Purchase.PurchaseTime, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1791268200000)));
        Assert.That(r.Purchase.ObfuscatedExternalAccountId, Is.EqualTo("abc"));
        Assert.That(r.Purchase.AcknowledgementState, Is.EqualTo(AcknowledgementState.NotAcknowledged));
    }

    [Test]
    public void ParsesPendingAndRealPurchases()
    {
        ProductPurchase p = GooglePlayPurchaseVerifier.ParseProductPurchase("{\"purchaseTimeMillis\":\"5\",\"purchaseState\":2,\"acknowledgementState\":1}");
        Assert.That(p.PurchaseState, Is.EqualTo(PlayPurchaseState.Pending));
        Assert.That(p.PurchaseType, Is.Null);
        Assert.That(p.IsTestPurchase, Is.False);
        Assert.That(p.AcknowledgementState, Is.EqualTo(AcknowledgementState.Acknowledged));
        Assert.Throws<FormatException>(() => GooglePlayPurchaseVerifier.ParseProductPurchase("{\"purchaseState\":9,\"purchaseTimeMillis\":\"5\"}"));
    }

    [TestCase(404, VerificationOutcome.NotFound)]
    [TestCase(410, VerificationOutcome.NotFound)]
    [TestCase(400, VerificationOutcome.NotFound)]
    [TestCase(403, VerificationOutcome.ConfigurationError)]
    [TestCase(429, VerificationOutcome.TransientError)]
    [TestCase(503, VerificationOutcome.TransientError)]
    [TestCase(0, VerificationOutcome.TransientError)]
    public async Task HttpStatusesMapToOutcomes(int status, VerificationOutcome expected)
    {
        GooglePlayPurchaseVerifier v = Verifier(_ => new HttpReply(status, "{}"));
        Assert.That((await v.GetProductPurchaseAsync("sku", "tok")).Outcome, Is.EqualTo(expected));
    }

    [Test]
    public async Task UnreadableBodyIsTransient()
    {
        GooglePlayPurchaseVerifier v = Verifier(_ => new HttpReply(200, "<html>"));
        Assert.That((await v.GetProductPurchaseAsync("sku", "tok")).Outcome, Is.EqualTo(VerificationOutcome.TransientError));
    }

    [Test]
    public async Task Unauthorized401RefreshesTheTokenOnce()
    {
        int apiCalls = 0;
        GooglePlayPurchaseVerifier v = Verifier(c =>
        {
            apiCalls++;
            return c.Headers["Authorization"] == "Bearer ya29.token1" ? new HttpReply(401, "") : new HttpReply(200, "{\"purchaseTimeMillis\":\"1\",\"purchaseState\":0}");
        });
        Assert.That((await v.GetProductPurchaseAsync("sku", "tok")).Outcome, Is.EqualTo(VerificationOutcome.Found));
        Assert.That(apiCalls, Is.EqualTo(2));
        Assert.That(_tokenExchanges, Is.EqualTo(2));

        GooglePlayPurchaseVerifier always401 = Verifier(_ => new HttpReply(401, ""));
        Assert.That((await always401.GetProductPurchaseAsync("sku", "tok")).Outcome, Is.EqualTo(VerificationOutcome.ConfigurationError));
    }

    [Test]
    public async Task AcknowledgeIsAPostToTheAcknowledgeMethod()
    {
        GooglePlayPurchaseVerifier v = Verifier(c => new HttpReply(c.Method == "POST" ? 204 : 500, ""));
        Assert.That(await v.AcknowledgeAsync("sku", "tok"), Is.EqualTo(AcknowledgeOutcome.Acknowledged));
        Assert.That(_http.Calls.Last().Url, Does.EndWith("/purchases/products/sku/tokens/tok:acknowledge"));
        Assert.That(_http.Calls.Last().Body, Is.EqualTo("{}"));
        Assert.That(await Verifier(_ => new HttpReply(502, "")).AcknowledgeAsync("sku", "tok"), Is.EqualTo(AcknowledgeOutcome.TransientError));
        Assert.That(await Verifier(_ => new HttpReply(400, "")).AcknowledgeAsync("sku", "tok"), Is.EqualTo(AcknowledgeOutcome.Failed));
    }

    [Test]
    public async Task VoidedPurchasesArePagedAndParsed()
    {
        GooglePlayPurchaseVerifier v = Verifier(c => c.Url.Contains("&token=p2", StringComparison.Ordinal)
            ? new HttpReply(200, "{\"voidedPurchases\":[{\"purchaseToken\":\"t3\",\"orderId\":\"o3\",\"voidedTimeMillis\":\"3000\",\"voidedSource\":2,\"voidedReason\":7}]}")
            : new HttpReply(200, "{\"tokenPagination\":{\"nextPageToken\":\"p2\"},\"voidedPurchases\":[{\"kind\":\"androidpublisher#voidedPurchase\",\"purchaseToken\":\"t1\",\"purchaseTimeMillis\":\"1\",\"voidedTimeMillis\":\"2000\",\"orderId\":\"o1\",\"voidedSource\":0,\"voidedReason\":1}]}"));
        VoidedPurchasesPage first = await v.ListVoidedPurchasesAsync(DateTimeOffset.FromUnixTimeMilliseconds(1000), null);
        Assert.That(_http.Calls.Last().Url, Does.Contain("/purchases/voidedpurchases?type=0&maxResults=1000&startTime=1000"));
        Assert.That(first.Items.Single().PurchaseToken, Is.EqualTo("t1"));
        Assert.That(first.NextPageToken, Is.EqualTo("p2"));
        VoidedPurchasesPage second = await v.ListVoidedPurchasesAsync(DateTimeOffset.FromUnixTimeMilliseconds(1000), first.NextPageToken);
        Assert.That(second.Items.Single().VoidedReason, Is.EqualTo(7));
        Assert.That(second.NextPageToken, Is.Null);
    }

    [Test]
    public async Task PurchaseServiceRunsEndToEndOverTheHttpVerifier()
    {
        string obf = ObfuscatedAccountId.For("alice", "salt");
        bool acked = false;
        GooglePlayPurchaseVerifier v = Verifier(c =>
        {
            if (c.Method == "POST")
            {
                acked = true;
                return new HttpReply(200, "{}");
            }
            return new HttpReply(200, "{\"purchaseTimeMillis\":\"" + T0.Noon.ToUnixTimeMilliseconds() + "\",\"purchaseState\":0,\"acknowledgementState\":" + (acked ? 1 : 0) +
                                       ",\"orderId\":\"GPA.1\",\"obfuscatedExternalAccountId\":\"" + obf + "\"}");
        });
        var svc = new PurchaseService(v, new InMemoryEntitlementLedgerStore(), new InMemoryAcknowledgementQueue(), Shop.StoreCatalog.Default, _clock,
            new BillingOptions { AccountIdSalt = "salt" });
        PurchaseResult r = await svc.HandlePurchaseAsync("alice", "ak.cosmetic.sunrise_pack", "tok");
        Assert.That(r.Status, Is.EqualTo(PurchaseStatus.Granted));
        Assert.That(acked, Is.True);
        Assert.That((await svc.HandlePurchaseAsync("alice", "ak.cosmetic.sunrise_pack", "tok")).Status, Is.EqualTo(PurchaseStatus.AlreadyOwned));
    }
}
