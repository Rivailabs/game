using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Tests.Land;

[TestFixture]
public class LandQuotaTests
{
    private static readonly byte[] ZeroSeed = new byte[32];

    [Test]
    public void CloseWin_AnyCard_Is1531()
    {
        foreach (CardId card in Cards.All)
            Assert.That(LandQuota.Compute(25520, 1, card), Is.EqualTo(1531), card.ToString());
    }

    [Test]
    public void OrdinaryMargin_Winner80Loser20_Chakra_Is3674()
    {
        int d = Hp.FromWhole(80) - Hp.FromWhole(20);
        Assert.That(d, Is.EqualTo(6000));
        Assert.That(LandQuota.Compute(25520, d, CardId.Chakra), Is.EqualTo(3674));
    }

    [Test]
    public void LimitedRemainingLand_ClampsTo900()
    {
        Assert.That(LandQuota.Compute(900, 6000, CardId.Chakra), Is.EqualTo(900));
        Assert.That(LandQuota.Compute(900, 1, CardId.Padma), Is.EqualTo(900));
    }

    [Test]
    public void Draw_GivesNoAllowanceAndNoCard()
    {
        Assert.That(LandQuota.Compute(25520, 0, CardId.Vajra), Is.EqualTo(0));
        Assert.That(LandQuota.IsCardOffered(0), Is.False);
        Assert.That(CardOffers.Pilot(0).Cards, Is.Empty);
        Assert.That(CardOffers.V1(ZeroSeed, 1, 0).Cards, Is.Empty);
    }

    [Test]
    public void NegativeDifference_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LandQuota.Compute(25520, -1, CardId.Chakra));
    }

    [Test]
    public void MaximumVajra_Is10208()
    {
        Assert.That(LandQuota.Compute(25520, 10000, CardId.Vajra), Is.EqualTo(10208));
    }

    [Test]
    public void StrictVajraGate_60_00_Versus_60_01()
    {
        Assert.That(Cards.Eligible(6000), Does.Not.Contain(CardId.Vajra));
        Assert.That(Cards.Eligible(6001), Does.Contain(CardId.Vajra));
        for (int round = 1; round <= 8; round++)
        {
            for (byte s = 0; s < 32; s++)
            {
                var seed = new byte[32];
                seed[0] = s;
                Assert.That(CardOffers.V1(seed, round, 6000).Contains(CardId.Vajra), Is.False);
            }
        }
    }

    [Test]
    public void PilotOffer_IsAlwaysChakraAndSuchi()
    {
        Assert.That(CardOffers.Pilot(1).Cards, Is.EqualTo(new[] { CardId.Chakra, CardId.Suchi }));
        Assert.That(CardOffers.Pilot(10000).Cards, Is.EqualTo(new[] { CardId.Chakra, CardId.Suchi }));
    }

    [Test]
    public void V1Offer_ZeroSeed_MatchesReferenceDraws()
    {
        // Reference values computed independently in Python from the plan's algorithm.
        var fiveEligible = CardOffers.V1(ZeroSeed, 1, 1);
        Assert.That(fiveEligible.Cards, Is.EqualTo(new[] { CardId.Suchi, CardId.Garuda, CardId.Chakra }));
        Assert.That(fiveEligible.StreamCounter, Is.EqualTo(3u));

        var sixEligible = CardOffers.V1(ZeroSeed, 1, 6001);
        Assert.That(sixEligible.Cards, Is.EqualTo(new[] { CardId.Vajra, CardId.Makara, CardId.Padma }));
    }

    [Test]
    public void V1Offer_AlwaysThreeDistinctEligibleCards()
    {
        bool sawVajra = false;
        for (int i = 0; i < 64; i++)
        {
            var seed = new byte[32];
            seed[31] = (byte)i;
            var offer = CardOffers.V1(seed, 1 + i % 8, 9000);
            Assert.That(offer.Cards, Has.Count.EqualTo(3));
            Assert.That(offer.Cards, Is.Unique);
            sawVajra |= offer.Contains(CardId.Vajra);
        }
        Assert.That(sawVajra, Is.True);
    }
}
