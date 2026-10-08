using BlackBox.Db;
using BlackBox.Energy;
using Microsoft.Data.Sqlite;

namespace BlackBox.Web;

/// <summary>
/// "Power &amp; cost" tab. Energy (15-min buckets) × price (spot + surcharge, + VAT) computed at query time,
/// so changing surcharge/VAT/base load in config.json re-prices history after a restart.
/// </summary>
public static class CostApi
{
    readonly record struct Quarter(long Ts, double CpuWh, double GpuWh, double WallWh, double Seconds);

    /// <summary>Sorted price intervals with binary-search lookup.</summary>
    sealed class Prices
    {
        public readonly List<(long ts, long end, double spot)> Rows = new();
        public double? SpotAt(long t)
        {
            int lo = 0, hi = Rows.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var r = Rows[mid];
                if (t < r.ts) hi = mid - 1;
                else if (t >= r.end) lo = mid + 1;
                else return r.spot;
            }
            return null;
        }
    }

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static long LocalMs(DateTime local) => new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    static DateTime ToLocal(long ms) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), TimeZoneInfo.Local).DateTime;

    static List<Quarter> LoadQuarters(SqliteConnection c, long from, long to, ElectricityConfig e)
    {
        var list = new List<Quarter>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, cpu_wh, gpu_wh, wall_wh, seconds FROM energy_quarter WHERE ts >= $f AND ts < $t ORDER BY ts";
        cmd.Parameters.AddWithValue("$f", from); cmd.Parameters.AddWithValue("$t", to);
        using var r = cmd.ExecuteReader();
        double eff = Math.Clamp(e.PsuEfficiency, 0.5, 1.0);
        while (r.Read())
        {
            // wall energy is derived here (not the stored wall_wh) so base-load / PSU-efficiency edits re-price history
            double cpu = D(r, 1), gpu = D(r, 2), sec = D(r, 4);
            list.Add(new Quarter(r.GetInt64(0), cpu, gpu, (cpu + gpu + e.BaseLoadW * sec / 3600) / eff, sec));
        }
        return list;
    }

    static Prices LoadPrices(SqliteConnection c, long from, long to)
    {
        var p = new Prices();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, ts_end, spot FROM price WHERE ts_end > $f AND ts < $t ORDER BY ts";
        cmd.Parameters.AddWithValue("$f", from); cmd.Parameters.AddWithValue("$t", to);
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (!r.IsDBNull(2)) p.Rows.Add((r.GetInt64(0), r.GetInt64(1), r.GetDouble(2)));
        return p;
    }

    static double? Total(ElectricityConfig e, Prices p, long t) =>
        e.Provider == "fixed" ? e.FixedPricePerKwh : p.SpotAt(t) is double s ? e.Total(s) : null;

    sealed class Agg
    {
        public double Kwh, CpuKwh, GpuKwh, Cost, UnpricedKwh, Hours;
        public void Add(Quarter q, double? price)
        {
            double kwh = q.WallWh / 1000;
            Kwh += kwh; CpuKwh += q.CpuWh / 1000; GpuKwh += q.GpuWh / 1000; Hours += q.Seconds / 3600;
            if (price is double pr) Cost += kwh * pr; else UnpricedKwh += kwh;
        }
        public object Json() => new
        {
            kwh = R(Kwh, 3), cpu_kwh = R(CpuKwh, 3), gpu_kwh = R(GpuKwh, 3), cost = R(Cost, 2), unpriced_kwh = R(UnpricedKwh, 3),
            hours = R(Hours, 2), avg_w = Hours > 0 ? R(Kwh * 1000 / Hours, 0) : (double?)null,
            avg_price = Kwh - UnpricedKwh > 0 ? R(Cost / (Kwh - UnpricedKwh), 3) : (double?)null,
        };
    }

    static double R(double v, int d) => double.IsFinite(v) ? Math.Round(v, d) : 0;
    static double D(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);

    public static void Map(WebApplication app, Config cfg)
    {
        var e = cfg.Electricity;
        SqliteConnection Read() => Database.Open(cfg.DbPath, readOnly: true);
        object Settings() => new
        {
            enabled = e.Enabled, provider = e.Provider, area = e.Area, currency = e.Currency, surcharge_per_kwh = e.SurchargePerKwh,
            vat_pct = e.VatPct, base_load_w = e.BaseLoadW, psu_efficiency = e.PsuEfficiency,
            fixed_price_per_kwh = e.Provider == "fixed" ? e.FixedPricePerKwh : (double?)null,
        };

        // ---- summary: tiles, periods, records, cheapest window, fun-fact inputs ----
        app.MapGet("/api/cost/summary", (EnergyMeter meter, PriceService prices) =>
        {
            long now = Now;
            var localNow = ToLocal(now);
            long today = LocalMs(localNow.Date), yesterday = LocalMs(localNow.Date.AddDays(-1));
            long month = LocalMs(new DateTime(localNow.Year, localNow.Month, 1));
            long week = now - 7 * 86_400_000L, d30 = now - 30 * 86_400_000L, all = now - cfg.LongRetentionMs;
            using var c = Read();
            var qs = LoadQuarters(c, all, now + 1, e);
            var px = LoadPrices(c, all, now + 2 * 86_400_000L);

            var periods = new (string key, long from, long to)[]
                { ("today", today, now + 1), ("yesterday", yesterday, today), ("week", week, now + 1), ("month", month, now + 1), ("days30", d30, now + 1), ("all", all, now + 1) };
            var aggs = periods.ToDictionary(p => p.key, _ => new Agg());
            var hourCost = new Dictionary<long, (double kwh, double cost)>();
            var dayKwh = new Dictionary<DateTime, double>();
            long first = qs.Count > 0 ? qs[0].Ts : 0;
            foreach (var q in qs)
            {
                var price = Total(e, px, q.Ts + EnergyMeter.QuarterMs / 2);
                foreach (var (key, from, to) in periods) if (q.Ts >= from && q.Ts < to) aggs[key].Add(q, price);
                long h = q.Ts / 3_600_000 * 3_600_000;
                var hc = hourCost.GetValueOrDefault(h);
                hourCost[h] = (hc.kwh + q.WallWh / 1000, hc.cost + q.WallWh / 1000 * (price ?? 0));
                var d = ToLocal(q.Ts).Date;
                dayKwh[d] = dayKwh.GetValueOrDefault(d) + q.WallWh / 1000;
            }

            // today's prices + cheapest upcoming 3 h window
            var todayRows = px.Rows.Where(r => r.ts >= today && r.ts < LocalMs(localNow.Date.AddDays(1))).Select(r => e.Total(r.spot)).ToList();
            double? priceNow = Total(e, px, now);
            object? cheapest = null;
            if (e.Provider != "fixed")
            {
                var fut = px.Rows.Where(r => r.end > now).ToList();
                double best = double.MaxValue; long bestStart = 0;
                for (int i = 0; i < fut.Count; i++)
                {
                    long start = Math.Max(fut[i].ts, now), endWin = start + 3 * 3_600_000L;
                    double sum = 0, dur = 0;
                    for (int j = i; j < fut.Count && fut[j].ts < endWin; j++)
                    {
                        if (j > i && fut[j].ts != fut[j - 1].end) { dur = 0; break; } // hole in price data
                        double seg = Math.Min(fut[j].end, endWin) - Math.Max(fut[j].ts, start);
                        sum += e.Total(fut[j].spot) * seg; dur += seg;
                    }
                    if (dur >= 3 * 3_600_000L - 1 && sum / dur < best) { best = sum / dur; bestStart = start; }
                }
                if (bestStart > 0) cheapest = new { from = bestStart, to = bestStart + 3 * 3_600_000L, avg_price = R(best, 3) };
            }
            var topHour = hourCost.OrderByDescending(kv => kv.Value.cost).FirstOrDefault();
            var topDay = dayKwh.OrderByDescending(kv => kv.Value).FirstOrDefault();
            double? minToday = todayRows.Count > 0 ? todayRows.Min() : null;
            var t = aggs["today"];

            // top processes this month (attributed CPU+GPU energy / PSU efficiency, priced per hour)
            var procs = ProcessCosts(c, e, px, month, now + 1, 5);

            return Results.Json(new
            {
                settings = Settings(),
                now = new
                {
                    wall_w = double.IsNaN(meter.LastWallW) ? (double?)null : R(meter.LastWallW, 0),
                    cpu_w = double.IsNaN(meter.LastCpuW) ? (double?)null : R(meter.LastCpuW, 0),
                    gpu_w = double.IsNaN(meter.LastGpuW) ? (double?)null : R(meter.LastGpuW, 0),
                    price = priceNow is double p ? R(p, 3) : (double?)null,
                    cost_per_hour = priceNow is double p2 && !double.IsNaN(meter.LastWallW) ? R(meter.LastWallW / 1000 * p2, 3) : (double?)null,
                    price_rank = priceNow is double p3 && todayRows.Count > 0 ? R(todayRows.Count(x => x < p3) / (double)todayRows.Count, 2) : (double?)null,
                },
                periods = aggs.ToDictionary(kv => kv.Key, kv => kv.Value.Json()),
                today_prices = todayRows.Count > 0 ? new { min = R(todayRows.Min(), 3), max = R(todayRows.Max(), 3), avg = R(todayRows.Average(), 3) } : null,
                cheapest_3h = cheapest,
                savings_today = minToday is double mt && t.Kwh - t.UnpricedKwh > 0 ? R(t.Cost - (t.Kwh - t.UnpricedKwh) * mt, 2) : (double?)null,
                record_hour = topHour.Key > 0 ? new { ts = topHour.Key, cost = R(topHour.Value.cost, 2), kwh = R(topHour.Value.kwh, 3) } : null,
                record_day = topDay.Value > 0 ? new { day = topDay.Key.ToString("yyyy-MM-dd"), kwh = R(topDay.Value, 3) } : null,
                top_processes_month = procs,
                tracking_since = first,
                price_status = new { last_fetch = prices.LastFetchUtc, error = prices.LastError, rows = px.Rows.Count },
            }, Config.Json);
        });

        // ---- per-hour energy + cost ----
        app.MapGet("/api/cost/hourly", (HttpContext ctx) =>
        {
            long to = long.TryParse(ctx.Request.Query["to"], out var tt) ? tt : Now;
            long from = long.TryParse(ctx.Request.Query["from"], out var ff) ? ff : to - 48 * 3_600_000L;
            from = from / 3_600_000 * 3_600_000;
            using var c = Read();
            var qs = LoadQuarters(c, from, to, e);
            var px = LoadPrices(c, from, to);
            var hours = new SortedDictionary<long, Agg>();
            foreach (var q in qs)
            {
                long h = q.Ts / 3_600_000 * 3_600_000;
                if (!hours.TryGetValue(h, out var a)) hours[h] = a = new Agg();
                a.Add(q, Total(e, px, q.Ts + EnergyMeter.QuarterMs / 2));
            }
            return Results.Json(hours.Select(kv => new { ts = kv.Key, kwh = R(kv.Value.Kwh, 4), cost = R(kv.Value.Cost, 3), avg_w = kv.Value.Hours > 0 ? R(kv.Value.Kwh * 1000 / kv.Value.Hours, 0) : 0, unpriced = kv.Value.UnpricedKwh > 0 }), Config.Json);
        });

        // ---- per-day energy + cost (local days) ----
        app.MapGet("/api/cost/daily", (HttpContext ctx) =>
        {
            int days = Math.Clamp(int.TryParse(ctx.Request.Query["days"], out var d) ? d : 30, 1, 800);
            var localNow = ToLocal(Now);
            long from = LocalMs(localNow.Date.AddDays(-(days - 1)));
            using var c = Read();
            var qs = LoadQuarters(c, from, Now + 1, e);
            var px = LoadPrices(c, from, Now + 1);
            var map = new SortedDictionary<DateTime, Agg>();
            for (int i = 0; i < days; i++) map[localNow.Date.AddDays(-(days - 1) + i)] = new Agg();
            foreach (var q in qs)
                if (map.TryGetValue(ToLocal(q.Ts).Date, out var a)) a.Add(q, Total(e, px, q.Ts + EnergyMeter.QuarterMs / 2));
            return Results.Json(map.Select(kv => new { day = kv.Key.ToString("yyyy-MM-dd"), ts = LocalMs(kv.Key), kwh = R(kv.Value.Kwh, 3), cost = R(kv.Value.Cost, 2), hours = R(kv.Value.Hours, 2) }), Config.Json);
        });

        // ---- price curve (total per kWh incl. surcharge + VAT) ----
        app.MapGet("/api/cost/prices", (HttpContext ctx) =>
        {
            var localNow = ToLocal(Now);
            long from = long.TryParse(ctx.Request.Query["from"], out var ff) ? ff : LocalMs(localNow.Date);
            long to = long.TryParse(ctx.Request.Query["to"], out var tt) ? tt : LocalMs(localNow.Date.AddDays(2));
            using var c = Read();
            var px = LoadPrices(c, from, to);
            return Results.Json(px.Rows.Select(r => new { ts = r.ts, end = r.end, spot = r.spot, total = R(e.Total(r.spot), 4) }), Config.Json);
        });

        // ---- which processes cost the most ----
        app.MapGet("/api/cost/processes", (HttpContext ctx) =>
        {
            long to = long.TryParse(ctx.Request.Query["to"], out var tt) ? tt : Now + 1;
            long from = long.TryParse(ctx.Request.Query["from"], out var ff) ? ff : LocalMs(new DateTime(ToLocal(Now).Year, ToLocal(Now).Month, 1));
            using var c = Read();
            var px = LoadPrices(c, from, to);
            return Results.Json(ProcessCosts(c, e, px, from, to, 25), Config.Json);
        });
    }

    static List<object> ProcessCosts(SqliteConnection c, ElectricityConfig e, Prices px, long from, long to, int limit)
    {
        var acc = new Dictionary<string, (double kwh, double cost, double hours)>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT p.name, h.ts, h.wh FROM proc_energy_hour h JOIN procname p ON p.id = h.procname_id
            WHERE h.ts >= $f AND h.ts < $t AND p.name <> '(other)'
            """;
        cmd.Parameters.AddWithValue("$f", from / 3_600_000 * 3_600_000); cmd.Parameters.AddWithValue("$t", to);
        using var r = cmd.ExecuteReader();
        double eff = Math.Clamp(e.PsuEfficiency, 0.5, 1.0);
        while (r.Read())
        {
            string name = r.GetString(0);
            long ts = r.GetInt64(1);
            double kwh = D(r, 2) / 1000 / eff;
            // hour's average price (mean of its quarter prices)
            double sum = 0; int n = 0;
            for (long q = ts; q < ts + 3_600_000; q += EnergyMeter.QuarterMs)
                if (Total(e, px, q + 1) is double pr) { sum += pr; n++; }
            var a = acc.GetValueOrDefault(name);
            acc[name] = (a.kwh + kwh, a.cost + (n > 0 ? kwh * sum / n : 0), a.hours + (kwh > 0.001 ? 1 : 0));
        }
        return acc.OrderByDescending(kv => kv.Value.cost).ThenByDescending(kv => kv.Value.kwh).Take(limit)
            .Select(kv => (object)new { name = kv.Key, kwh = R(kv.Value.kwh, 3), cost = R(kv.Value.cost, 2), active_hours = kv.Value.hours }).ToList();
    }
}
