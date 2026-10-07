using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using BlackBox.Db;

namespace BlackBox.Events;

/// <summary>
/// Push subscription to the System log for crash/shutdown-relevant events (spec §4.3). On start, backfills the
/// retention window (from the newest event already stored) and continues live from the backfill's bookmark,
/// so nothing is missed or duplicated between the two.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventWatcher : BackgroundService
{
    // One <Select> per provider keeps every XPath well under the event log's per-expression limit.
    static readonly string[] Selects =
    [
        "Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41 or EventID=125)",
        "Provider[@Name='EventLog'] and EventID=6008",
        "Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=1 or EventID=17 or EventID=18 or EventID=19 or EventID=46 or EventID=47)",
        "Provider[@Name='Display'] and EventID=4101",
        "Provider[@Name='amdkmdag'] and (Level=1 or Level=2 or Level=3)",
        "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001",
        "Provider[@Name='BugCheck'] and EventID=1001",
        "Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and (EventID=37 or EventID=26)",
        "Provider[@Name='User32'] and EventID=1074",
    ];

    static string QueryXml(string? sinceIso)
    {
        var time = sinceIso == null ? "" : $" and TimeCreated[@SystemTime&gt;='{sinceIso}']";
        var sel = string.Concat(Selects.Select(x =>
            $"<Select Path=\"System\">*[System[{x.Replace("'", "&apos;")}{time}]]</Select>"));
        return $"<QueryList><Query Id=\"0\" Path=\"System\">{sel}</Query></QueryList>";
    }

    readonly Config _cfg;
    readonly Writer _writer;
    readonly ILogger _log;
    EventLogWatcher? _watcher;

    public EventWatcher(Config cfg, Writer writer, ILogger<EventWatcher> log) { _cfg = cfg; _writer = writer; _log = log; }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        // run off the startup path: a 72 h backfill can take a moment
        return Task.Run(() =>
        {
            EventBookmark? bookmark = null;
            try { bookmark = Backfill(); }
            catch (Exception ex) { _log.LogError(ex, "Event log backfill failed"); }
            try
            {
                var q = new EventLogQuery("System", PathType.LogName, QueryXml(null));
                _watcher = bookmark != null ? new EventLogWatcher(q, bookmark, true) : new EventLogWatcher(q);
                _watcher.EventRecordWritten += (_, e) =>
                {
                    if (e.EventRecord != null) using (e.EventRecord) Store(e.EventRecord);
                    else if (e.EventException != null) _log.LogWarning(e.EventException, "Event watcher error");
                };
                _watcher.Enabled = true;
                _log.LogInformation("Event log subscription active");
            }
            catch (Exception ex) { _log.LogError(ex, "Event log subscription failed"); }
        }, ct);
    }

    EventBookmark? Backfill()
    {
        long since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _cfg.RetentionMs;
        long newest = _writer.WithConnection(c => Database.Scalar<long?>(c, "SELECT MAX(ts) FROM event WHERE source LIKE 'winlog:%'") ?? 0);
        if (newest >= since) since = newest + 1;
        var iso = DateTimeOffset.FromUnixTimeMilliseconds(since).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var q = new EventLogQuery("System", PathType.LogName, QueryXml(iso));
        EventBookmark? last = null;
        int n = 0;
        using var reader = new EventLogReader(q);
        for (var r = reader.ReadEvent(); r != null; r = reader.ReadEvent())
            using (r) { Store(r); last = r.Bookmark; n++; }
        _log.LogInformation("Backfilled {n} Windows events since {since}", n, iso);
        return last;
    }

    void Store(EventRecord r)
    {
        string provider = r.ProviderName ?? "?";
        int id = r.Id;
        (string kind, string sev) = (provider, id) switch
        {
            ("Microsoft-Windows-Kernel-Power", 41) => ("kernel_power_41", "crit"),
            ("Microsoft-Windows-Kernel-Power", _) => ("thermal", "warn"),
            ("EventLog", 6008) => ("unexpected_shutdown", "crit"),
            ("Microsoft-Windows-WHEA-Logger", _) => ("whea", r.Level is 1 or 2 ? "crit" : "warn"),
            ("Display", _) or ("amdkmdag", _) => ("gpu_tdr", "warn"),
            (_, 1001) => ("bugcheck", "crit"),
            ("Microsoft-Windows-Kernel-Processor-Power", _) => ("cpu_firmware_limit", "warn"),
            ("User32", _) => ("shutdown_initiated", "info"),
            _ => ("winlog", "info"),
        };
        string msg;
        try { msg = r.FormatDescription() ?? ""; } catch { msg = ""; }
        if (string.IsNullOrWhiteSpace(msg))
            msg = string.Join(", ", r.Properties.Select(p => p.Value?.ToString()));
        if (kind == "kernel_power_41" && r.Properties.Count > 0)
            msg = $"BugcheckCode={r.Properties[0].Value}. " + msg;
        if (msg.Length > 1500) msg = msg[..1500];
        long ts = r.TimeCreated is DateTime t ? new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _writer.AddEvent(new EventRow(ts, kind, sev, $"winlog:{provider}", null, id, $"[{provider} {id} #{r.RecordId}] {msg.Trim()}"));
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
