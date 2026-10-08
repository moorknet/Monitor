using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;

namespace BlackBox.Update;

/// <summary>
/// "--apply-update" mode: runs from the staged NEW build, detached from the service.
/// stop service → back up install dir → copy new files → start → wait for /api/status to report the new version.
/// Any failure restores the backup and restarts the old version. Result goes to updates\last-result.json + update.log.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Updater
{
    public static int Run(string[] args)
    {
        string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"missing {name}"); }
        string source = Arg("--source"), target = Arg("--target"), data = Arg("--data"), from = Arg("--from-version"), to = Arg("--to-version");
        int port = int.Parse(Arg("--port"));
        string updates = Path.Combine(data, "updates"), backup = Path.Combine(updates, "backup");
        var logPath = Path.Combine(data, "logs", "update.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        void Log(string m) { try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}{Environment.NewLine}"); } catch { } }
        void Result(bool ok, string msg)
        {
            Log((ok ? "OK: " : "FAILED: ") + msg);
            File.WriteAllText(Path.Combine(updates, "last-result.json"), JsonSerializer.Serialize(new { ok, message = msg, from, to, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
        }

        Log($"Updating {from} -> {to}: {source} -> {target}");
        using var svc = new ServiceController("BlackBox");
        try
        {
            if (svc.Status != ServiceControllerStatus.Stopped)
            {
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
            Thread.Sleep(1500); // let the old process release file handles
            Log("Service stopped");

            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            CopyDir(target, backup);
            Log("Backup written");

            CopyDir(source, target);
            Log("Files replaced");

            svc.Start();
            svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            if (WaitForVersion(port, to, TimeSpan.FromSeconds(60)))
            {
                Result(true, $"Updated BlackBox {from} → {to}");
                try { Directory.Delete(source.EndsWith("publish") ? Path.GetDirectoryName(source)! : source, true); } catch { } // staging cleanup (best effort; we run from it)
                return 0;
            }
            throw new Exception("new version did not answer on /api/status within 60 s");
        }
        catch (Exception ex)
        {
            Log("Error: " + ex);
            try
            {
                svc.Refresh();
                if (svc.Status != ServiceControllerStatus.Stopped) { svc.Stop(); svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)); }
                Thread.Sleep(1500);
                if (Directory.Exists(backup)) CopyDir(backup, target);
                svc.Start();
                Result(false, $"Update to {to} failed and was rolled back to {from}: {ex.Message}");
            }
            catch (Exception ex2)
            {
                Result(false, $"Update to {to} failed and rollback ALSO failed ({ex2.Message}). Reinstall with install.ps1. Backup: {backup}");
            }
            return 1;
        }
    }

    static void CopyDir(string src, string dst)
    {
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dst, Path.GetRelativePath(src, file));
            for (int attempt = 1; ; attempt++)
            {
                try { File.Copy(file, to, true); break; }
                catch (IOException) when (attempt < 15) { Thread.Sleep(1000); }  // file still locked by the exiting process
            }
        }
    }

    static bool WaitForVersion(int port, string version, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var doc = JsonDocument.Parse(http.GetStringAsync($"http://127.0.0.1:{port}/api/status").Result);
                var v = doc.RootElement.GetProperty("version").GetString();
                if (v != null && Version.TryParse(v, out var got) && Version.TryParse(version, out var want)
                    && got.Major == want.Major && got.Minor == want.Minor && got.Build == want.Build) return true;
            }
            catch { }
            Thread.Sleep(1000);
        }
        return false;
    }
}
