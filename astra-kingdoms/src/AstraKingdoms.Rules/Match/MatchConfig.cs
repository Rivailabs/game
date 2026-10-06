using System;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Match
{
    /// <summary>How the two players are connected. Rules are identical; only default timers differ.</summary>
    public enum MatchMode : byte
    {
        /// <summary>Two people on one phone, choices entered privately in sequence.</summary>
        SharedPhone = 0,
        /// <summary>Two connected clients against the authoritative match service.</summary>
        Online = 1,
        /// <summary>Local practice (a person against a bot); the host may pause its own clock.</summary>
        Practice = 2,
    }

    /// <summary>Which card-offer rule applies after a won duel.</summary>
    public enum CardOfferRule : byte
    {
        /// <summary>Pilot: always the two fixed choices Chakra and Suchi.</summary>
        Pilot = 0,
        /// <summary>V1: three different eligible cards drawn from the "AK-TR-1/cards" stream.</summary>
        V1 = 1,
    }

    /// <summary>
    /// Immutable room configuration fixed before either loadout is submitted (tickets 2, 15, 19).
    /// Use the factories for the approved presets; <see cref="Validate"/> rejects illegal combinations.
    /// </summary>
    public sealed class MatchConfig
    {
        public MatchMode Mode { get; }
        public CatalogPreset Catalog { get; }
        public CardOfferRule CardOffers { get; }
        /// <summary>Stable terrain template ID (see <see cref="TerrainTemplates"/>).</summary>
        public string TerrainTemplateId { get; }
        /// <summary>Private-room experimental Brahmastra flag (Full catalog only, never in the pilot).</summary>
        public bool BrahmastraEnabled { get; }
        /// <summary>Rules version the match is pinned to.</summary>
        public string RulesVersion { get; }

        public MatchConfig(MatchMode mode, CatalogPreset catalog, CardOfferRule cardOffers, string terrainTemplateId,
            bool brahmastraEnabled = false, string rulesVersion = RulesConstants.RulesVersion)
            : this(mode, catalog, cardOffers, terrainTemplateId, brahmastraEnabled, rulesVersion, validate: true)
        {
        }

        private MatchConfig(MatchMode mode, CatalogPreset catalog, CardOfferRule cardOffers, string terrainTemplateId,
            bool brahmastraEnabled, string rulesVersion, bool validate)
        {
            Mode = mode;
            Catalog = catalog;
            CardOffers = cardOffers;
            TerrainTemplateId = terrainTemplateId;
            BrahmastraEnabled = brahmastraEnabled;
            RulesVersion = rulesVersion;
            if (validate) Validate();
        }

        /// <summary>Reconstructs a stored config without validating it (records of other rules versions).</summary>
        internal static MatchConfig CreateUnvalidated(MatchMode mode, CatalogPreset catalog, CardOfferRule cardOffers,
            string terrainTemplateId, bool brahmastraEnabled, string rulesVersion) =>
            new MatchConfig(mode, catalog, cardOffers, terrainTemplateId, brahmastraEnabled, rulesVersion, validate: false);

        /// <summary>Pilot: Starter catalog, plain terrain, Chakra/Suchi offers, no Brahmastra.</summary>
        public static MatchConfig Pilot(MatchMode mode = MatchMode.SharedPhone) =>
            new MatchConfig(mode, CatalogPreset.Starter, CardOfferRule.Pilot, TerrainTemplates.PlainId);

        /// <summary>V1 Starter room: five basic weapons, plain terrain, V1 card offers.</summary>
        public static MatchConfig V1Starter(MatchMode mode = MatchMode.Online) =>
            new MatchConfig(mode, CatalogPreset.Starter, CardOfferRule.V1, TerrainTemplates.PlainId);

        /// <summary>V1 Full room: all twenty weapons loaned, the mirrored five-terrain map, V1 card offers.</summary>
        public static MatchConfig V1Full(MatchMode mode = MatchMode.Online, bool brahmastraEnabled = false) =>
            new MatchConfig(mode, CatalogPreset.Full, CardOfferRule.V1, TerrainTemplates.FullId, brahmastraEnabled);

        /// <summary>Throws <see cref="RulesViolationException"/> for an illegal combination.</summary>
        public void Validate()
        {
            if (RulesVersion != RulesConstants.RulesVersion)
                throw new RulesViolationException("RULES_VERSION", "Unsupported rules version '" + RulesVersion + "'.");
            if (!Enum.IsDefined(typeof(MatchMode), Mode)) throw new RulesViolationException("CONFIG_MODE", "Unknown match mode.");
            if (!Enum.IsDefined(typeof(CatalogPreset), Catalog)) throw new RulesViolationException("CONFIG_CATALOG", "Unknown catalog.");
            if (!Enum.IsDefined(typeof(CardOfferRule), CardOffers)) throw new RulesViolationException("CONFIG_CARDS", "Unknown card rule.");
            if (TerrainTemplateId != TerrainTemplates.PlainId && TerrainTemplateId != TerrainTemplates.FullId)
                throw new RulesViolationException("CONFIG_TERRAIN", "Unknown terrain template '" + TerrainTemplateId + "'.");
            if (Catalog == CatalogPreset.Starter && TerrainTemplateId != TerrainTemplates.PlainId)
                throw new RulesViolationException("CONFIG_TERRAIN", "Starter rooms use the plain template.");
            if (CardOffers == CardOfferRule.Pilot && Catalog != CatalogPreset.Starter)
                throw new RulesViolationException("CONFIG_PILOT", "The pilot card offer is only used with the Starter catalog.");
            if (BrahmastraEnabled && (Catalog != CatalogPreset.Full || CardOffers == CardOfferRule.Pilot))
                throw new RulesViolationException("CONFIG_BRAHMASTRA", "Brahmastra may only be enabled in a Full V1 private room.");
        }

        public TerrainTemplate Template =>
            TerrainTemplateId == TerrainTemplates.FullId ? TerrainTemplates.FullMirrored : TerrainTemplates.PlainOnly;

        /// <summary>Choice deadline (the shared-phone value is per player, entered in sequence).</summary>
        public int ChoiceDeadlineMs => RulesConstants.ChoiceDeadlineMs;

        /// <summary>Combined card choice, pose and cut window.</summary>
        public int CutWindowMs => Mode == MatchMode.Online ? RulesConstants.OnlineCutWindowMs : RulesConstants.SharedPhoneCutWindowMs;

        /// <summary>Canonical encoding used by records and hashes.</summary>
        public void WriteTo(CanonicalWriter w) =>
            w.Ascii(RulesVersion).U8((int)Mode).U8((int)Catalog).U8((int)CardOffers).Ascii(TerrainTemplateId).Bool(BrahmastraEnabled);

        public override string ToString() =>
            RulesVersion + " " + Mode + " " + Catalog + " cards=" + CardOffers + " terrain=" + TerrainTemplateId +
            (BrahmastraEnabled ? " brahmastra" : string.Empty);
    }
}
