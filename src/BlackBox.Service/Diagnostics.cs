using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using BlackBox.Db;

namespace BlackBox;

/// <summary>Self-measurement exposed via /api/status (spec §8).</summary>
public sealed class Stats
{
    public readonly DateTime Started = DateTime.UtcNow;
    public long Ticks, SlowTicks;
    public double LastTickMs, MaxTickMs, SumTickMs;
    public double LastProcMs, MaxProcMs;
    public int SensorCount, StoredTier1, StoredTier2, ProcCount;
    public double CpuPctNow = double.NaN, CpuPctAvg = double.NaN;
    public double PrivateMb, WorkingSetMb, GcHeapMb;
    public string Driver = "";
    public bool UncleanPreviousShutdown;

    public void OnTick(double ms)
    {
        Ticks++; LastTickMs = ms; SumTickMs += ms;
        if (ms > MaxTickMs) MaxTickMs = ms;
    }
}

public sealed class SelfMonitor : BackgroundService
{
    readonly Stats _stats;
    readonly Writer _writer;
    readonly Config _cfg;
    readonly ILogger _log;
    public SelfMonitor(Stats stats, Writer writer, Config cfg, ILogger<SelfMonitor> log) { _stats = stats; _writer = writer; _cfg = cfg; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var p = Process.GetCurrentProcess();
        var start = p.TotalProcessorTime;
        var startWall = Stopwatch.GetTimestamp();
        var prev = start; var prevWall = startWall;
        int n = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        await Task.Delay(2000, ct); // first reading early so /api/status is populated right after start
        do
        {
            p.Refresh();
            var cpu = p.TotalProcessorTime; var wall = Stopwatch.GetTimestamp();
            double cores = Environment.ProcessorCount;
            _stats.CpuPctNow = (cpu - prev).TotalSeconds / Stopwatch.GetElapsedTime(prevWall, wall).TotalSeconds / cores * 100;
            _stats.CpuPctAvg = (cpu - start).TotalSeconds / Stopwatch.GetElapsedTime(startWall, wall).TotalSeconds / cores * 100;
            prev = cpu; prevWall = wall;
            _stats.PrivateMb = p.PrivateMemorySize64 / 1048576.0;
            _stats.WorkingSetMb = p.WorkingSet64 / 1048576.0;
            _stats.GcHeapMb = GC.GetTotalMemory(false) / 1048576.0;
            if (++n % 360 == 0) // hourly summary in the log
                _log.LogInformation("self: cpu avg {cpu:0.00}% private {mb:0} MB, db {db:0} MB", _stats.CpuPctAvg, _stats.PrivateMb, Database.FileSize(_cfg.DbPath) / 1048576.0);
            if (n % 360 == 0 && _stats.PrivateMb > 80)
                _writer.Event("budget", "warn", $"Private memory {_stats.PrivateMb:0} MB exceeds 80 MB budget");
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>Hourly retention + 2 s commit loop for the writer.</summary>
public sealed class WriterService : BackgroundService
{
    readonly Writer _w; readonly Config _cfg; readonly ILogger _log;
    public WriterService(Writer w, Config cfg, ILogger<WriterService> log) { _w = w; _cfg = cfg; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_cfg.CommitIntervalMs));
        var nextRetention = DateTime.UtcNow.AddMinutes(1);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { _w.Flush(); } catch (Exception ex) { _log.LogError(ex, "Commit failed"); }
                if (DateTime.UtcNow >= nextRetention)
                {
                    nextRetention = DateTime.UtcNow.AddHours(1);
                    try { _w.Retention(); } catch (Exception ex) { _log.LogError(ex, "Retention failed"); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Minimal rolling file logger: logs\blackbox.log, rolled to .1 at 5 MB (≤ 10 MB total).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    readonly string _path;
    readonly BlockingCollection<string> _q = new(1000);
    readonly Thread _thread;
    const long MaxBytes = 5 * 1024 * 1024;

    public FileLoggerProvider(string dir)
    {
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "blackbox.log");
        _thread = new Thread(Run) { IsBackground = true, Name = "log" };
        _thread.Start();
    }

    void Run()
    {
        foreach (var line in _q.GetConsumingEnumerable())
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(_path, _path + ".1", true);
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch { }
        }
    }

    public ILogger CreateLogger(string category) => new L(this, category);
    public void Dispose() { _q.CompleteAdding(); _thread.Join(2000); }

    sealed class L(FileLoggerProvider p, string cat) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel l) => l >= LogLevel.Information;
        public void Log<TState>(LogLevel l, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f)
        {
            if (!IsEnabled(l)) return;
            var c = cat.Length > 40 ? cat[(cat.LastIndexOf('.') + 1)..] : cat;
            p._q.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {l.ToString()[..4].ToUpperInvariant()} {c}: {f(state, ex)}{(ex != null ? Environment.NewLine + ex : "")}{Environment.NewLine}");
        }
    }
}
