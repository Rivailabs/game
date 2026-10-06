using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AstraKingdoms.Server.Security;

/// <summary>
/// Token bucket per key (ticket 55): <see cref="Capacity"/> tokens, refilled continuously at
/// <see cref="RefillPerSecond"/>. Keys are identities (realtime and authenticated HTTP) or IP
/// addresses (anonymous HTTP). Time comes from <see cref="TimeProvider"/>, so tests are exact.
/// </summary>
public sealed class TokenBucketLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastTicks;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public TokenBucketLimiter(TimeProvider time, int capacity, double refillPerSecond)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (refillPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(refillPerSecond));
        _time = time;
        Capacity = capacity;
        RefillPerSecond = refillPerSecond;
    }

    public int Capacity { get; }
    public double RefillPerSecond { get; }

    /// <summary>Takes one token for <paramref name="key"/>; false when the bucket is empty.</summary>
    public bool TryTake(string key)
    {
        long now = _time.GetTimestamp();
        Bucket b = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = Capacity, LastTicks = now });
        lock (b)
        {
            double elapsed = (now - b.LastTicks) / (double)_time.TimestampFrequency;
            if (elapsed > 0)
            {
                b.Tokens = Math.Min(Capacity, b.Tokens + elapsed * RefillPerSecond);
                b.LastTicks = now;
            }
            if (b.Tokens < 1) return false;
            b.Tokens -= 1;
            return true;
        }
    }

    /// <summary>Drops idle full buckets (called periodically so memory stays bounded).</summary>
    public void Sweep()
    {
        long now = _time.GetTimestamp();
        foreach (KeyValuePair<string, Bucket> kv in _buckets)
        {
            Bucket b = kv.Value;
            lock (b)
            {
                double elapsed = (now - b.LastTicks) / (double)_time.TimestampFrequency;
                if (b.Tokens + elapsed * RefillPerSecond >= Capacity) _buckets.TryRemove(kv.Key, out _);
            }
        }
    }
}

/// <summary>
/// Redaction for anything that might reach logs from outside: bearer tokens, JWT-shaped strings and
/// dev tokens are replaced. The service never logs message payloads, so unrevealed choices cannot
/// reach logs; this guards exception texts and headers.
/// </summary>
public static class LogRedaction
{
    private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*", RegexOptions.Compiled);
    private static readonly Regex Bearer = new(@"(?i)bearer\s+\S+", RegexOptions.Compiled);
    private static readonly Regex Dev = new(@"dev:[A-Za-z0-9_\-]+", RegexOptions.Compiled);

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Bearer.Replace(text, "Bearer [redacted]");
        text = Jwt.Replace(text, "[redacted-jwt]");
        return Dev.Replace(text, "dev:[redacted]");
    }
}
