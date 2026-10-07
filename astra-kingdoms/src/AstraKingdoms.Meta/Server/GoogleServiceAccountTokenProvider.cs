using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Meta.Server
{
    /// <summary>An OAuth 2.0 bearer token, or why there is none.</summary>
    public sealed class AccessToken
    {
        public string Value { get; }
        public DateTimeOffset ExpiresAt { get; }
        /// <summary>HTTP status of a failed exchange (0 = network), or 200.</summary>
        public int Status { get; }

        public AccessToken(string value, DateTimeOffset expiresAt, int status)
        {
            Value = value;
            ExpiresAt = expiresAt;
            Status = status;
        }

        public bool Ok => !string.IsNullOrEmpty(Value);
    }

    public interface IAccessTokenProvider
    {
        Task<AccessToken> GetTokenAsync(CancellationToken ct = default);
        /// <summary>Forget the cached token (after a 401).</summary>
        void Invalidate();
    }

    /// <summary>The fields of a Google service-account JSON key that the JWT flow needs.</summary>
    public sealed class ServiceAccountKey
    {
        public string ClientEmail { get; }
        public string PrivateKeyPem { get; }
        public string TokenUri { get; }
        public string PrivateKeyId { get; }

        public ServiceAccountKey(string clientEmail, string privateKeyPem, string tokenUri = DefaultTokenUri, string privateKeyId = null)
        {
            ClientEmail = clientEmail ?? throw new ArgumentNullException(nameof(clientEmail));
            PrivateKeyPem = privateKeyPem ?? throw new ArgumentNullException(nameof(privateKeyPem));
            TokenUri = string.IsNullOrEmpty(tokenUri) ? DefaultTokenUri : tokenUri;
            PrivateKeyId = privateKeyId;
        }

        public const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

        /// <summary>Parses the JSON key file downloaded from Google Cloud (keep it in a secret store, never in the repo or client).</summary>
        public static ServiceAccountKey Parse(string json)
        {
            JsonNode root = JsonNode.Parse(json);
            return new ServiceAccountKey(JsonRead.String(root, "client_email"), JsonRead.String(root, "private_key"),
                JsonRead.String(root, "token_uri"), JsonRead.String(root, "private_key_id"));
        }
    }

    /// <summary>
    /// OAuth 2.0 JWT-bearer flow for a service account: builds an RS256-signed assertion
    /// (iss = client email, scope, aud = token URI, iat/exp one hour apart), exchanges it at the token
    /// endpoint and caches the access token until shortly before it expires.
    /// </summary>
    public sealed class GoogleServiceAccountTokenProvider : IAccessTokenProvider
    {
        public const string AndroidPublisherScope = "https://www.googleapis.com/auth/androidpublisher";

        private readonly ServiceAccountKey _key;
        private readonly IHttpTransport _http;
        private readonly IClock _clock;
        private readonly string _scope;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private AccessToken _cached;

        public GoogleServiceAccountTokenProvider(ServiceAccountKey key, IHttpTransport http, IClock clock, string scope = AndroidPublisherScope)
        {
            _key = key ?? throw new ArgumentNullException(nameof(key));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _scope = scope;
        }

        public void Invalidate() => _cached = null;

        public async Task<AccessToken> GetTokenAsync(CancellationToken ct = default)
        {
            AccessToken c = _cached;
            if (c != null && _clock.UtcNow < c.ExpiresAt - TimeSpan.FromSeconds(60)) return c;
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                c = _cached;
                if (c != null && _clock.UtcNow < c.ExpiresAt - TimeSpan.FromSeconds(60)) return c;
                DateTimeOffset now = _clock.UtcNow;
                string assertion = CreateAssertion(now);
                string body = "grant_type=" + Uri.EscapeDataString("urn:ietf:params:oauth:grant-type:jwt-bearer") +
                              "&assertion=" + Uri.EscapeDataString(assertion);
                HttpReply reply = await _http.SendAsync(new HttpCall("POST", _key.TokenUri, null, body, "application/x-www-form-urlencoded"), ct).ConfigureAwait(false);
                if (!reply.IsSuccess) return new AccessToken(null, now, reply.Status);
                JsonNode json;
                try
                {
                    json = JsonNode.Parse(reply.Body);
                }
                catch (FormatException)
                {
                    return new AccessToken(null, now, 502);
                }
                string token = JsonRead.String(json, "access_token");
                long expiresIn = JsonRead.Long(json, "expires_in") ?? 0;
                if (string.IsNullOrEmpty(token) || expiresIn <= 0) return new AccessToken(null, now, 502);
                _cached = new AccessToken(token, now.AddSeconds(expiresIn), 200);
                return _cached;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>The signed JWT assertion (public for tests: verify it with the matching public key).</summary>
        public string CreateAssertion(DateTimeOffset now)
        {
            long iat = now.ToUnixTimeSeconds();
            JsonNode header = JsonNode.Object().Add("alg", "RS256").Add("typ", "JWT");
            if (!string.IsNullOrEmpty(_key.PrivateKeyId)) header.Add("kid", _key.PrivateKeyId);
            JsonNode claims = JsonNode.Object()
                .Add("iss", _key.ClientEmail)
                .Add("scope", _scope)
                .Add("aud", _key.TokenUri)
                .Add("iat", iat)
                .Add("exp", iat + 3600);
            string signingInput = Base64Url.Encode(Encoding.UTF8.GetBytes(header.ToCanonicalString())) + "." +
                                  Base64Url.Encode(Encoding.UTF8.GetBytes(claims.ToCanonicalString()));
            using (RSA rsa = RSA.Create())
            {
                rsa.ImportPkcs8PrivateKey(Pem.Decode(_key.PrivateKeyPem, "PRIVATE KEY"), out _);
                byte[] sig = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return signingInput + "." + Base64Url.Encode(sig);
            }
        }
    }

    /// <summary>RFC 4648 base64url without padding.</summary>
    public static class Base64Url
    {
        public static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(string text)
        {
            string s = text.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException("Invalid base64url length.");
            }
            return Convert.FromBase64String(s);
        }
    }

    /// <summary>Minimal PEM decoding (one block of the given label).</summary>
    public static class Pem
    {
        public static byte[] Decode(string pem, string label)
        {
            if (pem == null) throw new ArgumentNullException(nameof(pem));
            string begin = "-----BEGIN " + label + "-----";
            string end = "-----END " + label + "-----";
            int b = pem.IndexOf(begin, StringComparison.Ordinal);
            int e = pem.IndexOf(end, StringComparison.Ordinal);
            if (b < 0 || e < b) throw new FormatException("PEM block '" + label + "' not found.");
            string body = pem.Substring(b + begin.Length, e - b - begin.Length);
            var sb = new StringBuilder(body.Length);
            foreach (char ch in body)
                if (!char.IsWhiteSpace(ch)) sb.Append(ch);
            return Convert.FromBase64String(sb.ToString());
        }

        public static string Encode(byte[] der, string label)
        {
            string b64 = Convert.ToBase64String(der);
            var sb = new StringBuilder();
            sb.Append("-----BEGIN ").Append(label).Append("-----\n");
            for (int i = 0; i < b64.Length; i += 64) sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
            sb.Append("-----END ").Append(label).Append("-----\n");
            return sb.ToString();
        }
    }

    internal static class Invariant
    {
        public static string S(long v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
