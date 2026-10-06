using System;
using System.Collections.Generic;

namespace AstraKingdoms.V2.Common
{
    /// <summary>
    /// What other V2 services need to know about relationships between two accounts. Implemented by
    /// <c>Social.SocialRelations</c> over the friend graph and clan rosters; kingdom visits, presence,
    /// clan invitations and preset chat all consult it so a block applies everywhere at once.
    /// </summary>
    public interface ISocialRelations
    {
        bool AreFriends(string a, string b);
        /// <summary>True when either account has blocked the other.</summary>
        bool IsBlockedEitherWay(string a, string b);
        bool AreClanmates(string a, string b);
    }

    /// <summary>Social features that moderation can suspend temporarily for one account.</summary>
    public enum SocialFeature : byte
    {
        FriendInvites = 1,
        ClanChat = 2,
        ClanCreation = 3,
        ClanInvites = 4,
        PhotoAvatar = 5,
        KingdomVisits = 6,
        ReplaySharing = 7,
        Reporting = 8,
    }

    /// <summary>Answers whether moderation has temporarily suspended a feature for an account.</summary>
    public interface IFeatureGate
    {
        bool IsSuspended(string playerId, SocialFeature feature, DateTimeOffset now);
    }

    /// <summary>A gate with no suspensions (tests and the offline device profile).</summary>
    public sealed class OpenFeatureGate : IFeatureGate
    {
        public static readonly OpenFeatureGate Instance = new OpenFeatureGate();
        public bool IsSuspended(string playerId, SocialFeature feature, DateTimeOffset now) => false;
    }

    /// <summary>One audit line (clan administration, succession, moderation, emergency changes).</summary>
    public sealed class AuditEntry
    {
        public long Sequence { get; }
        public DateTimeOffset At { get; }
        /// <summary>Who acted: a player id, "support:{agent}", "moderator:{id}" or "system".</summary>
        public string Actor { get; }
        public string Action { get; }
        /// <summary>The object acted on (clan id, case id, player id).</summary>
        public string Subject { get; }
        public string Detail { get; }

        public AuditEntry(long sequence, DateTimeOffset at, string actor, string action, string subject, string detail)
        {
            Sequence = sequence;
            At = at;
            Actor = actor;
            Action = action;
            Subject = subject;
            Detail = detail ?? string.Empty;
        }

        public override string ToString() => "#" + Sequence + " " + At.ToString("u", System.Globalization.CultureInfo.InvariantCulture) + " " + Actor + " " + Action + " " + Subject + " " + Detail;
    }

    /// <summary>Append-only audit trail (server: an insert-only table, retained per the support policy).</summary>
    public sealed class AuditLog
    {
        private readonly object _gate = new object();
        private readonly List<AuditEntry> _entries = new List<AuditEntry>();

        public AuditEntry Write(DateTimeOffset at, string actor, string action, string subject, string detail = null)
        {
            lock (_gate)
            {
                var e = new AuditEntry(_entries.Count + 1, at, actor, action, subject, detail);
                _entries.Add(e);
                return e;
            }
        }

        public IReadOnlyList<AuditEntry> Entries
        {
            get { lock (_gate) return _entries.ToArray(); }
        }

        public IReadOnlyList<AuditEntry> For(string subject)
        {
            lock (_gate) return _entries.FindAll(e => e.Subject == subject).ToArray();
        }
    }
}
