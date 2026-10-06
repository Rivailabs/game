using System.Diagnostics;

namespace AstraKingdoms.Server.Ops;

/// <summary>
/// In-process service timings for readiness and load checks: how long the service took to handle a
/// player command (validation, rules engine, persistence and fan-out, inside the match lock), over
/// a sliding window of recent commands. Network time is excluded by design.
/// </summary>
public sealed class ServiceMetrics
{
    private const int Window = 8192;
    private readonly double[] _ms = new double[Window];
    private readonly object _gate = new();
    private long _count;

    public long Commands
    {
        get { lock (_gate) return _count; }
    }

    public void RecordCommand(long startTimestamp)
    {
        double ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        lock (_gate) _ms[_count++ % Window] = ms;
    }

    /// <summary>Quantile (0-1) of the recent command handling times in milliseconds, or 0 with no data.</summary>
    public double Quantile(double q)
    {
        double[] copy;
        lock (_gate)
        {
            int n = (int)Math.Min(_count, Window);
            if (n == 0) return 0;
            copy = new double[n];
            Array.Copy(_ms, copy, n);
        }
        Array.Sort(copy);
        return copy[Math.Max(0, Math.Min(copy.Length - 1, (int)Math.Ceiling(q * copy.Length) - 1))];
    }
}
