using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Storage;

namespace AstraKingdoms.Server.Tests;

/// <summary>Ticket 50: accepted intents resolve through the shared, versioned rules assembly; matches persist and settle once.</summary>
[TestFixture]
public class MatchServiceTests
{
    [Test]
    public async Task FriendMatchPlaysToTheEndThroughTheRulesEngineAndItsRecordReplays()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice", BotDifficulty.Normal, 1);
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob", BotDifficulty.Hard, 2);
        MatchHost host = await Play.FriendMatch(h, a, b);

        await Play.ToEnd(h, host, a, b);

        StoredMatch stored = h.Stored(host.MatchId);
        Assert.That(stored.Status, Is.EqualTo(MatchStatus.Finished));
        Assert.That(stored.Outcome, Is.EqualTo(MatchOutcomes.Completed));
        Assert.That(stored.Ranked, Is.False);
        ReplayReport replay = Replayer.Verify(stored.RecordJson);
        Assert.That(replay.Success, Is.True, replay.ToString());
        Assert.That(replay.Engine.Result.ToString(), Is.EqualTo(host.Engine.Result.ToString()));

        // Both clients end with exactly the server's final private view (seed and loadouts disclosed).
        await ServerHarness.Until(() => a.Match.IsOver && b.Match.IsOver && a.Ends.Any() && b.Ends.Any(), "match.end");
        Assert.That(a.Match.View.ToCanonicalText(), Is.EqualTo(ServerHarness.ServerViewText(host, a.Match.LocalSide)));
        Assert.That(b.Match.View.ToCanonicalText(), Is.EqualTo(ServerHarness.ServerViewText(host, b.Match.LocalSide)));
        Assert.That(a.Match.View.SeedHex, Is.Not.Null);
        Assert.That(a.Rejections + b.Rejections, Is.Zero);

        MatchEndMessage end = a.Ends.Last();
        Assert.That(end.ResultId, Is.EqualTo(MatchHost.ResultIdFor(host.MatchId)));
        Assert.That(h.Grants(end.ResultId), Has.Count.EqualTo(2), "one grant per person, keyed by the result ID");
    }

    [Test]
    public async Task MatchStartPinsRulesHashConfigAndOnlineTimers()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice");
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob");
        MatchHost host = await Play.FriendMatch(h, a, b, CatalogPreset.Full);

        MatchStartMessage start = a.Starts.Last();
        Assert.That(start.Rules.RulesHashHex, Is.EqualTo(RulesBundle.HashHex));
        Assert.That(start.Rules.Config.Catalog, Is.EqualTo(CatalogPreset.Full));
        Assert.That(start.Rules.Config.Mode, Is.EqualTo(MatchMode.Online));
        Assert.That(start.Rules.Ranked, Is.False);
        Assert.That(start.Rules.Timings.AnnounceMs, Is.EqualTo(2000));
        Assert.That(start.Rules.Timings.ChoiceMs, Is.EqualTo(12000));
        Assert.That(start.Rules.Timings.ReplayMs, Is.EqualTo(2500));
        Assert.That(start.Rules.Timings.CutMs, Is.EqualTo(12000));
        Assert.That(host.Engine.Config.CutWindowMs, Is.EqualTo(12000), "online config uses the 12 s combined cut window");
        Assert.That(a.Match.LocalSide, Is.Not.EqualTo(b.Match.LocalSide));
    }

    [Test]
    public async Task ServerClockIssuesEachOnlineDeadline()
    {
        await using ServerHarness h = ServerHarness.Start();
        await using TestPlayer a = await TestPlayer.ConnectAsync(h, "alice", BotDifficulty.Normal);
        await using TestPlayer b = await TestPlayer.ConnectAsync(h, "bob", BotDifficulty.Normal);
        MatchHost host = await Play.FriendMatch(h, a, b);
        await Play.Quiesce(host, a, b);

        // Loadouts in: terrain announcement for 2 s.
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.TerrainAnnounce));
        Assert.That(Remaining(h, host), Is.EqualTo(2000));
        h.AdvanceMs(1999);
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.TerrainAnnounce));
        a.DisableAutopilot();
        b.DisableAutopilot();
        h.AdvanceMs(1);
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Selection));
        Assert.That(Remaining(h, host), Is.EqualTo(12000), "12 s concurrent choice");
        await ServerHarness.Until(() => a.Match.View.Phase == MatchPhase.Selection, "selection view");
        Assert.That(a.Match.StageSecondsRemaining, Is.EqualTo(12.0).Within(0.001), "the client counts down the server's deadline");

        h.AdvanceMs(12000);
        Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Resolution), "deadline Pass for both resolves the volley");
        Assert.That(Remaining(h, host), Is.EqualTo(2500), "2.5 s replay");
    }

    private static long Remaining(ServerHarness h, MatchHost host) => (long)(host.Deadline.Value - h.Time.GetUtcNow()).TotalMilliseconds;
}
