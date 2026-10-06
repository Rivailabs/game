using System.Reflection;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.V2.Common;
using AstraKingdoms.V2.Integration;
using AstraKingdoms.V2.Kingdom;
using AstraKingdoms.V2.Social;
using NUnit.Framework;

namespace AstraKingdoms.V2.Tests;

/// <summary>V2 social package: friends, block semantics, visits, clans, succession, preset chat, milestones, moderation.</summary>
public class SocialTests
{
    private static SocialHub Hub(out ManualClock clock, InMemoryGrantLedgerStore grants = null)
    {
        clock = TestUtil.Clock();
        return new SocialHub(clock, grants ?? new InMemoryGrantLedgerStore(), "test-seed");
    }

    private static void MakeFriends(SocialHub hub, string a, string b)
    {
        Assert.That(hub.Friends.Invite(a, hub.Friends.CodeFor(b)), Is.EqualTo(FriendInviteResult.Sent));
        FriendInvitation inv = hub.Friends.Incoming(b).Single(i => i.From == a);
        Assert.That(hub.Friends.Respond(b, inv.Id, true), Is.EqualTo(FriendResponseResult.Accepted));
    }

    // ------------------------------------------------------------------ friends

    [Test]
    public void FriendCodesAreReadableUniqueAndForgiving()
    {
        SocialHub hub = Hub(out _);
        var codes = Enumerable.Range(0, 500).Select(i => hub.Friends.CodeFor("p" + i)).ToList();
        Assert.That(codes, Is.Unique);
        Assert.That(codes.All(c => c.Length == 9 && c[4] == '-'), Is.True);
        Assert.That(codes.SelectMany(c => c.Replace("-", "")).All(ch => FriendService.CodeAlphabet.Contains(ch)), Is.True);
        Assert.That(hub.Friends.CodeFor("p1"), Is.EqualTo(codes[1]), "stable");
        Assert.That(FriendService.Normalise(" abcd-efgh "), Is.EqualTo("ABCDEFGH"));
        Assert.That(FriendService.Normalise("o1il-0000"), Is.EqualTo("0111" + "0000"));
        Assert.That(FriendService.Normalise("ABC"), Is.Null);
        string old = hub.Friends.CodeFor("p2");
        string fresh = hub.Friends.RegenerateCode("p2");
        Assert.That(fresh, Is.Not.EqualTo(old));
        Assert.That(hub.Friends.Invite("p3", old), Is.EqualTo(FriendInviteResult.InvalidCode));
    }

    [Test]
    public void FriendshipNeedsExplicitAcceptanceAndInvitationsExpire()
    {
        SocialHub hub = Hub(out ManualClock clock);
        Assert.That(hub.Friends.Invite("a", hub.Friends.CodeFor("a")), Is.EqualTo(FriendInviteResult.Self));
        Assert.That(hub.Friends.Invite("a", hub.Friends.CodeFor("b")), Is.EqualTo(FriendInviteResult.Sent));
        Assert.That(hub.Friends.Invite("a", hub.Friends.CodeFor("b")), Is.EqualTo(FriendInviteResult.AlreadyPending));
        Assert.That(hub.Friends.Invite("b", hub.Friends.CodeFor("a")), Is.EqualTo(FriendInviteResult.AlreadyPending), "crossing invitations");
        Assert.That(hub.Friends.AreFriends("a", "b"), Is.False, "no friendship without acceptance");
        FriendInvitation inv = hub.Friends.Incoming("b").Single();
        Assert.That(hub.Friends.Respond("a", inv.Id, true), Is.EqualTo(FriendResponseResult.NotFound), "only the recipient answers");
        clock.Advance(SocialRules.Default.FriendInviteTtl);
        Assert.That(hub.Friends.Incoming("b"), Is.Empty);
        Assert.That(hub.Friends.Respond("b", inv.Id, true), Is.EqualTo(FriendResponseResult.Expired));
        Assert.That(hub.Friends.AreFriends("a", "b"), Is.False);
        MakeFriends(hub, "a", "b");
        Assert.That(hub.Friends.AreFriends("b", "a"), Is.True);
        Assert.That(hub.Friends.Invite("a", hub.Friends.CodeFor("b")), Is.EqualTo(FriendInviteResult.AlreadyFriends));
        Assert.That(hub.Friends.Remove("b", "a"), Is.True);
        Assert.That(hub.Friends.AreFriends("a", "b"), Is.False);
    }

    [Test]
    public void BlockSuppressesInvitationsPresenceAndFriendshipWithoutRevealingItself()
    {
        SocialHub hub = Hub(out _);
        MakeFriends(hub, "a", "b");
        hub.Friends.SetPresence("a", PresenceState.InMatch);
        Assert.That(hub.Friends.PresenceAs("b", "a"), Is.EqualTo(PresenceState.InMatch));
        Assert.That(hub.Friends.PresenceAs("c", "a"), Is.Null, "strangers see nothing");
        Assert.That(hub.Friends.Invite("c", hub.Friends.CodeFor("a")), Is.EqualTo(FriendInviteResult.Sent));

        hub.Friends.Block("a", "b");
        hub.Friends.Block("a", "c");
        Assert.That(hub.Friends.AreFriends("a", "b"), Is.False);
        Assert.That(hub.Friends.PresenceAs("b", "a"), Is.Null);
        Assert.That(hub.Friends.PresenceAs("a", "b"), Is.Null, "both directions");
        Assert.That(hub.Friends.Incoming("a"), Is.Empty, "the pending invitation from c was withdrawn");
        Assert.That(hub.Friends.Invite("b", hub.Friends.CodeFor("a")), Is.EqualTo(FriendInviteResult.Sent), "looks sent ...");
        Assert.That(hub.Friends.Incoming("a"), Is.Empty, "... but nothing is delivered");
        Assert.That(hub.Friends.Outgoing("b"), Is.Empty);
        Assert.That(hub.Friends.Invite("a", hub.Friends.CodeFor("b")), Is.EqualTo(FriendInviteResult.Sent));
        Assert.That(hub.Friends.Incoming("b"), Is.Empty, "the blocker cannot reach the blocked account either");

        Assert.That(hub.Friends.Unblock("a", "b"), Is.True);
        Assert.That(hub.Friends.AreFriends("a", "b"), Is.False, "unblocking does not restore the friendship");
        MakeFriends(hub, "b", "a");
    }

    [Test]
    public void SuspendedPlayersCannotSendInvitations()
    {
        SocialHub hub = Hub(out _);
        hub.Moderation.Report("r", ReportTarget.Player, "spammer", "spammer", ReportReason.Spam, out ModerationCase c);
        hub.Moderation.Suspend("mod1", c.Id, SocialFeature.FriendInvites, TimeSpan.FromDays(3));
        Assert.That(hub.Friends.Invite("spammer", hub.Friends.CodeFor("x")), Is.EqualTo(FriendInviteResult.Suspended));
    }

    // ------------------------------------------------------------------ visits

    private sealed class Profiles : IPublicProfiles
    {
        public string Alias(string playerId) => "Alias-" + playerId.Length;
        public string AvatarId(string playerId) => "avatar.stock-01";
    }

    [Test]
    public void VisitsAreReadOnlyFriendsOnlyByDefaultAndOwnerControlled()
    {
        V2Environment env = TestUtil.Env(out ManualClock clock);
        SocialHub hub = new(clock, env.Grants, "seed");
        var visits = new VisitService(env.Kingdom, hub.Relations, hub.Moderation, new Profiles(), clock);
        MakeFriends(hub, "owner", "friend");
        hub.Clans.Create("owner", "Lotus Guard", out _);
        Assert.That(hub.Clans.Invite("owner", "mate", out ClanInvitation ci), Is.EqualTo(ClanResult.Ok));
        hub.Clans.Respond("mate", ci.Id, true);

        VisitResult ok = visits.Visit("friend", "owner");
        Assert.That(ok.Allowed, Is.True);
        Assert.That(ok.Snapshot.Plots, Has.Count.EqualTo(12));
        Assert.That(ok.Snapshot.OwnerAlias, Does.Not.Contain("owner"), "aliases, not account ids");
        Assert.That(typeof(KingdomSnapshot).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName), Is.Empty,
            "a snapshot has no operations");
        Assert.That(visits.Visit("stranger", "owner").VisibleDenial, Is.EqualTo(VisitDenial.NotInAudience));
        Assert.That(visits.Visit("mate", "owner").Allowed, Is.False, "clanmates are not friends by default");
        env.Kingdom.SetVisitAudience("owner", VisitAudience.FriendsAndClanmates);
        Assert.That(visits.Visit("mate", "owner").Allowed, Is.True);
        env.Kingdom.SetVisitAudience("owner", VisitAudience.Nobody);
        Assert.That(visits.Visit("friend", "owner").Denial, Is.EqualTo(VisitDenial.VisitsDisabled));
        env.Kingdom.SetVisitAudience("owner", VisitAudience.FriendsOnly);
        hub.Friends.Block("owner", "friend");
        VisitResult blocked = visits.Visit("friend", "owner");
        Assert.That(blocked.Denial, Is.EqualTo(VisitDenial.Blocked));
        Assert.That(blocked.VisibleDenial, Is.EqualTo(VisitDenial.NotInAudience), "the blocked visitor is not told about the block");
        Assert.That(visits.Visit("owner", "owner").Allowed, Is.True);
    }

    // ------------------------------------------------------------------ clans

    private static Clan ClanWith(SocialHub hub, int members, out List<string> ids)
    {
        Assert.That(hub.Clans.Create("leader", "Sun Archers", out Clan clan), Is.EqualTo(ClanResult.Ok));
        ids = new List<string> { "leader" };
        for (int i = 1; i < members; i++)
        {
            string id = "m" + i;
            Assert.That(hub.Clans.Invite("leader", id, out ClanInvitation inv), Is.EqualTo(ClanResult.Ok));
            Assert.That(hub.Clans.Respond(id, inv.Id, true), Is.EqualTo(ClanResult.Ok));
            ids.Add(id);
        }
        return clan;
    }

    [Test]
    public void ClansHoldTwentyMembersWithOneLeaderAndAtMostTwoOfficers()
    {
        SocialHub hub = Hub(out _);
        Clan clan = ClanWith(hub, 20, out _);
        Assert.That(clan.Members, Has.Count.EqualTo(20));
        Assert.That(hub.Clans.Invite("leader", "m20", out _), Is.EqualTo(ClanResult.ClanFull));
        Assert.That(hub.Clans.SetRole("leader", "m1", ClanRole.Officer), Is.EqualTo(ClanResult.Ok));
        Assert.That(hub.Clans.SetRole("leader", "m2", ClanRole.Officer), Is.EqualTo(ClanResult.Ok));
        Assert.That(hub.Clans.SetRole("leader", "m3", ClanRole.Officer), Is.EqualTo(ClanResult.OfficerLimit));
        Assert.That(hub.Clans.SetRole("leader", "m3", ClanRole.Leader), Is.EqualTo(ClanResult.NotAllowed), "leadership never moves through roles");
        Assert.That(clan.Members.Count(m => m.Role == ClanRole.Leader), Is.EqualTo(1));
    }

    [Test]
    public void OfficersInviteAndOrganiseButOnlyTheLeaderChangesOfficerRoles()
    {
        SocialHub hub = Hub(out ManualClock clock);
        Clan clan = ClanWith(hub, 4, out _);
        hub.Clans.SetRole("leader", "m1", ClanRole.Officer);
        Assert.That(hub.Clans.Invite("m1", "newbie", out _), Is.EqualTo(ClanResult.Ok));
        Assert.That(hub.Clans.Invite("m2", "other", out _), Is.EqualTo(ClanResult.NotAllowed), "members cannot invite");
        Assert.That(hub.Clans.OrganiseEvent("m1", "clan.event.practice", clock.UtcNow + TimeSpan.FromDays(1), out ClanEvent e), Is.EqualTo(ClanResult.Ok));
        Assert.That(hub.Clans.OrganiseEvent("m2", "clan.event.practice", clock.UtcNow, out _), Is.EqualTo(ClanResult.NotAllowed));
        Assert.That(hub.Clans.OrganiseEvent("m1", "free text party", clock.UtcNow, out _), Is.EqualTo(ClanResult.InvalidPreset));
        Assert.That(hub.Clans.Events(clan.Id).Single().Id, Is.EqualTo(e.Id));
        Assert.That(hub.Clans.SetRole("m1", "m2", ClanRole.Officer), Is.EqualTo(ClanResult.NotAllowed), "officers cannot promote");
        Assert.That(hub.Clans.SetRole("m1", "m1", ClanRole.Member), Is.EqualTo(ClanResult.NotAllowed));
        Assert.That(hub.Clans.Remove("m1", "m2"), Is.EqualTo(ClanResult.Ok), "officers organise members");
        Assert.That(hub.Clans.Remove("m1", "leader"), Is.EqualTo(ClanResult.NotAllowed));
        hub.Clans.SetRole("leader", "m3", ClanRole.Officer);
        Assert.That(hub.Clans.Remove("m1", "m3"), Is.EqualTo(ClanResult.NotAllowed), "officers cannot remove officers");
        Assert.That(hub.Clans.Remove("leader", "m3"), Is.EqualTo(ClanResult.Ok));
        Assert.That(hub.Clans.Rename("m1", "New Name"), Is.EqualTo(ClanResult.NotAllowed));
        Assert.That(hub.Clans.Leave("leader"), Is.EqualTo(ClanResult.LeaderMustTransfer));
    }

    [Test]
    public void NoRoleCanTransferAnotherMembersAssets()
    {
        foreach (Type t in new[] { typeof(ClanService), typeof(ClanMilestoneService), typeof(SuccessionService), typeof(ClanChatService) })
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Assert.That(m.Name, Does.Not.Match("(?i)gift|give|donat|transferitem|transfercoin|transferasset|withdraw|bank|trade|send(coins|items)"), t.Name + "." + m.Name);
        Assert.That(typeof(Clan).GetProperties().Select(p => p.Name), Has.None.Match("(?i)bank|treasury|inventory|coins"));
    }

    [Test]
    public void ClanNamesAreValidatedAndCanBeResetByModeration()
    {
        SocialHub hub = Hub(out _);
        Assert.That(ClanNamePolicy.Validate("ab", ClanRules.Default), Is.EqualTo("length"));
        Assert.That(ClanNamePolicy.Validate("Official Clan", ClanRules.Default), Is.EqualTo("reserved"));
        Assert.That(ClanNamePolicy.Validate("Hi<script>", ClanRules.Default), Is.EqualTo("characters"));
        Assert.That(ClanNamePolicy.Validate("ಕನ್ನಡ ಬಿಲ್ಲುಗಾರರು", ClanRules.Default), Is.Null, "Indic scripts with combining marks are allowed");
        Assert.That(ClanNamePolicy.Validate("अस्त्र सेना", ClanRules.Default), Is.Null);
        hub.Clans.Create("leader", "Bad Words Here", out Clan clan);
        Assert.That(hub.Clans.Create("x", "bad words here", out _), Is.EqualTo(ClanResult.NameTaken));
        hub.Moderation.Report("reporter", ReportTarget.ClanName, clan.Id, "leader", ReportReason.InappropriateName, out ModerationCase c);
        Assert.That(hub.Moderation.ResetContent("mod1", c.Id), Is.True);
        Assert.That(clan.Name, Is.EqualTo(ClanNamePolicy.Placeholder(clan.Id)));
        Assert.That(hub.Clans.Audit.Entries.Any(e => e.Action == "clan.name-reset"), Is.True);
        Assert.That(hub.Clans.SetProfile("leader", "free text", "emblem.sun"), Is.EqualTo(ClanResult.InvalidPreset), "profiles are presets");
    }

    [Test]
    public void BlocksApplyToClanInvitationsAndChat()
    {
        SocialHub hub = Hub(out _);
        hub.Clans.Create("leader", "Wave Riders", out _);
        hub.Friends.Block("target", "leader");
        Assert.That(hub.Clans.Invite("leader", "target", out _), Is.EqualTo(ClanResult.SilentlyDropped));
        Assert.That(hub.Clans.Invitations("target"), Is.Empty);
        hub.Clans.Invite("leader", "m1", out ClanInvitation i1);
        hub.Clans.Respond("m1", i1.Id, true);
        hub.Clans.Invite("leader", "m2", out ClanInvitation i2);
        hub.Clans.Respond("m2", i2.Id, true);
        Assert.That(hub.Chat.Post("m1", "chat.hello"), Is.EqualTo(ChatPostResult.Posted));
        Assert.That(hub.Chat.Post("m2", "chat.goodGame"), Is.EqualTo(ChatPostResult.Posted));
        hub.Friends.Block("m2", "m1");
        Assert.That(hub.Chat.Feed("m2").Select(e => e.PresetId), Is.EqualTo(new[] { "chat.goodGame" }));
        Assert.That(hub.Chat.Feed("m1").Select(e => e.PresetId), Is.EqualTo(new[] { "chat.hello" }), "both directions");
        Assert.That(hub.Chat.Feed("leader"), Has.Count.EqualTo(2));
    }

    [Test]
    public void ChatAcceptsOnlyPresetsAndIsRateLimitedAndSuspendable()
    {
        SocialHub hub = Hub(out ManualClock clock);
        hub.Clans.Create("leader", "Stone Wall", out _);
        Assert.That(hub.Chat.Post("leader", "hello everyone!"), Is.EqualTo(ChatPostResult.UnknownPreset));
        Assert.That(hub.Chat.Post("outsider", "chat.hello"), Is.EqualTo(ChatPostResult.NotMember));
        for (int i = 0; i < hub.Chat.MaxPostsPerMinute; i++) Assert.That(hub.Chat.Post("leader", "emote.wave"), Is.EqualTo(ChatPostResult.Posted));
        Assert.That(hub.Chat.Post("leader", "emote.wave"), Is.EqualTo(ChatPostResult.RateLimited));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That(hub.Chat.Post("leader", "emote.bow"), Is.EqualTo(ChatPostResult.Posted));
        hub.Moderation.Report("x", ReportTarget.Player, "leader", "leader", ReportReason.Spam, out ModerationCase c);
        hub.Moderation.Suspend("mod", c.Id, SocialFeature.ClanChat, TimeSpan.FromHours(1));
        Assert.That(hub.Chat.Post("leader", "chat.hello"), Is.EqualTo(ChatPostResult.Suspended));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.That(hub.Chat.Post("leader", "chat.hello"), Is.EqualTo(ChatPostResult.Posted), "suspensions are temporary");
        foreach (MethodInfo m in typeof(ClanChatService).GetMethods().Where(m => m.Name == "Post"))
            Assert.That(m.GetParameters().Select(p => p.Name), Is.EqualTo(new[] { "senderId", "presetId" }));
    }

    // ------------------------------------------------------------------ succession

    [Test]
    public void LeaderInactivityNeverTransfersLeadershipAutomatically()
    {
        SocialHub hub = Hub(out ManualClock clock);
        Clan clan = ClanWith(hub, 3, out _);
        hub.Clans.SetRole("leader", "m1", ClanRole.Officer);
        clock.Advance(TimeSpan.FromDays(365));
        Assert.That(clan.LeaderId, Is.EqualTo("leader"), "a year of inactivity changes nothing by itself");
        Assert.That(hub.Succession.LeaderInactive(clan.Id), Is.True);
    }

    [Test]
    public void SupportReviewedSuccessionWithNoticeAndAudit()
    {
        SocialHub hub = Hub(out ManualClock clock);
        Clan clan = ClanWith(hub, 3, out _);
        hub.Clans.SetRole("leader", "m1", ClanRole.Officer);
        clock.Advance(TimeSpan.FromDays(29));
        Assert.That(hub.Succession.Open("agent7", clan.Id, "members asked", out _), Is.EqualTo(SuccessionResult.LeaderStillActive));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.That(hub.Succession.Open("", clan.Id, "x", out _), Is.EqualTo(SuccessionResult.MissingAccountability));
        Assert.That(hub.Succession.Open("agent7", clan.Id, "members asked", out SuccessionCase sc), Is.EqualTo(SuccessionResult.Ok));
        Assert.That(hub.Succession.Open("agent7", clan.Id, "again", out _), Is.EqualTo(SuccessionResult.CaseAlreadyOpen));
        Assert.That(hub.Chat.Notices(clan.Id).Select(n => n.PresetId), Does.Contain("clan.notice.successionOpened"), "the clan is notified");
        Assert.That(hub.Succession.Approve("agent7", sc.Id, "m1", "officer, longest tenure"), Is.EqualTo(SuccessionResult.NoticePeriodRunning));
        clock.Advance(ClanRules.Default.SuccessionNotice);
        Assert.That(hub.Succession.Approve("agent7", sc.Id, "stranger", "x"), Is.EqualTo(SuccessionResult.InvalidCandidate));
        Assert.That(hub.Succession.Approve("agent7", sc.Id, "m1", "officer, longest tenure"), Is.EqualTo(SuccessionResult.Ok));
        Assert.That(clan.LeaderId, Is.EqualTo("m1"));
        Assert.That(clan.Member("leader").Role, Is.Not.EqualTo(ClanRole.Leader));
        string[] actions = hub.Clans.Audit.For(clan.Id).Select(e => e.Action).ToArray();
        Assert.That(actions, Does.Contain("succession.open").And.Contain("succession.approve"));
        Assert.That(hub.Clans.Audit.For(clan.Id).Where(e => e.Action.StartsWith("succession")).All(e => e.Actor.StartsWith("support:")), Is.True);
    }

    [Test]
    public void AReturningLeaderCancelsTheCase()
    {
        SocialHub hub = Hub(out ManualClock clock);
        Clan clan = ClanWith(hub, 2, out _);
        clock.Advance(TimeSpan.FromDays(31));
        hub.Succession.Open("agent", clan.Id, "inactive", out SuccessionCase sc);
        clock.Advance(TimeSpan.FromDays(2));
        hub.Clans.Activity.Record("leader", clock.UtcNow);
        clock.Advance(TimeSpan.FromDays(6));
        Assert.That(hub.Succession.Approve("agent", sc.Id, "m1", "x"), Is.EqualTo(SuccessionResult.LeaderStillActive));
        Assert.That(sc.State, Is.EqualTo(SuccessionState.CancelledLeaderReturned));
        Assert.That(clan.LeaderId, Is.EqualTo("leader"));
    }

    // ------------------------------------------------------------------ cooperative milestones

    [Test]
    public void IndividualContributionIsCappedSoNoOneUnlocksAlone()
    {
        var grants = new InMemoryGrantLedgerStore();
        SocialHub hub = Hub(out ManualClock clock, grants);
        Assert.That(ClanMilestoneTrack.Default.Validate(ClanRules.Default.MaxMembers), Is.Empty);
        Clan clan = ClanWith(hub, 5, out List<string> members);
        MatchOutcomeReport Report(string p, int i) => new("cm" + i, p, MatchKind.OnlineHuman, PlayerOutcome.Win, MatchEnding.RoundsComplete, clock.UtcNow);
        for (int i = 0; i < 500; i++) hub.ClanMilestones.RecordMatch("S1", Report("leader", i));
        ClanProgressView p = hub.ClanMilestones.Progress(clan.Id, "S1");
        Assert.That(p.Total, Is.EqualTo(ClanMilestoneTrack.Default.PerMemberCap));
        Assert.That(p.ReachedCount, Is.Zero, "one hyperactive member cannot unlock anything");
        Assert.That(hub.ClanMilestones.RecordMatch("S1", Report("leader", 1)), Is.Zero, "duplicate match");
        foreach (string m in members.Skip(1))
            for (int i = 0; i < 40; i++) hub.ClanMilestones.RecordMatch("S1", Report(m, i));
        p = hub.ClanMilestones.Progress(clan.Id, "S1");
        Assert.That(p.Total, Is.EqualTo(125));
        Assert.That(p.ReachedCount, Is.EqualTo(1));
        Assert.That(members.All(m => grants.Find(GrantKeys.ClanMilestone(clan.Id, "S1", "m1", m)) != null), Is.True, "every member receives the cosmetic once");
        int count = grants.Count;
        hub.ClanMilestones.RecordMatch("S1", Report("m1", 999));
        Assert.That(grants.Count, Is.EqualTo(count));
        // Leaving and rejoining does not reset the cap.
        hub.Clans.Leave("m1");
        hub.Clans.Invite("leader", "m1", out ClanInvitation inv);
        hub.Clans.Respond("m1", inv.Id, true);
        Assert.That(hub.ClanMilestones.RecordMatch("S1", Report("m1", 1000)), Is.Zero);
    }

    // ------------------------------------------------------------------ moderation

    [Test]
    public void ReportsQueueByUrgencyAndDeduplicate()
    {
        SocialHub hub = Hub(out _);
        Assert.That(hub.Moderation.Report("r1", ReportTarget.Player, "bad", "bad", ReportReason.Spam, out _), Is.EqualTo(ReportResult.Received));
        Assert.That(hub.Moderation.Report("r1", ReportTarget.Player, "bad", "bad", ReportReason.Harassment, out _), Is.EqualTo(ReportResult.AlreadyReported));
        Assert.That(hub.Moderation.Report("r2", ReportTarget.Avatar, "bad", "bad", ReportReason.ChildSafety, out _), Is.EqualTo(ReportResult.Received));
        Assert.That(hub.Moderation.Report("bad", ReportTarget.Player, "bad", "bad", ReportReason.Spam, out _), Is.EqualTo(ReportResult.Invalid), "self report");
        Assert.That(hub.Moderation.Queue().First().Reason, Is.EqualTo(ReportReason.ChildSafety));
        for (int i = 0; i < hub.Moderation.MaxReportsPerDay - 1; i++) hub.Moderation.Report("r1", ReportTarget.Player, "t" + i, "t" + i, ReportReason.Other, out _);
        Assert.That(hub.Moderation.Report("r1", ReportTarget.Player, "one-more", "one-more", ReportReason.Other, out _), Is.EqualTo(ReportResult.RateLimited));
    }

    [Test]
    public void SuspensionsAreTemporaryAndAppealableToAnotherModerator()
    {
        SocialHub hub = Hub(out ManualClock clock);
        hub.Moderation.Report("r", ReportTarget.Player, "p", "p", ReportReason.Harassment, out ModerationCase c);
        FeatureSuspension s = hub.Moderation.Suspend("modA", c.Id, SocialFeature.ClanChat, TimeSpan.FromDays(90));
        Assert.That(s.Until - s.From, Is.EqualTo(hub.Moderation.MaxSuspension), "capped");
        Assert.That(hub.Moderation.IsSuspended("p", SocialFeature.ClanChat, clock.UtcNow), Is.True);
        Assert.That(hub.Moderation.IsSuspended("p", SocialFeature.FriendInvites, clock.UtcNow), Is.False, "one feature only");
        Assert.That(hub.Moderation.FileAppeal("p", s.Id, "appeal.misunderstanding", out Appeal a), Is.EqualTo(AppealResult.Received));
        Assert.That(hub.Moderation.FileAppeal("p", s.Id, "appeal.misunderstanding", out _), Is.EqualTo(AppealResult.AlreadyAppealed));
        Assert.That(hub.Moderation.ResolveAppeal("modA", a.Id, true, "self review"), Is.False, "the same moderator cannot review");
        Assert.That(hub.Moderation.ResolveAppeal("modB", a.Id, true, "context was banter"), Is.True);
        Assert.That(hub.Moderation.IsSuspended("p", SocialFeature.ClanChat, clock.UtcNow), Is.False);
        Assert.That(hub.Moderation.Audit.Entries.Select(e => e.Action), Does.Contain("appeal.overturn"));
    }

    [Test]
    public void AppealsCloseAfterTheWindow()
    {
        SocialHub hub = Hub(out ManualClock clock);
        hub.Moderation.Report("r", ReportTarget.Player, "p", "p", ReportReason.Harassment, out ModerationCase c);
        FeatureSuspension s = hub.Moderation.Suspend("modA", c.Id, SocialFeature.ClanCreation, TimeSpan.FromDays(30));
        clock.Advance(hub.Moderation.AppealWindow + TimeSpan.FromSeconds(1));
        Assert.That(hub.Moderation.FileAppeal("p", s.Id, "appeal.x", out _), Is.EqualTo(AppealResult.WindowClosed));
    }

    [Test]
    public void SocialFeaturesNeedAReachableSupportOwner()
    {
        var ops = new SocialOperations();
        Assert.That(ops.SocialFeaturesMayLaunch, Is.False);
        Assert.That(ops.MissingForLaunch(), Has.Count.EqualTo(3));
        ops.SupportOwner = "founder";
        ops.SupportContact = "support@example.invalid";
        ops.ResponseProcessUrl = "https://example.invalid/process";
        Assert.That(ops.SocialFeaturesMayLaunch, Is.True);
    }

    [Test]
    public void AbuseFlowRehearsal()
    {
        // Abuse flow: a harasser spams invites and chat, gets blocked, reported, suspended, appeals, the appeal is upheld.
        SocialHub hub = Hub(out ManualClock clock);
        hub.Clans.Create("victim", "Quiet Garden", out _);
        hub.Clans.Invite("victim", "harasser", out ClanInvitation inv);
        hub.Clans.Respond("harasser", inv.Id, true);
        for (int i = 0; i < 6; i++) hub.Chat.Post("harasser", "chat.rematch");
        hub.Friends.Block("victim", "harasser");
        Assert.That(hub.Chat.Feed("victim").Any(e => e.SenderId == "harasser"), Is.False);
        Assert.That(hub.Moderation.Report("victim", ReportTarget.Player, "harasser", "harasser", ReportReason.Harassment, out ModerationCase c), Is.EqualTo(ReportResult.Received));
        FeatureSuspension s = hub.Moderation.Suspend("modA", c.Id, SocialFeature.ClanChat, TimeSpan.FromDays(7));
        Assert.That(hub.Chat.Post("harasser", "chat.hello"), Is.EqualTo(ChatPostResult.Suspended));
        hub.Moderation.FileAppeal("harasser", s.Id, "appeal.misunderstanding", out Appeal a);
        Assert.That(hub.Moderation.ResolveAppeal("modB", a.Id, false, "upheld"), Is.True);
        Assert.That(hub.Moderation.IsSuspended("harasser", SocialFeature.ClanChat, clock.UtcNow), Is.True);
        clock.Advance(TimeSpan.FromDays(7));
        Assert.That(hub.Moderation.IsSuspended("harasser", SocialFeature.ClanChat, clock.UtcNow), Is.False);
        Assert.That(hub.Chat.Feed("victim").Any(e => e.SenderId == "harasser"), Is.False, "the block outlives the suspension");
    }
}
