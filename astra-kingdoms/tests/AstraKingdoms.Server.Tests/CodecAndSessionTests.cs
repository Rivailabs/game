using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Online;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Server.Tests;

/// <summary>Wire formats shared by client and server, and the session interface shared by local and online play.</summary>
[TestFixture]
public class CodecAndSessionTests
{
    [TestCase(CatalogPreset.Starter, 1UL)]
    [TestCase(CatalogPreset.Full, 2UL)]
    public void PlayerViewSurvivesTheWireAtEveryStepOfAMatch(CatalogPreset catalog, ulong seedBase)
    {
        BotMatchRunner.SeedFor(seedBase, 0, out byte[] seed, out string matchId);
        MatchConfig config = catalog == CatalogPreset.Full ? MatchConfig.V1Full(MatchMode.Online) : MatchConfig.V1Starter(MatchMode.Online);
        MatchEngine engine = MatchEngine.Create(config, seed, matchId);
        BotPlayer botA = BotPlayer.Create(PlayerSide.A, BotDifficulty.Normal, seed);
        BotPlayer botB = BotPlayer.Create(PlayerSide.B, BotDifficulty.Hard, seed);
        var known = new Territory[2];
        var knownRevision = new ulong[2];
        int checks = 0;

        for (int step = 0; step < 1000 && !engine.IsOver; step++)
        {
            foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
            {
                PlayerView v = engine.GetView(side);
                // Full encoding (as in a snapshot) and the incremental form (map omitted when unchanged).
                PlayerView full = PlayerViewCodec.FromJson(JsonNode.Parse(PlayerViewCodec.ToJson(v, true).ToCanonicalString()), null);
                Assert.That(full.ToCanonicalText(), Is.EqualTo(v.ToCanonicalText()));
                Assert.That(full.CloneTerritory().ComputeOwnershipHash(), Is.EqualTo(v.CloneTerritory().ComputeOwnershipHash()));
                if (known[(int)side] != null && knownRevision[(int)side] == v.MapRevision)
                {
                    PlayerView inc = PlayerViewCodec.FromJson(JsonNode.Parse(PlayerViewCodec.ToJson(v, false).ToCanonicalString()), known[(int)side]);
                    Assert.That(inc.ToCanonicalText(), Is.EqualTo(v.ToCanonicalText()));
                }
                known[(int)side] = full.CloneTerritory();
                knownRevision[(int)side] = v.MapRevision;
                checks++;
            }
            MatchCommand a = botA.Decide(engine.GetView(PlayerSide.A));
            MatchCommand b = botB.Decide(engine.GetView(PlayerSide.B));
            if (a != null) Assert.That(engine.Submit(PlayerSide.A, RoundTrip(a)).Accepted, Is.True);
            else if (b != null) Assert.That(engine.Submit(PlayerSide.B, RoundTrip(b)).Accepted, Is.True);
            else engine.Advance(engine.CreateAdvance(Guid.NewGuid().ToString("D")));
        }
        Assert.That(engine.IsOver, Is.True);
        Assert.That(checks, Is.GreaterThan(50));
        Assert.That(PlayerViewCodec.FromJson(PlayerViewCodec.ToJson(engine.GetView(PlayerSide.A), true), null).SeedHex, Is.Not.Null);
    }

    /// <summary>Every command crosses the wire unchanged (identical canonical bytes, so duplicates are recognised).</summary>
    private static MatchCommand RoundTrip(MatchCommand cmd)
    {
        MatchCommand back = CommandCodec.FromJson(JsonNode.Parse(CommandCodec.ToJson(cmd).ToCanonicalString()));
        Assert.That(back.CanonicalBytes(), Is.EqualTo(cmd.CanonicalBytes()));
        return back;
    }

    [Test]
    public void ManualCutCommandRoundTrips()
    {
        var header = new CommandHeader(RulesBundle.Hash, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 3, 42);
        var cut = new SubmitCutCommand(header, 7, CardId.Suchi, 128 * 256 + 128, 120, 130, 5, 64, CutMode.Manual,
            new[] { new CellPoint(1, 2), new CellPoint(3, 4), new CellPoint(5, 6) });
        Assert.That(RoundTrip(cut).CanonicalBytes(), Is.EqualTo(cut.CanonicalBytes()));
        Assert.That(() => CommandCodec.FromJson(JsonNode.Parse("{\"kind\":\"AdvancePhase\",\"schema_version\":1,\"round_index\":1,\"expected_state_revision\":1}")),
            Throws.InstanceOf<FormatException>(), "clients cannot send the server's AdvancePhase");
    }

    [Test]
    public void MessagesRoundTrip()
    {
        var rules = new MatchRules { Config = MatchConfig.V1Full(MatchMode.Online), RulesHashHex = RulesBundle.HashHex };
        var room = new RoomStateMessage
        {
            Code = "ABC234", Revision = 3, Status = RoomStatus.Full, IsHost = true, Members = 2, ExpiresInMs = 1000, Rules = rules,
            ConfirmedSelf = true,
        };
        RoomStateMessage back = RoomStateMessage.Parse(JsonNode.Parse(room.ToJson().ToCanonicalString()));
        Assert.That(back.ToJson().ToCanonicalString(), Is.EqualTo(room.ToJson().ToCanonicalString()));

        var end = new MatchEndMessage
        {
            MatchId = "m", Outcome = MatchOutcomes.Completed, Result = new MatchResult(TerminalReason.Territory90, PlayerSide.B, null, 5000, 46040, 6),
            ResultId = "r", RewardXp = 125, RewardCoins = 15,
        };
        Assert.That(MatchEndMessage.Parse(JsonNode.Parse(end.ToJson().ToCanonicalString())).ToJson().ToCanonicalString(),
            Is.EqualTo(end.ToJson().ToCanonicalString()));
    }

    [Test]
    public void LocalHostRunsThroughTheSameSessionInterface()
    {
        BotMatchRunner.SeedFor(77, 0, out byte[] seed, out string matchId);
        var host = new LocalMatchHost(MatchConfig.Pilot(MatchMode.Practice), seed, matchId, SeatKind.Human, SeatKind.Bot,
            timings: new HostTimings { BotCutDelaySeconds = 0 });
        IMatchSession session = new LocalMatchSession(host);
        var me = BotPlayer.Create(PlayerSide.A, BotDifficulty.Normal, seed);
        int revealed = 0;
        MatchResult ended = null;
        session.VolleyRevealed += _ => revealed++;
        session.MatchEnded += r => ended = r;
        host.Start();

        for (int i = 0; i < 5000 && session.Stage != HostStage.MatchOver; i++)
        {
            if (session.IsLocalHuman(PlayerSide.A) && session.CanView(PlayerSide.A) && session.StageSide == PlayerSide.A)
            {
                PlayerView v = session.ViewFor(PlayerSide.A);
                switch (me.Decide(v))
                {
                    case SubmitLoadoutCommand lo when session.Stage == HostStage.LoadoutEntry:
                        session.SubmitLoadout(PlayerSide.A, lo.Weapons, 0);
                        continue;
                    case LockInputCommand li when session.Stage == HostStage.Entry:
                        session.SubmitLock(PlayerSide.A, li.ToChoice());
                        continue;
                    case SubmitCutCommand cut when session.Stage == HostStage.CardAndCut:
                        CutResult preview = session.PreviewCut(PlayerSide.A, cut.CardId, cut.Pose, cut.AnchorCellId, cut.Mode, null);
                        Assert.That(preview, Is.Not.Null);
                        session.SubmitCut(PlayerSide.A, cut.CardId, cut.Pose, cut.AnchorCellId, cut.Mode, null);
                        continue;
                }
            }
            session.Tick(0.5);
        }
        Assert.That(ended, Is.Not.Null);
        Assert.That(session.Result, Is.SameAs(ended));
        Assert.That(revealed, Is.EqualTo(host.Engine.GetView(PlayerSide.A).History.Count));
        Assert.That(session.Snapshot().CellsA + session.Snapshot().CellsB, Is.EqualTo(RulesConstants.ActiveCells));
    }

    [Test]
    public void OnlineSessionShowsOnlyTheLocalSeatAndNeverPauses()
    {
        var start = new MatchStartMessage
        {
            MatchId = Guid.NewGuid().ToString("D"), Side = PlayerSide.B, OpponentKind = OpponentKinds.Bot, OpponentLabel = "Bot - Normal (unranked)",
            Origin = MatchOrigins.QueueBot, Rules = new MatchRules { Config = MatchConfig.V1Starter(), RulesHashHex = RulesBundle.HashHex },
        };
        var sent = new List<JsonNode>();
        var session = new OnlineMatchSession(start, m =>
        {
            sent.Add(m);
            return true;
        });
        BotMatchRunner.SeedFor(5, 0, out byte[] seed, out _);
        MatchEngine engine = MatchEngine.Create(MatchConfig.V1Starter(), seed, start.MatchId);
        session.OnUpdate(new MatchUpdateMessage
        {
            MatchId = start.MatchId, Snapshot = true, View = PlayerViewCodec.ToJson(engine.GetView(PlayerSide.B), true), LastSeq = 1,
            DeadlineRemainingMs = 60000,
        });
        Assert.That(session.PauseAllowed, Is.False);
        Assert.That(session.CanView(PlayerSide.B), Is.True);
        Assert.That(session.CanView(PlayerSide.A), Is.False);
        Assert.That(() => session.ViewFor(PlayerSide.A), Throws.InvalidOperationException);
        Assert.That(session.Stage, Is.EqualTo(HostStage.LoadoutEntry));
        Assert.That(session.OpponentIsBot, Is.True);
        session.Tick(10);
        Assert.That(session.StageSecondsRemaining, Is.EqualTo(50).Within(1e-9));
        Assert.That(sent.Last().Type(), Is.EqualTo(MessageTypes.MatchAck));

        // An incremental view whose map is unknown triggers a resync instead of a wrong board.
        bool resync = false;
        session.ResyncRequested += () => resync = true;
        JsonNode noMap = PlayerViewCodec.ToJson(engine.GetView(PlayerSide.B), false);
        JsonNode tampered = JsonNode.Object();
        foreach (KeyValuePair<string, JsonNode> m in noMap.Members) tampered.Add(m.Key, m.Key == "map_revision" ? JsonNode.Of(99UL) : m.Value);
        session.OnUpdate(new MatchUpdateMessage { MatchId = start.MatchId, View = tampered, LastSeq = 1 });
        Assert.That(resync, Is.True);
        Assert.That(sent.Last().Type(), Is.EqualTo(MessageTypes.MatchResume));
    }
}
