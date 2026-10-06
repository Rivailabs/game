using System.Globalization;
using System.Text.Json.Nodes;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>
/// <c>version-policy-check</c> (ticket 81; plan "Release and rollback procedure": "Maintain a
/// compatible server window for supported client versions and a controlled minimum-version
/// policy"): validates an <c>AK-MIN-VERSION-POLICY/1</c> document and, given the previous policy,
/// the transition. Raising the minimum version or retiring a rules version must not strand active
/// matches: enforcement must start at least <c>grace_minutes</c> (&gt;= the longest match, proposed
/// 60 min) after the change, and only new matches are affected.
/// </summary>
public static class VersionPolicyCheck
{
    public const string Format = "AK-MIN-VERSION-POLICY/1";
    /// <summary>PROPOSED: longer than the longest shared-phone or online match (8 rounds plus timeouts).</summary>
    public const int MinGraceMinutes = 60;

    public sealed class Policy
    {
        public DateTimeOffset Changed, EnforceAfter;
        public long Latest, Recommended, Minimum;
        public int GraceMinutes;
        public List<long> Blocked = new();
        public List<string> RulesVersions = new();
        public int CompatibilityDays;
    }

    public static Policy Parse(JsonNode n)
    {
        if (Util.Str(n, "format") != Format) throw new FormatException("Not an " + Format + " document.");
        DateTimeOffset Time(string key) => DateTimeOffset.Parse(Util.Str(n, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return new Policy
        {
            Changed = Time("changed_utc"),
            EnforceAfter = Time("enforce_after_utc"),
            Latest = Util.LongOrNull(n, "latest_version_code") ?? 0,
            Recommended = Util.LongOrNull(n, "recommended_version_code") ?? 0,
            Minimum = Util.LongOrNull(n, "minimum_version_code") ?? 0,
            GraceMinutes = (int)(Util.LongOrNull(n, "grace_minutes") ?? 0),
            Blocked = (n["blocked_version_codes"]?.AsArray() ?? new JsonArray()).Select(x => x!.GetValue<long>()).ToList(),
            RulesVersions = (n["compatible_rules_versions"]?.AsArray() ?? new JsonArray()).Select(x => x!.GetValue<string>()).ToList(),
            CompatibilityDays = (int)(Util.LongOrNull(n, "server_compatibility_window_days") ?? 0),
        };
    }

    public static GateReport Evaluate(Policy p, Policy previous)
    {
        var r = new GateReport("minimum-version policy");
        if (p.Minimum < 1 || p.Minimum > p.Recommended || p.Recommended > p.Latest)
            r.Add("order", GateStatus.Fail, "need 1 <= minimum (" + p.Minimum + ") <= recommended (" + p.Recommended + ") <= latest (" + p.Latest + ")");
        else r.Add("order", GateStatus.Pass, "minimum " + p.Minimum + " <= recommended " + p.Recommended + " <= latest " + p.Latest);
        if (p.Blocked.Contains(p.Latest)) r.Add("blocked", GateStatus.Fail, "the latest version code is blocked");
        if (p.RulesVersions.Count == 0) r.Add("rules", GateStatus.Fail, "no compatible rules version");
        if (p.GraceMinutes < MinGraceMinutes) r.Add("grace", GateStatus.Fail, "grace_minutes " + p.GraceMinutes + " < " + MinGraceMinutes + " (longest match)");
        if (p.CompatibilityDays < 7) r.Add("window", GateStatus.Warn, "server compatibility window of " + p.CompatibilityDays + " days is short for players who update slowly");
        if (p.EnforceAfter < p.Changed) r.Add("enforce", GateStatus.Fail, "enforce_after_utc is before changed_utc");

        if (previous != null)
        {
            bool raised = p.Minimum > previous.Minimum;
            var retired = previous.RulesVersions.Except(p.RulesVersions, StringComparer.Ordinal).ToList();
            var newlyBlocked = p.Blocked.Except(previous.Blocked).ToList();
            if (raised || retired.Count > 0 || newlyBlocked.Count > 0)
            {
                TimeSpan lead = p.EnforceAfter - p.Changed;
                string what = (raised ? "minimum " + previous.Minimum + " -> " + p.Minimum + "; " : "") +
                              (retired.Count > 0 ? "retired rules " + string.Join(",", retired) + "; " : "") +
                              (newlyBlocked.Count > 0 ? "blocked " + string.Join(",", newlyBlocked) + "; " : "");
                r.Add("transition", lead.TotalMinutes >= Math.Max(p.GraceMinutes, MinGraceMinutes) ? GateStatus.Pass : GateStatus.Fail,
                    what + "enforced " + lead.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min after the change (needs >= " +
                    Math.Max(p.GraceMinutes, MinGraceMinutes) + " so active matches finish on their pinned version)");
            }
            if (p.Latest < previous.Latest)
                r.Add("rollback", GateStatus.Warn, "latest version code went down (" + previous.Latest + " -> " + p.Latest +
                    "): a client rollback needs another store release; devices do not revert automatically");
        }
        return r;
    }

    public static int Cli(CliArgs a)
    {
        Policy p = Parse(Util.ReadJson(a.Require("policy")));
        Policy prev = a.Get("previous") is string prevPath ? Parse(Util.ReadJson(prevPath)) : null;
        return GateJson.Emit(Evaluate(p, prev), a.Get("out"));
    }
}
