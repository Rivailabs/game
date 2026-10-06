using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Security;
using AstraKingdoms.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AstraKingdoms.Server.Tests;

/// <summary>Ticket 55: tampered actions fail; rate limits hold; diagnostics are useful and never expose secrets.</summary>
[TestFixture]
public class ValidationAuditTests
{
    private static JsonNode With(JsonNode obj, string key, JsonNode value)
    {
        JsonNode copy = JsonNode.Object();
        bool replaced = false;
        foreach (KeyValuePair<string, JsonNode> m in obj.Members)
        {
            if (m.Key == key)
            {
                copy.Add(key, value);
                replaced = true;
            }
            else copy.Add(m.Key, m.Value);
        }
        if (!replaced) copy.Add(key, value);
        return copy;
    }

    private static JsonNode CommandMessage(string rid, string matchId, JsonNode command) =>
        Json.Message(MessageTypes.MatchCommand).Add("rid", rid).Add("match_id", matchId).Add("command", command);

    private static async Task<(MatchHost host, TestPlayer a, TestPlayer b)> InSelection(ServerHarness h)
    {
        TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(a);
        Play.SubmitLoadout(b);
        await Play.ViewAt(host, a, MatchPhase.TerrainAnnounce);
        h.AdvanceToDeadline(host);
        await Play.ViewAt(host, a, MatchPhase.Selection);
        await Play.ViewAt(host, b, MatchPhase.Selection);
        return (host, a, b);
    }

    [Test]
    public async Task MalformedAndUnknownMessagesAreRejectedWithoutSideEffects()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using RawClient raw = await RawClient.ConnectAsync(h, "dev:mallory");
        await raw.SendAsync("{not json");
        await raw.SendAsync("[1,2,3]");
        await raw.SendAsync("{\"t\":\"room.create\",\"rid\":\"r1\",\"catalog\":\"Mega\"}");
        await raw.SendAsync("{\"t\":\"room.confirm\",\"rid\":\"r2\"}");
        await raw.SendAsync("{\"t\":\"launch.missiles\",\"rid\":\"r3\"}");
        await raw.SendAsync("{\"t\":\"match.command\",\"rid\":\"r4\",\"match_id\":\"x\",\"command\":{\"kind\":\"AdvancePhase\"}}");
        await ServerHarness.Until(() => raw.Errors.Count() >= 6, "six errors");
        Assert.That(raw.Errors.Count(e => e.Code == ErrorCodes.BadMessage), Is.GreaterThanOrEqualTo(4));
        Assert.That(raw.HasError(ErrorCodes.UnknownType), Is.True);
        Assert.That(h.Lobby.RoomCount, Is.Zero);
        Assert.That(raw.Closed, Is.False, "malformed messages are answered, not fatal");

        await raw.SendAsync(new string('x', OnlineProtocol.MaxMessageBytes + 1));
        await ServerHarness.Until(() => raw.Closed, "oversized frame closes the socket");
    }

    [Test]
    public async Task HelloIsRequiredAndOldClientsAreTurnedAway()
    {
        await using ServerHarness h = ServerHarness.Start(new Dictionary<string, string> { ["AstraServer:Clients:MinimumVersion"] = "1.2.0" });
        await using RawClient noHello = await RawClient.ConnectAsync(h, "dev:a", hello: false);
        await noHello.SendAsync(ClientMessages.RoomCreate("r", Rules.Core.CatalogPreset.Starter));
        await ServerHarness.Until(() => noHello.HasError(ErrorCodes.HelloRequired), "hello first");

        await using RawClient old = await RawClient.ConnectAsync(h, "dev:b", hello: false);
        await old.SendAsync(ClientMessages.Hello("1.1.9"));
        await ServerHarness.Until(() => old.HasError(ErrorCodes.ClientTooOld) && old.Closed, "minimum version");

        await using RawClient future = await RawClient.ConnectAsync(h, "dev:c", hello: false);
        await future.SendAsync(Json.Message(MessageTypes.Hello).Add("protocol", 99).Add("client_version", "9.0.0"));
        await ServerHarness.Until(() => future.HasError(ErrorCodes.ProtocolUnsupported) && future.Closed, "protocol version");
    }

    [Test]
    public async Task TamperedCommandsFailAndChangeNothing()
    {
        await using ServerHarness h = ServerHarness.Start();
        (MatchHost host, TestPlayer a, TestPlayer b) = await InSelection(h);
        await using (a)
        await using (b)
        {
            ulong revision = host.Engine.StateRevision;
            JsonNode good = CommandCodec.ToJson(Play.LockFor(a.Match.View));
            string zeros = new('0', 64);
            a.Client.SendRaw(CommandMessage("hash", host.MatchId, With(good, "rules_hash", JsonNode.Of(zeros))));
            a.Client.SendRaw(CommandMessage("schema", host.MatchId, With(With(good, "schema_version", JsonNode.Of(2)), "request_id", JsonNode.Of(Guid.NewGuid().ToString("D")))));
            a.Client.SendRaw(CommandMessage("power", host.MatchId, With(With(good, "power_percent", JsonNode.Of(150)), "request_id", JsonNode.Of(Guid.NewGuid().ToString("D")))));
            a.Client.SendRaw(CommandMessage("weapon", host.MatchId, With(With(good, "weapon_id", JsonNode.Of(17)), "request_id", JsonNode.Of(Guid.NewGuid().ToString("D")))));
            a.Client.SendRaw(CommandMessage("rev", host.MatchId, With(With(good, "expected_state_revision", JsonNode.Of(revision - 1)), "request_id", JsonNode.Of(Guid.NewGuid().ToString("D")))));
            a.Client.SendRaw(CommandMessage("other", Guid.NewGuid().ToString("D"), good)); // header names another match
            a.Client.SendRaw(CommandMessage("mismatch", host.MatchId, With(good, "match_id", JsonNode.Of(Guid.NewGuid().ToString("D")))));

            await ServerHarness.Until(() => a.LockReceipts.Count() == 5 && a.Errors.Count() >= 2, "answers");
            string[] codes = a.LockReceipts.Select(r => r.Code).ToArray();
            Assert.That(codes, Is.EqualTo(new[] { MatchErrors.RulesHash, MatchErrors.SchemaVersion, InputValidator.PowerRange, InputValidator.WeaponNotEquipped, MatchErrors.StaleStateRevision }));
            Assert.That(a.LockReceipts.All(r => !r.Accepted), Is.True);
            Assert.That(a.HasError(ErrorCodes.MatchNotFound, "other"), Is.True);
            Assert.That(a.HasError(ErrorCodes.BadMessage, "mismatch"), Is.True);
            Assert.That(host.Engine.StateRevision, Is.EqualTo(revision));
            Assert.That(host.Engine.GetView(a.Match.LocalSide).OwnLock, Is.Null);
            Assert.That(h.Audit(host.MatchId).Count(e => e.Action == "command_rejected"), Is.EqualTo(5));
        }
    }

    [Test]
    public async Task TokenBucketLimitsEachIdentityAndRefills()
    {
        await using ServerHarness h = ServerHarness.Start(new Dictionary<string, string>
        {
            ["AstraServer:RateLimits:Capacity"] = "10",
            ["AstraServer:RateLimits:RefillPerSecond"] = "5",
            ["AstraServer:RateLimits:CloseAfterViolations"] = "25",
        });
        await using RawClient flood = await RawClient.ConnectAsync(h, "dev:flood");
        await using RawClient calm = await RawClient.ConnectAsync(h, "dev:calm");
        // Every frame costs a token (hello included): nine pings pass, the tenth is limited.
        for (int i = 0; i < 12; i++) await flood.SendAsync(ClientMessages.Ping("p" + i));
        await ServerHarness.Until(() => flood.Messages.Count(m => m.Type() == MessageTypes.Pong) == 9 && flood.HasError(ErrorCodes.RateLimited), "limit");
        await calm.SendAsync(ClientMessages.Ping("calm"));
        await ServerHarness.Until(() => calm.Messages.Any(m => m.Type() == MessageTypes.Pong), "other identities are unaffected");

        h.Advance(TimeSpan.FromSeconds(1)); // five tokens back
        for (int i = 0; i < 5; i++) await flood.SendAsync(ClientMessages.Ping("q" + i));
        await ServerHarness.Until(() => flood.Messages.Count(m => m.Type() == MessageTypes.Pong) == 14, "refill");

        for (int i = 0; i < 40; i++) await flood.SendAsync(ClientMessages.Ping("z" + i));
        await ServerHarness.Until(() => flood.Closed, "persistent flooding closes the connection");
        Assert.That(flood.CloseDescription, Is.EqualTo(CloseReasons.PolicyViolation));
    }

    [Test]
    public void TokenBucketArithmetic()
    {
        var time = new FakeTimeProvider(ServerHarness.Epoch);
        var bucket = new TokenBucketLimiter(time, 3, 2);
        Assert.That(Enumerable.Range(0, 4).Select(_ => bucket.TryTake("k")), Is.EqualTo(new[] { true, true, true, false }));
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.That(bucket.TryTake("k"), Is.True);
        Assert.That(bucket.TryTake("k"), Is.False);
        time.Advance(TimeSpan.FromSeconds(10));
        bucket.Sweep();
        Assert.That(Enumerable.Range(0, 4).Count(_ => bucket.TryTake("k")), Is.EqualTo(3), "capacity caps the refill");
    }

    [Test]
    public async Task AuditAndLogsHoldNoSecretBeforeReveal()
    {
        await using ServerHarness h = ServerHarness.Start();
        (MatchHost host, TestPlayer a, TestPlayer b) = await InSelection(h);
        await using (a)
        await using (b)
        {
            LockInputCommand secret = Play.LockFor(a.Match.View, seed: 31);
            a.Match.SendCommand(secret);
            await ServerHarness.Until(() => a.LockReceipts.Any(r => r.Accepted), "lock accepted");
            await ServerHarness.Until(() => b.Match.View.OpponentLocked, "B sees ready");

            // Nothing that reveals the locked choice exists anywhere but the engine and the sealed record.
            string audit = string.Join("\n", h.Audit(host.MatchId).Select(e => e.Action + " " + e.Detail));
            string logs = h.Logs.All;
            foreach (string text in new[] { audit, logs })
            {
                Assert.That(text, Does.Not.Contain("pitch").IgnoreCase.And.Not.Contain("yaw").IgnoreCase.And.Not.Contain("dodge").IgnoreCase);
                Assert.That(text, Does.Not.Contain("weapon_id").And.Not.Contain("Weapon=").And.Not.Contain("power").IgnoreCase);
                Assert.That(text, Does.Not.Contain("dev:alice").And.Not.Contain("dev:bob"), "tokens are never logged");
                Assert.That(text, Does.Not.Contain(a.Uid).And.Not.Contain(b.Uid), "identities appear only as pseudonymous references");
            }
            Assert.That(audit, Does.Contain("command_accepted kind=LockInput"));
            Assert.That(audit, Does.Not.Contain("choices_revealed"), "the reveal is audited only when the match is settled");
        }
    }

    [Test]
    public async Task AuditTrailIsAppendOnly()
    {
        await using ServerHarness h = ServerHarness.Start();
        h.Store.Append(null, "server", "test_entry", "detail");
        using var c = new SqliteConnection("Data Source=" + h.DbPath);
        c.Open();
        using SqliteCommand update = c.CreateCommand();
        update.CommandText = "UPDATE audit_log SET detail = 'tampered';";
        Assert.That(() => update.ExecuteNonQuery(), Throws.InstanceOf<SqliteException>().With.Message.Contains("append-only"));
        using SqliteCommand delete = c.CreateCommand();
        delete.CommandText = "DELETE FROM audit_log;";
        Assert.That(() => delete.ExecuteNonQuery(), Throws.InstanceOf<SqliteException>());
        Assert.That(h.Store.Read(null).Any(e => e.Action == "test_entry" && e.Detail == "detail"), Is.True);
    }

    [Test]
    public void RedactionRemovesTokens()
    {
        string jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl";
        string text = LogRedaction.Redact("Authorization: Bearer " + jwt + " also " + jwt + " and dev:alice");
        Assert.That(text, Does.Not.Contain("eyJ").And.Not.Contain("alice"));
        Assert.That(text, Does.Contain("[redacted"));
    }
}
