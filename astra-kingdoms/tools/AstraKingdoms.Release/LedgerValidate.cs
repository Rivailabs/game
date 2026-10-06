using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AstraKingdoms.Client.Assets;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>
/// <c>ledger-validate</c> (tickets 65-72): validates one or more <c>AK-ASSET-LEDGER/1</c> files with the
/// client's own rules (<see cref="AssetLedger.Validate"/>, linked source), then checks what the
/// client cannot: IDs unique across files, every file-backed row hashes to the committed bytes, every
/// asset file under <c>Assets/Art</c> is listed, and (with <c>--require-approved</c>, for a store
/// release) that no placeholder or planned row remains. It can export the approved rows in Forge's
/// release-package provenance format (<c>forge release package --assets ... --provenance ...</c>).
/// </summary>
public static partial class LedgerValidate
{
    public static readonly string[] AssetExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".exr", ".wav", ".ogg", ".mp3", ".aif", ".aiff", ".fbx", ".obj", ".blend", ".ttf", ".otf", ".anim", ".mp4" };

    public sealed class Options
    {
        public List<string> Ledgers = new();
        /// <summary>The Unity project folder (contains Assets/). Null skips file checks.</summary>
        public string UnityRoot;
        public bool RequireApproved;
        public bool IncludePlaceholdersInExport;
    }

    public sealed class Result
    {
        public GateReport Report;
        public List<(string LedgerFile, LedgerEntry Entry)> Entries = new();
    }

    public static Result Run(Options o)
    {
        var res = new Result { Report = new GateReport("asset ledger") };
        GateReport r = res.Report;
        if (o.Ledgers.Count == 0) r.Add("input", GateStatus.Incomplete, "no ledger files given");
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in o.Ledgers)
        {
            AssetLedger ledger;
            try
            {
                ledger = AssetLedger.Parse(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                r.Add("parse", GateStatus.Fail, file + ": " + ex.Message);
                continue;
            }
            IReadOnlyList<string> problems = ledger.Validate();
            foreach (string p in problems) r.Add("rules", GateStatus.Fail, Path.GetFileName(file) + ": " + p);
            foreach (LedgerEntry e in ledger.Entries)
            {
                res.Entries.Add((file, e));
                if (e.Id == null) continue;
                if (seen.TryGetValue(e.Id, out string other)) r.Add("unique", GateStatus.Fail, e.Id + ": listed in both " + other + " and " + Path.GetFileName(file));
                else seen[e.Id] = Path.GetFileName(file);
            }
            var counts = ledger.Entries.GroupBy(e => e.Status).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count());
            r.Add("summary", GateStatus.Pass, Path.GetFileName(file) + ": " + ledger.Entries.Count + " rows (" + string.Join(", ", counts) + ")");
        }

        if (o.UnityRoot != null) CheckFiles(o.UnityRoot, res);
        if (o.RequireApproved)
        {
            foreach ((string _, LedgerEntry e) in res.Entries)
                if (e.Status is LedgerStatus.Placeholder or LedgerStatus.Planned)
                    r.Add("store-release", GateStatus.Fail, e.Id + " is " + e.Status + "; a store release needs every shipped asset Approved (or the row removed with the asset)");
        }
        else
        {
            int pending = res.Entries.Count(x => x.Entry.Status is LedgerStatus.Placeholder or LedgerStatus.Planned);
            if (pending > 0) r.Add("pending", GateStatus.Warn, pending + " placeholder/planned row(s); acceptable for internal and closed tests only");
        }
        return res;
    }

    /// <summary>Unity asset path a row refers to ("Assets/..." or "generated:Assets/..."), or null.</summary>
    public static string FilePath(LedgerEntry e)
    {
        if (e.Path == null) return null;
        string p = e.Path.StartsWith("generated:", StringComparison.Ordinal) ? e.Path["generated:".Length..] : e.Path;
        return p.StartsWith("Assets/", StringComparison.Ordinal) ? p : null;
    }

    private static void CheckFiles(string unityRoot, Result res)
    {
        GateReport r = res.Report;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string _, LedgerEntry e) in res.Entries)
        {
            string rel = FilePath(e);
            if (rel == null)
            {
                if (e.Status == LedgerStatus.Approved) r.Add("files", GateStatus.Fail, e.Id + ": approved row has no Assets/ path");
                continue;
            }
            listed.Add(rel);
            string full = Path.Combine(unityRoot, rel);
            if (!File.Exists(full))
            {
                if (e.Status is LedgerStatus.Approved or LedgerStatus.Placeholder)
                    r.Add("files", GateStatus.Fail, e.Id + ": " + rel + " does not exist");
                continue;
            }
            if (string.IsNullOrEmpty(e.Sha256))
            {
                if (e.Status == LedgerStatus.Placeholder) r.Add("files", GateStatus.Warn, e.Id + ": placeholder file has no recorded hash");
                continue;
            }
            string actual = Util.Sha256File(full);
            if (!string.Equals(actual, e.Sha256, StringComparison.Ordinal))
                r.Add("files", GateStatus.Fail, e.Id + ": " + rel + " hashes to " + actual + ", ledger says " + e.Sha256 + " (the row describes other bytes)");
        }
        int hashed = res.Entries.Count(x => FilePath(x.Entry) != null && !string.IsNullOrEmpty(x.Entry.Sha256));
        r.Add("files", GateStatus.Pass, hashed + " file-backed row(s) checked against the files on disk");

        string art = Path.Combine(unityRoot, "Assets", "Art");
        if (!Directory.Exists(art)) return;
        foreach (string f in Directory.EnumerateFiles(art, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!AssetExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            string rel = Path.GetRelativePath(unityRoot, f).Replace('\\', '/');
            if (!listed.Contains(rel)) r.Add("unlisted", GateStatus.Fail, rel + " is not in any ledger (plan: keep a ledger row for every asset)");
        }
    }

    /// <summary>Forge's PROVENANCE_FIELDS (game-forge/forge/release/package.py) for each exported row.</summary>
    public static (JsonObject Assets, JsonArray Provenance) ForgeExport(Result res, bool includePlaceholders)
    {
        var assets = new JsonObject();
        var prov = new JsonArray();
        foreach ((string _, LedgerEntry e) in res.Entries)
        {
            bool eligible = e.Status == LedgerStatus.Approved || (includePlaceholders && e.Status == LedgerStatus.Placeholder);
            string rel = FilePath(e);
            if (!eligible || rel == null || string.IsNullOrEmpty(e.Sha256)) continue;
            string path = "astra-kingdoms/unity/" + rel;
            assets[path] = e.Sha256;
            prov.Add(new JsonObject
            {
                ["path"] = path,
                ["sha256"] = e.Sha256,
                ["source"] = e.Source ?? "",
                ["rights"] = (e.Licence ?? "") + (string.IsNullOrEmpty(e.RightsHolder) ? "" : "; rights holder: " + e.RightsHolder),
                ["generator"] = e.Source ?? "",
                // The ledger has no separate date field; a date written in the provenance text is used.
                // Without one this stays empty and Forge reports the provenance as incomplete.
                ["generated_at"] = DateRegex().Match(e.Provenance ?? "") is { Success: true } m ? m.Value : "",
                ["terms"] = e.Licence ?? "",
                ["transformations"] = e.Provenance ?? "",
                ["attribution"] = string.IsNullOrEmpty(e.Attribution) ? "none required" : e.Attribution,
                ["territory_flags"] = e.Territory ?? "",
                ["ledger_id"] = e.Id,
                ["ledger_status"] = e.Status.ToString(),
            });
        }
        return (assets, prov);
    }

    [GeneratedRegex(@"\b20\d\d-\d\d-\d\d\b")]
    private static partial Regex DateRegex();

    public static int Cli(CliArgs a)
    {
        var o = new Options
        {
            Ledgers = a.All("ledger").ToList(),
            UnityRoot = a.Get("unity"),
            RequireApproved = a.Flag("require-approved"),
            IncludePlaceholdersInExport = a.Flag("include-placeholders"),
        };
        Result res = Run(o);
        if (a.Get("forge-assets-out") is string assetsOut)
        {
            (JsonObject assets, JsonArray prov) = ForgeExport(res, o.IncludePlaceholdersInExport);
            Util.WriteJson(assetsOut, assets);
            Util.WriteJson(a.Require("forge-provenance-out"), prov);
        }
        return GateJson.Emit(res.Report, a.Get("out"));
    }
}
