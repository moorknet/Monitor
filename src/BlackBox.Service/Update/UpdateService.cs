using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using System.IO.Compression;

namespace BlackBox.Update;

/// <summary>
/// Checks GitHub Releases for a newer build, and on request downloads + verifies (SHA-256) + stages it, then hands
/// over to a detached updater process (the NEW BlackBox.exe with --apply-update) that stops this service, swaps the
/// files, restarts and rolls back if the new version does not come up.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    public sealed record Release(Version Version, string Tag, string Name, string Notes, string Url, DateTime Published,
                                 string ZipUrl, string? ShaUrl, long Size);

    readonly Config _cfg;
    readonly Db.Writer _writer;
    readonly ILogger _log;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };      // release download
    readonly HttpClient _api = new() { Timeout = TimeSpan.FromSeconds(20) };       // release check / checksum
    static readonly bool CanInstall = DetectService();
    readonly SemaphoreSlim _busy = new(1, 1);

    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
    public Release? Latest { get; private set; }
    public string Status { get; private set; } = "idle";   // idle | checking | downloading | verifying | staged | applying | error
    public string? Error { get; private set; }
    public double Progress { get; private set; }
    public DateTime? LastCheck { get; private set; }
    public object? LastResult { get; private set; }         // outcome of the previous install, read at startup
    public bool Available => Latest != null && Latest.Version > Current;
    string UpdatesDir => Path.Combine(_cfg.DataPath, "updates");

    public UpdateService(Config cfg, Db.Writer writer, ILogger<UpdateService> log)
    {
        _cfg = cfg; _writer = writer; _log = log;
        foreach (var h in new[] { _http, _api }) h.DefaultRequestHeaders.UserAgent.ParseAdd($"BlackBox-updater/{Current}");
        _api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        ReadLastResult();
    }

    static bool DetectService()
    {
        try { return OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService(); }
        catch { return false; }
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.Update.Enabled) return;
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        while (!ct.IsCancellationRequested)
        {
            await Check(ct);
            try { await Task.Delay(TimeSpan.FromHours(Math.Max(1, _cfg.Update.CheckHours)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task Check(CancellationToken ct = default)
    {
        if (!await _busy.WaitAsync(0, ct)) return;
        try
        {
            Status = "checking"; Error = null;
            using var res = await _api.GetAsync($"https://api.github.com/repos/{_cfg.Update.Repo}/releases/latest", ct);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) { Latest = null; Status = "idle"; LastCheck = DateTime.UtcNow; return; } // no releases yet
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) throw new InvalidDataException($"unrecognised release tag '{tag}'");
            string? zip = null, sha = null; long size = 0;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) { zip = a.GetProperty("browser_download_url").GetString(); size = a.GetProperty("size").GetInt64(); }
                else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase)) sha = a.GetProperty("browser_download_url").GetString();
            }
            if (zip == null) throw new InvalidDataException($"release {tag} has no *-win-x64.zip asset");
            Latest = new Release(Normalize(ver), tag, r.GetProperty("name").GetString() ?? tag, r.GetProperty("body").GetString() ?? "",
                r.GetProperty("html_url").GetString() ?? "", r.GetProperty("published_at").GetDateTime(), zip, sha, size);
            LastCheck = DateTime.UtcNow;
            Status = "idle";
            if (Available) _log.LogInformation("Update available: {v} (running {cur})", Latest.Version, Current);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = "error";
            Error = "Update check failed: " + (ex is TaskCanceledException ? "GitHub did not answer within 20 s (network/firewall/proxy for the service account?)" : ex.Message);
            LastCheck = DateTime.UtcNow;
            _log.LogWarning("Update check failed: {msg}", ex.Message);
        }
        finally { _busy.Release(); }
    }

    /// <summary>Download, verify, stage, launch the updater. Runs in the background; progress via Status/Progress.</summary>
    public string? StartApply()
    {
        if (!Available || Latest == null) return "No update available.";
        bool dryRun = !CanInstall;   // console/simulated runs only stage the update (nothing to swap safely)
        if (!_busy.Wait(0)) return "An update operation is already running.";
        var rel = Latest;
        _ = Task.Run(async () =>
        {
            try
            {
                Error = null;
                var dir = Directory.CreateDirectory(Path.Combine(UpdatesDir, rel.Tag)).FullName;
                var zipPath = Path.Combine(UpdatesDir, $"{rel.Tag}.zip");
                Status = "downloading"; Progress = 0;
                await Download(rel.ZipUrl, zipPath, rel.Size);

                Status = "verifying";
                if (rel.ShaUrl != null)
                {
                    var expected = (await _api.GetStringAsync(rel.ShaUrl)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
                    string actual;
                    await using (var fs = File.OpenRead(zipPath)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
                    if (actual != expected) throw new InvalidDataException($"checksum mismatch (expected {expected[..12]}…, got {actual[..12]}…)");
                }
                else _log.LogWarning("Release {tag} has no .sha256 asset; skipping checksum", rel.Tag);

                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                ZipFile.ExtractToDirectory(zipPath, dir);
                File.Delete(zipPath);
                var payload = Path.Combine(dir, "publish");
                if (!File.Exists(Path.Combine(payload, "BlackBox.exe"))) throw new InvalidDataException("package does not contain publish\\BlackBox.exe");

                if (dryRun)
                {
                    Status = "staged";
                    _log.LogInformation("Update {tag} staged in {dir} (dry run: not running as the installed service)", rel.Tag, payload);
                    return;
                }
                Status = "applying";
                _writer.Event("update", "info", $"Installing update {Current} → {rel.Version}");
                _writer.Flush();
                // the updater is the NEW exe, run from the staging folder, detached from this service
                var psi = new ProcessStartInfo(Path.Combine(payload, "BlackBox.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = payload,
                };
                foreach (var a in new[] { "--apply-update", "--source", payload, "--target", AppContext.BaseDirectory.TrimEnd('\\', '/'),
                                          "--data", _cfg.DataPath, "--port", _cfg.Port.ToString(), "--from-version", Current.ToString(), "--to-version", rel.Version.ToString() })
                    psi.ArgumentList.Add(a);
                Process.Start(psi);
                _log.LogInformation("Updater launched for {tag}; the service will now be restarted by it", rel.Tag);
            }
            catch (Exception ex)
            {
                Status = "error"; Error = "Update failed: " + ex.Message;
                _log.LogError(ex, "Update failed");
            }
            finally { _busy.Release(); }
        });
        return null;
    }

    async Task Download(string url, string path, long expected)
    {
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        long total = res.Content.Headers.ContentLength ?? expected, done = 0;
        await using var src = await res.Content.ReadAsStreamAsync();
        await using var dst = File.Create(path);
        var buf = new byte[81920];
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n));
            done += n;
            if (total > 0) Progress = Math.Round(done * 100.0 / total, 1);
        }
    }

    void ReadLastResult()
    {
        var f = Path.Combine(UpdatesDir, "last-result.json");
        if (!File.Exists(f)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var r = doc.RootElement;
            bool ok = r.GetProperty("ok").GetBoolean();
            string msg = r.GetProperty("message").GetString() ?? "";
            LastResult = new { ok, message = msg, from = r.GetProperty("from").GetString(), to = r.GetProperty("to").GetString(), ts = r.GetProperty("ts").GetInt64() };
            _writer.Event(ok ? "update_installed" : "update_failed", ok ? "info" : "warn", msg);
            File.Delete(f);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not read update result"); }
    }

    public object Json() => new
    {
        current = Current.ToString(),
        enabled = _cfg.Update.Enabled,
        repo = _cfg.Update.Repo,
        available = Available,
        latest = Latest == null ? null : new { version = Latest.Version.ToString(), tag = Latest.Tag, name = Latest.Name, notes = Latest.Notes, url = Latest.Url, published = Latest.Published, size_mb = Math.Round(Latest.Size / 1048576.0, 1) },
        status = Status, error = Error, progress = Progress, last_check = LastCheck, last_result = LastResult,
        can_install = CanInstall,
    };

    public override void Dispose() { _http.Dispose(); _api.Dispose(); base.Dispose(); }
}
