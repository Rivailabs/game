using System;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Social
{
    /// <summary><see cref="ISocialRelations"/> over the friend graph (friends, blocks) and clan rosters.</summary>
    public sealed class SocialRelations : ISocialRelations
    {
        private readonly FriendService _friends;
        private readonly Func<ClanService> _clans;

        public SocialRelations(FriendService friends, Func<ClanService> clans)
        {
            _friends = friends ?? throw new ArgumentNullException(nameof(friends));
            _clans = clans ?? (() => null);
        }

        public bool AreFriends(string a, string b) => _friends.AreFriends(a, b);
        public bool IsBlockedEitherWay(string a, string b) => _friends.IsBlockedEitherWay(a, b);
        public bool AreClanmates(string a, string b) => _clans()?.AreClanmates(a, b) == true && !_friends.IsBlockedEitherWay(a, b);
    }

    /// <summary>
    /// Composition of the social services in the order their dependencies need (moderation gate →
    /// friends → clans → chat → succession → milestones), with moderation outcomes wired to clans.
    /// </summary>
    public sealed class SocialHub
    {
        public ModerationService Moderation { get; }
        public FriendService Friends { get; }
        public ClanService Clans { get; }
        public ClanChatService Chat { get; }
        public SuccessionService Succession { get; }
        public ClanMilestoneService ClanMilestones { get; }
        public SocialRelations Relations { get; }
        public SocialOperations Operations { get; }

        public SocialHub(Meta.Common.IClock clock, IGrantLedgerStore grants, string friendCodeSeed, SocialOperations operations = null,
            SocialRules socialRules = null, ClanRules clanRules = null, ClanMilestoneTrack track = null)
        {
            var audit = new AuditLog();
            Operations = operations ?? new SocialOperations();
            Moderation = new ModerationService(clock, audit);
            Friends = new FriendService(socialRules, Moderation, clock, friendCodeSeed);
            Clans = new ClanService(clanRules, Friends.IsBlockedEitherWay, Moderation, clock, audit, new ActivityTracker());
            Chat = new ClanChatService(p => Clans.ClanOf(p)?.Id, Friends.IsBlockedEitherWay, Moderation, clock);
            Succession = new SuccessionService(Clans, Chat, clock);
            ClanMilestones = new ClanMilestoneService(track, Clans, Chat, grants, clock);
            Relations = new SocialRelations(Friends, () => Clans);
            Moderation.ClanNameReset += (clanId, moderator, caseId) => Clans.ResetNameByModeration(clanId, moderator, caseId);
        }
    }
}
