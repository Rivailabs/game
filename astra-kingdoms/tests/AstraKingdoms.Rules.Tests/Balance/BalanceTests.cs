using AstraKingdoms.Rules.Balance;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Tests.Balance;

/// <summary>Ticket 24: versioned balance publication, validation, pinning and rollback.</summary>
public class BalanceTests
{
    private static BalanceBundle Tuned(string id, params (string Name, long Value)[] overrides) =>
        new(id, RulesConstants.RulesVersion, overrides.Select(o => new KeyValuePair<string, long>(o.Name, o.Value)), RulesConstants.RulesVersion, "test");

    /// <summary>Accepts every bundle (stands in for a future parameterized engine).</summary>
    private static bool AnyEngine(BalanceBundle b) => true;

    private static BalanceChannel Channel(Func<BalanceBundle, bool>? exec = null) =>
        new("live", BalanceBundle.Baseline(), "owner", 1000, exec ?? AnyEngine);

    [Test]
    public void Baseline_IsValid_AndHashesToTheCompiledRules()
    {
        BalanceBundle b = BalanceBundle.Baseline();
        Assert.That(BalanceValidator.Validate(b), Is.Empty);
        Assert.That(b.IsBaseline, Is.True);
        Assert.That(b.EffectiveRulesHashHex, Is.EqualTo(RulesBundle.HashHex));
        Assert.That(BalanceChannel.CompiledEngineOnly(b), Is.True);
    }

    [Test]
    public void Schema_BaselinesMatchTheCompiledConstants()
    {
        RulesBundleContents c = RulesBundleContents.Current();
        foreach (TunableDefinition t in TunableSchema.All)
        {
            Assert.That(t.InRange(t.Baseline), Is.True, t.Name);
            if (t.Target == TunableTarget.Constant)
                Assert.That(c.Constants.Single(k => k.Key == t.ConstantName).Value, Is.EqualTo(t.Baseline), t.Name);
        }
        Assert.That(TunableSchema.All.Select(t => t.Name), Is.Unique);
        Assert.That(TunableSchema.TryGet("BoardSize", out _), Is.False, "structural values are not tunable");
        Assert.That(TunableSchema.TryGet("TicksPerSecond", out _), Is.False);
    }

    [Test]
    public void FrozenBaseline_CannotBeChangedUnderItsOwnId()
    {
        var b = new BalanceBundle(RulesConstants.RulesVersion, RulesConstants.RulesVersion,
            new[] { new KeyValuePair<string, long>(TunableSchema.WeaponDamageName(11), 1200) });
        Assert.That(BalanceValidator.Validate(b).Select(i => i.Code), Does.Contain("FROZEN_BASELINE"));
    }

    [Test]
    public void AnyChange_GetsANewEffectiveRulesHash()
    {
        BalanceBundle tuned = Tuned("AK-TR-1.b2", (TunableSchema.WeaponDamageName(11), 1200));
        Assert.That(BalanceValidator.Validate(tuned), Is.Empty);
        Assert.That(tuned.EffectiveRulesHashHex, Is.Not.EqualTo(RulesBundle.HashHex));
        Assert.That(tuned.ToRulesContents().RulesVersion, Is.EqualTo("AK-TR-1.b2"));
        Assert.That(tuned.ToRulesContents().Weapons.Single(w => w.Id == 11).DamageUnits, Is.EqualTo(1200));
        Assert.That(BalanceChannel.CompiledEngineOnly(tuned), Is.False, "the compiled engine cannot resolve a tuned bundle");
    }

    [TestCase("Weapon.11.DamageUnits", -1, "OUT_OF_RANGE")]
    [TestCase("Weapon.11.DamageUnits", 1500, "REDUNDANT_OVERRIDE")]
    [TestCase("Duel.StartHpUnits", 0, "OUT_OF_RANGE")]
    [TestCase("Card.Vajra.CapPercent", 60, "OUT_OF_RANGE")]
    [TestCase("BoardSize", 300, "UNKNOWN_TUNABLE")]
    [TestCase("Match.VictoryCells", 25520, "OUT_OF_RANGE")]
    [TestCase("Weapon.3.UnlockLevel", 4, "CROSS_FIELD")]
    [TestCase("Weapon.9.UnlockLevel", 1, "CROSS_FIELD")]
    [TestCase("Timing.OnlineCutWindowMs", 25000, "CROSS_FIELD")]
    [TestCase("Land.VajraMinExclusiveDiffUnits", 10000, "CROSS_FIELD")]
    public void InvalidOverrides_FailValidation(string name, long value, string code)
    {
        Assert.That(BalanceValidator.Validate(Tuned("AK-TR-1.bad", (name, value))).Select(i => i.Code), Does.Contain(code));
    }

    [TestCase("AK-TR-2.b1", "BUNDLE_ID")]
    [TestCase("b2", "BUNDLE_ID")]
    [TestCase("AK-TR-1 b2", "BUNDLE_ID")]
    public void BadIds_FailValidation(string id, string code)
    {
        Assert.That(BalanceValidator.Validate(Tuned(id, ("Damage.BurnUnits", 600))).Select(i => i.Code), Does.Contain(code));
    }

    [Test]
    public void EmptyRelease_AndMissingLineage_AreRejected()
    {
        Assert.That(BalanceValidator.Validate(Tuned("AK-TR-1.b2")).Select(i => i.Code), Does.Contain("EMPTY_RELEASE"));
        var orphan = new BalanceBundle("AK-TR-1.b2", "AK-TR-1", new[] { new KeyValuePair<string, long>("Damage.BurnUnits", 600) });
        Assert.That(BalanceValidator.Validate(orphan).Select(i => i.Code), Does.Contain("LINEAGE"));
        var unknownBase = new BalanceBundle("AK-TR-9.b2", "AK-TR-9", new[] { new KeyValuePair<string, long>("Damage.BurnUnits", 600) }, "AK-TR-9");
        Assert.That(BalanceValidator.Validate(unknownBase).Select(i => i.Code), Does.Contain("BASE_VERSION"));
    }

    [Test]
    public void Json_RoundTripsAndPreservesTheContentHash()
    {
        BalanceBundle b = Tuned("AK-TR-1.b2", (TunableSchema.WeaponDamageName(11), 1200), ("Card.Vajra.CapPercent", 18));
        BalanceBundle back = BalanceBundle.FromJson(b.ToJson());
        Assert.That(back.ContentHashHex, Is.EqualTo(b.ContentHashHex));
        Assert.That(back.EffectiveRulesHashHex, Is.EqualTo(b.EffectiveRulesHashHex));
        Assert.That(back.Overrides, Is.EquivalentTo(b.Overrides));
        Assert.Throws<FormatException>(() => BalanceBundle.FromJson("{\"format\":\"nope\"}"));
        Assert.Throws<FormatException>(() => BalanceBundle.FromJson(
            "{\"format\":\"AK-BALANCE-BUNDLE/1\",\"bundle_id\":\"x\",\"base_rules_version\":\"AK-TR-1\",\"previous_bundle_id\":null,\"notes\":\"\",\"overrides\":{\"a\":1,\"a\":2}}"));
    }

    [Test]
    public void OnlyNewMatches_AdoptAPublishedBundle()
    {
        BalanceChannel ch = Channel();
        BalanceSnapshot before = ch.PinForNewMatch("match-1");
        PublicationResult r = ch.Publish(Tuned("AK-TR-1.b2", ("Damage.BurnUnits", 600)), "owner", 2000);
        Assert.That(r.Accepted, Is.True, r.ToString());

        Assert.That(ch.PinnedFor("match-1")!.BundleId, Is.EqualTo("AK-TR-1"), "an ongoing match keeps its version");
        Assert.That(ch.PinForNewMatch("match-1"), Is.SameAs(before), "re-pinning is idempotent");
        Assert.That(ch.PinForNewMatch("match-2").BundleId, Is.EqualTo("AK-TR-1.b2"));
        Assert.That(ch.PinnedFor("match-2")!.EffectiveRulesHashHex, Is.Not.EqualTo(before.EffectiveRulesHashHex));
    }

    [Test]
    public void Rollback_RestoresThePreviousBundleForNewMatchesOnly()
    {
        BalanceChannel ch = Channel();
        Assert.That(ch.Rollback("owner", 1500, "nothing").Accepted, Is.False);
        Assert.That(ch.Publish(Tuned("AK-TR-1.b2", ("Damage.BurnUnits", 600)), "owner", 2000).Accepted, Is.True);
        BalanceSnapshot during = ch.PinForNewMatch("during");
        Assert.That(ch.Rollback("owner", 3000, "  ").Accepted, Is.False, "a reason is required");
        PublicationResult rb = ch.Rollback("owner", 3000, "burn too strong in playtest");
        Assert.That(rb.Accepted, Is.True);
        Assert.That(ch.Active.BundleId, Is.EqualTo("AK-TR-1"));
        Assert.That(ch.PinnedFor("during"), Is.SameAs(during), "a pinned match keeps its snapshot after rollback");
        Assert.That(ch.PinForNewMatch("after").BundleId, Is.EqualTo("AK-TR-1"));
        Assert.That(ch.History.Select(h => h.Action), Is.EqualTo(new[] { PublicationAction.Initial, PublicationAction.Publish, PublicationAction.Rollback }));
        Assert.That(ch.Find("AK-TR-1.b2"), Is.Not.Null, "rolled-back bundles stay available to replay their matches");
    }

    [Test]
    public void BundleIds_AreNeverReusedForDifferentContent()
    {
        BalanceChannel ch = Channel();
        Assert.That(ch.Publish(Tuned("AK-TR-1.b2", ("Damage.BurnUnits", 600)), "owner", 2000).Accepted, Is.True);
        PublicationResult reuse = ch.Publish(Tuned("AK-TR-1.b2", ("Damage.BurnUnits", 700)), "owner", 3000);
        Assert.That(reuse.Accepted, Is.False);
        Assert.That(reuse.Issues.Select(i => i.Code), Does.Contain("ID_REUSED"));
        Assert.That(ch.Publish(Tuned("AK-TR-1.b2", ("Damage.BurnUnits", 600)), "owner", 3000).Issues.Select(i => i.Code), Does.Contain("ALREADY_ACTIVE"));
        Assert.That(ch.Active.BundleId, Is.EqualTo("AK-TR-1.b2"));
    }

    [Test]
    public void InvalidOrUnknownLineage_DoesNotChangeTheActiveBundle()
    {
        BalanceChannel ch = Channel();
        var bad = Tuned("AK-TR-1.b3", ("Duel.StartHpUnits", 1));
        Assert.That(ch.Publish(bad, "owner", 2000).Accepted, Is.False);
        var skip = new BalanceBundle("AK-TR-1.b3", "AK-TR-1", new[] { new KeyValuePair<string, long>("Damage.BurnUnits", 600) }, "AK-TR-1.b2");
        Assert.That(ch.Publish(skip, "owner", 2000).Issues.Select(i => i.Code), Does.Contain("LINEAGE"));
        Assert.That(ch.Active.IsBaseline, Is.True);
        Assert.That(ch.History, Has.Count.EqualTo(1));
    }

    [Test]
    public void CompiledEngine_RefusesToServeATunedBundle()
    {
        // Since the engine became parameterized (ticket 24) the default channel serves tuned bundles;
        // a deployment that opts into the strict compiled-hash check still refuses them.
        var ch = new BalanceChannel("live", BalanceBundle.Baseline(), "owner", 0, BalanceChannel.CompiledEngineOnly);
        PublicationResult r = ch.Publish(Tuned("AK-TR-1.b2", ("Damage.BurnUnits", 600)), "owner", 1);
        Assert.That(r.Accepted, Is.False);
        Assert.That(r.Issues.Select(i => i.Code), Does.Contain("NOT_EXECUTABLE"));
    }
}
