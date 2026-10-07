using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Rules.Tests.Match;

public class ReplayTests
{
    private static MatchEngine BotMatch(int n, MatchConfig? config = null, BotDifficulty a = BotDifficulty.Hard, BotDifficulty b = BotDifficulty.Normal)
    {
        byte[] seed = MatchKit.Seed(n);
        return BotMatchRunner.Run(config ?? MatchConfig.V1Full(), seed, MatchKit.MatchId(n),
            BotPlayer.Create(PlayerSide.A, a, seed), BotPlayer.Create(PlayerSide.B, b, seed));
    }

    private static readonly Lazy<string> GoldenJson = new(() => MatchRecord.FromEngine(BotMatch(21)).ToJson());

    [Test]
    public void RecordRoundTrips_AndReplayVerifies()
    {
        string json = GoldenJson.Value;
        MatchRecord parsed = MatchRecord.FromJson(json);
        Assert.That(parsed.ToJson(), Is.EqualTo(json), "canonical JSON round-trips byte for byte");
        ReplayReport report = Replayer.Verify(json);
        Assert.That(report.Success, Is.True, report.ToString());
        Assert.That(report.Engine!.Result!.ToString(), Is.EqualTo(parsed.Result!.ToString()));
        Assert.That(parsed.Rounds.All(r => r.StateHashHex!.Length == 64), Is.True);
        Assert.That(parsed.RulesHashHex, Is.EqualTo(RulesBundle.HashHex));
    }

    [Test]
    public void SameSeedAndPolicies_ProduceIdenticalRecords()
    {
        Assert.That(MatchRecord.FromEngine(BotMatch(21)).ToJson(), Is.EqualTo(GoldenJson.Value));
    }

    [Test]
    public void ScriptedMatchWithTimeoutsAndForfeit_Replays()
    {
        var h = Harness.FullPlain(8);
        h.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
        h.Volley(MatchKit.ThunderHit(PlayerSide.A), null);
        h.Volley(MatchKit.ThunderHit(PlayerSide.A), MatchKit.Miss);
        h.FinishDuel();
        Assert.That(h.Advance().Accepted, Is.True); // cut timeout
        h.DrawRound();
        h.Volley(null, null);
        h.Volley(null, null); // void
        Assert.That(h.E.Result!.Reason, Is.EqualTo(TerminalReason.Void));
        ReplayReport report = Replayer.Verify(MatchRecord.FromEngine(h.E).ToJson());
        Assert.That(report.Success, Is.True, report.ToString());
    }

    [Test]
    public void TamperedCommand_IsDetected()
    {
        MatchRecord record = MatchRecord.FromJson(GoldenJson.Value);
        int i = record.Commands.FindIndex(c => c.Command is LockInputCommand);
        var original = (LockInputCommand)record.Commands[i].Command;
        var weapon = WeaponCatalog.Get(original.WeaponId);
        var range = AstraKingdoms.Rules.Combat.LaunchProfiles.CentralPitchRange(weapon);
        int pitch = original.PitchQdeg + 40 <= range.Max ? original.PitchQdeg + 40 : original.PitchQdeg - 40;
        record.Commands[i] = new RecordedCommand(record.Commands[i].Sender, new LockInputCommand(original.Header, original.VolleyIndex,
            original.WeaponId, pitch, original.YawQdeg, original.PowerPercent, original.Dodge));
        ReplayReport report = Replayer.Verify(record);
        Assert.That(report.Success, Is.False);
        Assert.That(report.Failure, Is.AnyOf(ReplayFailure.RoundMismatch, ReplayFailure.CommandRejected, ReplayFailure.ResultMismatch));
    }

    [Test]
    public void TamperedStateHashOrResult_IsDetected()
    {
        MatchRecord record = MatchRecord.FromJson(GoldenJson.Value);
        record.Rounds[0].StateHashHex = new string('0', 64);
        Assert.That(Replayer.Verify(record).Failure, Is.EqualTo(ReplayFailure.RoundMismatch));

        record = MatchRecord.FromJson(GoldenJson.Value);
        record.Rounds[^1].Volleys[0].LogHashHex = new string('f', 64);
        Assert.That(Replayer.Verify(record).Failure, Is.EqualTo(ReplayFailure.RoundMismatch));

        record = MatchRecord.FromJson(GoldenJson.Value);
        MatchResult r = record.Result!;
        record.Result = new MatchResult(r.Reason, r.Winner, r.ForfeitedBy, r.CellsA + 1, r.CellsB - 1, r.RoundsPlayed);
        Assert.That(Replayer.Verify(record).Failure, Is.EqualTo(ReplayFailure.ResultMismatch));

        record = MatchRecord.FromJson(GoldenJson.Value);
        record.SeedHex = new string('1', 64);
        Assert.That(Replayer.Verify(record).Failure, Is.EqualTo(ReplayFailure.SeedCommitmentMismatch));
    }

    [Test]
    public void IncompatibleRulesVersionOrHash_IsRejected()
    {
        string json = GoldenJson.Value;
        ReplayReport v2 = Replayer.Verify(json.Replace("\"rules_version\":\"AK-TR-1\"", "\"rules_version\":\"AK-TR-2\""));
        Assert.That(v2.Failure, Is.EqualTo(ReplayFailure.IncompatibleRules));
        ReplayReport hash = Replayer.Verify(json.Replace(RulesBundle.HashHex, new string('a', 64)));
        Assert.That(hash.Failure, Is.EqualTo(ReplayFailure.IncompatibleRules));
        Assert.That(Replayer.Verify("{\"format\":\"nope\"}").Failure, Is.EqualTo(ReplayFailure.Malformed));
        Assert.That(Replayer.Verify(json.Substring(0, json.Length / 2)).Failure, Is.EqualTo(ReplayFailure.Malformed));
    }

    [Test]
    public void CanonicalJson_ParsesOnlyIntegersAndEscapes()
    {
        JsonNode n = JsonNode.Parse(" {\"a\": [1, -2, 18446744073709551615], \"s\": \"q\\\"\\u0041\", \"b\": true, \"n\": null} ");
        Assert.That(n["a"].AsArray()[2].AsULong(), Is.EqualTo(ulong.MaxValue));
        Assert.That(n["a"].AsArray()[1].AsInt(), Is.EqualTo(-2));
        Assert.That(n["s"].AsString(), Is.EqualTo("q\"A"));
        Assert.That(n.ToCanonicalString(), Is.EqualTo("{\"a\":[1,-2,18446744073709551615],\"s\":\"q\\\"A\",\"b\":true,\"n\":null}"));
        Assert.Throws<FormatException>(() => JsonNode.Parse("{\"x\":1.5}"));
        Assert.Throws<FormatException>(() => JsonNode.Parse("[1,]"));
    }
}
