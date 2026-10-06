using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>
/// <c>repro-compare</c> (ticket 76): compares two builds of the same commit (two AAB/APK files, or
/// two output folders) and lists every entry whose bytes differ. Signature files can be ignored
/// (<c>--ignore-signing</c>) because the debug-signed integration build is re-signed by Forge's
/// separate signer. Known, reviewed sources of nondeterminism go in an allowlist with a reason;
/// they become WARN. Anything else FAILs, so "same filename" is never mistaken for "same artifact"
/// (plan: "Signing or uploading a different file because it has the same filename is not permitted").
/// </summary>
public static class ReproCompare
{
    public static readonly string[] SigningPatterns =
    {
        "META-INF/*.SF", "META-INF/*.RSA", "META-INF/*.EC", "META-INF/*.DSA", "META-INF/MANIFEST.MF",
        "META-INF/BNDLTOOL.SF", "META-INF/BNDLTOOL.RSA", "stamp-cert-sha256",
    };

    public sealed class Allowed
    {
        public string Pattern = "";
        public string Reason = "";
    }

    public sealed class Options
    {
        public string A;
        public string B;
        public bool IgnoreSigning;
        public List<Allowed> Allowlist = new();
    }

    public sealed class Result
    {
        public GateReport Report;
        public List<string> Differing = new();
        public List<string> OnlyInA = new();
        public List<string> OnlyInB = new();
        public List<(string Entry, string Reason)> Accepted = new();
    }

    public static Regex Glob(string pattern)
    {
        string rx = "^" + Regex.Escape(pattern).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]").Replace("\u0001", ".*") + "$";
        return new Regex(rx, RegexOptions.CultureInvariant);
    }

    public static Dictionary<string, string> Inventory(string path)
    {
        if (Directory.Exists(path))
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(path, f).Replace('\\', '/');
                if (IsArchive(f))
                    foreach (KeyValuePair<string, string> kv in ArchiveInventory(f)) d[rel + "!" + kv.Key] = kv.Value;
                else
                    d[rel] = Util.Sha256File(f);
            }
            return d;
        }
        if (File.Exists(path)) return ArchiveInventory(path);
        throw new FileNotFoundException("Build output not found: " + path);
    }

    private static bool IsArchive(string f) => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".aab", StringComparison.OrdinalIgnoreCase) ||
                                               f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".apks", StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, string> ArchiveInventory(string file)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        using ZipArchive zip = ZipFile.OpenRead(file);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            using Stream s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            d[e.FullName] = Util.Sha256Bytes(ms.ToArray());
        }
        return d;
    }

    public static Result Run(Options o)
    {
        var res = new Result { Report = new GateReport("build reproducibility") };
        GateReport r = res.Report;
        if (File.Exists(o.A) && File.Exists(o.B) && Util.Sha256File(o.A) == Util.Sha256File(o.B))
        {
            r.Add("artifact", GateStatus.Pass, "the two artifacts are bit-identical (" + Util.Sha256File(o.A) + ")");
            return res;
        }
        Dictionary<string, string> a = Inventory(o.A), b = Inventory(o.B);
        var ignore = (o.IgnoreSigning ? SigningPatterns : Array.Empty<string>()).Select(p => Glob(p)).ToList();
        var allow = o.Allowlist.Select(x => (Rx: Glob(x.Pattern), x.Reason)).ToList();
        bool Ignored(string name)
        {
            string inner = name.Contains('!') ? name[(name.LastIndexOf('!') + 1)..] : name;
            return ignore.Any(rx => rx.IsMatch(inner) || rx.IsMatch(name));
        }

        foreach (string k in a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (Ignored(k)) continue;
            bool inA = a.TryGetValue(k, out string ha), inB = b.TryGetValue(k, out string hb);
            if (inA && inB && ha == hb) continue;
            (Regex Rx, string Reason) hit = allow.FirstOrDefault(x => x.Rx.IsMatch(k));
            if (hit.Rx != null)
            {
                res.Accepted.Add((k, hit.Reason));
                continue;
            }
            if (!inA) res.OnlyInB.Add(k);
            else if (!inB) res.OnlyInA.Add(k);
            else res.Differing.Add(k);
        }
        int compared = a.Keys.Union(b.Keys).Count(k => !Ignored(k));
        foreach (string k in res.Differing) r.Add("differs", GateStatus.Fail, k);
        foreach (string k in res.OnlyInA) r.Add("only_in_a", GateStatus.Fail, k);
        foreach (string k in res.OnlyInB) r.Add("only_in_b", GateStatus.Fail, k);
        foreach ((string k, string why) in res.Accepted) r.Add("accepted", GateStatus.Warn, k + " differs (allowlisted: " + why + ")");
        if (res.Differing.Count + res.OnlyInA.Count + res.OnlyInB.Count == 0)
            r.Add("entries", GateStatus.Pass, compared + " entries identical" + (o.IgnoreSigning ? " (signature files ignored)" : "") +
                                              (File.Exists(o.A) ? "; container bytes differ (entry order, timestamps or signatures)" : ""));
        return res;
    }

    public static List<Allowed> LoadAllowlist(string path)
    {
        if (path == null) return new List<Allowed>();
        JsonNode root = Util.ReadJson(path);
        if (Util.Str(root, "format") != "AK-REPRO-ALLOWLIST/1") throw new FormatException("Not an AK-REPRO-ALLOWLIST/1 document.");
        return root["entries"]!.AsArray().Select(e => new Allowed { Pattern = Util.Str(e, "pattern"), Reason = Util.Str(e, "reason") }).ToList();
    }

    public static int Cli(CliArgs a)
    {
        Result res = Run(new Options
        {
            A = a.Require("a"),
            B = a.Require("b"),
            IgnoreSigning = a.Flag("ignore-signing"),
            Allowlist = LoadAllowlist(a.Get("allowlist")),
        });
        var extra = new JsonObject
        {
            ["a"] = a.Require("a"),
            ["b"] = a.Require("b"),
            ["nondeterministic_entries"] = new JsonArray(res.Differing.Concat(res.OnlyInA).Concat(res.OnlyInB).Select(x => (JsonNode)x).ToArray()),
        };
        return GateJson.Emit(res.Report, a.Get("out"), extra);
    }
}
