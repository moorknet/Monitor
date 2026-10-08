using System.Diagnostics;
using BlackBox.Db;
using BlackBox.Energy;
using BlackBox.Sensors;

namespace BlackBox;

/// <summary>
/// Dev tool: <c>BlackBox --simulate --data DIR --generate-history 72</c> writes N hours of synthetic history through the
/// real Writer (same tiering, row shapes and commit batching) to measure DB size, startup time and query latency.
/// </summary>
public static class HistoryGenerator
{
    public static void Run(Config cfg, double hours, double energyDays, ILoggerFactory lf)
    {
        var sw = Stopwatch.StartNew();
        var meter = new EnergyMeter(cfg);
        var w = new Writer(cfg, lf.CreateLogger<Writer>(), meter);
        w.Open(false);
        var src = new SimSensorSource();
        var groups = src.Enumerate();
        SensorClassifier.Resolve(groups, cfg.TierOverrides, cfg.SensorTiers);
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
            meter.Add(ts, SimSensorSource.CpuPower, SimSensorSource.GpuPower);
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
        if (energyDays * 24 > hours) LongTermEnergy(w, cfg, names[0], names[5], other, start - (long)((energyDays * 24 - hours) * 3600_000), start);
        w.Dispose();
        SimSensorSource.FakeNowMs = null;
        Console.WriteLine($"Generated {hours} h in {sw.Elapsed.TotalSeconds:0} s; rows {w.RowsWritten:N0}; DB {Database.FileSize(cfg.DbPath) / 1048576.0:0.0} MB");
    }

    /// <summary>Aggregates only (energy per 15 min, per-process energy per hour) for days before the detailed window:
    /// evening gaming sessions, longer on weekends, the PC sleeping at night.</summary>
    static void LongTermEnergy(Writer w, Config cfg, ProcKey game, ProcKey dwm, ProcKey other, long from, long to)
    {
        var rnd = new Random(3);
        var em = new EnergyMeter(cfg);
        w.WithConnection(c =>
        {
            int Id(ProcKey k) => Database.Scalar<int>(c, "INSERT INTO procname(name, path) VALUES ($n, NULL) ON CONFLICT(name, path) DO UPDATE SET name=excluded.name RETURNING id", ("$n", k.Name));
            int gid = Id(game), did = Id(dwm), oid = Id(other);
            using var tx = c.BeginTransaction();
            using var eq = c.CreateCommand(); eq.Transaction = tx;
            eq.CommandText = "INSERT OR REPLACE INTO energy_quarter(ts,cpu_wh,gpu_wh,wall_wh,seconds) VALUES ($t,$c,$g,$w,900)";
            using var ph = c.CreateCommand(); ph.Transaction = tx;
            ph.CommandText = "INSERT INTO proc_energy_hour(ts,procname_id,wh) VALUES ($t,$p,$wh) ON CONFLICT(ts,procname_id) DO UPDATE SET wh=wh+excluded.wh";
            for (long q = from / EnergyMeter.QuarterMs * EnergyMeter.QuarterMs; q < to; q += EnergyMeter.QuarterMs)
            {
                var lt = DateTimeOffset.FromUnixTimeMilliseconds(q).ToLocalTime();
                bool weekend = lt.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                double h = lt.Hour + lt.Minute / 60.0;
                bool on = h >= 8 && h < 24 && (weekend || h >= 16);
                if (!on) continue;                                         // asleep
                var day = new Random((int)(q / 86_400_000));
                double gStart = weekend ? 13 + day.Next(0, 4) : 18 + day.NextDouble() * 2, gLen = weekend ? 4 + day.Next(0, 5) : 1.5 + day.NextDouble() * 3;
                bool gaming = h >= gStart && h < gStart + gLen;
                double cpu = gaming ? 70 + rnd.NextDouble() * 15 : 30 + rnd.NextDouble() * 8;
                double gpu = gaming ? 260 + rnd.NextDouble() * 50 : 18 + rnd.NextDouble() * 6;
                double wall = em.WallWatts(cpu, gpu);
                eq.Parameters.Clear();
                eq.Parameters.AddWithValue("$t", q); eq.Parameters.AddWithValue("$c", cpu / 4); eq.Parameters.AddWithValue("$g", gpu / 4); eq.Parameters.AddWithValue("$w", wall / 4);
                eq.ExecuteNonQuery();
                long hour = q / 3_600_000 * 3_600_000;
                void P(int id, double wh) { ph.Parameters.Clear(); ph.Parameters.AddWithValue("$t", hour); ph.Parameters.AddWithValue("$p", id); ph.Parameters.AddWithValue("$wh", wh); ph.ExecuteNonQuery(); }
                if (gaming) P(gid, (cpu * 0.8 + gpu * 0.95) / 4);
                P(did, (cpu * 0.03 + gpu * 0.02) / 4);
                P(oid, (cpu * (gaming ? 0.17 : 0.97) + gpu * (gaming ? 0.03 : 0.98)) / 4);
            }
            tx.Commit();
            return 0;
        });
        Console.WriteLine($"Long-term energy written for {(to - from) / 86_400_000.0:0} days");
    }
}
