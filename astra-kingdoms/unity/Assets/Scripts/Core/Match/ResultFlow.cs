using System;
using AstraKingdoms.Client.Land;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Client.Match
{
    /// <summary>Localization key plus arguments (formatted by the screen; never concatenated).</summary>
    public readonly struct TextRef
    {
        public readonly string Key;
        public readonly object[] Args;

        public TextRef(string key, params object[] args)
        {
            Key = key;
            Args = args ?? Array.Empty<object>();
        }
    }

    /// <summary>What the result screen shows (ticket 42), derived only from the engine's <see cref="MatchResult"/>.</summary>
    public sealed class ResultSummary
    {
        public TextRef Title { get; private set; }
        public TextRef Reason { get; private set; }
        public TextRef Rounds { get; private set; }
        public LandTotals Totals { get; private set; }
        public PlayerSide? Winner { get; private set; }
        public bool IsDraw { get; private set; }
        public bool IsVoid { get; private set; }

        public static ResultSummary From(MatchResult result, Func<PlayerSide, string> names)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (names == null) throw new ArgumentNullException(nameof(names));
            var s = new ResultSummary
            {
                Winner = result.Winner,
                IsDraw = result.IsDraw,
                IsVoid = result.IsVoid,
                Totals = LandTotals.From(result.CellsA, result.CellsB),
                Rounds = new TextRef("result.rounds", result.RoundsPlayed),
            };
            if (result.IsVoid) s.Title = new TextRef("result.void");
            else if (result.Winner.HasValue) s.Title = new TextRef("result.winner", names(result.Winner.Value));
            else s.Title = new TextRef("result.draw");
            s.Reason = result.Reason == TerminalReason.Forfeit && result.ForfeitedBy.HasValue
                ? new TextRef("reason.Forfeit", names(result.ForfeitedBy.Value))
                : new TextRef("reason." + result.Reason);
            return s;
        }
    }

    /// <summary>
    /// Rematch flow guard: a rematch creates fresh state exactly once per finished match, however
    /// many times the button is tapped (ticket 42: "rematch creates fresh state once").
    /// </summary>
    public sealed class RematchGuard
    {
        private string _finishedMatchId;
        private bool _consumed;

        /// <summary>Arms the guard for a newly finished match.</summary>
        public void MatchFinished(string matchId)
        {
            _finishedMatchId = matchId;
            _consumed = false;
        }

        /// <summary>True exactly once after <see cref="MatchFinished"/>; later taps are ignored.</summary>
        public bool TryRematch()
        {
            if (_finishedMatchId == null || _consumed) return false;
            _consumed = true;
            return true;
        }

        public bool Armed => _finishedMatchId != null && !_consumed;
    }
}
