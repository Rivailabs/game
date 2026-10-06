using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>
/// <c>release-record</c> (tickets 76 and 82; plan "Release and rollback procedure": "Before signing, the
/// release record contains the current scope, known issues, device results, purchase/ad test results
/// when relevant, data declarations, provenance exceptions, support contact and rollback method"):
/// assembles one <c>AK-RELEASE-RECORD/1</c> for one immutable candidate (commit, rules hash, artifact
/// hash, asset hashes, evidence files with their hashes, declarations, known issues) and lists the
/// blockers. With <c>--forge-dir</c> it also writes the inputs Forge's release commands take
/// (<c>forge release package --assets --provenance --tests</c> and <c>forge release approve --record</c>),
/// so the owner approves exactly what this record describes.
/// </summary>
public static partial class ReleaseRecord
{
    public const string Format = "AK-RELEASE-RECORD/1";

    public sealed class Options
    {
        public string Commit;
        public bool? Dirty;
        public string BuildManifest;
        public List<string> Ledgers = new();
        public string UnityRoot;
        public List<string> Evidence = new();
        public List<string> TestResults = new();
        public string Declarations;
        public string KnownIssues;
        public string Scope = "";
        public string SupportContact = "";
        public string Rollback = "";
        public string RestoreDrill = "";
        public string MigrationNotes = "";
        public List<string> PurchaseAdResults = new();
        public List<string> ProvenanceExceptions = new();
        public bool StoreRelease;
    }

    public sealed class Issue
    {
        public string Id = "", Title = "", Severity = "", Status = "";
    }

    [GeneratedRegex(@"^\|\s*(KI-\d+)\s*\|\s*([^|]*)\|\s*([^|]*)\|\s*([^|]*)\|")]
    private static partial Regex IssueRow();

    public static List<Issue> ParseKnownIssues(string markdown) =>
        markdown.Replace("\r\n", "\n").Split('\n').Select(l => IssueRow().Match(l)).Where(m => m.Success)
            .Select(m => new Issue { Id = m.Groups[1].Value, Title = m.Groups[2].Value.Trim(), Severity = m.Groups[3].Value.Trim().ToLowerInvariant(), Status = m.Groups[4].Value.Trim().ToLowerInvariant() })
            .ToList();

    /// <summary>(total, failed) from a TRX or JUnit XML results file.</summary>
    public static (int Total, int Failed) ParseTestResults(string xml)
    {
        XDocument d = XDocument.Parse(xml);
        XElement counters = d.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters");
        if (counters != null)
            return ((int?)counters.Attribute("total") ?? 0, ((int?)counters.Attribute("failed") ?? 0) + ((int?)counters.Attribute("error") ?? 0));
        var suites = d.Descendants().Where(e => e.Name.LocalName == "testsuite").ToList();
        if (suites.Count == 0 && d.Root?.Name.LocalName == "testsuites") suites.Add(d.Root);
        int total = suites.Sum(s => (int?)s.Attribute("tests") ?? 0);
        int failed = suites.Sum(s => ((int?)s.Attribute("failures") ?? 0) + ((int?)s.Attribute("errors") ?? 0));
        if (total == 0) throw new FormatException("Unrecognised test results file (expected TRX Counters or JUnit testsuite).");
        return (total, failed);
    }

    public static string EvidenceClass(string gate, JsonNode doc)
    {
        string explicitClass = Util.Str(doc, "evidence_class");
        if (explicitClass.Length > 0) return explicitClass;
        string g = gate.ToLowerInvariant();
        if (g.StartsWith("perf", StringComparison.Ordinal) || g.StartsWith("sustained", StringComparison.Ordinal)) return "device";
        if (g.Contains("data safety")) return "declarations";
        if (g.Contains("store text")) return "store";
        return "integration";
    }

    public static string GitCommit(out bool dirty)
    {
        dirty = Git("status --porcelain").Length > 0;
        return Git("rev-parse HEAD");
    }

    private static string Git(string args)
    {
        try
        {
            using Process p = Process.Start(new ProcessStartInfo("git", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return p.ExitCode == 0 ? o : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static (GateReport Report, JsonObject Record, JsonObject Forge) Build(Options o)
    {
        var r = new GateReport("release candidate record");
        var blockers = new JsonArray();
        void Block(GateStatus s, string id, string msg)
        {
            r.Add(id, s, msg);
            blockers.Add(GateReport.Label(s) + " " + id + ": " + msg);
        }

        // Source and build identity.
        if (string.IsNullOrEmpty(o.Commit)) Block(GateStatus.Incomplete, "commit", "source commit unknown");
        if (o.Dirty == true) Block(GateStatus.Fail, "commit", "working tree is dirty; a candidate is one immutable commit");
        JsonNode manifest = o.BuildManifest != null && File.Exists(o.BuildManifest) ? Util.ReadJson(o.BuildManifest) : null;
        if (manifest == null) Block(GateStatus.Incomplete, "build", "no build manifest (AK-RELEASE-BUILD/1 from ReleaseCandidateBuild or AK-BUILD-MANIFEST/1)");
        else
        {
            if (!Util.Bool(manifest, "succeeded")) Block(GateStatus.Fail, "build", "the build manifest records a failed build");
            string built = Util.Str(manifest, "commit");
            if (!string.IsNullOrEmpty(o.Commit) && built.Length > 0 && built != o.Commit)
                Block(GateStatus.Fail, "build", "artifact was built from " + built + ", not the candidate commit " + o.Commit);
            if (Util.Bool(manifest, "commit_dirty")) Block(GateStatus.Fail, "build", "artifact was built from a dirty tree");
            if (Util.Bool(manifest, "development_build")) Block(GateStatus.Fail, "build", "development build: release candidates use the release configuration");
            if (Util.Str(manifest, "artifact_sha256").Length != 64) Block(GateStatus.Fail, "build", "artifact hash missing");
        }

        // Assets.
        var assets = new JsonObject();
        JsonArray provenance = new();
        if (o.Ledgers.Count > 0)
        {
            LedgerValidate.Result lr = LedgerValidate.Run(new LedgerValidate.Options { Ledgers = o.Ledgers, UnityRoot = o.UnityRoot, RequireApproved = o.StoreRelease });
            if (lr.Report.Overall >= GateStatus.Incomplete) Block(lr.Report.Overall, "assets", "asset ledger gate is " + GateReport.Label(lr.Report.Overall));
            foreach ((string _, var e) in lr.Entries)
                if (LedgerValidate.FilePath(e) is string p && !string.IsNullOrEmpty(e.Sha256)) assets["astra-kingdoms/unity/" + p] = e.Sha256;
            (JsonObject _, provenance) = LedgerValidate.ForgeExport(lr, includePlaceholders: !o.StoreRelease);
        }
        else Block(GateStatus.Incomplete, "assets", "no asset ledger given");

        // Evidence.
        var tests = new JsonArray();
        bool deviceEvidence = false;
        foreach (string f in o.Evidence)
        {
            JsonNode doc = Util.ReadJson(f);
            if (Util.Str(doc, "format") != GateJson.Format) throw new FormatException(f + " is not " + GateJson.Format);
            string gate = Util.Str(doc, "gate"), status = Util.Str(doc, "status");
            string cls = EvidenceClass(gate, doc);
            // Forge counts only PASS; WARN is a pass with notes, INCOMPLETE/FAIL never pass.
            string forgeStatus = status == "WARN" ? "PASS" : status == "INCOMPLETE" ? "INCOMPLETE" : status;
            tests.Add(new JsonObject { ["name"] = gate, ["status"] = forgeStatus, ["sha256"] = Util.Sha256File(f), ["evidence_class"] = cls, ["detail"] = status, ["file"] = Path.GetFileName(f) });
            if (status is "FAIL" or "INCOMPLETE") Block(status == "FAIL" ? GateStatus.Fail : GateStatus.Incomplete, "evidence", gate + " is " + status);
            if (cls == "device" && forgeStatus == "PASS") deviceEvidence = true;
        }
        foreach (string f in o.TestResults)
        {
            (int total, int failed) = ParseTestResults(File.ReadAllText(f));
            string status = failed == 0 && total > 0 ? "PASS" : "FAIL";
            tests.Add(new JsonObject { ["name"] = "tests:" + Path.GetFileName(f), ["status"] = status, ["sha256"] = Util.Sha256File(f), ["evidence_class"] = "rules", ["detail"] = total + " tests, " + failed + " failed" });
            if (status == "FAIL") Block(GateStatus.Fail, "tests", Path.GetFileName(f) + ": " + failed + " of " + total + " failed");
        }
        if (!deviceEvidence) Block(GateStatus.Incomplete, "device", "no passing physical-device evidence (perf-compare / sustained-collate on a registered phone)");

        // Declarations, known issues and the human fields Forge requires.
        string declarations = "";
        if (o.Declarations != null && File.Exists(o.Declarations)) declarations = Path.GetFileName(o.Declarations) + " sha256 " + Util.Sha256File(o.Declarations);
        else Block(GateStatus.Incomplete, "declarations", "no Data safety declaration draft");
        var issues = o.KnownIssues != null && File.Exists(o.KnownIssues) ? ParseKnownIssues(File.ReadAllText(o.KnownIssues)) : new List<Issue>();
        if (o.KnownIssues == null) Block(GateStatus.Incomplete, "known_issues", "no known-issues register given");
        foreach (Issue i in issues.Where(i => i.Status == "open" && i.Severity == "release-blocker"))
            Block(GateStatus.Fail, "known_issues", i.Id + " is an open release blocker: " + i.Title);
        foreach ((string field, string value) in new[] { ("scope", o.Scope), ("support_contact", o.SupportContact), ("rollback_method", o.Rollback), ("last_restore_drill", o.RestoreDrill) })
            if (string.IsNullOrWhiteSpace(value)) Block(GateStatus.Incomplete, field, field + " is empty");
        if (o.StoreRelease && o.ProvenanceExceptions.Count > 0)
            r.Add("provenance", GateStatus.Warn, o.ProvenanceExceptions.Count + " provenance exception(s) recorded for owner decision");
        if (blockers.Count == 0) r.Add("record", GateStatus.Pass, "all required fields and evidence present");

        var openIssues = new JsonArray(issues.Where(i => i.Status != "closed").Select(i => (JsonNode)(i.Id + " [" + i.Severity + "/" + i.Status + "] " + i.Title)).ToArray());
        var candidate = new JsonObject
        {
            ["source_commit"] = o.Commit ?? "",
            ["package_id"] = Util.Str(manifest, "application_id"),
            ["version_name"] = Util.Str(manifest, "version_name", Util.Str(manifest, "bundle_version")),
            ["version_code"] = Util.LongOrNull(manifest, "version_code"),
            ["rules_version"] = Util.Str(manifest, "rules_version"),
            ["rules_hash"] = Util.Str(manifest, "rules_hash"),
            ["unity_version"] = Util.Str(manifest, "unity_version"),
            ["artifact"] = Util.Str(manifest, "artifact"),
            ["artifact_sha256"] = Util.Str(manifest, "artifact_sha256"),
            ["assets"] = assets,
        };
        string candidateHash = Util.Sha256Text(candidate.ToJsonString());
        var record = new JsonObject
        {
            ["format"] = Format,
            ["status"] = GateReport.Label(r.Overall),
            ["candidate"] = candidate,
            ["candidate_hash"] = candidateHash,
            ["evidence"] = tests,
            ["data_declarations"] = declarations,
            ["known_issues"] = openIssues,
            ["scope"] = o.Scope,
            ["support_contact"] = o.SupportContact,
            ["rollback_method"] = o.Rollback,
            ["last_restore_drill"] = o.RestoreDrill,
            ["migration_notes"] = o.MigrationNotes,
            ["purchase_ad_results"] = o.PurchaseAdResults.Count == 0 ? null : new JsonArray(o.PurchaseAdResults.Select(x => (JsonNode)x).ToArray()),
            ["provenance_exceptions"] = new JsonArray(o.ProvenanceExceptions.Select(x => (JsonNode)x).ToArray()),
            ["blockers"] = blockers,
            ["note"] = "Assembled by AstraKingdoms.Release. Owner approval and signing happen only through Forge (forge release approve; python -m forge.release.signer).",
        };
        // Forge release-record fields (game-forge/forge/release/candidate.py ReleaseRecord, minus the candidate it builds itself).
        var forge = new JsonObject
        {
            ["record"] = new JsonObject
            {
                ["scope"] = o.Scope,
                ["known_issues"] = openIssues.DeepClone(),
                ["device_results"] = new JsonArray(tests.Where(t => Util.Str(t, "evidence_class") == "device").Select(t => (JsonNode)(Util.Str(t, "name") + ": " + Util.Str(t, "detail"))).ToArray()),
                ["purchase_ad_results"] = o.PurchaseAdResults.Count == 0 ? null : new JsonArray(o.PurchaseAdResults.Select(x => (JsonNode)x).ToArray()),
                ["data_declarations"] = declarations,
                ["provenance_exceptions"] = new JsonArray(o.ProvenanceExceptions.Select(x => (JsonNode)x).ToArray()),
                ["support_contact"] = o.SupportContact,
                ["rollback_method"] = o.Rollback,
                ["migration_notes"] = o.MigrationNotes,
                ["last_restore_drill"] = o.RestoreDrill,
                ["destination"] = "signing",
            },
            ["tests"] = new JsonArray(tests.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = Util.Str(t, "name"), ["status"] = Util.Str(t, "status"), ["sha256"] = Util.Str(t, "sha256"), ["evidence_class"] = Util.Str(t, "evidence_class"),
            }).ToArray()),
            ["assets"] = assets.DeepClone(),
            ["provenance"] = provenance,
        };
        return (r, record, forge);
    }

    public static string Markdown(JsonObject record)
    {
        var sb = new StringBuilder();
        JsonNode c = record["candidate"];
        sb.Append("# Release candidate record\n\n");
        sb.Append("Status: **").Append(Util.Str(record, "status")).Append("**  \nCandidate hash: `").Append(Util.Str(record, "candidate_hash")).Append("`\n\n");
        sb.Append("| Field | Value |\n| --- | --- |\n");
        foreach (string k in new[] { "source_commit", "package_id", "version_name", "rules_version", "rules_hash", "unity_version", "artifact", "artifact_sha256" })
            sb.Append("| ").Append(k).Append(" | `").Append(Util.Str(c, k)).Append("` |\n");
        sb.Append("| assets | ").Append(c?["assets"]?.AsObject().Count ?? 0).Append(" hashed files |\n\n");
        sb.Append("## Evidence\n\n| Gate | Status | Class | sha256 |\n| --- | --- | --- | --- |\n");
        foreach (JsonNode t in record["evidence"]!.AsArray())
            sb.Append("| ").Append(Util.Str(t, "name")).Append(" | ").Append(Util.Str(t, "detail")).Append(" | ").Append(Util.Str(t, "evidence_class")).Append(" | `")
              .Append(Util.Str(t, "sha256")[..Math.Min(12, Util.Str(t, "sha256").Length)]).Append("` |\n");
        sb.Append("\n## Blockers\n\n");
        var blockers = record["blockers"]!.AsArray();
        if (blockers.Count == 0) sb.Append("None.\n");
        foreach (JsonNode b in blockers) sb.Append("- ").Append(b!.GetValue<string>()).Append('\n');
        sb.Append("\n## Known issues (open)\n\n");
        foreach (JsonNode i in record["known_issues"]!.AsArray()) sb.Append("- ").Append(i!.GetValue<string>()).Append('\n');
        foreach (string k in new[] { "scope", "data_declarations", "support_contact", "rollback_method", "last_restore_drill", "migration_notes" })
            sb.Append("\n**").Append(k).Append(":** ").Append(Util.Str(record, k)).Append('\n');
        return sb.ToString();
    }

    public static int Cli(CliArgs a)
    {
        var o = new Options
        {
            Commit = a.Get("commit"),
            BuildManifest = a.Get("build-manifest"),
            Ledgers = a.All("ledger").ToList(),
            UnityRoot = a.Get("unity"),
            Evidence = a.All("evidence").ToList(),
            TestResults = a.All("test-results").ToList(),
            Declarations = a.Get("declarations"),
            KnownIssues = a.Get("known-issues"),
            Scope = a.Get("scope", ""),
            SupportContact = a.Get("support-contact", ""),
            Rollback = a.Get("rollback", ""),
            RestoreDrill = a.Get("restore-drill", ""),
            MigrationNotes = a.Get("migration-notes", ""),
            PurchaseAdResults = a.All("purchase-ad-result").ToList(),
            ProvenanceExceptions = a.All("provenance-exception").ToList(),
            StoreRelease = a.Flag("store-release"),
        };
        if (o.Commit == null)
        {
            o.Commit = GitCommit(out bool dirty);
            o.Dirty = dirty;
        }
        (GateReport r, JsonObject record, JsonObject forge) = Build(o);
        Console.Write(r.ToText());
        Util.WriteJson(a.Require("out"), record);
        if (a.Get("md-out") is string md) File.WriteAllText(md, Markdown(record));
        if (a.Get("forge-dir") is string dir)
        {
            Directory.CreateDirectory(dir);
            Util.WriteJson(Path.Combine(dir, "forge-record.json"), forge["record"]!.DeepClone());
            Util.WriteJson(Path.Combine(dir, "forge-tests.json"), forge["tests"]!.DeepClone());
            Util.WriteJson(Path.Combine(dir, "forge-assets.json"), forge["assets"]!.DeepClone());
            Util.WriteJson(Path.Combine(dir, "forge-provenance.json"), forge["provenance"]!.DeepClone());
            Console.WriteLine("wrote Forge inputs to " + dir);
        }
        return r.ExitCode;
    }
}
