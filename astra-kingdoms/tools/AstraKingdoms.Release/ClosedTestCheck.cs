using System.Text.Json.Nodes;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>
/// <c>closed-test-check</c> (ticket 79; plan "Store deletion and purchase readiness" [S26]): reads the
/// tester tracker (<c>release/closed-test/tester-tracker.csv</c>) and decides whether the Play
/// closed-test requirement for new personal developer accounts is met: <b>at least 12 testers opted
/// in continuously for 14 days</b>. "Twenty invitations or fourteen calendar days alone do not prove
/// eligibility" (plan), so only testers with all of day01..day14 marked <c>Y</c> and no opt-out count.
/// It also reports the cohort mix so friends, colleagues and contributors stay identifiable.
/// </summary>
public static class ClosedTestCheck
{
    public const int RequiredTesters = 12;
    public const int RequiredDays = 14;
    public static readonly string[] Cohorts = { "friend", "colleague", "contributor", "community", "recruited" };

    public sealed class Tester
    {
        public string Id = "", Cohort = "", OptedOut = "";
        public bool[] Days = new bool[RequiredDays];
        public bool AnyEntry;
        public bool Continuous => Days.All(d => d) && OptedOut.Length == 0;
    }

    public static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString());
        return cells;
    }

    public static List<Tester> Parse(string csv)
    {
        string[] lines = csv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new FormatException("Empty tracker.");
        List<string> header = SplitCsvLine(lines[0]);
        int Col(string name)
        {
            int i = header.IndexOf(name);
            if (i < 0) throw new FormatException("Tracker has no '" + name + "' column.");
            return i;
        }
        int id = Col("tester_id"), cohort = Col("cohort"), optOut = Col("opted_out_utc");
        int[] days = Enumerable.Range(1, RequiredDays).Select(d => Col("day" + d.ToString("00", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var testers = new List<Tester>();
        foreach (string line in lines.Skip(1))
        {
            List<string> c = SplitCsvLine(line);
            if (c.Count != header.Count) throw new FormatException("Row '" + line + "' has " + c.Count + " cells; header has " + header.Count + ".");
            var t = new Tester { Id = c[id].Trim(), Cohort = c[cohort].Trim().ToLowerInvariant(), OptedOut = c[optOut].Trim() };
            for (int d = 0; d < RequiredDays; d++)
            {
                string v = c[days[d]].Trim();
                t.Days[d] = v.Equals("Y", StringComparison.OrdinalIgnoreCase);
                t.AnyEntry |= v.Length > 0;
            }
            testers.Add(t);
        }
        return testers;
    }

    public static GateReport Evaluate(List<Tester> testers)
    {
        var r = new GateReport("closed test eligibility");
        int continuous = testers.Count(t => t.Continuous);
        int started = testers.Count(t => t.AnyEntry);
        if (started == 0) r.Add("testers", GateStatus.Incomplete, "no opt-in days recorded yet (" + testers.Count + " tracker rows)");
        else if (continuous >= RequiredTesters)
            r.Add("testers", GateStatus.Pass, continuous + " testers opted in continuously for " + RequiredDays + " days (required " + RequiredTesters + "); confirm against Play Console before applying for production access");
        else
            r.Add("testers", GateStatus.Incomplete, continuous + " of " + RequiredTesters + " required testers have " + RequiredDays + " continuous opted-in days (" + started + " started)");
        foreach (Tester t in testers.Where(t => t.AnyEntry && !t.Continuous))
        {
            int firstGap = Array.IndexOf(t.Days, false);
            r.Add("tester." + t.Id, GateStatus.Warn, t.OptedOut.Length > 0 ? "opted out " + t.OptedOut : "not opted in on day " + (firstGap + 1));
        }
        foreach (Tester t in testers.Where(t => t.AnyEntry && !Cohorts.Contains(t.Cohort)))
            r.Add("cohort." + t.Id, GateStatus.Fail, "cohort '" + t.Cohort + "' must be one of " + string.Join("/", Cohorts) + " so non-representative feedback stays identifiable");
        return r;
    }

    public static JsonObject CohortMix(List<Tester> testers) =>
        new(Cohorts.Select(c => new KeyValuePair<string, JsonNode>(c, testers.Count(t => t.AnyEntry && t.Cohort == c))));

    public static int Cli(CliArgs a)
    {
        List<Tester> testers = Parse(File.ReadAllText(a.Require("tracker")));
        return GateJson.Emit(Evaluate(testers), a.Get("out"), new JsonObject { ["cohorts"] = CohortMix(testers) });
    }
}
