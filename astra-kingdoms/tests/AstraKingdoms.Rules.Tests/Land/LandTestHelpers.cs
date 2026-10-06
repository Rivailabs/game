using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Tests.Land;

/// <summary>Test-only deterministic RNG (SplitMix64). Never used by the rules assembly.</summary>
internal sealed class TestRng
{
    private ulong _state;

    public TestRng(ulong seed) => _state = seed;

    public ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform-enough integer in [min, max] for tests.</summary>
    public int Range(int min, int max) => min + (int)(Next() % (ulong)(max - min + 1));
}

internal static class LandTestHelpers
{
    public static CellPoint P(int x, int y) => new(x, y);

    public static CellPoint[] Rect(int x0, int y0, int x1, int y1) =>
        new[] { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1) };

    public static bool IsFourConnected(IReadOnlyList<int> cells)
    {
        if (cells.Count == 0) return true;
        var set = new HashSet<int>(cells);
        var seen = new HashSet<int> { cells[0] };
        var queue = new Queue<int>();
        queue.Enqueue(cells[0]);
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            int x = Board.X(c), y = Board.Y(c);
            for (int d = 0; d < 4; d++)
            {
                int nx = x + Board.NeighbourDx[d], ny = y + Board.NeighbourDy[d];
                if (!Board.IsOnGrid(nx, ny)) continue;
                int n = Board.CellId(nx, ny);
                if (set.Contains(n) && seen.Add(n)) queue.Enqueue(n);
            }
        }
        return seen.Count == set.Count;
    }

    /// <summary>Largest scale (quarters) whose envelope fits 2Q, assuming monotone growth (star-shaped cards).</summary>
    public static int MaxFittingScale(CardId card, int rotation, int quota)
    {
        int lo = 0, hi = RulesConstants.MaxScaleQuarters;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (CardEnvelope.FitsAllowance(card, mid, rotation, quota)) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }
}
