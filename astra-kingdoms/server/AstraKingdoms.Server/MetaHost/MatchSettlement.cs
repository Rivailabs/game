using System.Threading.Channels;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Reporting;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Storage;
using Microsoft.Data.Sqlite;

namespace AstraKingdoms.Server.Matches
{
    /// <summary>A match as it was settled (already durable in the <c>matches</c> and <c>reward_grants</c> tables).</summary>
    public sealed record SettledMatch(string MatchId, string ResultId, string Outcome, MatchResult Result, string RecordJson,
        IReadOnlyList<HumanSeat> Humans, bool VersusBot, DateTimeOffset CreatedAt, DateTimeOffset SettledAt, int Rounds,
        IReadOnlyList<RewardGrant> Grants);

    /// <summary>Receives settled matches. Must not block: it is called under the match's lock.</summary>
    public interface IMatchSettlementHook
    {
        void Enqueue(SettledMatch match);
    }
}

namespace AstraKingdoms.Server.MetaHost
{
    /// <summary>
    /// The match service hook of the meta README: on every terminal result, each human seat's
    /// <see cref="MatchOutcomeReport"/> goes to <see cref="ProgressionService.GrantForMatch"/> and
    /// <see cref="Meta.Economy.DailyTaskService.RecordMatch"/>, and a <see cref="MatchRecordSummary"/>
    /// (no player ids) is stored for completion reporting.
    /// <para>
    /// <b>Exactly once.</b> The settlement transaction already wrote <c>reward_grants</c> (primary key
    /// result id + player). This hook applies those grants to the meta ledger, whose idempotency key is
    /// <c>match:{resultId}:{player}</c>, so a repeat is a no-op. It runs on a background worker so a
    /// burst of settling matches never waits on extra SQLite writes; if the process dies between the
    /// settlement commit and the ledger append, <see cref="Reconcile"/> (startup and hourly) applies
    /// whatever <c>reward_grants</c> holds that the ledger does not.
    /// </para>
    /// </summary>
    public sealed class MetaSettlement : BackgroundService, IMatchSettlementHook
    {
        private readonly Channel<SettledMatch> _queue = Channel.CreateUnbounded<SettledMatch>(new UnboundedChannelOptions { SingleReader = true });
        private readonly MetaServices _meta;
        private readonly SqliteStore _matches;
        private readonly ILogger<MetaSettlement> _log;
        private long _pending;

        public MetaSettlement(MetaServices meta, SqliteStore matches, ILogger<MetaSettlement> log)
        {
            _meta = meta;
            _matches = matches;
            _log = log;
        }

        /// <summary>Settled matches not yet applied (tests wait for zero).</summary>
        public long Pending => Interlocked.Read(ref _pending);

        public void Enqueue(SettledMatch match)
        {
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(match)) Interlocked.Decrement(ref _pending);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Reconcile(); // grants committed by a previous process but never applied
            try
            {
                await foreach (SettledMatch m in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        Apply(m);
                    }
                    catch (Exception e) when (e is SqliteException || e is InvalidOperationException || e is FormatException)
                    {
                        _log.LogError(e, "Meta settlement of match {MatchId} failed; reconciliation will retry", m.MatchId);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>Applies one settled match (idempotent).</summary>
        public void Apply(SettledMatch m)
        {
            MatchRecord record = string.IsNullOrEmpty(m.RecordJson) ? null : MatchRecord.FromJson(m.RecordJson);
            int humans = m.Humans.Count;
            MatchKind kind = m.VersusBot ? MatchKind.OnlineBot : MatchKind.OnlineHuman;
            if (m.Result != null && record?.Result != null)
                _meta.Store.SaveSummary(MatchReportBuilder.Summary(record, kind, humans, m.CreatedAt));
            else
                _meta.Store.SaveSummary(new MatchRecordSummary(m.MatchId, kind, MatchEnding.TechnicalAbort, humans, m.Rounds, m.CreatedAt, false, false));

            if (m.Result == null) return; // technical void: no reward and no loss
            foreach (MatchOutcomeReport report in RewardPolicy.Reports(m.ResultId, m.Outcome, m.Result, m.Humans, m.VersusBot, m.SettledAt, record))
            {
                MatchGrantResult g = _meta.Progression.GrantForMatch(report);
                _meta.DailyTasks.RecordMatch(report);
                if (g.Status == GrantStatus.Granted && g.LevelAfter > g.LevelBefore)
                    _log.LogInformation("Player {Player} reached level {Level}", Identity.PlayerRef.Of(report.PlayerId), g.LevelAfter);
            }
        }

        /// <summary>
        /// Applies every <c>reward_grants</c> row the meta ledger lacks (crash between the settlement
        /// commit and the background apply). Returns the number repaired. Rows of deleted accounts are
        /// pseudonymised (<c>deleted:</c>) and skipped.
        /// </summary>
        public int Reconcile()
        {
            int repaired = 0;
            foreach (UnappliedGrant g in _meta.Store.UnappliedGrants())
            {
                StoredMatch stored = g.MatchId == null ? null : _matches.Get(g.MatchId);
                IReadOnlyList<int> weapons = Array.Empty<int>();
                if (stored?.RecordJson != null)
                {
                    try
                    {
                        MatchRecord record = MatchRecord.FromJson(stored.RecordJson);
                        weapons = MatchReportBuilder.WeaponsUsed(record, stored.PlayerB == g.Player ? PlayerSide.B : PlayerSide.A);
                    }
                    catch (FormatException)
                    {
                    }
                }
                var entry = new RewardLedgerEntry(ProgressionService.MatchKey(g.ResultId, g.Player), g.Player, LedgerSource.Match, g.Xp, g.Coins,
                    g.GrantedAt, reference: g.ResultId, weaponsUsed: weapons);
                if (_meta.Store.TryAppend(entry).Status == AppendStatus.Appended) repaired++;
            }
            if (repaired > 0) _log.LogWarning("Reconciliation applied {Count} settled grants missing from the meta ledger", repaired);
            return repaired;
        }
    }
}
