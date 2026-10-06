using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Client.Land
{
    /// <summary>Which term of Q = min(L, floor(N × max(30,000, C × D) / 1,000,000)) set the allowance.</summary>
    public enum QuotaLimit : byte
    {
        /// <summary>The 3% floor (the HP margin was small).</summary>
        Floor = 0,
        /// <summary>Card cap × HP difference.</summary>
        CapTimesMargin = 1,
        /// <summary>The loser's remaining land.</summary>
        LoserLand = 2,
    }

    /// <summary>One offered card, its exact allowance (from the rule record) and why.</summary>
    public sealed class CardChoice
    {
        public CardId Card { get; internal set; }
        public int CapPercent { get; internal set; }
        /// <summary>The engine's allowance for this card (authoritative).</summary>
        public int Quota { get; internal set; }
        public QuotaLimit LimitedBy { get; internal set; }
        public string NameKey => "card." + (int)Card;
        /// <summary>Localization key of the one-line reason; parameters: cap %, HP margin text, quota, loser cells.</summary>
        public string ReasonKey => LimitedBy == QuotaLimit.Floor ? "land.reason.floor" : LimitedBy == QuotaLimit.LoserLand ? "land.reason.loserLand" : "land.reason.margin";
    }

    /// <summary>The whole card decision explained (ticket 38).</summary>
    public sealed class CardChoiceExplanation
    {
        public int HpDifferenceUnits { get; internal set; }
        public int LoserCells { get; internal set; }
        public List<CardChoice> Choices { get; } = new List<CardChoice>();
        /// <summary>Vajra exists but needs a margin above 60.00 HP; true when that is why it is absent.</summary>
        public bool VajraLockedByMargin { get; internal set; }
        /// <summary>True when every offered quota matched the rules formula (a mismatch would be a bug; the engine's value is shown).</summary>
        public bool Consistent { get; internal set; } = true;
    }

    /// <summary>
    /// Explains the legal card choices after a won duel with the values from the rule record: the
    /// offered cards and quotas come from the engine's public view, and each is re-derived with
    /// <see cref="LandQuota"/> only to name the limiting term. The display always uses the
    /// engine's number.
    /// </summary>
    public static class CardChoices
    {
        public static CardChoiceExplanation Explain(IReadOnlyList<CardId> offered, IReadOnlyList<int> quotas, int hpDifferenceUnits, int loserCells)
        {
            if (offered == null) throw new ArgumentNullException(nameof(offered));
            if (quotas == null || quotas.Count != offered.Count) throw new ArgumentException("One quota per offered card is required.", nameof(quotas));
            var x = new CardChoiceExplanation { HpDifferenceUnits = hpDifferenceUnits, LoserCells = loserCells };
            bool vajraOffered = false;
            for (int i = 0; i < offered.Count; i++)
            {
                CardId card = offered[i];
                vajraOffered |= card == CardId.Vajra;
                int cap = Cards.CapPercent(card);
                int expected = LandQuota.Compute(loserCells, hpDifferenceUnits, cap);
                if (expected != quotas[i]) x.Consistent = false;
                long share = (long)cap * hpDifferenceUnits;
                long uncapped = (long)RulesConstants.ActiveCells * Math.Max(RulesConstants.QuotaFloorPpm, share) / RulesConstants.QuotaDenominator;
                QuotaLimit limit = uncapped > loserCells ? QuotaLimit.LoserLand : share <= RulesConstants.QuotaFloorPpm ? QuotaLimit.Floor : QuotaLimit.CapTimesMargin;
                x.Choices.Add(new CardChoice { Card = card, CapPercent = cap, Quota = quotas[i], LimitedBy = limit });
            }
            x.VajraLockedByMargin = !vajraOffered && hpDifferenceUnits > 0 && hpDifferenceUnits <= RulesConstants.VajraMinExclusiveDiffUnits;
            return x;
        }
    }
}
