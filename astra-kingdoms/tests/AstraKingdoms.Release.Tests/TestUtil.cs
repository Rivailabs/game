using System.IO.Compression;
using System.Text.Json.Nodes;
using AstraKingdoms.Release.Gates;
using NUnit.Framework;

namespace AstraKingdoms.Release.Tests;

internal static class Repo
{
    /// <summary>astra-kingdoms/ (the folder holding AstraKingdoms.sln).</summary>
    public static string Ak { get; } = FindAk();

    public static string Root => Path.GetDirectoryName(Ak)!;

    public static string P(params string[] parts) => Path.Combine(new[] { Ak }.Concat(parts).ToArray());

    private static string FindAk()
    {
        string d = TestContext.CurrentContext.TestDirectory;
        while (d != null && !File.Exists(Path.Combine(d, "AstraKingdoms.sln"))) d = Path.GetDirectoryName(d);
        return d ?? throw new InvalidOperationException("AstraKingdoms.sln not found above the test directory.");
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ak-release-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, string text)
    {
        string p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, text);
        return p;
    }

    public string Zip(string name, IDictionary<string, string> entries, DateTimeOffset? stamp = null, bool reverse = false)
    {
        string p = System.IO.Path.Combine(Path, name);
        using (ZipArchive z = ZipFile.Open(p, ZipArchiveMode.Create))
        {
            IEnumerable<KeyValuePair<string, string>> items = reverse ? entries.Reverse() : entries;
            foreach (KeyValuePair<string, string> kv in items)
            {
                ZipArchiveEntry e = z.CreateEntry(kv.Key);
                if (stamp.HasValue) e.LastWriteTime = stamp.Value;
                using var w = new StreamWriter(e.Open());
                w.Write(kv.Value);
            }
        }
        return p;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Gate
{
    public static IEnumerable<GateFinding> With(this GateReport r, GateStatus s) => r.Findings.Where(f => f.Status == s);

    public static bool Has(this GateReport r, string id, GateStatus s) => r.Findings.Any(f => f.Id == id && f.Status == s);

    public static JsonNode Json(string text) => JsonNode.Parse(text)!;
}

internal static class Cli
{
    private static readonly object Lock = new();

    /// <summary>Runs Program.Main with captured stdout/stderr.</summary>
    public static (int Code, string Out) Run(params string[] args)
    {
        lock (Lock)
        {
            TextWriter o = Console.Out, e = Console.Error;
            var sw = new StringWriter();
            Console.SetOut(sw);
            Console.SetError(sw);
            try
            {
                return (Program.Main(args), sw.ToString());
            }
            finally
            {
                Console.SetOut(o);
                Console.SetError(e);
            }
        }
    }
}
