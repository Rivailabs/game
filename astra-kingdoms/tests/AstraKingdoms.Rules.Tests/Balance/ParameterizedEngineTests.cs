using AstraKingdoms.Rules.Balance;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Rules.Tests.Balance;

/// <summary>
/// Ticket 24: the AK-TR-1 engine reads every schema tunable from the <see cref="RulesParameters"/>
/// pinned in its match config, so a published balance bundle changes outcomes only for matches
/// pinned to it; replays of old and new matches verify; rollback affects new matches only.
/// </summary>
public class ParameterizedEngineTests
{
    private const int MistVeil = 10;

    /// <summary>The example release from the ticket: Mist Veil 20 → 24 HP, River heal 10 → 8 HP.</summary>
    private static readonly BalanceBundle B2 = new("AK-TR-1.b2", RulesConstants.RulesVersion, new[]
    {
        new KeyValuePair<string, long>(TunableSchema.WeaponDamageName(MistVeil), 2400),
        new KeyValuePair<string, long>("Damage.RiverHealUnits", 800),
    }, RulesConstants.RulesVersion, "Mist Veil 20->24, River heal 10->8");

    private static BalanceBundle Bundle(string id, params (string Name, long Value)[] overrides) =>
        new(id, RulesConstants.RulesVersion, overrides.Select(o => new KeyValuePair<string, long>(o.Name, o.Value)), RulesConstants.RulesVersion, "test");

    private static MatchEngine BotMatch(MatchConfig config, int n, BotDifficulty a = BotDifficulty.Hard, BotDifficulty b = BotDifficulty.Normal)
    {
        BotMatchRunner.SeedFor(2024, n, out byte[] seed, out string id);
        return BotMatchRunner.Run(config, seed, id, BotPlayer.Create(PlayerSide.A, a, seed), BotPlayer.Create(PlayerSide.B, b, seed));
    }

    private static string RecordJson(MatchEngine e) => MatchRecord.FromEngine(e).ToJson();

    private static string StateHashes(MatchEngine e) => string.Join(",", e.Rounds.Select(r => r.StateHashHex));

    // ------------------------------------------------------------------ defaults are AK-TR-1

    [Test]
    public void Default_EqualsTheCompiledRules()
    {
        RulesParameters p = RulesParameters.Default;
        Assert.That(p.IsDefault, Is.True);
        Assert.That(p.RulesVersion, Is.EqualTo(RulesConstants.RulesVersion));
        Assert.That(p.RulesHashHex, Is.EqualTo(RulesBundle.HashHex));
        Assert.That(p.MaxRounds, Is.EqualTo(RulesConstants.MaxRounds));
        Assert.That(p.VictoryCells, Is.EqualTo(RulesConstants.VictoryCells));
        Assert.That(p.MaxVolleys, Is.EqualTo(RulesConstants.MaxVolleys));
        Assert.That(p.StartHpUnits, Is.EqualTo(RulesConstants.StartHpUnits));
        Assert.That(p.QuotaFloorPpm, Is.EqualTo(RulesConstants.QuotaFloorPpm));
        Assert.That(p.VajraMinExclusiveDiffUnits, Is.EqualTo(RulesConstants.VajraMinExclusiveDiffUnits));
        Assert.That(p.ChoiceDeadlineMs, Is.EqualTo(RulesConstants.ChoiceDeadlineMs));
        Assert.That(p.OnlineCutWindowMs, Is.EqualTo(RulesConstants.OnlineCutWindowMs));
        Assert.That(p.SharedPhoneCutWindowMs, Is.EqualTo(RulesConstants.SharedPhoneCutWindowMs));
        Assert.That(p.TerrainAnnouncementMs, Is.EqualTo(RulesConstants.TerrainAnnouncementMs));
        Assert.That(p.SharedPhoneHandoverMs, Is.EqualTo(RulesConstants.SharedPhoneHandoverMs));
        Assert.That(p.ResolutionReplayMaxMs, Is.EqualTo(RulesConstants.ResolutionReplayMaxMs));
        Assert.That(p.ChainBonusUnits, Is.EqualTo(DamageCalculator.ChainBonusUnits));
        Assert.That(p.BurnUnits, Is.EqualTo(DamageCalculator.BurnUnits));
        Assert.That(p.OceanHealUnits, Is.EqualTo(DamageCalculator.OceanHealUnits));
        Assert.That(p.RiverHealUnits, Is.EqualTo(DamageCalculator.RiverHealUnits));
        foreach (CardId c in Cards.All) Assert.That(p.CardCapPercent(c), Is.EqualTo(Cards.CapPercent(c)), c.ToString());
        foreach (WeaponDefinition w in WeaponCatalog.All) Assert.That(p.Weapon(w.Id), Is.SameAs(w), "default weapons are the catalog's objects");
        Assert.That(BalanceBundle.Baseline().ToParameters(), Is.SameAs(p));
        Assert.That(MatchConfig.V1Full().Parameters, Is.SameAs(p));
    }

    [Test]
    public void DefaultConfig_AndBaselinePinnedConfig_ProduceIdenticalRecords()
    {
        MatchConfig plain = MatchConfig.V1Full();
        MatchConfig pinned = plain.WithParameters(BalanceBundle.Baseline().ToParameters());
        Assert.That(pinned.RulesVersion, Is.EqualTo(RulesConstants.RulesVersion));
        string a = RecordJson(BotMatch(plain, 3));
        Assert.That(RecordJson(BotMatch(pinned, 3)), Is.EqualTo(a));
        Assert.That(a, Does.Not.Contain("balance_content_hash"), "AK-TR-1 records keep their exact canonical form");
    }

    // ------------------------------------------------------------------ tuned values reach the rules

    [Test]
    public void TunedBundle_HasItsOwnRulesHash_AndValues()
    {
        RulesParameters p = B2.ToParameters();
        Assert.That(p.IsDefault, Is.False);
        Assert.That(p.RulesVersion, Is.EqualTo("AK-TR-1.b2"));
        Assert.That(p.RulesHashHex, Is.EqualTo(B2.EffectiveRulesHashHex));
        Assert.That(p.RulesHashHex, Is.Not.EqualTo(RulesBundle.HashHex));
        Assert.That(p.BalanceContentHashHex, Is.EqualTo(B2.ContentHashHex));
        Assert.That(p.Weapon(MistVeil).DamagePerProjectileUnits, Is.EqualTo(2400));
        Assert.That(p.Weapon(MistVeil).Ability, Is.EqualTo(WeaponAbility.Veil), "structural fields are kept");
        Assert.That(p.Weapon(1), Is.SameAs(WeaponCatalog.Get(1)), "untouched weapons are shared");
        Assert.That(p.RiverHealUnits, Is.EqualTo(800));
        Assert.That(WeaponCatalog.Get(MistVeil).DamagePerProjectileUnits, Is.EqualTo(2000), "the compiled catalog never changes");
        Assert.That(B2.ToParameters(), Is.SameAs(p), "parameters are cached per bundle");
    }

    [Test]
    public void InvalidBundle_CannotBecomeParameters()
    {
        Assert.Throws<ArgumentException>(() => Bundle("AK-TR-1.bad", ("Duel.StartHpUnits", 1)).ToParameters());
        Assert.Throws<ArgumentException>(() => RulesParameters.FromBundle(Bundle("AK-TR-1.bad", ("BoardSize", 300))));
    }

    [Test]
    public void TunedDamageAndRiverHeal_ChangeTheVolley()
    {
        // A hits B (the River defender) with one Mist Veil core contact; B passes.
        DuelState Start(RulesParameters p) =>
            DuelState.Start(1, TerrainType.River, PlayerSide.B, Loadout.Create(CatalogPreset.Full, new[] { MistVeil }),
                Loadout.Create(CatalogPreset.Full, new[] { 1 }), p);
        var veil = new VolleyInput(MistVeil, LaunchProfiles.CentralPitchRange(WeaponCatalog.Get(MistVeil)).Min, 0, 100, Dodge.None);
        var contact = new[] { new GeometricContact(new ProjectileId(1, 1, PlayerSide.A, 0), ContactKind.Core, 60, 0) };

        VolleyResult baseline = VolleyResolver.ResolveWithScriptedContacts(Start(RulesParameters.Default), veil, VolleyInput.Pass(), contact);
        VolleyResult tuned = VolleyResolver.ResolveWithScriptedContacts(Start(B2.ToParameters()), veil, VolleyInput.Pass(), contact);

        Assert.That(baseline.NewState.B.HpUnits, Is.EqualTo(10000 - 2000 + 1000));
        Assert.That(tuned.NewState.B.HpUnits, Is.EqualTo(10000 - 2400 + 800));
        Assert.That(tuned.Explanation.B.RiverHealUnits, Is.EqualTo(800));
        Assert.That(tuned.NewState.Parameters, Is.SameAs(B2.ToParameters()), "the snapshot travels with the duel state");
        // The parameterless start is the default snapshot and resolves exactly like before.
        DuelState legacy = DuelState.Start(1, TerrainType.River, PlayerSide.B, Loadout.Create(CatalogPreset.Full, new[] { MistVeil }),
            Loadout.Create(CatalogPreset.Full, new[] { 1 }));
        Assert.That(VolleyResolver.ResolveWithScriptedContacts(legacy, veil, VolleyInput.Pass(), contact).Log.ToCanonicalBytes(),
            Is.EqualTo(baseline.Log.ToCanonicalBytes()));
    }

    [Test]
    public void TunedStartHp_VolleyCount_AndSideEffects_AreRead()
    {
        RulesParameters p = Bundle("AK-TR-1.t1", ("Duel.StartHpUnits", 12000), ("Duel.MaxVolleys", 4), ("Damage.BurnUnits", 700),
            ("Land.VajraMinExclusiveDiffUnits", 3000), ("Land.QuotaFloorPpm", 50000), ("Card.Chakra.CapPercent", 16),
            ("Match.MaxRounds", 10)).ToParameters();
        DuelState s = DuelState.Start(10, TerrainType.Plain, PlayerSide.B, Loadout.Create(CatalogPreset.Full, new[] { 1 }),
            Loadout.Create(CatalogPreset.Full, new[] { 1 }), p);
        Assert.That(s.A.HpUnits, Is.EqualTo(12000));
        Assert.Throws<RulesViolationException>(() => DuelState.Start(10, TerrainType.Plain, PlayerSide.B,
            Loadout.Create(CatalogPreset.Full, new[] { 1 }), Loadout.Create(CatalogPreset.Full, new[] { 1 })), "AK-TR-1 still has 8 rounds");

        Assert.That(VolleyResolver.Decide(5000, 4000, 3, p.MaxVolleys), Is.EqualTo(DuelResult.InProgress));
        Assert.That(VolleyResolver.Decide(5000, 4000, 4, p.MaxVolleys), Is.EqualTo(DuelResult.AWins));
        Assert.That(VolleyResolver.Decide(5000, 4000, 3), Is.EqualTo(DuelResult.AWins), "the legacy overload keeps AK-TR-1's three volleys");
        s.VolleyIndex = 4;
        Assert.DoesNotThrow(() => InputValidator.ValidateLock(s, PlayerSide.A, new LockInput(4, Combat.Kit.Shot(1))));
        Assert.That(p.ClampHp(13000), Is.EqualTo(12000), "heals clamp at the tuned starting HP");
        Assert.That(DamageCalculator.ApplyHealthBatch(11500, 0, 0, 1500, 0, p.StartHpUnits), Is.EqualTo(12000));

        Assert.That(Cards.Eligible(4000, p.VajraMinExclusiveDiffUnits), Does.Contain(CardId.Vajra));
        Assert.That(Cards.Eligible(4000), Does.Not.Contain(CardId.Vajra));
        Assert.That(LandQuota.Compute(25520, 100, CardId.Chakra, p), Is.EqualTo(51040L * 50000 / 1000000));
        Assert.That(LandQuota.Compute(25520, 4000, CardId.Chakra, p), Is.EqualTo(51040L * 16 * 4000 / 1000000));
        Assert.That(LandQuota.Compute(25520, 2000, CardId.Chakra, RulesParameters.Default), Is.EqualTo(LandQuota.Compute(25520, 2000, CardId.Chakra)));
        Assert.That(CardOffers.V1(MatchKitSeed(), 10, 4000, p).Cards, Has.Count.EqualTo(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => CardOffers.V1(MatchKitSeed(), 10, 4000));
    }

    private static byte[] MatchKitSeed() => Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Test]
    public void TunedTimers_AreExposedByTheConfig()
    {
        MatchConfig c = MatchConfig.V1Full().WithParameters(Bundle("AK-TR-1.t2", ("Timing.ChoiceDeadlineMs", 15000),
            ("Timing.OnlineCutWindowMs", 14000)).ToParameters());
        Assert.That(c.RulesVersion, Is.EqualTo("AK-TR-1.t2"));
        Assert.That(c.ChoiceDeadlineMs, Is.EqualTo(15000));
        Assert.That(c.CutWindowMs, Is.EqualTo(14000));
        Assert.That(MatchConfig.V1Full().CutWindowMs, Is.EqualTo(RulesConstants.OnlineCutWindowMs));
        Assert.Throws<RulesViolationException>(() => new MatchConfig(MatchMode.Online, CatalogPreset.Full, CardOfferRule.V1,
            TerrainTemplates.FullId, false, "AK-TR-1.t2"), "a bundle ID without its parameters is not a runnable config");
    }

    [Test]
    public void TunedRoundsAndVictory_ChangeTheMatchLength()
    {
        MatchConfig longer = MatchConfig.V1Starter().WithParameters(Bundle("AK-TR-1.r10", ("Match.MaxRounds", 10), ("Match.VictoryCells", 51040)).ToParameters());
        MatchEngine e = BotMatch(longer, 1, BotDifficulty.Easy, BotDifficulty.Easy);
        Assert.That(e.Result!.Reason, Is.EqualTo(TerminalReason.RoundsComplete));
        Assert.That(e.Result.RoundsPlayed, Is.EqualTo(10));

        MatchConfig sudden = MatchConfig.V1Starter().WithParameters(Bundle("AK-TR-1.v1", ("Match.VictoryCells", 25521)).ToParameters());
        MatchEngine s = BotMatch(sudden, 1, BotDifficulty.Hard, BotDifficulty.Easy);
        Assert.That(s.Result!.Reason, Is.EqualTo(TerminalReason.Territory90), "the first transfer crosses the tuned threshold");
        Assert.That(s.Rounds.Count(r => r.CellsTransferred > 0), Is.EqualTo(1));
    }

    // ------------------------------------------------------------------ pinning, replay, rollback

    [Test]
    public void TunedBundle_ChangesOutcomesOnlyForMatchesPinnedToIt()
    {
        MatchConfig room = MatchConfig.V1Full();
        MatchConfig tuned = room.WithParameters(B2.ToParameters());
        int differing = 0;
        for (int n = 0; n < 6; n++)
        {
            MatchEngine a = BotMatch(room, n), again = BotMatch(room, n), b = BotMatch(tuned, n);
            Assert.That(RecordJson(again), Is.EqualTo(RecordJson(a)), "default matches are unaffected and deterministic");
            Assert.That(b.RulesHash, Is.EqualTo(B2.EffectiveRulesHash));
            Assert.That(a.RulesHash, Is.EqualTo(RulesBundle.Hash));
            if (StateHashes(a) != StateHashes(b)) differing++;
        }
        Assert.That(differing, Is.GreaterThan(0), "the tuning changes at least one of six Full-room matches");
    }

    [Test]
    public void ReplaysOfOldAndNewMatches_Verify_AndUnknownBundlesAreRefused()
    {
        var channel = new BalanceChannel("live", BalanceBundle.Baseline(), "owner", 0);
        MatchConfig room = MatchConfig.V1Full();
        MatchEngine old = BotMatch(channel.PinForNewMatch("old").Pin(room), 4);
        Assert.That(channel.Publish(B2, "owner", 10).Accepted, Is.True, "a parameterized engine serves tuned bundles");
        MatchEngine fresh = BotMatch(channel.PinForNewMatch("new").Pin(room), 4);

        MatchRecord oldRecord = MatchRecord.FromEngine(old);
        MatchRecord newRecord = MatchRecord.FromEngine(fresh);
        Assert.That(newRecord.RulesVersion, Is.EqualTo("AK-TR-1.b2"));
        Assert.That(newRecord.RulesHashHex, Is.EqualTo(B2.EffectiveRulesHashHex));
        Assert.That(newRecord.BalanceContentHashHex, Is.EqualTo(B2.ContentHashHex));
        Assert.That(oldRecord.BalanceContentHashHex, Is.Null);

        Assert.That(Replayer.Verify(oldRecord).Success, Is.True);
        Assert.That(Replayer.Verify(oldRecord, channel.Find).Success, Is.True);
        string newJson = newRecord.ToJson();
        Assert.That(MatchRecord.FromJson(newJson).ToJson(), Is.EqualTo(newJson), "tuned records round-trip");
        ReplayReport ok = Replayer.Verify(newJson, channel.Find);
        Assert.That(ok.Success, Is.True, ok.ToString());
        Assert.That(ok.Engine!.Parameters, Is.SameAs(B2.ToParameters()));

        Assert.That(Replayer.Verify(newJson).Failure, Is.EqualTo(ReplayFailure.UnknownBundle), "no resolver, no tuned replay");
        Assert.That(Replayer.Verify(newJson, _ => null).Failure, Is.EqualTo(ReplayFailure.UnknownBundle));
        BalanceBundle impostor = Bundle("AK-TR-1.b2", (TunableSchema.WeaponDamageName(MistVeil), 2500));
        Assert.That(Replayer.Verify(newJson, _ => impostor).Failure, Is.EqualTo(ReplayFailure.IncompatibleRules), "same ID, other content");

        MatchRecord forged = MatchRecord.FromJson(newJson);
        forged.RulesHashHex = RulesBundle.HashHex;
        Assert.That(Replayer.Verify(forged, channel.Find).Failure, Is.EqualTo(ReplayFailure.IncompatibleRules));
        MatchRecord relabelled = MatchRecord.FromJson(RecordJson(old));
        relabelled.BalanceContentHashHex = B2.ContentHashHex;
        Assert.That(Replayer.Verify(relabelled, channel.Find).Failure, Is.EqualTo(ReplayFailure.IncompatibleRules));

        // Replaying the new match's commands under AK-TR-1 must not silently "verify".
        MatchRecord downgraded = MatchRecord.FromJson(newJson);
        downgraded.RulesVersion = RulesConstants.RulesVersion;
        downgraded.BalanceContentHashHex = null;
        Assert.That(Replayer.Verify(downgraded).Success, Is.False);
    }

    [Test]
    public void Rollback_RestoresOldBehaviourForNewMatchesOnly()
    {
        var channel = new BalanceChannel("live", BalanceBundle.Baseline(), "owner", 0);
        MatchConfig room = MatchConfig.V1Full();
        string reference = StateHashes(BotMatch(room, 2));
        string tunedReference = StateHashes(BotMatch(room.WithParameters(B2.ToParameters()), 2));
        Assert.That(tunedReference, Is.Not.EqualTo(reference), "seed 2 is sensitive to the tuning");

        BalanceSnapshot before = channel.PinForNewMatch("m-before");
        Assert.That(channel.Publish(B2, "owner", 10).Accepted, Is.True);
        BalanceSnapshot during = channel.PinForNewMatch("m-during");
        Assert.That(channel.Rollback("owner", 20, "River too weak in playtest").Accepted, Is.True);
        BalanceSnapshot after = channel.PinForNewMatch("m-after");

        Assert.That(StateHashes(BotMatch(before.Pin(room), 2)), Is.EqualTo(reference));
        Assert.That(StateHashes(BotMatch(during.Pin(room), 2)), Is.EqualTo(tunedReference));
        Assert.That(StateHashes(BotMatch(after.Pin(room), 2)), Is.EqualTo(reference), "rollback restores AK-TR-1 behaviour for new matches");
        Assert.That(channel.PinnedFor("m-during")!.Parameters, Is.SameAs(B2.ToParameters()), "the pinned match keeps the rolled-back bundle");
    }

    [Test]
    public void RunningMatch_KeepsItsSnapshotAcrossPublishAndRollback()
    {
        var channel = new BalanceChannel("live", BalanceBundle.Baseline(), "owner", 0);
        Assert.That(channel.Publish(B2, "owner", 10).Accepted, Is.True);
        MatchConfig room = MatchConfig.V1Full();
        BotMatchRunner.SeedFor(2024, 2, out byte[] seed, out string id);
        MatchEngine running = MatchEngine.Create(channel.PinForNewMatch(id).Pin(room), seed, id);
        var botA = BotPlayer.Create(PlayerSide.A, BotDifficulty.Hard, seed);
        var botB = BotPlayer.Create(PlayerSide.B, BotDifficulty.Normal, seed);
        var server = BotRng.FromMatchSeed(seed, PlayerSide.A, 0x5E4E4UL); // the runner's host stream

        void Step()
        {
            MatchCommand? a = botA.Decide(running.GetView(PlayerSide.A));
            if (a != null) { Assert.That(running.Submit(PlayerSide.A, a).Accepted, Is.True); }
            MatchCommand? b = running.IsOver ? null : botB.Decide(running.GetView(PlayerSide.B));
            if (b != null) { Assert.That(running.Submit(PlayerSide.B, b).Accepted, Is.True); }
            if (a == null && b == null) Assert.That(running.Advance(running.CreateAdvance(server.NextUuid())).Accepted, Is.True);
        }

        while (!running.IsOver && running.RoundIndex < 4) Step();
        // Operations change the channel mid-match: a rollback, then a different release.
        Assert.That(channel.Rollback("owner", 20, "incident").Accepted, Is.True);
        Assert.That(channel.Publish(Bundle("AK-TR-1.b3", ("Damage.RiverHealUnits", 1200)), "owner", 30).Accepted, Is.True);
        for (int i = 0; i < 1000 && !running.IsOver; i++) Step();

        Assert.That(running.IsOver, Is.True);
        Assert.That(running.Parameters, Is.SameAs(B2.ToParameters()));
        Assert.That(StateHashes(running), Is.EqualTo(StateHashes(BotMatch(room.WithParameters(B2.ToParameters()), 2))),
            "the match finished exactly as an uninterrupted b2 match");
        Assert.That(Replayer.Verify(MatchRecord.FromEngine(running), channel.Find).Success, Is.True);
    }

    [Test]
    public void DefaultChannel_NowServesTunedBundles_AndStrictDeploymentsCanStillRefuse()
    {
        var ch = new BalanceChannel("live", BalanceBundle.Baseline(), "owner", 0);
        Assert.That(BalanceChannel.ParameterizedEngine(B2), Is.True);
        Assert.That(BalanceChannel.ParameterizedEngine(Bundle("AK-TR-1.bad", ("Duel.StartHpUnits", 1))), Is.False);
        Assert.That(ch.Publish(B2, "owner", 1).Accepted, Is.True);
        Assert.That(ch.PinForNewMatch("x").Parameters.RulesHashHex, Is.EqualTo(B2.EffectiveRulesHashHex));

        var strict = new BalanceChannel("legacy", BalanceBundle.Baseline(), "owner", 0, BalanceChannel.CompiledEngineOnly);
        Assert.That(strict.Publish(B2, "owner", 1).Issues.Select(i => i.Code), Does.Contain("NOT_EXECUTABLE"));
    }
}
