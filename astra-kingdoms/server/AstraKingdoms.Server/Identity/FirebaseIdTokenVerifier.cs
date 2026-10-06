using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AstraKingdoms.Server.Identity;

/// <summary>Supplies the RSA public keys that may sign ID tokens, by key ID.</summary>
public interface ISigningKeySource
{
    /// <summary>Current keys. <paramref name="refresh"/> asks for a re-fetch (an unknown kid may mean the keys rotated).</summary>
    Task<IReadOnlyDictionary<string, RSA>> GetKeysAsync(bool refresh, CancellationToken ct);
}

/// <summary>
/// Verifies Firebase Authentication ID tokens as Firebase documents for third-party JWT libraries:
/// alg RS256 with a kid from Google's published keys, a valid RSA-SHA256 signature, exp in the
/// future, iat and auth_time in the past, aud equal to the project ID, iss equal to
/// https://securetoken.google.com/&lt;project&gt;, and a non-empty sub (the UID, at most 128 chars).
/// </summary>
public sealed class FirebaseIdTokenVerifier : IIdentityVerifier
{
    private readonly ISigningKeySource _keys;
    private readonly TimeProvider _time;
    private readonly string _projectId;
    private readonly TimeSpan _skew;

    public FirebaseIdTokenVerifier(ISigningKeySource keys, TimeProvider time, IOptions<ServerOptions> options)
        : this(keys, time, options.Value.Auth.FirebaseProjectId, TimeSpan.FromSeconds(options.Value.Auth.ClockSkewSeconds))
    {
    }

    public FirebaseIdTokenVerifier(ISigningKeySource keys, TimeProvider time, string projectId, TimeSpan skew)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("A Firebase project ID is required in Firebase auth mode.", nameof(projectId));
        _keys = keys;
        _time = time;
        _projectId = projectId;
        _skew = skew;
    }

    public string ExpectedIssuer => "https://securetoken.google.com/" + _projectId;

    public async Task<IdentityResult> VerifyAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 8192) return IdentityResult.Fail("token missing or too long");
        string[] parts = token.Split('.');
        if (parts.Length != 3) return IdentityResult.Fail("not a JWS compact token");

        JsonElement header, payload;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(Base64Url.Decode(parts[0])).RootElement;
            payload = JsonDocument.Parse(Base64Url.Decode(parts[1])).RootElement;
            signature = Base64Url.Decode(parts[2]);
        }
        catch (Exception e) when (e is FormatException || e is JsonException)
        {
            return IdentityResult.Fail("malformed token");
        }

        if (Str(header, "alg") != "RS256") return IdentityResult.Fail("alg is not RS256");
        string kid = Str(header, "kid");
        if (string.IsNullOrEmpty(kid)) return IdentityResult.Fail("kid missing");

        IReadOnlyDictionary<string, RSA> keys = await _keys.GetKeysAsync(false, ct).ConfigureAwait(false);
        if (!keys.TryGetValue(kid, out RSA rsa))
        {
            keys = await _keys.GetKeysAsync(true, ct).ConfigureAwait(false);
            if (!keys.TryGetValue(kid, out rsa)) return IdentityResult.Fail("unknown kid");
        }
        byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            return IdentityResult.Fail("bad signature");

        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        long skew = (long)_skew.TotalSeconds;
        if (!Num(payload, "exp", out long exp) || exp <= now - skew) return IdentityResult.Fail("expired");
        if (!Num(payload, "iat", out long iat) || iat > now + skew) return IdentityResult.Fail("issued in the future");
        if (!Num(payload, "auth_time", out long authTime) || authTime > now + skew) return IdentityResult.Fail("auth_time in the future");
        if (Str(payload, "aud") != _projectId) return IdentityResult.Fail("wrong audience");
        if (Str(payload, "iss") != ExpectedIssuer) return IdentityResult.Fail("wrong issuer");
        string sub = Str(payload, "sub");
        if (string.IsNullOrEmpty(sub) || sub.Length > 128) return IdentityResult.Fail("bad subject");
        return IdentityResult.Success(new VerifiedIdentity(sub, "firebase"));
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Num(JsonElement e, string name, out long value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number &&
               v.TryGetInt64(out value);
    }
}

/// <summary>Base64url without padding (RFC 7515).</summary>
public static class Base64Url
{
    public static byte[] Decode(string s)
    {
        if (s == null) throw new FormatException("null");
        string b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4)
        {
            case 2: b += "=="; break;
            case 3: b += "="; break;
            case 1: throw new FormatException("bad base64url length");
        }
        return Convert.FromBase64String(b);
    }

    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Google's published x509 certificates for Firebase ID tokens, cached for the response's
/// Cache-Control max-age. A forced refresh (unknown kid) is limited to once a minute so a flood of
/// forged kids cannot hammer Google.
/// </summary>
public sealed class GoogleX509KeySource : ISigningKeySource
{
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly string _url;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, RSA> _keys = new Dictionary<string, RSA>();
    private DateTimeOffset _expires = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;

    public GoogleX509KeySource(HttpClient http, TimeProvider time, string url)
    {
        _http = http;
        _time = time;
        _url = url;
    }

    public async Task<IReadOnlyDictionary<string, RSA>> GetKeysAsync(bool refresh, CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (now < _expires && !(refresh && now - _lastFetch > TimeSpan.FromMinutes(1))) return _keys;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = _time.GetUtcNow();
            if (now < _expires && !(refresh && now - _lastFetch > TimeSpan.FromMinutes(1))) return _keys;
            using HttpResponseMessage response = await _http.GetAsync(_url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _keys = ParseCertificates(json);
            _lastFetch = now;
            TimeSpan maxAge = MaxAge(response.Headers.CacheControl) ?? TimeSpan.FromMinutes(5);
            _expires = now + maxAge;
            return _keys;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TimeSpan? MaxAge(CacheControlHeaderValue cc) => cc?.MaxAge;

    /// <summary>Parses {"kid": "-----BEGIN CERTIFICATE-----..."} into RSA public keys.</summary>
    public static IReadOnlyDictionary<string, RSA> ParseCertificates(string json)
    {
        var result = new Dictionary<string, RSA>(StringComparer.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            using X509Certificate2 cert = X509Certificate2.CreateFromPem(p.Value.GetString());
            RSA rsa = cert.GetRSAPublicKey();
            if (rsa != null) result[p.Name] = rsa;
        }
        return result;
    }
}
