using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;

namespace AstraKingdoms.Meta.Tests;

internal static class T0
{
    /// <summary>2026-10-06 12:00 IST (06:30 UTC): mid-day in the default task-day offset.</summary>
    public static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 6, 6, 30, 0, TimeSpan.Zero);

    public static ManualClock Clock() => new ManualClock(Noon);

    public static MatchOutcomeReport Human(string id, string player, PlayerOutcome outcome, DateTimeOffset at,
        MatchEnding ending = MatchEnding.RoundsComplete, params int[] weapons) =>
        new MatchOutcomeReport(id, player, MatchKind.OnlineHuman, outcome, ending, at, weapons);

    public static MatchOutcomeReport Practice(string id, string player, DateTimeOffset at, params int[] weapons) =>
        new MatchOutcomeReport(id, player, MatchKind.Practice, PlayerOutcome.Win, MatchEnding.RoundsComplete, at, weapons);
}
