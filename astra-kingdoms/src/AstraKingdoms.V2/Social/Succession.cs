using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Social
{
    public enum SuccessionState : byte
    {
        /// <summary>Notice posted to the clan; waiting for the notice period.</summary>
        NoticePosted = 0,
        Approved = 1,
        Denied = 2,
        /// <summary>The leader became active again during review; nothing changes.</summary>
        CancelledLeaderReturned = 3,
    }

    public sealed class SuccessionCase
    {
        public string Id { get; }
        public string ClanId { get; }
        public string LeaderId { get; }
        public string OpenedBy { get; }
        public DateTimeOffset OpenedAt { get; }
        public DateTimeOffset NoticeEndsAt { get; }
        public SuccessionState State { get; internal set; }
        public string NewLeaderId { get; internal set; }
        public string Resolution { get; internal set; }

        public SuccessionCase(string id, string clanId, string leaderId, string openedBy, DateTimeOffset openedAt, DateTimeOffset noticeEndsAt)
        {
            Id = id;
            ClanId = clanId;
            LeaderId = leaderId;
            OpenedBy = openedBy;
            OpenedAt = openedAt;
            NoticeEndsAt = noticeEndsAt;
            State = SuccessionState.NoticePosted;
        }
    }

    public enum SuccessionResult : byte
    {
        Ok = 0,
        /// <summary>The leader has been active within the inactivity period.</summary>
        LeaderStillActive = 1,
        NoticePeriodRunning = 2,
        NotFound = 3,
        InvalidCandidate = 4,
        CaseClosed = 5,
        CaseAlreadyOpen = 6,
        /// <summary>Support actions need an agent id and a written reason for the audit record.</summary>
        MissingAccountability = 7,
    }

    /// <summary>
    /// Leader succession after thirty days of inactivity (plan: "support can review succession with
    /// notice and an audit record; there is no silent automated takeover"). Every step is an explicit
    /// support action, posts a notice to the clan feed and writes the audit log. Nothing here runs on a
    /// timer: if support never acts, the leader stays leader indefinitely.
    /// </summary>
    public sealed class SuccessionService
    {
        private readonly object _gate = new object();
        private readonly ClanService _clans;
        private readonly ClanChatService _feed;
        private readonly IClock _clock;
        private readonly Dictionary<string, SuccessionCase> _cases = new Dictionary<string, SuccessionCase>(StringComparer.Ordinal);
        private int _counter;

        public SuccessionService(ClanService clans, ClanChatService feed, IClock clock)
        {
            _clans = clans ?? throw new ArgumentNullException(nameof(clans));
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>True when the clan's leader has been inactive for at least the configured period.</summary>
        public bool LeaderInactive(string clanId)
        {
            Clan clan = _clans.Get(clanId);
            if (clan == null) return false;
            DateTimeOffset? last = _clans.Activity.LastActive(clan.LeaderId);
            DateTimeOffset since = last ?? clan.CreatedAt;
            return _clock.UtcNow - since >= _clans.Rules.LeaderInactivity;
        }

        public SuccessionResult Open(string supportAgentId, string clanId, string reason, out SuccessionCase opened)
        {
            opened = null;
            if (string.IsNullOrWhiteSpace(supportAgentId) || string.IsNullOrWhiteSpace(reason)) return SuccessionResult.MissingAccountability;
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                Clan clan = _clans.Get(clanId);
                if (clan == null) return SuccessionResult.NotFound;
                if (_cases.Values.Any(c => c.ClanId == clanId && c.State == SuccessionState.NoticePosted)) return SuccessionResult.CaseAlreadyOpen;
                if (!LeaderInactive(clanId)) return SuccessionResult.LeaderStillActive;
                opened = new SuccessionCase("sc-" + (++_counter).ToString(CultureInfo.InvariantCulture), clanId, clan.LeaderId, supportAgentId, now,
                    now + _clans.Rules.SuccessionNotice);
                _cases[opened.Id] = opened;
                _feed.PostSystemNotice(clanId, "clan.notice.successionOpened");
                _clans.Audit.Write(now, "support:" + supportAgentId, "succession.open", clanId, opened.Id + " leader=" + clan.LeaderId + " reason=" + reason);
                return SuccessionResult.Ok;
            }
        }

        /// <summary>
        /// Support decides after the notice period. If the leader was active after the case opened, the
        /// case is cancelled instead. The candidate must be a current member (officers first by policy).
        /// </summary>
        public SuccessionResult Approve(string supportAgentId, string caseId, string newLeaderId, string reason)
        {
            if (string.IsNullOrWhiteSpace(supportAgentId) || string.IsNullOrWhiteSpace(reason)) return SuccessionResult.MissingAccountability;
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                if (!_cases.TryGetValue(caseId, out SuccessionCase c)) return SuccessionResult.NotFound;
                if (c.State != SuccessionState.NoticePosted) return SuccessionResult.CaseClosed;
                if (CancelIfLeaderReturned(c, now)) return SuccessionResult.LeaderStillActive;
                if (now < c.NoticeEndsAt) return SuccessionResult.NoticePeriodRunning;
                Clan clan = _clans.Get(c.ClanId);
                if (clan == null) return SuccessionResult.NotFound;
                if (clan.LeaderId != c.LeaderId)
                {
                    c.State = SuccessionState.CancelledLeaderReturned;
                    c.Resolution = "leadership changed by the leader";
                    return SuccessionResult.CaseClosed;
                }
                if (newLeaderId == c.LeaderId || clan.Member(newLeaderId) == null) return SuccessionResult.InvalidCandidate;
                _clans.ApplySuccession(c.ClanId, newLeaderId);
                c.State = SuccessionState.Approved;
                c.NewLeaderId = newLeaderId;
                c.Resolution = reason;
                _feed.PostSystemNotice(c.ClanId, "clan.notice.successionApproved");
                _clans.Audit.Write(now, "support:" + supportAgentId, "succession.approve", c.ClanId, c.Id + " new=" + newLeaderId + " reason=" + reason);
                return SuccessionResult.Ok;
            }
        }

        public SuccessionResult Deny(string supportAgentId, string caseId, string reason)
        {
            if (string.IsNullOrWhiteSpace(supportAgentId) || string.IsNullOrWhiteSpace(reason)) return SuccessionResult.MissingAccountability;
            lock (_gate)
            {
                if (!_cases.TryGetValue(caseId, out SuccessionCase c)) return SuccessionResult.NotFound;
                if (c.State != SuccessionState.NoticePosted) return SuccessionResult.CaseClosed;
                c.State = SuccessionState.Denied;
                c.Resolution = reason;
                _feed.PostSystemNotice(c.ClanId, "clan.notice.successionClosed");
                _clans.Audit.Write(_clock.UtcNow, "support:" + supportAgentId, "succession.deny", c.ClanId, c.Id + " reason=" + reason);
                return SuccessionResult.Ok;
            }
        }

        public SuccessionCase Get(string caseId)
        {
            lock (_gate) return _cases.TryGetValue(caseId, out SuccessionCase c) ? c : null;
        }

        private bool CancelIfLeaderReturned(SuccessionCase c, DateTimeOffset now)
        {
            DateTimeOffset? last = _clans.Activity.LastActive(c.LeaderId);
            if (last == null || last.Value <= c.OpenedAt) return false;
            c.State = SuccessionState.CancelledLeaderReturned;
            c.Resolution = "leader active again";
            _feed.PostSystemNotice(c.ClanId, "clan.notice.successionClosed");
            _clans.Audit.Write(now, "system", "succession.cancel", c.ClanId, c.Id + " leader returned");
            return true;
        }
    }
}
