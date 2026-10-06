using AstraKingdoms.Client.Assets;
using AstraKingdoms.Client.Audio;
using AstraKingdoms.Client.Combat;
using AstraKingdoms.Client.Land;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Client.Presentation;

namespace AstraKingdoms.Client.Tests;

/// <summary>Ticket 48 localization (formatting, plurals, fallback, glossary, font coverage) plus the audio cue registry and asset ledger.</summary>
public sealed class LocalizationV1Tests
{
    private static LocalizationTable Load(string code) =>
        LocalizationTable.Parse(code, File.ReadAllText(Path.Combine(Paths.Localization, code + ".txt")));

    [TestCase("en", 0, "0 cells")]
    [TestCase("en", 1, "1 cell")]
    [TestCase("en", 2, "2 cells")]
    [TestCase("hi", 0, "0 सेल")]
    [TestCase("hi", 1, "1 सेल")]
    [TestCase("hi", 5, "5 सेलें")]
    public void Plurals_FollowEachLanguagesRules(string lang, int n, string expected)
    {
        const string en = "{0,plural,one{# cell} other{# cells}}";
        const string hi = "{0,plural,one{# सेल} other{# सेलें}}";
        Assert.That(MessageFormat.Format(lang == "en" ? en : hi, lang, n), Is.EqualTo(expected));
    }

    [Test]
    public void PluralRules_MatchCldrForIntegers()
    {
        Assert.That(PluralRules.Select("en", 0), Is.EqualTo(PluralCategory.Other));
        Assert.That(PluralRules.Select("en", 1), Is.EqualTo(PluralCategory.One));
        Assert.That(PluralRules.Select("hi", 0), Is.EqualTo(PluralCategory.One));
        Assert.That(PluralRules.Select("kn", 1), Is.EqualTo(PluralCategory.One));
        Assert.That(PluralRules.Select("kn", 2), Is.EqualTo(PluralCategory.Other));
    }

    [Test]
    public void Numbers_UseTheLanguagesGrouping()
    {
        Assert.That(MessageFormat.GroupedInteger(51040, "en"), Is.EqualTo("51,040"));
        Assert.That(MessageFormat.GroupedInteger(1234567, "en"), Is.EqualTo("1,234,567"));
        Assert.That(MessageFormat.GroupedInteger(1234567, "hi"), Is.EqualTo("12,34,567"));
        Assert.That(MessageFormat.GroupedInteger(123456, "kn"), Is.EqualTo("1,23,456"));
        Assert.That(MessageFormat.GroupedInteger(999, "hi"), Is.EqualTo("999"));
        Assert.That(MessageFormat.GroupedInteger(-1000, "en"), Is.EqualTo("-1,000"));
        Assert.That(MessageFormat.Format("{0,number} of {1,number}", "en", 25520, 51040), Is.EqualTo("25,520 of 51,040"));
    }

    [Test]
    public void Templates_SubstituteParametersWithoutConcatenation()
    {
        Assert.That(MessageFormat.Format("{1} took {0,plural,one{# cell from {2}} other{# cells from {2}}}.", "en", 3, "Asha", "Bala"),
            Is.EqualTo("Asha took 3 cells from Bala."));
        Assert.That(MessageFormat.ArgumentCount("{1} took {0,plural,one{#} other{# {2}}}"), Is.EqualTo(3));
        Assert.That(MessageFormat.Validate("{0,plural,one{x}}"), Does.Contain("other"));
        Assert.That(MessageFormat.Validate("{0,bogus}"), Does.Contain("Unknown"));
        Assert.That(MessageFormat.Validate("text }"), Is.Not.Null);
        Assert.That(MessageFormat.Validate("{0,plural,one{# a} other{# b}}"), Is.Null);
        Assert.Throws<FormatException>(() => MessageFormat.Format("{0,plural,one{a} other{b}}", "en", "not a number"));
    }

    [Test]
    public void Fallback_UsesEnglishThenAPlaceholder_NeverARawKey()
    {
        var loc = new Localizer(LocalizationTable.Parse("en", "k = {0,plural,one{# cell} other{# cells}}\nonly = English"));
        loc.AddLanguage(LocalizationTable.Parse("kn", "k = {0,plural,one{# broken"));
        loc.Language = "kn";
        var missing = new List<string>();
        loc.MissingKey += (lang, key) => missing.Add(lang + ":" + key);
        Assert.That(loc.Format("k", 2), Is.EqualTo("2 cells"), "a malformed translation falls back to English");
        Assert.That(loc.Get("only"), Is.EqualTo("English"));
        Assert.That(loc.Format("nope", 1), Is.EqualTo(Localizer.MissingPlaceholder));
        Assert.That(missing, Does.Contain("kn:k").And.Contain("kn:only"));
        Assert.That(loc.Number(51040), Is.EqualTo("51,040"));
    }

    [TestCase("en")]
    [TestCase("hi")]
    [TestCase("kn")]
    public void EveryTemplateIsWellFormed(string code)
    {
        LocalizationTable t = Load(code);
        foreach (string key in t.Keys)
        {
            t.TryGet(key, out string v);
            Assert.That(MessageFormat.Validate(v), Is.Null, code + ":" + key);
        }
    }

    [TestCase("en")]
    [TestCase("hi")]
    [TestCase("kn")]
    public void Glossary_ProperNamesAreSpelledConsistently(string code)
    {
        Glossary g = Glossary.Parse(File.ReadAllText(Path.Combine(Paths.Localization, "glossary.txt")));
        Assert.That(g.Errors, Is.Empty);
        Assert.That(g.Entries, Has.Count.GreaterThanOrEqualTo(39));
        Assert.That(g.Inconsistencies(Load(code)), Is.Empty);
        Assert.That(Glossary.Parse("a | keep | X | Y | X |").Errors, Is.Not.Empty, "'keep' means identical in every script");
    }

    [TestCase("en")]
    [TestCase("hi")]
    [TestCase("kn")]
    public void FontPlan_CoversEveryCharacterTheTablesUse(string code)
    {
        LocalizationTable t = Load(code);
        Assert.That(FontCoverage.Uncovered(t).Select(FontCoverage.Describe), Is.Empty);
        IReadOnlyList<string> faces = FontCoverage.FacesNeeded(t);
        if (code == "hi") Assert.That(faces, Does.Contain(FontCoverage.DevanagariFace));
        if (code == "kn") Assert.That(faces, Does.Contain(FontCoverage.KannadaFace));
        string atlas = FontCoverage.AtlasCharacters(new[] { t }, code == "kn" ? FontCoverage.KannadaFace : code == "hi" ? FontCoverage.DevanagariFace : FontCoverage.LatinFace);
        Assert.That(atlas, Is.Not.Empty);
    }

    [Test]
    public void CueAndStatusGlyphs_AreInThePlannedFonts()
    {
        var glyphs = new List<string>(HudModel.StatusGlyphs);
        foreach (CueKind k in VolleyCueBuilder.AllKinds) glyphs.Add(VolleyCueBuilder.GlyphFor(k));
        foreach (Rules.Core.Dodge d in Enum.GetValues<Rules.Core.Dodge>()) glyphs.Add(VolleyCueBuilder.GlyphFor(CueKind.Dodge, d));
        foreach (string g in glyphs)
            foreach (char c in g)
                Assert.That(FontCoverage.Plan.Any(r => r.Contains(c)), Is.True, "glyph " + FontCoverage.Describe(c));
    }

    [TestCase("hi")]
    [TestCase("kn")]
    public void DraftTranslations_StayMarkedForFluentReview(string code)
    {
        Assert.That(string.Join("\n", Load(code).HeaderComments), Does.Contain("needs fluent-speaker review"));
    }

    /// <summary>Keys built at run time by the V1 presentation code.</summary>
    [Test]
    public void DynamicV1KeyFamiliesExist()
    {
        LocalizationTable en = Load("en");
        var keys = new List<string>();
        foreach (CueKind k in VolleyCueBuilder.AllKinds) keys.Add(VolleyCueBuilder.CaptionKeyFor(k));
        keys.Add(VolleyCueBuilder.CaptionKeyFor(CueKind.Impact, Rules.Combat.ContactKind.Graze));
        keys.Add(VolleyCueBuilder.CaptionKeyFor(CueKind.Impact) + "Covered");
        keys.Add(VolleyCueBuilder.CaptionKeyFor(CueKind.Impact, Rules.Combat.ContactKind.Graze) + "Covered");
        foreach (Rules.Match.MatchPhase ph in Enum.GetValues<Rules.Match.MatchPhase>())
            keys.Add("phase." + char.ToLowerInvariant(ph.ToString()[0]) + ph.ToString().Substring(1));
        foreach (string k in new[] { "loadout.loaned", "loadout.hintStarter", "loadout.hintFull", "loadout.pickReserveActive", "catalog.starter", "catalog.full", "phase.unknown" })
            keys.Add(k);
        foreach (GestureHint h in Enum.GetValues<GestureHint>())
            if (h != GestureHint.None) keys.Add(CutGesture.HintKey(h));
        foreach (QuotaLimit q in Enum.GetValues<QuotaLimit>()) keys.Add(ReasonKey(q));
        foreach (ArenaVariant v in ArenaVariants.All) keys.Add(v.NameKey);
        foreach (Flow.SessionOverlay o in Enum.GetValues<Flow.SessionOverlay>()) keys.Add("session.overlay." + o);
        Assert.That(keys.Where(k => !en.Contains(k)), Is.Empty);
    }

    private static string ReasonKey(QuotaLimit q) =>
        q == QuotaLimit.Floor ? "land.reason.floor" : q == QuotaLimit.LoserLand ? "land.reason.loserLand" : "land.reason.margin";

    // ------------------------------------------------------------------ audio and ledger

    [Test]
    public void AudioCues_CoverThePlanListAndAreUnderstandableMuted()
    {
        var cues = AudioCueRegistry.All.Select(s => s.Cue).ToList();
        Assert.That(cues, Is.SupersetOf(new[] { AudioCue.Draw, AudioCue.Release, AudioCue.Clash, AudioCue.Hit, AudioCue.Dodge, AudioCue.CardSelect, AudioCue.LandTransfer, AudioCue.Victory, AudioCue.UiClick }));
        Assert.That(cues, Is.Unique);
        foreach (AudioCueSpec s in AudioCueRegistry.All)
            Assert.That(s.VisualCounterpart, Is.Not.Empty, s.Cue + " has a visual twin");
        Assert.That(AudioMix.Volume(AudioChannel.Music, 0.2f, 0.9f), Is.EqualTo(0.2f));
        Assert.That(AudioMix.Volume(AudioChannel.Effects, 0.2f, 0.9f), Is.EqualTo(0.9f));
        Assert.That(AudioMix.Volume(AudioChannel.Ui, 0.2f, 0f), Is.EqualTo(0f), "UI follows effects, independently of music");
        Assert.That(AudioMix.Volume(AudioChannel.Music, float.NaN, 1f), Is.EqualTo(0f));
        Assert.That(AudioCueRegistry.ResolveLedgerId(AudioCue.Draw), Is.Null, "silent placeholder until a licensed clip exists");
        Assert.That(AudioCueRegistry.ResolveLedgerId(AudioCue.Hit), Is.EqualTo("audio.placeholder.hit"));
    }

    [Test]
    public void AssetLedger_IsValidAndResolvesEveryReference()
    {
        string path = Path.Combine(Paths.Unity, "Assets", "Resources", "Ledger", "asset-ledger.json");
        AssetLedger ledger = AssetLedger.Parse(File.ReadAllText(path));
        Assert.That(ledger.Validate(), Is.Empty);
        var referenced = AudioCueRegistry.All.Select(s => s.LedgerId ?? s.PlaceholderLedgerId).Where(id => id != null).ToList();
        referenced.AddRange(FontCoverage.Plan.Select(r => r.PlannedFace).Distinct());
        referenced.Add("font.legacy-runtime");
        referenced.Add("model.archer-placeholder");
        referenced.Add("animation.archer-placeholder-clips");
        Assert.That(ledger.UnresolvedReferences(referenced), Is.Empty);
        Assert.That(AssetLedger.Parse(ledger.ToJson()).Entries, Has.Count.EqualTo(ledger.Entries.Count), "round trip");

        var bad = new AssetLedger();
        bad.Add(new LedgerEntry { Id = "x", Kind = LedgerKind.Sound, Status = LedgerStatus.Approved, Purpose = "hit", Path = "Assets/a.wav" });
        bad.Add(new LedgerEntry { Id = "x", Kind = LedgerKind.Model, Status = LedgerStatus.Placeholder, Purpose = "p", Path = "Assets/real.fbx" });
        IReadOnlyList<string> problems = bad.Validate();
        Assert.That(problems, Has.Some.Contains("rights holder"));
        Assert.That(problems, Has.Some.Contains("SHA-256"));
        Assert.That(problems, Has.Some.Contains("duplicate"));
        Assert.That(problems, Has.Some.Contains("placeholder"));
    }
}
