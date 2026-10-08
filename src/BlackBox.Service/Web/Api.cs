using System.Globalization;
using System.Text;
using System.Text.Json;
using BlackBox.Db;
using BlackBox.Sensors;
using Microsoft.Data.Sqlite;

namespace BlackBox.Web;

public static class Api
{
    const long RollupMs = 2 * 3600_000;
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static (long from, long to) Range(HttpRequest r)
    {
        long to = long.TryParse(r.Query["to"], out var t) ? t : Now;
        long from = long.TryParse(r.Query["from"], out var f) ? f : to - 3600_000;
        if (from >= to) from = to - 1000;
        return (from, to);
    }

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    static async Task WriteJson(HttpResponse res, Action<Utf8JsonWriter> body)
    {
        res.ContentType = "application/json";
        res.Headers.CacheControl = "no-store";
        await using (var w = new Utf8JsonWriter(res.BodyWriter))
            body(w);
        await res.BodyWriter.FlushAsync();
    }

    static void Num(Utf8JsonWriter w, double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) w.WriteNullValue();
        else w.WriteNumberValue(Math.Round(v, 3));
    }

    public static void Map(WebApplication app, Config cfg)
    {
        string db = cfg.DbPath;
        SqliteConnection Read() => Database.Open(db, readOnly: true);

        // ---- /api/sensors: everything ever recorded (+ live value if present now) ----
        app.MapGet("/api/sensors", async (HttpContext ctx, SensorSampler sampler) =>
        {
            var live = sampler.Groups.SelectMany(g => g.Sensors).Where(s => s.Id > 0).ToDictionary(s => s.Id);
            using var c = Read();
            using var cmd = Cmd(c, "SELECT s.id, s.name, s.type, s.unit, s.tier, s.role, s.identifier, h.name, h.kind FROM sensor s LEFT JOIN hardware h ON h.id = s.hardware_id ORDER BY h.id, s.type, s.id");
            using var r = cmd.ExecuteReader();
            await WriteJson(ctx.Response, w =>
            {
                w.WriteStartArray();
                while (r.Read())
                {
                    int id = r.GetInt32(0);
                    w.WriteStartObject();
                    w.WriteNumber("id", id);
                    w.WriteString("name", r.GetString(1));
                    w.WriteString("type", r.GetString(2));
                    w.WriteString("unit", r.GetString(3));
                    w.WriteNumber("tier", r.GetInt32(4));
                    w.WriteString("role", r.IsDBNull(5) ? null : r.GetString(5));
                    w.WriteString("identifier", r.GetString(6));
                    w.WriteString("hw", r.IsDBNull(7) ? "" : r.GetString(7));
                    w.WriteString("hw_kind", r.IsDBNull(8) ? "" : r.GetString(8));
                    w.WriteBoolean("present", live.ContainsKey(id));
                    w.WritePropertyName("last");
                    if (live.TryGetValue(id, out var s)) Num(w, s.Last); else w.WriteNullValue();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        });

        // ---- /api/series: bucketed min/max/avg on one shared time grid (uPlot-aligned) ----
        app.MapGet("/api/series", async (HttpContext ctx) =>
        {
            var (from, to) = Range(ctx.Request);
            int maxPoints = Math.Clamp(int.TryParse(ctx.Request.Query["maxPoints"], out var mp) ? mp : 2000, 50, 20000);
            var ids = ((string?)ctx.Request.Query["ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var i) ? i : -1).Where(i => i > 0).Distinct().Take(64).ToArray();
            long bucket = Math.Max(1000, (long)Math.Ceiling((to - from) / (double)maxPoints / 1000.0) * 1000);
            using var c = Read();
            var data = new Dictionary<long, double[]>[ids.Length];
            var tiers = new int[ids.Length];
            var keys = new SortedSet<long>();
            for (int i = 0; i < ids.Length; i++)
            {
                tiers[i] = Database.Scalar<int?>(c, "SELECT tier FROM sensor WHERE id=$id", ("$id", ids[i])) ?? 1;
                var d = data[i] = new();
                using var cmd = Cmd(c, """
                    SELECT (ts - $f + $h) / $b AS k, AVG(value), MIN(value), MAX(value)
                    FROM sample WHERE sensor_id = $id AND ts >= $f AND ts < $t GROUP BY k
                    """, ("$f", from), ("$t", to), ("$b", bucket), ("$h", bucket / 2), ("$id", ids[i]));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long k = r.GetInt64(0);
                    d[k] = [r.GetDouble(1), r.GetDouble(2), r.GetDouble(3)];
                    keys.Add(k);
                }
            }
            // When nothing at all was recorded for longer than the slowest series' interval (service stopped, PC off,
            // power loss), insert an explicit empty row so the chart breaks the line instead of drawing a diagonal.
            long maxGapMs = (long)(Math.Max(bucket, cfg.Tier2EveryS * 1000) * 2.5);
            var gridList = new List<long>(keys.Count + 8);
            var gapKeys = new HashSet<long>();
            long prev = long.MinValue;
            foreach (var k in keys)
            {
                if (prev != long.MinValue && (k - prev) * bucket > maxGapMs) { gridList.Add(prev + 1); gapKeys.Add(prev + 1); }
                gridList.Add(k); prev = k;
            }
            var grid = gridList.ToArray();
            await WriteJson(ctx.Response, w =>
            {
                w.WriteStartObject();
                w.WriteNumber("from", from); w.WriteNumber("to", to); w.WriteNumber("bucket", bucket);
                w.WriteStartArray("t");
                foreach (var k in grid) w.WriteNumberValue(from + k * bucket);
                w.WriteEndArray();
                w.WriteStartArray("series");
                for (int i = 0; i < ids.Length; i++)
                {
                    // sample-and-hold across this series' own sampling interval (tier 2 = every N s); real gaps stay null
                    long holdMs = (long)(Math.Max(bucket, tiers[i] == 2 ? cfg.Tier2EveryS * 1000 : 1000) * 1.6);
                    w.WriteStartObject();
                    w.WriteNumber("id", ids[i]);
                    for (int a = 0; a < 3; a++)
                    {
                        w.WriteStartArray(a == 0 ? "avg" : a == 1 ? "min" : "max");
                        double[]? last = null; long lastK = long.MinValue;
                        foreach (var k in grid)
                        {
                            if (gapKeys.Contains(k)) { w.WriteNullValue(); last = null; }
                            else if (data[i].TryGetValue(k, out var v)) { last = v; lastK = k; Num(w, v[a]); }
                            else if (last != null && (k - lastK) * bucket <= holdMs) Num(w, last[a]);
                            else w.WriteNullValue();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        });

        // ---- /api/processes: per-name aggregate over a range (energy estimate, avg/peak) ----
        // Ranges >= RollupMs read the per-minute rollup (minute granularity at the edges); shorter ranges read raw rows.
        app.MapGet("/api/processes", async (HttpContext ctx) =>
        {
            var (from, to) = Range(ctx.Request);
            bool roll = to - from >= RollupMs;
            if (roll) { from = from / 60_000 * 60_000; }
            using var c = Read();
            double interval = cfg.ProcessIntervalS;
            // one "(other)" row is written per sample tick, so its row count is the tick count
            long ticks = Database.Scalar<long?>(c, roll
                ? "SELECT SUM(n) FROM proc_minute WHERE ts >= $f AND ts < $t AND procname_id IN (SELECT id FROM procname WHERE name = '(other)')"
                : "SELECT COUNT(*) FROM proc_sample WHERE ts >= $f AND ts < $t AND pid = 0", ("$f", from), ("$t", to)) ?? 0;
            string src = roll
                ? "SELECT procname_id, SUM(n), SUM(cpu_sum), MAX(cpu_max), SUM(gpu_sum), MAX(gpu_max), MAX(ws_max), SUM(cw_sum), SUM(gw_sum), SUM(io_sum), MAX(w_max) FROM proc_minute"
                : "SELECT procname_id, COUNT(*), SUM(cpu_pct), MAX(cpu_pct), SUM(gpu_pct), MAX(gpu_pct), MAX(ws_mb), SUM(est_cpu_w), SUM(est_gpu_w), SUM(io_bps), MAX(est_cpu_w + est_gpu_w) FROM proc_sample";
            using var cmd = Cmd(c, $"""
                WITH a(id, n, cs, cm, gs, gm, ws, cw, gw, io, wm) AS ({src} WHERE ts >= $f AND ts < $t GROUP BY procname_id)
                SELECT p.name, MAX(p.path), SUM(a.n), SUM(a.cs), MAX(a.cm), SUM(a.gs), MAX(a.gm), MAX(a.ws), SUM(a.cw), SUM(a.gw), SUM(a.io), MAX(a.wm)
                FROM a JOIN procname p ON p.id = a.id
                GROUP BY p.name ORDER BY SUM(a.cw + a.gw) DESC LIMIT 60
                """, ("$f", from), ("$t", to));
            using var r = cmd.ExecuteReader();
            await WriteJson(ctx.Response, w =>
            {
                w.WriteStartObject();
                w.WriteNumber("ticks", ticks);
                w.WriteNumber("interval_s", interval);
                w.WriteBoolean("rollup", roll);
                w.WriteStartArray("rows");
                double n = Math.Max(1, ticks);
                while (r.Read())
                {
                    w.WriteStartObject();
                    w.WriteString("name", r.GetString(0));
                    w.WriteString("path", r.IsDBNull(1) ? null : r.GetString(1));
                    w.WriteNumber("rows", r.GetInt64(2));
                    w.WritePropertyName("cpu_avg"); Num(w, r.GetDouble(3) / n);      // all instances combined, averaged over the range
                    w.WritePropertyName("cpu_peak"); Num(w, r.GetDouble(4));         // single process peak
                    w.WritePropertyName("gpu_avg"); Num(w, r.GetDouble(5) / n);
                    w.WritePropertyName("gpu_peak"); Num(w, r.GetDouble(6));
                    w.WritePropertyName("ws_peak_mb"); Num(w, r.GetDouble(7));
                    w.WritePropertyName("cpu_wh"); Num(w, r.GetDouble(8) * interval / 3600);
                    w.WritePropertyName("gpu_wh"); Num(w, r.GetDouble(9) * interval / 3600);
                    w.WritePropertyName("io_avg_bps"); Num(w, r.GetDouble(10) / n);
                    w.WritePropertyName("w_peak"); Num(w, r.GetDouble(11));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        });

        // ---- /api/proc-series: est. watts per top process (by energy in range) + "other" ----
        app.MapGet("/api/proc-series", async (HttpContext ctx) =>
        {
            var (from, to) = Range(ctx.Request);
            int top = Math.Clamp(int.TryParse(ctx.Request.Query["top"], out var tp) ? tp : 8, 1, 20);
            int maxPoints = Math.Clamp(int.TryParse(ctx.Request.Query["maxPoints"], out var mp) ? mp : 1000, 50, 5000);
            bool roll = to - from >= RollupMs;
            long step = roll ? 60_000 : cfg.ProcessIntervalS * 1000L;
            long bucket = Math.Max(step, (long)Math.Ceiling((to - from) / (double)maxPoints / step) * step);
            if (roll) from = from / 60_000 * 60_000;
            using var c = Read();
            var names = new Dictionary<int, string>();
            using (var cmd = Cmd(c, "SELECT id, name FROM procname"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) names[r.GetInt32(0)] = r.GetString(1);

            // per (bucket, procname_id): summed est. watts and row count
            var cells = new List<(long k, int id, double w, long n)>();
            using (var cmd = Cmd(c, roll
                ? "SELECT (ts - $f) / $b AS k, procname_id, SUM(cw_sum + gw_sum), SUM(n) FROM proc_minute WHERE ts >= $f AND ts < $t GROUP BY k, procname_id"
                : "SELECT (ts - $f) / $b AS k, procname_id, SUM(est_cpu_w + est_gpu_w), COUNT(*) FROM proc_sample WHERE ts >= $f AND ts < $t GROUP BY k, procname_id",
                ("$f", from), ("$t", to), ("$b", bucket)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) cells.Add((r.GetInt64(0), r.GetInt32(1), r.GetDouble(2), r.GetInt64(3)));

            var energy = new Dictionary<string, double>();
            var ticksPer = new Dictionary<long, long>();
            foreach (var (k, id, wsum, n) in cells)
            {
                var name = names.GetValueOrDefault(id, "?");
                if (name == "(other)") ticksPer[k] = ticksPer.GetValueOrDefault(k) + n; // one (other) row per tick
                else energy[name] = energy.GetValueOrDefault(name) + wsum;
            }
            var topNames = energy.OrderByDescending(kv => kv.Value).Take(top).Select(kv => kv.Key).ToList();
            var idx = topNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
            var rows = new SortedDictionary<long, double[]>();
            foreach (var (k, id, wsum, _) in cells)
            {
                if (!rows.TryGetValue(k, out var arr)) rows[k] = arr = new double[topNames.Count + 1];
                arr[idx.TryGetValue(names.GetValueOrDefault(id, "?"), out var i) ? i : topNames.Count] += wsum;
            }
            await WriteJson(ctx.Response, w =>
            {
                w.WriteStartObject();
                w.WriteNumber("bucket", bucket);
                w.WriteBoolean("rollup", roll);
                w.WriteStartArray("names");
                foreach (var n in topNames) w.WriteStringValue(n);
                w.WriteStringValue("(other)");
                w.WriteEndArray();
                long gapMs = Math.Max(3 * bucket, 10_000);
                w.WriteStartArray("t");
                long prevK = long.MinValue;
                foreach (var k in rows.Keys)
                {
                    if (prevK != long.MinValue && (k - prevK) * bucket > gapMs) w.WriteNumberValue(from + (prevK + 1) * bucket); // gap row
                    w.WriteNumberValue(from + k * bucket); prevK = k;
                }
                w.WriteEndArray();
                w.WriteStartArray("series");
                for (int col = 0; col <= topNames.Count; col++)
                {
                    w.WriteStartArray();
                    prevK = long.MinValue;
                    foreach (var (k, arr) in rows)
                    {
                        if (prevK != long.MinValue && (k - prevK) * bucket > gapMs) w.WriteNullValue();
                        Num(w, arr[col] / Math.Max(1, ticksPer.GetValueOrDefault(k))); prevK = k;
                    }
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        });

        // ---- /api/events ----
        app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            var (from, to) = Range(ctx.Request);
            bool all = ctx.Request.Query["all"] == "1";
            using var c = Read();
            using var cmd = Cmd(c, $"""
                SELECT id, ts, kind, severity, source, sensor_id, value, message FROM event
                WHERE ts >= $f AND ts < $t {(all ? "" : "AND kind NOT IN ('process_start','process_exit')")}
                ORDER BY ts LIMIT 5000
                """, ("$f", from), ("$t", to));
            using var r = cmd.ExecuteReader();
            await WriteJson(ctx.Response, w =>
            {
                w.WriteStartArray();
                while (r.Read())
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", r.GetInt64(0));
                    w.WriteNumber("ts", r.GetInt64(1));
                    w.WriteString("kind", r.GetString(2));
                    w.WriteString("severity", r.GetString(3));
                    w.WriteString("source", r.GetString(4));
                    if (!r.IsDBNull(5)) w.WriteNumber("sensor_id", r.GetInt32(5));
                    if (!r.IsDBNull(6)) { w.WritePropertyName("value"); Num(w, r.GetDouble(6)); }
                    w.WriteString("message", r.IsDBNull(7) ? "" : r.GetString(7));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        });

        // ---- /api/limits: bound limits with chart lines ----
        app.MapGet("/api/limits", (LimitEvaluator le) => Results.Json(le.Bound.Select(b =>
        {
            var (wh, ch, wl, cl) = b.Lines();
            return new
            {
                sensor_id = b.Sensor.Id, sensor = $"{b.Sensor.Group.Name} / {b.Sensor.Name}", role = b.Sensor.Role, unit = b.Sensor.Unit,
                label = b.Def.Label ?? b.Sensor.Name, kind = b.Def.Kind, warn = wh, crit = ch, warn_lo = wl, crit_lo = cl,
                hysteresis = b.Def.Hysteresis, min_duration_s = b.Def.MinDurationS, source = b.Def.Source, profile = b.ProfileName, level = b.Level,
                tier = b.Sensor.Tier,
            };
        }), Config.Json));

        // ---- /api/limit-summary: peak / time above warn / verdict for a range ----
        app.MapGet("/api/limit-summary", (HttpContext ctx, LimitEvaluator le) =>
        {
            var (from, to) = Range(ctx.Request);
            using var c = Read();
            var result = new List<object>();
            foreach (var b in le.Bound)
            {
                var d = b.Def; int id = b.Sensor.Id;
                double secPer = b.Sensor.Tier == 2 ? cfg.Tier2EveryS : 1;
                string cond = d.Kind switch { "low" => "value <=", "band" => "ABS(value - $n) / $n * 100 >=", _ => "value >=" };
                double? warnT = d.Kind == "band" ? d.WarnPct : d.Warn, critT = d.Kind == "band" ? d.CritPct : d.Crit;
                string peakExpr = d.Kind switch { "low" or "zero_while" => "MIN(value)", "band" => "(SELECT value FROM sample WHERE sensor_id=$id AND ts>=$f AND ts<$t ORDER BY ABS(value-$n) DESC LIMIT 1)", _ => "MAX(value)" };
                double? peak; long nWarn = 0, nCrit = 0, n;
                if (d.Kind == "zero_while")
                {
                    var w = le.Bound.FirstOrDefault(x => x == b)?.While;
                    nCrit = w == null ? 0 : Database.Scalar<long?>(c, """
                        SELECT COUNT(*) FROM sample a JOIN sample b ON b.sensor_id = $w AND b.ts = a.ts
                        WHERE a.sensor_id = $id AND a.ts >= $f AND a.ts < $t AND a.value <= $c AND b.value > $above
                        """, ("$w", w.Id), ("$id", id), ("$f", from), ("$t", to), ("$c", d.Crit ?? 0), ("$above", d.WhileAbove ?? 60)) ?? 0;
                    peak = Database.Scalar<double?>(c, "SELECT MIN(value) FROM sample WHERE sensor_id=$id AND ts>=$f AND ts<$t", ("$id", id), ("$f", from), ("$t", to));
                    n = Database.Scalar<long?>(c, "SELECT COUNT(*) FROM sample WHERE sensor_id=$id AND ts>=$f AND ts<$t", ("$id", id), ("$f", from), ("$t", to)) ?? 0;
                }
                else
                {
                    using var cmd = Cmd(c, $"""
                        SELECT {peakExpr}, SUM({cond} $w), SUM({cond} $c), COUNT(*) FROM sample WHERE sensor_id=$id AND ts>=$f AND ts<$t
                        """, ("$id", id), ("$f", from), ("$t", to), ("$w", warnT ?? double.MaxValue), ("$c", critT ?? double.MaxValue), ("$n", d.Nominal ?? 1));
                    if (d.Kind == "low") { cmd.Parameters["$w"].Value = warnT ?? double.MinValue; cmd.Parameters["$c"].Value = critT ?? double.MinValue; }
                    using var r = cmd.ExecuteReader();
                    r.Read();
                    peak = r.IsDBNull(0) ? null : r.GetDouble(0);
                    nWarn = r.IsDBNull(1) ? 0 : r.GetInt64(1); nCrit = r.IsDBNull(2) ? 0 : r.GetInt64(2); n = r.GetInt64(3);
                }
                double warnS = nWarn * secPer, critS = nCrit * secPer, minDur = Math.Max(d.MinDurationS, secPer);
                string verdict = n == 0 ? "no data" : critS >= minDur ? "Crit" : warnS >= minDur ? "Warn" : "OK";
                var (wh, ch, wl, cl) = b.Lines();
                result.Add(new
                {
                    sensor_id = id, label = d.Label ?? b.Sensor.Name, unit = b.Sensor.Unit, kind = d.Kind, peak, warn = wh ?? wl, crit = ch ?? cl,
                    warn_s = warnS, crit_s = critS, samples = n, verdict, source = d.Source, warn_pct = d.WarnPct, crit_pct = d.CritPct,
                });
            }
            return Results.Json(result, Config.Json);
        });

        // ---- /api/status ----
        app.MapGet("/api/status", (Stats st, Writer wr, LimitEvaluator le, SensorSampler sampler) =>
        {
            long size = Database.FileSize(cfg.DbPath), used;
            long oldest;
            using (var c = Read())
            {
                oldest = Database.Scalar<long?>(c, "SELECT MIN(m) FROM (SELECT (SELECT MIN(ts) FROM sample WHERE sensor_id = s.id) AS m FROM sensor s)") ?? 0;
                // logical data size (includes committed-but-not-checkpointed pages); the -wal file itself is reused space
                used = (Database.Scalar<long>(c, "PRAGMA page_count") - Database.Scalar<long>(c, "PRAGMA freelist_count")) * Database.Scalar<long>(c, "PRAGMA page_size");
            }
            double coveredH = oldest > 0 ? (Now - oldest) / 3600_000.0 : 0;
            // needs an hour of history before the extrapolation means anything
            double projMb = coveredH >= 1 ? used / 1048576.0 * cfg.RetentionHours / Math.Min(coveredH, cfg.RetentionHours) : double.NaN;
            var roles = sampler.Groups.SelectMany(g => g.Sensors).Where(s => s.Role != null).GroupBy(s => s.Role!)
                .ToDictionary(g => g.Key, g => g.Count());
            return Results.Json(new
            {
                version = typeof(Api).Assembly.GetName().Version?.ToString(),
                simulate = cfg.Simulate,
                driver = st.Driver,
                uptime_s = (int)(DateTime.UtcNow - st.Started).TotalSeconds,
                unclean_previous_shutdown = st.UncleanPreviousShutdown,
                cpu_pct_avg = Round(st.CpuPctAvg), cpu_pct_now = Round(st.CpuPctNow), cpu_budget_pct = 0.5,
                private_mb = Round(st.PrivateMb), working_set_mb = Round(st.WorkingSetMb), gc_heap_mb = Round(st.GcHeapMb), private_budget_mb = 80,
                tick_last_ms = Round(st.LastTickMs), tick_max_ms = Round(st.MaxTickMs), tick_avg_ms = Round(st.Ticks > 0 ? st.SumTickMs / st.Ticks : 0),
                proc_last_ms = Round(st.LastProcMs), proc_max_ms = Round(st.MaxProcMs), processes = st.ProcCount,
                commit_last_ms = Round(wr.LastCommitMs), commit_max_ms = Round(wr.MaxCommitMs), commits = wr.Commits, rows_written = wr.RowsWritten,
                sample_lag_ms = wr.LastSampleTsCommitted > 0 ? Now - wr.LastSampleTsCommitted : (long?)null,
                db_mb = Round(size / 1048576.0), db_data_mb = Round(used / 1048576.0), db_budget_mb = cfg.DbBudgetMb, db_covered_h = Round(coveredH), db_projected_mb = Round(projMb),
                retention_h = cfg.RetentionHours, last_retention = wr.LastRetention,
                sensors = st.SensorCount, tier1 = st.StoredTier1, tier2 = st.StoredTier2,
                roles, profiles = le.ActiveProfiles, db_reset = wr.ResetReason,
            }, Config.Json);
        });

        // ---- /api/export.csv: wide CSV of selected sensors ----
        app.MapGet("/api/export.csv", async (HttpContext ctx) =>
        {
            var (from, to) = Range(ctx.Request);
            var ids = ((string?)ctx.Request.Query["ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var i) ? i : -1).Where(i => i > 0).Distinct().ToArray();
            using var c = Read();
            var names = new Dictionary<int, string>();
            using (var cmd = Cmd(c, $"SELECT s.id, h.name || ' / ' || s.name || ' [' || s.unit || ']' FROM sensor s LEFT JOIN hardware h ON h.id=s.hardware_id WHERE s.id IN ({string.Join(',', ids.DefaultIfEmpty(-1))})"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) names[r.GetInt32(0)] = r.GetString(1);
            ids = ids.Where(names.ContainsKey).ToArray();
            var col = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            ctx.Response.ContentType = "text/csv; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=blackbox-{DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime:yyyyMMdd-HHmmss}.csv";
            var sb = new StringBuilder();
            sb.Append("time_local,ts_ms");
            foreach (var id in ids) sb.Append(",\"").Append(names[id].Replace("\"", "'")).Append('"');
            sb.Append('\n');
            using var q = Cmd(c, $"SELECT ts, sensor_id, value FROM sample WHERE sensor_id IN ({string.Join(',', ids.DefaultIfEmpty(-1))}) AND ts >= $f AND ts < $t ORDER BY ts",
                ("$f", from), ("$t", to));
            using var rr = q.ExecuteReader();
            var row = new double?[ids.Length];
            long cur = -1;
            void Emit()
            {
                sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(cur).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(cur);
                foreach (var v in row) sb.Append(',').Append(v?.ToString(CultureInfo.InvariantCulture));
                sb.Append('\n');
                Array.Clear(row);
            }
            while (rr.Read())
            {
                long ts = rr.GetInt64(0);
                if (cur >= 0 && ts - cur > 500) { Emit(); }   // samples of one tick share (almost) the same ts
                if (cur < 0 || ts - cur > 500) cur = ts;
                row[col[rr.GetInt32(1)]] = rr.GetDouble(2);
                if (sb.Length > 64 * 1024) { await ctx.Response.WriteAsync(sb.ToString()); sb.Clear(); }
            }
            if (cur >= 0) Emit();
            await ctx.Response.WriteAsync(sb.ToString());
        });
    }

    static double? Round(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : Math.Round(v, 2);
}
