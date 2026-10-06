using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AstraKingdoms.Client.Online;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Identity;
using AstraKingdoms.Server.Matches;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AstraKingdoms.Server.Tests;

/// <summary>Ticket 49: unauthenticated or non-participant requests cannot read or mutate private match state.</summary>
[TestFixture]
public class AuthTests
{
    private RSA _key;
    private StaticKeySource _keys;
    private FakeTimeProvider _time;
    private FirebaseIdTokenVerifier _verifier;

    [SetUp]
    public void SetUp()
    {
        _key = RSA.Create(2048);
        _keys = new StaticKeySource();
        _keys.Keys["k1"] = _key;
        _time = new FakeTimeProvider(ServerHarness.Epoch);
        _verifier = new FirebaseIdTokenVerifier(_keys, _time, Jwt.ProjectId, TimeSpan.FromSeconds(60));
    }

    [TearDown]
    public void TearDown() => _key.Dispose();

    private Task<IdentityResult> Verify(string token) => _verifier.VerifyAsync(token, CancellationToken.None);

    [Test]
    public async Task ValidFirebaseTokenYieldsItsSubject()
    {
        IdentityResult r = await Verify(Jwt.Sign(_key, "k1", Jwt.Claims(_time.GetUtcNow(), sub: "uid-42")));
        Assert.That(r.Ok, Is.True, r.Failure);
        Assert.That(r.Identity.Uid, Is.EqualTo("uid-42"));
        Assert.That(r.Identity.Ref, Does.StartWith("p_").And.Not.Contain("uid-42"));
    }

    [Test]
    public async Task FirebaseVerifierRejectsEveryInvalidToken()
    {
        DateTimeOffset now = _time.GetUtcNow();
        using RSA other = RSA.Create(2048);
        var bad = new Dictionary<string, string>
        {
            ["wrong audience"] = Jwt.Sign(_key, "k1", Jwt.Claims(now, aud: "someone-else")),
            ["wrong issuer"] = Jwt.Sign(_key, "k1", Jwt.Claims(now, iss: "https://securetoken.google.com/someone-else")),
            ["expired"] = Jwt.Sign(_key, "k1", Jwt.Claims(now, expiresInSeconds: -120)),
            ["unknown kid"] = Jwt.Sign(_key, "k9", Jwt.Claims(now)),
            ["foreign key"] = Jwt.Sign(other, "k1", Jwt.Claims(now)),
            ["alg HS256"] = Jwt.Sign(_key, "k1", Jwt.Claims(now), alg: "HS256"),
            ["empty subject"] = Jwt.Sign(_key, "k1", Jwt.Claims(now, sub: "")),
            ["garbage"] = "not.a.jwt",
            ["missing"] = null,
        };
        var future = Jwt.Claims(now);
        future["iat"] = now.ToUnixTimeSeconds() + 600;
        bad["issued in the future"] = Jwt.Sign(_key, "k1", future);
        string good = Jwt.Sign(_key, "k1", Jwt.Claims(now));
        string[] parts = good.Split('.');
        bad["tampered payload"] = parts[0] + "." + Base64Url.Encode(System.Text.Encoding.UTF8.GetBytes(
            "{\"iss\":\"https://securetoken.google.com/astra-test\",\"aud\":\"astra-test\",\"sub\":\"admin\",\"iat\":1,\"auth_time\":1,\"exp\":9999999999}")) + "." + parts[2];

        foreach (KeyValuePair<string, string> kv in bad)
            Assert.That((await Verify(kv.Value)).Ok, Is.False, kv.Key);
        Assert.That(_keys.Refreshes, Is.GreaterThan(0), "an unknown kid forces one key refresh");
    }

    [Test]
    public async Task GoogleKeySourceParsesCertificatesAndHonoursMaxAge()
    {
        using var cert = new CertificateRequest("CN=test", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        string pem = cert.ExportCertificatePem();
        var handler = new CountingHandler("{\"k1\":" + System.Text.Json.JsonSerializer.Serialize(pem) + "}");
        var source = new GoogleX509KeySource(new HttpClient(handler), _time, "https://keys.invalid/x509");
        var verifier = new FirebaseIdTokenVerifier(source, _time, Jwt.ProjectId, TimeSpan.FromSeconds(60));

        IdentityResult r = await verifier.VerifyAsync(Jwt.Sign(_key, "k1", Jwt.Claims(_time.GetUtcNow())), CancellationToken.None);
        Assert.That(r.Ok, Is.True, r.Failure);
        await verifier.VerifyAsync(Jwt.Sign(_key, "k1", Jwt.Claims(_time.GetUtcNow())), CancellationToken.None);
        Assert.That(handler.Calls, Is.EqualTo(1), "cached within max-age");
        _time.Advance(TimeSpan.FromSeconds(3601));
        await verifier.VerifyAsync(Jwt.Sign(_key, "k1", Jwt.Claims(_time.GetUtcNow())), CancellationToken.None);
        Assert.That(handler.Calls, Is.EqualTo(2), "re-fetched after max-age");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public int Calls;
        public CountingHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) };
            resp.Headers.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromSeconds(3600) };
            return Task.FromResult(resp);
        }
    }

    [Test]
    public async Task FirebaseModeAcceptsSignedTokensAndRefusesUnauthenticatedSockets()
    {
        await using ServerHarness h = ServerHarness.Start(
            new Dictionary<string, string> { ["AstraServer:Auth:Mode"] = "Firebase", ["AstraServer:Auth:FirebaseProjectId"] = Jwt.ProjectId },
            time: _time, services: s => s.AddSingleton<ISigningKeySource>(_keys));

        string token = Jwt.Sign(_key, "k1", Jwt.Claims(_time.GetUtcNow(), sub: "fb-alice"));
        await using TestPlayer alice = await TestPlayer.ConnectWithTokenAsync(h, "alice", token, "fb-alice");
        Assert.That(alice.Client.Connection.Welcome.PlayerRef, Is.EqualTo(PlayerRef.Of("fb-alice")));

        Assert.ThrowsAsync<InvalidOperationException>(() => h.RawSocketAsync(null), "no token: the upgrade is refused");
        Assert.ThrowsAsync<InvalidOperationException>(() => h.RawSocketAsync("dev:mallory"), "dev tokens are not valid in Firebase mode");
        Assert.ThrowsAsync<InvalidOperationException>(() => h.RawSocketAsync(Jwt.Sign(_key, "k1", Jwt.Claims(_time.GetUtcNow(), aud: "x"))));

        // The client library stops (no retry storm) when the server rejects its identity.
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using TestPlayer forged = await TestPlayer.ConnectWithTokenAsync(h, "forged", "dev:forged", "x");
        });
    }

    [Test]
    public async Task NonParticipantsCannotReadOrMutateAMatch()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        await using TestPlayer eve = await TestPlayer.ConnectAsync(h, "eve");
        MatchHost host = await Play.FriendMatch(h, a, b);

        // HTTP: anonymous 401, outsider 403, participant gets only their own view.
        using HttpClient anon = h.Http();
        Assert.That((await anon.GetAsync("/v1/matches/" + host.MatchId + "/view")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using HttpClient eveHttp = h.Http(eve.Token);
        Assert.That((await eveHttp.GetAsync("/v1/matches/" + host.MatchId + "/view")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await eveHttp.GetAsync("/v1/matches/" + host.MatchId + "/record")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using HttpClient aliceHttp = h.Http(a.Token);
        HttpResponseMessage own = await aliceHttp.GetAsync("/v1/matches/" + host.MatchId + "/view");
        Assert.That(own.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        JsonNode view = JsonNode.Parse(await own.Content.ReadAsStringAsync());
        Assert.That(view["viewer"].AsInt(), Is.EqualTo((int)a.Match.LocalSide));
        Assert.That((await aliceHttp.GetAsync("/v1/matches/" + host.MatchId + "/record")).StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
            "the record (seed, choices) stays sealed until the match is settled");

        // Realtime: resume and commands for someone else's match are refused and change nothing.
        ulong revision = host.Engine.StateRevision;
        eve.Client.SendRaw(ClientMessages.MatchResume("r-1", host.MatchId, 0));
        await ServerHarness.Until(() => eve.HasError(ErrorCodes.NotParticipant, "r-1"), "resume refusal");
        PlayerView aView = host.Engine.GetView(a.Match.LocalSide);
        var forged = new SubmitLoadoutCommand(aView.NewHeader(Guid.NewGuid().ToString("D")), new[] { 1 });
        eve.Client.SendRaw(ClientMessages.MatchCommand("r-2", forged));
        await ServerHarness.Until(() => eve.HasError(ErrorCodes.NotParticipant, "r-2"), "command refusal");
        Assert.That(host.Engine.StateRevision, Is.EqualTo(revision));
        Assert.That(host.Engine.GetView(a.Match.LocalSide).OwnLoadout, Is.Null);
        Assert.That(eve.OfType(MessageTypes.MatchUpdate), Is.Empty, "an outsider never receives a view");

        eve.Client.SendRaw(ClientMessages.MatchResume("r-3", Guid.NewGuid().ToString("D"), 0));
        await ServerHarness.Until(() => eve.HasError(ErrorCodes.MatchNotFound, "r-3"), "unknown match");
    }

    [Test]
    public async Task SettledRecordIsReadableByParticipantsOnly()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice", BotDifficulty.Normal);
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        await using TestPlayer eve = await TestPlayer.ConnectAsync(h, "eve");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(b);
        await Play.ToEnd(h, host, a, b); // bob never locks: forfeit after two timeouts

        using HttpClient bobHttp = h.Http(b.Token);
        HttpResponseMessage record = await bobHttp.GetAsync("/v1/matches/" + host.MatchId + "/record");
        Assert.That(record.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Replayer.Verify(await record.Content.ReadAsStringAsync()).Success, Is.True);
        using HttpClient eveHttp = h.Http(eve.Token);
        Assert.That((await eveHttp.GetAsync("/v1/matches/" + host.MatchId + "/record")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task ProductionWiringUsesGoogleKeysAndRequiresAProjectId()
    {
        await using (ServerHarness h = ServerHarness.Start(new Dictionary<string, string>
                     {
                         ["AstraServer:Auth:Mode"] = "Firebase",
                         ["AstraServer:Auth:FirebaseProjectId"] = Jwt.ProjectId,
                     }, environment: "Production"))
        {
            Assert.That(h.Get<IIdentityVerifier>(), Is.InstanceOf<FirebaseIdTokenVerifier>());
            Assert.That(h.Get<ISigningKeySource>(), Is.InstanceOf<GoogleX509KeySource>());
        }
        Assert.That(() => ServerHarness.Start(new Dictionary<string, string> { ["AstraServer:Auth:Mode"] = "Firebase" }, environment: "Production"),
            Throws.InstanceOf<Exception>(), "Firebase mode without a project ID fails at startup");
    }

    [Test]
    public void DevAuthenticationIsRefusedOutsideDevelopment()
    {
        Assert.That(() => ServerHarness.Start(environment: "Production"), Throws.InstanceOf<Exception>());
    }
}
