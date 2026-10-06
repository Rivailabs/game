using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Economy;

namespace AstraKingdoms.Meta.Ads
{
    public enum AdLoadResult : byte
    {
        Loaded = 0,
        NoFill = 1,
        Error = 2,
    }

    /// <summary>What the SDK reported on the device. Never a reason to grant anything by itself.</summary>
    public enum AdShowOutcome : byte
    {
        /// <summary>The SDK says the reward was earned; the server callback is still what grants.</summary>
        EarnedReward = 0,
        /// <summary>Closed early / declined.</summary>
        Dismissed = 1,
        Failed = 2,
        NotLoaded = 3,
    }

    /// <summary>
    /// Client-side rewarded-ad SDK boundary (ticket 63). An adapter for the chosen network implements
    /// it; tests and the editor use <see cref="FakeRewardedAdProvider"/>. The server-side verification
    /// user id and custom data are passed through to the network's signed callback.
    /// </summary>
    public interface IRewardedAdProvider
    {
        bool IsReady { get; }
        Task<AdLoadResult> LoadAsync(AdRequestOptions options, CancellationToken ct = default);
        Task<AdShowOutcome> ShowAsync(string ssvUserId, string ssvCustomData, CancellationToken ct = default);
    }

    /// <summary>Scripted provider for tests and editor runs. It never calls the server callback itself.</summary>
    public sealed class FakeRewardedAdProvider : IRewardedAdProvider
    {
        public AdLoadResult NextLoad { get; set; } = AdLoadResult.Loaded;
        public AdShowOutcome NextShow { get; set; } = AdShowOutcome.EarnedReward;
        public AdRequestOptions LastOptions { get; private set; }
        public string LastUserId { get; private set; }
        public string LastCustomData { get; private set; }
        public int ShowCount { get; private set; }
        public bool IsReady { get; private set; }

        public Task<AdLoadResult> LoadAsync(AdRequestOptions options, CancellationToken ct = default)
        {
            LastOptions = options;
            IsReady = NextLoad == AdLoadResult.Loaded;
            return Task.FromResult(NextLoad);
        }

        public Task<AdShowOutcome> ShowAsync(string ssvUserId, string ssvCustomData, CancellationToken ct = default)
        {
            if (!IsReady) return Task.FromResult(AdShowOutcome.NotLoaded);
            IsReady = false;
            ShowCount++;
            LastUserId = ssvUserId;
            LastCustomData = ssvCustomData;
            return Task.FromResult(NextShow);
        }
    }

    /// <summary>The parameters of a signature-verified server-side-verification callback.</summary>
    public sealed class SsvCallback
    {
        public string TransactionId { get; }
        public string UserId { get; }
        public string CustomData { get; }
        public string AdUnit { get; }
        public int RewardAmount { get; }
        public string RewardItem { get; }
        public DateTimeOffset Timestamp { get; }

        public SsvCallback(string transactionId, string userId, string customData, string adUnit, int rewardAmount, string rewardItem, DateTimeOffset timestamp)
        {
            TransactionId = transactionId;
            UserId = userId;
            CustomData = customData;
            AdUnit = adUnit;
            RewardAmount = rewardAmount;
            RewardItem = rewardItem;
            Timestamp = timestamp;
        }
    }

    public enum TicketStatus : byte
    {
        Open = 0,
        Rewarded = 1,
        Expired = 2,
        Unknown = 3,
    }

    /// <summary>A server-issued, single-use offer. Its id travels as the SSV custom data.</summary>
    public sealed class AdOfferTicket
    {
        public string TicketId { get; }
        public string PlayerId { get; }
        public DateTimeOffset IssuedAt { get; }
        public DateTimeOffset ExpiresAt { get; }
        public AdRequestOptions RequestOptions { get; }

        public AdOfferTicket(string ticketId, string playerId, DateTimeOffset issuedAt, DateTimeOffset expiresAt, AdRequestOptions options)
        {
            TicketId = ticketId;
            PlayerId = playerId;
            IssuedAt = issuedAt;
            ExpiresAt = expiresAt;
            RequestOptions = options;
        }
    }

    public interface IAdTicketStore
    {
        void Add(AdOfferTicket ticket);
        AdOfferTicket Find(string ticketId);
        int DeletePlayer(string playerId);
    }

    public sealed class InMemoryAdTicketStore : IAdTicketStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, AdOfferTicket> _tickets = new Dictionary<string, AdOfferTicket>(StringComparer.Ordinal);

        public void Add(AdOfferTicket ticket)
        {
            lock (_gate) _tickets.Add(ticket.TicketId, ticket);
        }

        public AdOfferTicket Find(string ticketId)
        {
            lock (_gate) return ticketId != null && _tickets.TryGetValue(ticketId, out AdOfferTicket t) ? t : null;
        }

        public int DeletePlayer(string playerId)
        {
            lock (_gate)
            {
                List<string> ids = _tickets.Values.Where(t => t.PlayerId == playerId).Select(t => t.TicketId).ToList();
                foreach (string id in ids) _tickets.Remove(id);
                return ids.Count;
            }
        }
    }

    /// <summary>Lets the ad service ask the match service whether a player is in a live match.</summary>
    public interface IMatchPresence
    {
        bool IsInActiveMatch(string playerId);
    }

    public sealed class OfferResult
    {
        public OfferDecision Decision { get; }
        public AdOfferTicket Ticket { get; }

        public OfferResult(OfferDecision decision, AdOfferTicket ticket)
        {
            Decision = decision;
            Ticket = ticket;
        }
    }

    public enum CallbackOutcome : byte
    {
        Granted = 0,
        /// <summary>Same ticket/transaction already rewarded: acknowledged, nothing new granted.</summary>
        Duplicate = 1,
        UnknownTicket = 2,
        WrongUser = 3,
        TicketExpired = 4,
        DailyCapReached = 5,
    }

    /// <summary>
    /// Ticket 63, server side: issues single-use offers only outside matches, grants cosmetic coins
    /// once per verified callback/ticket, and never grants on a decline or failure (no callback).
    /// <para>Server integration: <see cref="IssueOffer"/> and <see cref="GetTicketStatus"/> are
    /// authenticated player endpoints. The network's SSV callback URL is an unauthenticated endpoint
    /// that first runs <c>AstraKingdoms.Meta.Server.SsvSignatureVerifier</c> on the raw query string
    /// and passes only verified callbacks to <see cref="HandleVerifiedCallback"/>. Always answer the
    /// network with HTTP 200 for verified callbacks (including duplicates) so it stops retrying.</para>
    /// </summary>
    public sealed class RewardedAdService
    {
        private readonly IRewardLedgerStore _ledger;
        private readonly IAdTicketStore _tickets;
        private readonly IMatchPresence _presence;
        private readonly IClock _clock;
        private readonly AdPolicy _policy;
        private readonly DailyResetPolicy _reset;

        /// <summary>How long an offer stays open.</summary>
        public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromMinutes(15);
        /// <summary>Extra time for the network's callback to arrive after the offer closed.</summary>
        public TimeSpan CallbackGrace { get; set; } = TimeSpan.FromMinutes(15);

        public event Action<string, CallbackOutcome> RewardStateChanged;

        public RewardedAdService(IRewardLedgerStore ledger, IAdTicketStore tickets, IMatchPresence presence, IClock clock, AdPolicy policy,
            DailyResetPolicy reset = null)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
            _presence = presence ?? throw new ArgumentNullException(nameof(presence));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _reset = reset ?? DailyResetPolicy.IndiaStandardTime;
        }

        public static string GrantKey(string ticketId) => "ad-ticket:" + ticketId;

        /// <summary>Verified rewards today (by the authority's day).</summary>
        public int RewardedToday(string playerId)
        {
            string today = _reset.DayKey(_clock.UtcNow);
            return _ledger.Entries(playerId).Count(e => e.Source == LedgerSource.RewardedAd && _reset.DayKey(e.At) == today);
        }

        public OfferResult IssueOffer(string playerId, ScreenContext context, AudienceProfile audience, bool personalisedAdsConsent)
        {
            if (_presence.IsInActiveMatch(playerId)) return new OfferResult(OfferDecision.InMatch, null);
            OfferDecision d = AdRules.CanOfferRewarded(_policy, context, audience, RewardedToday(playerId));
            if (d != OfferDecision.Available) return new OfferResult(d, null);
            DateTimeOffset now = _clock.UtcNow;
            var ticket = new AdOfferTicket(NewTicketId(), playerId, now, now + TicketLifetime, AdRequestOptions.For(audience, personalisedAdsConsent));
            _tickets.Add(ticket);
            return new OfferResult(OfferDecision.Available, ticket);
        }

        public TicketStatus GetTicketStatus(string playerId, string ticketId)
        {
            AdOfferTicket t = _tickets.Find(ticketId);
            if (t == null || t.PlayerId != playerId) return TicketStatus.Unknown;
            if (_ledger.Find(GrantKey(ticketId)) != null) return TicketStatus.Rewarded;
            return _clock.UtcNow >= t.ExpiresAt + CallbackGrace ? TicketStatus.Expired : TicketStatus.Open;
        }

        /// <summary>Grants for a callback whose signature was already verified.</summary>
        public CallbackOutcome HandleVerifiedCallback(SsvCallback cb)
        {
            CallbackOutcome outcome = HandleCore(cb);
            RewardStateChanged?.Invoke(cb?.UserId, outcome);
            return outcome;
        }

        private CallbackOutcome HandleCore(SsvCallback cb)
        {
            if (cb == null) throw new ArgumentNullException(nameof(cb));
            AdOfferTicket t = _tickets.Find(cb.CustomData);
            if (t == null) return CallbackOutcome.UnknownTicket;
            if (t.PlayerId != cb.UserId) return CallbackOutcome.WrongUser;
            if (_ledger.Find(GrantKey(t.TicketId)) != null) return CallbackOutcome.Duplicate;
            if (_clock.UtcNow >= t.ExpiresAt + CallbackGrace) return CallbackOutcome.TicketExpired;

            string today = _reset.DayKey(_clock.UtcNow);
            int cap = _policy.MaxRewardedPerDay;
            var entry = new RewardLedgerEntry(GrantKey(t.TicketId), t.PlayerId, LedgerSource.RewardedAd, 0, _policy.RewardedCoins, _clock.UtcNow,
                reference: "txn:" + cb.TransactionId);
            LedgerAppendResult r = _ledger.TryAppend(entry, totals =>
                _ledger.Entries(t.PlayerId).Count(e => e.Source == LedgerSource.RewardedAd && _reset.DayKey(e.At) == today) >= cap ? "daily cap" : null);
            if (r.Status == AppendStatus.Duplicate) return CallbackOutcome.Duplicate;
            if (r.Status == AppendStatus.Rejected) return CallbackOutcome.DailyCapReached;
            return CallbackOutcome.Granted;
        }

        private static string NewTicketId()
        {
            byte[] bytes = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
