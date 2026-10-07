using System.Reflection;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.World.Challenges;

namespace AstraKingdoms.World.Tests;

/// <summary>Plan: "Defenders publish a legal defensive loadout and bot policy ... legal observations only".</summary>
public class OfflineDefenceTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static BotPlayer Attacker(byte[] seed) => BotPlayer.Create(PlayerSide.A, BotDifficulty.Normal, seed);

    [Test]
    public void Publication_MustBeALegalFullCatalogLoadout()
    {
        Assert.Throws<RulesViolationException>(() => DefencePublication.Create(new[] { 1, 1 }, 0, BotDifficulty.Easy, DefenceMode.Automatic, null));
        Assert.Throws<RulesViolationException>(() => DefencePublication.Create(new[] { 1, 2 }, 3, BotDifficulty.Easy, DefenceMode.Automatic, null));
        Assert.Throws<RulesViolationException>(() => DefencePublication.Create(new[] { 21 }, 0, BotDifficulty.Easy, DefenceMode.Automatic, null));
        Assert.That(DefencePublication.Default.Weapons, Has.Count.EqualTo(6));
        Assert.That(DefencePublication.Default.Mode, Is.EqualTo(DefenceMode.Automatic));
    }

    [Test]
    public void Encounter_IsAnUnmodifiedAkTr1MatchWithThePublishedLoadoutAndCommitsOnce()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        DefencePublication pub = DefencePublication.Create(new[] { 11, 12, 13, 14, 15, 16 }, 0, BotDifficulty.Hard, DefenceMode.Automatic, null);
        w.Shard.PublishDefence("def", pub);
        Challenge c = w.Challenge("att", "def").Challenge;

        EncounterResult result = OfflineEncounter.RunAutomatic(c, Secret, Attacker);
        EncounterResult again = OfflineEncounter.RunAutomatic(c, Secret, Attacker);
        Assert.That(again.ResolutionId, Is.EqualTo(result.ResolutionId), "deterministic encounter");
        Assert.That(result.RecordJson, Does.Contain(RulesConstants.RulesVersion));
        Assert.That(result.RecordJson, Does.Contain("\"weapons\":[11,12,13,14,15,16]"), "the defender seat used the published loadout");

        Assert.That(w.Shard.Resolve(c.ChallengeId, result.ResolutionId, result.Outcome, w.Now + 1).Accepted, Is.True);
        Assert.That(w.Shard.Resolve(c.ChallengeId, result.ResolutionId, result.Outcome, w.Now + 2).Replayed, Is.True);
        string owner = w.Shard.Tile(c.TileId)!.OwnerId;
        Assert.That(owner, Is.EqualTo(result.Outcome == EncounterOutcome.AttackerWins ? "att" : "def"));
    }

    [Test]
    public void LiveDefence_IsNeverTakenOverByABot()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        w.Shard.PublishDefence("def", DefencePublication.Create(new[] { 1, 2, 3 }, 0, BotDifficulty.Normal, DefenceMode.Live, null));
        Challenge c = w.Challenge("att", "def").Challenge;
        // Switching to automatic after the snapshot changes nothing for this encounter.
        w.Shard.PublishDefence("def", DefencePublication.Default);
        Assert.Throws<InvalidOperationException>(() => OfflineEncounter.DefenderSeat(c, OfflineEncounter.EncounterSeed(Secret, c.ChallengeId)));
    }

    [Test]
    public void DefenderPolicy_ReceivesOnlyAnObservation()
    {
        foreach (MethodInfo m in typeof(PublishedDefencePolicy).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => !m.IsSpecialName))
        {
            Type[] inputs = m.GetParameters().Where(p => !p.IsOut).Select(p => p.ParameterType).ToArray();
            Assert.That(inputs, Is.EqualTo(new[] { typeof(BotObservation) }), m.Name);
        }
    }

    [Test]
    public void DefenderDecision_DoesNotDependOnTheAttackersHiddenLock()
    {
        var w = new WorldKit();
        w.Add("def");
        w.Add("att");
        Challenge c = w.Challenge("att", "def").Challenge;
        byte[] seed = OfflineEncounter.EncounterSeed(Secret, c.ChallengeId);
        string matchId = OfflineEncounter.EncounterMatchId(seed);

        MatchEngine ToSelection()
        {
            MatchEngine e = MatchEngine.Create(MatchConfig.V1Full(MatchMode.Online), seed, matchId);
            BotPlayer defender = OfflineEncounter.DefenderSeat(c, seed);
            Assert.That(e.Submit(PlayerSide.A, new SubmitLoadoutCommand(e.GetView(PlayerSide.A).NewHeader("11111111-0000-4000-8000-000000000001"),
                new[] { 1, 10, 19 }, 0)).Accepted, Is.True);
            Assert.That(e.Submit(PlayerSide.B, defender.Decide(e.GetView(PlayerSide.B))!).Accepted, Is.True);
            int n = 0;
            while (e.Phase != MatchPhase.Selection)
                Assert.That(e.Advance(e.CreateAdvance("22222222-0000-4000-8000-" + (++n).ToString("x12"))).Accepted, Is.True);
            return e;
        }

        MatchEngine e1 = ToSelection(), e2 = ToSelection();
        var miss = new VolleyInput(1, 260, 32, 100, Dodge.None);
        AimSolution aim = AimSolver.Solve(19, PlayerSide.A, Fixed.Zero, Fixed.Zero);
        var hit = new VolleyInput(19, aim.PitchQdeg, aim.YawQdeg, 100, Dodge.Jump);
        Assert.That(e1.Submit(PlayerSide.A, new LockInputCommand(e1.GetView(PlayerSide.A).NewHeader("33333333-0000-4000-8000-000000000001"), 1, miss)).Accepted, Is.True);
        Assert.That(e2.Submit(PlayerSide.A, new LockInputCommand(e2.GetView(PlayerSide.A).NewHeader("33333333-0000-4000-8000-000000000001"), 1, hit)).Accepted, Is.True);
        MatchCommand d1 = OfflineEncounter.DefenderSeat(c, seed).Decide(e1.GetView(PlayerSide.B))!;
        MatchCommand d2 = OfflineEncounter.DefenderSeat(c, seed).Decide(e2.GetView(PlayerSide.B))!;
        Assert.That(d1.CanonicalBytes(), Is.EqualTo(d2.CanonicalBytes()), "the defence bot cannot see the attacker's secret");
    }
}
