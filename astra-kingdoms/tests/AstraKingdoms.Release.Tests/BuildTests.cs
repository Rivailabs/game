using System.Text.Json.Nodes;
using AstraKingdoms.Release.Gates;
using NUnit.Framework;

namespace AstraKingdoms.Release.Tests;

[TestFixture]
public class SizeTests
{
    [Test]
    public void DownloadUnderBudgetPassesOverFailsMissingIncomplete()
    {
        Assert.That(SizeGate.Evaluate(new SizeRecord { InitialDownloadBytes = 79_999_999, DeviceSpec = "ref.json", AabBytes = 1 }).Overall, Is.Not.EqualTo(GateStatus.Fail));
        Assert.That(SizeGate.Evaluate(new SizeRecord { InitialDownloadBytes = 80_000_001, DeviceSpec = "ref.json" }).Has("download", GateStatus.Fail));
        GateReport missing = SizeGate.Evaluate(new SizeRecord { ApkBytes = 50_000_000 });
        Assert.That(missing.Has("download", GateStatus.Incomplete));
        Assert.That(missing.Findings.First(f => f.Id == "download").Message, Does.Contain("not the defined device-specific download"));
    }

    [Test]
    public void UnnamedDeviceSpecIsIncomplete() =>
        Assert.That(SizeGate.Evaluate(new SizeRecord { InitialDownloadBytes = 10, AabBytes = 1 }).Has("download.spec", GateStatus.Incomplete));

    [Test]
    public void ParsesBundletoolGetSizeCsv()
    {
        Assert.That(SizeCheck.ParseBundletoolSize("MIN,MAX\n41234567,45678901\n"), Is.EqualTo(45678901));
        Assert.That(SizeCheck.ParseBundletoolSize("SDK,ABI,MIN,MAX\n21,arm64-v8a,100,200\n23,arm64-v8a,150,250\n"), Is.EqualTo(250));
        Assert.That(SizeCheck.ParseBundletoolSize("garbage"), Is.Null);
    }

    [Test]
    public void SummarisesTheEditorBuildReportExport()
    {
        var report = new JsonObject
        {
            ["format"] = "AK-BUILD-REPORT/1", ["result"] = "Succeeded", ["platform"] = "Android", ["total_size"] = 1000,
            ["top_assets"] = new JsonArray(new JsonObject { ["path"] = "Assets/Art/a.png", ["packed_size"] = 600 }),
            ["packed_by_type"] = new JsonArray(new JsonObject { ["type"] = "AudioClip", ["packed_size"] = 100 }, new JsonObject { ["type"] = "Texture2D", ["packed_size"] = 700 }),
        };
        JsonObject s = SizeCheck.SummariseBuildReport(report);
        Assert.That(s["top_assets"]![0]!["path"]!.GetValue<string>(), Is.EqualTo("Assets/Art/a.png"));
        Assert.That(s["packed_by_type"]![0]!["type"]!.GetValue<string>(), Is.EqualTo("Texture2D"));
        Assert.Throws<FormatException>(() => SizeCheck.SummariseBuildReport(new JsonObject { ["format"] = "x" }));
    }

    [Test]
    public void CliRecordsSizesSeparately()
    {
        using var t = new TempDir();
        string aab = t.File("astra.aab", new string('x', 1234));
        string csv = t.File("size.csv", "MIN,MAX\n60000000,61000000\n");
        string outPath = Path.Combine(t.Path, "size.json");
        (int code, string output) = Cli.Run("size-check", "--aab", aab, "--bundletool-size", csv, "--device-spec", "ref.json",
            "--installed-bytes", "150000000", "--on-demand-bytes", "0", "--out", outPath);
        Assert.That(code, Is.EqualTo(0), output);
        JsonNode rec = JsonNode.Parse(File.ReadAllText(outPath))!["size_record"]!;
        Assert.That(rec["aab_bytes"]!.GetValue<long>(), Is.EqualTo(1234));
        Assert.That(rec["initial_download_bytes"]!.GetValue<long>(), Is.EqualTo(61000000));
        Assert.That(rec["installed_bytes"]!.GetValue<long>(), Is.EqualTo(150000000));
    }
}

[TestFixture]
public class ReproTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["AndroidManifest.xml"] = "<manifest/>",
        ["lib/arm64-v8a/libil2cpp.so"] = "native",
        ["assets/bin/Data/data.unity3d"] = "data",
        ["META-INF/CERT.SF"] = "sig-a",
    };

    [Test]
    public void IdenticalEntriesWithDifferentContainerBytesPass()
    {
        using var t = new TempDir();
        string a = t.Zip("a.aab", Files, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        string b = t.Zip("b.aab", Files, DateTimeOffset.Parse("2026-02-01T00:00:00Z"), reverse: true);
        ReproCompare.Result r = ReproCompare.Run(new ReproCompare.Options { A = a, B = b });
        Assert.That(r.Report.Overall, Is.EqualTo(GateStatus.Pass), r.Report.ToText());
        Assert.That(r.Report.Findings.Single().Message, Does.Contain("container bytes differ"));
    }

    [Test]
    public void BitIdenticalArtifactsPass()
    {
        using var t = new TempDir();
        string a = t.Zip("a.apk", Files, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        string b = Path.Combine(t.Path, "b.apk");
        File.Copy(a, b);
        Assert.That(ReproCompare.Run(new ReproCompare.Options { A = a, B = b }).Report.Has("artifact", GateStatus.Pass));
    }

    [Test]
    public void DifferingEntriesFailAndAreListed()
    {
        using var t = new TempDir();
        var other = new Dictionary<string, string>(Files) { ["assets/bin/Data/data.unity3d"] = "DATA", ["META-INF/CERT.SF"] = "sig-b", ["extra.txt"] = "x" };
        other.Remove("AndroidManifest.xml");
        ReproCompare.Result r = ReproCompare.Run(new ReproCompare.Options { A = t.Zip("a.aab", Files), B = t.Zip("b.aab", other), IgnoreSigning = true });
        Assert.That(r.Differing, Is.EqualTo(new[] { "assets/bin/Data/data.unity3d" }));
        Assert.That(r.OnlyInA, Is.EqualTo(new[] { "AndroidManifest.xml" }));
        Assert.That(r.OnlyInB, Is.EqualTo(new[] { "extra.txt" }));
        Assert.That(r.Report.Overall, Is.EqualTo(GateStatus.Fail));
    }

    [Test]
    public void SignaturesDifferUnlessIgnored()
    {
        using var t = new TempDir();
        var other = new Dictionary<string, string>(Files) { ["META-INF/CERT.SF"] = "sig-b" };
        string a = t.Zip("a.aab", Files), b = t.Zip("b.aab", other);
        Assert.That(ReproCompare.Run(new ReproCompare.Options { A = a, B = b }).Differing, Is.EqualTo(new[] { "META-INF/CERT.SF" }));
        Assert.That(ReproCompare.Run(new ReproCompare.Options { A = a, B = b, IgnoreSigning = true }).Report.Overall, Is.EqualTo(GateStatus.Pass));
    }

    [Test]
    public void AllowlistedDifferencesWarnWithReason()
    {
        using var t = new TempDir();
        var other = new Dictionary<string, string>(Files) { ["assets/bin/Data/data.unity3d"] = "DATA" };
        ReproCompare.Result r = ReproCompare.Run(new ReproCompare.Options
        {
            A = t.Zip("a.aab", Files), B = t.Zip("b.aab", other),
            Allowlist = { new ReproCompare.Allowed { Pattern = "assets/**/*.unity3d", Reason = "embedded build time" } },
        });
        Assert.That(r.Report.Overall, Is.EqualTo(GateStatus.Warn));
        Assert.That(r.Accepted.Single().Reason, Is.EqualTo("embedded build time"));
    }

    [Test]
    public void ComparesOutputFoldersIncludingNestedArchives()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Path, "a"));
        Directory.CreateDirectory(Path.Combine(t.Path, "b"));
        t.File("a/build-report.json", "{}");
        t.File("b/build-report.json", "{}");
        t.Zip("a/astra.aab", Files);
        t.Zip("b/astra.aab", new Dictionary<string, string>(Files) { ["lib/arm64-v8a/libil2cpp.so"] = "other" });
        ReproCompare.Result r = ReproCompare.Run(new ReproCompare.Options { A = Path.Combine(t.Path, "a"), B = Path.Combine(t.Path, "b") });
        Assert.That(r.Differing, Is.EqualTo(new[] { "astra.aab!lib/arm64-v8a/libil2cpp.so" }));
    }

    [TestCase("META-INF/*.SF", "META-INF/CERT.SF", true)]
    [TestCase("META-INF/*.SF", "META-INF/sub/CERT.SF", false)]
    [TestCase("assets/**", "assets/a/b/c", true)]
    [TestCase("a?c", "abc", true)]
    public void GlobMatching(string pattern, string path, bool match) => Assert.That(ReproCompare.Glob(pattern).IsMatch(path), Is.EqualTo(match));

    [Test]
    public void CommittedAllowlistLoads() => Assert.That(ReproCompare.LoadAllowlist(Repo.P("release", "packaging", "repro-allowlist.json")), Is.Empty);
}

[TestFixture]
public class ImportPolicyTests
{
    [Test]
    public void IconsAreUncompressedFreeSpritesWithoutMipmaps()
    {
        TexturePreset p = ImportPolicy.ForTexture("Assets/Art/Placeholder/Icons/element-agni.png");
        Assert.That(p.Use, Is.EqualTo(TextureUse.UiSprite));
        Assert.That(p.Sprite && !p.Mipmaps && p.SRgb && p.AlphaIsTransparency, Is.True);
        Assert.That(p.MaxSize, Is.LessThanOrEqualTo(1024));
    }

    [Test]
    public void WorldTexturesFollowTheThousandPixelCeilingAndColourSpace()
    {
        TexturePreset colour = ImportPolicy.ForTexture("Assets/Art/Archer/archer_base.png");
        TexturePreset normal = ImportPolicy.ForTexture("Assets/Art/Archer/archer_n.png");
        Assert.That(colour.SRgb && colour.Mipmaps && colour.MaxSize == 1024, Is.True);
        Assert.That(normal.Use, Is.EqualTo(TextureUse.WorldData));
        Assert.That(normal.SRgb, Is.False);
        Assert.That(ImportPolicy.ForTexture("Assets/Art/VFX/agni_flipbook.png").MaxSize, Is.EqualTo(512));
    }

    [Test]
    public void OnlyAssetsArtIsManaged()
    {
        Assert.That(ImportPolicy.ForTexture("Assets/Resources/foo.png"), Is.Null);
        Assert.That(ImportPolicy.ForAudio("Assets/Audio/x.wav", 1), Is.Null);
        Assert.That(ImportPolicy.ClassifyTexture(@"Assets\Art\Placeholder\Icons\x.png"), Is.EqualTo(TextureUse.UiSprite));
    }

    [Test]
    public void ShortEffectsDecompressLongEffectsStayCompressedMusicStreams()
    {
        AudioPreset shortFx = ImportPolicy.ForAudio("Assets/Art/Placeholder/Audio/Sfx/sfx-hit.wav", 0.26);
        AudioPreset longFx = ImportPolicy.ForAudio("Assets/Art/Placeholder/Audio/Sfx/sfx-victory.wav", 1.34);
        AudioPreset music = ImportPolicy.ForAudio("Assets/Art/Placeholder/Audio/Music/music-menu.wav", 24);
        Assert.That((shortFx.ForceMono, shortFx.LoadType, shortFx.Compression), Is.EqualTo((true, "DecompressOnLoad", "ADPCM")));
        Assert.That((longFx.ForceMono, longFx.LoadType, longFx.Compression), Is.EqualTo((true, "CompressedInMemory", "Vorbis")));
        Assert.That((music.LoadType, music.Compression, music.LoadInBackground, music.PreloadAudioData), Is.EqualTo(("Streaming", "Vorbis", true, false)));
    }

    [Test]
    public void EveryCommittedPlaceholderHasAPreset()
    {
        string art = Repo.P("unity", "Assets", "Art");
        foreach (string f in Directory.EnumerateFiles(art, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".png") || f.EndsWith(".wav")))
        {
            string rel = Path.GetRelativePath(Repo.P("unity"), f).Replace('\\', '/');
            if (f.EndsWith(".png")) Assert.That(ImportPolicy.ForTexture(rel), Is.Not.Null, rel);
            else Assert.That(ImportPolicy.ForAudio(rel, null), Is.Not.Null, rel);
        }
    }
}
