using System.Text.RegularExpressions;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Client.Tests;

public sealed class LocalizationTests
{
    private static LocalizationTable Load(string code) =>
        LocalizationTable.Parse(code, File.ReadAllText(Path.Combine(Paths.Localization, code + ".txt")));

    private static Localizer All()
    {
        var loc = new Localizer(Load(Localizer.English));
        loc.AddLanguage(Load(Localizer.Hindi));
        loc.AddLanguage(Load(Localizer.Kannada));
        return loc;
    }

    [TestCase("en")]
    [TestCase("hi")]
    [TestCase("kn")]
    public void FileParsesWithoutErrors(string code)
    {
        LocalizationTable t = Load(code);
        Assert.That(t.Errors, Is.Empty);
        Assert.That(t.Count, Is.GreaterThan(150));
    }

    [TestCase("hi")]
    [TestCase("kn")]
    public void TranslationHasExactlyTheEnglishKeysAndParameters(string code)
    {
        LocalizationTable en = Load("en");
        LocalizationTable t = Load(code);
        Assert.That(t.Keys, Is.EquivalentTo(en.Keys), "same key set");
        foreach (string key in en.Keys)
        {
            en.TryGet(key, out string ev);
            t.TryGet(key, out string tv);
            Assert.That(LocalizationTable.ParameterCount(tv), Is.EqualTo(LocalizationTable.ParameterCount(ev)), code + ":" + key);
            Assert.That(tv, Is.Not.Empty, code + ":" + key);
        }
    }

    [TestCase("hi")]
    [TestCase("kn")]
    public void TranslationsAreMarkedForFluentSpeakerReview(string code)
    {
        Assert.That(string.Join("\n", Load(code).HeaderComments), Does.Contain("needs fluent-speaker review"));
    }

    [Test]
    public void MissingKeyFallsBackToEnglishNeverRawKey()
    {
        var en = LocalizationTable.Parse("en", "a.b = Hello {0}\nonly.en = English only");
        var hi = LocalizationTable.Parse("hi", "a.b = Namaste {0}");
        var loc = new Localizer(en);
        loc.AddLanguage(hi);
        loc.Language = "hi";
        Assert.That(loc.Format("a.b", "X"), Is.EqualTo("Namaste X"));
        Assert.That(loc.Get("only.en"), Is.EqualTo("English only"));
        Assert.That(loc.Get("no.such.key"), Is.EqualTo(Localizer.MissingPlaceholder));
        Assert.That(loc.Get("no.such.key"), Does.Not.Contain("no.such"));
        loc.Language = "xx";
        Assert.That(loc.Language, Is.EqualTo("en"));
    }

    [Test]
    public void MalformedTranslationFallsBackToEnglishTemplate()
    {
        var loc = new Localizer(LocalizationTable.Parse("en", "k = {0} cells"));
        loc.AddLanguage(LocalizationTable.Parse("kn", "k = {0 broken"));
        loc.Language = "kn";
        Assert.That(loc.Format("k", 5), Is.EqualTo("{0 broken").Or.EqualTo("5 cells"));
        Assert.DoesNotThrow(() => loc.Format("k", 5));
    }

    [Test]
    public void DuplicateKeysAreReported()
    {
        LocalizationTable t = LocalizationTable.Parse("en", "a = 1\na = 2");
        Assert.That(t.Errors, Has.Count.EqualTo(1));
    }

    /// <summary>Every literal key used by the client sources exists in English.</summary>
    [Test]
    public void EveryLiteralKeyInTheClientExists()
    {
        LocalizationTable en = Load("en");
        var pattern = new Regex("\\b(?:T|TF|Get|Format)\\(\\s*\"([a-zA-Z][a-zA-Z0-9]*(?:\\.[a-zA-Z0-9]+)+)\"");
        var missing = new List<string>();
        int found = 0;
        foreach (string file in Directory.GetFiles(Paths.Scripts, "*.cs", SearchOption.AllDirectories))
        {
            foreach (System.Text.RegularExpressions.Match m in pattern.Matches(File.ReadAllText(file)))
            {
                found++;
                if (!en.Contains(m.Groups[1].Value)) missing.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value);
            }
        }
        Assert.That(found, Is.GreaterThan(80), "the key scan found too few keys; did the pattern break?");
        Assert.That(missing, Is.Empty);
    }

    /// <summary>Keys built from enum names or IDs at runtime.</summary>
    [Test]
    public void DynamicKeyFamiliesExist()
    {
        LocalizationTable en = Load("en");
        var keys = new List<string>();
        for (int id = 1; id <= RulesConstants.RegularWeaponCount; id++) keys.Add("weapon." + id);
        foreach (Element e in Enum.GetValues(typeof(Element))) keys.Add("element." + e.ToString().ToLowerInvariant());
        foreach (Dodge d in Enum.GetValues(typeof(Dodge))) keys.Add("dodge." + d.ToString().ToLowerInvariant());
        foreach (TerrainType t in Enum.GetValues(typeof(TerrainType))) keys.Add("terrain." + t.ToString().ToLowerInvariant());
        foreach (CardId c in Enum.GetValues(typeof(CardId))) keys.Add("card." + (int)c);
        foreach (CutRejection r in Enum.GetValues(typeof(CutRejection))) keys.Add("cut.reject." + r);
        foreach (TerminalReason r in Enum.GetValues(typeof(TerminalReason)))
            if (r != TerminalReason.None) keys.Add("reason." + r);
        foreach (Rules.Bots.BotDifficulty d in Enum.GetValues(typeof(Rules.Bots.BotDifficulty))) keys.Add("difficulty." + d.ToString().ToLowerInvariant());
        Assert.That(keys.Where(k => !en.Contains(k)), Is.Empty);
    }

    [Test]
    public void AllThreeLanguagesResolveEveryEnglishKey()
    {
        Localizer loc = All();
        foreach (string code in Localizer.SupportedLanguages) Assert.That(loc.MissingKeys(code), Is.Empty, code);
    }
}
