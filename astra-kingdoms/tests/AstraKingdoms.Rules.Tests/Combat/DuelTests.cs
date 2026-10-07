using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Tests.Combat;

public class DuelTests
{
    private const PlayerSide A = PlayerSide.A;
    private const PlayerSide B = PlayerSide.B;

    private static string Code(TestDelegate action) => Assert.Throws<RulesViolationException>(action)!.Code;

    [Test]
    public void SoleSurvivorWinsImmediately()
    {
        var duel = new Duel(Kit.State(new[] { 16 }, new[] { 1 }));
        duel.State.B.HpUnits = 2000;
        duel.ResolveWithScriptedContacts(Kit.Shot(16), VolleyInput.Pass(), new[] { Kit.Contact(A, ContactKind.Core) });
        Assert.That(duel.IsOver, Is.True);
        Assert.That(duel.Winner, Is.EqualTo(A));
        Assert.That(duel.HpUnits(B), Is.EqualTo(0));
        Assert.That(duel.HpDifferenceUnits, Is.EqualTo(10000));
        Assert.That(Code(() => duel.Resolve(VolleyInput.Pass(), VolleyInput.Pass())), Is.EqualTo(InputValidator.DuelOver));
    }

    [Test]
    public void HigherHpAfterVolleyThreeWins_EqualIsDraw()
    {
        var duel = new Duel(Kit.State(new[] { 1 }, new[] { 2 }));
        duel.ResolveWithScriptedContacts(Kit.Shot(1), Kit.Shot(2), new[] { Kit.Contact(B, ContactKind.Core) }); // A takes 25 x 0.5
        Assert.That(duel.IsOver, Is.False);
        Assert.Throws<InvalidOperationException>(() => { var _ = duel.HpDifferenceUnits; });
        duel.Resolve(VolleyInput.Pass(), VolleyInput.Pass());
        Assert.That(duel.CurrentVolley, Is.EqualTo(3));
        duel.Resolve(VolleyInput.Pass(), VolleyInput.Pass());
        Assert.That(duel.Result, Is.EqualTo(DuelResult.BWins));
        Assert.That(duel.HpDifferenceUnits, Is.EqualTo(1250));
        Assert.That(duel.Volleys, Has.Count.EqualTo(3));

        var even = new Duel(Kit.State(new[] { 1 }, new[] { 1 }));
        for (int v = 0; v < 3; v++) even.Resolve(VolleyInput.Pass(), VolleyInput.Pass());
        Assert.That(even.Result, Is.EqualTo(DuelResult.Draw));
        Assert.That(even.HpDifferenceUnits, Is.EqualTo(0));
    }

    [Test]
    public void EarlyLossCanStillWinOnFinalHp()
    {
        var duel = new Duel(Kit.State(new[] { 1 }, new[] { 16 }));
        duel.ResolveWithScriptedContacts(Kit.Shot(1), Kit.Shot(16), new[] { Kit.Contact(B, ContactKind.Core) }); // A -40
        duel.ResolveWithScriptedContacts(Kit.Shot(1), VolleyInput.Pass(), new[] { Kit.Contact(A, ContactKind.Core, volley: 2) }); // B -30
        duel.ResolveWithScriptedContacts(Kit.Shot(1), VolleyInput.Pass(), new[] { Kit.Contact(A, ContactKind.Core, volley: 3) }); // B -30 -5 burn
        Assert.That(duel.HpUnits(A), Is.EqualTo(6000));
        Assert.That(duel.HpUnits(B), Is.EqualTo(3500));
        Assert.That(duel.Winner, Is.EqualTo(A));
    }

    [Test]
    public void ResolveLocks_NullLockIsTimeoutPass()
    {
        var duel = new Duel(Kit.State(new[] { 1 }, new[] { 1 }));
        var r = duel.ResolveLocks(new LockInput(1, Kit.Shot(1)), null);
        Assert.That(r.Explanation.B.IsPass, Is.True);
        Assert.That(r.Explanation.B.WeaponName, Is.EqualTo("Pass"));
        Assert.That(duel.CurrentVolley, Is.EqualTo(2));
    }

    [Test]
    public void LockValidation_RejectsOutOfRangeInputs()
    {
        var state = Kit.State(new[] { 1, 6, 7 }, new[] { 1 });
        void Lock(VolleyInput v, int volley = 1) => InputValidator.ValidateLock(state, A, new LockInput(volley, v));

        Assert.DoesNotThrow(() => Lock(new VolleyInput(1, 0, 0, 70, Dodge.Jump)));
        Assert.DoesNotThrow(() => Lock(new VolleyInput(1, 260, 32, 100, Dodge.Left)));
        Assert.That(Code(() => Lock(new VolleyInput(1, -1, 0, 100, Dodge.None))), Is.EqualTo(InputValidator.PitchRange));
        Assert.That(Code(() => Lock(new VolleyInput(1, 261, 0, 100, Dodge.None))), Is.EqualTo(InputValidator.PitchRange));
        Assert.That(Code(() => Lock(new VolleyInput(1, 100, 33, 100, Dodge.None))), Is.EqualTo(InputValidator.YawRange));
        Assert.That(Code(() => Lock(new VolleyInput(1, 100, -33, 100, Dodge.None))), Is.EqualTo(InputValidator.YawRange));
        Assert.That(Code(() => Lock(new VolleyInput(1, 100, 0, 69, Dodge.None))), Is.EqualTo(InputValidator.PowerRange));
        Assert.That(Code(() => Lock(new VolleyInput(1, 100, 0, 101, Dodge.None))), Is.EqualTo(InputValidator.PowerRange));
        Assert.That(Code(() => Lock(new VolleyInput(1, 100, 0, 100, (Dodge)4))), Is.EqualTo(InputValidator.DodgeInvalid));
        Assert.That(Code(() => Lock(new VolleyInput(2, 100, 0, 100, Dodge.None))), Is.EqualTo(InputValidator.WeaponNotEquipped));
        Assert.That(Code(() => Lock(new VolleyInput(21, 100, 0, 100, Dodge.None))), Is.EqualTo(InputValidator.WeaponUnknown));
        Assert.That(Code(() => Lock(VolleyInput.Pass())), Is.EqualTo(InputValidator.PassServerOnly));
        Assert.That(Code(() => Lock(Kit.Shot(1), volley: 2)), Is.EqualTo(InputValidator.VolleyIndexInvalid));
        Assert.That(Code(() => Lock(Kit.Shot(1), volley: 4)), Is.EqualTo(InputValidator.VolleyIndexInvalid));
    }

    [Test]
    public void LockValidation_UsesNarrowedPatternRanges()
    {
        var state = Kit.State(new[] { 6, 7 }, new[] { 1 });
        void Lock(VolleyInput v) => InputValidator.ValidateLock(state, A, new LockInput(1, v));
        // Fire Fan: pitch 3..62 deg, yaw -6.5..+6.5 deg.
        Assert.That(Code(() => Lock(new VolleyInput(6, 11, 0, 100, Dodge.None))), Is.EqualTo(InputValidator.PitchRange));
        Assert.DoesNotThrow(() => Lock(new VolleyInput(6, 12, -26, 100, Dodge.None)));
        Assert.DoesNotThrow(() => Lock(new VolleyInput(6, 248, 26, 100, Dodge.None)));
        Assert.That(Code(() => Lock(new VolleyInput(6, 249, 0, 100, Dodge.None))), Is.EqualTo(InputValidator.PitchRange));
        Assert.That(Code(() => Lock(new VolleyInput(6, 100, 27, 100, Dodge.None))), Is.EqualTo(InputValidator.YawRange));
        // Twin Gust: yaw -8..+4 deg so the +4 deg second launch stays within +8.
        Assert.DoesNotThrow(() => Lock(new VolleyInput(7, -20, 16, 100, Dodge.None)));
        Assert.That(Code(() => Lock(new VolleyInput(7, 0, 17, 100, Dodge.None))), Is.EqualTo(InputValidator.YawRange));
        Assert.DoesNotThrow(() => Lock(new VolleyInput(7, 0, -32, 100, Dodge.None)));
    }

    [Test]
    public void Brahmastra_RequiresFlagChargeAndCanonicalPlaceholders()
    {
        var off = Kit.State(new[] { 1 }, new[] { 1 });
        Assert.That(Code(() => InputValidator.ValidateLock(off, A, new LockInput(1, VolleyInput.Brahmastra()))), Is.EqualTo(InputValidator.BrahmastraDisabled));

        var on = Kit.State(new[] { 1 }, new[] { 1 }, brahmastra: true);
        Assert.DoesNotThrow(() => InputValidator.ValidateLock(on, A, new LockInput(1, VolleyInput.Brahmastra())));
        Assert.That(Code(() => InputValidator.ValidateLock(on, A, new LockInput(1, new VolleyInput(1000, 4, 0, 100, Dodge.None)))),
            Is.EqualTo(InputValidator.BrahmastraPlaceholder));
        Assert.That(Code(() => InputValidator.ValidateLock(on, A, new LockInput(1, new VolleyInput(1000, 0, 0, 100, Dodge.Jump)))),
            Is.EqualTo(InputValidator.BrahmastraPlaceholder));

        var duel = new Duel(on);
        duel.Resolve(VolleyInput.Brahmastra(), VolleyInput.Pass());
        Assert.That(Code(() => duel.ValidateLock(A, new LockInput(2, VolleyInput.Brahmastra()))), Is.EqualTo(InputValidator.BrahmastraSpent));
        Assert.DoesNotThrow(() => duel.ValidateLock(B, new LockInput(2, VolleyInput.Brahmastra())));

        Assert.That(Assert.Throws<RulesViolationException>(() =>
            DuelState.Start(1, TerrainType.Plain, A, Loadout.Create(CatalogPreset.Starter, new[] { 1 }),
                Loadout.Create(CatalogPreset.Starter, new[] { 1 }), brahmastraEnabled: true))!.Code, Is.EqualTo("BRAHMASTRA_NOT_FULL"));
    }

    [Test]
    public void ArmouryReserve_OnlyForTheDefenderInFull()
    {
        var six = new[] { 1, 2, 3, 4, 5, 6 };
        var la = Kit.FullWithReserve(six, 16);
        var lb = Kit.FullWithReserve(six, 17);
        var armoury = DuelState.Start(1, TerrainType.Armoury, B, la, lb);
        Assert.DoesNotThrow(() => InputValidator.ValidateLock(armoury, B, new LockInput(1, Kit.Shot(17))));
        Assert.That(Code(() => InputValidator.ValidateLock(armoury, A, new LockInput(1, Kit.Shot(16)))), Is.EqualTo(InputValidator.ReserveNotEligible));

        var plain = DuelState.Start(1, TerrainType.Plain, B, la, lb);
        Assert.That(Code(() => InputValidator.ValidateLock(plain, B, new LockInput(1, Kit.Shot(17)))), Is.EqualTo(InputValidator.ReserveNotEligible));

        // The reserve resolves like any equipped weapon when eligible.
        var r = VolleyResolver.Resolve(armoury, VolleyInput.Pass(), Kit.Shot(17));
        Assert.That(r.Explanation.B.WeaponName, Is.EqualTo("Sky Dive"));
    }

    [Test]
    public void EveryWeaponFiresLegalShotsAtItsRangeBounds()
    {
        foreach (var w in WeaponCatalog.All)
        {
            var pitch = LaunchProfiles.CentralPitchRange(w);
            var yaw = LaunchProfiles.CentralYawRange(w);
            foreach (var side in new[] { A, B })
            foreach (int p in new[] { pitch.Min, pitch.Max })
            foreach (int y in new[] { yaw.Min, yaw.Max })
            foreach (int power in new[] { 70, 100 })
            {
                var state = Kit.State(new[] { w.Id }, new[] { w.Id });
                var shot = new VolleyInput(w.Id, p, y, power, Dodge.None);
                InputValidator.ValidateLock(state, side, new LockInput(1, shot));
                var r = side == A
                    ? VolleyResolver.Resolve(state, shot, VolleyInput.Pass())
                    : VolleyResolver.Resolve(state, VolleyInput.Pass(), shot);
                var tracks = r.Simulation!.Tracks;
                Assert.That(tracks, Has.Count.EqualTo(w.ProjectileCount), w.Name);
                foreach (var t in tracks)
                    Assert.That(t.Termination, Is.Not.EqualTo(TerminationReason.None), w.Name + " p" + p + " y" + y);
            }
        }
    }

    [Test]
    public void FullResolutionIsDeterministic()
    {
        VolleyResult Run()
        {
            var state = Kit.State(new[] { 6, 1 }, new[] { 7, 8 }, TerrainType.Forest, defender: A);
            state.A.BaselineOffsetRight = Kit.M(25);
            return VolleyResolver.Resolve(state, new VolleyInput(6, 90, 5, 91, Dodge.Left), new VolleyInput(7, 30, -10, 85, Dodge.Jump));
        }
        var first = Run();
        var second = Run();
        Assert.That(second.Log.ToCanonicalBytes(), Is.EqualTo(first.Log.ToCanonicalBytes()));
        Assert.That(second.Explanation.ToText(), Is.EqualTo(first.Explanation.ToText()));
        Assert.That(first.Log.Events.Select(e => e.Sequence), Is.EqualTo(Enumerable.Range(0, first.Log.Count)));
    }

    [Test]
    public void InputStateIsNotMutatedByResolution()
    {
        var state = Kit.State(new[] { 1 }, new[] { 2 });
        var r = VolleyResolver.ResolveWithScriptedContacts(state, Kit.Shot(1), Kit.Shot(2), new[] { Kit.Contact(A, ContactKind.Core) });
        Assert.That(state.B.HpUnits, Is.EqualTo(10000));
        Assert.That(state.VolleyIndex, Is.EqualTo(1));
        Assert.That(r.NewState.VolleyIndex, Is.EqualTo(2));
    }
}
