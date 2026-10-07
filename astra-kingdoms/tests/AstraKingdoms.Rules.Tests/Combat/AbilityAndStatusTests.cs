using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Tests.Combat;

public class AbilityAndStatusTests
{
    private const PlayerSide A = PlayerSide.A;
    private const PlayerSide B = PlayerSide.B;

    private static VolleyResult Hit(DuelState s, VolleyInput a, VolleyInput b, params GeometricContact[] contacts) =>
        VolleyResolver.ResolveWithScriptedContacts(s, a, b, contacts);

    [Test]
    public void Cyclone_ReversesOpponentSideDodge_NetHasPriority()
    {
        var state = Kit.State(new[] { 12 }, new[] { 1 });
        var r = Hit(state, Kit.Shot(12), Kit.Shot(1, Dodge.Left));
        Assert.That(r.Explanation.B.EffectiveDodge, Is.EqualTo(Dodge.Right));
        Assert.That(r.Explanation.B.DodgeReversedByCyclone, Is.True);

        var jump = Hit(state, Kit.Shot(12), Kit.Shot(1, Dodge.Jump));
        Assert.That(jump.Explanation.B.EffectiveDodge, Is.EqualTo(Dodge.Jump));

        var netted = state.Clone();
        netted.VolleyIndex = 2;
        netted.B.NetDueVolley = 2;
        var n = Hit(netted, Kit.Shot(12), Kit.Shot(1, Dodge.Left));
        Assert.That(n.Explanation.B.EffectiveDodge, Is.EqualTo(Dodge.None));
        Assert.That(n.Explanation.B.DodgeReversedByCyclone, Is.False);

        var shocked = state.Clone();
        shocked.VolleyIndex = 2;
        shocked.A.ShockDueVolley = 2;
        Assert.That(Hit(shocked, Kit.Shot(12), Kit.Shot(1, Dodge.Left)).Explanation.B.EffectiveDodge, Is.EqualTo(Dodge.Left));
    }

    [Test]
    public void StormNet_ForcesNoneNextVolley()
    {
        var state = Kit.State(new[] { 14 }, new[] { 1 });
        var r = Hit(state, Kit.Shot(14), Kit.Shot(1), Kit.Contact(A, ContactKind.Core));
        Assert.That(r.NewState.B.NetDueVolley, Is.EqualTo(2));
        var r2 = Hit(r.NewState, VolleyInput.Pass(), Kit.Shot(1, Dodge.Right));
        Assert.That(r2.Explanation.B.EffectiveDodge, Is.EqualTo(Dodge.None));
        Assert.That(r2.NewState.B.NetDueVolley, Is.EqualTo(0));
    }

    [Test]
    public void Quake_AddsFiveDegreesClippedToCentralRange()
    {
        var state = Kit.State(new[] { 18 }, new[] { 1, 6 });
        var r = Hit(state, Kit.Shot(18), Kit.Shot(1), Kit.Contact(A, ContactKind.Core));
        Assert.That(r.NewState.B.QuakeDueVolley, Is.EqualTo(2));

        var r2 = Hit(r.NewState, VolleyInput.Pass(), Kit.Shot(1, pitchQdeg: 100));
        Assert.That(r2.Explanation.B.QuakeApplied, Is.True);
        Assert.That(r2.Explanation.B.EffectivePitchQdeg, Is.EqualTo(120));

        // Clipped to Fire Fan's narrowed central maximum (62 deg = 248 qdeg).
        var r3 = Hit(r.NewState, VolleyInput.Pass(), Kit.Shot(6, pitchQdeg: 240));
        Assert.That(r3.Explanation.B.EffectivePitchQdeg, Is.EqualTo(248));
    }

    [Test]
    public void IronWall_CoversThisAndNextVolley()
    {
        var state = Kit.State(new[] { 1 }, new[] { 13 });
        var v1 = Hit(state, Kit.Shot(1), Kit.Shot(13), Kit.Contact(A, ContactKind.Core));
        Assert.That(v1.Explanation.B.IronWallRaised, Is.True);
        Assert.That(v1.Explanation.B.Incoming[0].DamageUnits, Is.EqualTo(3375)); // 30 x 1.5 x 0.75

        var v2 = Hit(v1.NewState, Kit.Shot(1), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core, volley: 2));
        Assert.That(v2.Explanation.B.Incoming[0].DamageUnits, Is.EqualTo(2250)); // neutral, covered
        Assert.That(v2.Explanation.B.BurnDamageUnits, Is.EqualTo(500), "Burn ignores cover");

        var v3 = Hit(v2.NewState, Kit.Shot(1), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core, volley: 3));
        Assert.That(v3.Explanation.B.Incoming[0].DamageUnits, Is.EqualTo(3000), "Iron Wall expired");
    }

    [Test]
    public void IronWall_SuppressedByShock_AndCoverDoesNotStackWithFort()
    {
        var state = Kit.State(new[] { 1 }, new[] { 13 }, TerrainType.Fort, defender: B);
        var v = Hit(state, Kit.Shot(1), Kit.Shot(13), Kit.Contact(A, ContactKind.Core));
        Assert.That(v.Explanation.B.Incoming[0].CoverFactor, Is.EqualTo(Rational.ThreeQuarters));

        var shocked = Kit.State(new[] { 1 }, new[] { 13 });
        shocked.VolleyIndex = 2;
        shocked.B.ShockDueVolley = 2;
        var s = Hit(shocked, Kit.Shot(1), Kit.Shot(13), Kit.Contact(A, ContactKind.Core, volley: 2));
        Assert.That(s.Explanation.B.IronWallRaised, Is.False);
        Assert.That(s.Explanation.B.Incoming[0].DamageUnits, Is.EqualTo(4500));
    }

    [Test]
    public void Flood_RemovesFortForRestOfDuel_BeforeItsOwnDamage()
    {
        var state = Kit.State(new[] { 15, 1 }, new[] { 1 }, TerrainType.Fort, defender: B);
        var v1 = Hit(state, Kit.Shot(15), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core));
        var c = v1.Explanation.B.Incoming[0];
        Assert.That(c.CoverRemovedByFlood, Is.True);
        Assert.That(c.CoverFactor, Is.EqualTo(Rational.One));
        Assert.That(c.DamageUnits, Is.EqualTo(3200));
        Assert.That(v1.NewState.FortCoverRemoved, Is.True);

        var v2 = Hit(v1.NewState, Kit.Shot(1), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core, volley: 2));
        Assert.That(v2.Explanation.B.Incoming[0].DamageUnits, Is.EqualTo(3000), "Fort stays removed");
    }

    [Test]
    public void Flood_ClearsIronWall_ButALaterIronWallCoversAgain()
    {
        var state = Kit.State(new[] { 15, 1 }, new[] { 13 });
        var v1 = Hit(state, Kit.Shot(15), Kit.Shot(13), Kit.Contact(A, ContactKind.Core));
        Assert.That(v1.Explanation.B.Incoming[0].CoverRemovedByFlood, Is.True);
        Assert.That(v1.NewState.B.IronWallActiveIn(2), Is.False);

        var v2 = Hit(v1.NewState, Kit.Shot(1), Kit.Shot(13), Kit.Contact(A, ContactKind.Core, volley: 2));
        Assert.That(v2.Explanation.B.Incoming[0].CoverFactor, Is.EqualTo(Rational.ThreeQuarters));
    }

    [Test]
    public void ChainBolt_BonusOnlyAgainstCoveredUnshieldedBodyHit()
    {
        var fort = Kit.State(new[] { 9 }, new[] { 11, 1 }, TerrainType.Fort, defender: B);
        var covered = Hit(fort, Kit.Shot(9), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core));
        var c = covered.Explanation.B.Incoming[0];
        Assert.That(c.DamageUnits, Is.EqualTo(1950)); // 26 x 0.75
        Assert.That(c.ChainBonusUnits, Is.EqualTo(1000));
        Assert.That(covered.NewState.B.HpUnits, Is.EqualTo(10000 - 2950));

        // Graze plus cover: the bonus bypasses dodge too.
        var graze = Hit(fort, Kit.Shot(9), Kit.Shot(1, Dodge.Left), Kit.Contact(A, ContactKind.Graze));
        Assert.That(graze.Explanation.B.Incoming[0].ChainBonusUnits, Is.EqualTo(1000));

        var plain = Kit.State(new[] { 9 }, new[] { 11 });
        var open = Hit(plain, Kit.Shot(9), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core));
        Assert.That(open.Explanation.B.Incoming[0].ChainBonusUnits, Is.EqualTo(0));

        var shielded = Hit(fort, Kit.Shot(9), Kit.Shot(11), Kit.Contact(A, ContactKind.Core));
        Assert.That(shielded.Explanation.B.Incoming[0].Blocked, Is.True);
        Assert.That(shielded.Explanation.B.DirectDamageUnits, Is.EqualTo(0));
    }

    [Test]
    public void SkyDive_IgnoresCoverUnlessSuppressed()
    {
        var fort = Kit.State(new[] { 17 }, new[] { 1 }, TerrainType.Fort, defender: B);
        var r = Hit(fort, Kit.Shot(17), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core));
        Assert.That(r.Explanation.B.Incoming[0].CoverIgnored, Is.True);
        Assert.That(r.Explanation.B.Incoming[0].DamageUnits, Is.EqualTo(3500));

        fort.VolleyIndex = 2;
        fort.A.ShockDueVolley = 2;
        var s = Hit(fort, Kit.Shot(17), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core, volley: 2));
        Assert.That(s.Explanation.B.Incoming[0].DamageUnits, Is.EqualTo(2625));
    }

    [Test]
    public void TerrainBenefitsOnlyTheDefender()
    {
        var fort = Kit.State(new[] { 1 }, new[] { 1 }, TerrainType.Fort, defender: B);
        var r = Hit(fort, Kit.Shot(1), Kit.Shot(1), Kit.Contact(A, ContactKind.Core), Kit.Contact(B, ContactKind.Core));
        Assert.That(r.Explanation.B.Incoming[0].DamageUnits, Is.EqualTo(2250));
        Assert.That(r.Explanation.A.Incoming[0].DamageUnits, Is.EqualTo(3000));
    }

    [Test]
    public void GalePush_ShiftsBaselineToTargetsRight_CappedAtHalfMetre()
    {
        var state = Kit.State(new[] { 2 }, new[] { 1 });
        var v1 = Hit(state, Kit.Shot(2), Kit.Shot(1), Kit.Contact(A, ContactKind.Core));
        Assert.That(v1.NewState.B.BaselineOffsetRight, Is.EqualTo(Kit.M(25)));

        // The pushed baseline moves B's launch point to its local Right (world -z) in the next volley.
        var v2 = VolleyResolver.Resolve(v1.NewState, VolleyInput.Pass(), Kit.Shot(1));
        var launch = v2.Log.Events.Single(e => e.Type == CombatEventType.Launch);
        Assert.That(launch.Position.Z, Is.EqualTo(-Kit.M(25)));

        var capped = state.Clone();
        capped.B.BaselineOffsetRight = Kit.M(50);
        var v3 = Hit(capped, Kit.Shot(2), Kit.Shot(1), Kit.Contact(A, ContactKind.Core));
        Assert.That(v3.NewState.B.BaselineOffsetRight, Is.EqualTo(Kit.M(50)));
    }

    [Test]
    public void MistVeil_ConcealsOwnersNextVolley()
    {
        var state = Kit.State(new[] { 10 }, new[] { 1 });
        var v1 = Hit(state, Kit.Shot(10), Kit.Shot(1), Kit.Contact(A, ContactKind.Core));
        Assert.That(v1.NewState.A.VeilVolley, Is.EqualTo(2));
        var v2 = Hit(v1.NewState, Kit.Shot(10), Kit.Shot(1));
        Assert.That(v2.Explanation.A.ConcealedFromOpponent, Is.True);
        Assert.That(v2.Explanation.B.ConcealedFromOpponent, Is.False);
        Assert.That(v2.NewState.A.VeilVolley, Is.EqualTo(0));
    }

    [Test]
    public void Shock_SuppressesNextVolleyOptionalAbilityButNotIntrinsics()
    {
        var state = Kit.State(new[] { 4 }, new[] { 1, 6 });
        var v1 = Hit(state, Kit.Shot(4), Kit.Shot(1), Kit.Contact(A, ContactKind.Core));
        Assert.That(v1.NewState.B.ShockDueVolley, Is.EqualTo(2));

        // Shocked Ember hit: damage applies, Burn is not queued.
        var v2 = Hit(v1.NewState, VolleyInput.Pass(), Kit.Shot(1), Kit.Contact(B, ContactKind.Core, volley: 2));
        Assert.That(v2.Explanation.B.AbilitySuppressed, Is.True);
        Assert.That(v2.NewState.A.BurnDueVolley, Is.EqualTo(0));
        Assert.That(v2.Explanation.A.DirectDamageUnits, Is.EqualTo(3000));

        // Fire Fan's spread is intrinsic: Shock has nothing to remove.
        var v2b = Hit(v1.NewState, VolleyInput.Pass(), Kit.Shot(6));
        Assert.That(v2b.Explanation.B.AbilitySuppressed, Is.False);
    }

    [Test]
    public void Burn_RefreshesRatherThanStacks()
    {
        var state = Kit.State(new[] { 1 }, new[] { 2 });
        var v1 = Hit(state, Kit.Shot(1), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core));
        var v2 = Hit(v1.NewState, Kit.Shot(1), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core, volley: 2));
        Assert.That(v2.Explanation.B.BurnDamageUnits, Is.EqualTo(500));
        Assert.That(v2.NewState.B.BurnDueVolley, Is.EqualTo(3), "fresh instance queued; the due one expired");
        var v3 = Hit(v2.NewState, VolleyInput.Pass(), VolleyInput.Pass());
        Assert.That(v3.Explanation.B.BurnDamageUnits, Is.EqualTo(500));
    }

    [Test]
    public void MissesDoNotTriggerOnHitAbilities()
    {
        var state = Kit.State(new[] { 1 }, new[] { 11 });
        var blocked = Hit(state, Kit.Shot(1), Kit.Shot(11), Kit.Contact(A, ContactKind.Core));
        Assert.That(blocked.NewState.B.BurnDueVolley, Is.EqualTo(0), "fully shielded contact is not a hit");
        var miss = Hit(state, Kit.Shot(1), VolleyInput.Pass());
        Assert.That(miss.NewState.B.BurnDueVolley, Is.EqualTo(0));
    }

    [Test]
    public void Brahmastra_GuaranteedNeutral60_BypassesDodgeAndCover()
    {
        var state = Kit.State(new[] { 1 }, new[] { 3 }, TerrainType.Fort, defender: B, brahmastra: true);
        var r = VolleyResolver.Resolve(state, VolleyInput.Brahmastra(), Kit.Shot(3, Dodge.Jump));
        var strike = r.Log.Events.Single(e => e.Type == CombatEventType.BrahmastraStrike);
        Assert.That(strike.Tick, Is.EqualTo(60));
        Assert.That(r.Explanation.B.Incoming.Single(c => c.Kind == ContactKind.Brahmastra).DamageUnits, Is.EqualTo(6000));
        Assert.That(r.NewState.A.BrahmastraAvailable, Is.False);
        Assert.That(r.Explanation.A.DefensiveElement, Is.EqualTo(Element.Neutral));
        Assert.That(r.Simulation!.Tracks.Count(t => t.Spec.Owner == A), Is.EqualTo(0), "no projectile, no clash");
    }

    [Test]
    public void Brahmastra_BlockedByAshShield_ChargeStillConsumed_ShockIrrelevant()
    {
        var state = Kit.State(new[] { 1 }, new[] { 11 }, brahmastra: true);
        var blocked = VolleyResolver.Resolve(state, VolleyInput.Brahmastra(), Kit.Shot(11));
        Assert.That(blocked.Explanation.B.Incoming.Single(c => c.Kind == ContactKind.Brahmastra).Blocked, Is.True);
        Assert.That(blocked.NewState.A.BrahmastraAvailable, Is.False);

        var shocked = Kit.State(new[] { 1 }, new[] { 1 }, brahmastra: true);
        shocked.VolleyIndex = 2;
        shocked.A.ShockDueVolley = 2;
        var s = VolleyResolver.Resolve(shocked, VolleyInput.Brahmastra(), VolleyInput.Pass());
        Assert.That(s.NewState.B.HpUnits, Is.EqualTo(4000));
    }

    [Test]
    public void Pass_HasNoProjectileNoDodgeAndNeutralElement()
    {
        var state = Kit.State(new[] { 1 }, new[] { 1 });
        var r = Hit(state, Kit.Shot(1), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Core));
        Assert.That(r.Explanation.B.DefensiveElement, Is.EqualTo(Element.Neutral));
        Assert.That(r.Explanation.B.EffectiveDodge, Is.EqualTo(Dodge.None));
        Assert.That(r.Explanation.B.DirectDamageUnits, Is.EqualTo(3000));
        Assert.Throws<ArgumentException>(() => Hit(state, Kit.Shot(1), VolleyInput.Pass(), Kit.Contact(B, ContactKind.Core)));
        Assert.Throws<ArgumentException>(() => Hit(state, Kit.Shot(1), VolleyInput.Pass(), Kit.Contact(A, ContactKind.Graze)));
    }

    [Test]
    public void ExplanationIsReadable()
    {
        var state = Kit.State(new[] { 1 }, new[] { 2 }, TerrainType.Fort, defender: B);
        var r = Hit(state, Kit.Shot(1), Kit.Shot(2, Dodge.Left), Kit.Contact(A, ContactKind.Graze));
        string text = r.Explanation.ToText();
        StringAssert.Contains("Ember Arrow", text);
        StringAssert.Contains("Graze", text);
        StringAssert.Contains("HP 100.00 -> 83.12", text); // 30 x 1.5 x 0.5 x 0.75 = 16.875 -> 16.88
    }
}
