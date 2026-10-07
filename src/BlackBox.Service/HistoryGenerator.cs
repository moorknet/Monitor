using System.Diagnostics;
using BlackBox.Db;
using BlackBox.Sensors;

namespace BlackBox;

/// <summary>
/// Dev tool: <c>BlackBox --simulate --data DIR --generate-history 72</c> writes N hours of synthetic history through the
/// real Writer (same tiering, row shapes and commit batching) to measure DB size, startup time and query latency.
/// </summary>
public static class HistoryGenerator
{
    public static void Run(Config cfg, double hours, ILoggerFactory lf)
    {
        var sw = Stopwatch.StartNew();
        var w = new Writer(cfg, lf.CreateLogger<Writer>());
        w.Open(false);
        var src = new SimSensorSource();
        var groups = src.Enumerate();
        SensorClassifier.Resolve(groups, cfg.TierOverrides);
        w.Register(groups);
        var sensors = groups.SelectMany(g => g.Sensors).ToList();
        var cpu = groups.First(g => g.Kind == "Cpu");
        var names = new[] { "Diablo IV.exe", "Battle.net.exe", "explorer.exe", "msedge.exe", "Discord.exe", "dwm.exe", "svchost.exe",
            "MsMpEng.exe", "audiodg.exe", "Spotify.exe", "OneDrive.exe", "System", "SearchHost.exe", "RuntimeBroker.exe", "ctfmon.exe" }
            .Select(n => new ProcKey { Name = n }).ToArray();
        var other = new ProcKey { Name = "(other)" };
        var rnd = new Random(1);

        long end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        long start = end - (long)(hours * 3600_000);
        long gapAt = start + (end - start) * 2 / 3, gapLen = 5 * 60_000;   // one simulated power loss
        long tick = 0;
        for (long ts = start; ts < end; ts += 1000, tick++)
        {
            if (ts >= gapAt && ts < gapAt + gapLen) continue;
            if (ts == gapAt + gapLen)
            {
                w.Event("power_loss", "crit", "Simulated power loss (history generator)", value: gapLen / 1000.0, ts: gapAt - 1000);
                w.Event("service_start", "info", "BlackBox started (simulated)", ts: ts);
            }
            SimSensorSource.FakeNowMs = ts;
            cpu.Update();
            bool t2 = tick % cfg.Tier2EveryS == 0;
            foreach (var s in sensors)
            {
                if (!(s.Tier == 1 || (t2 && s.Tier == 2))) continue;
                var v = s.Group.Kind == "Derived" ? null : s.Read();
                if (v is float f) w.AddSample(ts, s.Id, SensorSampler.Quantize(f, s.Quantum));
            }
            if (tick % cfg.ProcessIntervalS == 0)
            {
                // ~15 persisted processes + (other), like a real desktop with a game running
                double cpuW = SimSensorSource.CpuPower, gpuW = SimSensorSource.GpuPower;
                bool game = SimSensorSource.GameLoad > 0.5;
                for (int i = 0; i < names.Length; i++)
                {
                    if (i == 0 && !game) continue;
                    double c = i == 0 ? SimSensorSource.CpuLoad * 0.8 : rnd.NextDouble() * 2;
                    double g = i == 0 ? SimSensorSource.GpuLoad * 0.97 : i == 5 ? 1.5 : 0;
                    w.AddProc(new ProcRow { Ts = ts, Pid = 1000 + i * 4, Key = names[i], Cpu = (float)c, Gpu = (float)g,
                        WsMb = i == 0 ? 6000 : 100 + i * 20, IoBps = (float)(rnd.NextDouble() * 1e6),
                        EstCpuW = (float)(cpuW * c / 60), EstGpuW = (float)(gpuW * g / 100) });
                }
                w.AddProc(new ProcRow { Ts = ts, Pid = 0, Key = other, Cpu = 3, Gpu = 0.2f, WsMb = 4000, IoBps = 1e5f, EstCpuW = 2, EstGpuW = 0.5f });
            }
            if (tick % 2 == 1) w.Flush();
            if (tick % 36000 == 0) Console.WriteLine($"  {(ts - start) / 3600_000.0:0.0} h generated, db {Database.FileSize(cfg.DbPath) / 1048576.0:0} MB");
        }
        w.Flush();
        w.Dispose();
        SimSensorSource.FakeNowMs = null;
        Console.WriteLine($"Generated {hours} h in {sw.Elapsed.TotalSeconds:0} s; rows {w.RowsWritten:N0}; DB {Database.FileSize(cfg.DbPath) / 1048576.0:0.0} MB");
    }
}
