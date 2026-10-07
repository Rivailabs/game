using System;
using System.Collections.Generic;

namespace AstraKingdoms.Client.Automation
{
    /// <summary>
    /// Frame-time and memory samples for the plan's provisional device budgets (p95 at most 35 ms
    /// at 30 fps; p99 and long stalls recorded separately). Samples marked as loading are excluded
    /// only where the UI shows loading. Memory values are what the engine's Profiler APIs report;
    /// Android total PSS must still come from the device service (dumpsys meminfo), never by adding
    /// overlapping counters.
    /// </summary>
    public sealed class FrameStats
    {
        public const double LongStallMs = 100.0;
        private readonly List<double> _frameMs = new List<double>(8192);

        public int Count => _frameMs.Count;
        public int ExcludedLoadingFrames { get; private set; }
        public long PeakAllocatedBytes { get; private set; }
        public long PeakReservedBytes { get; private set; }
        public long PeakManagedBytes { get; private set; }
        public long PeakGraphicsDriverBytes { get; private set; }

        public void AddFrame(double milliseconds, bool loading = false)
        {
            if (double.IsNaN(milliseconds) || milliseconds < 0) return;
            if (loading)
            {
                ExcludedLoadingFrames++;
                return;
            }
            _frameMs.Add(milliseconds);
        }

        public void AddMemory(long allocated, long reserved, long managed, long graphicsDriver)
        {
            PeakAllocatedBytes = Math.Max(PeakAllocatedBytes, allocated);
            PeakReservedBytes = Math.Max(PeakReservedBytes, reserved);
            PeakManagedBytes = Math.Max(PeakManagedBytes, managed);
            PeakGraphicsDriverBytes = Math.Max(PeakGraphicsDriverBytes, graphicsDriver);
        }

        /// <summary>Nearest-rank percentile (p in (0,100]); 0 when there are no samples.</summary>
        public double Percentile(double p)
        {
            if (_frameMs.Count == 0) return 0;
            if (p <= 0 || p > 100) throw new ArgumentOutOfRangeException(nameof(p));
            var sorted = new List<double>(_frameMs);
            sorted.Sort();
            int rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, rank - 1))];
        }

        public double Mean
        {
            get
            {
                if (_frameMs.Count == 0) return 0;
                double sum = 0;
                foreach (double v in _frameMs) sum += v;
                return sum / _frameMs.Count;
            }
        }

        public double Max
        {
            get
            {
                double m = 0;
                foreach (double v in _frameMs) m = Math.Max(m, v);
                return m;
            }
        }

        public int CountAbove(double ms)
        {
            int n = 0;
            foreach (double v in _frameMs) if (v > ms) n++;
            return n;
        }
    }
}
