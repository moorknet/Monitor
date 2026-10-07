using System.Diagnostics;
using BlackBox.Db;
using BlackBox.Sensors;

namespace BlackBox.Processes;

/// <summary>
/// Every ProcessIntervalS: per-process CPU %, GPU %, working set, I/O rate and estimated power share (spec §4.2).
/// Persists top-N by CPU ∪ top-N by GPU ∪ anything over the threshold; the rest is folded into one "(other)" row.
/// </summary>
public sealed class ProcessSampler : BackgroundService
{
    sealed class PState
    {
        public long CreateTime, PrevCpu, PrevIo, DCpu, DIo, Ws;
        public required ProcKey Key;
        public bool PathResolved, Seen, Keep;
        public int Pid;
        public double Cpu, Gpu;
    }

    readonly Config _cfg;
    readonly Writer _writer;
    readonly IProcessSource _src;
    readonly SensorSampler _sensors;
    readonly Stats _stats;
    readonly ILogger _log;
    readonly Dictionary<int, PState> _state = new(512);
    readonly Dictionary<int, double> _gpu = new(256);
    readonly List<PState> _active = new(512), _gone = new(64);
    readonly ProcKey _other = new() { Name = "(other)" };
    ProcSnap[] _snap = new ProcSnap[512];
    long _prevWall;
    bool _first = true;

    static readonly Comparison<PState> ByCpu = (a, b) => b.Cpu.CompareTo(a.Cpu);
    static readonly Comparison<PState> ByGpu = (a, b) => b.Gpu.CompareTo(a.Gpu);

    public ProcessSampler(Config cfg, Writer writer, IProcessSource src, SensorSampler sensors, Stats stats, ILogger<ProcessSampler> log)
    { _cfg = cfg; _writer = writer; _src = src; _sensors = sensors; _stats = stats; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_cfg.ProcessIntervalS));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { Sample(); }
            catch (Exception ex) { _log.LogError(ex, "Process sample failed"); }
        }
    }

    void Sample()
    {
        long t0 = Stopwatch.GetTimestamp();
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int n = _src.Snapshot(ref _snap);
        _src.CollectGpu(_gpu);
        long wall = Stopwatch.GetTimestamp();
        double dtSec = _prevWall == 0 ? _cfg.ProcessIntervalS : Stopwatch.GetElapsedTime(_prevWall, wall).TotalSeconds;
        _prevWall = wall;
        double cores = Environment.ProcessorCount;

        foreach (var s in _state.Values) s.Seen = false;
        _active.Clear();
        long totalCpu = 0;
        double totalGpu = 0;
        for (int i = 0; i < n; i++)
        {
            ref var sn = ref _snap[i];
            if (sn.Pid == 0) continue; // Idle
            if (!_state.TryGetValue(sn.Pid, out var st) || st.CreateTime != sn.CreateTime)
            {
                if (st != null) ProcessExit(st, ts); // PID reuse
                st = new PState { Pid = sn.Pid, CreateTime = sn.CreateTime, PrevCpu = sn.CpuTime, PrevIo = sn.IoBytes, Key = new ProcKey { Name = _src.GetName(sn) } };
                _state[sn.Pid] = st;
                if (!_first) _writer.Event("process_start", "info", st.Key.Name, value: sn.Pid, ts: ts);
            }
            st.Seen = true;
            st.DCpu = Math.Max(0, sn.CpuTime - st.PrevCpu); st.PrevCpu = sn.CpuTime;
            st.DIo = Math.Max(0, sn.IoBytes - st.PrevIo); st.PrevIo = sn.IoBytes;
            st.Ws = sn.WorkingSet;
            st.Cpu = st.DCpu / (dtSec * 1e7 * cores) * 100;
            st.Gpu = _gpu.TryGetValue(sn.Pid, out var g) ? g : 0;
            totalCpu += st.DCpu;
            totalGpu += st.Gpu;
            st.Keep = false;
            _active.Add(st);
        }
        _gone.Clear();
        foreach (var s in _state.Values) if (!s.Seen) _gone.Add(s);
        foreach (var s in _gone) { _state.Remove(s.Pid); ProcessExit(s, ts); }

        if (_first) { _first = false; return; } // first pass only establishes the CPU-time baseline

        // selection: top-N CPU ∪ top-N GPU ∪ over threshold
        _active.Sort(ByCpu);
        for (int i = 0; i < _active.Count && i < _cfg.TopByCpu; i++) _active[i].Keep = true;
        _active.Sort(ByGpu);
        for (int i = 0; i < _active.Count && i < _cfg.TopByGpu; i++) if (_active[i].Gpu > 0) _active[i].Keep = true;

        double cpuW = Fresh(_sensors.CpuPower, ts), gpuW = Fresh(_sensors.GpuPower, ts);
        double oCpu = 0, oGpu = 0, oWs = 0, oIo = 0, oCw = 0, oGw = 0;
        foreach (var s in _active)
        {
            double ecw = totalCpu > 0 && !double.IsNaN(cpuW) ? cpuW * s.DCpu / totalCpu : 0;
            double egw = totalGpu > 0 && !double.IsNaN(gpuW) ? gpuW * s.Gpu / totalGpu : 0;
            double io = s.DIo / dtSec, ws = s.Ws / 1048576.0;
            if (s.Keep || s.Cpu > _cfg.ProcThresholdPct || s.Gpu > _cfg.ProcThresholdPct)
            {
                if (!s.PathResolved) { s.PathResolved = true; s.Key.Path = SafePath(s.Pid); }
                _writer.AddProc(new ProcRow { Ts = ts, Pid = s.Pid, Key = s.Key, Cpu = (float)s.Cpu, Gpu = (float)s.Gpu,
                    WsMb = (float)ws, IoBps = (float)io, EstCpuW = (float)ecw, EstGpuW = (float)egw });
            }
            else { oCpu += s.Cpu; oGpu += s.Gpu; oWs += ws; oIo += io; oCw += ecw; oGw += egw; }
        }
        _writer.AddProc(new ProcRow { Ts = ts, Pid = 0, Key = _other, Cpu = (float)oCpu, Gpu = (float)oGpu, WsMb = (float)oWs,
            IoBps = (float)oIo, EstCpuW = (float)oCw, EstGpuW = (float)oGw });

        _stats.ProcCount = _active.Count;
        _stats.LastProcMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (_stats.LastProcMs > _stats.MaxProcMs) _stats.MaxProcMs = _stats.LastProcMs;
    }

    static double Fresh(SensorDesc? s, long ts) => s != null && ts - s.LastTs < 5000 ? s.Last : double.NaN;

    string? SafePath(int pid) { try { return _src.GetPath(pid); } catch { return null; } }

    void ProcessExit(PState s, long ts) => _writer.Event("process_exit", "info", s.Key.Name, value: s.Pid, ts: ts);
}
