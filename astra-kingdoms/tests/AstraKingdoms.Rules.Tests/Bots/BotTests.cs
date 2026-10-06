using System.Collections.Concurrent;
using System.Reflection;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Rules.Tests.Match;

namespace AstraKingdoms.Rules.Tests.Bots;

public class BotTests
{
    [Test]
    public void ObservationInterface_AcceptsOnlyAPrivateView()
    {
        ConstructorInfo[] ctors = typeof(BotObservation).GetConstructors();
        Assert.That(ctors, Has.Length.EqualTo(1));
        Assert.That(ctors[0].GetParameters().Select(p => p.ParameterType), Is.EqualTo(new[] { typeof(PlayerView) }));
        foreach (MethodInfo m in typeof(IBotPolicy).GetMethods().Where(m => !m.IsSpecialName))
        {
            Type[] inputs = m.GetParameters().Where(p => !p.IsOut).Select(p => p.ParameterType).ToArray();
            Assert.That(inputs, Is.EqualTo(new[] { typeof(BotObservation) }), m.Name + " must only receive an observation");
        }
        // PlayerView exposes no engine, duel state or raw opponent input.
        Type[] forbidden = { typeof(MatchEngine), typeof(Duel), typeof(DuelState), typeof(PlayerDuelState), typeof(LockInputCommand) };
        foreach (PropertyInfo p in typeof(PlayerView).GetProperties())
            Assert.That(forbidden, Has.No.Member(p.PropertyType), "PlayerView." + p.Name);
    }

    [Test]
    public void BotDecision_DoesNotDependOnTheOpponentsUnrevealedLock()
    {
        foreach (BotDifficulty difficulty in Enum.GetValues<BotDifficulty>())
        {
            var h1 = Harness.FullPlain(12);
            var h2 = Harness.FullPlain(12);
            h1.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
            h2.Loadouts(MatchKit.DuelKit, MatchKit.DuelKit);
            h1.ToSelection();
            h2.ToSelection();
            Assert.That(h1.Lock(PlayerSide.A, MatchKit.Miss).Accepted, Is.True);
            Assert.That(h2.Lock(PlayerSide.A, MatchKit.ThunderHit(PlayerSide.A)).Accepted, Is.True);

            byte[] seed = MatchKit.Seed(12);
            MatchCommand c1 = BotPlayer.Create(PlayerSide.B, difficulty, seed).Decide(h1.View(PlayerSide.B))!;
            MatchCommand c2 = BotPlayer.Create(PlayerSide.B, difficulty, seed).Decide(h2.View(PlayerSide.B))!;
            Assert.That(c1.CanonicalBytes(), Is.EqualTo(c2.CanonicalBytes()), difficulty + " bot reacted to a secret it cannot see");
        }
    }

    [Test]
    public void BotRng_IsDeterministicAndMakesCanonicalUuids()
    {
        var a = BotRng.FromMatchSeed(MatchKit.Seed(3), PlayerSide.A, 1);
        var b = BotRng.FromMatchSeed(MatchKit.Seed(3), PlayerSide.A, 1);
        var c = BotRng.FromMatchSeed(MatchKit.Seed(3), PlayerSide.B, 1);
        Assert.That(a.NextULong(), Is.EqualTo(b.NextULong()));
        Assert.That(a.NextULong(), Is.Not.EqualTo(c.NextULong()));
        for (int i = 0; i < 100; i++) Assert.That(SeededStream.IsCanonicalUuid(a.NextUuid()), Is.True);
        for (int i = 0; i < 1000; i++) Assert.That(a.Range(-3, 3), Is.InRange(-3, 3));
    }

    [Test]
    public void AimSolver_FindsCoreHitsForEveryWeaponFromBothSeats()
    {
        foreach (WeaponDefinition w in WeaponCatalog.All)
            foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
            {
                AimSolution aim = AimSolver.Solve(w.Id, side, Fixed.Zero, Fixed.Zero);
                Assert.That(aim.FullContacts, Is.GreaterThan(0), w.Name + " from " + side);
                Assert.That(LaunchProfiles.CentralPitchRange(w).Contains(aim.PitchQdeg), Is.True);
                Assert.That(LaunchProfiles.CentralYawRange(w).Contains(aim.YawQdeg), Is.True);
            }
    }

    /// <summary>
    /// 500 seeds across pilot, Starter, Full and Full+Brahmastra rooms and every difficulty pairing.
    /// The runner throws on any rejected bot command, so finishing proves every command was legal.
    /// Each match is also checked for area conservation, alternation and terminal consistency.
    /// </summary>
    [Test]
    public void Bots_AlwaysProduceLegalCommands_Across500Seeds()
    {
        MatchConfig[] configs =
        {
            MatchConfig.Pilot(MatchMode.Online), MatchConfig.V1Starter(), MatchConfig.V1Full(), MatchConfig.V1Full(brahmastraEnabled: true),
        };
        BotDifficulty[] levels = Enum.GetValues<BotDifficulty>();
        var failures = new ConcurrentBag<string>();
        var reasons = new ConcurrentDictionary<TerminalReason, int>();
        Parallel.For(0, 500, n =>
        {
            MatchConfig config = configs[n % configs.Length];
            BotDifficulty a = levels[n % 3], b = levels[(n / 3) % 3];
            BotMatchRunner.SeedFor(0xC0FFEE, n, out byte[] seed, out string matchId);
            try
            {
                MatchEngine e = BotMatchRunner.Run(config, seed, matchId, BotPlayer.Create(PlayerSide.A, a, seed), BotPlayer.Create(PlayerSide.B, b, seed));
                reasons.AddOrUpdate(e.Result!.Reason, 1, (_, v) => v + 1);
                string? problem = CheckMatch(e);
                if (problem != null) failures.Add(n + ": " + problem);
            }
            catch (Exception ex)
            {
                failures.Add(n + " (" + config + ", " + a + " v " + b + "): " + ex.Message);
            }
        });
        Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(5)));
        Assert.That(reasons.Keys, Is.SubsetOf(new[] { TerminalReason.Territory90, TerminalReason.RoundsComplete }));
        Assert.That(reasons.Values.Sum(), Is.EqualTo(500));
    }

    private static string? CheckMatch(MatchEngine e)
    {
        if (!e.IsOver) return "not over";
        MatchResult r = e.Result!;
        if (r.CellsA + r.CellsB != 51040) return "area not conserved at the end";
        for (int i = 0; i < e.Rounds.Count; i++)
        {
            RoundRecord round = e.Rounds[i];
            if (round.CellsA + round.CellsB != 51040) return "area not conserved in round " + round.Round;
            if (round.Attacker != e.AttackerOf(round.Round)) return "attacker did not alternate in round " + round.Round;
            if (i > 0 && round.Attacker == e.Rounds[i - 1].Attacker) return "same attacker twice";
            if (round.DuelResult == DuelResult.Draw && (round.CellsTransferred != 0 || round.OfferedCards.Count != 0)) return "draw transferred land";
            if (round.HpDifference > 0 && round.CellsTransferred > LandQuota.Compute(51040, round.HpDifference, Cards.All.Max(c => Cards.CapPercent(c))))
                return "transfer above any quota";
            if (round.StateHashHex == null) return "missing state hash";
        }
        if (r.Reason == TerminalReason.Territory90 && Math.Max(r.CellsA, r.CellsB) < RulesConstants.VictoryCells) return "early finish below 90%";
        if (r.Reason == TerminalReason.RoundsComplete)
        {
            if (r.RoundsPlayed != 8) return "rounds complete before round 8";
            PlayerSide? expected = r.CellsA > r.CellsB ? PlayerSide.A : r.CellsB > r.CellsA ? PlayerSide.B : null;
            if (r.Winner != expected) return "final comparison wrong";
        }
        return null;
    }

    [Test]
    public void BotMatches_ReplayAndVerify()
    {
        var failures = new ConcurrentBag<string>();
        Parallel.For(0, 12, n =>
        {
            BotMatchRunner.SeedFor(77, n, out byte[] seed, out string id);
            MatchConfig config = n % 2 == 0 ? MatchConfig.V1Full() : MatchConfig.V1Starter();
            MatchEngine e = BotMatchRunner.Run(config, seed, id, BotPlayer.Create(PlayerSide.A, BotDifficulty.Hard, seed),
                BotPlayer.Create(PlayerSide.B, (BotDifficulty)(n % 3), seed));
            ReplayReport report = Replayer.Verify(MatchRecord.FromEngine(e).ToJson());
            if (!report.Success) failures.Add(n + ": " + report);
        });
        Assert.That(failures, Is.Empty, string.Join("\n", failures));
    }

    [Test]
    public void DifficultyDifferences_AreObservable()
    {
        // Mirrored seats on the same seeds: Hard should beat Easy in the large majority of matches.
        int hardWins = 0, games = 0;
        object gate = new();
        Parallel.For(0, 10, n =>
        {
            BotMatchRunner.SeedFor(99, n, out byte[] seed, out string id);
            foreach (bool hardIsA in new[] { true, false })
            {
                MatchEngine e = BotMatchRunner.Run(MatchConfig.V1Full(), seed, id,
                    BotPlayer.Create(PlayerSide.A, hardIsA ? BotDifficulty.Hard : BotDifficulty.Easy, seed),
                    BotPlayer.Create(PlayerSide.B, hardIsA ? BotDifficulty.Easy : BotDifficulty.Hard, seed));
                lock (gate)
                {
                    games++;
                    if (e.Result!.Winner == (hardIsA ? PlayerSide.A : PlayerSide.B)) hardWins++;
                }
            }
        });
        Assert.That(games, Is.EqualTo(20));
        Assert.That(hardWins, Is.GreaterThanOrEqualTo(16), "Hard won only " + hardWins + "/20 against Easy");
    }

    [Test]
    public void HardBot_CompensatesQuakeAndAimsAtTheBaseline()
    {
        var h = Harness.FullPlain(14);
        h.Loadouts(new[] { 18, 19 }, MatchKit.DuelKit);
        AimSolution quakeAim = AimSolver.Solve(18, PlayerSide.A, Fixed.Zero, Fixed.Zero);
        h.Volley(new VolleyInput(18, quakeAim.PitchQdeg, quakeAim.YawQdeg, 100, Dodge.None), MatchKit.Miss);
        h.ToSelection();
        PlayerView b = h.View(PlayerSide.B);
        Assert.That(b.Self.QuakeDue, Is.True, "Quake Arrow hit queues +5 deg on B's next pitch");
        var bot = new BotPolicy(BotDifficulty.Hard, new BotRng(1));
        VolleyInput choice = bot.ChooseVolley(new BotObservation(b));
        AimSolution aim = AimSolver.Solve(choice.WeaponId, PlayerSide.B, Fixed.Zero, Fixed.Zero);
        var range = LaunchProfiles.CentralPitchRange(WeaponCatalog.Get(choice.WeaponId));
        Assert.That(choice.PitchQdeg, Is.InRange(range.Clamp(aim.PitchQdeg - 21), range.Clamp(aim.PitchQdeg - 19)));
    }
}
