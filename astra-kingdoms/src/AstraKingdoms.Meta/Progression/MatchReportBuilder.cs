using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Reporting;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Meta.Progression
{
    /// <summary>
    /// Builds meta inputs from an authoritative rules record, so the local host (offline guest play)
    /// and the online match service (server) derive grants, record summaries and analytics tokens the
    /// same way.
    /// <para>Server integration: on a terminal result call <see cref="FromRecord"/> once per human seat
    /// and pass the report to ProgressionService.GrantForMatch and DailyTaskService.RecordMatch; store
    /// <see cref="Summary"/> for completion reporting and reconciliation.</para>
    /// </summary>
    public static class MatchReportBuilder
    {
        /// <summary>Distinct regular weapons the side locked in (Pass and Brahmastra are not weapons for mastery).</summary>
        public static IReadOnlyList<int> WeaponsUsed(MatchRecord record, PlayerSide side)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return record.Commands
                .Where(c => c.Sender == side && c.Command is LockInputCommand)
                .Select(c => ((LockInputCommand)c.Command).WeaponId)
                .Where(WeaponCatalog.IsRegularId)
                .Distinct().OrderBy(w => w).ToArray();
        }

        public static MatchOutcomeReport FromRecord(MatchRecord record, string playerId, PlayerSide side, MatchKind kind, DateTimeOffset completedAt,
            bool attributeOutcome = true, bool isAutomation = false, bool isDeveloperTest = false, bool isValid = true)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Result == null) throw new ArgumentException("The match has not finished.", nameof(record));
            PlayerOutcome outcome = attributeOutcome ? MatchOutcomeReport.OutcomeFrom(record.Result, side) : PlayerOutcome.Unattributed;
            return new MatchOutcomeReport(record.MatchId, playerId, kind, outcome, MatchOutcomeReport.EndingFrom(record.Result), completedAt,
                WeaponsUsed(record, side), record.Config?.Catalog ?? CatalogPreset.Starter, isAutomation, isDeveloperTest, isValid);
        }

        public static MatchRecordSummary Summary(MatchRecord record, MatchKind kind, int humanSeats, DateTimeOffset startedAt, bool isAutomation = false, bool isInternal = false)
        {
            if (record?.Result == null) throw new ArgumentException("finished record required", nameof(record));
            return new MatchRecordSummary(record.MatchId, kind, MatchOutcomeReport.EndingFrom(record.Result), humanSeats, record.Result.RoundsPlayed,
                startedAt, isAutomation, isInternal);
        }
    }
}

namespace AstraKingdoms.Meta.Analytics
{
    /// <summary>Typed builders for the event set, so clients and the server emit identical tokens.</summary>
    public static class AnalyticsEvents
    {
        public static string ResultToken(PlayerOutcome o)
        {
            switch (o)
            {
                case PlayerOutcome.Win: return "win";
                case PlayerOutcome.Draw: return "draw";
                case PlayerOutcome.Loss: return "loss";
                default: return "unattributed";
            }
        }

        public static Dictionary<string, object> MatchStart(string matchId, MatchKind kind, CatalogPreset catalog) =>
            AnalyticsClient.Props("match_id", matchId, "mode", EventReconciler.ModeToken(kind), "catalog", catalog == CatalogPreset.Full ? "full" : "starter");

        public static Dictionary<string, object> MatchEnd(MatchOutcomeReport r, int rounds) =>
            AnalyticsClient.Props("match_id", r.MatchResultId, "mode", EventReconciler.ModeToken(r.Kind), "reason", EventReconciler.ReasonToken(r.Ending),
                "result", ResultToken(r.Outcome), "rounds", (long)rounds);

        public static Dictionary<string, object> ValidSession(string appVersion, bool development) =>
            AnalyticsClient.Props("foreground", true, "app_version", appVersion, "build", development ? "development" : "release");

        public static Dictionary<string, object> AdRewardState(string placement, string state) =>
            AnalyticsClient.Props("placement", placement, "state", state);

        public static Dictionary<string, object> PurchaseState(string sku, string state, bool test = false) =>
            AnalyticsClient.Props("sku", sku, "state", state, "test_purchase", test);
    }
}
