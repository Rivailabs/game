using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Tests.Combat;

/// <summary>
/// The combat rows of the plan's "Worked acceptance cases" table. Damage-rule cases use scripted
/// geometric contacts so the rule under test is isolated from aiming; geometry cases are covered in
/// <see cref="GeometryTests"/> and <see cref="FlightSimulatorTests"/>, and a few cases are repeated
/// end-to-end through the flight simulator.
/// </summary>
public class AcceptanceCaseTests
{
    private const PlayerSide A = PlayerSide.A;
    private const PlayerSide B = PlayerSide.B;

    // ---------------------------------------------------------------- Counter arithmetic

    [Test]
    public void CounterArithmetic_EmberHitsVayu_45AndQueuedBurn()
    {
        var state = Kit.State(new[] { 1 }, new[] { 2 });
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(1), Kit.Shot(2), new[] { Kit.Contact(A, ContactKind.Core) });
        Assert.That(r.Explanation.B.DirectDamageUnits, Is.EqualTo(4500));
        Assert.That(r.NewState.B.HpUnits, Is.EqualTo(5500));
        Assert.That(r.NewState.B.BurnDueVolley, Is.EqualTo(2), "another volley follows, so Burn is queued");
        Assert.That(r.Explanation.B.StatusesQueued, Is.EqualTo(new[] { StatusKind.Burn }));

        // Next volley: the 5.00 Burn lands in the health batch (B passes, A passes).
        var r2 = VolleyResolver.Resolve(r.NewState, VolleyInput.Pass(), VolleyInput.Pass());
        Assert.That(r2.Explanation.B.BurnDamageUnits, Is.EqualTo(500));
        Assert.That(r2.NewState.B.HpUnits, Is.EqualTo(5000));
        Assert.That(r2.NewState.B.BurnDueVolley, Is.EqualTo(0), "Burn expires after its volley");
    }

    [Test]
    public void CounterArithmetic_EndToEndThroughFlightSimulation()
    {
        int? pitch = Kit.FindPitch(A, 1, new TargetPose(B, Fixed.Zero, Dodge.None), ContactKind.Core);
        Assert.That(pitch, Is.Not.Null);
        var state = Kit.State(new[] { 1 }, new[] { 2 });
        // B fires its Gale low and wide so it does not interact with the Ember arrow.
        var gale = new VolleyInput(2, -20, 32, 70, Dodge.None);
        var r = VolleyResolver.Resolve(state, new VolleyInput(1, pitch!.Value, 0, 100, Dodge.None), gale);
        Assert.That(r.Explanation.B.Incoming.Single().Kind, Is.EqualTo(ContactKind.Core));
        Assert.That(r.Explanation.B.DirectDamageUnits, Is.EqualTo(4500));
        Assert.That(r.NewState.B.BurnDueVolley, Is.EqualTo(2));
        Assert.That(r.Simulation, Is.Not.Null);
        Assert.That(r.Log.Events.Any(e => e.Type == CombatEventType.Launch), Is.True);
    }

    // ---------------------------------------------------------------- Thunder interpretation

    [Test]
    public void Thunder_AdvantageBecomes200Percent_76NotOneHundredFourteen()
    {
        var state = Kit.State(new[] { 19 }, new[] { 1 });
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(19), Kit.Shot(1), new[] { Kit.Contact(A, ContactKind.Core) });
        Assert.That(r.Explanation.B.DirectDamageUnits, Is.EqualTo(7600));
        Assert.That(r.Explanation.B.Incoming.Single().ElementFactor, Is.EqualTo(Rational.Double));
    }

    [Test]
    public void SuppressedThunder_OrdinaryAdvantage57()
    {
        var state = Kit.State(new[] { 19 }, new[] { 1 });
        state.VolleyIndex = 2;
        state.A.ShockDueVolley = 2; // Shock remains: Thunder is not Tide, so nothing cleanses it
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(19), Kit.Shot(1),
            new[] { Kit.Contact(A, ContactKind.Core, volley: 2) });
        Assert.That(r.Explanation.A.AbilitySuppressed, Is.True);
        Assert.That(r.Explanation.B.DirectDamageUnits, Is.EqualTo(5700));
    }

    // ---------------------------------------------------------------- Rounding

    [Test]
    public void Rounding_StoneDisadvantageGrazeCover_RoundedOnceTo656()
    {
        // Stone (Prithvi) vs Gale (Vayu) is a disadvantage; B grazes with a side dodge on Fort.
        var state = Kit.State(new[] { 3 }, new[] { 2 }, TerrainType.Fort, defender: B);
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(3), Kit.Shot(2, Dodge.Left),
            new[] { Kit.Contact(A, ContactKind.Graze) });
        var c = r.Explanation.B.Incoming.Single();
        Assert.That(c.ElementFactor, Is.EqualTo(Rational.Half));
        Assert.That(c.DodgeFactor, Is.EqualTo(Rational.Half));
        Assert.That(c.CoverFactor, Is.EqualTo(Rational.ThreeQuarters));
        Assert.That(c.DamageUnits, Is.EqualTo(656)); // 6.5625 -> 6.56
        Assert.That(DamageCalculator.ContactDamage(3500, Rational.Half, Rational.Half, Rational.ThreeQuarters), Is.EqualTo(656));
    }

    [Test]
    public void Rounding_HalfCentTieRoundsUp()
    {
        // 15 x 0.5 x 0.75 = 5.625 -> 5.63 (half-up), not banker's 5.62.
        Assert.That(DamageCalculator.ContactDamage(1500, Rational.One, Rational.Half, Rational.ThreeQuarters), Is.EqualTo(563));
    }

    // ---------------------------------------------------------------- Simultaneous healing

    [Test]
    public void SimultaneousHealing_20Minus25PlusRiver10_Ends5()
    {
        var state = Kit.State(new[] { 2 }, new[] { 1 }, TerrainType.River, defender: B);
        state.B.HpUnits = 2000;
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(2), VolleyInput.Pass(),
            new[] { Kit.Contact(A, ContactKind.Core) });
        Assert.That(r.Explanation.B.DirectDamageUnits, Is.EqualTo(2500));
        Assert.That(r.Explanation.B.RiverHealUnits, Is.EqualTo(1000));
        Assert.That(r.NewState.B.HpUnits, Is.EqualTo(500));
        Assert.That(r.NewState.Result, Is.EqualTo(DuelResult.InProgress), "no intermediate death check");
        Assert.That(r.Explanation.A.RiverHealUnits, Is.EqualTo(0), "River benefits only the defender");
    }

    [Test]
    public void SimultaneousHealing_IndependentOfContactListOrder()
    {
        var state = Kit.State(new[] { 6 }, new[] { 1 }, TerrainType.River, defender: B);
        state.B.HpUnits = 4000;
        var c0 = Kit.Contact(A, ContactKind.Core, 0, tick: 50);
        var c1 = Kit.Contact(A, ContactKind.Core, 1, tick: 51);
        var c2 = Kit.Contact(A, ContactKind.Core, 2, tick: 52);
        var forward = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(6), VolleyInput.Pass(), new[] { c0, c1, c2 });
        var reverse = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(6), VolleyInput.Pass(), new[] { c2, c0, c1 });
        Assert.That(forward.NewState.B.HpUnits, Is.EqualTo(4000 - 3600 + 1000));
        Assert.That(reverse.Log.ToCanonicalText(), Is.EqualTo(forward.Log.ToCanonicalText()));
    }

    // ---------------------------------------------------------------- Full-health healing

    [Test]
    public void FullHealthHealing_100Minus60PlusOcean15_Ends55()
    {
        var state = Kit.State(new[] { 20 }, new[] { 1 }, brahmastra: true);
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(20), VolleyInput.Brahmastra(), new[]
        {
            Kit.Contact(B, ContactKind.Brahmastra, tick: 60, sub: 65536),
            Kit.Contact(A, ContactKind.Core, tick: 80),
        });
        Assert.That(r.Explanation.A.DirectDamageUnits, Is.EqualTo(6000));
        Assert.That(r.Explanation.A.OceanHealUnits, Is.EqualTo(1500));
        Assert.That(r.NewState.A.HpUnits, Is.EqualTo(5500));
        Assert.That(r.NewState.B.HpUnits, Is.EqualTo(10000 - 3600), "Brahmastra user is Neutral: Ocean deals 36.00");
    }

    // ---------------------------------------------------------------- Mutual knockout

    [Test]
    public void MutualKnockout_BothZeroInOneUpdate_Draw()
    {
        var duel = new Duel(Kit.State(new[] { 16 }, new[] { 16 }));
        duel.State.A.HpUnits = 3000;
        duel.State.B.HpUnits = 3000;
        var r = duel.ResolveWithScriptedContacts(Kit.Shot(16), Kit.Shot(16), new[]
        {
            Kit.Contact(A, ContactKind.Core, tick: 30),
            Kit.Contact(B, ContactKind.Core, tick: 31),
        });
        Assert.That(r.NewState.A.HpUnits, Is.EqualTo(0));
        Assert.That(r.NewState.B.HpUnits, Is.EqualTo(0));
        Assert.That(duel.Result, Is.EqualTo(DuelResult.Draw));
        Assert.That(duel.Winner, Is.Null);
        Assert.That(duel.HpDifferenceUnits, Is.EqualTo(0), "zero transfer");
    }

    [Test]
    public void MutualKnockout_EndToEndWithRealLances()
    {
        // Lances aimed with different heights so they do not clash: both pass the other and hit.
        var duel = new Duel(Kit.State(new[] { 16 }, new[] { 16 }));
        duel.State.A.HpUnits = 3000;
        duel.State.B.HpUnits = 3000;
        var r = duel.Resolve(new VolleyInput(16, 0, 4, 100, Dodge.None), new VolleyInput(16, -12, 4, 100, Dodge.None));
        Assert.That(r.Explanation.A.Incoming.Count(c => c.IsHit) + r.Explanation.B.Incoming.Count(c => c.IsHit), Is.EqualTo(2));
        Assert.That(duel.Result, Is.EqualTo(DuelResult.Draw));
    }

    // ---------------------------------------------------------------- Due-status cleanse

    [Test]
    public void DueStatusCleanse_TideRemovesBurnAndShockBeforeSuppression()
    {
        var state = Kit.State(new[] { 5, 1 }, new[] { 1 });
        state.VolleyIndex = 2;
        state.A.BurnDueVolley = 2;
        state.A.ShockDueVolley = 2;
        var r = VolleyResolver.Resolve(state, Kit.Shot(5), VolleyInput.Pass());
        Assert.That(r.Explanation.A.CleansedBurn && r.Explanation.A.CleansedShock, Is.True);
        Assert.That(r.Explanation.A.BurnDamageUnits, Is.EqualTo(0));
        Assert.That(r.Explanation.A.Shocked, Is.False);
        Assert.That(r.Explanation.A.AbilitySuppressed, Is.False);
        Assert.That(r.Log.Events.First().Type, Is.EqualTo(CombatEventType.Cleansed));

        // Contrast: without Tide both apply.
        var r2 = VolleyResolver.Resolve(state, Kit.Shot(1), VolleyInput.Pass());
        Assert.That(r2.Explanation.A.BurnDamageUnits, Is.EqualTo(500));
        Assert.That(r2.Explanation.A.AbilitySuppressed, Is.True);
    }

    [Test]
    public void PassCannotCleanse()
    {
        var state = Kit.State(new[] { 5 }, new[] { 1 });
        state.VolleyIndex = 2;
        state.A.BurnDueVolley = 2;
        var r = VolleyResolver.Resolve(state, VolleyInput.Pass(), VolleyInput.Pass());
        Assert.That(r.Explanation.A.BurnDamageUnits, Is.EqualTo(500));
    }

    // ---------------------------------------------------------------- Last-volley status

    [Test]
    public void LastVolleyStatus_EmberInVolleyThree_NoFourthVolleyBurn()
    {
        var state = Kit.State(new[] { 1 }, new[] { 2 });
        state.VolleyIndex = 3;
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(1), Kit.Shot(2),
            new[] { Kit.Contact(A, ContactKind.Core, volley: 3) });
        Assert.That(r.Explanation.B.DirectDamageUnits, Is.EqualTo(4500), "current damage applies");
        Assert.That(r.NewState.B.BurnDueVolley, Is.EqualTo(0));
        Assert.That(r.Explanation.B.StatusesQueued, Is.Empty);
        Assert.That(r.NewState.Result, Is.EqualTo(DuelResult.AWins));

        // A new duel starts clean.
        var nextDuel = DuelState.Start(2, TerrainType.Plain, A, r.NewState.A.Loadout, r.NewState.B.Loadout);
        Assert.That(nextDuel.B.BurnDueVolley, Is.EqualTo(0));
        Assert.That(nextDuel.B.HpUnits, Is.EqualTo(10000));
    }

    // ---------------------------------------------------------------- Multi-arrow shield

    [Test]
    public void MultiArrowShield_FirstQualifyingContactBlocked_RestResolve()
    {
        var state = Kit.State(new[] { 6 }, new[] { 11 });
        // Equal-time contacts consume the shield in ascending projectile index.
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(6), Kit.Shot(11), new[]
        {
            Kit.Contact(A, ContactKind.Core, 2, tick: 70),
            Kit.Contact(A, ContactKind.Core, 0, tick: 70),
            Kit.Contact(A, ContactKind.Core, 1, tick: 70),
        });
        var incoming = r.Explanation.B.Incoming;
        Assert.That(incoming.Select(c => c.Projectile.Index), Is.EqualTo(new[] { 0, 1, 2 }));
        Assert.That(incoming[0].Blocked, Is.True);
        Assert.That(incoming[1].DamageUnits + incoming[2].DamageUnits, Is.EqualTo(2400));
        Assert.That(r.NewState.B.HpUnits, Is.EqualTo(7600));
        Assert.That(r.Explanation.B.AshShieldConsumed, Is.True);
    }

    [Test]
    public void MultiArrowShield_EarliestContactIsBlockedEvenWithHigherIndex()
    {
        var state = Kit.State(new[] { 6 }, new[] { 11 });
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(6), Kit.Shot(11), new[]
        {
            Kit.Contact(A, ContactKind.Core, 0, tick: 71),
            Kit.Contact(A, ContactKind.Core, 2, tick: 70),
        });
        Assert.That(r.Explanation.B.Incoming[0].Projectile.Index, Is.EqualTo(2));
        Assert.That(r.Explanation.B.Incoming[0].Blocked, Is.True);
        Assert.That(r.Explanation.B.Incoming[1].DamageUnits, Is.EqualTo(1200));
    }

    // ---------------------------------------------------------------- Forest graze

    [Test]
    public void ForestGraze_FinalDodgeFactorHalfNotQuarter()
    {
        var state = Kit.State(new[] { 1 }, new[] { 6 }, TerrainType.Forest, defender: B);
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(1), Kit.Shot(6, Dodge.Right),
            new[] { Kit.Contact(A, ContactKind.Graze) });
        var c = r.Explanation.B.Incoming.Single();
        Assert.That(c.ForestApplied, Is.True);
        Assert.That(c.DodgeFactor, Is.EqualTo(Rational.Half));
        Assert.That(c.DamageUnits, Is.EqualTo(1500));
        Assert.That(r.NewState.ForestChargeAvailable, Is.False);
    }

    [Test]
    public void Forest_CoreDuringChargeIsHalved_AndOnlyFirstSideDodgeCounts()
    {
        var state = Kit.State(new[] { 1 }, new[] { 6 }, TerrainType.Forest, defender: B);
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(1), Kit.Shot(6, Dodge.Left),
            new[] { Kit.Contact(A, ContactKind.Core) });
        Assert.That(r.Explanation.B.Incoming.Single().DamageUnits, Is.EqualTo(1500));
        // Second volley: charge is gone, ordinary core.
        var r2 = VolleyResolver.ResolveWithScriptedContacts(r.NewState, Kit.Shot(1), Kit.Shot(6, Dodge.Left),
            new[] { Kit.Contact(A, ContactKind.Core, volley: 2) });
        Assert.That(r2.Explanation.B.Incoming.Single().DamageUnits, Is.EqualTo(3000));
        Assert.That(r2.Explanation.B.BurnDamageUnits, Is.EqualTo(500), "Forest does not reduce Burn");
    }

    [Test]
    public void Forest_ChargeSpentEvenIfAllArrowsMiss_NetForcedNoneSpendsNothing()
    {
        var state = Kit.State(new[] { 1 }, new[] { 6 }, TerrainType.Forest, defender: B);
        var miss = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(1), Kit.Shot(6, Dodge.Right), Array.Empty<GeometricContact>());
        Assert.That(miss.NewState.ForestChargeAvailable, Is.False);

        var netted = state.Clone();
        netted.VolleyIndex = 2;
        netted.B.NetDueVolley = 2;
        var r = VolleyResolver.ResolveWithScriptedContacts(netted, Kit.Shot(1), Kit.Shot(6, Dodge.Right), Array.Empty<GeometricContact>());
        Assert.That(r.Explanation.B.EffectiveDodge, Is.EqualTo(Dodge.None));
        Assert.That(r.Explanation.B.DodgeForcedByNet, Is.True);
        Assert.That(r.NewState.ForestChargeAvailable, Is.True);

        // Jump is not a side dodge and keeps the charge.
        var jump = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(1), Kit.Shot(6, Dodge.Jump), Array.Empty<GeometricContact>());
        Assert.That(jump.NewState.ForestChargeAvailable, Is.True);
    }

    // ---------------------------------------------------------------- Low-shot / torso-shot Jump end-to-end

    [Test]
    public void JumpEndToEnd_LowShotGrazesJumperButStonePiercesToCore()
    {
        var jumpB = new TargetPose(B, Fixed.Zero, Dodge.Jump);
        // Some Ember pitch grazes a jumping B (a low shot under the raised core).
        Assert.That(Kit.FindPitch(A, 1, jumpB, ContactKind.Graze), Is.Not.Null);
        // Torso-height shots still core a jumper.
        Assert.That(Kit.FindPitch(A, 1, jumpB, ContactKind.Core), Is.Not.Null);
        // Stone: find a pitch whose shot is a core with Jump Pierce but not without it.
        var stone = WeaponCatalog.Get(3);
        var range = LaunchProfiles.CentralPitchRange(stone);
        bool found = false;
        for (int p = range.Min; p <= range.Max && !found; p++)
        {
            ContactKind With(bool pierce)
            {
                var specs = LaunchProfiles.BuildPattern(1, 1, A, stone, p, 0, 100, Fixed.Zero, pierce);
                var sim = FlightSimulator.Simulate(specs, new TargetPose(A, Fixed.Zero, Dodge.None), jumpB);
                return sim.Contacts.Count == 0 ? ContactKind.Miss : sim.Contacts[0].Kind;
            }
            found = With(true) == ContactKind.Core && With(false) != ContactKind.Core;
        }
        Assert.That(found, Is.True, "a low Stone shot cores a jumper only thanks to Jump Pierce");
    }
}
