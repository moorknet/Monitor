using System.Collections.Concurrent;
using System.Diagnostics;
using BlackBox.Energy;
using BlackBox.Sensors;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BlackBox.Db;

public struct SampleRow { public long Ts; public int SensorId; public double Value; }

public sealed class ProcKey
{
    public required string Name { get; init; }
    public string? Path { get; set; }
    public int Id;                         // procname.id, resolved lazily by the writer
}

public struct ProcRow
{
    public long Ts; public int Pid; public ProcKey Key;
    public float Cpu, Gpu, WsMb, IoBps, EstCpuW, EstGpuW;
}

public sealed record EventRow(long Ts, string Kind, string Severity, string Source, int? SensorId, double? Value, string? Message);

/// <summary>
/// Owns the single write connection. Producers append to in-memory buffers (no allocation per sample beyond
/// occasional buffer growth); every CommitIntervalMs the buffers are swapped and written in one transaction
/// with reused prepared statements. Also runs hourly retention.
/// </summary>
public sealed class Writer : IDisposable
{
    readonly Config _cfg;
    readonly ILogger _log;
    readonly object _dbLock = new(), _bufLock = new();
    SqliteConnection _c = null!;
    // hot-path inserts use raw prepared statements: bind_int64/bind_double never box, so a commit allocates nothing per row
    sqlite3_stmt _insSample = null!, _insProc = null!, _upMinute = null!;
    struct MinuteAgg { public long N; public double CpuSum, CpuMax, GpuSum, GpuMax, WsMax, IoSum, CwSum, GwSum, WMax; }
    readonly Dictionary<(long ts, int id), MinuteAgg> _minutes = new();
    readonly Dictionary<(long ts, int id), double> _procHourWh = new();
    readonly EnergyMeter? _meter;
    readonly EnergyMeter.Acc[] _energy = new EnergyMeter.Acc[4];
    sqlite3_stmt _upEnergy = null!, _upProcHour = null!;
    SqliteCommand _insEvent = null!;
    SqliteParameter[] _pE = null!;

    SampleRow[] _samples = new SampleRow[2048], _samplesOut = new SampleRow[2048];
    ProcRow[] _procs = new ProcRow[256], _procsOut = new ProcRow[256];
    int _nSamples, _nProcs;
    readonly ConcurrentQueue<EventRow> _events = new();
    readonly Dictionary<(string, string?), int> _procNames = new();

    // stats for /api/status
    public long RowsWritten, Commits, LastCommitTs, LastSampleTsCommitted;
    public double LastCommitMs, MaxCommitMs;
    public DateTime? LastRetention;
    public string? ResetReason { get; private set; }

    public Writer(Config cfg, ILogger<Writer> log, EnergyMeter? meter = null) { _cfg = cfg; _log = log; _meter = meter; }

    public void Open(bool uncleanShutdown)
    {
        _c = Database.OpenForWrite(_cfg.DbPath, uncleanShutdown, _log, out var reset);
        ResetReason = reset;
        _insSample = Raw("INSERT OR REPLACE INTO sample(ts, sensor_id, value) VALUES (?,?,?)");
        _insProc = Raw("INSERT INTO proc_sample(ts,pid,procname_id,cpu_pct,gpu_pct,ws_mb,io_bps,est_cpu_w,est_gpu_w) VALUES (?,?,?,?,?,?,?,?,?)");
        _upMinute = Raw("""
            INSERT INTO proc_minute(ts,procname_id,n,cpu_sum,cpu_max,gpu_sum,gpu_max,ws_max,io_sum,cw_sum,gw_sum,w_max) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(ts, procname_id) DO UPDATE SET n=n+excluded.n, cpu_sum=cpu_sum+excluded.cpu_sum, cpu_max=MAX(cpu_max,excluded.cpu_max),
              gpu_sum=gpu_sum+excluded.gpu_sum, gpu_max=MAX(gpu_max,excluded.gpu_max), ws_max=MAX(ws_max,excluded.ws_max),
              io_sum=io_sum+excluded.io_sum, cw_sum=cw_sum+excluded.cw_sum, gw_sum=gw_sum+excluded.gw_sum, w_max=MAX(w_max,excluded.w_max)
            """);
        _upEnergy = Raw("""
            INSERT INTO energy_quarter(ts, cpu_wh, gpu_wh, wall_wh, seconds) VALUES (?,?,?,?,?)
            ON CONFLICT(ts) DO UPDATE SET cpu_wh=cpu_wh+excluded.cpu_wh, gpu_wh=gpu_wh+excluded.gpu_wh,
              wall_wh=wall_wh+excluded.wall_wh, seconds=seconds+excluded.seconds
            """);
        _upProcHour = Raw("""
            INSERT INTO proc_energy_hour(ts, procname_id, wh) VALUES (?,?,?)
            ON CONFLICT(ts, procname_id) DO UPDATE SET wh = wh + excluded.wh
            """);
        _insEvent = Prep("INSERT INTO event(ts,kind,severity,source,sensor_id,value,message) VALUES ($1,$2,$3,$4,$5,$6,$7)", 7, out _pE);
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT id, name, path FROM procname";
        using var r = cmd.ExecuteReader();
        while (r.Read()) _procNames[(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2))] = r.GetInt32(0);
    }

    sqlite3_stmt Raw(string sql)
    {
        int rc = raw.sqlite3_prepare_v2(_c.Handle, sql, out var stmt);
        if (rc != raw.SQLITE_OK) throw new InvalidOperationException($"prepare failed ({rc}): {raw.sqlite3_errmsg(_c.Handle).utf8_to_string()}");
        return stmt;
    }

    // NaN/Infinity would be stored as NULL, and NULL + x = NULL would poison the additive rollups forever
    static void BindD(sqlite3_stmt st, int i, double v) => raw.sqlite3_bind_double(st, i, double.IsFinite(v) ? v : 0);

    void Step(sqlite3_stmt st)
    {
        int rc = raw.sqlite3_step(st);
        raw.sqlite3_reset(st);
        if (rc != raw.SQLITE_DONE) throw new SqliteException(raw.sqlite3_errmsg(_c.Handle).utf8_to_string(), rc);
    }

    SqliteCommand Prep(string sql, int n, out SqliteParameter[] ps)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        ps = new SqliteParameter[n];
        for (int i = 0; i < n; i++) ps[i] = cmd.Parameters.Add("$" + (i + 1), SqliteType.Real);
        cmd.Prepare();
        return cmd;
    }

    public SqliteConnection Connection => _c;
    public T WithConnection<T>(Func<SqliteConnection, T> f) { lock (_dbLock) return f(_c); }

    // ---------- producers ----------
    public void AddSample(long ts, int sensorId, double value)
    {
        lock (_bufLock)
        {
            if (_nSamples == _samples.Length) Array.Resize(ref _samples, _samples.Length * 2);
            ref var r = ref _samples[_nSamples++];
            r.Ts = ts; r.SensorId = sensorId; r.Value = value;
        }
    }

    public void AddProc(in ProcRow row)
    {
        lock (_bufLock)
        {
            if (_nProcs == _procs.Length) Array.Resize(ref _procs, _procs.Length * 2);
            _procs[_nProcs++] = row;
        }
    }

    public void AddEvent(EventRow e) => _events.Enqueue(e);
    public void Event(string kind, string severity, string message, string source = "blackbox", int? sensorId = null, double? value = null, long? ts = null)
        => _events.Enqueue(new EventRow(ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, severity, source, sensorId, value, message));

    // ---------- registration (rare, synchronous) ----------
    public void Register(List<HwGroup> groups)
    {
        lock (_dbLock)
        {
            using var tx = _c.BeginTransaction();
            foreach (var g in groups)
            {
                g.Id = Database.Scalar<int>(_c, """
                    INSERT INTO hardware(kind, name, identifier) VALUES ($k,$n,$i)
                    ON CONFLICT(identifier) DO UPDATE SET kind=excluded.kind, name=excluded.name RETURNING id
                    """, ("$k", g.Kind), ("$n", g.Name), ("$i", g.Identifier));
                foreach (var s in g.Sensors)
                    s.Id = Database.Scalar<int>(_c, """
                        INSERT INTO sensor(hardware_id, identifier, name, type, unit, tier, role) VALUES ($h,$i,$n,$t,$u,$tier,$r)
                        ON CONFLICT(identifier) DO UPDATE SET hardware_id=excluded.hardware_id, name=excluded.name, type=excluded.type,
                          unit=excluded.unit, tier=excluded.tier, role=excluded.role RETURNING id
                        """, ("$h", g.Id), ("$i", s.Identifier), ("$n", s.Name), ("$t", s.Type), ("$u", s.Unit), ("$tier", s.Tier), ("$r", s.Role));
            }
            tx.Commit();
        }
    }

    // ---------- consumer ----------
    public void Flush()
    {
        int nS, nP;
        lock (_bufLock)
        {
            (_samples, _samplesOut) = (_samplesOut, _samples);
            (_procs, _procsOut) = (_procsOut, _procs);
            nS = _nSamples; nP = _nProcs;
            _nSamples = 0; _nProcs = 0;
            // keep both halves the same size so a swap never hands producers a smaller buffer than they needed
            if (_samples.Length < _samplesOut.Length) _samples = new SampleRow[_samplesOut.Length];
            if (_procs.Length < _procsOut.Length) _procs = new ProcRow[_procsOut.Length];
        }
        int nEn = _meter?.Drain(_energy) ?? 0;
        if (nS == 0 && nP == 0 && nEn == 0 && _events.IsEmpty) return;

        long t0 = Stopwatch.GetTimestamp();
        lock (_dbLock)
        {
            using var tx = _c.BeginTransaction();
            _insEvent.Transaction = tx;
            long maxTs = 0;
            for (int i = 0; i < nS; i++)
            {
                ref var r = ref _samplesOut[i];
                raw.sqlite3_bind_int64(_insSample, 1, r.Ts);
                raw.sqlite3_bind_int(_insSample, 2, r.SensorId);
                BindD(_insSample, 3, r.Value);
                Step(_insSample);
                if (r.Ts > maxTs) maxTs = r.Ts;
            }
            for (int i = 0; i < nP; i++)
            {
                ref var r = ref _procsOut[i];
                if (r.Key.Id == 0) r.Key.Id = ProcNameId(r.Key);
                var st = _insProc;
                raw.sqlite3_bind_int64(st, 1, r.Ts); raw.sqlite3_bind_int(st, 2, r.Pid); raw.sqlite3_bind_int(st, 3, r.Key.Id);
                BindD(st, 4, Math.Round(r.Cpu, 2)); BindD(st, 5, Math.Round(r.Gpu, 2));
                BindD(st, 6, Math.Round(r.WsMb)); BindD(st, 7, Math.Round(r.IoBps));
                BindD(st, 8, Math.Round(r.EstCpuW, 2)); BindD(st, 9, Math.Round(r.EstGpuW, 2));
                Step(st);
                ref var m = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_minutes, (r.Ts / 60_000 * 60_000, r.Key.Id), out _);
                m.N++; m.CpuSum += r.Cpu; m.GpuSum += r.Gpu; m.IoSum += r.IoBps; m.CwSum += r.EstCpuW; m.GwSum += r.EstGpuW;
                m.CpuMax = Math.Max(m.CpuMax, r.Cpu); m.GpuMax = Math.Max(m.GpuMax, r.Gpu); m.WsMax = Math.Max(m.WsMax, r.WsMb);
                m.WMax = Math.Max(m.WMax, r.EstCpuW + r.EstGpuW);
                ref var ph = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_procHourWh, (r.Ts / 3_600_000 * 3_600_000, r.Key.Id), out _);
                ph += (r.EstCpuW + r.EstGpuW) * _cfg.ProcessIntervalS / 3600.0;
                r.Key = null!; // drop reference
            }
            foreach (var ((mts, mid), m) in _minutes)
            {
                var st = _upMinute;
                raw.sqlite3_bind_int64(st, 1, mts); raw.sqlite3_bind_int(st, 2, mid); raw.sqlite3_bind_int64(st, 3, m.N);
                BindD(st, 4, Math.Round(m.CpuSum, 2)); BindD(st, 5, Math.Round(m.CpuMax, 2));
                BindD(st, 6, Math.Round(m.GpuSum, 2)); BindD(st, 7, Math.Round(m.GpuMax, 2));
                BindD(st, 8, Math.Round(m.WsMax)); BindD(st, 9, Math.Round(m.IoSum));
                BindD(st, 10, Math.Round(m.CwSum, 2)); BindD(st, 11, Math.Round(m.GwSum, 2));
                BindD(st, 12, Math.Round(m.WMax, 2));
                Step(st);
            }
            _minutes.Clear();
            foreach (var ((hts, hid), wh) in _procHourWh)
            {
                raw.sqlite3_bind_int64(_upProcHour, 1, hts); raw.sqlite3_bind_int(_upProcHour, 2, hid);
                BindD(_upProcHour, 3, wh);
                Step(_upProcHour);
            }
            _procHourWh.Clear();
            for (int i = 0; i < nEn; i++)
            {
                ref var e = ref _energy[i];
                raw.sqlite3_bind_int64(_upEnergy, 1, e.Ts); BindD(_upEnergy, 2, e.CpuWh);
                BindD(_upEnergy, 3, e.GpuWh); BindD(_upEnergy, 4, e.WallWh);
                BindD(_upEnergy, 5, e.Seconds);
                Step(_upEnergy);
            }
            int nE = 0;
            while (_events.TryDequeue(out var e))
            {
                _pE[0].Value = e.Ts; _pE[1].Value = e.Kind; _pE[2].Value = e.Severity; _pE[3].Value = e.Source;
                _pE[4].Value = (object?)e.SensorId ?? DBNull.Value; _pE[5].Value = (object?)e.Value ?? DBNull.Value;
                _pE[6].Value = (object?)e.Message ?? DBNull.Value;
                _insEvent.ExecuteNonQuery();
                nE++;
            }
            tx.Commit();
            RowsWritten += nS + nP + nE;
            Commits++;
            if (maxTs > 0) LastSampleTsCommitted = maxTs;
        }
        LastCommitMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (LastCommitMs > MaxCommitMs) MaxCommitMs = LastCommitMs;
        LastCommitTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    int ProcNameId(ProcKey k)
    {
        if (_procNames.TryGetValue((k.Name, k.Path), out var id)) return id;
        id = Database.Scalar<int>(_c, "INSERT INTO procname(name, path) VALUES ($n,$p) ON CONFLICT(name, path) DO UPDATE SET name=excluded.name RETURNING id",
            ("$n", k.Name), ("$p", k.Path));
        _procNames[(k.Name, k.Path)] = id;
        return id;
    }

    /// <summary>Delete rows older than the retention window in small chunks, then give back excess free pages.</summary>
    public void Retention()
    {
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _cfg.RetentionMs;
        long deleted = 0;
        var sw = Stopwatch.StartNew();
        List<int> ids;
        lock (_dbLock)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT id FROM sensor";
            using var r = cmd.ExecuteReader();
            ids = new();
            while (r.Read()) ids.Add(r.GetInt32(0));
        }
        // per-sensor deletes are range deletes on the PK and stay small (≈1 h of rows per sensor per run)
        foreach (var id in ids)
            lock (_dbLock) deleted += Del("DELETE FROM sample WHERE sensor_id = $id AND ts < $c", ("$id", id), ("$c", cutoff));
        lock (_dbLock) deleted += Del("DELETE FROM proc_minute WHERE ts < $c", ("$c", cutoff));
        long longCut = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _cfg.LongRetentionMs;
        foreach (var t in new[] { "energy_quarter", "proc_energy_hour", "price" })
            lock (_dbLock) deleted += Del($"DELETE FROM {t} WHERE ts < $c", ("$c", longCut));
        foreach (var table in new[] { "proc_sample", "event" })
        {
            int n;
            do
            {
                lock (_dbLock) n = Del($"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE ts < $c LIMIT 50000)", ("$c", cutoff));
                deleted += n;
            } while (n == 50000);
        }
        lock (_dbLock)
        {
            // Steady state reuses freed pages; only shrink when the free list exceeds 10 % of the file.
            long pages = Database.Scalar<long>(_c, "PRAGMA page_count"), free = Database.Scalar<long>(_c, "PRAGMA freelist_count");
            if (free > pages / 10) Database.Exec(_c, $"PRAGMA incremental_vacuum({free - pages / 20})");
            Database.Exec(_c, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        LastRetention = DateTime.UtcNow;
        _log.LogInformation("Retention: deleted {n} rows older than {h} h in {ms} ms", deleted, _cfg.RetentionHours, sw.ElapsedMilliseconds);
    }

    int Del(string sql, params (string, object?)[] ps)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteNonQuery();
    }

    bool _disposed;
    public void Dispose()
    {
        if (_disposed || _c == null) return;
        _disposed = true;
        try { Flush(); } catch (Exception ex) { _log.LogError(ex, "final flush failed"); }
        lock (_dbLock)
        {
            _insSample?.Dispose(); _insProc?.Dispose(); _upMinute?.Dispose(); _upEnergy?.Dispose(); _upProcHour?.Dispose(); _insEvent?.Dispose();
            try { if (_c != null) Database.Exec(_c, "PRAGMA wal_checkpoint(TRUNCATE)"); } catch { }
            _c?.Dispose();
        }
    }
}
