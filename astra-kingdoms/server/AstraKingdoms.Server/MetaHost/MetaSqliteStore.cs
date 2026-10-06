using System.Globalization;
using System.Text;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Economy;
using AstraKingdoms.Meta.Privacy;
using AstraKingdoms.Meta.Reporting;
using AstraKingdoms.Rules.Core;
using Microsoft.Data.Sqlite;

namespace AstraKingdoms.Server.MetaHost;

/// <summary>
/// Durable SQLite implementation of every storage contract the meta library defines (see
/// <c>src/AstraKingdoms.Meta/README.md</c>, "Server integration points"), in the same database file
/// as the match service. Each call opens a pooled connection, like <see cref="Storage.SqliteStore"/>.
/// <list type="bullet">
/// <item><c>reward_ledger</c>: <see cref="IRewardLedgerStore"/>. <c>UNIQUE(idempotency_key)</c>; an
/// append runs in <c>BEGIN IMMEDIATE</c> (SQLite's single writer lock), so a precondition such as
/// "enough coins" sees exactly the totals the insert commits against. Rows are never updated;
/// account deletion removes the player's rows.</item>
/// <item><c>entitlement_ledger</c>: <see cref="IEntitlementLedgerStore"/>. Append-only, enforced by
/// triggers: DELETE is refused and UPDATE may change only the player column (pseudonymisation on
/// account deletion; purchase records are retained for accounting, tax and fraud).</item>
/// <item><c>daily_progress</c>: <see cref="IDailyTaskProgressStore"/>, one row per (player, day),
/// read-modify-write inside one immediate transaction.</item>
/// <item><c>equipment</c>, <c>ad_tickets</c>, <c>ack_queue</c>: plain keyed tables.</item>
/// </list>
/// Profiles, analytics, deletion requests and job checkpoints have their own small APIs below.
/// </summary>
public sealed class MetaSqliteStore : IRewardLedgerStore, IEntitlementLedgerStore, IDailyTaskProgressStore, IEquipmentStore, IAdTicketStore,
    IAcknowledgementQueue
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public MetaSqliteStore(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 30, Pooling = true }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using SqliteCommand pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using SqliteConnection c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, @"
CREATE TABLE IF NOT EXISTS meta_schema_version (version INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS reward_ledger (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  idempotency_key TEXT NOT NULL UNIQUE,
  player TEXT NOT NULL,
  source INTEGER NOT NULL,
  xp INTEGER NOT NULL,
  coins INTEGER NOT NULL,
  cosmetic_id TEXT,
  weapons TEXT NOT NULL,
  reference TEXT,
  corrects_key TEXT,
  at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_reward_ledger_player ON reward_ledger(player, seq);
CREATE TRIGGER IF NOT EXISTS reward_ledger_no_update BEFORE UPDATE ON reward_ledger
  WHEN NEW.idempotency_key IS NOT OLD.idempotency_key OR NEW.player IS NOT OLD.player OR NEW.xp IS NOT OLD.xp OR NEW.coins IS NOT OLD.coins
    OR NEW.source IS NOT OLD.source OR NEW.cosmetic_id IS NOT OLD.cosmetic_id
  BEGIN SELECT RAISE(ABORT, 'reward_ledger is append-only'); END;
CREATE TABLE IF NOT EXISTS entitlement_ledger (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  idempotency_key TEXT NOT NULL UNIQUE,
  player TEXT,
  sku TEXT,
  purchase_token TEXT,
  order_id TEXT,
  action INTEGER NOT NULL,
  reason INTEGER NOT NULL,
  test_purchase INTEGER NOT NULL,
  note TEXT,
  at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_entitlement_token ON entitlement_ledger(purchase_token);
CREATE INDEX IF NOT EXISTS ix_entitlement_player ON entitlement_ledger(player);
CREATE TRIGGER IF NOT EXISTS entitlement_no_delete BEFORE DELETE ON entitlement_ledger
  BEGIN SELECT RAISE(ABORT, 'entitlement_ledger is append-only'); END;
CREATE TRIGGER IF NOT EXISTS entitlement_only_pseudonymise BEFORE UPDATE ON entitlement_ledger
  WHEN NEW.idempotency_key IS NOT OLD.idempotency_key OR NEW.sku IS NOT OLD.sku OR NEW.purchase_token IS NOT OLD.purchase_token
    OR NEW.order_id IS NOT OLD.order_id OR NEW.action IS NOT OLD.action OR NEW.reason IS NOT OLD.reason
    OR NEW.test_purchase IS NOT OLD.test_purchase OR NEW.note IS NOT OLD.note OR NEW.at IS NOT OLD.at
  BEGIN SELECT RAISE(ABORT, 'entitlement_ledger rows may only be pseudonymised'); END;
CREATE TABLE IF NOT EXISTS daily_progress (
  player TEXT NOT NULL,
  day TEXT NOT NULL,
  matches TEXT NOT NULL,
  elements TEXT NOT NULL,
  practice TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (player, day)
);
CREATE INDEX IF NOT EXISTS ix_daily_day ON daily_progress(day);
CREATE TABLE IF NOT EXISTS equipment (
  player TEXT NOT NULL,
  slot INTEGER NOT NULL,
  cosmetic_id TEXT NOT NULL,
  PRIMARY KEY (player, slot)
);
CREATE TABLE IF NOT EXISTS ad_tickets (
  ticket_id TEXT PRIMARY KEY,
  player TEXT NOT NULL,
  issued_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  non_personalized INTEGER NOT NULL,
  child_directed INTEGER NOT NULL,
  under_age INTEGER NOT NULL,
  max_rating TEXT
);
CREATE INDEX IF NOT EXISTS ix_ad_tickets_player ON ad_tickets(player);
CREATE TABLE IF NOT EXISTS ack_queue (
  purchase_token TEXT PRIMARY KEY,
  sku TEXT NOT NULL,
  deadline TEXT NOT NULL,
  attempts INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS profiles (
  player TEXT PRIMARY KEY,
  age_group INTEGER NOT NULL,
  parental_consent INTEGER NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS analytics_events (
  event_id TEXT PRIMARY KEY,
  type INTEGER NOT NULL,
  schema_version INTEGER NOT NULL,
  analytics_id TEXT NOT NULL,
  session_id TEXT NOT NULL,
  occurred_at TEXT NOT NULL,
  received_at TEXT NOT NULL,
  flags INTEGER NOT NULL,
  cohort INTEGER NOT NULL,
  properties TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_analytics_received ON analytics_events(received_at);
CREATE INDEX IF NOT EXISTS ix_analytics_id ON analytics_events(analytics_id);
CREATE TABLE IF NOT EXISTS analytics_links (
  player TEXT NOT NULL,
  analytics_id TEXT NOT NULL,
  PRIMARY KEY (player, analytics_id)
);
CREATE TABLE IF NOT EXISTS match_summaries (
  match_id TEXT PRIMARY KEY,
  kind INTEGER NOT NULL,
  ending INTEGER NOT NULL,
  human_seats INTEGER NOT NULL,
  rounds INTEGER NOT NULL,
  started_at TEXT NOT NULL,
  automation INTEGER NOT NULL,
  internal INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS deletion_requests (
  request_id TEXT PRIMARY KEY,
  account TEXT,
  account_ref TEXT,
  channel INTEGER NOT NULL,
  state INTEGER NOT NULL,
  received_at TEXT NOT NULL,
  due_by TEXT NOT NULL,
  completed_at TEXT,
  steps TEXT NOT NULL,
  contact TEXT
);
CREATE INDEX IF NOT EXISTS ix_deletion_account ON deletion_requests(account);
CREATE TABLE IF NOT EXISTS deletion_retained (
  request_id TEXT NOT NULL,
  store TEXT NOT NULL,
  category TEXT NOT NULL,
  rows INTEGER NOT NULL,
  justification TEXT NOT NULL,
  recorded_at TEXT NOT NULL,
  PRIMARY KEY (request_id, store)
);
CREATE TABLE IF NOT EXISTS meta_kv (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);");
        long version = Convert.ToInt64(Scalar(c, "SELECT COALESCE(MAX(version), 0) FROM meta_schema_version;"), CultureInfo.InvariantCulture);
        if (version == 0) Exec(c, "INSERT INTO meta_schema_version(version) VALUES (" + SchemaVersion + ");");
        else if (version > SchemaVersion)
            throw new InvalidOperationException("Meta schema " + version + " is newer than this server (" + SchemaVersion + ").");
    }

    // ================================================================== reward ledger (IRewardLedgerStore)

    public LedgerAppendResult TryAppend(RewardLedgerEntry entry, Func<PlayerTotals, string> precondition = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using SqliteConnection c = Open();
        using SqliteTransaction tx = c.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: holds the writer lock
        RewardLedgerEntry existing = FindLedger(c, tx, entry.IdempotencyKey);
        if (existing != null) return new LedgerAppendResult(AppendStatus.Duplicate, existing, "duplicate", !existing.SamePayload(entry));
        if (precondition != null)
        {
            string reason = precondition(TotalsOf(c, tx, entry.PlayerId));
            if (reason != null) return new LedgerAppendResult(AppendStatus.Rejected, null, reason);
        }
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO reward_ledger (idempotency_key, player, source, xp, coins, cosmetic_id, weapons, reference, corrects_key, at)
VALUES ($k, $p, $s, $xp, $c, $cos, $w, $r, $fix, $at);";
            P(cmd, "$k", entry.IdempotencyKey); P(cmd, "$p", entry.PlayerId); P(cmd, "$s", (int)entry.Source); P(cmd, "$xp", entry.XpDelta);
            P(cmd, "$c", entry.CoinDelta); P(cmd, "$cos", entry.CosmeticId); P(cmd, "$w", string.Join(",", entry.WeaponsUsed));
            P(cmd, "$r", entry.Reference); P(cmd, "$fix", entry.CorrectsKey); P(cmd, "$at", Iso(entry.At));
            cmd.ExecuteNonQuery();
        }
        RewardLedgerEntry stored = FindLedger(c, tx, entry.IdempotencyKey);
        tx.Commit();
        return new LedgerAppendResult(AppendStatus.Appended, stored);
    }

    public RewardLedgerEntry Find(string idempotencyKey)
    {
        using SqliteConnection c = Open();
        return FindLedger(c, null, idempotencyKey);
    }

    public IReadOnlyList<RewardLedgerEntry> Entries(string playerId)
    {
        using SqliteConnection c = Open();
        return LedgerRows(c, null, "WHERE player = $v ORDER BY seq", playerId);
    }

    public PlayerTotals Totals(string playerId)
    {
        using SqliteConnection c = Open();
        return TotalsOf(c, null, playerId);
    }

    public int DeletePlayer(string playerId)
    {
        using SqliteConnection c = Open();
        using SqliteTransaction tx = c.BeginTransaction(deferred: false);
        int n = Execute(c, tx, "DELETE FROM reward_ledger WHERE player = $v;", ("$v", playerId));
        // The one-time guest-migration marker stays (it stops the same local data seeding another
        // account) but must not keep naming the deleted account.
        Execute(c, tx, "UPDATE reward_ledger SET reference = $ref WHERE reference = $old;",
            ("$ref", "migrated-to:" + Identity.PlayerRef.Of(playerId)), ("$old", "migrated-to:" + playerId));
        tx.Commit();
        return n;
    }

    /// <summary>
    /// Settled grants (<c>reward_grants</c>, written by the match service in its settlement
    /// transaction) that have no matching meta-ledger entry. Pseudonymised rows of deleted accounts
    /// (<c>deleted:</c>) are never re-applied.
    /// </summary>
    public IReadOnlyList<UnappliedGrant> UnappliedGrants()
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT g.result_id, g.player, g.xp, g.coins, g.granted_at, m.match_id
FROM reward_grants g
LEFT JOIN reward_ledger l ON l.idempotency_key = 'match:' || g.result_id || ':' || g.player
LEFT JOIN matches m ON m.result_id = g.result_id
WHERE l.seq IS NULL AND g.player NOT LIKE 'deleted:%'
ORDER BY g.granted_at;";
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<UnappliedGrant>();
        while (r.Read()) list.Add(new UnappliedGrant(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), ParseIso(r.GetString(4)), S(r, 5)));
        return list;
    }

    private static PlayerTotals TotalsOf(SqliteConnection c, SqliteTransaction tx, string playerId) =>
        PlayerTotals.Fold(playerId, LedgerRows(c, tx, "WHERE player = $v ORDER BY seq", playerId));

    private static RewardLedgerEntry FindLedger(SqliteConnection c, SqliteTransaction tx, string key) =>
        LedgerRows(c, tx, "WHERE idempotency_key = $v", key).FirstOrDefault();

    private static List<RewardLedgerEntry> LedgerRows(SqliteConnection c, SqliteTransaction tx, string where, string value)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT seq, idempotency_key, player, source, xp, coins, cosmetic_id, weapons, reference, corrects_key, at FROM reward_ledger " + where + ";";
        P(cmd, "$v", value);
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<RewardLedgerEntry>();
        while (r.Read())
        {
            string w = r.GetString(7);
            int[] weapons = w.Length == 0 ? Array.Empty<int>() : w.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            list.Add(new RewardLedgerEntry(r.GetString(1), r.GetString(2), (LedgerSource)r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                ParseIso(r.GetString(10)), S(r, 8), S(r, 6), weapons, S(r, 9), r.GetInt64(0)));
        }
        return list;
    }

    // ================================================================== entitlement ledger (IEntitlementLedgerStore)

    public EntitlementEntry TryAppend(EntitlementEntry entry, out bool appended)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using SqliteConnection c = Open();
        using SqliteTransaction tx = c.BeginTransaction(deferred: false);
        EntitlementEntry existing = EntitlementRows(c, tx, "WHERE idempotency_key = $v", entry.IdempotencyKey).FirstOrDefault();
        if (existing != null)
        {
            appended = false;
            return existing;
        }
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO entitlement_ledger (idempotency_key, player, sku, purchase_token, order_id, action, reason, test_purchase, note, at)
VALUES ($k, $p, $sku, $tok, $ord, $act, $why, $test, $note, $at);";
            P(cmd, "$k", entry.IdempotencyKey); P(cmd, "$p", entry.PlayerId); P(cmd, "$sku", entry.Sku); P(cmd, "$tok", entry.PurchaseToken);
            P(cmd, "$ord", entry.OrderId); P(cmd, "$act", (int)entry.Action); P(cmd, "$why", (int)entry.Reason); P(cmd, "$test", entry.IsTestPurchase ? 1 : 0);
            P(cmd, "$note", entry.Note); P(cmd, "$at", Iso(entry.At));
            cmd.ExecuteNonQuery();
        }
        EntitlementEntry stored = EntitlementRows(c, tx, "WHERE idempotency_key = $v", entry.IdempotencyKey).First();
        tx.Commit();
        appended = true;
        return stored;
    }

    EntitlementEntry IEntitlementLedgerStore.Find(string idempotencyKey)
    {
        using SqliteConnection c = Open();
        return EntitlementRows(c, null, "WHERE idempotency_key = $v", idempotencyKey).FirstOrDefault();
    }

    public IReadOnlyList<EntitlementEntry> ForPlayer(string playerId)
    {
        using SqliteConnection c = Open();
        return EntitlementRows(c, null, "WHERE player = $v ORDER BY seq", playerId);
    }

    public IReadOnlyList<EntitlementEntry> ForToken(string purchaseToken)
    {
        using SqliteConnection c = Open();
        return EntitlementRows(c, null, "WHERE purchase_token = $v ORDER BY seq", purchaseToken);
    }

    public int Pseudonymise(string playerId, string pseudonym)
    {
        using SqliteConnection c = Open();
        return Execute(c, null, "UPDATE entitlement_ledger SET player = $new WHERE player = $old;", ("$new", pseudonym), ("$old", playerId));
    }

    private static List<EntitlementEntry> EntitlementRows(SqliteConnection c, SqliteTransaction tx, string where, string value)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT seq, idempotency_key, player, sku, purchase_token, order_id, action, reason, test_purchase, note, at FROM entitlement_ledger " +
                          where + ";";
        P(cmd, "$v", value);
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<EntitlementEntry>();
        while (r.Read())
            list.Add(new EntitlementEntry(r.GetString(1), S(r, 2), S(r, 3), S(r, 4), S(r, 5), (EntitlementAction)r.GetInt32(6),
                (EntitlementReason)r.GetInt32(7), ParseIso(r.GetString(10)), r.GetInt32(8) == 1, S(r, 9), r.GetInt64(0)));
        return list;
    }

    // ================================================================== daily task progress (IDailyTaskProgressStore)

    public DailyProgress Get(string playerId, string dayKey)
    {
        using SqliteConnection c = Open();
        return ReadProgress(c, null, playerId, dayKey);
    }

    public DailyProgress Update(string playerId, string dayKey, Func<DailyProgress, DailyProgress> mutate)
    {
        using SqliteConnection c = Open();
        using SqliteTransaction tx = c.BeginTransaction(deferred: false);
        DailyProgress current = ReadProgress(c, tx, playerId, dayKey);
        DailyProgress next = mutate(current) ?? current;
        if (!ReferenceEquals(next, current))
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO daily_progress (player, day, matches, elements, practice, updated_at) VALUES ($p, $d, $m, $e, $pr, $at)
ON CONFLICT(player, day) DO UPDATE SET matches = excluded.matches, elements = excluded.elements, practice = excluded.practice, updated_at = excluded.updated_at;";
            P(cmd, "$p", playerId); P(cmd, "$d", dayKey);
            P(cmd, "$m", JoinSorted(next.CountedMatches)); P(cmd, "$e", string.Join(",", next.Elements.Select(x => ((int)x).ToString(CultureInfo.InvariantCulture)).OrderBy(x => x, StringComparer.Ordinal)));
            P(cmd, "$pr", JoinSorted(next.PracticeEvents)); P(cmd, "$at", Iso(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return next;
    }

    int IDailyTaskProgressStore.DeletePlayer(string playerId)
    {
        using SqliteConnection c = Open();
        return Execute(c, null, "DELETE FROM daily_progress WHERE player = $v;", ("$v", playerId));
    }

    /// <summary>Retention sweep: removes daily rows for days before <paramref name="oldestDayToKeep"/> (yyyy-MM-dd).</summary>
    public int PurgeDailyProgressBefore(string oldestDayToKeep)
    {
        using SqliteConnection c = Open();
        return Execute(c, null, "DELETE FROM daily_progress WHERE day < $d;", ("$d", oldestDayToKeep));
    }

    private static DailyProgress ReadProgress(SqliteConnection c, SqliteTransaction tx, string playerId, string dayKey)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT matches, elements, practice FROM daily_progress WHERE player = $p AND day = $d;";
        P(cmd, "$p", playerId); P(cmd, "$d", dayKey);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return DailyProgress.Empty;
        return new DailyProgress(Split(r.GetString(0)), Split(r.GetString(1)).Select(s => (Element)int.Parse(s, CultureInfo.InvariantCulture)),
            Split(r.GetString(2)));
    }

    // Match result ids, element numbers and exercise keys contain no newline, so newline-separated lists are unambiguous.
    private static string JoinSorted(IEnumerable<string> items) => string.Join("\n", items.OrderBy(x => x, StringComparer.Ordinal));
    private static IEnumerable<string> Split(string s) => s.Length == 0 ? Array.Empty<string>() : s.Split(s.Contains('\n') ? '\n' : ',');

    // ================================================================== equipment (IEquipmentStore)

    IReadOnlyDictionary<CosmeticSlot, string> IEquipmentStore.Get(string playerId)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT slot, cosmetic_id FROM equipment WHERE player = $p;";
        P(cmd, "$p", playerId);
        using SqliteDataReader r = cmd.ExecuteReader();
        var d = new Dictionary<CosmeticSlot, string>();
        while (r.Read()) d[(CosmeticSlot)r.GetInt32(0)] = r.GetString(1);
        return d;
    }

    public void Set(string playerId, CosmeticSlot slot, string cosmeticId)
    {
        using SqliteConnection c = Open();
        Execute(c, null, "INSERT INTO equipment (player, slot, cosmetic_id) VALUES ($p, $s, $c) ON CONFLICT(player, slot) DO UPDATE SET cosmetic_id = excluded.cosmetic_id;",
            ("$p", playerId), ("$s", (int)slot), ("$c", cosmeticId));
    }

    int IEquipmentStore.DeletePlayer(string playerId)
    {
        using SqliteConnection c = Open();
        return Execute(c, null, "DELETE FROM equipment WHERE player = $v;", ("$v", playerId));
    }

    // ================================================================== rewarded-ad tickets (IAdTicketStore)

    public void Add(AdOfferTicket ticket)
    {
        using SqliteConnection c = Open();
        Execute(c, null, @"INSERT INTO ad_tickets (ticket_id, player, issued_at, expires_at, non_personalized, child_directed, under_age, max_rating)
VALUES ($id, $p, $i, $e, $np, $cd, $ua, $mr);", ("$id", ticket.TicketId), ("$p", ticket.PlayerId), ("$i", Iso(ticket.IssuedAt)),
            ("$e", Iso(ticket.ExpiresAt)), ("$np", ticket.RequestOptions.NonPersonalized ? 1 : 0),
            ("$cd", ticket.RequestOptions.TagForChildDirectedTreatment ? 1 : 0), ("$ua", ticket.RequestOptions.TagForUnderAgeOfConsent ? 1 : 0),
            ("$mr", ticket.RequestOptions.MaxAdContentRating));
    }

    AdOfferTicket IAdTicketStore.Find(string ticketId)
    {
        if (ticketId == null) return null;
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ticket_id, player, issued_at, expires_at, non_personalized, child_directed, under_age, max_rating FROM ad_tickets WHERE ticket_id = $id;";
        P(cmd, "$id", ticketId);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new AdOfferTicket(r.GetString(0), r.GetString(1), ParseIso(r.GetString(2)), ParseIso(r.GetString(3)),
            new AdRequestOptions(r.GetInt32(4) == 1, r.GetInt32(5) == 1, r.GetInt32(6) == 1, S(r, 7)));
    }

    int IAdTicketStore.DeletePlayer(string playerId)
    {
        using SqliteConnection c = Open();
        return Execute(c, null, "DELETE FROM ad_tickets WHERE player = $v;", ("$v", playerId));
    }

    /// <summary>Retention sweep for ad tickets (30 days; tickets referenced by a support case are not tracked here).</summary>
    public int PurgeAdTicketsIssuedBefore(DateTimeOffset cutoff)
    {
        using SqliteConnection c = Open();
        return Execute(c, null, "DELETE FROM ad_tickets WHERE issued_at < $t;", ("$t", Iso(cutoff)));
    }

    // ================================================================== acknowledgement queue (IAcknowledgementQueue)

    public void Upsert(PendingAcknowledgement item)
    {
        using SqliteConnection c = Open();
        Execute(c, null, @"INSERT INTO ack_queue (purchase_token, sku, deadline, attempts) VALUES ($t, $s, $d, $a)
ON CONFLICT(purchase_token) DO UPDATE SET attempts = excluded.attempts, deadline = excluded.deadline;",
            ("$t", item.PurchaseToken), ("$s", item.Sku), ("$d", Iso(item.Deadline)), ("$a", item.Attempts));
    }

    public void Remove(string purchaseToken)
    {
        using SqliteConnection c = Open();
        Execute(c, null, "DELETE FROM ack_queue WHERE purchase_token = $t;", ("$t", purchaseToken));
    }

    public IReadOnlyList<PendingAcknowledgement> All()
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sku, purchase_token, deadline, attempts FROM ack_queue ORDER BY deadline;";
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<PendingAcknowledgement>();
        while (r.Read()) list.Add(new PendingAcknowledgement(r.GetString(0), r.GetString(1), ParseIso(r.GetString(2)), r.GetInt32(3)));
        return list;
    }

    // ================================================================== profiles (audience)

    public AudienceProfile GetAudience(string playerId)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT age_group, parental_consent FROM profiles WHERE player = $p;";
        P(cmd, "$p", playerId);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? new AudienceProfile((AgeGroup)r.GetInt32(0), (ParentalConsent)r.GetInt32(1)) : AudienceProfile.Unknown;
    }

    public void SetAudience(string playerId, AudienceProfile audience, DateTimeOffset now)
    {
        using SqliteConnection c = Open();
        Execute(c, null, @"INSERT INTO profiles (player, age_group, parental_consent, updated_at) VALUES ($p, $a, $c, $t)
ON CONFLICT(player) DO UPDATE SET age_group = excluded.age_group, parental_consent = excluded.parental_consent, updated_at = excluded.updated_at;",
            ("$p", playerId), ("$a", (int)audience.AgeGroup), ("$c", (int)audience.ParentalConsent), ("$t", Iso(now)));
    }

    public int DeleteProfile(string playerId)
    {
        using SqliteConnection c = Open();
        return Execute(c, null, "DELETE FROM profiles WHERE player = $v;", ("$v", playerId));
    }

    // ================================================================== analytics

    /// <summary>Stores events (de-duplicated on event_id) and links the uploading account to the analytics ids it used.</summary>
    public (int Inserted, int Duplicates) InsertAnalytics(string playerId, IReadOnlyList<StoredAnalyticsEvent> events, DateTimeOffset receivedAt)
    {
        using SqliteConnection c = Open();
        using SqliteTransaction tx = c.BeginTransaction(deferred: false);
        int inserted = 0, duplicates = 0;
        foreach (StoredAnalyticsEvent e in events)
        {
            int n = Execute(c, tx, @"INSERT OR IGNORE INTO analytics_events (event_id, type, schema_version, analytics_id, session_id, occurred_at, received_at, flags, cohort, properties)
VALUES ($id, $type, $v, $aid, $sid, $at, $rx, $flags, $cohort, $props);", ("$id", e.EventId), ("$type", e.Type), ("$v", e.SchemaVersion),
                ("$aid", e.AnalyticsId), ("$sid", e.SessionId), ("$at", Iso(e.OccurredAt)), ("$rx", Iso(receivedAt)), ("$flags", e.Flags),
                ("$cohort", e.Cohort), ("$props", e.PropertiesJson));
            if (n == 1) inserted++;
            else duplicates++;
            if (playerId != null)
                Execute(c, tx, "INSERT OR IGNORE INTO analytics_links (player, analytics_id) VALUES ($p, $a);", ("$p", playerId), ("$a", e.AnalyticsId));
        }
        tx.Commit();
        return (inserted, duplicates);
    }

    public int CountAnalytics(string analyticsId = null)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = analyticsId == null ? "SELECT COUNT(*) FROM analytics_events;" : "SELECT COUNT(*) FROM analytics_events WHERE analytics_id = $a;";
        P(cmd, "$a", analyticsId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Account deletion: raw events of every analytics id the account uploaded, and the links themselves.</summary>
    public int DeleteAnalyticsFor(string playerId)
    {
        using SqliteConnection c = Open();
        using SqliteTransaction tx = c.BeginTransaction(deferred: false);
        int n = Execute(c, tx, "DELETE FROM analytics_events WHERE analytics_id IN (SELECT analytics_id FROM analytics_links WHERE player = $p);", ("$p", playerId));
        Execute(c, tx, "DELETE FROM analytics_links WHERE player = $p;", ("$p", playerId));
        tx.Commit();
        return n;
    }

    public int PurgeAnalyticsReceivedBefore(DateTimeOffset cutoff)
    {
        using SqliteConnection c = Open();
        return Execute(c, null, "DELETE FROM analytics_events WHERE received_at < $t;", ("$t", Iso(cutoff)));
    }

    // ================================================================== match summaries (completion reporting; no player ids)

    public void SaveSummary(MatchRecordSummary s)
    {
        using SqliteConnection c = Open();
        Execute(c, null, @"INSERT OR IGNORE INTO match_summaries (match_id, kind, ending, human_seats, rounds, started_at, automation, internal)
VALUES ($m, $k, $e, $h, $r, $s, $a, $i);", ("$m", s.MatchId), ("$k", (int)s.Kind), ("$e", (int)s.Ending), ("$h", s.HumanSeats),
            ("$r", s.Rounds), ("$s", Iso(s.StartedAt)), ("$a", s.IsAutomation ? 1 : 0), ("$i", s.IsInternal ? 1 : 0));
    }

    public int CountSummaries()
    {
        using SqliteConnection c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM match_summaries;"), CultureInfo.InvariantCulture);
    }

    // ================================================================== deletion requests

    public void SaveDeletion(StoredDeletionRequest r)
    {
        using SqliteConnection c = Open();
        Execute(c, null, @"INSERT INTO deletion_requests (request_id, account, account_ref, channel, state, received_at, due_by, completed_at, steps, contact)
VALUES ($id, $acc, $ref, $ch, $st, $rx, $due, $done, $steps, $contact)
ON CONFLICT(request_id) DO UPDATE SET account = excluded.account, account_ref = excluded.account_ref, state = excluded.state,
  completed_at = excluded.completed_at, steps = excluded.steps, contact = excluded.contact;",
            ("$id", r.RequestId), ("$acc", r.Account), ("$ref", r.AccountRef), ("$ch", (int)r.Channel), ("$st", (int)r.State),
            ("$rx", Iso(r.ReceivedAt)), ("$due", Iso(r.DueBy)), ("$done", r.CompletedAt.HasValue ? Iso(r.CompletedAt.Value) : null),
            ("$steps", r.StepsText()), ("$contact", r.Contact));
    }

    public StoredDeletionRequest GetDeletion(string requestId) => DeletionRows("WHERE request_id = $v", requestId).FirstOrDefault();

    public StoredDeletionRequest OpenDeletionFor(string account) =>
        DeletionRows("WHERE account = $v AND state <> " + (int)DeletionState.Completed + " ORDER BY received_at", account).FirstOrDefault();

    public IReadOnlyList<StoredDeletionRequest> DeletionsInState(params DeletionState[] states) =>
        DeletionRows("WHERE state IN (" + string.Join(",", states.Select(s => (int)s)) + ") ORDER BY received_at", null);

    private List<StoredDeletionRequest> DeletionRows(string where, string value)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT request_id, account, account_ref, channel, state, received_at, due_by, completed_at, steps, contact FROM deletion_requests " + where + ";";
        P(cmd, "$v", value);
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<StoredDeletionRequest>();
        while (r.Read())
        {
            var d = new StoredDeletionRequest
            {
                RequestId = r.GetString(0),
                Account = S(r, 1),
                AccountRef = S(r, 2),
                Channel = (DeletionChannel)r.GetInt32(3),
                State = (DeletionState)r.GetInt32(4),
                ReceivedAt = ParseIso(r.GetString(5)),
                DueBy = ParseIso(r.GetString(6)),
                CompletedAt = S(r, 7) == null ? null : ParseIso(r.GetString(7)),
                Contact = S(r, 9),
            };
            d.ParseSteps(r.GetString(8));
            list.Add(d);
        }
        return list;
    }

    public void RecordRetained(string requestId, string store, string category, int rows, string justification, DateTimeOffset at)
    {
        using SqliteConnection c = Open();
        Execute(c, null, @"INSERT INTO deletion_retained (request_id, store, category, rows, justification, recorded_at) VALUES ($r, $s, $c, $n, $j, $t)
ON CONFLICT(request_id, store) DO UPDATE SET rows = excluded.rows, justification = excluded.justification, recorded_at = excluded.recorded_at;",
            ("$r", requestId), ("$s", store), ("$c", category), ("$n", rows), ("$j", justification), ("$t", Iso(at)));
    }

    public IReadOnlyList<(string Store, string Category, int Rows, string Justification)> Retained(string requestId)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT store, category, rows, justification FROM deletion_retained WHERE request_id = $r ORDER BY store;";
        P(cmd, "$r", requestId);
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<(string, string, int, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
        return list;
    }

    // ================================================================== job checkpoints

    public string GetValue(string key)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta_kv WHERE key = $k;";
        P(cmd, "$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetValue(string key, string value)
    {
        using SqliteConnection c = Open();
        Execute(c, null, "INSERT INTO meta_kv (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", ("$k", key), ("$v", value));
    }

    /// <summary>Counts rows naming a player in each meta table (deletion tests and the operator's verification).</summary>
    public IReadOnlyDictionary<string, int> RowsNaming(string playerId)
    {
        using SqliteConnection c = Open();
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string table in new[] { "reward_ledger", "entitlement_ledger", "daily_progress", "equipment", "ad_tickets", "profiles", "analytics_links" })
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM " + table + " WHERE player = $p;";
            P(cmd, "$p", playerId);
            d[table] = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM reward_ledger WHERE reference LIKE $r;";
            P(cmd, "$r", "%" + playerId + "%");
            d["reward_ledger.reference"] = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return d;
    }

    // ================================================================== helpers

    private static int Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string name, object value) in args) P(cmd, name, value);
        return cmd.ExecuteNonQuery();
    }

    private static void P(SqliteCommand cmd, string name, object value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    internal static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseIso(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void Exec(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object Scalar(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}

/// <summary>A settled grant the meta ledger has not received yet.</summary>
public sealed record UnappliedGrant(string ResultId, string Player, int Xp, int Coins, DateTimeOffset GrantedAt, string MatchId);

/// <summary>One validated analytics event as stored (properties as canonical JSON).</summary>
public sealed record StoredAnalyticsEvent(string EventId, int Type, int SchemaVersion, string AnalyticsId, string SessionId, DateTimeOffset OccurredAt,
    int Flags, int Cohort, string PropertiesJson);

/// <summary>A durable account-deletion request (state machine in <see cref="AccountDeletionProcessor"/>).</summary>
public sealed class StoredDeletionRequest
{
    public string RequestId { get; set; }
    /// <summary>The account ID while the request is open; cleared when it completes (only the reference stays).</summary>
    public string Account { get; set; }
    public string AccountRef { get; set; }
    public DeletionChannel Channel { get; set; }
    public DeletionState State { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset DueBy { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Web form only: how to reach the requester (cleared on completion).</summary>
    public string Contact { get; set; }
    public Dictionary<string, EraseOutcome> Steps { get; } = new(StringComparer.Ordinal);

    internal string StepsText()
    {
        var sb = new StringBuilder();
        foreach (KeyValuePair<string, EraseOutcome> kv in Steps.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append((int)kv.Value).Append(';');
        return sb.ToString();
    }

    internal void ParseSteps(string text)
    {
        Steps.Clear();
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) Steps[part[..eq]] = (EraseOutcome)int.Parse(part[(eq + 1)..], CultureInfo.InvariantCulture);
        }
    }
}
