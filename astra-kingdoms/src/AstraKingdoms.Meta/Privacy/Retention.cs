using System;
using System.Collections.Generic;
using System.Linq;

namespace AstraKingdoms.Meta.Privacy
{
    /// <summary>Kinds of game data with a retention rule (see docs/privacy-data-map.md).</summary>
    public enum DataCategory : byte
    {
        AccountProfile = 0,
        ProgressionLedger = 1,
        DailyTaskProgress = 2,
        CosmeticEquipment = 3,
        AdOfferTicket = 4,
        PurchaseRecord = 5,
        MatchDiagnosticRecord = 6,
        RawProductAnalytics = 7,
        AggregateDecisionReport = 8,
        CrashReport = 9,
        LocalPilotFeedback = 10,
        SupportCase = 11,
    }

    public sealed class RetentionRule
    {
        public DataCategory Category { get; }
        /// <summary>Maximum age; null = kept while the account exists (deleted with it) or until an owner decision.</summary>
        public TimeSpan? MaxAge { get; }
        /// <summary>A specific support case may hold a record past <see cref="MaxAge"/>.</summary>
        public bool SupportHoldAllowed { get; }
        /// <summary>True when the period is still an open owner/legal decision.</summary>
        public bool NeedsDecision { get; }
        public string Handling { get; }

        public RetentionRule(DataCategory category, TimeSpan? maxAge, bool supportHold, bool needsDecision, string handling)
        {
            Category = category;
            MaxAge = maxAge;
            SupportHoldAllowed = supportHold;
            NeedsDecision = needsDecision;
            Handling = handling;
        }
    }

    /// <summary>
    /// Proposed operational defaults from the plan ("Data and artifact retention defaults") plus the
    /// meta-game stores. Not statutory periods; they require review.
    /// </summary>
    public static class RetentionDefaults
    {
        public static readonly IReadOnlyList<RetentionRule> Rules = new[]
        {
            new RetentionRule(DataCategory.AccountProfile, null, false, false, "Deleted with the account."),
            new RetentionRule(DataCategory.ProgressionLedger, null, false, false, "Deleted with the account."),
            new RetentionRule(DataCategory.DailyTaskProgress, TimeSpan.FromDays(30), false, false, "Only today's row is needed; older rows are swept."),
            new RetentionRule(DataCategory.CosmeticEquipment, null, false, false, "Deleted with the account."),
            new RetentionRule(DataCategory.AdOfferTicket, TimeSpan.FromDays(30), true, false, "Kept for reward disputes, then swept."),
            new RetentionRule(DataCategory.PurchaseRecord, null, true, true,
                "Period set with accounting, tax and fraud requirements; only justified fields; pseudonymised on account deletion and disclosed."),
            new RetentionRule(DataCategory.MatchDiagnosticRecord, TimeSpan.FromDays(30), true, false, "Access restricted; replay sharing is a separate V2 purpose."),
            new RetentionRule(DataCategory.RawProductAnalytics, TimeSpan.FromDays(90), false, false, "Only when permitted; aggregate reports live longer."),
            new RetentionRule(DataCategory.AggregateDecisionReport, null, false, false, "Aggregates without identifiers; kept for decision records."),
            new RetentionRule(DataCategory.CrashReport, TimeSpan.FromDays(90), true, false, "Only with crash-report consent."),
            new RetentionRule(DataCategory.LocalPilotFeedback, null, false, true, "Through the pilot decision and one follow-up cycle; no unnecessary identifiers."),
            new RetentionRule(DataCategory.SupportCase, TimeSpan.FromDays(365), true, true, "Grievance outcome records (India Online Gaming Rules, rule 20); period needs legal review."),
        };

        public static RetentionRule For(DataCategory c) => Rules.First(r => r.Category == c);
    }

    /// <summary>Selects records past their retention period (the server's nightly sweep deletes them).</summary>
    public static class RetentionSweeper
    {
        public static IReadOnlyList<T> Expired<T>(IEnumerable<T> records, DataCategory category, Func<T, DateTimeOffset> createdAt,
            Func<T, bool> onSupportHold, DateTimeOffset now)
        {
            RetentionRule rule = RetentionDefaults.For(category);
            if (!rule.MaxAge.HasValue) return Array.Empty<T>();
            return records.Where(r => now - createdAt(r) >= rule.MaxAge.Value)
                .Where(r => !(rule.SupportHoldAllowed && onSupportHold != null && onSupportHold(r)))
                .ToArray();
        }
    }
}
