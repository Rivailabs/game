using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AstraKingdoms.Release.Gates;

namespace AstraKingdoms.Release;

/// <summary>Minimal <c>--key value</c> parser; keys may repeat (e.g. several <c>--ledger</c>).</summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public CliArgs(IEnumerable<string> args, IEnumerable<string> flags = null)
    {
        var flagSet = new HashSet<string>(flags ?? Array.Empty<string>(), StringComparer.Ordinal);
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected argument '" + a + "'.");
            string key = a[2..];
            if (flagSet.Contains(key))
            {
                _flags.Add(key);
                continue;
            }
            if (i + 1 >= list.Count) throw new ArgumentException("Missing value for " + a + ".");
            if (!_values.TryGetValue(key, out List<string> vs)) _values[key] = vs = new List<string>();
            vs.Add(list[++i]);
        }
    }

    public bool Flag(string key) => _flags.Contains(key);

    public string Get(string key, string fallback = null) => _values.TryGetValue(key, out List<string> v) ? v[^1] : fallback;

    public string Require(string key) => Get(key) ?? throw new ArgumentException("--" + key + " is required.");

    public IReadOnlyList<string> All(string key) => _values.TryGetValue(key, out List<string> v) ? v : Array.Empty<string>();

    public long? Long(string key)
    {
        string v = Get(key);
        return v == null ? null : long.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}

public static class Util
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Sha256File(string path)
    {
        using FileStream s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public static string Sha256Bytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Text(string text) => Sha256Bytes(Encoding.UTF8.GetBytes(text));

    public static JsonNode ReadJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
        ?? throw new FormatException(path + " is empty.");

    public static void WriteJson(string path, JsonNode node)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, node.ToJsonString(Pretty) + "\n");
    }

    public static string Str(JsonNode n, string key, string fallback = "") => n?[key] is JsonValue v && v.TryGetValue(out string s) ? s : fallback;

    public static long? LongOrNull(JsonNode n, string key)
    {
        if (n?[key] is not JsonValue v) return null;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out double d)) return (long)Math.Round(d);
        return null;
    }

    public static bool Bool(JsonNode n, string key, bool fallback = false) => n?[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
}

/// <summary>AK-GATE-RESULT/1: the evidence file every gate writes (also written by the editor ReleaseGate).</summary>
public static class GateJson
{
    public const string Format = "AK-GATE-RESULT/1";

    public static JsonObject ToJson(GateReport r, JsonObject extra = null)
    {
        var findings = new JsonArray();
        foreach (GateFinding f in r.Findings)
            findings.Add(new JsonObject { ["id"] = f.Id, ["status"] = GateReport.Label(f.Status), ["message"] = f.Message });
        var o = new JsonObject { ["format"] = Format, ["gate"] = r.Gate, ["status"] = GateReport.Label(r.Overall), ["findings"] = findings };
        if (extra != null)
            foreach (KeyValuePair<string, JsonNode> kv in extra.ToList())
            {
                extra.Remove(kv.Key);
                o[kv.Key] = kv.Value;
            }
        return o;
    }

    /// <summary>Prints the report, writes <c>--out</c> when given and returns the gate's exit code.</summary>
    public static int Emit(GateReport r, string outPath, JsonObject extra = null)
    {
        Console.Write(r.ToText());
        if (!string.IsNullOrEmpty(outPath))
        {
            Util.WriteJson(outPath, ToJson(r, extra));
            Console.WriteLine("wrote " + outPath);
        }
        return r.ExitCode;
    }
}
