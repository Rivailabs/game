using System;
using System.Collections.Generic;
using AstraKingdoms.World.Season;

namespace AstraKingdoms.World.Social
{
    public enum AllianceRole : byte
    {
        Member = 0,
        Officer = 1,
        Leader = 2,
    }

    /// <summary>An optional shared objective scheduled by an officer or the leader.</summary>
    public sealed class AllianceObjective
    {
        public string ObjectiveId { get; internal set; }
        public int Target { get; internal set; }
        public string CosmeticReward { get; internal set; }
        public string ScheduledBy { get; internal set; }
        public long StartMs { get; internal set; }
        public long EndMs { get; internal set; }
        public bool Completed { get; internal set; }
        internal readonly Dictionary<string, int> Contributions = new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly HashSet<string> Participants = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> AppliedKeys = new HashSet<string>(StringComparer.Ordinal);

        public int Progress
        {
            get
            {
                int sum = 0;
                foreach (int v in Contributions.Values) sum += v;
                return sum;
            }
        }

        public int ContributionOf(string member) => Contributions.TryGetValue(member, out int v) ? v : 0;
        public bool IsParticipant(string member) => Participants.Contains(member);
    }

    /// <summary>An audit entry: who changed what in an alliance.</summary>
    public sealed class AllianceAudit
    {
        public long AtMs { get; internal set; }
        public string Actor { get; internal set; }
        public string Action { get; internal set; }
        public string Subject { get; internal set; }
    }

    /// <summary>
    /// PROPOSED alliances reusing the twenty-member clan structure (plan: "Alliances seasonal
    /// operations and the production gate"): one leader, at most two officers, at most twenty
    /// members; officers invite and schedule optional objectives; only the leader changes officer
    /// roles. Members choose whether to take part. Contributions are capped per member so a few
    /// highly active members cannot control all progress. Rewards are cosmetics granted directly
    /// to each contributing participant's own account: there is no alliance wallet, no
    /// confiscation, no attendance penalty and no API that moves one member's assets to another.
    /// Thread-safe (one lock); every role or membership change is audited.
    /// </summary>
    public sealed class AllianceDirectory
    {
        private sealed class Alliance
        {
            public string Id;
            public readonly Dictionary<string, AllianceRole> Members = new Dictionary<string, AllianceRole>(StringComparer.Ordinal);
            public readonly HashSet<string> Invites = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, AllianceObjective> Objectives = new Dictionary<string, AllianceObjective>(StringComparer.Ordinal);
            public readonly List<AllianceAudit> Audit = new List<AllianceAudit>();

            public int Officers
            {
                get
                {
                    int n = 0;
                    foreach (AllianceRole r in Members.Values) if (r == AllianceRole.Officer) n++;
                    return n;
                }
            }
        }

        private readonly object _gate = new object();
        private readonly AccountRegistry _accounts;
        private readonly Dictionary<string, Alliance> _alliances = new Dictionary<string, Alliance>(StringComparer.Ordinal);

        public AllianceDirectory(AccountRegistry accounts)
        {
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        }

        public string Create(string allianceId, string leaderId, long nowMs)
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(allianceId)) return "ALLIANCE_ID";
                if (_alliances.ContainsKey(allianceId)) return "ALLIANCE_EXISTS";
                AccountSnapshot leader = _accounts.Get(leaderId);
                if (leader == null) return "UNKNOWN_ACCOUNT";
                if (leader.AllianceId != null) return "ALREADY_IN_ALLIANCE";
                var a = new Alliance { Id = allianceId };
                a.Members[leaderId] = AllianceRole.Leader;
                _alliances[allianceId] = a;
                _accounts.SetAlliance(leaderId, allianceId, nowMs);
                Log(a, nowMs, leaderId, "create", leaderId);
                return null;
            }
        }

        public string Invite(string actorId, string allianceId, string inviteeId, long nowMs)
        {
            lock (_gate)
            {
                if (!_alliances.TryGetValue(allianceId, out Alliance a)) return "UNKNOWN_ALLIANCE";
                if (!a.Members.TryGetValue(actorId, out AllianceRole role) || role == AllianceRole.Member) return "NOT_PERMITTED";
                if (!_accounts.Exists(inviteeId)) return "UNKNOWN_ACCOUNT";
                if (a.Members.ContainsKey(inviteeId)) return "ALREADY_MEMBER";
                a.Invites.Add(inviteeId);
                Log(a, nowMs, actorId, "invite", inviteeId);
                return null;
            }
        }

        public string Accept(string inviteeId, string allianceId, long nowMs)
        {
            lock (_gate)
            {
                if (!_alliances.TryGetValue(allianceId, out Alliance a)) return "UNKNOWN_ALLIANCE";
                if (!a.Invites.Contains(inviteeId)) return "NOT_INVITED";
                if (_accounts.Get(inviteeId).AllianceId != null) return "ALREADY_IN_ALLIANCE";
                if (a.Members.Count >= WorldRules.AllianceMaxMembers) return "ALLIANCE_FULL";
                a.Invites.Remove(inviteeId);
                a.Members[inviteeId] = AllianceRole.Member;
                _accounts.SetAlliance(inviteeId, allianceId, nowMs);
                Log(a, nowMs, inviteeId, "join", inviteeId);
                return null;
            }
        }

        /// <summary>A member leaves voluntarily. The leader must hand over leadership first unless alone.</summary>
        public string Leave(string memberId, long nowMs)
        {
            lock (_gate)
            {
                Alliance a = AllianceOf(memberId);
                if (a == null) return "NOT_IN_ALLIANCE";
                if (a.Members[memberId] == AllianceRole.Leader && a.Members.Count > 1) return "LEADER_MUST_HAND_OVER";
                a.Members.Remove(memberId);
                _accounts.SetAlliance(memberId, null, nowMs);
                Log(a, nowMs, memberId, "leave", memberId);
                if (a.Members.Count == 0) _alliances.Remove(a.Id);
                return null;
            }
        }

        /// <summary>
        /// Removes a member from the roster. Only membership changes: the member keeps every coin,
        /// cosmetic, homeland plot and border tile (nothing is confiscated).
        /// </summary>
        public string Remove(string actorId, string allianceId, string memberId, long nowMs)
        {
            lock (_gate)
            {
                if (!_alliances.TryGetValue(allianceId, out Alliance a)) return "UNKNOWN_ALLIANCE";
                if (!a.Members.TryGetValue(actorId, out AllianceRole actor) || !a.Members.TryGetValue(memberId, out AllianceRole target))
                    return "NOT_PERMITTED";
                bool allowed = actor == AllianceRole.Leader ? target != AllianceRole.Leader
                    : actor == AllianceRole.Officer && target == AllianceRole.Member;
                if (!allowed) return "NOT_PERMITTED";
                a.Members.Remove(memberId);
                _accounts.SetAlliance(memberId, null, nowMs);
                Log(a, nowMs, actorId, "remove", memberId);
                return null;
            }
        }

        /// <summary>Only the leader changes officer roles; at most two officers.</summary>
        public string SetOfficer(string actorId, string allianceId, string memberId, bool officer, long nowMs)
        {
            lock (_gate)
            {
                if (!_alliances.TryGetValue(allianceId, out Alliance a)) return "UNKNOWN_ALLIANCE";
                if (!a.Members.TryGetValue(actorId, out AllianceRole actor) || actor != AllianceRole.Leader) return "NOT_PERMITTED";
                if (!a.Members.TryGetValue(memberId, out AllianceRole current) || current == AllianceRole.Leader) return "NOT_PERMITTED";
                if (officer && current != AllianceRole.Officer && a.Officers >= WorldRules.AllianceMaxOfficers) return "OFFICER_LIMIT";
                a.Members[memberId] = officer ? AllianceRole.Officer : AllianceRole.Member;
                Log(a, nowMs, actorId, officer ? "promote" : "demote", memberId);
                return null;
            }
        }

        /// <summary>The leader hands leadership to a member (who becomes leader; the old leader becomes a member).</summary>
        public string HandOverLeadership(string leaderId, string allianceId, string successorId, long nowMs)
        {
            lock (_gate)
            {
                if (!_alliances.TryGetValue(allianceId, out Alliance a)) return "UNKNOWN_ALLIANCE";
                if (!a.Members.TryGetValue(leaderId, out AllianceRole r) || r != AllianceRole.Leader) return "NOT_PERMITTED";
                if (!a.Members.ContainsKey(successorId) || successorId == leaderId) return "NOT_PERMITTED";
                a.Members[successorId] = AllianceRole.Leader;
                a.Members[leaderId] = AllianceRole.Member;
                Log(a, nowMs, leaderId, "handover", successorId);
                return null;
            }
        }

        public string ScheduleObjective(string actorId, string allianceId, string objectiveId, int target, string cosmeticReward,
            long startMs, long endMs)
        {
            lock (_gate)
            {
                if (!_alliances.TryGetValue(allianceId, out Alliance a)) return "UNKNOWN_ALLIANCE";
                if (!a.Members.TryGetValue(actorId, out AllianceRole role) || role == AllianceRole.Member) return "NOT_PERMITTED";
                if (a.Objectives.ContainsKey(objectiveId)) return "OBJECTIVE_EXISTS";
                if (target <= 0 || endMs <= startMs) return "OBJECTIVE_INVALID";
                a.Objectives[objectiveId] = new AllianceObjective
                {
                    ObjectiveId = objectiveId,
                    Target = target,
                    CosmeticReward = cosmeticReward,
                    ScheduledBy = actorId,
                    StartMs = startMs,
                    EndMs = endMs,
                };
                Log(a, startMs, actorId, "schedule", objectiveId);
                return null;
            }
        }

        /// <summary>Members choose to take part; nobody is enrolled automatically and nobody is penalised for staying out.</summary>
        public string OptIn(string memberId, string allianceId, string objectiveId)
        {
            lock (_gate)
            {
                if (!_alliances.TryGetValue(allianceId, out Alliance a) || !a.Members.ContainsKey(memberId)) return "NOT_MEMBER";
                if (!a.Objectives.TryGetValue(objectiveId, out AllianceObjective o)) return "UNKNOWN_OBJECTIVE";
                o.Participants.Add(memberId);
                return null;
            }
        }

        /// <summary>
        /// Adds a participant's contribution (idempotent per key), clipped so no member exceeds the
        /// per-member cap. Returns the amount counted. When the objective completes, every
        /// contributing participant receives the cosmetic once, in their own account.
        /// </summary>
        public int Contribute(string memberId, string allianceId, string objectiveId, int amount, string key, long nowMs)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            lock (_gate)
            {
                if (!_alliances.TryGetValue(allianceId, out Alliance a) || !a.Members.ContainsKey(memberId)) return 0;
                if (!a.Objectives.TryGetValue(objectiveId, out AllianceObjective o)) return 0;
                if (!o.Participants.Contains(memberId) || nowMs < o.StartMs || nowMs >= o.EndMs) return 0;
                if (!o.AppliedKeys.Add(key)) return 0;
                int counted = Math.Max(0, Math.Min(amount, WorldRules.ObjectiveContributionCapPerMember - o.ContributionOf(memberId)));
                o.Contributions[memberId] = o.ContributionOf(memberId) + counted;
                if (!o.Completed && o.Progress >= o.Target)
                {
                    o.Completed = true;
                    foreach (KeyValuePair<string, int> c in o.Contributions)
                    {
                        if (c.Value <= 0 || !a.Members.ContainsKey(c.Key)) continue;
                        string grant = "alliance/" + a.Id + "/" + o.ObjectiveId + "/" + c.Key;
                        _accounts.GrantCosmetic(grant, c.Key, o.CosmeticReward);
                    }
                }
                return counted;
            }
        }

        public AllianceRole? RoleOf(string allianceId, string memberId)
        {
            lock (_gate)
            {
                if (!_alliances.TryGetValue(allianceId, out Alliance a) || !a.Members.TryGetValue(memberId, out AllianceRole r)) return null;
                return r;
            }
        }

        public int MemberCount(string allianceId)
        {
            lock (_gate) return _alliances.TryGetValue(allianceId, out Alliance a) ? a.Members.Count : 0;
        }

        public AllianceObjective Objective(string allianceId, string objectiveId)
        {
            lock (_gate)
            {
                if (!_alliances.TryGetValue(allianceId, out Alliance a)) return null;
                return a.Objectives.TryGetValue(objectiveId, out AllianceObjective o) ? o : null;
            }
        }

        public IReadOnlyList<AllianceAudit> AuditOf(string allianceId)
        {
            lock (_gate) return _alliances.TryGetValue(allianceId, out Alliance a) ? a.Audit.ToArray() : Array.Empty<AllianceAudit>();
        }

        private Alliance AllianceOf(string memberId)
        {
            foreach (Alliance a in _alliances.Values)
                if (a.Members.ContainsKey(memberId)) return a;
            return null;
        }

        private static void Log(Alliance a, long nowMs, string actor, string action, string subject) =>
            a.Audit.Add(new AllianceAudit { AtMs = nowMs, Actor = actor, Action = action, Subject = subject });
    }
}
