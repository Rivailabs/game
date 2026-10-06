using System.Globalization;

namespace AstraKingdoms.Sim;

/// <summary>Binary outcome tally with a Wilson score interval.</summary>
public sealed class Tally
{
    public long Wins, Losses, Draws;
    public long Decisive => Wins + Losses;
    public long Total => Wins + Losses + Draws;

    public void Add(int sign)
    {
        if (sign > 0) Wins++;
        else if (sign < 0) Losses++;
        else Draws++;
    }

    public void Add(Tally other)
    {
        Wins += other.Wins;
        Losses += other.Losses;
        Draws += other.Draws;
    }

    /// <summary>Win share among decisive outcomes.</summary>
    public double Rate => Decisive == 0 ? double.NaN : (double)Wins / Decisive;

    public (double Lo, double Hi) Wilson(double z = Stats.Z95) => Stats.Wilson(Wins, Decisive, z);
}

/// <summary>Statistics helpers (presentation-side doubles; never used by the rules).</summary>
public static class Stats
{
    public const double Z95 = 1.959963984540054;

    /// <summary>Below this many decisive outcomes a stratum is reported as insufficient.</summary>
    public const int MinDecisive = 30;

    public static (double Lo, double Hi) Wilson(long k, long n, double z = Z95)
    {
        if (n == 0) return (double.NaN, double.NaN);
        double p = (double)k / n;
        double z2 = z * z;
        double denom = 1 + z2 / n;
        double center = (p + z2 / (2.0 * n)) / denom;
        double half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / denom;
        return (Math.Max(0, center - half), Math.Min(1, center + half));
    }

    /// <summary>
    /// Two-sided critical value for a family of <paramref name="tests"/> comparisons at overall
    /// alpha 0.05 (Bonferroni): z = Phi^-1(1 - 0.025 / tests).
    /// </summary>
    public static double BonferroniZ(int tests) => InverseNormal(1 - 0.025 / Math.Max(1, tests));

    /// <summary>Acklam's rational approximation of the standard normal quantile (|error| &lt; 1.2e-9).</summary>
    public static double InverseNormal(double p)
    {
        if (p <= 0 || p >= 1) throw new ArgumentOutOfRangeException(nameof(p));
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double low = 0.02425, high = 1 - low;
        double q, r;
        if (p < low)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > high)
        {
            q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        q = p - 0.5;
        r = q * q;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }

    public static string Pct(double v) => double.IsNaN(v) ? "n/a" : (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    public static string Interval((double Lo, double Hi) ci) =>
        double.IsNaN(ci.Lo) ? "n/a" : Pct(ci.Lo) + " - " + Pct(ci.Hi);

    public static string F(double v) => double.IsNaN(v) ? "" : v.ToString("F4", CultureInfo.InvariantCulture);

    public static string I(long v) => v.ToString(CultureInfo.InvariantCulture);
}
