namespace BlackBox.Energy;

/// <summary>
/// Integrates power into energy per 15-minute bucket (the price resolution). Fed by the sensor tick, drained by
/// the writer on every commit, so a power cut loses at most one commit interval of energy.
/// Wall power is an estimate: (CPU package + GPU board + configured rest-of-system load) / PSU efficiency.
/// </summary>
public sealed class EnergyMeter
{
    public const long QuarterMs = 900_000;
    public struct Acc { public long Ts; public double CpuWh, GpuWh, WallWh, Seconds; }

    readonly ElectricityConfig _cfg;
    readonly object _lock = new();
    readonly Acc[] _pending = new Acc[4];   // at most 2 buckets between commits; spare room for slow commits
    int _n;
    long _lastTs;

    public double LastWallW { get; private set; } = double.NaN;
    public double LastCpuW { get; private set; } = double.NaN;
    public double LastGpuW { get; private set; } = double.NaN;

    public EnergyMeter(Config cfg) => _cfg = cfg.Electricity;

    public double WallWatts(double cpuW, double gpuW) => (cpuW + gpuW + _cfg.BaseLoadW) / Math.Clamp(_cfg.PsuEfficiency, 0.5, 1.0);

    /// <summary>Add one sample. Gaps (sleep, stopped service) are not integrated: dt is capped at 5 s.</summary>
    public void Add(long ts, double cpuW, double gpuW)
    {
        if (double.IsNaN(cpuW) && double.IsNaN(gpuW)) { _lastTs = 0; return; }
        if (double.IsNaN(cpuW)) cpuW = 0;
        if (double.IsNaN(gpuW)) gpuW = 0;
        double dt = _lastTs == 0 ? 1 : (ts - _lastTs) / 1000.0;
        _lastTs = ts;
        if (dt <= 0) return;
        if (dt > 5) dt = 1;
        double wall = WallWatts(cpuW, gpuW);
        LastWallW = wall; LastCpuW = cpuW; LastGpuW = gpuW;
        long q = ts / QuarterMs * QuarterMs;
        lock (_lock)
        {
            int i = 0;
            while (i < _n && _pending[i].Ts != q) i++;
            if (i == _n)
            {
                if (_n == _pending.Length) return; // writer stalled for > 30 min; drop rather than allocate
                _pending[_n++] = new Acc { Ts = q };
            }
            ref var a = ref _pending[i];
            a.CpuWh += cpuW * dt / 3600; a.GpuWh += gpuW * dt / 3600; a.WallWh += wall * dt / 3600; a.Seconds += dt;
        }
    }

    /// <summary>Copy pending buckets into <paramref name="dst"/> and reset. Returns count.</summary>
    public int Drain(Acc[] dst)
    {
        lock (_lock)
        {
            int n = _n;
            Array.Copy(_pending, dst, n);
            _n = 0;
            return n;
        }
    }
}
