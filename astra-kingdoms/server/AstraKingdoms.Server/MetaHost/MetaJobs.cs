using System.Globalization;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Privacy;
using Microsoft.Extensions.Options;

namespace AstraKingdoms.Server.MetaHost;

/// <summary>
/// The meta README's scheduled jobs, on one background loop (checked every minute on the service's
/// <see cref="TimeProvider"/>):
/// <list type="bullet">
/// <item>Play acknowledgement retry (every 5 minutes): Play refunds and revokes one-time purchases
/// not acknowledged within three days; purchases inside the last 24 hours of that window are logged
/// as errors for the operator.</item>
/// <item>Voided-purchase polling (every 12 hours): revokes refunded or charged-back tokens. The poll
/// window overlaps the previous one by an hour; revokes are idempotent.</item>
/// <item>Account deletion (hourly): finishes verified requests, retries partial ones, alerts on
/// requests past the published window.</item>
/// <item>Retention sweep (daily): daily-task rows and ad tickets after 30 days, raw analytics after 90
/// (plan: "Data and artifact retention defaults").</item>
/// <item>Ledger reconciliation (hourly): settled grants missing from the meta ledger.</item>
/// </list>
/// Each job is also a public method so tests and operators can run it on demand.
/// </summary>
public sealed class MetaJobs : BackgroundService
{
    public const string VoidedSinceKey = "jobs.voided_since";

    private readonly MetaServices _meta;
    private readonly AccountDeletionProcessor _deletion;
    private readonly MetaSettlement _settlement;
    private readonly TimeProvider _time;
    private readonly MetaJobOptions _options;
    private readonly ILogger<MetaJobs> _log;
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new(StringComparer.Ordinal);

    public MetaJobs(MetaServices meta, AccountDeletionProcessor deletion, MetaSettlement settlement, TimeProvider time, IOptions<ServerOptions> options,
        ILogger<MetaJobs> log)
    {
        _meta = meta;
        _deletion = deletion;
        _settlement = settlement;
        _time = time;
        _options = options.Value.Meta.Jobs;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(1), _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // A failing job must not stop the others; it is retried at its next interval.
                _log.LogError(e, "Meta job loop failed");
            }
        }
    }

    /// <summary>Runs every job whose interval has elapsed (all of them on the first call).</summary>
    public async Task RunDueAsync(CancellationToken ct)
    {
        if (Due("ack", TimeSpan.FromMinutes(_options.AcknowledgementRetryMinutes))) await RetryAcknowledgementsAsync(ct).ConfigureAwait(false);
        if (Due("voided", TimeSpan.FromHours(_options.VoidedPurchasePollHours))) await PollVoidedPurchasesAsync(ct).ConfigureAwait(false);
        if (Due("deletion", TimeSpan.FromMinutes(_options.DeletionProcessingMinutes))) await ProcessDeletionsAsync(ct).ConfigureAwait(false);
        if (Due("retention", TimeSpan.FromHours(_options.RetentionSweepHours))) SweepRetention();
        if (Due("reconcile", TimeSpan.FromMinutes(_options.ReconciliationMinutes))) _settlement.Reconcile();
    }

    private bool Due(string job, TimeSpan every)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (_lastRun.TryGetValue(job, out DateTimeOffset last) && now - last < every) return false;
        _lastRun[job] = now;
        return true;
    }

    /// <summary>Retries pending acknowledgements; returns the purchases at risk of Play's automatic refund.</summary>
    public async Task<IReadOnlyList<PendingAcknowledgement>> RetryAcknowledgementsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PendingAcknowledgement> atRisk = await _meta.Purchases.RetryAcknowledgementsAsync(ct).ConfigureAwait(false);
        foreach (PendingAcknowledgement p in atRisk)
            _log.LogError("Purchase acknowledgement at risk: {Sku} deadline {Deadline} after {Attempts} attempts", p.Sku, p.Deadline, p.Attempts);
        return atRisk;
    }

    /// <summary>Pulls voided purchases since the last successful poll (minus an hour of overlap). Returns new revokes, or -1 on a store error.</summary>
    public async Task<int> PollVoidedPurchasesAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = _time.GetUtcNow();
        string stored = _meta.Store.GetValue(VoidedSinceKey);
        DateTimeOffset since = stored != null
            ? DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            : now.AddDays(-30); // Play keeps voided purchases for 30 days
        int revoked = await _meta.Purchases.ProcessVoidedPurchasesAsync(since, ct).ConfigureAwait(false);
        if (revoked < 0)
        {
            _log.LogWarning("Voided purchase poll failed; retrying at the next interval");
            return revoked;
        }
        _meta.Store.SetValue(VoidedSinceKey, MetaSqliteStore.Iso(now.AddHours(-1)));
        if (revoked > 0) _log.LogInformation("Revoked {Count} voided purchases", revoked);
        return revoked;
    }

    public async Task<int> ProcessDeletionsAsync(CancellationToken ct = default)
    {
        int n = await _deletion.ProcessPendingAsync(ct).ConfigureAwait(false);
        foreach (StoredDeletionRequest r in _deletion.Overdue())
            _log.LogError("Deletion request {Request} is past its published window ({DueBy}, state {State})", r.RequestId, r.DueBy, r.State);
        return n;
    }

    /// <summary>Deletes rows past their retention period. Returns rows removed per category.</summary>
    public IReadOnlyDictionary<DataCategory, int> SweepRetention()
    {
        DateTimeOffset now = _time.GetUtcNow();
        var removed = new Dictionary<DataCategory, int>();
        TimeSpan daily = RetentionDefaults.For(DataCategory.DailyTaskProgress).MaxAge.Value;
        removed[DataCategory.DailyTaskProgress] = _meta.Store.PurgeDailyProgressBefore(_meta.DailyTasks.Reset.DayKey(now - daily));
        removed[DataCategory.AdOfferTicket] = _meta.Store.PurgeAdTicketsIssuedBefore(now - RetentionDefaults.For(DataCategory.AdOfferTicket).MaxAge.Value);
        removed[DataCategory.RawProductAnalytics] =
            _meta.Store.PurgeAnalyticsReceivedBefore(now - RetentionDefaults.For(DataCategory.RawProductAnalytics).MaxAge.Value);
        _log.LogInformation("Retention sweep removed {Daily} daily rows, {Tickets} ad tickets, {Events} analytics events",
            removed[DataCategory.DailyTaskProgress], removed[DataCategory.AdOfferTicket], removed[DataCategory.RawProductAnalytics]);
        return removed;
    }
}
