using System.Text.RegularExpressions;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Sim;

/// <summary>A playtest file that could not be used, and why.</summary>
public readonly record struct IngestExclusion(string File, string Reason);

/// <summary>Result of ingesting a folder of playtest records.</summary>
public sealed class IngestResult
{
    public List<MatchObservation> Observations { get; } = new();
    public List<IngestExclusion> Excluded { get; } = new();
    public int SyntheticIncluded => Observations.Count(o => o.Synthetic);
}

/// <summary>
/// Ingest format for human playtest matches (ticket 25), <c>AK-PLAYTEST-RECORD/1</c>: the
/// ordinary deterministic match record (<c>AK-MATCH-RECORD/1</c>) wrapped with seat metadata.
/// <code>
/// {
///   "format": "AK-PLAYTEST-RECORD/1",
///   "session_id": "pt-2026-10-12-a",          // playtest session (ASCII)
///   "collected_at_utc": "2026-10-12T10:30:00Z",
///   "build": "commit or version string",
///   "synthetic": false,                        // true only for generated examples
///   "seats": {
///     "A": { "kind": "human", "player_ref": "T03", "account_level": 4, "consent_recorded": true },
///     "B": { "kind": "bot", "policy": "Hard" }
///   },
///   "notes": "free ASCII text, no personal data",
///   "match_record": { ...AK-MATCH-RECORD/1... }
/// }
/// </code>
/// Privacy: people appear only as a pseudonymous <c>player_ref</c> (letters, digits, '-' or '_',
/// at most 24 characters) with a recorded consent flag; no names, contacts or device IDs. Every
/// record is re-executed by <see cref="Replayer.Verify"/> before use, so a tampered or
/// incompatible record is excluded (with its reason) rather than counted.
/// </summary>
public static class PlaytestRecord
{
    public const string Format = "AK-PLAYTEST-RECORD/1";
    private static readonly Regex PlayerRefPattern = new("^[A-Za-z0-9_-]{1,24}$", RegexOptions.CultureInvariant);

    /// <summary>Serializes a finished match with seat metadata.</summary>
    public static string Write(MatchRecord record, SeatInfo a, SeatInfo b, string sessionId, string collectedAtUtc, string build,
        bool synthetic, string notes = "")
    {
        JsonNode Seat(SeatInfo s)
        {
            JsonNode n = JsonNode.Object().Add("kind", s.Kind);
            if (s.IsHuman) n.Add("player_ref", s.PlayerRef).Add("consent_recorded", true);
            else n.Add("policy", s.Label);
            n.Add("account_level", s.AccountLevel.HasValue ? JsonNode.Of(s.AccountLevel.Value) : JsonNode.Null);
            return n;
        }

        return JsonNode.Object()
            .Add("format", Format)
            .Add("session_id", sessionId)
            .Add("collected_at_utc", collectedAtUtc)
            .Add("build", build)
            .Add("synthetic", synthetic)
            .Add("seats", JsonNode.Object().Add("A", Seat(a)).Add("B", Seat(b)))
            .Add("notes", notes)
            .Add("match_record", MatchRecordSerializer.ToNode(record))
            .ToCanonicalString();
    }

    /// <summary>Parses and verifies one playtest file. Returns null and a reason when it is unusable.</summary>
    public static MatchObservation? Read(string json, bool allowSynthetic, out string? reason)
    {
        reason = null;
        JsonNode root;
        try
        {
            root = JsonNode.Parse(json);
            if (root.Kind != JsonKind.Object) throw new FormatException("Top level must be an object.");
            if (root["format"].AsString() != Format) throw new FormatException("Unsupported format.");
        }
        catch (FormatException e)
        {
            reason = "malformed: " + e.Message;
            return null;
        }
        catch (OverflowException e)
        {
            reason = "malformed: " + e.Message;
            return null;
        }

        try
        {
            bool synthetic = root["synthetic"].AsBool();
            if (synthetic && !allowSynthetic)
            {
                reason = "synthetic example record (use --include-synthetic to include it)";
                return null;
            }
            SeatInfo a = ReadSeat(root["seats"]["A"]);
            SeatInfo b = ReadSeat(root["seats"]["B"]);
            ReplayReport report = Replayer.Verify(root["match_record"].ToCanonicalString());
            if (!report.Success)
            {
                reason = "record failed verification: " + report.Failure + " - " + report.Detail;
                return null;
            }
            if (report.Engine.Result == null)
            {
                reason = "match did not finish";
                return null;
            }
            return MatchObservation.FromEngine(report.Engine, a, b, MatchObservation.Playtest, synthetic: synthetic);
        }
        catch (FormatException e)
        {
            reason = "invalid metadata: " + e.Message;
            return null;
        }
    }

    private static SeatInfo ReadSeat(JsonNode n)
    {
        string kind = n["kind"].AsString() ?? throw new FormatException("Seat kind is required.");
        JsonNode levelNode = n["account_level"];
        int? level = levelNode.Kind == JsonKind.Null ? null : levelNode.AsInt();
        if (level is < 1 or > 20) throw new FormatException("account_level must be 1-20 or null.");
        if (kind == SeatInfo.HumanKind)
        {
            string playerRef = n["player_ref"].AsString() ?? "";
            if (!PlayerRefPattern.IsMatch(playerRef)) throw new FormatException("player_ref must be a short pseudonym (A-Z, 0-9, '-', '_').");
            if (!n["consent_recorded"].AsBool()) throw new FormatException("A human seat needs consent_recorded = true.");
            return SeatInfo.Human(playerRef, level);
        }
        if (kind == SeatInfo.BotKind)
        {
            string policy = n["policy"].AsString() ?? "";
            if (!Enum.TryParse(policy, out BotDifficulty d) || !Enum.IsDefined(d) || d.ToString() != policy)
                throw new FormatException("Unknown bot policy '" + policy + "'.");
            return SeatInfo.Bot(policy, level);
        }
        throw new FormatException("Seat kind must be 'human' or 'bot'.");
    }

    /// <summary>Reads every *.json file in a folder (sorted by name for a stable report).</summary>
    public static IngestResult IngestDirectory(string directory, bool allowSynthetic)
    {
        var result = new IngestResult();
        foreach (string file in Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            MatchObservation? o = Read(File.ReadAllText(file), allowSynthetic, out string? reason);
            if (o == null) result.Excluded.Add(new IngestExclusion(Path.GetRelativePath(directory, file), reason ?? "unknown"));
            else result.Observations.Add(o);
        }
        return result;
    }

    /// <summary>A labelled synthetic example (bot-played) showing the format; ingest skips it by default.</summary>
    public static string SyntheticExample(ulong seed = 20261006)
    {
        BotMatchRunner.SeedFor(seed, 0, out byte[] matchSeed, out string matchId);
        var config = Rules.Match.MatchConfig.V1Full(Rules.Match.MatchMode.Online);
        var engine = BotMatchRunner.Run(config, matchSeed, matchId,
            BotPlayer.Create(PlayerSide.A, BotDifficulty.Normal, matchSeed), BotPlayer.Create(PlayerSide.B, BotDifficulty.Hard, matchSeed));
        return Write(MatchRecord.FromEngine(engine), SeatInfo.Human("EXAMPLE01", 4), SeatInfo.Bot("Hard"),
            "synthetic-example", "2026-10-06T00:00:00Z", "example", synthetic: true,
            "SYNTHETIC EXAMPLE: both seats were played by bots; seat A is labelled human only to show the format.");
    }
}
