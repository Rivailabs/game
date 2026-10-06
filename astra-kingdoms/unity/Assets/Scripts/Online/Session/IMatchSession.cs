using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Client.Online
{
    /// <summary>
    /// The view-driven surface screens need from a match, whoever runs it: the shared-phone
    /// <see cref="LocalMatchHost"/> (through <see cref="LocalMatchSession"/>) or the authoritative
    /// online service (through <see cref="OnlineMatchSession"/>). Screens receive only the entitled
    /// player's <see cref="PlayerView"/> or the secret-free <see cref="PublicSnapshot"/>, exactly as in
    /// the shared-phone flow, so they can be reused unchanged.
    /// <para>
    /// Submissions return nothing: online results arrive asynchronously. Accepted actions show up as
    /// state changes (<see cref="StageChanged"/>, <see cref="CutApplied"/>); rejections raise
    /// <see cref="CommandRejected"/> with the engine's stable code.
    /// </para>
    /// </summary>
    public interface IMatchSession
    {
        MatchConfig Config { get; }
        HostStage Stage { get; }
        /// <summary>The side the stage concerns (online: the local player during entry, the duel winner during the cut).</summary>
        PlayerSide? StageSide { get; }
        double StageSecondsRemaining { get; }
        bool PauseAllowed { get; }
        /// <summary>True when a person on this device acts for <paramref name="side"/>.</summary>
        bool IsLocalHuman(PlayerSide side);
        /// <summary>True when the screen may show <paramref name="side"/>'s private view right now.</summary>
        bool CanView(PlayerSide side);
        /// <summary>The private view of <paramref name="side"/>; throws when <see cref="CanView"/> is false.</summary>
        PlayerView ViewFor(PlayerSide side);
        PublicSnapshot Snapshot();
        Territory CloneTerritory();
        MatchResult Result { get; }

        CutResult PreviewCut(PlayerSide side, CardId card, CardPose pose, int anchorCellId, CutMode mode, IReadOnlyList<CellPoint> vertices);
        void SubmitLoadout(PlayerSide side, IReadOnlyList<int> weapons, int reserve);
        void SubmitLock(PlayerSide side, VolleyInput choice);
        void SubmitCut(PlayerSide side, CardId card, CardPose pose, int anchorCellId, CutMode mode, IReadOnlyList<CellPoint> vertices);

        /// <summary>Advances local countdowns by real seconds (online: display only; the server owns deadlines).</summary>
        void Tick(double seconds);

        event Action<HostStage> StageChanged;
        /// <summary>A volley resolved; the argument is the volley as the local viewer may see it (Mist Veil applied).</summary>
        event Action<RevealedVolley> VolleyRevealed;
        /// <summary>Cells moved by an accepted cut and the cutter.</summary>
        event Action<PlayerSide, int> CutApplied;
        event Action<MatchResult> MatchEnded;
        /// <summary>A command was rejected; the argument is the stable reject code.</summary>
        event Action<string> CommandRejected;
    }
}
