using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Social
{
    /// <summary>
    /// Translated preset messages and emotes (plan: "Start with translated preset messages and
    /// emotes"). The API accepts only these ids, so free text cannot be posted at all; each id is a
    /// localization key, so every reader sees the message in their own language.
    /// </summary>
    public static class PresetMessages
    {
        public static readonly IReadOnlyList<string> Messages = new[]
        {
            "chat.hello", "chat.goodGame", "chat.wellPlayed", "chat.thanks", "chat.rematch", "chat.practiceTonight",
            "chat.joinEvent", "chat.niceKingdom", "chat.goodLuck", "chat.brb", "chat.seeYou",
        };

        public static readonly IReadOnlyList<string> Emotes = new[]
        {
            "emote.wave", "emote.bow", "emote.cheer", "emote.laugh", "emote.thinking", "emote.namaste",
        };

        public static readonly IReadOnlyList<string> SystemNotices = new[]
        {
            "clan.notice.successionOpened", "clan.notice.successionApproved", "clan.notice.successionClosed", "clan.notice.milestoneReached",
        };

        public static bool IsPlayerPreset(string id) => id != null && (Messages.Contains(id) || Emotes.Contains(id));
    }

    public sealed class ChatEntry
    {
        public long Sequence { get; }
        public string ClanId { get; }
        /// <summary>Null for a system notice.</summary>
        public string SenderId { get; }
        public string PresetId { get; }
        public DateTimeOffset At { get; }

        public ChatEntry(long sequence, string clanId, string senderId, string presetId, DateTimeOffset at)
        {
            Sequence = sequence;
            ClanId = clanId;
            SenderId = senderId;
            PresetId = presetId;
            At = at;
        }

        public bool IsSystem => SenderId == null;
    }

    public enum ChatPostResult : byte
    {
        Posted = 0,
        NotMember = 1,
        UnknownPreset = 2,
        Suspended = 3,
        RateLimited = 4,
    }

    /// <summary>Clan preset chat. A block hides the blocked sender's posts from the blocker's feed (and vice versa).</summary>
    public sealed class ClanChatService
    {
        private readonly object _gate = new object();
        private readonly Func<string, string> _clanOf;
        private readonly Func<string, string, bool> _blockedEitherWay;
        private readonly IFeatureGate _features;
        private readonly IClock _clock;
        private readonly List<ChatEntry> _entries = new List<ChatEntry>();

        /// <summary>PROPOSED rate limit: posts per sender per minute.</summary>
        public int MaxPostsPerMinute { get; set; } = 6;
        /// <summary>Feed history kept per clan.</summary>
        public int HistoryPerClan { get; set; } = 200;

        public ClanChatService(Func<string, string> clanOfPlayer, Func<string, string, bool> blockedEitherWay, IFeatureGate features, IClock clock)
        {
            _clanOf = clanOfPlayer ?? throw new ArgumentNullException(nameof(clanOfPlayer));
            _blockedEitherWay = blockedEitherWay ?? ((a, b) => false);
            _features = features ?? OpenFeatureGate.Instance;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public ChatPostResult Post(string senderId, string presetId)
        {
            DateTimeOffset now = _clock.UtcNow;
            if (!PresetMessages.IsPlayerPreset(presetId)) return ChatPostResult.UnknownPreset;
            string clanId = _clanOf(senderId);
            if (clanId == null) return ChatPostResult.NotMember;
            if (_features.IsSuspended(senderId, SocialFeature.ClanChat, now)) return ChatPostResult.Suspended;
            lock (_gate)
            {
                if (_entries.Count(e => e.SenderId == senderId && now - e.At < TimeSpan.FromMinutes(1)) >= MaxPostsPerMinute) return ChatPostResult.RateLimited;
                AddLocked(clanId, senderId, presetId, now);
                return ChatPostResult.Posted;
            }
        }

        internal void PostSystemNotice(string clanId, string noticeId)
        {
            lock (_gate) AddLocked(clanId, null, noticeId, _clock.UtcNow);
        }

        private void AddLocked(string clanId, string senderId, string presetId, DateTimeOffset now)
        {
            _entries.Add(new ChatEntry(_entries.Count == 0 ? 1 : _entries[_entries.Count - 1].Sequence + 1, clanId, senderId, presetId, now));
            List<ChatEntry> clanEntries = _entries.Where(e => e.ClanId == clanId).ToList();
            for (int i = 0; i < clanEntries.Count - HistoryPerClan; i++) _entries.Remove(clanEntries[i]);
        }

        /// <summary>The viewer's clan feed, newest last, without posts from accounts either side has blocked.</summary>
        public IReadOnlyList<ChatEntry> Feed(string viewerId)
        {
            string clanId = _clanOf(viewerId);
            if (clanId == null) return Array.Empty<ChatEntry>();
            lock (_gate)
                return _entries.Where(e => e.ClanId == clanId && (e.IsSystem || e.SenderId == viewerId || !_blockedEitherWay(viewerId, e.SenderId))).ToArray();
        }

        /// <summary>System feed entries of a clan (tests and support tools).</summary>
        public IReadOnlyList<ChatEntry> Notices(string clanId)
        {
            lock (_gate) return _entries.Where(e => e.ClanId == clanId && e.IsSystem).ToArray();
        }

        public int DeletePlayer(string playerId)
        {
            lock (_gate) return _entries.RemoveAll(e => e.SenderId == playerId);
        }
    }
}
