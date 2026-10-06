using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using NUnit.Framework;

namespace AstraKingdoms.Rules.Tests.Core
{
    public class WeaponCatalogTests
    {
        [Test]
        public void TwentyWeaponsWithSequentialIdsAndValidData()
        {
            Assert.That(WeaponCatalog.All.Count, Is.EqualTo(20));
            for (int i = 0; i < 20; i++)
            {
                var w = WeaponCatalog.All[i];
                Assert.That(w.Id, Is.EqualTo(i + 1));
                Assert.That(w.Element, Is.Not.EqualTo(Element.Neutral));
                Assert.That(w.DamagePerProjectileUnits, Is.GreaterThan(0));
                Assert.That(w.ProjectileCount, Is.InRange(1, 3));
                Assert.That(w.MassPerProjectile, Is.InRange(1, 4));
                Assert.That(w.RadiusMm, Is.InRange(30, 120));
                Assert.That(w.Ability, Is.EqualTo((WeaponAbility)w.Id));
            }
        }

        [Test]
        public void FourWeaponsPerElement()
        {
            var counts = new Dictionary<Element, int>();
            foreach (var w in WeaponCatalog.All)
                counts[w.Element] = counts.TryGetValue(w.Element, out var c) ? c + 1 : 1;
            Assert.That(counts.Count, Is.EqualTo(5));
            foreach (var kv in counts) Assert.That(kv.Value, Is.EqualTo(4), kv.Key.ToString());
        }

        [Test]
        public void UnlockLadderFiveStartersThenOnePerLevelTo16()
        {
            Assert.That(WeaponCatalog.UnlockedAtLevel(1).Count, Is.EqualTo(5));
            for (int level = 2; level <= 16; level++)
                Assert.That(WeaponCatalog.UnlockedAtLevel(level).Count, Is.EqualTo(level + 4));
            Assert.That(WeaponCatalog.UnlockedAtLevel(20).Count, Is.EqualTo(20));
        }

        [Test]
        public void PlanDamageValues()
        {
            Assert.That(WeaponCatalog.Get(1).DamagePerProjectileUnits, Is.EqualTo(3000));
            Assert.That(WeaponCatalog.Get(6).DamagePerProjectileUnits, Is.EqualTo(1200));
            Assert.That(WeaponCatalog.Get(6).ProjectileCount, Is.EqualTo(3));
            Assert.That(WeaponCatalog.Get(7).ProjectileCount, Is.EqualTo(2));
            Assert.That(WeaponCatalog.Get(8).DamagePerProjectileUnits, Is.EqualTo(4500));
            Assert.That(WeaponCatalog.Get(19).DamagePerProjectileUnits, Is.EqualTo(3800));
        }

        [Test]
        public void StarterPresetIsFiveBaseElementalWeapons()
        {
            var starter = WeaponCatalog.ForPreset(CatalogPreset.Starter);
            Assert.That(starter.Count, Is.EqualTo(5));
            var elements = new HashSet<Element>();
            foreach (var w in starter) elements.Add(w.Element);
            Assert.That(elements.Count, Is.EqualTo(5));
        }
    }

    public class LoadoutTests
    {
        [Test]
        public void EmptyLoadoutRejected()
        {
            var ex = Assert.Throws<RulesViolationException>(() => Loadout.Create(CatalogPreset.Starter, new int[0]));
            Assert.That(ex.Code, Is.EqualTo("LOADOUT_EMPTY"));
        }

        [Test]
        public void StarterAllowsOneToFiveStarterWeapons()
        {
            Assert.DoesNotThrow(() => Loadout.Create(CatalogPreset.Starter, new[] { 3 }));
            Assert.DoesNotThrow(() => Loadout.Create(CatalogPreset.Starter, new[] { 1, 2, 3, 4, 5 }));
            Assert.That(Assert.Throws<RulesViolationException>(() => Loadout.Create(CatalogPreset.Starter, new[] { 6 })).Code,
                Is.EqualTo("LOADOUT_NOT_IN_CATALOG"));
        }

        [Test]
        public void FullAllowsSixAndRejectsSeven()
        {
            Assert.DoesNotThrow(() => Loadout.Create(CatalogPreset.Full, new[] { 1, 6, 9, 13, 17, 20 }));
            Assert.That(Assert.Throws<RulesViolationException>(() => Loadout.Create(CatalogPreset.Full, new[] { 1, 2, 3, 4, 5, 6, 7 })).Code,
                Is.EqualTo("LOADOUT_TOO_MANY"));
        }

        [Test]
        public void DuplicatesRejected()
        {
            Assert.That(Assert.Throws<RulesViolationException>(() => Loadout.Create(CatalogPreset.Full, new[] { 4, 4 })).Code,
                Is.EqualTo("LOADOUT_DUPLICATE"));
        }

        [Test]
        public void ReserveRules()
        {
            var six = new[] { 1, 2, 3, 4, 5, 6 };
            var ok = Loadout.Create(CatalogPreset.Full, six, 19);
            Assert.That(ok.HasReserve, Is.True);
            Assert.That(ok.Reserve, Is.EqualTo(19));

            Assert.That(Assert.Throws<RulesViolationException>(() => Loadout.Create(CatalogPreset.Full, new[] { 1, 2 }, 19)).Code,
                Is.EqualTo("RESERVE_NEEDS_SIX"));
            Assert.That(Assert.Throws<RulesViolationException>(() => Loadout.Create(CatalogPreset.Full, six, 6)).Code,
                Is.EqualTo("RESERVE_DUPLICATE"));
            Assert.That(Assert.Throws<RulesViolationException>(() => Loadout.Create(CatalogPreset.Starter, new[] { 1, 2, 3, 4, 5 }, 6)).Code,
                Is.EqualTo("RESERVE_NOT_FULL"));
        }
    }

    public class RationalAndHpTests
    {
        [Test]
        public void StoneDisadvantageGrazeCoverRoundsOnceTo656()
        {
            // 35 x 0.5 x 0.5 x 0.75 = 6.5625 -> 6.56 HP (656 units)
            var factor = Rational.Half * Rational.Half * Rational.ThreeQuarters;
            Assert.That(factor.ApplyRoundHalfUp(3500), Is.EqualTo(656));
        }

        [Test]
        public void HalfCentTiesRoundUp()
        {
            // 0.125 HP of 1 unit... use 1 unit x 1/2 = 0.5 unit -> rounds up to 1
            Assert.That(Rational.Half.ApplyRoundHalfUp(1), Is.EqualTo(1));
            Assert.That(Rational.Half.ApplyRoundHalfUp(3), Is.EqualTo(2));
        }

        [Test]
        public void EmberAdvantageIs4500Units()
        {
            Assert.That(Rational.ThreeHalves.ApplyRoundHalfUp(3000), Is.EqualTo(4500));
        }

        [Test]
        public void HpFormatAndClamp()
        {
            Assert.That(Hp.Format(4500), Is.EqualTo("45.00"));
            Assert.That(Hp.Format(656), Is.EqualTo("6.56"));
            Assert.That(Hp.Clamp(-5), Is.EqualTo(0));
            Assert.That(Hp.Clamp(12000), Is.EqualTo(10000));
        }

        [Test]
        public void VajraEligibilityIsStrict()
        {
            Assert.That(Cards.Eligible(6000), Has.No.Member(CardId.Vajra));
            Assert.That(Cards.Eligible(6001), Has.Member(CardId.Vajra));
            Assert.That(Cards.Eligible(0), Is.Empty);
            Assert.That(Cards.Eligible(1).Count, Is.EqualTo(5));
        }
    }
}
