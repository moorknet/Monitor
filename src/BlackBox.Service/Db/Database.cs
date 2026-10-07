using Microsoft.Data.Sqlite;

namespace BlackBox.Db;

public static class Database
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS hardware (id INTEGER PRIMARY KEY, kind TEXT, name TEXT, identifier TEXT UNIQUE);
        CREATE TABLE IF NOT EXISTS sensor   (id INTEGER PRIMARY KEY, hardware_id INT, identifier TEXT UNIQUE,
                                             name TEXT, type TEXT, unit TEXT, tier INT, visible INT DEFAULT 1, role TEXT);
        CREATE TABLE IF NOT EXISTS sample   (ts INT, sensor_id INT, value REAL, PRIMARY KEY (sensor_id, ts)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS procname (id INTEGER PRIMARY KEY, name TEXT, path TEXT, UNIQUE(name, path));
        CREATE TABLE IF NOT EXISTS proc_sample (ts INT, pid INT, procname_id INT, cpu_pct REAL, gpu_pct REAL,
                                                ws_mb REAL, io_bps REAL, est_cpu_w REAL, est_gpu_w REAL);
        CREATE INDEX IF NOT EXISTS ix_proc_ts ON proc_sample(ts);
        -- per-minute rollup of proc_sample, maintained by the writer; serves process queries over long ranges
        CREATE TABLE IF NOT EXISTS proc_minute (ts INT, procname_id INT, n INT, cpu_sum REAL, cpu_max REAL, gpu_sum REAL, gpu_max REAL,
                                                ws_max REAL, io_sum REAL, cw_sum REAL, gw_sum REAL, w_max REAL,
                                                PRIMARY KEY (ts, procname_id)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS event (id INTEGER PRIMARY KEY, ts INT, kind TEXT, severity TEXT,
                                          source TEXT, sensor_id INT, value REAL, message TEXT);
        CREATE INDEX IF NOT EXISTS ix_event_ts ON event(ts);
        """;

    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = readOnly,
            DefaultTimeout = 5,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, readOnly
            ? "PRAGMA busy_timeout=5000; PRAGMA cache_size=-4000; PRAGMA temp_store=MEMORY;"
            : "PRAGMA busy_timeout=5000; PRAGMA cache_size=-4000;");
        return c;
    }

    /// <summary>Open the writer connection, verify integrity after an unclean shutdown, create schema.</summary>
    public static SqliteConnection OpenForWrite(string path, bool runQuickCheck, ILogger log, out string? resetReason)
    {
        resetReason = null;
        if (File.Exists(path) && runQuickCheck)
        {
            string result;
            try
            {
                using var probe = Open(path);
                result = Scalar<string>(probe, "PRAGMA quick_check") ?? "null";
            }
            catch (Exception ex) { result = ex.Message; }
            SqliteConnection.ClearAllPools();
            if (result != "ok")
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                log.LogError("quick_check failed ({result}); moving DB aside", result);
                foreach (var ext in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + ext)) File.Move(path + ext, path.Replace(".db", $".corrupt-{stamp}.db") + ext);
                resetReason = $"quick_check failed: {result}";
            }
        }

        bool fresh = !File.Exists(path);
        var c = Open(path);
        if (fresh) Exec(c, "PRAGMA auto_vacuum=INCREMENTAL;");   // must precede the first table
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA journal_size_limit=33554432; PRAGMA wal_autocheckpoint=2000;");
        Exec(c, Schema);
        // schema migration for DBs created before 'role' existed
        try { Exec(c, "ALTER TABLE sensor ADD COLUMN role TEXT"); } catch (SqliteException) { }
        return c;
    }

    public static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static T? Scalar<T>(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        if (r is null or DBNull) return default;
        return (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    /// <summary>Newest sample timestamp, via one index seek per sensor (MAX(ts) over the whole table would be a full scan).</summary>
    public static long LastSampleTs(SqliteConnection c) =>
        Scalar<long?>(c, "SELECT MAX(m) FROM (SELECT (SELECT MAX(ts) FROM sample WHERE sensor_id = s.id) AS m FROM sensor s)") ?? 0;

    public static long FileSize(string path)
    {
        long n = 0;
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var f = new FileInfo(path + ext);
            if (f.Exists) n += f.Length;
        }
        return n;
    }
}
