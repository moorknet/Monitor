using System.Net;
using BlackBox;
using BlackBox.Db;
using BlackBox.Energy;
using BlackBox.Events;
using BlackBox.Processes;
using BlackBox.Sensors;
using BlackBox.Web;

// BlackBox: always-on hardware flight recorder. Runs as a Windows service (LocalSystem) or as a console app.
//   BlackBox.exe                 console / service (auto-detected)
//   BlackBox.exe --simulate      synthetic sensors + processes (no driver needed; default on non-Windows)
//   BlackBox.exe --data <dir>    data directory (default C:\ProgramData\BlackBox)
if (args.Contains("--apply-update") && OperatingSystem.IsWindows()) return BlackBox.Update.Updater.Run(args);

var cfg = Config.Load(args);
int genIdx = Array.IndexOf(args, "--generate-history");
if (genIdx >= 0)
{
    using var lf = LoggerFactory.Create(b => b.AddSimpleConsole());
    int edIdx = Array.IndexOf(args, "--energy-days");
    HistoryGenerator.Run(cfg, double.Parse(args[genIdx + 1], System.Globalization.CultureInfo.InvariantCulture),
        edIdx >= 0 ? double.Parse(args[edIdx + 1], System.Globalization.CultureInfo.InvariantCulture) : 0, lf);
    return 0;
}

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "web"),
});
builder.Host.UseWindowsService(o => o.ServiceName = "BlackBox");
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(cfg.LogDir));
if (Environment.UserInteractive) builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o =>
{
    o.Listen(IPAddress.Loopback, cfg.Port);   // localhost only, never exposed
    o.AddServerHeader = false;
});

var s = builder.Services;
s.AddSingleton(cfg);
s.AddSingleton<Stats>();
s.AddSingleton<EnergyMeter>();
s.AddSingleton<Writer>();
s.AddSingleton<PriceService>();
s.AddHostedService(sp => sp.GetRequiredService<PriceService>());
s.AddSingleton<LimitEvaluator>();
if (cfg.Simulate || !OperatingSystem.IsWindows())
{
    s.AddSingleton<ISensorSource, SimSensorSource>();
    s.AddSingleton<IProcessSource, SimProcessSource>();
}
else
{
    s.AddSingleton<ISensorSource>(sp => new LhmSensorSource(sp.GetRequiredService<ILogger<LhmSensorSource>>()));
    s.AddSingleton<IProcessSource>(_ => new NtProcessSource());
    s.AddHostedService<EventWatcher>();
}
s.AddSingleton<SensorSampler>();
s.AddHostedService(sp => sp.GetRequiredService<SensorSampler>());
s.AddHostedService<ProcessSampler>();
s.AddHostedService<WriterService>();
s.AddHostedService<SelfMonitor>();
s.AddSingleton<BlackBox.Update.UpdateService>();
s.AddHostedService(sp => sp.GetRequiredService<BlackBox.Update.UpdateService>());

var app = builder.Build();
var log = app.Services.GetRequiredService<ILogger<Program>>();
var writer = app.Services.GetRequiredService<Writer>();
var stats = app.Services.GetRequiredService<Stats>();

// ---- startup: integrity, power-loss gap detection, sensor enumeration (target < 3 s) ----
var sw = System.Diagnostics.Stopwatch.StartNew();
string runFlag = Path.Combine(cfg.DataPath, "running.flag");
bool unclean = File.Exists(runFlag);           // flag survives only if we did not stop cleanly
stats.UncleanPreviousShutdown = unclean;
writer.Open(uncleanShutdown: unclean);          // quick_check only after an unclean stop (keeps clean starts fast)
File.WriteAllText(runFlag, Environment.ProcessId.ToString());
long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
if (writer.ResetReason != null) writer.Event("db_reset", "crit", "Database was corrupt and has been moved aside: " + writer.ResetReason);
long lastTs = writer.WithConnection(Database.LastSampleTs);
if (unclean && lastTs > 0)
{
    double gap = (now - lastTs) / 1000.0;
    writer.Event("power_loss", "crit",
        $"Recording stopped without a clean shutdown at {DateTimeOffset.FromUnixTimeMilliseconds(lastTs).LocalDateTime:yyyy-MM-dd HH:mm:ss} " +
        $"(power loss, hard reset or crash); resumed after {gap:0} s", value: gap, ts: lastTs);
}
var sampler = app.Services.GetRequiredService<SensorSampler>();
var source = app.Services.GetRequiredService<ISensorSource>();
stats.Driver = source.DriverInfo;
sampler.Initialize();
writer.Event("service_start", "info", $"BlackBox started ({(cfg.Simulate ? "simulated" : source.DriverInfo)}), startup {sw.ElapsedMilliseconds} ms", value: sw.ElapsedMilliseconds);
writer.Flush();
log.LogInformation("Startup complete in {ms} ms; data {dir}; http://127.0.0.1:{port}", sw.ElapsedMilliseconds, cfg.DataPath, cfg.Port);

// Localhost-only hardening: reject foreign Host headers (DNS rebinding) and cross-site POSTs (CSRF).
// A browser cannot add the X-BlackBox header to a cross-origin request without a CORS preflight, which we never allow.
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host;
    if (host is not ("127.0.0.1" or "localhost" or "[::1]")) { ctx.Response.StatusCode = 421; return; }
    if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Headers["X-BlackBox"] != "1") { ctx.Response.StatusCode = 403; return; }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
Api.Map(app, cfg);
CostApi.Map(app, cfg);
app.MapGet("/api/update", (BlackBox.Update.UpdateService u) => Results.Json(u.Json(), Config.Json));
app.MapPost("/api/update/check", async (BlackBox.Update.UpdateService u) => { await u.Check(); return Results.Json(u.Json(), Config.Json); });
app.MapPost("/api/update/apply", (BlackBox.Update.UpdateService u) =>
    u.StartApply() is string err ? Results.Json(new { error = err }, Config.Json, statusCode: 409) : Results.Json(u.Json(), Config.Json));

app.Lifetime.ApplicationStopping.Register(() => writer.Event("service_stop", "info", "BlackBox stopped cleanly"));
app.Lifetime.ApplicationStopped.Register(() =>
{
    writer.Dispose();                           // final flush + checkpoint
    try { File.Delete(runFlag); } catch { }
});

app.Run();
return 0;
