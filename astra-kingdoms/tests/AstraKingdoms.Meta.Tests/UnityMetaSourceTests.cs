using System.Text.RegularExpressions;
using NUnit.Framework;

namespace AstraKingdoms.Meta.Tests;

/// <summary>
/// Source-level checks on unity/Assets/Scripts/Meta (the Unity side compiles only against stubs
/// here, so these guard what the compile check cannot): string keys exist, server-only code and
/// combat commands are never referenced, and the core client keeps only the additive hook.
/// </summary>
public class UnityMetaSourceTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AstraKingdoms.sln"))) dir = dir.Parent;
            Assert.That(dir, Is.Not.Null);
            return dir.FullName;
        }
    }

    private static string MetaScripts => Path.Combine(Root, "unity", "Assets", "Scripts", "Meta");

    private static IEnumerable<string> Sources() => Directory.GetFiles(MetaScripts, "*.cs", SearchOption.AllDirectories);

    private static Dictionary<string, string> EnglishTable()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in File.ReadAllLines(Path.Combine(MetaScripts, "Resources", "MetaLocalization", "en.txt")))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            Assert.That(eq, Is.GreaterThan(0), line);
            string key = line[..eq].Trim();
            Assert.That(table.ContainsKey(key), Is.False, "duplicate key " + key);
            table[key] = line[(eq + 1)..].Trim();
        }
        return table;
    }

    [Test]
    public void EveryLiteralMetaKeyExistsInTheEnglishTable()
    {
        Dictionary<string, string> en = EnglishTable();
        var pattern = new Regex("\\b(?:L|LF)\\(\\s*\"([a-zA-Z][a-zA-Z0-9]*(?:\\.[a-zA-Z0-9]+)+)\"");
        var used = new HashSet<string>();
        foreach (string file in Sources())
            foreach (Match m in pattern.Matches(File.ReadAllText(file)))
                used.Add(m.Groups[1].Value);
        Assert.That(used.Count, Is.GreaterThan(40), "the key scan found too few keys");
        Assert.That(used.Where(k => !en.ContainsKey(k)), Is.Empty);
        Assert.That(en.Keys.Where(k => !used.Contains(k)), Is.Empty, "unused keys in en.txt");
    }

    [Test]
    public void ClientNeverReferencesServerOnlyCode()
    {
        foreach (string file in Sources())
        {
            string text = File.ReadAllText(file);
            Assert.That(text, Does.Not.Contain("AstraKingdoms.Meta.Server"), file);
            Assert.That(text, Does.Not.Contain("GooglePlayPurchaseVerifier"), file);
            Assert.That(text, Does.Not.Contain("ServiceAccount"), file);
        }
    }

    [Test]
    public void MetaLayerNeverIssuesCombatCommands()
    {
        string[] forbidden = { "SubmitLock", "SubmitLoadout", "SubmitCut", "LockInputCommand(", "SubmitLoadoutCommand(", "VolleyInput(" };
        foreach (string file in Sources())
        {
            string text = File.ReadAllText(file);
            foreach (string f in forbidden) Assert.That(text, Does.Not.Contain(f), file);
        }
    }

    [Test]
    public void TheCoreClientOnlyGainsTheAdditiveHook()
    {
        string runtime = Path.Combine(Root, "unity", "Assets", "Scripts", "Runtime");
        foreach (string file in Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories))
            Assert.That(File.ReadAllText(file), Does.Not.Contain("AstraKingdoms.Client.Meta"), file);
        string flow = File.ReadAllText(Path.Combine(runtime, "GameFlow.cs"));
        Assert.That(flow, Does.Contain("public static event Action<GameFlow> UiBuilt;"));
        Assert.That(flow, Does.Contain("UiBuilt?.Invoke(this);"));
    }

    [Test]
    public void IapAdapterIsIsolatedBehindItsDefine()
    {
        string iap = Path.Combine(MetaScripts, "Store", "Iap");
        string asmdef = File.ReadAllText(Path.Combine(iap, "AstraKingdoms.Client.Meta.Iap.asmdef"));
        Assert.That(asmdef, Does.Contain("\"AK_UNITY_IAP\"").And.Contain("com.unity.purchasing"));
        foreach (string file in Sources().Where(f => !f.StartsWith(iap, StringComparison.Ordinal)))
            Assert.That(File.ReadAllText(file), Does.Not.Contain("UnityEngine.Purchasing"), file);
    }
}
