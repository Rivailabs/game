using System;

namespace AstraKingdoms.Meta.Common
{
    /// <summary>
    /// Source of "now" for every time-dependent meta rule (daily task days, claim expiry, ad tickets,
    /// purchase acknowledgement deadlines, retention windows). Services never read the system clock
    /// directly so boundary cases can be tested exactly.
    /// </summary>
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }

    /// <summary>The real UTC clock.</summary>
    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>A clock that only moves when told to (tests, replays of support cases).</summary>
    public sealed class ManualClock : IClock
    {
        private readonly object _gate = new object();
        private DateTimeOffset _now;

        public ManualClock(DateTimeOffset start) => _now = start.ToUniversalTime();

        public DateTimeOffset UtcNow
        {
            get { lock (_gate) return _now; }
        }

        public void Set(DateTimeOffset value)
        {
            lock (_gate) _now = value.ToUniversalTime();
        }

        public void Advance(TimeSpan delta)
        {
            lock (_gate) _now = _now.Add(delta);
        }
    }
}
