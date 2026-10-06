using System.Security.Cryptography;
using System.Text;

namespace AstraKingdoms.Server.Identity;

/// <summary>An authenticated account. <see cref="Uid"/> is the only seat authority: clients never name a player.</summary>
public sealed record VerifiedIdentity(string Uid, string Issuer)
{
    /// <summary>Pseudonymous reference for logs, audit and the client (never the raw account ID).</summary>
    public string Ref => PlayerRef.Of(Uid);
}

/// <summary>Result of verifying a bearer token. Failure reasons are for logs only, never echoed to clients.</summary>
public sealed record IdentityResult(VerifiedIdentity Identity, string Failure)
{
    public bool Ok => Identity != null;
    public static IdentityResult Success(VerifiedIdentity id) => new(id, null);
    public static IdentityResult Fail(string reason) => new(null, reason);
}

/// <summary>Pluggable identity check (ticket 49): Firebase ID tokens in production, dev tokens locally.</summary>
public interface IIdentityVerifier
{
    Task<IdentityResult> VerifyAsync(string token, CancellationToken ct);
}

/// <summary>Stable pseudonymous player references (first 16 hex digits of SHA-256 over a domain tag and the UID).</summary>
public static class PlayerRef
{
    public static string Of(string uid)
    {
        if (uid == null) return "-";
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("AK-PLAYER-REF/1\n" + uid));
        return "p_" + Convert.ToHexString(h, 0, 8).ToLowerInvariant();
    }
}

/// <summary>
/// Development verifier: accepts "dev:&lt;name&gt;" where name is 1-64 characters of [A-Za-z0-9_-].
/// Registered only in Dev auth mode, which Program refuses outside the Development environment
/// unless explicitly allowed.
/// </summary>
public sealed class DevIdentityVerifier : IIdentityVerifier
{
    public const string Prefix = "dev:";

    public Task<IdentityResult> VerifyAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return Task.FromResult(IdentityResult.Fail("not a dev token"));
        string name = token.Substring(Prefix.Length);
        if (name.Length == 0 || name.Length > 64) return Task.FromResult(IdentityResult.Fail("dev name length"));
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')) return Task.FromResult(IdentityResult.Fail("dev name characters"));
        return Task.FromResult(IdentityResult.Success(new VerifiedIdentity("dev-" + name, "dev")));
    }
}
