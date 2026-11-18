using Microsoft.Data.Sqlite;
using SaveFileSync.Core.Model;

namespace SaveFileSync.Core.State;

/// <summary>Persisted per-unit sync state on this device.</summary>
public sealed record UnitStateRecord(
    string UnitId,
    string? BaseVersionId,
    DateTime? LastSyncUtc,
    string? ConflictJson);

/// <summary>Hash cache entry: if size and mtime are unchanged the hash is reused.</summary>
public sealed record IndexEntry(string Key, long Size, long MtimeTicks, string Sha256);

public interface ILocalState : IDisposable
{
    UnitStateRecord? GetUnitState(string unitId);

    void SaveUnitState(UnitStateRecord record);

    void DeleteUnit(string unitId);

    IReadOnlyDictionary<string, IndexEntry> GetFileIndex(string unitId);

    void ReplaceFileIndex(string unitId, IEnumerable<IndexEntry> entries);

    VersionManifest? GetCachedVersion(string unitId, string versionId, out bool complete);

    void PutCachedVersion(VersionManifest version, bool complete);

    string? GetJournal(string unitId);

    void SetJournal(string unitId, string targetVersionId, DateTime utcNow);

    void ClearJournal(string unitId);

    long NextSeq();

    /// <summary>Makes sure future sequence numbers are greater than <paramref name="seq"/>.</summary>
    void EnsureSeqAbove(long seq);
}

/// <summary>SQLite implementation of <see cref="ILocalState"/> (<c>state.db</c>, PLAN.md §5).</summary>
public sealed class SqliteLocalState : ILocalState
{
    private readonly SqliteConnection _db;
    private readonly Lock _lock = new();

    public SqliteLocalState(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS unit_state(
                unit_id TEXT PRIMARY KEY,
                base_version_id TEXT NULL,
                last_sync_utc TEXT NULL,
                conflict_json TEXT NULL);
            CREATE TABLE IF NOT EXISTS file_index(
                unit_id TEXT NOT NULL,
                key TEXT NOT NULL COLLATE NOCASE,
                size INTEGER NOT NULL,
                mtime_ticks INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                PRIMARY KEY(unit_id, key));
            CREATE TABLE IF NOT EXISTS version_cache(
                unit_id TEXT NOT NULL,
                version_id TEXT NOT NULL,
                complete INTEGER NOT NULL,
                json BLOB NOT NULL,
                PRIMARY KEY(unit_id, version_id));
            CREATE TABLE IF NOT EXISTS apply_journal(
                unit_id TEXT PRIMARY KEY,
                target_version_id TEXT NOT NULL,
                started_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS seq(
                id INTEGER PRIMARY KEY CHECK (id = 1),
                next INTEGER NOT NULL);
            INSERT OR IGNORE INTO seq(id, next) VALUES (1, 1);
            """);
    }

    public UnitStateRecord? GetUnitState(string unitId)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT base_version_id, last_sync_utc, conflict_json FROM unit_state WHERE unit_id=$u", ("$u", unitId));
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return null;
            }

            return new UnitStateRecord(
                unitId,
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? null : DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                r.IsDBNull(2) ? null : r.GetString(2));
        }
    }

    public void SaveUnitState(UnitStateRecord record)
    {
        lock (_lock)
        {
            using var cmd = Command(
                """
                INSERT INTO unit_state(unit_id, base_version_id, last_sync_utc, conflict_json)
                VALUES ($u, $b, $t, $c)
                ON CONFLICT(unit_id) DO UPDATE SET
                    base_version_id=excluded.base_version_id,
                    last_sync_utc=excluded.last_sync_utc,
                    conflict_json=excluded.conflict_json
                """,
                ("$u", record.UnitId),
                ("$b", record.BaseVersionId),
                ("$t", record.LastSyncUtc?.ToString("O")),
                ("$c", record.ConflictJson));
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteUnit(string unitId)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var table in new[] { "unit_state", "file_index", "version_cache", "apply_journal" })
            {
                using var cmd = Command($"DELETE FROM {table} WHERE unit_id=$u", ("$u", unitId));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IReadOnlyDictionary<string, IndexEntry> GetFileIndex(string unitId)
    {
        lock (_lock)
        {
            var result = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
            using var cmd = Command("SELECT key, size, mtime_ticks, sha256 FROM file_index WHERE unit_id=$u", ("$u", unitId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var e = new IndexEntry(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3));
                result[e.Key] = e;
            }

            return result;
        }
    }

    public void ReplaceFileIndex(string unitId, IEnumerable<IndexEntry> entries)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var del = Command("DELETE FROM file_index WHERE unit_id=$u", ("$u", unitId)))
            {
                del.Transaction = tx;
                del.ExecuteNonQuery();
            }

            using var ins = Command("INSERT OR REPLACE INTO file_index(unit_id, key, size, mtime_ticks, sha256) VALUES ($u,$k,$s,$m,$h)");
            ins.Transaction = tx;
            var pu = ins.Parameters.Add("$u", SqliteType.Text);
            var pk = ins.Parameters.Add("$k", SqliteType.Text);
            var ps = ins.Parameters.Add("$s", SqliteType.Integer);
            var pm = ins.Parameters.Add("$m", SqliteType.Integer);
            var ph = ins.Parameters.Add("$h", SqliteType.Text);
            foreach (var e in entries)
            {
                pu.Value = unitId;
                pk.Value = e.Key;
                ps.Value = e.Size;
                pm.Value = e.MtimeTicks;
                ph.Value = e.Sha256;
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public VersionManifest? GetCachedVersion(string unitId, string versionId, out bool complete)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT complete, json FROM version_cache WHERE unit_id=$u AND version_id=$v", ("$u", unitId), ("$v", versionId));
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                complete = false;
                return null;
            }

            complete = r.GetInt64(0) != 0;
            return Json.Deserialize<VersionManifest>((byte[])r.GetValue(1));
        }
    }

    public void PutCachedVersion(VersionManifest version, bool complete)
    {
        lock (_lock)
        {
            using var cmd = Command(
                "INSERT OR REPLACE INTO version_cache(unit_id, version_id, complete, json) VALUES ($u,$v,$c,$j)",
                ("$u", version.UnitId),
                ("$v", version.VersionId),
                ("$c", complete ? 1 : 0),
                ("$j", Json.Serialize(version)));
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetJournal(string unitId)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT target_version_id FROM apply_journal WHERE unit_id=$u", ("$u", unitId));
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetJournal(string unitId, string targetVersionId, DateTime utcNow)
    {
        lock (_lock)
        {
            using var cmd = Command(
                "INSERT OR REPLACE INTO apply_journal(unit_id, target_version_id, started_utc) VALUES ($u,$v,$t)",
                ("$u", unitId), ("$v", targetVersionId), ("$t", utcNow.ToString("O")));
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearJournal(string unitId)
    {
        lock (_lock)
        {
            using var cmd = Command("DELETE FROM apply_journal WHERE unit_id=$u", ("$u", unitId));
            cmd.ExecuteNonQuery();
        }
    }

    public long NextSeq()
    {
        lock (_lock)
        {
            using var cmd = Command("UPDATE seq SET next = next + 1 WHERE id = 1 RETURNING next - 1");
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void EnsureSeqAbove(long seq)
    {
        lock (_lock)
        {
            using var cmd = Command("UPDATE seq SET next = MAX(next, $s + 1) WHERE id = 1", ("$s", seq));
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _db.Dispose();

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }
}
