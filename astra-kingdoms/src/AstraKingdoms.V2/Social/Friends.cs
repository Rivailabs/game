using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Social
{
    /// <summary>Versioned social constants (PROPOSED).</summary>
    public sealed class SocialRules
    {
        public const string CurrentVersion = "AK-SOCIAL-1";

        public string Version { get; } = CurrentVersion;
        public TimeSpan FriendInviteTtl { get; set; } = TimeSpan.FromDays(7);
        public int MaxFriends { get; set; } = 200;
        public int MaxPendingOutgoing { get; set; } = 30;

        public static readonly SocialRules Default = new SocialRules();
    }

    public enum PresenceState : byte
    {
        Offline = 0,
        Online = 1,
        InMatch = 2,
    }

    public enum InviteState : byte
    {
        Pending = 0,
        Accepted = 1,
        Declined = 2,
        Expired = 3,
        /// <summary>Withdrawn by the system because one side blocked the other.</summary>
        Withdrawn = 4,
    }

    public sealed class FriendInvitation
    {
        public string Id { get; }
        public string From { get; }
        public string To { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset ExpiresAt { get; }
        public InviteState State { get; internal set; }

        public FriendInvitation(string id, string from, string to, DateTimeOffset createdAt, DateTimeOffset expiresAt)
        {
            Id = id;
            From = from;
            To = to;
            CreatedAt = createdAt;
            ExpiresAt = expiresAt;
            State = InviteState.Pending;
        }

        public bool IsOpen(DateTimeOffset now) => State == InviteState.Pending && now < ExpiresAt;
    }

    public enum FriendInviteResult : byte
    {
        /// <summary>
        /// Shown to the sender. Also returned (with nothing stored or delivered) when a block exists, so
        /// a blocked account cannot discover the block by probing friend codes.
        /// </summary>
        Sent = 0,
        InvalidCode = 1,
        Self = 2,
        AlreadyFriends = 3,
        AlreadyPending = 4,
        Suspended = 5,
        LimitReached = 6,
    }

    public enum FriendResponseResult : byte
    {
        Accepted = 0,
        Declined = 1,
        Expired = 2,
        NotFound = 3,
        LimitReached = 4,
    }

    /// <summary>
    /// Friends through friend codes and explicit acceptance (plan: no public contact discovery).
    /// Invitations expire; a block suppresses invitations, presence and every social interaction with
    /// that account, in both directions, without telling the blocked account.
    /// </summary>
    public sealed class FriendService
    {
        /// <summary>Crockford base32: no I, L, O or U, so codes are easy to read aloud.</summary>
        public const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public const int CodeLength = 8;

        private readonly object _gate = new object();
        private readonly SocialRules _rules;
        private readonly IFeatureGate _gateFeatures;
        private readonly IClock _clock;
        private readonly DeterministicRandom _codeRandom;
        private readonly Dictionary<string, string> _codeByPlayer = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _playerByCode = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _friends = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _blocks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, FriendInvitation> _invites = new Dictionary<string, FriendInvitation>(StringComparer.Ordinal);
        private readonly Dictionary<string, PresenceState> _presence = new Dictionary<string, PresenceState>(StringComparer.Ordinal);
        private int _inviteCounter;

        /// <param name="codeSeed">Server secret seeding code generation (codes are not derivable from account ids).</param>
        public FriendService(SocialRules rules, IFeatureGate gate, IClock clock, string codeSeed)
        {
            _rules = rules ?? SocialRules.Default;
            _gateFeatures = gate ?? OpenFeatureGate.Instance;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _codeRandom = DeterministicRandom.FromText(codeSeed ?? throw new ArgumentNullException(nameof(codeSeed)));
        }

        // ------------------------------------------------------------------ codes

        /// <summary>The player's friend code ("ABCD-2345"), created on first use.</summary>
        public string CodeFor(string playerId)
        {
            lock (_gate)
            {
                if (_codeByPlayer.TryGetValue(playerId, out string code)) return Format(code);
                return Format(AssignCode(playerId));
            }
        }

        /// <summary>Issues a new code; the old one stops working (e.g. after it was shared too widely).</summary>
        public string RegenerateCode(string playerId)
        {
            lock (_gate)
            {
                if (_codeByPlayer.TryGetValue(playerId, out string old)) _playerByCode.Remove(old);
                _codeByPlayer.Remove(playerId);
                return Format(AssignCode(playerId));
            }
        }

        private string AssignCode(string playerId)
        {
            string code;
            do
            {
                var sb = new StringBuilder(CodeLength);
                for (int i = 0; i < CodeLength; i++) sb.Append(CodeAlphabet[_codeRandom.NextInt(CodeAlphabet.Length)]);
                code = sb.ToString();
            } while (_playerByCode.ContainsKey(code));
            _codeByPlayer[playerId] = code;
            _playerByCode[code] = playerId;
            return code;
        }

        private static string Format(string code) => code.Substring(0, 4) + "-" + code.Substring(4);

        /// <summary>Normalises typed input: case, dashes/spaces, and the look-alikes O→0, I/L→1.</summary>
        public static string Normalise(string typed)
        {
            if (typed == null) return null;
            var sb = new StringBuilder();
            foreach (char raw in typed.ToUpperInvariant())
            {
                char c = raw == 'O' ? '0' : raw == 'I' || raw == 'L' ? '1' : raw;
                if (c == '-' || c == ' ') continue;
                if (CodeAlphabet.IndexOf(c) < 0) return null;
                sb.Append(c);
            }
            return sb.Length == CodeLength ? sb.ToString() : null;
        }

        // ------------------------------------------------------------------ invitations

        public FriendInviteResult Invite(string fromPlayer, string typedCode)
        {
            DateTimeOffset now = _clock.UtcNow;
            if (_gateFeatures.IsSuspended(fromPlayer, SocialFeature.FriendInvites, now)) return FriendInviteResult.Suspended;
            string code = Normalise(typedCode);
            lock (_gate)
            {
                if (code == null || !_playerByCode.TryGetValue(code, out string to)) return FriendInviteResult.InvalidCode;
                if (to == fromPlayer) return FriendInviteResult.Self;
                if (BlockedEitherWayLocked(fromPlayer, to)) return FriendInviteResult.Sent; // silently dropped
                if (FriendsLocked(fromPlayer).Contains(to)) return FriendInviteResult.AlreadyFriends;
                if (_invites.Values.Any(i => i.IsOpen(now) && ((i.From == fromPlayer && i.To == to) || (i.From == to && i.To == fromPlayer))))
                    return FriendInviteResult.AlreadyPending;
                if (_invites.Values.Count(i => i.From == fromPlayer && i.IsOpen(now)) >= _rules.MaxPendingOutgoing) return FriendInviteResult.LimitReached;
                if (FriendsLocked(fromPlayer).Count >= _rules.MaxFriends) return FriendInviteResult.LimitReached;
                string id = "fi-" + (++_inviteCounter).ToString(CultureInfo.InvariantCulture);
                _invites[id] = new FriendInvitation(id, fromPlayer, to, now, now + _rules.FriendInviteTtl);
                return FriendInviteResult.Sent;
            }
        }

        /// <summary>Open invitations addressed to the player (expired and blocked senders excluded).</summary>
        public IReadOnlyList<FriendInvitation> Incoming(string playerId)
        {
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                ExpireLocked(now);
                return _invites.Values.Where(i => i.To == playerId && i.IsOpen(now) && !BlockedEitherWayLocked(i.From, i.To))
                    .OrderBy(i => i.CreatedAt).ToArray();
            }
        }

        public IReadOnlyList<FriendInvitation> Outgoing(string playerId)
        {
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                ExpireLocked(now);
                return _invites.Values.Where(i => i.From == playerId && i.IsOpen(now)).OrderBy(i => i.CreatedAt).ToArray();
            }
        }

        /// <summary>Explicit acceptance or decline by the recipient only.</summary>
        public FriendResponseResult Respond(string playerId, string invitationId, bool accept)
        {
            DateTimeOffset now = _clock.UtcNow;
            lock (_gate)
            {
                if (!_invites.TryGetValue(invitationId, out FriendInvitation inv) || inv.To != playerId) return FriendResponseResult.NotFound;
                if (inv.State == InviteState.Pending && now >= inv.ExpiresAt) inv.State = InviteState.Expired;
                if (inv.State == InviteState.Expired) return FriendResponseResult.Expired;
                if (inv.State != InviteState.Pending || BlockedEitherWayLocked(inv.From, inv.To)) return FriendResponseResult.NotFound;
                if (!accept)
                {
                    inv.State = InviteState.Declined;
                    return FriendResponseResult.Declined;
                }
                if (FriendsLocked(inv.From).Count >= _rules.MaxFriends || FriendsLocked(inv.To).Count >= _rules.MaxFriends) return FriendResponseResult.LimitReached;
                inv.State = InviteState.Accepted;
                FriendsLocked(inv.From).Add(inv.To);
                FriendsLocked(inv.To).Add(inv.From);
                return FriendResponseResult.Accepted;
            }
        }

        private void ExpireLocked(DateTimeOffset now)
        {
            foreach (FriendInvitation i in _invites.Values)
                if (i.State == InviteState.Pending && now >= i.ExpiresAt) i.State = InviteState.Expired;
        }

        // ------------------------------------------------------------------ friends

        public IReadOnlyList<string> Friends(string playerId)
        {
            lock (_gate) return FriendsLocked(playerId).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }

        public bool AreFriends(string a, string b)
        {
            lock (_gate) return FriendsLocked(a).Contains(b) && !BlockedEitherWayLocked(a, b);
        }

        public bool Remove(string playerId, string friendId)
        {
            lock (_gate)
            {
                bool removed = FriendsLocked(playerId).Remove(friendId);
                FriendsLocked(friendId).Remove(playerId);
                return removed;
            }
        }

        private HashSet<string> FriendsLocked(string playerId)
        {
            if (!_friends.TryGetValue(playerId, out HashSet<string> set)) _friends[playerId] = set = new HashSet<string>(StringComparer.Ordinal);
            return set;
        }

        // ------------------------------------------------------------------ blocks

        /// <summary>
        /// Blocks <paramref name="target"/>: ends the friendship, withdraws pending invitations in both
        /// directions, hides presence both ways, and makes every other social service (clan invites,
        /// chat, visits, matchmaking) treat the pair as blocked.
        /// </summary>
        public void Block(string blocker, string target)
        {
            if (blocker == target) return;
            lock (_gate)
            {
                if (!_blocks.TryGetValue(blocker, out HashSet<string> set)) _blocks[blocker] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(target);
                FriendsLocked(blocker).Remove(target);
                FriendsLocked(target).Remove(blocker);
                foreach (FriendInvitation i in _invites.Values)
                    if (i.State == InviteState.Pending && ((i.From == blocker && i.To == target) || (i.From == target && i.To == blocker)))
                        i.State = InviteState.Withdrawn;
            }
        }

        /// <summary>Lifts a block. The previous friendship is not restored; a new invitation is needed.</summary>
        public bool Unblock(string blocker, string target)
        {
            lock (_gate) return _blocks.TryGetValue(blocker, out HashSet<string> set) && set.Remove(target);
        }

        public bool HasBlocked(string blocker, string target)
        {
            lock (_gate) return _blocks.TryGetValue(blocker, out HashSet<string> set) && set.Contains(target);
        }

        public IReadOnlyList<string> BlockedBy(string blocker)
        {
            lock (_gate) return _blocks.TryGetValue(blocker, out HashSet<string> set) ? set.OrderBy(x => x, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        }

        public bool IsBlockedEitherWay(string a, string b)
        {
            lock (_gate) return BlockedEitherWayLocked(a, b);
        }

        private bool BlockedEitherWayLocked(string a, string b) =>
            (_blocks.TryGetValue(a, out HashSet<string> x) && x.Contains(b)) || (_blocks.TryGetValue(b, out HashSet<string> y) && y.Contains(a));

        // ------------------------------------------------------------------ presence

        public void SetPresence(string playerId, PresenceState state)
        {
            lock (_gate) _presence[playerId] = state;
        }

        /// <summary>
        /// What <paramref name="viewer"/> may see of <paramref name="target"/>: null (nothing at all)
        /// unless they are friends and neither has blocked the other.
        /// </summary>
        public PresenceState? PresenceAs(string viewer, string target)
        {
            lock (_gate)
            {
                if (viewer != target && (!FriendsLocked(viewer).Contains(target) || BlockedEitherWayLocked(viewer, target))) return null;
                return _presence.TryGetValue(target, out PresenceState s) ? s : PresenceState.Offline;
            }
        }

        /// <summary>Account deletion: code, friendships (both sides), blocks, invitations and presence.</summary>
        public int DeletePlayer(string playerId)
        {
            lock (_gate)
            {
                int n = 0;
                if (_codeByPlayer.TryGetValue(playerId, out string code)) { _playerByCode.Remove(code); _codeByPlayer.Remove(playerId); n++; }
                foreach (string f in FriendsLocked(playerId).ToList()) { FriendsLocked(f).Remove(playerId); n++; }
                _friends.Remove(playerId);
                if (_blocks.Remove(playerId)) n++;
                foreach (HashSet<string> set in _blocks.Values) set.Remove(playerId);
                foreach (string id in _invites.Values.Where(i => i.From == playerId || i.To == playerId).Select(i => i.Id).ToList()) { _invites.Remove(id); n++; }
                _presence.Remove(playerId);
                return n;
            }
        }
    }
}
