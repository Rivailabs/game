using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Match
{
    /// <summary>What a shared screen may show about one player's volley choice right now.</summary>
    public enum ChoiceVisibility : byte
    {
        /// <summary>Nothing at all (an opaque privacy screen is up).</summary>
        Hidden = 0,
        /// <summary>Only whether the player has locked ("Choosing" / "Ready"); never the choice.</summary>
        ReadyFlagOnly = 1,
        /// <summary>The player's own controls, editable (only during their private entry).</summary>
        OwnEditable = 2,
        /// <summary>The player's own locked choice, read-only (still private).</summary>
        OwnLocked = 3,
        /// <summary>Common reveal: weapon, element, aim and dodge.</summary>
        Revealed = 4,
        /// <summary>Common reveal with Mist Veil: outcome and dodge, but weapon, element and controls withheld.</summary>
        RevealedVeiled = 5,
    }

    /// <summary>
    /// Selection, lock and reveal presentation rule (ticket 30): secret information appears only in
    /// the permitted phase. Before the common reveal a player's choice is visible only to that
    /// player during their own private entry (or, against a bot, to the single human at any time);
    /// everyone else sees at most a ready flag. From the resolution replay on, choices are revealed,
    /// except that Mist Veil withholds weapon details from a human opponent.
    /// </summary>
    public static class RevealPolicy
    {
        /// <param name="owner">Whose choice is being displayed.</param>
        /// <param name="stage">Host stage.</param>
        /// <param name="stageSide">The side the stage concerns (entrant, handover target), if any.</param>
        /// <param name="ownerSeat">Who controls the owner's seat.</param>
        /// <param name="otherSeat">Who controls the other seat.</param>
        /// <param name="ownerLocked">The owner has locked this volley.</param>
        /// <param name="veiledFromViewer">The resolved choice is veiled from a human watching this screen.</param>
        public static ChoiceVisibility For(PlayerSide owner, HostStage stage, PlayerSide? stageSide, SeatKind ownerSeat, SeatKind otherSeat,
            bool ownerLocked, bool veiledFromViewer = false)
        {
            switch (stage)
            {
                case HostStage.EntryReady:
                case HostStage.Handover:
                case HostStage.LoadoutReady:
                    return ChoiceVisibility.Hidden;
                case HostStage.Entry:
                case HostStage.LoadoutEntry:
                    bool alone = otherSeat == SeatKind.Bot && ownerSeat == SeatKind.Human;
                    bool ownTurn = stageSide == owner && ownerSeat == SeatKind.Human;
                    if (ownTurn || alone) return ownerLocked ? ChoiceVisibility.OwnLocked : ChoiceVisibility.OwnEditable;
                    return ChoiceVisibility.ReadyFlagOnly;
                case HostStage.Resolution:
                case HostStage.CardAndCut:
                case HostStage.TerrainAnnounce:
                case HostStage.MatchOver:
                    // Resolved history is public (subject to Mist Veil); an open volley never reaches these stages.
                    return veiledFromViewer ? ChoiceVisibility.RevealedVeiled : ChoiceVisibility.Revealed;
                default:
                    return ChoiceVisibility.Hidden;
            }
        }

        /// <summary>True when the visibility exposes any part of the choice (weapon, aim, power or dodge).</summary>
        public static bool ExposesChoice(ChoiceVisibility v) =>
            v == ChoiceVisibility.OwnEditable || v == ChoiceVisibility.OwnLocked || v == ChoiceVisibility.Revealed || v == ChoiceVisibility.RevealedVeiled;
    }
}
