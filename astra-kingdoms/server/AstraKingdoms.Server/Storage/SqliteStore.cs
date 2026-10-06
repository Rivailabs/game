using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AstraKingdoms.Server.Storage;

/// <summary>
/// SQLite implementation of every store (matches, reward ledger, audit trail, grievances).
/// <para>
/// The audit table is append-only at the database level: triggers abort UPDATE and DELETE. Reward
/// grants have a primary key on (result_id, player) and finalization runs in one transaction with
/// the match row, so a retried or duplicated finalization cannot grant twice. WAL mode lets the
/// readiness probe and readers run beside the single writer.
/// </para>
/// </summary>
public sealed class SqliteStore : IMatchRepository, IRewardLedger, IAuditLog, IGrievanceStore
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;

    public SqliteStore(string path, TimeProvider time, int retentionDays)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 30, Pooling = true }.ToString();
        _time = time;
        _retention = TimeSpan.FromDays(retentionDays);
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        // WAL with synchronous=NORMAL: commits are atomic and the database cannot corrupt; a power
        // loss may drop the last commits, which recovery treats like any interrupted match.
        using (SqliteCommand pragma = c.CreateCommand())
        {
            pragma.CommandText = "PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
        return c;
    }

    private void Migrate()
    {
        using SqliteConnection c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, @"
CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS matches (
  match_id TEXT PRIMARY KEY,
  status TEXT NOT NULL,
  origin TEXT NOT NULL,
  ranked INTEGER NOT NULL,
  player_a TEXT NOT NULL,
  player_b TEXT NOT NULL,
  kind_a TEXT NOT NULL,
  kind_b TEXT NOT NULL,
  bot_difficulty TEXT,
  rules_version TEXT NOT NULL,
  rules_hash TEXT NOT NULL,
  record_json TEXT NOT NULL,
  phase_deadline_ms INTEGER,
  remaining_ms_at_suspend INTEGER,
  setup_deadline_ms INTEGER NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  suspended_at TEXT,
  outcome TEXT,
  outcome_detail TEXT,
  result_json TEXT,
  result_id TEXT UNIQUE,
  retain_until TEXT
);
CREATE INDEX IF NOT EXISTS ix_matches_status ON matches(status);
CREATE TABLE IF NOT EXISTS reward_grants (
  result_id TEXT NOT NULL,
  player TEXT NOT NULL,
  xp INTEGER NOT NULL,
  coins INTEGER NOT NULL,
  granted_at TEXT NOT NULL,
  PRIMARY KEY (result_id, player)
);
CREATE TABLE IF NOT EXISTS audit_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  at TEXT NOT NULL,
  match_id TEXT,
  actor TEXT NOT NULL,
  action TEXT NOT NULL,
  detail TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_audit_match ON audit_log(match_id);
CREATE TRIGGER IF NOT EXISTS audit_no_update BEFORE UPDATE ON audit_log BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
CREATE TRIGGER IF NOT EXISTS audit_no_delete BEFORE DELETE ON audit_log BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
CREATE TABLE IF NOT EXISTS grievances (
  id TEXT PRIMARY KEY,
  received_at TEXT NOT NULL,
  reporter TEXT,
  category TEXT NOT NULL,
  match_id TEXT,
  description TEXT NOT NULL,
  contact TEXT,
  status TEXT NOT NULL
);");
        long version = Convert.ToInt64(Scalar(c, "SELECT COALESCE(MAX(version), 0) FROM schema_version;"), CultureInfo.InvariantCulture);
        if (version == 0) Exec(c, "INSERT INTO schema_version(version) VALUES (" + SchemaVersion + ");");
        else if (version > SchemaVersion)
            throw new InvalidOperationException("Database schema " + version + " is newer than this server (" + SchemaVersion + "); roll forward or restore a matching backup.");
    }

    // ------------------------------------------------------------------ matches

    public void Save(StoredMatch m)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        WriteMatch(cmd, m);
        cmd.ExecuteNonQuery();
    }

    private static void WriteMatch(SqliteCommand cmd, StoredMatch m)
    {
        cmd.CommandText = @"
INSERT INTO matches (match_id, status, origin, ranked, player_a, player_b, kind_a, kind_b, bot_difficulty, rules_version, rules_hash,
  record_json, phase_deadline_ms, remaining_ms_at_suspend, setup_deadline_ms, created_at, updated_at, suspended_at, outcome, outcome_detail,
  result_json, result_id, retain_until)
VALUES ($id, $status, $origin, $ranked, $a, $b, $ka, $kb, $bot, $rv, $rh, $record, $deadline, $remaining, $setup, $created, $updated,
  $suspended, $outcome, $detail, $result, $resultId, $retain)
ON CONFLICT(match_id) DO UPDATE SET status=excluded.status, record_json=excluded.record_json, phase_deadline_ms=excluded.phase_deadline_ms,
  remaining_ms_at_suspend=excluded.remaining_ms_at_suspend, updated_at=excluded.updated_at, suspended_at=excluded.suspended_at,
  outcome=excluded.outcome, outcome_detail=excluded.outcome_detail, result_json=excluded.result_json, result_id=excluded.result_id,
  retain_until=excluded.retain_until;";
        P(cmd, "$id", m.MatchId); P(cmd, "$status", m.Status); P(cmd, "$origin", m.Origin); P(cmd, "$ranked", m.Ranked ? 1 : 0);
        P(cmd, "$a", m.PlayerA); P(cmd, "$b", m.PlayerB); P(cmd, "$ka", m.KindA); P(cmd, "$kb", m.KindB); P(cmd, "$bot", m.BotDifficulty);
        P(cmd, "$rv", m.RulesVersion); P(cmd, "$rh", m.RulesHashHex); P(cmd, "$record", m.RecordJson); P(cmd, "$deadline", m.PhaseDeadlineMs);
        P(cmd, "$remaining", m.RemainingMsAtSuspend); P(cmd, "$setup", m.SetupDeadlineMs); P(cmd, "$created", Iso(m.CreatedAt));
        P(cmd, "$updated", Iso(m.UpdatedAt)); P(cmd, "$suspended", m.SuspendedAt.HasValue ? Iso(m.SuspendedAt.Value) : null);
        P(cmd, "$outcome", m.Outcome); P(cmd, "$detail", m.OutcomeDetail); P(cmd, "$result", m.ResultJson); P(cmd, "$resultId", m.ResultId);
        P(cmd, "$retain", m.RetainUntil.HasValue ? Iso(m.RetainUntil.Value) : null);
    }

    public StoredMatch Get(string matchId)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM matches WHERE match_id = $id;";
        P(cmd, "$id", matchId);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? ReadMatch(r) : null;
    }

    public IReadOnlyList<StoredMatch> ListByStatus(string status)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM matches WHERE status = $s ORDER BY created_at;";
        P(cmd, "$s", status);
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<StoredMatch>();
        while (r.Read()) list.Add(ReadMatch(r));
        return list;
    }

    public IReadOnlyList<RewardGrant> Finalize(StoredMatch m, IReadOnlyList<RewardGrant> grants)
    {
        m.RetainUntil ??= _time.GetUtcNow() + _retention;
        using SqliteConnection c = Open();
        using SqliteTransaction tx = c.BeginTransaction();
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            WriteMatch(cmd, m);
            cmd.ExecuteNonQuery();
        }
        var granted = new List<RewardGrant>();
        foreach (RewardGrant g in grants)
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO reward_grants (result_id, player, xp, coins, granted_at) VALUES ($r, $p, $xp, $coins, $at);";
            P(cmd, "$r", g.ResultId); P(cmd, "$p", g.PlayerUid); P(cmd, "$xp", g.Xp); P(cmd, "$coins", g.Coins); P(cmd, "$at", Iso(_time.GetUtcNow()));
            if (cmd.ExecuteNonQuery() == 1) granted.Add(g);
        }
        tx.Commit();
        return granted;
    }

    public int PurgeExpired(DateTimeOffset now)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM matches WHERE status = $s AND retain_until IS NOT NULL AND retain_until < $now;";
        P(cmd, "$s", MatchStatus.Finished);
        P(cmd, "$now", Iso(now));
        return cmd.ExecuteNonQuery();
    }

    public bool Ping()
    {
        try
        {
            using SqliteConnection c = Open();
            return Convert.ToInt64(Scalar(c, "SELECT 1;"), CultureInfo.InvariantCulture) == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ rewards

    public IReadOnlyList<RewardGrant> GrantsFor(string playerUid) =>
        Grants("SELECT result_id, player, xp, coins FROM reward_grants WHERE player = $v ORDER BY granted_at;", playerUid);

    public IReadOnlyList<RewardGrant> GrantsForResult(string resultId) =>
        Grants("SELECT result_id, player, xp, coins FROM reward_grants WHERE result_id = $v ORDER BY player;", resultId);

    private IReadOnlyList<RewardGrant> Grants(string sql, string value)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        P(cmd, "$v", value);
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<RewardGrant>();
        while (r.Read()) list.Add(new RewardGrant(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    // ------------------------------------------------------------------ audit

    public void Append(string matchId, string actorRef, string action, string detail)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_log (at, match_id, actor, action, detail) VALUES ($at, $m, $actor, $action, $detail);";
        P(cmd, "$at", Iso(_time.GetUtcNow())); P(cmd, "$m", matchId); P(cmd, "$actor", actorRef ?? "server");
        P(cmd, "$action", action); P(cmd, "$detail", detail ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditEntry> Read(string matchId, int limit = 1000)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = matchId == null
            ? "SELECT id, at, match_id, actor, action, detail FROM audit_log ORDER BY id LIMIT $limit;"
            : "SELECT id, at, match_id, actor, action, detail FROM audit_log WHERE match_id = $m ORDER BY id LIMIT $limit;";
        P(cmd, "$m", matchId);
        P(cmd, "$limit", limit);
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<AuditEntry>();
        while (r.Read())
            list.Add(new AuditEntry(r.GetInt64(0), ParseIso(r.GetString(1)), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5)));
        return list;
    }

    // ------------------------------------------------------------------ grievances

    public void Add(Grievance g)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO grievances (id, received_at, reporter, category, match_id, description, contact, status)
VALUES ($id, $at, $reporter, $category, $match, $description, $contact, $status);";
        P(cmd, "$id", g.Id); P(cmd, "$at", Iso(g.ReceivedAt)); P(cmd, "$reporter", g.ReporterRef); P(cmd, "$category", g.Category);
        P(cmd, "$match", g.MatchId); P(cmd, "$description", g.Description); P(cmd, "$contact", g.Contact); P(cmd, "$status", g.Status);
        cmd.ExecuteNonQuery();
    }

    public Grievance GetGrievance(string id)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, received_at, reporter, category, match_id, description, contact, status FROM grievances WHERE id = $id;";
        P(cmd, "$id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Grievance
        {
            Id = r.GetString(0),
            ReceivedAt = ParseIso(r.GetString(1)),
            ReporterRef = r.IsDBNull(2) ? null : r.GetString(2),
            Category = r.GetString(3),
            MatchId = r.IsDBNull(4) ? null : r.GetString(4),
            Description = r.GetString(5),
            Contact = r.IsDBNull(6) ? null : r.GetString(6),
            Status = r.GetString(7),
        };
    }

    Grievance IGrievanceStore.Get(string id) => GetGrievance(id);

    // ------------------------------------------------------------------ helpers

    private static StoredMatch ReadMatch(SqliteDataReader r)
    {
        string S(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
        long? L(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt64(r.GetOrdinal(col));
        return new StoredMatch
        {
            MatchId = S("match_id"),
            Status = S("status"),
            Origin = S("origin"),
            Ranked = L("ranked") == 1,
            PlayerA = S("player_a"),
            PlayerB = S("player_b"),
            KindA = S("kind_a"),
            KindB = S("kind_b"),
            BotDifficulty = S("bot_difficulty"),
            RulesVersion = S("rules_version"),
            RulesHashHex = S("rules_hash"),
            RecordJson = S("record_json"),
            PhaseDeadlineMs = L("phase_deadline_ms"),
            RemainingMsAtSuspend = L("remaining_ms_at_suspend"),
            SetupDeadlineMs = L("setup_deadline_ms") ?? 0,
            CreatedAt = ParseIso(S("created_at")),
            UpdatedAt = ParseIso(S("updated_at")),
            SuspendedAt = S("suspended_at") == null ? null : ParseIso(S("suspended_at")),
            Outcome = S("outcome"),
            OutcomeDetail = S("outcome_detail"),
            ResultJson = S("result_json"),
            ResultId = S("result_id"),
            RetainUntil = S("retain_until") == null ? null : ParseIso(S("retain_until")),
        };
    }

    private static void P(SqliteCommand cmd, string name, object value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string s) =>
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
