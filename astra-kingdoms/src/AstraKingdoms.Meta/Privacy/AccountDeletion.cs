using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Economy;

namespace AstraKingdoms.Meta.Privacy
{
    public enum DeletionChannel : byte
    {
        /// <summary>The discoverable in-app path (Profile → Privacy → Delete account).</summary>
        InApp = 0,
        /// <summary>The external web resource (works when the app is uninstalled).</summary>
        Web = 1,
    }

    public enum DeletionState : byte
    {
        /// <summary>The requester must prove account ownership (web request, or expired in-app session).</summary>
        AwaitingVerification = 0,
        InProgress = 1,
        /// <summary>Some stores finished, others (e.g. an outside provider) failed; retried until complete.</summary>
        PartiallyCompleted = 2,
        Completed = 3,
    }

    public enum EraseOutcome : byte
    {
        Erased = 0,
        /// <summary>Kept for a justified, disclosed reason (purchase records), with the account id pseudonymised.</summary>
        RetainedJustified = 1,
        /// <summary>An outside provider could not be reached; retry.</summary>
        ProviderUnavailable = 2,
        Failed = 3,
    }

    /// <summary>One store that holds account data. The server registers one per store/provider.</summary>
    public interface IDataEraser
    {
        string Name { get; }
        DataCategory Category { get; }
        Task<EraseOutcome> EraseAsync(string accountId, CancellationToken ct = default);
    }

    public sealed class DeletionRequest
    {
        public string RequestId { get; }
        public string AccountId { get; }
        public DeletionChannel Channel { get; }
        public DateTimeOffset ReceivedAt { get; }
        /// <summary>The completion window we publish; the request is overdue after it.</summary>
        public DateTimeOffset DueBy { get; }
        public DeletionState State { get; internal set; }
        public IDictionary<string, EraseOutcome> Steps { get; } = new Dictionary<string, EraseOutcome>(StringComparer.Ordinal);
        public DateTimeOffset? CompletedAt { get; internal set; }

        public DeletionRequest(string requestId, string accountId, DeletionChannel channel, DateTimeOffset receivedAt, DateTimeOffset dueBy, DeletionState state)
        {
            RequestId = requestId;
            AccountId = accountId;
            Channel = channel;
            ReceivedAt = receivedAt;
            DueBy = dueBy;
            State = state;
        }
    }

    /// <summary>
    /// Account and associated-data deletion (plan: "Store deletion and purchase readiness"). Clearing a
    /// local save is not deletion: this runs every registered server-side eraser, records each
    /// outcome, keeps retrying outside providers, and reports justified retention.
    /// <para>Server integration: expose <see cref="Request"/> to the in-app path (authenticated) and to
    /// the web form (unauthenticated → verification by sign-in link), <see cref="ConfirmVerification"/>
    /// from the verification link, and run <see cref="ProcessPendingAsync"/> on a schedule. Register
    /// erasers for the server's own stores (match diagnostic records, sessions, friend rooms).</para>
    /// </summary>
    public sealed class AccountDeletionService
    {
        private readonly object _gate = new object();
        private readonly List<IDataEraser> _erasers;
        private readonly IClock _clock;
        private readonly Dictionary<string, DeletionRequest> _requests = new Dictionary<string, DeletionRequest>(StringComparer.Ordinal);

        /// <summary>Published completion window (proposed: 30 days).</summary>
        public TimeSpan CompletionWindow { get; set; } = TimeSpan.FromDays(30);

        public AccountDeletionService(IEnumerable<IDataEraser> erasers, IClock clock)
        {
            _erasers = (erasers ?? throw new ArgumentNullException(nameof(erasers))).ToList();
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// Records a request. One open request per account: a repeat returns the existing one. In-app
        /// requests with a valid session start immediately; web requests and expired sessions wait for
        /// ownership verification (so a stranger cannot delete someone else's account).
        /// </summary>
        public DeletionRequest Request(string accountId, DeletionChannel channel, bool sessionValid)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("account id required", nameof(accountId));
            lock (_gate)
            {
                DeletionRequest open = _requests.Values.FirstOrDefault(r => r.AccountId == accountId && r.State != DeletionState.Completed);
                if (open != null)
                {
                    if (open.State == DeletionState.AwaitingVerification && channel == DeletionChannel.InApp && sessionValid) open.State = DeletionState.InProgress;
                    return open;
                }
                DateTimeOffset now = _clock.UtcNow;
                bool verified = channel == DeletionChannel.InApp && sessionValid;
                var req = new DeletionRequest(Guid.NewGuid().ToString("N"), accountId, channel, now, now + CompletionWindow,
                    verified ? DeletionState.InProgress : DeletionState.AwaitingVerification);
                _requests.Add(req.RequestId, req);
                return req;
            }
        }

        public bool ConfirmVerification(string requestId)
        {
            lock (_gate)
            {
                if (!_requests.TryGetValue(requestId, out DeletionRequest r) || r.State != DeletionState.AwaitingVerification) return false;
                r.State = DeletionState.InProgress;
                return true;
            }
        }

        public DeletionRequest Get(string requestId)
        {
            lock (_gate) return _requests.TryGetValue(requestId, out DeletionRequest r) ? r : null;
        }

        /// <summary>Runs every eraser that has not finished for this request. Safe to repeat.</summary>
        public async Task<DeletionRequest> ProcessAsync(string requestId, CancellationToken ct = default)
        {
            DeletionRequest r = Get(requestId);
            if (r == null || r.State == DeletionState.AwaitingVerification || r.State == DeletionState.Completed) return r;
            foreach (IDataEraser e in _erasers)
            {
                if (r.Steps.TryGetValue(e.Name, out EraseOutcome done) && (done == EraseOutcome.Erased || done == EraseOutcome.RetainedJustified)) continue;
                EraseOutcome outcome;
                try
                {
                    outcome = await e.EraseAsync(r.AccountId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    outcome = EraseOutcome.Failed;
                }
                lock (_gate) r.Steps[e.Name] = outcome;
            }
            lock (_gate)
            {
                bool all = _erasers.All(e => r.Steps.TryGetValue(e.Name, out EraseOutcome o) && (o == EraseOutcome.Erased || o == EraseOutcome.RetainedJustified));
                r.State = all ? DeletionState.Completed : DeletionState.PartiallyCompleted;
                if (all) r.CompletedAt = _clock.UtcNow;
            }
            return r;
        }

        public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
        {
            List<string> ids;
            lock (_gate) ids = _requests.Values.Where(r => r.State == DeletionState.InProgress || r.State == DeletionState.PartiallyCompleted).Select(r => r.RequestId).ToList();
            foreach (string id in ids) await ProcessAsync(id, ct).ConfigureAwait(false);
            return ids.Count;
        }

        /// <summary>Requests past their published window (for the operator's alert).</summary>
        public IReadOnlyList<DeletionRequest> Overdue()
        {
            lock (_gate) return _requests.Values.Where(r => r.State != DeletionState.Completed && _clock.UtcNow > r.DueBy).ToArray();
        }
    }

    /// <summary>Erasers for the meta stores in this assembly.</summary>
    public static class MetaErasers
    {
        private sealed class Delegated : IDataEraser
        {
            private readonly Func<string, EraseOutcome> _erase;

            public Delegated(string name, DataCategory category, Func<string, EraseOutcome> erase)
            {
                Name = name;
                Category = category;
                _erase = erase;
            }

            public string Name { get; }
            public DataCategory Category { get; }
            public Task<EraseOutcome> EraseAsync(string accountId, CancellationToken ct = default) => Task.FromResult(_erase(accountId));
        }

        public static IDataEraser RewardLedger(IRewardLedgerStore store) =>
            new Delegated("progression-ledger", DataCategory.ProgressionLedger, id => { store.DeletePlayer(id); return EraseOutcome.Erased; });

        public static IDataEraser DailyTasks(IDailyTaskProgressStore store) =>
            new Delegated("daily-task-progress", DataCategory.DailyTaskProgress, id => { store.DeletePlayer(id); return EraseOutcome.Erased; });

        public static IDataEraser Equipment(IEquipmentStore store) =>
            new Delegated("cosmetic-equipment", DataCategory.CosmeticEquipment, id => { store.DeletePlayer(id); return EraseOutcome.Erased; });

        public static IDataEraser AdTickets(IAdTicketStore store) =>
            new Delegated("ad-offer-tickets", DataCategory.AdOfferTicket, id => { store.DeletePlayer(id); return EraseOutcome.Erased; });

        /// <summary>Purchase records are retained for accounting/tax/fraud, re-keyed to a pseudonym (disclosed in the data map).</summary>
        public static IDataEraser Entitlements(IEntitlementLedgerStore store, Func<string, string> pseudonym) =>
            new Delegated("purchase-records", DataCategory.PurchaseRecord, id =>
            {
                store.Pseudonymise(id, pseudonym(id));
                return EraseOutcome.RetainedJustified;
            });
    }

    /// <summary>
    /// Where players find deletion, privacy and grievance routes. The URLs are configuration owned by
    /// the operator; release validation refuses placeholders.
    /// </summary>
    public sealed class PrivacyLinks
    {
        /// <summary>External deletion request page (Play Console "Delete account URL").</summary>
        public string AccountDeletionUrl { get; set; } = "https://example.invalid/astra-kingdoms/delete-account";
        public string PrivacyPolicyUrl { get; set; } = "https://example.invalid/astra-kingdoms/privacy";
        /// <summary>Grievance contact route (India Online Gaming Rules 2026, rule 20).</summary>
        public string GrievanceUrl { get; set; } = "https://example.invalid/astra-kingdoms/grievance";
        /// <summary>The in-app path, as written in the store listing and the help page.</summary>
        public string InAppPath { get; set; } = "Home → Profile → Privacy & account → Delete account";

        public IReadOnlyList<string> ValidateForRelease()
        {
            var errors = new List<string>();
            foreach (KeyValuePair<string, string> u in new Dictionary<string, string>
                     {
                         { "account deletion", AccountDeletionUrl }, { "privacy policy", PrivacyPolicyUrl }, { "grievance", GrievanceUrl },
                     })
            {
                if (!Uri.TryCreate(u.Value, UriKind.Absolute, out Uri uri) || uri.Scheme != "https") errors.Add(u.Key + " URL must be https");
                else if (uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) || uri.Host == "example.com")
                    errors.Add(u.Key + " URL is still a placeholder");
            }
            if (string.IsNullOrWhiteSpace(InAppPath)) errors.Add("in-app deletion path must be described");
            return errors;
        }
    }
}
