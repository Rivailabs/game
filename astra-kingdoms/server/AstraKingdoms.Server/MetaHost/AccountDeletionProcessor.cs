using System.Security.Cryptography;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Economy;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Privacy;
using AstraKingdoms.Server.Identity;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Storage;
using Microsoft.Extensions.Options;

namespace AstraKingdoms.Server.MetaHost;

/// <summary>One step's result, including what was kept and why.</summary>
public sealed record EraseStep(EraseOutcome Outcome, int Rows = 0, string Justification = null);

/// <summary>One store that holds account data, erased (or, with a disclosed reason, retained pseudonymised).</summary>
public interface IAccountEraser
{
    string Name { get; }
    DataCategory Category { get; }
    Task<EraseStep> EraseAsync(string uid, CancellationToken ct);
}

/// <summary>
/// Durable account and associated-data deletion (plan: "Store deletion and purchase readiness"; the
/// meta library's <see cref="AccountDeletionService"/> semantics over SQLite so requests survive
/// restarts). Two channels:
/// <list type="bullet">
/// <item><b>In-app</b> (authenticated): starts immediately.</item>
/// <item><b>Web</b> (the Play Console "delete account URL", works after uninstall): without a valid
/// sign-in the request waits for ownership verification, which the requester gives by signing in
/// on the web page (<c>confirm</c> with their ID token). A stranger therefore cannot delete someone
/// else's account.</item>
/// </list>
/// Every eraser is idempotent and retried until all succeed (or are retained with a disclosed
/// justification). Retained records are written to <c>deletion_retained</c>; when the request
/// completes, the account id is removed from the request row itself.
/// </summary>
public sealed class AccountDeletionProcessor
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MetaSqliteStore _store;
    private readonly TimeProvider _time;
    private readonly MetaPrivacyOptions _options;
    private readonly IReadOnlyList<IAccountEraser> _erasers;
    private readonly ILogger<AccountDeletionProcessor> _log;

    public AccountDeletionProcessor(MetaSqliteStore store, SqliteStore matches, MatchRegistry registry, TimeProvider time,
        IOptions<ServerOptions> options, ILogger<AccountDeletionProcessor> log)
    {
        _store = store;
        _time = time;
        _options = options.Value.Meta.Privacy;
        _log = log;
        _erasers = DefaultErasers(store, matches, registry);
    }

    public IReadOnlyList<IAccountEraser> Erasers => _erasers;

    /// <summary>The pseudonym that replaces an account id in records kept after deletion.</summary>
    public static string Pseudonym(string uid) => "deleted:" + PlayerRef.Of(uid);

    /// <summary>
    /// Records a request. One open request per account: a repeat returns it. <paramref name="uid"/>
    /// is null for an unverified web request (then <paramref name="contact"/> is required).
    /// </summary>
    public StoredDeletionRequest Request(string uid, DeletionChannel channel, bool verified, string contact = null)
    {
        _gate.Wait();
        try
        {
            if (uid != null)
            {
                StoredDeletionRequest open = _store.OpenDeletionFor(uid);
                if (open != null)
                {
                    if (open.State == DeletionState.AwaitingVerification && verified)
                    {
                        open.State = DeletionState.InProgress;
                        _store.SaveDeletion(open);
                    }
                    return open;
                }
            }
            DateTimeOffset now = _time.GetUtcNow();
            var r = new StoredDeletionRequest
            {
                RequestId = NewRequestId(),
                Account = uid,
                AccountRef = uid == null ? null : PlayerRef.Of(uid),
                Channel = channel,
                State = verified && uid != null ? DeletionState.InProgress : DeletionState.AwaitingVerification,
                ReceivedAt = now,
                DueBy = now.AddDays(_options.DeletionCompletionDays),
                Contact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim(),
            };
            _store.SaveDeletion(r);
            _log.LogInformation("Deletion request {Request} received ({Channel}, {State})", r.RequestId, channel, r.State);
            return r;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ownership verification of a web request: the requester signed in as <paramref name="uid"/>.
    /// A request already bound to another account is refused. Returns the request, or null.
    /// </summary>
    public StoredDeletionRequest ConfirmOwnership(string requestId, string uid)
    {
        _gate.Wait();
        try
        {
            StoredDeletionRequest r = _store.GetDeletion(requestId);
            if (r == null || r.State == DeletionState.Completed) return r;
            if (r.Account != null && r.Account != uid) return null;
            StoredDeletionRequest open = _store.OpenDeletionFor(uid);
            if (open != null && open.RequestId != r.RequestId) return open; // the account already has a request in flight
            r.Account = uid;
            r.AccountRef = PlayerRef.Of(uid);
            if (r.State == DeletionState.AwaitingVerification) r.State = DeletionState.InProgress;
            _store.SaveDeletion(r);
            return r;
        }
        finally
        {
            _gate.Release();
        }
    }

    public StoredDeletionRequest Get(string requestId) => _store.GetDeletion(requestId);

    /// <summary>What was kept for a request, per store, with its disclosed justification.</summary>
    public IReadOnlyList<(string Store, string Category, int Rows, string Justification)> RetainedFor(string requestId) => _store.Retained(requestId);

    /// <summary>Runs every unfinished eraser for the request. Safe to repeat; serialized per process.</summary>
    public async Task<StoredDeletionRequest> ProcessAsync(string requestId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StoredDeletionRequest r = _store.GetDeletion(requestId);
            if (r == null || r.Account == null || r.State == DeletionState.AwaitingVerification || r.State == DeletionState.Completed) return r;
            foreach (IAccountEraser e in _erasers)
            {
                if (r.Steps.TryGetValue(e.Name, out EraseOutcome done) && (done == EraseOutcome.Erased || done == EraseOutcome.RetainedJustified)) continue;
                EraseStep step;
                try
                {
                    step = await e.EraseAsync(r.Account, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Deletion {Request}: eraser {Eraser} failed", r.RequestId, e.Name);
                    step = new EraseStep(EraseOutcome.Failed);
                }
                r.Steps[e.Name] = step.Outcome;
                if (step.Outcome == EraseOutcome.RetainedJustified)
                    _store.RecordRetained(r.RequestId, e.Name, e.Category.ToString(), step.Rows, step.Justification ?? "retained", _time.GetUtcNow());
            }
            bool all = _erasers.All(e => r.Steps.TryGetValue(e.Name, out EraseOutcome o) && (o == EraseOutcome.Erased || o == EraseOutcome.RetainedJustified));
            r.State = all ? DeletionState.Completed : DeletionState.PartiallyCompleted;
            if (all)
            {
                r.CompletedAt = _time.GetUtcNow();
                r.Account = null; // the completed request keeps only the pseudonymous reference
                r.Contact = null;
                _log.LogInformation("Deletion request {Request} completed", r.RequestId);
            }
            _store.SaveDeletion(r);
            return r;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Processes every verified, unfinished request (scheduled hourly). Returns how many were attempted.</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        IReadOnlyList<StoredDeletionRequest> pending = _store.DeletionsInState(DeletionState.InProgress, DeletionState.PartiallyCompleted);
        foreach (StoredDeletionRequest r in pending) await ProcessAsync(r.RequestId, ct).ConfigureAwait(false);
        return pending.Count;
    }

    /// <summary>Unfinished requests past the published window (the operator's alert).</summary>
    public IReadOnlyList<StoredDeletionRequest> Overdue()
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _store.DeletionsInState(DeletionState.AwaitingVerification, DeletionState.InProgress, DeletionState.PartiallyCompleted)
            .Where(r => now > r.DueBy).ToArray();
    }

    private static string NewRequestId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // ------------------------------------------------------------------ erasers

    private sealed class Eraser : IAccountEraser
    {
        private readonly Func<string, EraseStep> _erase;

        public Eraser(string name, DataCategory category, Func<string, EraseStep> erase)
        {
            Name = name;
            Category = category;
            _erase = erase;
        }

        public string Name { get; }
        public DataCategory Category { get; }
        public Task<EraseStep> EraseAsync(string uid, CancellationToken ct) => Task.FromResult(_erase(uid));
    }

    /// <summary>
    /// Every store that can hold the account id. What is retained (and why) matches
    /// <c>docs/privacy-data-map.md</c>: purchase records (accounting, tax, fraud) pseudonymised;
    /// settled match records and grants pseudonymised (diagnostic window, then purged; idempotency
    /// of settled results); the append-only audit trail and grievance records, which only ever hold
    /// the pseudonymous reference (integrity and the Rule 20 grievance record).
    /// </summary>
    private static IReadOnlyList<IAccountEraser> DefaultErasers(MetaSqliteStore store, SqliteStore matches, MatchRegistry registry) => new IAccountEraser[]
    {
        new Eraser("progression-ledger", DataCategory.ProgressionLedger, uid => new EraseStep(EraseOutcome.Erased, ((IRewardLedgerStore)store).DeletePlayer(uid))),
        new Eraser("daily-task-progress", DataCategory.DailyTaskProgress, uid => new EraseStep(EraseOutcome.Erased, ((IDailyTaskProgressStore)store).DeletePlayer(uid))),
        new Eraser("cosmetic-equipment", DataCategory.CosmeticEquipment, uid => new EraseStep(EraseOutcome.Erased, ((IEquipmentStore)store).DeletePlayer(uid))),
        new Eraser("ad-offer-tickets", DataCategory.AdOfferTicket, uid => new EraseStep(EraseOutcome.Erased, ((IAdTicketStore)store).DeletePlayer(uid))),
        new Eraser("account-profile", DataCategory.AccountProfile, uid => new EraseStep(EraseOutcome.Erased, store.DeleteProfile(uid))),
        new Eraser("raw-analytics", DataCategory.RawProductAnalytics, uid => new EraseStep(EraseOutcome.Erased, store.DeleteAnalyticsFor(uid))),
        new Eraser("purchase-records", DataCategory.PurchaseRecord, uid => new EraseStep(EraseOutcome.RetainedJustified, store.Pseudonymise(uid, Pseudonym(uid)),
            "Kept for accounting, tax and fraud obligations; the account id is replaced by a pseudonym (privacy data map: purchase records).")),
        new Eraser("match-records", DataCategory.MatchDiagnosticRecord, uid =>
        {
            // A live match finishes first (its result must settle); the scheduled job retries.
            if (registry.ActiveFor(uid) != null) return new EraseStep(EraseOutcome.ProviderUnavailable);
            (int m, int g) = matches.PseudonymiseAccount(uid, Pseudonym(uid));
            return new EraseStep(EraseOutcome.RetainedJustified, m + g,
                "Settled match records are kept for the 30-day diagnostic window and grants as the record that a result was settled once; both pseudonymised.");
        }),
        new Eraser("audit-and-grievances", DataCategory.SupportCase, uid =>
        {
            string reference = PlayerRef.Of(uid);
            int rows = matches.CountAuditByActor(reference) + matches.CountGrievancesByReporter(reference);
            return new EraseStep(EraseOutcome.RetainedJustified, rows,
                "Append-only audit trail and grievance records (India Online Gaming Rules, Rule 20) hold only the pseudonymous player reference.");
        }),
    };
}
