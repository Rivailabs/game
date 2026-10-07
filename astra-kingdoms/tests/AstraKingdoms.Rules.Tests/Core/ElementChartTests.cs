using AstraKingdoms.Rules.Core;
using NUnit.Framework;

namespace AstraKingdoms.Rules.Tests.Core
{
    public class ElementChartTests
    {
        private static readonly Element[] Five =
        {
            Element.Agni, Element.Vayu, Element.Prithvi, Element.Vidyut, Element.Varuna,
        };

        [TestCase(Element.Agni, Element.Vayu)]
        [TestCase(Element.Agni, Element.Prithvi)]
        [TestCase(Element.Vayu, Element.Prithvi)]
        [TestCase(Element.Vayu, Element.Vidyut)]
        [TestCase(Element.Prithvi, Element.Vidyut)]
        [TestCase(Element.Prithvi, Element.Varuna)]
        [TestCase(Element.Vidyut, Element.Varuna)]
        [TestCase(Element.Vidyut, Element.Agni)]
        [TestCase(Element.Varuna, Element.Agni)]
        [TestCase(Element.Varuna, Element.Vayu)]
        public void PlanCounterTable(Element attacker, Element defender)
        {
            Assert.That(ElementChart.Beats(attacker, defender), Is.True);
            Assert.That(ElementChart.Beats(defender, attacker), Is.False);
            Assert.That(ElementChart.Multiplier(attacker, defender), Is.EqualTo(Rational.ThreeHalves));
            Assert.That(ElementChart.Multiplier(defender, attacker), Is.EqualTo(Rational.Half));
        }

        [Test]
        public void EveryElementBeatsTwoAndLosesToTwo()
        {
            foreach (var a in Five)
            {
                int beats = 0, loses = 0;
                foreach (var d in Five)
                {
                    if (ElementChart.Beats(a, d)) beats++;
                    if (ElementChart.Beats(d, a)) loses++;
                }
                Assert.That(beats, Is.EqualTo(2), a.ToString());
                Assert.That(loses, Is.EqualTo(2), a.ToString());
                Assert.That(ElementChart.Multiplier(a, a), Is.EqualTo(Rational.One));
            }
        }

        [Test]
        public void MeanMultiplierAgainstUniformSelectionIsOne()
        {
            foreach (var a in Five)
            {
                var sum = 0m;
                foreach (var d in Five)
                {
                    var m = ElementChart.Multiplier(a, d);
                    sum += (decimal)m.Numerator / m.Denominator;
                }
                Assert.That(sum / 5m, Is.EqualTo(1m));
            }
        }

        [Test]
        public void NeutralHasNoAdvantageEitherWay()
        {
            foreach (var e in Five)
            {
                Assert.That(ElementChart.Multiplier(Element.Neutral, e), Is.EqualTo(Rational.One));
                Assert.That(ElementChart.Multiplier(e, Element.Neutral), Is.EqualTo(Rational.One));
            }
        }

        [Test]
        public void ThunderReplacesAdvantageRatherThanMultiplying()
        {
            Assert.That(ElementChart.Multiplier(Element.Vidyut, Element.Agni, thunderAdvantage: true), Is.EqualTo(Rational.Double));
            Assert.That(ElementChart.Multiplier(Element.Vidyut, Element.Vidyut, thunderAdvantage: true), Is.EqualTo(Rational.One));
            Assert.That(ElementChart.Multiplier(Element.Vidyut, Element.Vayu, thunderAdvantage: true), Is.EqualTo(Rational.Half));
        }
    }
}
