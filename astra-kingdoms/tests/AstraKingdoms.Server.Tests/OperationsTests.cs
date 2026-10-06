using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AstraKingdoms.Server.Tests;

/// <summary>Ticket 56: health, graceful shutdown (preserve or void), restart recovery and the grievance intake.</summary>
[TestFixture]
public class OperationsTests
{
    [Test]
    public async Task HealthAndReadinessReflectDraining()
    {
        await using ServerHarness h = ServerHarness.Start();
        using HttpClient http = h.Http();
        Assert.That((await http.GetAsync("/healthz")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        HttpResponseMessage ready = await http.GetAsync("/readyz");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        JsonElement body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()).RootElement;
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ready"));
        Assert.That(body.GetProperty("rules_hash").GetString(), Is.EqualTo(RulesBundle.HashHex));

        await using TestPlayer host = await TestPlayer.ConnectAsync(h, "host");
        host.Client.CreateRoom(CatalogPreset.Starter);
        await ServerHarness.Until(() => host.Client.Room != null, "room");

        h.Drain();
        Assert.That((await http.GetAsync("/readyz")).StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That((await http.GetAsync("/healthz")).StatusCode, Is.EqualTo(HttpStatusCode.OK), "liveness stays up while draining");
        await ServerHarness.Until(() => host.RoomClosures.Any(), "room closed for shutdown");
        Assert.That(host.RoomClosures.Single().Reason, Is.EqualTo(RoomCloseReasons.ServerShutdown));
        Assert.That(host.OfType(MessageTypes.ServerDraining).Any(), Is.True);
        Assert.ThrowsAsync<InvalidOperationException>(() => h.RawSocketAsync("dev:newcomer"), "no new connections while draining");
    }

    /// <summary>A friend match in the first selection, A locked, 5 s of the 12 s window used.</summary>
    private static async Task<string> MidSelection(ServerHarness h, int lockSeed)
    {
        TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        Play.SubmitLoadout(a);
        Play.SubmitLoadout(b);
        await Play.ViewAt(host, a, MatchPhase.TerrainAnnounce);
        h.AdvanceToDeadline(host);
        await Play.ViewAt(host, a, MatchPhase.Selection);
        a.Match.SendCommand(Play.LockFor(a.Match.View, seed: lockSeed));
        await ServerHarness.Until(() => a.LockReceipts.Any(r => r.Accepted), "A locked");
        h.AdvanceMs(5000);
        a.DisableAutopilot();
        await a.DisposeAsync();
        await b.DisposeAsync();
        return host.MatchId;
    }

    [Test]
    public async Task GracefulShutdownPreservesTheMatchAndRestartResumesIt()
    {
        var clock = new FakeTimeProvider(ServerHarness.Epoch);
        string db = Path.Combine(Path.GetTempPath(), "ak-server-tests", Guid.NewGuid().ToString("N") + ".db");
        string matchId;
        int lockedWeapon;
        await using (ServerHarness h1 = ServerHarness.Start(dbPath: db, time: clock))
        {
            matchId = await MidSelection(h1, lockSeed: 8);
            MatchHost host = h1.Matches.Get(matchId);
            PlayerSide aSide = host.ParticipantFor("dev-alice").Side;
            lockedWeapon = host.Engine.GetView(aSide).OwnLock.WeaponId;
        }

        await using (ServerHarness h2 = ServerHarness.Start(dbPath: db, time: clock))
        {
            // (Recovery ran at startup; the row is active again.)
            Assert.That(h2.Stored(matchId).Status, Is.EqualTo(MatchStatus.Active));
            Assert.That(h2.Audit(matchId).Any(e => e.Action == "match_suspended" && e.Detail == "remaining_ms=7000"), Is.True);
            MatchHost host = h2.Matches.Get(matchId);
            Assert.That(host, Is.Not.Null);
            Assert.That(host.IsSettled, Is.False);
            Assert.That((host.Deadline.Value - clock.GetUtcNow()).TotalMilliseconds, Is.EqualTo(7000), "the phase keeps the time it had left");

            await using TestPlayer a = await TestPlayer.ConnectAsync(h2, "alice");
            await using TestPlayer b = await TestPlayer.ConnectAsync(h2, "bob");
            await ServerHarness.Until(() => a.Match?.View != null && b.Match?.View != null, "both resumed");
            Assert.That(a.Match.View.OwnLock, Is.Not.Null, "an accepted lock survives the restart");
            Assert.That(a.Match.View.OwnLock.WeaponId, Is.EqualTo(lockedWeapon));
            b.Match.SendCommand(Play.LockFor(b.Match.View));
            await ServerHarness.Until(() => host.Engine.Phase == MatchPhase.Resolution, "resolution after restart");
            Assert.That(host.Engine.GetView(a.Match.LocalSide).History[0][a.Match.LocalSide].WeaponId, Is.EqualTo(lockedWeapon));
        }
    }

    [Test]
    public async Task VoidPolicySettlesActiveMatchesAsTechnicalVoids()
    {
        await using ServerHarness h = ServerHarness.Start(new Dictionary<string, string> { ["AstraServer:Lifecycle:ShutdownPolicy"] = "Void" });
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        h.Drain();
        Assert.That(host.IsSettled, Is.True);
        Assert.That(host.Outcome, Is.EqualTo(MatchOutcomes.TechnicalVoid));
        await ServerHarness.Until(() => a.Ends.Any() && b.Ends.Any(), "players told");
        Assert.That(a.Ends.Single().Outcome, Is.EqualTo(MatchOutcomes.TechnicalVoid));
        Assert.That(a.Ends.Single().Detail, Is.EqualTo("shutdown"));
        Assert.That(h.Grants(host.ResultId), Is.Empty, "no competitive reward or loss");
    }

    [Test]
    public async Task CrashLeftoversAndStaleSuspensionsBecomeTechnicalVoids()
    {
        var clock = new FakeTimeProvider(ServerHarness.Epoch);
        string db = Path.Combine(Path.GetTempPath(), "ak-server-tests", Guid.NewGuid().ToString("N") + ".db");
        string crashed, stale;
        await using (ServerHarness h1 = ServerHarness.Start(dbPath: db, time: clock))
        {
            crashed = await MidSelection(h1, 1);
        }
        // Pretend the process died without its shutdown sequence: the row is still "active".
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE matches SET status = 'active' WHERE match_id = $id;";
            cmd.Parameters.AddWithValue("$id", crashed);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        await using (ServerHarness h2 = ServerHarness.Start(dbPath: db, time: clock))
        {
            Assert.That(h2.Stored(crashed).Outcome, Is.EqualTo(MatchOutcomes.TechnicalVoid));
            Assert.That(h2.Stored(crashed).OutcomeDetail, Is.EqualTo("service_interrupted"));
            stale = await MidSelection(h2, 2);
        }
        clock.Advance(TimeSpan.FromMinutes(11));
        await using (ServerHarness h3 = ServerHarness.Start(dbPath: db, time: clock))
        {
            StoredMatch s = h3.Stored(stale);
            Assert.That(s.Outcome, Is.EqualTo(MatchOutcomes.TechnicalVoid));
            Assert.That(s.OutcomeDetail, Is.EqualTo("suspended_too_long"));
            Assert.That(h3.Grants(s.ResultId), Is.Empty);
            Assert.That(h3.Matches.Active, Is.Empty);
        }
    }

    [Test]
    public async Task GrievanceIntakeRecordsReportsWithAnId()
    {
        await using ServerHarness h = ServerHarness.Start();
        using HttpClient anon = h.Http();
        JsonElement contact = JsonDocument.Parse(await anon.GetStringAsync("/v1/grievances/contact")).RootElement;
        Assert.That(contact.GetProperty("email").GetString(), Is.Not.Empty);
        Assert.That(contact.GetProperty("acknowledge_within_hours").GetInt32(), Is.EqualTo(24));

        HttpResponseMessage created = await anon.PostAsJsonAsync("/v1/grievances", new
        {
            category = "match_result",
            description = "My cut was rejected although it looked legal.",
            match_id = Guid.NewGuid().ToString("D"),
            contact = "player@example.invalid",
        });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        string id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("grievance_id").GetString();
        Assert.That(id, Does.Match("^GRV-20261006-[2-9A-Z]{8}$"));
        Assert.That(h.Store.GetGrievance(id).Category, Is.EqualTo("match_result"));
        Assert.That((await anon.GetAsync("/v1/grievances/" + id)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(h.Store.Read(null).Any(e => e.Action == "grievance_received" && e.Detail.Contains(id)), Is.True);

        HttpResponseMessage bad = await anon.PostAsJsonAsync("/v1/grievances", new { category = "spam", description = "x" });
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        HttpStatusCode last = HttpStatusCode.OK;
        for (int i = 0; i < 12; i++)
            last = (await anon.PostAsJsonAsync("/v1/grievances", new { category = "other", description = "Repeated report number " + i })).StatusCode;
        Assert.That(last, Is.EqualTo(HttpStatusCode.TooManyRequests), "anonymous intake is rate limited per address");
    }

    [Test]
    public async Task IncidentSwitchesStopNewMatchesWithoutTouchingActiveOnes()
    {
        await using ServerHarness h = ServerHarness.Start(new Dictionary<string, string> { ["AstraServer:Lifecycle:DisabledCatalogs:0"] = "Full" });
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        a.Client.SendRaw(ClientMessages.RoomCreate("full", CatalogPreset.Full));
        await ServerHarness.Until(() => a.HasError(ErrorCodes.NewMatchesPaused, "full"), "Full catalog paused");
        b.Client.SendRaw(ClientMessages.QueueJoin("q", CatalogPreset.Full));
        await ServerHarness.Until(() => b.HasError(ErrorCodes.NewMatchesPaused, "q"), "queue for Full paused");
        MatchHost starter = await Play.FriendMatch(h, a, b, CatalogPreset.Starter);
        Assert.That(starter.IsSettled, Is.False, "other catalogs keep working");
    }

    [Test]
    public async Task CheckpointsNeverOverwriteADurableWrite()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        CheckpointWriter writer = h.Get<CheckpointWriter>();
        StoredMatch stale = h.Stored(host.MatchId).Copy();
        stale.OutcomeDetail = "stale-checkpoint";
        writer.Enqueue(stale);
        host.TechnicalVoid("test"); // durable settlement discards the queued checkpoint
        writer.Flush();
        await ServerHarness.Until(() => writer.Backlog == 0, "writer idle");
        Assert.That(h.Stored(host.MatchId).Status, Is.EqualTo(MatchStatus.Finished));
        Assert.That(h.Stored(host.MatchId).OutcomeDetail, Is.EqualTo("test"));
    }

    [Test]
    public async Task RetentionPurgesOnlyExpiredSettledRecords()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b);
        h.AdvanceMs(60000); // setup expiry: settled
        Assert.That(host.IsSettled, Is.True);
        Assert.That(h.Store.PurgeExpired(h.Time.GetUtcNow().AddDays(29)), Is.Zero);
        Assert.That(h.Store.PurgeExpired(h.Time.GetUtcNow().AddDays(31)), Is.EqualTo(1), "plan default: 30 days");
        Assert.That(h.Stored(host.MatchId), Is.Null);
    }
}
