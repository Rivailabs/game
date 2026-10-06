using System;
using System.Collections.Generic;

namespace AstraKingdoms.Rules.Core
{
    /// <summary>Formation card caps (integer percent of total board area).</summary>
    public static class Cards
    {
        public static int CapPercent(CardId card)
        {
            switch (card)
            {
                case CardId.Chakra: return 12;
                case CardId.Garuda: return 15;
                case CardId.Suchi: return 18;
                case CardId.Makara: return 14;
                case CardId.Padma: return 10;
                case CardId.Vajra: return 20;
                default: throw new ArgumentOutOfRangeException(nameof(card));
            }
        }

        public static string Name(CardId card) => card.ToString();

        /// <summary>All six V1 cards sorted by stable ID.</summary>
        public static IReadOnlyList<CardId> All { get; } = new[]
        {
            CardId.Chakra, CardId.Garuda, CardId.Suchi, CardId.Makara, CardId.Padma, CardId.Vajra,
        };

        /// <summary>Pilot always offers exactly these two fixed choices.</summary>
        public static IReadOnlyList<CardId> PilotOffer { get; } = new[] { CardId.Chakra, CardId.Suchi };

        /// <summary>
        /// Cards eligible after a won duel with the given positive HP difference (units).
        /// Vajra requires a difference strictly greater than 60.00 HP.
        /// </summary>
        public static IReadOnlyList<CardId> Eligible(int hpDifferenceUnits)
        {
            if (hpDifferenceUnits <= 0) return Array.Empty<CardId>();
            var list = new List<CardId>(6);
            foreach (var c in All)
            {
                if (c == CardId.Vajra && hpDifferenceUnits <= RulesConstants.VajraMinExclusiveDiffUnits) continue;
                list.Add(c);
            }
            return list;
        }
    }
}
