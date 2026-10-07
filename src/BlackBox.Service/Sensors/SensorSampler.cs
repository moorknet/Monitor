using System.Diagnostics;
using BlackBox.Db;

namespace BlackBox.Sensors;

/// <summary>1 Hz sensor loop. Updates a hardware item only when it has sensors due this tick (spec §4.1).</summary>
public sealed class SensorSampler : BackgroundService
{
    readonly Config _cfg;
    readonly Writer _writer;
    readonly ISensorSource _source;
    readonly LimitEvaluator _limits;
    readonly Stats _stats;
    readonly ILogger _log;

    List<HwGroup> _groups = new();
    SensorDesc[] _coreClocks = [], _coreLoads = [];
    SensorDesc? _cpuPower, _gpuPower, _gpuLoad;
    readonly HwGroup _derived;
    readonly SensorDesc _dClkMax, _dClkAvg, _dLoadMax, _dPowerTotal;
    double _vClkMax = double.NaN, _vClkAvg = double.NaN, _vLoadMax = double.NaN, _vPowerTotal = double.NaN;
    long _tick, _lastReset, _lastSlowEvent;

    public IReadOnlyList<HwGroup> Groups => _groups;
    public SensorDesc? CpuPower => _cpuPower;
    public SensorDesc? GpuPower => _gpuPower;
    public SensorDesc? GpuLoad => _gpuLoad;

    public SensorSampler(Config cfg, Writer writer, ISensorSource source, LimitEvaluator limits, Stats stats, ILogger<SensorSampler> log)
    {
        _cfg = cfg; _writer = writer; _source = source; _limits = limits; _stats = stats; _log = log;
        _derived = new HwGroup { Identifier = "/blackbox/derived", Name = "BlackBox (derived)", Kind = "Derived", Update = () => { } };
        SensorDesc D(string id, string name, string type, string role, Func<double> get)
        {
            var (unit, q) = SensorClassifier.UnitFor(type);
            var s = new SensorDesc { Identifier = "/blackbox/derived/" + id, Name = name, Type = type, Unit = unit, Quantum = q,
                Group = _derived, Tier = 1, Role = role, Read = () => { var v = get(); return double.IsNaN(v) ? null : (float)v; } };
            _derived.Sensors.Add(s);
            return s;
        }
        _dClkMax = D("cpu_clock_max", "CPU core clock (max)", "Clock", Roles.CpuClockMax, () => _vClkMax);
        _dClkAvg = D("cpu_clock_avg", "CPU core clock (avg)", "Clock", Roles.CpuClockAvg, () => _vClkAvg);
        _dLoadMax = D("cpu_core_load_max", "CPU core load (max)", "Load", Roles.CpuCoreLoadMax, () => _vLoadMax);
        _dPowerTotal = D("power_total", "CPU + GPU power", "Power", Roles.PowerTotal, () => _vPowerTotal);
        _derived.HasTier1 = true;
    }

    /// <summary>Synchronous first enumeration so the API and process sampler see sensors before the loop starts.</summary>
    public void Initialize()
    {
        _log.LogInformation("Sensor backend: {d}", _source.DriverInfo);
        Build();
    }

    void Build()
    {
        var sw = Stopwatch.StartNew();
        var groups = _source.Enumerate();
        groups.Add(_derived);
        SensorClassifier.Resolve(groups, _cfg.TierOverrides);
        _writer.Register(groups);
        var all = groups.SelectMany(g => g.Sensors).ToList();
        _coreClocks = all.Where(s => s.Role == Roles.CpuCoreClock).ToArray();
        _coreLoads = all.Where(s => s.Role == Roles.CpuCoreLoad).ToArray();
        _cpuPower = all.FirstOrDefault(s => s.Role == Roles.CpuPower);
        _gpuPower = all.FirstOrDefault(s => s.Role == Roles.GpuPower);
        _gpuLoad = all.FirstOrDefault(s => s.Role == Roles.GpuLoad);
        _groups = groups;
        _limits.Bind(groups);
        _stats.SensorCount = all.Count;
        _stats.StoredTier1 = all.Count(s => s.Tier == 1);
        _stats.StoredTier2 = all.Count(s => s.Tier == 2);
        _log.LogInformation("Enumerated {h} hardware, {s} sensors (tier1 {t1}, tier2 {t2}) in {ms} ms",
            groups.Count, all.Count, _stats.StoredTier1, _stats.StoredTier2, sw.ElapsedMilliseconds);
        foreach (var r in new[] { Roles.CpuTctl, Roles.CpuPower, Roles.GpuPower, Roles.GpuHotspot, Roles.GpuEdge, Roles.GpuMem, Roles.Vrm, Roles.V12 })
            if (!all.Any(s => s.Role == r)) _log.LogWarning("No sensor found for role {role}", r);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { Tick(); }
            catch (Exception ex) { _log.LogError(ex, "Sensor tick failed"); }
        }
    }

    void Tick()
    {
        long t0 = Stopwatch.GetTimestamp();
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool tier2 = _tick++ % _cfg.Tier2EveryS == 0;

        if (_source.Changed) { _log.LogInformation("Hardware/sensor set changed; re-enumerating"); Build(); }

        bool needReset = false;
        var groups = _groups;
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            if (g == _derived || !(g.HasTier1 || (tier2 && g.HasTier2))) continue;
            try { g.Update(); g.Failures = 0; }
            catch (Exception ex)
            {
                if (++g.Failures == 1) _log.LogWarning(ex, "Update of {hw} failed", g.Name);
                if (g.Failures >= 5) needReset = true;
                continue;
            }
            ReadGroup(g, ts, tier2);
        }

        ComputeDerived();
        ReadGroup(_derived, ts, tier2);
        _limits.Evaluate(ts);

        if (needReset && ts - _lastReset > 30_000)
        {
            // e.g. GPU TDR / driver reset: reopen the backend instead of crashing
            _lastReset = ts;
            _writer.Event("sensor_reset", "warn", "Sensor backend reset after repeated update failures (driver reset?)");
            _source.Reset();
            Build();
        }

        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _stats.OnTick(ms);
        if (ms > _cfg.SlowTickMs && ts - _lastSlowEvent > 60_000)
        {
            _lastSlowEvent = ts;
            _writer.Event("slow_tick", "warn", $"Sensor tick took {ms:0} ms (budget {_cfg.SlowTickMs} ms)", value: ms);
        }
    }

    void ReadGroup(HwGroup g, long ts, bool tier2)
    {
        var list = g.Sensors;
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            float? v;
            try { v = s.Read(); } catch { v = null; }
            if (v is not float f || !float.IsFinite(f)) continue;
            s.Last = f; s.LastTs = ts;
            if (s.Tier == 1 || (tier2 && s.Tier == 2))
                _writer.AddSample(ts, s.Id, Quantize(f, s.Quantum));
        }
    }

    internal static double Quantize(double v, double q) => q >= 1 ? Math.Round(v / q) * q : Math.Round(v, (int)Math.Round(-Math.Log10(q)));

    void ComputeDerived()
    {
        double max = double.NaN, sum = 0; int n = 0;
        foreach (var s in _coreClocks)
            if (!double.IsNaN(s.Last)) { sum += s.Last; n++; if (!(s.Last <= max)) max = s.Last; }
        _vClkMax = max; _vClkAvg = n > 0 ? sum / n : double.NaN;
        double lmax = double.NaN;
        foreach (var s in _coreLoads) if (!double.IsNaN(s.Last) && !(s.Last <= lmax)) lmax = s.Last;
        _vLoadMax = lmax;
        double c = _cpuPower?.Last ?? double.NaN, gp = _gpuPower?.Last ?? double.NaN;
        _vPowerTotal = double.IsNaN(c) && double.IsNaN(gp) ? double.NaN : (double.IsNaN(c) ? 0 : c) + (double.IsNaN(gp) ? 0 : gp);
    }
}
