using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Social
{
    /// <summary>Versioned clan constants (plan: PROPOSED clans hold twenty members, one leader, up to two officers).</summary>
    public sealed class ClanRules
    {
        public const string CurrentVersion = "AK-CLAN-1";

        public string Version { get; } = CurrentVersion;
        public int MaxMembers { get; set; } = 20;
        public int MaxOfficers { get; set; } = 2;
        public TimeSpan InviteTtl { get; set; } = TimeSpan.FromDays(7);
        /// <summary>Leader inactivity after which support may review succession (plan: thirty days).</summary>
        public TimeSpan LeaderInactivity { get; set; } = TimeSpan.FromDays(30);
        /// <summary>How long the succession notice is posted to the clan before support may decide. PROPOSED.</summary>
        public TimeSpan SuccessionNotice { get; set; } = TimeSpan.FromDays(7);
        public int NameMinLength { get; set; } = 3;
        public int NameMaxLength { get; set; } = 20;

        public static readonly ClanRules Default = new ClanRules();
    }

    public enum ClanRole : byte
    {
        Member = 0,
        Officer = 1,
        Leader = 2,
    }

    public sealed class ClanMember
    {
        public string PlayerId { get; }
        public ClanRole Role { get; internal set; }
        public DateTimeOffset JoinedAt { get; }

        public ClanMember(string playerId, ClanRole role, DateTimeOffset joinedAt)
        {
            PlayerId = playerId;
            Role = role;
            JoinedAt = joinedAt;
        }
    }

    /// <summary>
    /// A clan. Identity, a roster and cooperative cosmetic milestones only: there is no clan bank,
    /// shared inventory or any operation that moves one member's coins, cosmetics, decorations or
    /// entitlements to another.
    /// </summary>
    public sealed class Clan
    {
        private readonly List<ClanMember> _members = new List<ClanMember>();

        public string Id { get; }
        public string Name { get; internal set; }
        /// <summary>Preset profile line (localized key) - clan profiles have no free text.</summary>
        public string ProfilePresetId { get; internal set; }
        public string EmblemId { get; internal set; }
        public DateTimeOffset CreatedAt { get; }

        internal Clan(string id, string name, DateTimeOffset createdAt)
        {
            Id = id;
            Name = name;
            CreatedAt = createdAt;
            ProfilePresetId = ClanPresets.DefaultProfile;
            EmblemId = ClanPresets.DefaultEmblem;
        }

        public IReadOnlyList<ClanMember> Members => _members;
        internal List<ClanMember> MutableMembers => _members;
        public ClanMember Member(string playerId) => _members.FirstOrDefault(m => m.PlayerId == playerId);
        public string LeaderId => _members.First(m => m.Role == ClanRole.Leader).PlayerId;
        public int OfficerCount => _members.Count(m => m.Role == ClanRole.Officer);
    }

    public sealed class ClanInvitation
    {
        public string Id { get; }
        public string ClanId { get; }
        public string From { get; }
        public string To { get; }
        public DateTimeOffset ExpiresAt { get; }
        public InviteState State { get; internal set; }

        public ClanInvitation(string id, string clanId, string from, string to, DateTimeOffset expiresAt)
        {
            Id = id;
            ClanId = clanId;
            From = from;
            To = to;
            ExpiresAt = expiresAt;
            State = InviteState.Pending;
        }

        public bool IsOpen(DateTimeOffset now) => State == InviteState.Pending && now < ExpiresAt;
    }

    public sealed class ClanEvent
    {
        public string Id { get; }
        public string ClanId { get; }
        public string PresetEventId { get; }
        public DateTimeOffset StartsAt { get; }
        public string OrganisedBy { get; }

        public ClanEvent(string id, string clanId, string presetEventId, DateTimeOffset startsAt, string organisedBy)
        {
            Id = id;
            ClanId = clanId;
            PresetEventId = presetEventId;
            StartsAt = startsAt;
            OrganisedBy = organisedBy;
        }
    }

    public enum ClanResult : byte
    {
        Ok = 0,
        NotAllowed = 1,
        NotFound = 2,
        ClanFull = 3,
        AlreadyInClan = 4,
        OfficerLimit = 5,
        InvalidName = 6,
        NameTaken = 7,
        Expired = 8,
        Suspended = 9,
        /// <summary>The leader must hand over leadership before leaving a clan that still has members.</summary>
        LeaderMustTransfer = 10,
        InvalidPreset = 11,
        /// <summary>Returned to an inviter when a block exists; nothing is delivered (same text as Ok on the client).</summary>
        SilentlyDropped = 12,
    }

    /// <summary>When each player last did something (sign-in, match, clan action). Server: a column on the account.</summary>
    public sealed class ActivityTracker
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, DateTimeOffset> _last = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        public void Record(string playerId, DateTimeOffset at)
        {
            lock (_gate)
                if (!_last.TryGetValue(playerId, out DateTimeOffset prev) || at > prev) _last[playerId] = at;
        }

        public DateTimeOffset? LastActive(string playerId)
        {
            lock (_gate) return _last.TryGetValue(playerId, out DateTimeOffset t) ? t : (DateTimeOffset?)null;
        }

        public void Delete(string playerId)
        {
            lock (_gate) _last.Remove(playerId);
        }
    }

    /// <summary>Clan names are the one free-text field; they follow these rules and can be reported and reset by moderation.</summary>
    public static class ClanNamePolicy
    {
        /// <summary>Terms that would impersonate staff or the game (case-insensitive, also inside words).</summary>
        public static readonly IReadOnlyList<string> Reserved = new[] { "admin", "moderator", "support", "official", "astra team", "rivai" };

        public static string Validate(string name, ClanRules rules)
        {
            if (name == null) return "empty";
            string trimmed = name.Trim();
            var info = new StringInfo(trimmed);
            if (info.LengthInTextElements < rules.NameMinLength || info.LengthInTextElements > rules.NameMaxLength) return "length";
            if (trimmed.Contains("  ")) return "spacing";
            foreach (char c in trimmed)
            {
                UnicodeCategory cat = char.GetUnicodeCategory(c);
                bool ok = char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '\'' ||
                          cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark;
                if (!ok) return "characters";
            }
            string lower = trimmed.ToLowerInvariant();
            if (Reserved.Any(r => lower.Contains(r))) return "reserved";
            return null;
        }

        /// <summary>The neutral name moderation assigns when it resets a reported name.</summary>
        public static string Placeholder(string clanId) => "Clan " + clanId.Substring(Math.Max(0, clanId.Length - 4)).ToUpperInvariant();
    }

    /// <summary>
    /// Clans (plan: "Friends clans avatars and replay sharing"): twenty members, one leader, up to
    /// two officers. Officers invite and organise events; only the leader changes officer roles; no
    /// role can transfer another member's assets (there is no such operation). Leader inactivity is
    /// handled by <see cref="SuccessionService"/> through support review, never automatically.
    /// </summary>
    public sealed class ClanService
    {
        private readonly object _gate = new object();
        private readonly ClanRules _rules;
        private readonly Func<string, string, bool> _blockedEitherWay;
        private readonly IFeatureGate _features;
        private readonly IClock _clock;
        private readonly AuditLog _audit;
        private readonly ActivityTracker _activity;
        private readonly Dictionary<string, Clan> _clans = new Dictionary<string, Clan>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _clanOf = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, ClanInvitation> _invites = new Dictionary<string, ClanInvitation>(StringComparer.Ordinal);
        private readonly List<ClanEvent> _events = new List<ClanEvent>();
        private int _clanCounter, _inviteCounter, _eventCounter;

        public ClanService(ClanRules rules, Func<string, string, bool> blockedEitherWay, IFeatureGate features, IClock clock, AuditLog audit, ActivityTracker activity)
        {
            _rules = rules ?? ClanRules.Default;
            _blockedEitherWay = blockedEitherWay ?? ((a, b) => false);
            _features = features ?? OpenFeatureGate.Instance;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _audit = audit ?? new AuditLog();
            _activity = activity ?? new ActivityTracker();
        }

        public ClanRules Rules => _rules;
        public AuditLog Audit => _audit;
        public ActivityTracker Activity => _activity;

        public Clan Get(string clanId)
        {
            lock (_gate) return clanId != null && _clans.TryGetValue(clanId, out Clan c) ? c : null;
        }

        public Clan ClanOf(string playerId)
        {
            lock (_gate) return _clanOf.TryGetValue(playerId, out string id) ? _clans[id] : null;
        }

        public bool AreClanmates(string a, string b)
        {
            lock (_gate) return _clanOf.TryGetValue(a, out string x) && _clanOf.TryGetValue(b, out string y) && x == y;
        }

        public ClanResult Create(string leaderId, string name, out Clan clan)
        {
            clan = null;
            DateTimeOffset now = _clock.UtcNow;
            if (_features.IsSuspended(leaderId, SocialFeature.ClanCreation, now)) return ClanResult.Suspended;
            if (ClanNamePolicy.Validate(name, _rules) != null) return ClanResult.InvalidName;
            lock (_gate)
            {
                if (_clanOf.ContainsKey(leaderId)) return ClanResult.AlreadyInClan;
                string trimmed = name.Trim();
                if (_clans.Values.Any(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))) return ClanResult.NameTaken;
                clan = new Clan("clan-" + (++_clanCounter).ToString("0000", CultureInfo.InvariantCulture), trimmed, now);
                clan.MutableMembers.Add(new ClanMember(leaderId, ClanRole.Leader, now));
                _clans[clan.Id] = clan;
                _clanOf[leaderId] = clan.Id;
                _activity.Record(leaderId, now);
                _audit.Write(now, leaderId, "clan.create", clan.Id, trimmed);
                return ClanResult.Ok;
            }
        }

        /// <summary>Leader or officer invites a player (by account, after the client resolved a friend code or friend list entry).</summary>
        public ClanResult Invite(string actorId, string targetId, out ClanInvitation invitation)
        {
            invitation = null;
            DateTimeOffset now = _clock.UtcNow;
            if (_features.IsSuspended(actorId, SocialFeature.ClanInvites, now)) return ClanResult.Suspended;
            lock (_gate)
            {
                Clan clan = ClanOfLocked(actorId);
                if (clan == null) return ClanResult.NotFound;
                ClanMember actor = clan.Member(actorId);
                if (actor.Role == ClanRole.Member) return ClanResult.NotAllowed;
                if (_clanOf.ContainsKey(targetId)) return ClanResult.AlreadyInClan;
                if (clan.Members.Count >= _rules.MaxMembers) return ClanResult.ClanFull;
                if (_blockedEitherWay(actorId, targetId)) return ClanResult.SilentlyDropped;
                invitation = new ClanInvitation("ci-" + (++_inviteCounter).ToString(CultureInfo.InvariantCulture), clan.Id, actorId, targetId, now + _rules.InviteTtl);
                _invites[invitation.Id] = invitation;
                _activity.Record(actorId, now);
                _audit.Write(now, actorId, "clan.invite", clan.Id, targetId);
                return ClanResult.Ok;
            }
        }

        public IReadOnlyList<ClanInvitation> Invitations(string playerId)
        {
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate) return _invites.Values.Where(i => i.To == playerId && i.IsOpen(now) && !_blockedEitherWay(i.From, i.To)).ToArray();
        }

        public ClanResult Respond(string playerId, string invitationId, bool accept)
        {
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                if (!_invites.TryGetValue(invitationId, out ClanInvitation inv) || inv.To != playerId || inv.State != InviteState.Pending) return ClanResult.NotFound;
                if (now >= inv.ExpiresAt) { inv.State = InviteState.Expired; return ClanResult.Expired; }
                if (_blockedEitherWay(inv.From, inv.To)) { inv.State = InviteState.Withdrawn; return ClanResult.NotFound; }
                if (!accept) { inv.State = InviteState.Declined; return ClanResult.Ok; }
                if (_clanOf.ContainsKey(playerId)) return ClanResult.AlreadyInClan;
                if (!_clans.TryGetValue(inv.ClanId, out Clan clan)) return ClanResult.NotFound;
                if (clan.Members.Count >= _rules.MaxMembers) return ClanResult.ClanFull;
                inv.State = InviteState.Accepted;
                clan.MutableMembers.Add(new ClanMember(playerId, ClanRole.Member, now));
                _clanOf[playerId] = clan.Id;
                _activity.Record(playerId, now);
                _audit.Write(now, playerId, "clan.join", clan.Id, inv.From);
                return ClanResult.Ok;
            }
        }

        public ClanResult Leave(string playerId)
        {
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                Clan clan = ClanOfLocked(playerId);
                if (clan == null) return ClanResult.NotFound;
                ClanMember m = clan.Member(playerId);
                if (m.Role == ClanRole.Leader && clan.Members.Count > 1) return ClanResult.LeaderMustTransfer;
                RemoveLocked(clan, playerId);
                _audit.Write(now, playerId, "clan.leave", clan.Id, null);
                if (clan.Members.Count == 0)
                {
                    _clans.Remove(clan.Id);
                    _audit.Write(now, "system", "clan.disband", clan.Id, "last member left");
                }
                return ClanResult.Ok;
            }
        }

        /// <summary>The leader may remove anyone; an officer may remove ordinary members only.</summary>
        public ClanResult Remove(string actorId, string targetId)
        {
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                Clan clan = ClanOfLocked(actorId);
                if (clan == null || clan.Member(targetId) == null || actorId == targetId) return ClanResult.NotFound;
                ClanRole actor = clan.Member(actorId).Role, target = clan.Member(targetId).Role;
                bool allowed = actor == ClanRole.Leader || (actor == ClanRole.Officer && target == ClanRole.Member);
                if (!allowed) return ClanResult.NotAllowed;
                RemoveLocked(clan, targetId);
                _activity.Record(actorId, now);
                _audit.Write(now, actorId, "clan.remove", clan.Id, targetId);
                return ClanResult.Ok;
            }
        }

        /// <summary>Only the leader promotes or demotes officers (at most <see cref="ClanRules.MaxOfficers"/>).</summary>
        public ClanResult SetRole(string actorId, string targetId, ClanRole role)
        {
            DateTimeOffset now = _clock.UtcNow;
            if (role == ClanRole.Leader) return ClanResult.NotAllowed; // leadership moves only through TransferLeadership or support succession
            lock (_gate)
            {
                Clan clan = ClanOfLocked(actorId);
                if (clan == null || clan.Member(targetId) == null) return ClanResult.NotFound;
                if (clan.Member(actorId).Role != ClanRole.Leader || actorId == targetId) return ClanResult.NotAllowed;
                ClanMember t = clan.Member(targetId);
                if (role == ClanRole.Officer && t.Role != ClanRole.Officer && clan.OfficerCount >= _rules.MaxOfficers) return ClanResult.OfficerLimit;
                t.Role = role;
                _activity.Record(actorId, now);
                _audit.Write(now, actorId, "clan.role", clan.Id, targetId + "=" + role);
                return ClanResult.Ok;
            }
        }

        /// <summary>The leader hands leadership to a member voluntarily (the old leader becomes an officer if a slot is free).</summary>
        public ClanResult TransferLeadership(string leaderId, string newLeaderId)
        {
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                Clan clan = ClanOfLocked(leaderId);
                if (clan == null || clan.Member(newLeaderId) == null || leaderId == newLeaderId) return ClanResult.NotFound;
                if (clan.Member(leaderId).Role != ClanRole.Leader) return ClanResult.NotAllowed;
                ChangeLeaderLocked(clan, newLeaderId);
                _activity.Record(leaderId, now);
                _audit.Write(now, leaderId, "clan.transfer", clan.Id, newLeaderId);
                return ClanResult.Ok;
            }
        }

        /// <summary>Used only by <see cref="SuccessionService"/> after support approval.</summary>
        internal void ApplySuccession(string clanId, string newLeaderId)
        {
            lock (_gate) ChangeLeaderLocked(_clans[clanId], newLeaderId);
        }

        private void ChangeLeaderLocked(Clan clan, string newLeaderId)
        {
            ClanMember oldLeader = clan.Members.First(m => m.Role == ClanRole.Leader);
            ClanMember next = clan.Member(newLeaderId);
            bool nextWasOfficer = next.Role == ClanRole.Officer;
            next.Role = ClanRole.Leader;
            oldLeader.Role = nextWasOfficer || clan.OfficerCount < _rules.MaxOfficers ? ClanRole.Officer : ClanRole.Member;
        }

        public ClanResult Rename(string actorId, string newName)
        {
            DateTimeOffset now = _clock.UtcNow;
            if (ClanNamePolicy.Validate(newName, _rules) != null) return ClanResult.InvalidName;
            lock (_gate)
            {
                Clan clan = ClanOfLocked(actorId);
                if (clan == null) return ClanResult.NotFound;
                if (clan.Member(actorId).Role != ClanRole.Leader) return ClanResult.NotAllowed;
                string trimmed = newName.Trim();
                if (_clans.Values.Any(c => c != clan && string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))) return ClanResult.NameTaken;
                clan.Name = trimmed;
                _audit.Write(now, actorId, "clan.rename", clan.Id, trimmed);
                return ClanResult.Ok;
            }
        }

        /// <summary>Leader or officers choose a preset profile line and emblem (no free text).</summary>
        public ClanResult SetProfile(string actorId, string profilePresetId, string emblemId)
        {
            if (!ClanPresets.Profiles.Contains(profilePresetId) || !ClanPresets.Emblems.Contains(emblemId)) return ClanResult.InvalidPreset;
            lock (_gate)
            {
                Clan clan = ClanOfLocked(actorId);
                if (clan == null) return ClanResult.NotFound;
                if (clan.Member(actorId).Role == ClanRole.Member) return ClanResult.NotAllowed;
                clan.ProfilePresetId = profilePresetId;
                clan.EmblemId = emblemId;
                _audit.Write(_clock.UtcNow, actorId, "clan.profile", clan.Id, profilePresetId + "/" + emblemId);
                return ClanResult.Ok;
            }
        }

        /// <summary>Moderation outcome for a reported name: reset to a neutral placeholder (audited).</summary>
        public void ResetNameByModeration(string clanId, string moderatorId, string caseId)
        {
            lock (_gate)
            {
                if (!_clans.TryGetValue(clanId, out Clan clan)) return;
                clan.Name = ClanNamePolicy.Placeholder(clanId);
                clan.ProfilePresetId = ClanPresets.DefaultProfile;
                _audit.Write(_clock.UtcNow, "moderator:" + moderatorId, "clan.name-reset", clanId, caseId);
            }
        }

        /// <summary>Officers and the leader organise events from the preset event list.</summary>
        public ClanResult OrganiseEvent(string actorId, string presetEventId, DateTimeOffset startsAt, out ClanEvent evt)
        {
            evt = null;
            if (!ClanPresets.Events.Contains(presetEventId)) return ClanResult.InvalidPreset;
            lock (_gate)
            {
                Clan clan = ClanOfLocked(actorId);
                if (clan == null) return ClanResult.NotFound;
                if (clan.Member(actorId).Role == ClanRole.Member) return ClanResult.NotAllowed;
                evt = new ClanEvent("ce-" + (++_eventCounter).ToString(CultureInfo.InvariantCulture), clan.Id, presetEventId, startsAt, actorId);
                _events.Add(evt);
                _activity.Record(actorId, _clock.UtcNow);
                _audit.Write(_clock.UtcNow, actorId, "clan.event", clan.Id, presetEventId);
                return ClanResult.Ok;
            }
        }

        public IReadOnlyList<ClanEvent> Events(string clanId)
        {
            lock (_gate) return _events.Where(e => e.ClanId == clanId).OrderBy(e => e.StartsAt).ToArray();
        }

        public int DeletePlayer(string playerId)
        {
            lock (_gate)
            {
                int n = 0;
                Clan clan = ClanOfLocked(playerId);
                if (clan != null)
                {
                    bool wasLeader = clan.Member(playerId).Role == ClanRole.Leader;
                    RemoveLocked(clan, playerId);
                    n++;
                    if (clan.Members.Count == 0) _clans.Remove(clan.Id);
                    else if (wasLeader)
                    {
                        // Account deletion is not a takeover: the clan is flagged for support-reviewed succession.
                        ClanMember interim = clan.Members.OrderByDescending(m => m.Role).ThenBy(m => m.JoinedAt).First();
                        interim.Role = ClanRole.Leader;
                        _audit.Write(_clock.UtcNow, "system", "clan.leader-deleted", clan.Id, "interim leader " + interim.PlayerId + "; support review required");
                    }
                }
                foreach (string id in _invites.Values.Where(i => i.From == playerId || i.To == playerId).Select(i => i.Id).ToList()) { _invites.Remove(id); n++; }
                _activity.Delete(playerId);
                return n;
            }
        }

        private Clan ClanOfLocked(string playerId) => _clanOf.TryGetValue(playerId, out string id) && _clans.TryGetValue(id, out Clan c) ? c : null;

        private void RemoveLocked(Clan clan, string playerId)
        {
            clan.MutableMembers.RemoveAll(m => m.PlayerId == playerId);
            _clanOf.Remove(playerId);
        }
    }

    /// <summary>Preset (translatable) identifiers for clan profiles, emblems, events and system notices.</summary>
    public static class ClanPresets
    {
        public const string DefaultProfile = "clan.profile.friendly";
        public const string DefaultEmblem = "emblem.lotus";

        public static readonly IReadOnlyCollection<string> Profiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "clan.profile.friendly", "clan.profile.competitive", "clan.profile.learning", "clan.profile.weekend", "clan.profile.builders",
        };

        public static readonly IReadOnlyCollection<string> Emblems = new HashSet<string>(StringComparer.Ordinal)
        {
            "emblem.lotus", "emblem.sun", "emblem.wave", "emblem.mountain", "emblem.bolt", "emblem.wind",
        };

        public static readonly IReadOnlyCollection<string> Events = new HashSet<string>(StringComparer.Ordinal)
        {
            "clan.event.practice", "clan.event.rankedPush", "clan.event.kingdomTour", "clan.event.friendlyDuels",
        };
    }
}
