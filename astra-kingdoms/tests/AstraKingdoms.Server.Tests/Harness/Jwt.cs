using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AstraKingdoms.Server.Identity;

namespace AstraKingdoms.Server.Tests.Harness;

/// <summary>Locally generated RSA keys standing in for Google's Firebase signing keys.</summary>
internal sealed class StaticKeySource : ISigningKeySource
{
    public readonly Dictionary<string, RSA> Keys = new(StringComparer.Ordinal);
    public int Refreshes;

    public Task<IReadOnlyDictionary<string, RSA>> GetKeysAsync(bool refresh, CancellationToken ct)
    {
        if (refresh) Refreshes++;
        return Task.FromResult<IReadOnlyDictionary<string, RSA>>(Keys);
    }
}

/// <summary>Builds RS256 JWTs shaped like Firebase ID tokens.</summary>
internal static class Jwt
{
    public const string ProjectId = "astra-test";

    public static string Sign(RSA key, string kid, Dictionary<string, object> claims, string alg = "RS256")
    {
        string header = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["alg"] = alg, ["kid"] = kid, ["typ"] = "JWT" }));
        string payload = B64(JsonSerializer.SerializeToUtf8Bytes(claims));
        byte[] sig = key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + B64(sig);
    }

    public static Dictionary<string, object> Claims(DateTimeOffset now, string sub = "firebase-user-1", string aud = ProjectId,
        string iss = "https://securetoken.google.com/" + ProjectId, int expiresInSeconds = 3600) => new()
    {
        ["iss"] = iss,
        ["aud"] = aud,
        ["sub"] = sub,
        ["iat"] = now.ToUnixTimeSeconds() - 10,
        ["auth_time"] = now.ToUnixTimeSeconds() - 20,
        ["exp"] = now.ToUnixTimeSeconds() + expiresInSeconds,
    };

    private static string B64(byte[] b) => Base64Url.Encode(b);
}
