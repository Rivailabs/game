using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AstraKingdoms.Release.Gates;
using NUnit.Framework;

namespace AstraKingdoms.Release.Tests;

[TestFixture]
public class ReleaseRecordTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private static string Gate(TempDir t, string name, string gate, string status) =>
        t.File(name, new JsonObject { ["format"] = "AK-GATE-RESULT/1", ["gate"] = gate, ["status"] = status, ["findings"] = new JsonArray() }.ToJsonString());

    private static string Manifest(TempDir t, bool dirty = false, bool development = false, string commit = Commit) =>
        t.File("release-build-manifest.json", new JsonObject
        {
            ["format"] = "AK-RELEASE-BUILD/1", ["succeeded"] = true, ["commit"] = commit, ["commit_dirty"] = dirty, ["development_build"] = development,
            ["application_id"] = "com.rivailabs.astrakingdoms", ["version_name"] = "1.0.0", ["version_code"] = 7, ["rules_version"] = "AK-TR-1",
            ["rules_hash"] = new string('a', 64), ["unity_version"] = "6000.0.23f1", ["artifact"] = "Builds/Release/a/astra.aab", ["artifact_sha256"] = new string('b', 64),
        }.ToJsonString());

    private static ReleaseRecord.Options Good(TempDir t)
    {
        t.File("unity/Assets/Art/a.png", "A");
        string ledger = t.File("ledger.json", "{\"format\":\"AK-ASSET-LEDGER/1\",\"entries\":[{\"id\":\"tex.a\",\"kind\":\"Texture\",\"status\":\"Approved\",\"path\":\"Assets/Art/a.png\"," +
            "\"purpose\":\"p\",\"source\":\"s\",\"licence\":\"l\",\"rights_holder\":\"r\",\"attribution\":null,\"provenance\":\"commissioned 2026-09-01\",\"territory\":\"worldwide\",\"sha256\":\"" + Util.Sha256Text("A") + "\"}]}");
        return new ReleaseRecord.Options
        {
            Commit = Commit,
            Dirty = false,
            BuildManifest = Manifest(t),
            Ledgers = { ledger },
            UnityRoot = Path.Combine(t.Path, "unity"),
            Evidence = { Gate(t, "perf.json", "perf frame-pacing on RefPhone", "PASS"), Gate(t, "size.json", "download/build size", "WARN") },
            TestResults = { t.File("ak.trx", "<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"10\" executed=\"10\" passed=\"10\" failed=\"0\" error=\"0\"/></ResultSummary></TestRun>") },
            Declarations = t.File("data-safety-draft.md", "# draft"),
            KnownIssues = t.File("known-issues.md", "| ID | Title | Severity | Status |\n| --- | --- | --- | --- |\n| KI-001 | Thing | minor | open |\n| KI-002 | Old | major | closed |\n"),
            Scope = "V1 closed test",
            SupportContact = "support@example.com",
            Rollback = "config rollback; previous build re-release",
            RestoreDrill = "2026-10-01 PASS",
            StoreRelease = true,
        };
    }

    [Test]
    public void CompleteCandidatePassesAndCarriesHashes()
    {
        using var t = new TempDir();
        (GateReport r, JsonObject rec, JsonObject forge) = ReleaseRecord.Build(Good(t));
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Pass), r.ToText());
        Assert.That(rec["candidate"]!["source_commit"]!.GetValue<string>(), Is.EqualTo(Commit));
        Assert.That(rec["candidate"]!["rules_hash"]!.GetValue<string>(), Is.EqualTo(new string('a', 64)));
        Assert.That(rec["candidate"]!["assets"]!["astra-kingdoms/unity/Assets/Art/a.png"]!.GetValue<string>(), Is.EqualTo(Util.Sha256Text("A")));
        Assert.That(rec["evidence"]!.AsArray(), Has.Count.EqualTo(3));
        Assert.That(rec["known_issues"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "KI-001 [minor/open] Thing" }));
        Assert.That(rec["candidate_hash"]!.GetValue<string>(), Has.Length.EqualTo(64));
        // WARN is a pass with notes for Forge; the original status is kept as detail.
        JsonNode size = forge["tests"]!.AsArray().Single(x => Util.Str(x, "name") == "download/build size")!;
        Assert.That(Util.Str(size, "status"), Is.EqualTo("PASS"));
        Assert.That(forge["tests"]!.AsArray().Any(x => Util.Str(x, "evidence_class") == "device" && Util.Str(x, "status") == "PASS"));
    }

    [Test]
    public void ForgeRecordHasEveryFieldForgesReleaseRecordDeclares()
    {
        // Read the pydantic model from Forge's source: the two must not drift.
        string py = File.ReadAllText(Path.Combine(Repo.Root, "game-forge", "forge", "release", "candidate.py"));
        string cls = py[py.IndexOf("class ReleaseRecord(BaseModel):", StringComparison.Ordinal)..];
        cls = cls[..cls.IndexOf("def missing_fields", StringComparison.Ordinal)];
        string[] fields = Regex.Matches(cls, @"^\s{4}([a-z_]+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Where(f => f != "candidate").ToArray();
        Assert.That(fields, Does.Contain("rollback_method").And.Contain("last_restore_drill"));

        using var t = new TempDir();
        (_, _, JsonObject forge) = ReleaseRecord.Build(Good(t));
        JsonObject record = forge["record"]!.AsObject();
        foreach (string f in fields) Assert.That(record.ContainsKey(f), Is.True, f);
        string[] forgeTestFields = { "name", "status", "sha256", "evidence_class" };
        foreach (JsonNode test in forge["tests"]!.AsArray())
            Assert.That(test!.AsObject().Select(kv => kv.Key), Is.EquivalentTo(forgeTestFields));
    }

    [Test]
    public void DirtyTreeDevelopmentBuildAndOtherCommitFail()
    {
        using var t = new TempDir();
        ReleaseRecord.Options o = Good(t);
        o.Dirty = true;
        Assert.That(ReleaseRecord.Build(o).Report.Has("commit", GateStatus.Fail));
        o = Good(t);
        o.BuildManifest = Manifest(t, development: true, commit: "ffff");
        GateReport r = ReleaseRecord.Build(o).Report;
        Assert.That(r.With(GateStatus.Fail).Select(f => f.Message), Has.Some.Contains("development build").And.Some.Contains("not the candidate commit"));
    }

    [Test]
    public void MissingDeviceEvidenceAndFieldsAreIncomplete()
    {
        using var t = new TempDir();
        ReleaseRecord.Options o = Good(t);
        o.Evidence = new List<string> { Gate(t, "size2.json", "download/build size", "PASS") };
        o.RestoreDrill = "";
        GateReport r = ReleaseRecord.Build(o).Report;
        Assert.That(r.Has("device", GateStatus.Incomplete));
        Assert.That(r.Has("last_restore_drill", GateStatus.Incomplete));
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Incomplete));
    }

    [Test]
    public void IncompleteEvidenceOpenBlockerAndFailedTestsBlock()
    {
        using var t = new TempDir();
        ReleaseRecord.Options o = Good(t);
        o.Evidence.Add(Gate(t, "ds.json", "data safety declarations", "INCOMPLETE"));
        o.KnownIssues = t.File("ki.md", "| KI-009 | Crash on start | release-blocker | open | x |\n");
        o.TestResults.Add(t.File("junit.xml", "<testsuites><testsuite tests=\"4\" failures=\"1\" errors=\"0\"/></testsuites>"));
        GateReport r = ReleaseRecord.Build(o).Report;
        Assert.That(r.Has("evidence", GateStatus.Incomplete));
        Assert.That(r.Has("known_issues", GateStatus.Fail));
        Assert.That(r.Has("tests", GateStatus.Fail));
    }

    [Test]
    public void StoreReleaseRefusesPlaceholderAssets()
    {
        using var t = new TempDir();
        ReleaseRecord.Options o = Good(t);
        o.Ledgers = new List<string> { Repo.P("art", "ledger", "placeholder-assets.ledger.json") };
        o.UnityRoot = Repo.P("unity");
        Assert.That(ReleaseRecord.Build(o).Report.Has("assets", GateStatus.Fail));
    }

    [Test]
    public void ParsesTrxJunitAndTheCommittedKnownIssues()
    {
        Assert.That(ReleaseRecord.ParseTestResults("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><ResultSummary><Counters total=\"5\" failed=\"1\" error=\"1\"/></ResultSummary></TestRun>"),
            Is.EqualTo((5, 2)));
        Assert.That(ReleaseRecord.ParseTestResults("<testsuite tests=\"3\" failures=\"0\" errors=\"0\"/>"), Is.EqualTo((3, 0)));
        Assert.Throws<FormatException>(() => ReleaseRecord.ParseTestResults("<x/>"));
        List<ReleaseRecord.Issue> issues = ReleaseRecord.ParseKnownIssues(File.ReadAllText(Repo.P("release", "operations", "known-issues.md")));
        Assert.That(issues, Has.Count.GreaterThanOrEqualTo(5));
        Assert.That(issues.All(i => i.Severity is "release-blocker" or "major" or "minor" && i.Status is "open" or "mitigated" or "closed"));
    }

    [Test]
    public void CliWritesRecordMarkdownAndForgeInputs()
    {
        using var t = new TempDir();
        ReleaseRecord.Options o = Good(t);
        string outDir = Path.Combine(t.Path, "rc");
        var args = new List<string> { "release-record", "--commit", Commit, "--build-manifest", o.BuildManifest, "--unity", o.UnityRoot, "--ledger", o.Ledgers[0],
            "--evidence", o.Evidence[0], "--evidence", o.Evidence[1], "--test-results", o.TestResults[0], "--declarations", o.Declarations,
            "--known-issues", o.KnownIssues, "--scope", o.Scope, "--support-contact", o.SupportContact, "--rollback", o.Rollback,
            "--restore-drill", o.RestoreDrill, "--store-release", "--out", Path.Combine(outDir, "record.json"), "--md-out", Path.Combine(t.Path, "record.md"),
            "--forge-dir", Path.Combine(outDir, "forge") };
        (int code, string output) = Cli.Run(args.ToArray());
        Assert.That(code, Is.EqualTo(0), output);
        foreach (string f in new[] { "forge-record.json", "forge-tests.json", "forge-assets.json", "forge-provenance.json" })
            Assert.That(File.Exists(Path.Combine(outDir, "forge", f)), f);
        Assert.That(File.ReadAllText(Path.Combine(t.Path, "record.md")), Does.Contain("Status: **PASS**"));
    }
}

[TestFixture]
public class CliAndDocumentTests
{
    [Test]
    public void UnknownCommandAndBadArgumentsAreUsageErrors()
    {
        Assert.That(Cli.Run("nope").Code, Is.EqualTo(2));
        Assert.That(Cli.Run().Code, Is.EqualTo(0));
        Assert.That(Cli.Run("perf-compare", "positional").Code, Is.EqualTo(2));
        Assert.That(Cli.Run("perf-compare").Code, Is.EqualTo(2));
    }

    [Test]
    public void UsageListsEveryCommandAndItsOptions()
    {
        string usage = Program.Usage; // a const; read through a local so NUnit2007 sees an actual value
        foreach (string c in new[] { "ledger-validate", "store-text-lint", "perf-compare", "size-check", "repro-compare", "sustained-collate", "data-safety",
                     "closed-test-check", "version-policy-check", "release-record" })
            Assert.That(usage, Does.Contain(c + " "), c);
    }

    private static readonly (int Ticket, string Path)[] TicketDocs =
    {
        (65, "art/briefs/65-archer.md"), (66, "art/briefs/66-bow-arrow.md"), (67, "art/briefs/67-weapon-effects.md"),
        (68, "art/briefs/68-arenas-terrain.md"), (69, "art/briefs/69-ui-store-icons.md"), (70, "art/briefs/70-sound-effects.md"),
        (71, "art/briefs/71-music.md"), (72, "art/briefs/72-store-art.md"),
        (73, "release/perf/README.md"), (74, "ci/size-check.sh"), (75, "release/scenarios/scenarios.json"),
        (76, "release/packaging/README.md"), (77, "release/declarations/consent-flow-checklist.md"),
        (78, "release/store/iarc-questionnaire-draft.md"), (79, "release/closed-test/README.md"), (80, "release/soft-launch/plan.md"),
        (81, "release/operations/priority-patch-runbook.md"), (82, "release/operations/restore-rehearsal-checklist.md"),
    };

    [Test]
    public void EveryTicketHasItsDocumentAndTheIndexLinksIt()
    {
        string index = File.ReadAllText(Repo.P("release", "README.md"));
        foreach ((int ticket, string path) in TicketDocs)
        {
            Assert.That(File.Exists(Repo.P(path.Split('/'))), path);
            Assert.That(index, Does.Contain("| " + ticket + " |"), "release/README.md lists ticket " + ticket);
        }
    }

    [Test]
    public void LegalDocumentsAreMarkedForLegalReview()
    {
        foreach (string p in new[] { "release/operations/grievance-process.md", "release/operations/dpdp-timing-note.md", "release/closed-test/tester-consent.md",
                     "release/declarations/consent-flow-checklist.md", "release/store/iarc-questionnaire-draft.md" })
            Assert.That(File.ReadAllText(Repo.P(p.Split('/'))), Does.Contain("legal review").IgnoreCase, p);
    }

    [Test]
    public void CiWorkflowRunsBothStacksAndKeepsSlowForgeTestsSeparate()
    {
        string yml = File.ReadAllText(Path.Combine(Repo.Root, ".github", "workflows", "ci.yml"));
        Assert.That(yml, Does.Contain("dotnet-version: '8.0.x'"));
        Assert.That(yml, Does.Contain("python-version: '3.12'"));
        Assert.That(yml, Does.Contain("dotnet test"));
        Assert.That(yml, Does.Contain("-m \"not dotnet\""));
        Assert.That(yml, Does.Contain("-m dotnet"));
    }
}
