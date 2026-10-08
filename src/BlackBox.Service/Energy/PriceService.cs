using System.Globalization;
using System.Net;
using System.Text.Json;
using BlackBox.Db;

namespace BlackBox.Energy;

/// <summary>
/// Keeps the <c>price</c> table filled. Checks every 30 min: today, tomorrow (published ~13:00 CET),
/// and backfills up to 30 past days that have recorded energy but no prices. One small HTTP GET per missing day.
/// Provider "elprisetjustnu": https://www.elprisetjustnu.se/api/v1/prices/{yyyy}/{MM-dd}_{area}.json (spot, excl. VAT).
/// </summary>
public sealed class PriceService : BackgroundService
{
    readonly ElectricityConfig _cfg;
    readonly Writer _writer;
    readonly ILogger _log;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public static readonly TimeZoneInfo MarketTz = FindTz();

    public DateTime? LastFetchUtc { get; private set; }
    public string? LastError { get; private set; }

    public PriceService(Config cfg, Writer writer, ILogger<PriceService> log)
    {
        _cfg = cfg.Electricity; _writer = writer; _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BlackBox/1.0 (hardware recorder; hourly price lookup)");
    }

    static TimeZoneInfo FindTz()
    {
        foreach (var id in new[] { "Europe/Stockholm", "W. Europe Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Local;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.Enabled || _cfg.Provider == "fixed") return;
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try { await Update(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { LastError = ex.Message; _log.LogWarning(ex, "Price update failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    async Task Update(CancellationToken ct)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, MarketTz);
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var days = new List<DateOnly> { today };
        if (nowLocal.Hour >= 13 || _cfg.Provider == "sim") days.Add(today.AddDays(1));
        // past days with energy but no prices (first install, outage, service was off)
        var have = new HashSet<DateOnly>();
        var needed = new HashSet<DateOnly>();
        long since = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        _writer.WithConnection(c =>
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT ts FROM price WHERE ts >= $s"; cmd.Parameters.AddWithValue("$s", since);
                using var r = cmd.ExecuteReader();
                while (r.Read()) have.Add(LocalDay(r.GetInt64(0)));
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT ts FROM energy_quarter WHERE ts >= $s"; cmd.Parameters.AddWithValue("$s", since);
                using var r = cmd.ExecuteReader();
                while (r.Read()) needed.Add(LocalDay(r.GetInt64(0)));
            }
            return 0;
        });
        days.AddRange(needed.Where(d => d < today).OrderDescending());
        int fetched = 0;
        foreach (var day in days.Distinct())
        {
            if (have.Contains(day) || (fetched >= 10 && _cfg.Provider != "sim")) continue;
            var rows = _cfg.Provider == "sim" ? SimDay(day) : await FetchDay(day, ct);
            if (rows == null) continue;
            Store(rows);
            fetched++;
        }
        if (fetched > 0) { LastFetchUtc = DateTime.UtcNow; LastError = null; _log.LogInformation("Stored prices for {n} day(s)", fetched); }
    }

    static DateOnly LocalDay(long ms) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), MarketTz).DateTime);

    async Task<List<(long ts, long end, double spot)>?> FetchDay(DateOnly day, CancellationToken ct)
    {
        var url = $"{_cfg.BaseUrl.TrimEnd('/')}/{day:yyyy}/{day:MM-dd}_{_cfg.Area}.json";
        using var res = await _http.GetAsync(url, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;   // tomorrow not published yet / too old
        res.EnsureSuccessStatusCode();
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        string field = $"{_cfg.Currency.ToUpperInvariant()}_per_kWh";
        var rows = new List<(long, long, double)>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty(field, out var p)) throw new InvalidDataException($"price feed has no {field}");
            long ts = DateTimeOffset.Parse(e.GetProperty("time_start").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
            long end = DateTimeOffset.Parse(e.GetProperty("time_end").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
            rows.Add((ts, end, p.GetDouble()));
        }
        return rows;
    }

    /// <summary>Deterministic synthetic Nordic-looking day: cheap nights, morning and evening peaks.</summary>
    public static List<(long ts, long end, double spot)> SimDay(DateOnly day)
    {
        var rows = new List<(long, long, double)>();
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), MarketTz.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)));
        var rnd = new Random(day.DayNumber);
        double level = 0.25 + rnd.NextDouble() * 0.9;            // windy vs calm day
        for (int q = 0; q < 96; q++)
        {
            double h = q / 4.0;
            double shape = 0.35 + 0.55 * Math.Exp(-Math.Pow((h - 8) / 1.6, 2)) + 0.9 * Math.Exp(-Math.Pow((h - 18.5) / 2.0, 2));
            double spot = Math.Max(0.01, level * shape + (rnd.NextDouble() - 0.5) * 0.05);
            long ts = start.AddMinutes(q * 15).ToUnixTimeMilliseconds();
            rows.Add((ts, ts + 900_000, Math.Round(spot, 4)));
        }
        return rows;
    }

    void Store(List<(long ts, long end, double spot)> rows) => _writer.WithConnection(c =>
    {
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO price(ts, ts_end, spot, area) VALUES ($t,$e,$p,$a)";
        var pt = cmd.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pe = cmd.Parameters.Add("$e", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pp = cmd.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Real);
        cmd.Parameters.AddWithValue("$a", _cfg.Area);
        foreach (var (ts, end, spot) in rows) { pt.Value = ts; pe.Value = end; pp.Value = spot; cmd.ExecuteNonQuery(); }
        tx.Commit();
        return 0;
    });

    public override void Dispose() { _http.Dispose(); base.Dispose(); }
}
