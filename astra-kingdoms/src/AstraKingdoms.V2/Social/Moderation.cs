using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Social
{
    public enum ReportTarget : byte
    {
        Player = 0,
        ClanName = 1,
        ClanProfile = 2,
        Avatar = 3,
        SharedClip = 4,
    }

    public enum ReportReason : byte
    {
        Harassment = 0,
        InappropriateName = 1,
        InappropriateImage = 2,
        Cheating = 3,
        Spam = 4,
        Impersonation = 5,
        ChildSafety = 6,
        Other = 7,
    }

    public enum CaseState : byte
    {
        Open = 0,
        Actioned = 1,
        Dismissed = 2,
    }

    public sealed class ModerationCase
    {
        public string Id { get; }
        public string ReporterId { get; }
        public ReportTarget TargetKind { get; }
        public string TargetId { get; }
        /// <summary>The account responsible for the target (player, clan leader at report time, avatar owner).</summary>
        public string SubjectPlayerId { get; }
        public ReportReason Reason { get; }
        public DateTimeOffset At { get; }
        public int Priority { get; }
        public CaseState State { get; internal set; }
        public string Resolution { get; internal set; }

        public ModerationCase(string id, string reporter, ReportTarget kind, string targetId, string subject, ReportReason reason, DateTimeOffset at, int priority)
        {
            Id = id;
            ReporterId = reporter;
            TargetKind = kind;
            TargetId = targetId;
            SubjectPlayerId = subject;
            Reason = reason;
            At = at;
            Priority = priority;
            State = CaseState.Open;
        }
    }

    public sealed class FeatureSuspension
    {
        public string Id { get; }
        public string PlayerId { get; }
        public SocialFeature Feature { get; }
        public DateTimeOffset From { get; }
        public DateTimeOffset Until { get; internal set; }
        public string CaseId { get; }
        public bool Lifted { get; internal set; }

        public FeatureSuspension(string id, string playerId, SocialFeature feature, DateTimeOffset from, DateTimeOffset until, string caseId)
        {
            Id = id;
            PlayerId = playerId;
            Feature = feature;
            From = from;
            Until = until;
            CaseId = caseId;
        }

        public bool ActiveAt(DateTimeOffset now) => !Lifted && now >= From && now < Until;
    }

    public enum AppealState : byte
    {
        Pending = 0,
        Upheld = 1,
        Overturned = 2,
    }

    public sealed class Appeal
    {
        public string Id { get; }
        public string SuspensionId { get; }
        public string PlayerId { get; }
        public string StatementPresetId { get; }
        public DateTimeOffset At { get; }
        public AppealState State { get; internal set; }

        public Appeal(string id, string suspensionId, string playerId, string statementPresetId, DateTimeOffset at)
        {
            Id = id;
            SuspensionId = suspensionId;
            PlayerId = playerId;
            StatementPresetId = statementPresetId;
            At = at;
        }
    }

    public enum ReportResult : byte
    {
        Received = 0,
        /// <summary>The reporter already has an open report on this target; the existing case stands.</summary>
        AlreadyReported = 1,
        RateLimited = 2,
        Suspended = 3,
        Invalid = 4,
    }

    public enum AppealResult : byte
    {
        Received = 0,
        NotFound = 1,
        AlreadyAppealed = 2,
        WindowClosed = 3,
        NotActive = 4,
    }

    /// <summary>Who answers reports and appeals (plan: "A reachable operator owns reports, appeals and temporary feature suspension").</summary>
    public sealed class SocialOperations
    {
        public string SupportOwner { get; set; }
        public string SupportContact { get; set; }
        /// <summary>Link to the documented response process (targets, escalation, child-safety route).</summary>
        public string ResponseProcessUrl { get; set; }

        /// <summary>Social features stay off until every field is set (V2 social package prerequisite).</summary>
        public IReadOnlyList<string> MissingForLaunch()
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(SupportOwner)) missing.Add("support owner");
            if (string.IsNullOrWhiteSpace(SupportContact)) missing.Add("support contact");
            if (string.IsNullOrWhiteSpace(ResponseProcessUrl)) missing.Add("documented response process");
            return missing;
        }

        public bool SocialFeaturesMayLaunch => MissingForLaunch().Count == 0;
    }

    /// <summary>
    /// Reports, the moderation queue, temporary feature suspensions and appeals. Moderation actions
    /// are always temporary feature suspensions or content resets (names, avatars): this service never
    /// deletes an account, removes purchases or touches match results. It implements
    /// <see cref="IFeatureGate"/>, which every social service consults.
    /// </summary>
    public sealed class ModerationService : IFeatureGate
    {
        private readonly object _gate = new object();
        private readonly IClock _clock;
        private readonly AuditLog _audit;
        private readonly List<ModerationCase> _cases = new List<ModerationCase>();
        private readonly List<FeatureSuspension> _suspensions = new List<FeatureSuspension>();
        private readonly List<Appeal> _appeals = new List<Appeal>();
        private int _caseCounter, _suspensionCounter, _appealCounter;

        /// <summary>PROPOSED: reports per reporter per day.</summary>
        public int MaxReportsPerDay { get; set; } = 20;
        /// <summary>PROPOSED: longest temporary suspension a moderator can apply in one action.</summary>
        public TimeSpan MaxSuspension { get; set; } = TimeSpan.FromDays(30);
        /// <summary>PROPOSED: how long after a suspension starts an appeal may be filed.</summary>
        public TimeSpan AppealWindow { get; set; } = TimeSpan.FromDays(14);

        /// <summary>Raised when an avatar is removed by moderation (wired to the avatar service).</summary>
        public event Action<string, string> AvatarRemoved;
        /// <summary>Raised when a clan name must be reset (wired to the clan service).</summary>
        public event Action<string, string, string> ClanNameReset;

        public ModerationService(IClock clock, AuditLog audit = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _audit = audit ?? new AuditLog();
        }

        public AuditLog Audit => _audit;

        public static int PriorityFor(ReportReason reason, ReportTarget target) =>
            reason == ReportReason.ChildSafety ? 0 : target == ReportTarget.Avatar || reason == ReportReason.InappropriateImage ? 1 : reason == ReportReason.Harassment ? 2 : 3;

        public ReportResult Report(string reporterId, ReportTarget kind, string targetId, string subjectPlayerId, ReportReason reason, out ModerationCase created)
        {
            created = null;
            DateTimeOffset now = _clock.UtcNow;
            if (string.IsNullOrEmpty(reporterId) || string.IsNullOrEmpty(targetId) || reporterId == subjectPlayerId) return ReportResult.Invalid;
            if (IsSuspended(reporterId, SocialFeature.Reporting, now)) return ReportResult.Suspended;
            lock (_gate)
            {
                if (_cases.Any(c => c.ReporterId == reporterId && c.TargetKind == kind && c.TargetId == targetId && c.State == CaseState.Open))
                    return ReportResult.AlreadyReported;
                if (_cases.Count(c => c.ReporterId == reporterId && now - c.At < TimeSpan.FromDays(1)) >= MaxReportsPerDay) return ReportResult.RateLimited;
                created = new ModerationCase("mc-" + (++_caseCounter).ToString(CultureInfo.InvariantCulture), reporterId, kind, targetId, subjectPlayerId, reason, now,
                    PriorityFor(reason, kind));
                _cases.Add(created);
                _audit.Write(now, reporterId, "report", created.Id, kind + ":" + targetId + " " + reason);
                return ReportResult.Received;
            }
        }

        /// <summary>Open cases, most urgent first (child safety, images, harassment, other), then oldest first.</summary>
        public IReadOnlyList<ModerationCase> Queue()
        {
            lock (_gate) return _cases.Where(c => c.State == CaseState.Open).OrderBy(c => c.Priority).ThenBy(c => c.At).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
        }

        public bool Dismiss(string moderatorId, string caseId, string note)
        {
            lock (_gate)
            {
                ModerationCase c = _cases.FirstOrDefault(x => x.Id == caseId && x.State == CaseState.Open);
                if (c == null || string.IsNullOrWhiteSpace(moderatorId)) return false;
                c.State = CaseState.Dismissed;
                c.Resolution = note;
                _audit.Write(_clock.UtcNow, "moderator:" + moderatorId, "case.dismiss", caseId, note);
                return true;
            }
        }

        /// <summary>Temporarily suspends one feature for the case's subject. Durations are capped.</summary>
        public FeatureSuspension Suspend(string moderatorId, string caseId, SocialFeature feature, TimeSpan duration)
        {
            if (string.IsNullOrWhiteSpace(moderatorId) || duration <= TimeSpan.Zero) return null;
            if (duration > MaxSuspension) duration = MaxSuspension;
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                ModerationCase c = _cases.FirstOrDefault(x => x.Id == caseId);
                if (c == null || c.SubjectPlayerId == null) return null;
                var s = new FeatureSuspension("fs-" + (++_suspensionCounter).ToString(CultureInfo.InvariantCulture), c.SubjectPlayerId, feature, now, now + duration, caseId);
                _suspensions.Add(s);
                c.State = CaseState.Actioned;
                c.Resolution = "suspended " + feature + " for " + duration;
                _audit.Write(now, "moderator:" + moderatorId, "suspend", c.SubjectPlayerId, feature + " until " + s.Until.ToString("u", CultureInfo.InvariantCulture) + " case " + caseId);
                return s;
            }
        }

        /// <summary>Resets reported content (an avatar back to stock, a clan name to a placeholder) and closes the case.</summary>
        public bool ResetContent(string moderatorId, string caseId)
        {
            ModerationCase c;
            lock (_gate)
            {
                c = _cases.FirstOrDefault(x => x.Id == caseId && x.State == CaseState.Open);
                if (c == null || string.IsNullOrWhiteSpace(moderatorId)) return false;
                if (c.TargetKind != ReportTarget.Avatar && c.TargetKind != ReportTarget.ClanName && c.TargetKind != ReportTarget.ClanProfile) return false;
                c.State = CaseState.Actioned;
                c.Resolution = "content reset";
                _audit.Write(_clock.UtcNow, "moderator:" + moderatorId, "content.reset", caseId, c.TargetKind + ":" + c.TargetId);
            }
            if (c.TargetKind == ReportTarget.Avatar) AvatarRemoved?.Invoke(c.SubjectPlayerId, caseId);
            else ClanNameReset?.Invoke(c.TargetId, moderatorId, caseId);
            return true;
        }

        public bool IsSuspended(string playerId, SocialFeature feature, DateTimeOffset now)
        {
            lock (_gate) return _suspensions.Any(s => s.PlayerId == playerId && s.Feature == feature && s.ActiveAt(now));
        }

        public IReadOnlyList<FeatureSuspension> SuspensionsOf(string playerId)
        {
            lock (_gate) return _suspensions.Where(s => s.PlayerId == playerId).ToArray();
        }

        /// <summary>One appeal per suspension, with a preset statement, within the appeal window.</summary>
        public AppealResult FileAppeal(string playerId, string suspensionId, string statementPresetId, out Appeal appeal)
        {
            appeal = null;
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                FeatureSuspension s = _suspensions.FirstOrDefault(x => x.Id == suspensionId && x.PlayerId == playerId);
                if (s == null) return AppealResult.NotFound;
                if (_appeals.Any(a => a.SuspensionId == suspensionId)) return AppealResult.AlreadyAppealed;
                if (now - s.From > AppealWindow) return AppealResult.WindowClosed;
                if (!s.ActiveAt(now)) return AppealResult.NotActive;
                appeal = new Appeal("ap-" + (++_appealCounter).ToString(CultureInfo.InvariantCulture), suspensionId, playerId, statementPresetId, now);
                _appeals.Add(appeal);
                _audit.Write(now, playerId, "appeal", suspensionId, statementPresetId);
                return AppealResult.Received;
            }
        }

        public IReadOnlyList<Appeal> PendingAppeals()
        {
            lock (_gate) return _appeals.Where(a => a.State == AppealState.Pending).OrderBy(a => a.At).ToArray();
        }

        /// <summary>A different moderator reviews the appeal; overturning lifts the suspension at once.</summary>
        public bool ResolveAppeal(string moderatorId, string appealId, bool overturn, string note)
        {
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                Appeal a = _appeals.FirstOrDefault(x => x.Id == appealId && x.State == AppealState.Pending);
                if (a == null || string.IsNullOrWhiteSpace(moderatorId)) return false;
                FeatureSuspension s = _suspensions.First(x => x.Id == a.SuspensionId);
                string suspendedBy = _audit.Entries.Where(e => e.Action == "suspend" && e.Detail.EndsWith("case " + s.CaseId, StringComparison.Ordinal))
                    .Select(e => e.Actor).FirstOrDefault();
                if (suspendedBy == "moderator:" + moderatorId) return false; // the same moderator may not review their own action
                a.State = overturn ? AppealState.Overturned : AppealState.Upheld;
                if (overturn)
                {
                    s.Lifted = true;
                    s.Until = now;
                }
                _audit.Write(now, "moderator:" + moderatorId, overturn ? "appeal.overturn" : "appeal.uphold", a.SuspensionId, note);
                return true;
            }
        }
    }
}
