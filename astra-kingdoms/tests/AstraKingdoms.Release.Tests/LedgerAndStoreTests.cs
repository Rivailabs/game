using System.Text.Json.Nodes;
using AstraKingdoms.Release.Gates;
using NUnit.Framework;

namespace AstraKingdoms.Release.Tests;

[TestFixture]
public class LedgerTests
{
    private static string RuntimeLedger => Repo.P("unity", "Assets", "Resources", "Ledger", "asset-ledger.json");
    private static string PlaceholderLedger => Repo.P("art", "ledger", "placeholder-assets.ledger.json");

    private static string Row(string id, string status, string path, string sha, string extra = "") =>
        "{\"id\":\"" + id + "\",\"kind\":\"Texture\",\"status\":\"" + status + "\",\"path\":" + (path == null ? "null" : "\"" + path + "\"") +
        ",\"purpose\":\"test\",\"source\":\"in-house\",\"licence\":\"own\",\"rights_holder\":\"owner\",\"attribution\":null," +
        "\"provenance\":\"made 2026-10-01" + extra + "\",\"territory\":\"worldwide\",\"sha256\":" + (sha == null ? "null" : "\"" + sha + "\"") + "}";

    private static string Ledger(params string[] rows) => "{\"format\":\"AK-ASSET-LEDGER/1\",\"entries\":[" + string.Join(",", rows) + "]}";

    [Test]
    public void CommittedLedgersValidateAndHashTheCommittedFiles()
    {
        LedgerValidate.Result r = LedgerValidate.Run(new LedgerValidate.Options { Ledgers = { RuntimeLedger, PlaceholderLedger }, UnityRoot = Repo.P("unity") });
        Assert.That(r.Report.With(GateStatus.Fail), Is.Empty, r.Report.ToText());
        Assert.That(r.Report.Has("pending", GateStatus.Warn), "placeholders are reported, not hidden");
        Assert.That(r.Entries.Count(e => LedgerValidate.FilePath(e.Entry) != null), Is.EqualTo(61));
    }

    [Test]
    public void StoreReleaseModeRefusesPlaceholders()
    {
        LedgerValidate.Result r = LedgerValidate.Run(new LedgerValidate.Options { Ledgers = { RuntimeLedger, PlaceholderLedger }, UnityRoot = Repo.P("unity"), RequireApproved = true });
        Assert.That(r.Report.Overall, Is.EqualTo(GateStatus.Fail));
        Assert.That(r.Report.With(GateStatus.Fail).Count(), Is.EqualTo(80));
    }

    [Test]
    public void TamperedBytesUnlistedFilesAndDuplicatesFail()
    {
        using var t = new TempDir();
        string unity = Path.Combine(t.Path, "unity");
        t.File("unity/Assets/Art/a.png", "A");
        t.File("unity/Assets/Art/b.png", "B");
        string good = Util.Sha256Text("A");
        string l1 = t.File("l1.json", Ledger(Row("tex.a", "Approved", "Assets/Art/a.png", good)));
        string l2 = t.File("l2.json", Ledger(Row("tex.a", "Placeholder", "generated:Assets/Art/a.png", new string('0', 64))));
        LedgerValidate.Result r = LedgerValidate.Run(new LedgerValidate.Options { Ledgers = { l1, l2 }, UnityRoot = unity });
        Assert.That(r.Report.Has("unique", GateStatus.Fail));
        Assert.That(r.Report.Findings.Any(f => f.Id == "files" && f.Status == GateStatus.Fail && f.Message.Contains("describes other bytes")));
        Assert.That(r.Report.Findings.Any(f => f.Id == "unlisted" && f.Message.StartsWith("Assets/Art/b.png")));
    }

    [Test]
    public void ClientRulesStillApplyThroughTheLinkedSource()
    {
        using var t = new TempDir();
        string l = t.File("l.json", Ledger(Row("tex.a", "Approved", "Assets/Art/a.png", null), Row("tex.b", "Placeholder", "Assets/Art/b.png", null)));
        LedgerValidate.Result r = LedgerValidate.Run(new LedgerValidate.Options { Ledgers = { l } });
        Assert.That(r.Report.With(GateStatus.Fail).Select(f => f.Message), Has.Some.Contains("needs the SHA-256"));
        Assert.That(r.Report.With(GateStatus.Fail).Select(f => f.Message), Has.Some.Contains("placeholder must be generated"));
    }

    [Test]
    public void ForgeExportUsesForgesProvenanceFields()
    {
        // The field list is read from Forge's own source so the two cannot drift apart.
        string py = File.ReadAllText(Path.Combine(Repo.Root, "game-forge", "forge", "release", "package.py"));
        string tuple = py[py.IndexOf("PROVENANCE_FIELDS = (", StringComparison.Ordinal)..];
        tuple = tuple[..(tuple.IndexOf(')') + 1)];
        string[] fields = System.Text.RegularExpressions.Regex.Matches(tuple, "\"([a-z_]+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.That(fields, Has.Length.EqualTo(10));

        using var t = new TempDir();
        t.File("unity/Assets/Art/a.png", "A");
        string l = t.File("l.json", Ledger(Row("tex.a", "Approved", "Assets/Art/a.png", Util.Sha256Text("A"))));
        LedgerValidate.Result r = LedgerValidate.Run(new LedgerValidate.Options { Ledgers = { l }, UnityRoot = Path.Combine(t.Path, "unity") });
        (JsonObject assets, JsonArray prov) = LedgerValidate.ForgeExport(r, includePlaceholders: false);
        Assert.That(assets["astra-kingdoms/unity/Assets/Art/a.png"]!.GetValue<string>(), Is.EqualTo(Util.Sha256Text("A")));
        JsonObject rec = prov.Single()!.AsObject();
        foreach (string f in fields) Assert.That(rec[f]?.GetValue<string>(), Is.Not.Null.And.Not.Empty, f);
        Assert.That(rec["generated_at"]!.GetValue<string>(), Is.EqualTo("2026-10-01"));
    }

    [Test]
    public void CliWritesGateResult()
    {
        using var t = new TempDir();
        string outPath = Path.Combine(t.Path, "ledger.json");
        (int code, string output) = Cli.Run("ledger-validate", "--unity", Repo.P("unity"), "--ledger", RuntimeLedger, "--ledger", PlaceholderLedger, "--out", outPath);
        Assert.That(code, Is.EqualTo(0), output);
        Assert.That(JsonNode.Parse(File.ReadAllText(outPath))!["status"]!.GetValue<string>(), Is.EqualTo("WARN"));
    }
}

[TestFixture]
public class StoreTextTests
{
    private static StoreTextLint.Rules Rules => StoreTextLint.Rules.Load(Repo.P("release", "store", "store-lint-rules.json"));

    private static GateReport Lint(string body) => StoreTextLint.Lint(new[] { ("t.md", "## Full description\n\n" + body + "\n") }, Rules);

    [Test]
    public void CommittedEnglishListingPasses()
    {
        GateReport r = StoreTextLint.Lint(new[] { ("listing.en.md", File.ReadAllText(Repo.P("release", "store", "listing.en.md"))) }, Rules);
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Pass), r.ToText());
    }

    [TestCase("listing.hi.md")]
    [TestCase("listing.kn.md")]
    public void TranslationPlaceholdersAreIncompleteNotPass(string file)
    {
        GateReport r = StoreTextLint.Lint(new[] { (file, File.ReadAllText(Repo.P("release", "store", file))) }, Rules);
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Incomplete), r.ToText());
    }

    [TestCase("Battle in epic four-player matches!", "v3.four-player")]
    [TestCase("Play 4 player kingdom wars", "v3.four-player")]
    [TestCase("Build your own kingdom and defend your homeland.", "v2.kingdom")]
    [TestCase("Conquer the world with your army.", "v4.world")]
    [TestCase("Join a clan and chat with friends.", "v2.social")]
    [TestCase("Climb the ranked ladder and unlock the season pass.", "v2.ranked")]
    [TestCase("Share your replays with friends.", "v2.replay-sharing")]
    [TestCase("Win real cash prizes in weekly tournaments!", "money.prizes")]
    [TestCase("An officially registered e-sport game.", "claims.registration")]
    [TestCase("Bug-free thanks to automated testing.", "claims.stability")]
    [TestCase("Completely ad-free.", "claims.ads")]
    [TestCase("Buy stronger weapons to win.", "claims.p2w")]
    [TestCase("Coming to iPhone soon.", "platform.ios")]
    [TestCase("Fast real-time combat.", "v3.realtime")]
    public void ForbiddenClaimsFail(string text, string rule)
    {
        GateReport r = Lint(text);
        Assert.That(r.Has(rule, GateStatus.Fail), rule + "\n" + r.ToText());
        Assert.That(r.ExitCode, Is.EqualTo(1));
    }

    [TestCase("Collect 40 weapons and 12 cards.", "count.weapon")]
    [TestCase("Up to 4 players per match.", "count.player")]
    [TestCase("Available in 3 languages.", "count.language")]
    public void CountsAboveWhatV1ShipsFail(string text, string rule) => Assert.That(Lint(text).Has(rule, GateStatus.Fail), Lint(text).ToText());

    [Test]
    public void UnsupportedLanguagesFailUntilDeclared()
    {
        Assert.That(Lint("Play in Hindi and Kannada.").With(GateStatus.Fail).Count(f => f.Id == "language"), Is.EqualTo(2));
        StoreTextLint.Rules rules = Rules;
        rules.SupportedLanguages.Add("Hindi");
        rules.MaxCounts["language"] = 2;
        GateReport r = StoreTextLint.Lint(new[] { ("t.md", "## Full description\nPlay in English and Hindi.\n") }, rules);
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Pass), r.ToText());
    }

    [Test]
    public void DeityWordingIsRoutedToReview() => Assert.That(Lint("Fight as the gods of old.").Has("culture.deities", GateStatus.Warn));

    [Test]
    public void AccurateV1TextPasses() =>
        Assert.That(Lint("Two players, 20 weapons, 6 cards and 5 terrain types. Optional rewarded ads outside matches.").Overall, Is.EqualTo(GateStatus.Pass));

    [Test]
    public void FieldLengthsFollowPlayLimits()
    {
        string text = "## Title\n" + new string('a', 31) + "\n## Short description\nok\n";
        Assert.That(StoreTextLint.Lint(new[] { ("t.md", text) }, Rules).Has("field.title", GateStatus.Fail));
        Assert.That(StoreTextLint.Lint(new[] { ("t.md", "## Title\n\n## Short description\nok\n") }, Rules).Has("field.title", GateStatus.Fail));
    }

    [Test]
    public void CommentsAndReviewerNotesAreNotStoreText()
    {
        string text = "<!-- four-player\nclans -->\n## Short description\nA duel.\n## Reviewer notes\n- remove the four-player line\n";
        GateReport r = StoreTextLint.Lint(new[] { ("t.md", text) }, Rules);
        Assert.That(r.Overall, Is.EqualTo(GateStatus.Pass), r.ToText());
        Assert.That(StoreTextLint.Parse("<!--\n\n-->\n## A\nx\n").Single().Lines.Single().Line, Is.EqualTo(5), "line numbers survive comments");
    }

    [Test]
    public void CliReportsLineNumbers()
    {
        using var t = new TempDir();
        string f = t.File("l.md", "# x\n## Full description\nok\nNow with clans!\n");
        (int code, string output) = Cli.Run("store-text-lint", "--rules", Repo.P("release", "store", "store-lint-rules.json"), "--file", f);
        Assert.That(code, Is.EqualTo(1));
        Assert.That(output, Does.Contain("l.md:4"));
    }
}
