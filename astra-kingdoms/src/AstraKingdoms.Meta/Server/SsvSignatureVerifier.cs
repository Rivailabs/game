using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Meta.Server
{
    /// <summary>Supplies the ad network's ECDSA P-256 verification keys (SubjectPublicKeyInfo DER) by key id.</summary>
    public interface IAdVerifierKeyProvider
    {
        Task<IReadOnlyDictionary<long, byte[]>> GetKeysAsync(bool forceRefresh, CancellationToken ct = default);
    }

    /// <summary>
    /// Fetches and caches the AdMob rewarded-ad verifier keys
    /// (<c>https://www.gstatic.com/admob/reward/verifier-keys.json</c>: <c>{"keys":[{"keyId":..,"pem":"..","base64":".."}]}</c>).
    /// Keys rotate; a refresh happens after <see cref="CacheLifetime"/> or when a callback names an unknown key id.
    /// </summary>
    public sealed class HttpAdVerifierKeyProvider : IAdVerifierKeyProvider
    {
        public const string AdMobKeysUrl = "https://www.gstatic.com/admob/reward/verifier-keys.json";

        private readonly IHttpTransport _http;
        private readonly IClock _clock;
        private readonly string _url;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private IReadOnlyDictionary<long, byte[]> _keys;
        private DateTimeOffset _fetchedAt;

        public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromHours(24);
        /// <summary>Minimum time between forced refreshes (stops unknown-key floods from hammering the endpoint).</summary>
        public TimeSpan MinRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

        public HttpAdVerifierKeyProvider(IHttpTransport http, IClock clock, string url = AdMobKeysUrl)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _url = url;
        }

        public async Task<IReadOnlyDictionary<long, byte[]>> GetKeysAsync(bool forceRefresh, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DateTimeOffset now = _clock.UtcNow;
                bool stale = _keys == null || now - _fetchedAt >= CacheLifetime;
                bool forced = forceRefresh && (_keys == null || now - _fetchedAt >= MinRefreshInterval);
                if (!stale && !forced) return _keys;
                HttpReply reply = await _http.SendAsync(new HttpCall("GET", _url), ct).ConfigureAwait(false);
                if (!reply.IsSuccess) return _keys ?? new Dictionary<long, byte[]>();
                IReadOnlyDictionary<long, byte[]> parsed = Parse(reply.Body);
                if (parsed.Count > 0)
                {
                    _keys = parsed;
                    _fetchedAt = now;
                }
                return _keys ?? parsed;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Parses the key document; entries that cannot be read are skipped.</summary>
        public static IReadOnlyDictionary<long, byte[]> Parse(string json)
        {
            var result = new Dictionary<long, byte[]>();
            JsonNode root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (FormatException)
            {
                return result;
            }
            JsonNode keys = JsonRead.Member(root, "keys");
            if (keys == null || keys.Kind != JsonKind.Array) return result;
            foreach (JsonNode k in keys.Items)
            {
                long? id = JsonRead.Long(k, "keyId");
                if (!id.HasValue) continue;
                try
                {
                    string b64 = JsonRead.String(k, "base64");
                    string pem = JsonRead.String(k, "pem");
                    byte[] der = !string.IsNullOrEmpty(b64) ? Convert.FromBase64String(b64) : Pem.Decode(pem, "PUBLIC KEY");
                    result[id.Value] = der;
                }
                catch (FormatException)
                {
                }
                catch (ArgumentNullException)
                {
                }
            }
            return result;
        }
    }

    public enum SsvVerificationStatus : byte
    {
        Verified = 0,
        Malformed = 1,
        UnknownKey = 2,
        BadSignature = 3,
        /// <summary>Timestamp outside the accepted window (replay protection in addition to idempotency).</summary>
        Stale = 4,
    }

    public sealed class SsvVerification
    {
        public SsvVerificationStatus Status { get; }
        public SsvCallback Callback { get; }

        public SsvVerification(SsvVerificationStatus status, SsvCallback callback = null)
        {
            Status = status;
            Callback = callback;
        }
    }

    /// <summary>
    /// Verifies a rewarded-ad server-side-verification callback the way AdMob documents it: the signed
    /// message is the raw query string up to (not including) <c>&amp;signature=</c>; <c>signature</c>
    /// (base64url DER ECDSA-SHA256) and <c>key_id</c> are the last two parameters.
    /// <para>UNVERIFIED against live AdMob callbacks here; tests use locally generated P-256 keys.</para>
    /// </summary>
    public sealed class SsvSignatureVerifier
    {
        private readonly IAdVerifierKeyProvider _keys;
        private readonly IClock _clock;

        public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(1);
        public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(5);

        public SsvSignatureVerifier(IAdVerifierKeyProvider keys, IClock clock)
        {
            _keys = keys ?? throw new ArgumentNullException(nameof(keys));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <param name="rawQuery">The query string exactly as received, without the leading '?'.</param>
        public async Task<SsvVerification> VerifyAsync(string rawQuery, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(rawQuery)) return new SsvVerification(SsvVerificationStatus.Malformed);
            if (rawQuery[0] == '?') rawQuery = rawQuery.Substring(1);
            int sigAt = rawQuery.IndexOf("&signature=", StringComparison.Ordinal);
            if (sigAt <= 0) return new SsvVerification(SsvVerificationStatus.Malformed);
            string message = rawQuery.Substring(0, sigAt);
            Dictionary<string, string> tail = ParseQuery(rawQuery.Substring(sigAt + 1));
            Dictionary<string, string> fields = ParseQuery(message);
            if (!tail.TryGetValue("signature", out string sigText) || !tail.TryGetValue("key_id", out string keyText) ||
                !long.TryParse(keyText, NumberStyles.None, CultureInfo.InvariantCulture, out long keyId))
                return new SsvVerification(SsvVerificationStatus.Malformed);

            byte[] der;
            try
            {
                der = Base64Url.Decode(sigText);
            }
            catch (FormatException)
            {
                return new SsvVerification(SsvVerificationStatus.Malformed);
            }
            byte[] p1363 = EcdsaSignature.DerToP1363(der, 32);
            if (p1363 == null) return new SsvVerification(SsvVerificationStatus.BadSignature);

            IReadOnlyDictionary<long, byte[]> keys = await _keys.GetKeysAsync(false, ct).ConfigureAwait(false);
            if (!keys.ContainsKey(keyId)) keys = await _keys.GetKeysAsync(true, ct).ConfigureAwait(false);
            if (!keys.TryGetValue(keyId, out byte[] spki)) return new SsvVerification(SsvVerificationStatus.UnknownKey);

            bool ok;
            try
            {
                using (ECDsa ecdsa = ECDsa.Create())
                {
                    ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
                    ok = ecdsa.VerifyData(Encoding.UTF8.GetBytes(message), p1363, HashAlgorithmName.SHA256);
                }
            }
            catch (CryptographicException)
            {
                return new SsvVerification(SsvVerificationStatus.UnknownKey);
            }
            if (!ok) return new SsvVerification(SsvVerificationStatus.BadSignature);

            fields.TryGetValue("timestamp", out string ts);
            if (!long.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out long millis))
                return new SsvVerification(SsvVerificationStatus.Malformed);
            DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(millis);
            DateTimeOffset now = _clock.UtcNow;
            if (at > now + MaxClockSkew || now - at > MaxAge) return new SsvVerification(SsvVerificationStatus.Stale);

            fields.TryGetValue("transaction_id", out string txn);
            fields.TryGetValue("user_id", out string user);
            fields.TryGetValue("custom_data", out string custom);
            fields.TryGetValue("ad_unit", out string unit);
            fields.TryGetValue("reward_item", out string item);
            fields.TryGetValue("reward_amount", out string amountText);
            int.TryParse(amountText, NumberStyles.None, CultureInfo.InvariantCulture, out int amount);
            if (string.IsNullOrEmpty(txn)) return new SsvVerification(SsvVerificationStatus.Malformed);
            return new SsvVerification(SsvVerificationStatus.Verified, new SsvCallback(txn, user, custom, unit, amount, item, at));
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in query.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = Uri.UnescapeDataString((eq < 0 ? part : part.Substring(0, eq)).Replace('+', ' '));
                string v = eq < 0 ? string.Empty : Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
                d[k] = v;
            }
            return d;
        }
    }

    /// <summary>
    /// ECDSA signature encodings. .NET's netstandard2.1 <see cref="ECDsa.VerifyData(byte[], byte[], HashAlgorithmName)"/>
    /// expects IEEE P1363 (r || s); ad networks send ASN.1 DER <c>SEQUENCE { INTEGER r, INTEGER s }</c>.
    /// </summary>
    public static class EcdsaSignature
    {
        /// <summary>DER to fixed-width r||s; null when the DER is malformed.</summary>
        public static byte[] DerToP1363(byte[] der, int fieldBytes)
        {
            if (der == null || der.Length < 8 || der[0] != 0x30) return null;
            int pos = 1;
            if (!ReadLength(der, ref pos, out int seqLen) || pos + seqLen != der.Length) return null;
            byte[] r = ReadInteger(der, ref pos);
            byte[] s = ReadInteger(der, ref pos);
            if (r == null || s == null || pos != der.Length) return null;
            var output = new byte[fieldBytes * 2];
            if (!CopyUnsigned(r, output, 0, fieldBytes) || !CopyUnsigned(s, output, fieldBytes, fieldBytes)) return null;
            return output;
        }

        /// <summary>Fixed-width r||s to DER (used by tests to build network-shaped signatures).</summary>
        public static byte[] P1363ToDer(byte[] p1363)
        {
            int half = p1363.Length / 2;
            byte[] r = Integer(p1363, 0, half);
            byte[] s = Integer(p1363, half, half);
            var body = new List<byte>();
            body.AddRange(r);
            body.AddRange(s);
            var der = new List<byte> { 0x30 };
            der.AddRange(Length(body.Count));
            der.AddRange(body);
            return der.ToArray();
        }

        private static byte[] Integer(byte[] src, int offset, int count)
        {
            int start = offset;
            while (start < offset + count - 1 && src[start] == 0) start++;
            bool pad = (src[start] & 0x80) != 0;
            var v = new List<byte> { 0x02 };
            int len = offset + count - start + (pad ? 1 : 0);
            v.AddRange(Length(len));
            if (pad) v.Add(0);
            for (int i = start; i < offset + count; i++) v.Add(src[i]);
            return v.ToArray();
        }

        private static byte[] Length(int len) => len < 0x80 ? new[] { (byte)len } : new[] { (byte)0x81, (byte)len };

        private static bool ReadLength(byte[] d, ref int pos, out int len)
        {
            len = 0;
            if (pos >= d.Length) return false;
            byte b = d[pos++];
            if (b < 0x80)
            {
                len = b;
                return true;
            }
            if (b != 0x81 || pos >= d.Length) return false;
            len = d[pos++];
            return len >= 0x80;
        }

        private static byte[] ReadInteger(byte[] d, ref int pos)
        {
            if (pos >= d.Length || d[pos++] != 0x02) return null;
            if (!ReadLength(d, ref pos, out int len) || len == 0 || pos + len > d.Length) return null;
            var v = new byte[len];
            Array.Copy(d, pos, v, 0, len);
            pos += len;
            return v;
        }

        private static bool CopyUnsigned(byte[] value, byte[] output, int offset, int width)
        {
            int start = 0;
            while (start < value.Length - 1 && value[start] == 0) start++;
            int len = value.Length - start;
            if (len > width) return false;
            Array.Copy(value, start, output, offset + width - len, len);
            return true;
        }
    }
}
