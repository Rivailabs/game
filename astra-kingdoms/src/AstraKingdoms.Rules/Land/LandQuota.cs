using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Land
{
    /// <summary>
    /// Maximum legal cutting allowance after a won duel:
    /// Q = min(L, floor(N * max(30,000, C * D) / 1,000,000)), where N = 51,040, L is the loser's
    /// current cell count, D the HP difference in hundredths (units) and C the card's integer
    /// percent cap. C * D is in "percent x HP-units", so 1,000,000 = 100% x 10,000 units and the
    /// 30,000 floor is 3% of the board. Q is a ceiling, never an award.
    /// </summary>
    public static class LandQuota
    {
        /// <summary>Allowance for a given card cap percent. D = 0 (a draw) yields 0: no card, no transfer.</summary>
        public static int Compute(int loserCells, int hpDifferenceUnits, int capPercent)
        {
            if (loserCells < 0) throw new ArgumentOutOfRangeException(nameof(loserCells));
            if (hpDifferenceUnits < 0) throw new ArgumentOutOfRangeException(nameof(hpDifferenceUnits), "Pass the winner's HP minus the loser's.");
            if (capPercent <= 0 || capPercent > 100) throw new ArgumentOutOfRangeException(nameof(capPercent));
            if (hpDifferenceUnits == 0) return 0;

            long share = Math.Max(RulesConstants.QuotaFloorPpm, (long)capPercent * hpDifferenceUnits);
            long allowance = (long)RulesConstants.ActiveCells * share / RulesConstants.QuotaDenominator;
            return (int)Math.Min(loserCells, allowance);
        }

        public static int Compute(int loserCells, int hpDifferenceUnits, CardId card) =>
            Compute(loserCells, hpDifferenceUnits, Cards.CapPercent(card));

        /// <summary>A card is offered only after a duel with a positive HP difference.</summary>
        public static bool IsCardOffered(int hpDifferenceUnits) => hpDifferenceUnits > 0;
    }

    /// <summary>The cards offered to a duel winner and the cards-stream counter consumed.</summary>
    public sealed class CardOffer
    {
        public IReadOnlyList<CardId> Cards { get; }

        /// <summary>Cards-stream counter after the draw (0 for the pilot's fixed offer or no offer).</summary>
        public uint StreamCounter { get; }

        public CardOffer(IReadOnlyList<CardId> cards, uint streamCounter)
        {
            Cards = cards ?? throw new ArgumentNullException(nameof(cards));
            StreamCounter = streamCounter;
        }

        public bool Contains(CardId card)
        {
            foreach (CardId c in Cards) if (c == card) return true;
            return false;
        }
    }

    /// <summary>Card offers after a won duel.</summary>
    public static class CardOffers
    {
        public const int V1OfferSize = 3;

        /// <summary>Pilot: always Chakra and Suchi after a win; nothing after a draw.</summary>
        public static CardOffer Pilot(int hpDifferenceUnits)
        {
            if (!LandQuota.IsCardOffered(hpDifferenceUnits)) return new CardOffer(Array.Empty<CardId>(), 0);
            return new CardOffer(Core.Cards.PilotOffer, 0);
        }

        /// <summary>
        /// V1: three different eligible cards drawn without replacement from the eligible list
        /// (sorted by stable card ID; Vajra only when D &gt; 6,000) using the "AK-TR-1/cards" stream
        /// for <paramref name="round"/> (1-8). Cards are returned in draw order. A draw (D = 0)
        /// offers nothing and consumes no digests.
        /// </summary>
        public static CardOffer V1(byte[] seed, int round, int hpDifferenceUnits)
        {
            if (round < 1 || round > RulesConstants.MaxRounds) throw new ArgumentOutOfRangeException(nameof(round));
            if (!LandQuota.IsCardOffered(hpDifferenceUnits)) return new CardOffer(Array.Empty<CardId>(), 0);

            IReadOnlyList<CardId> eligible = Core.Cards.Eligible(hpDifferenceUnits);
            SeededStream stream = SeededStream.Cards(seed, (uint)round);
            List<CardId> drawn = stream.DrawWithoutReplacement(eligible, V1OfferSize);
            return new CardOffer(drawn, stream.Counter);
        }
    }
}
