using System.Text.Json;
using System.Text.RegularExpressions;
using BlackBox.Db;
using BlackBox.Sensors;

namespace BlackBox;

public sealed class LimitDef
{
    public string? Role { get; set; }
    public string? SensorRegex { get; set; }     // optional: match "hardware name/sensor name" instead of role
    public string? Label { get; set; }
    public string Kind { get; set; } = "high";   // high | low | band | zero_while
    public double? Warn { get; set; }
    public double? Crit { get; set; }
    public double? Nominal { get; set; }         // band
    public double? WarnPct { get; set; }
    public double? CritPct { get; set; }
    public double Hysteresis { get; set; } = 2;
    public double MinDurationS { get; set; } = 3;
    public string? WhileRole { get; set; }       // zero_while: e.g. fan at 0 RPM while cpu.tctl > 60
    public double? WhileAbove { get; set; }
    public string? Source { get; set; }
}

public sealed class Profile
{
    public string Name { get; set; } = "";
    public string? HardwareRegex { get; set; }   // null = generic fallback profile
    public List<LimitDef> Limits { get; set; } = new();
    public string? File;
}

public sealed class BoundLimit
{
    public required SensorDesc Sensor { get; init; }
    public required LimitDef Def { get; init; }
    public required string ProfileName { get; init; }
    public SensorDesc? While { get; init; }
    public int Level, Pending;
    public long PendingSince, BreachStart;
    public double Peak;

    /// <summary>Thresholds in sensor units for chart lines (band → both sides).</summary>
    public (double? warnHi, double? critHi, double? warnLo, double? critLo) Lines() => Def.Kind switch
    {
        "low" => (null, null, Def.Warn, Def.Crit),
        "band" when Def.Nominal is double n => (n * (1 + Def.WarnPct / 100), n * (1 + Def.CritPct / 100), n * (1 - Def.WarnPct / 100), n * (1 - Def.CritPct / 100)),
        "zero_while" => (null, null, null, Def.Crit ?? 0),
        _ => (Def.Warn, Def.Crit, null, null),
    };

    /// <summary>Map a value to "badness" space where higher is worse; returns thresholds in the same space.</summary>
    public (double x, double warnT, double critT) Metric(double v)
    {
        var d = Def;
        switch (d.Kind)
        {
            case "low": return (-v, -(d.Warn ?? double.NegativeInfinity), -(d.Crit ?? double.NegativeInfinity));
            case "band":
                double n = d.Nominal ?? v;
                return (Math.Abs(v - n) / n * 100, d.WarnPct ?? double.PositiveInfinity, d.CritPct ?? double.PositiveInfinity);
            case "zero_while":
                bool cond = v <= (d.Crit ?? 0) && While is { } w && !double.IsNaN(w.Last) && w.Last > (d.WhileAbove ?? 60);
                return (cond ? 1 : 0, double.PositiveInfinity, 1);
            default: return (v, d.Warn ?? double.PositiveInfinity, d.Crit ?? double.PositiveInfinity);
        }
    }

    public bool Worse(double a, double b) => Def.Kind switch { "low" or "zero_while" => a < b, "band" => Math.Abs(a - (Def.Nominal ?? 0)) > Math.Abs(b - (Def.Nominal ?? 0)), _ => a > b };
}

public sealed class LimitEvaluator
{
    readonly Config _cfg;
    readonly Writer _writer;
    readonly ILogger _log;
    public List<Profile> Profiles { get; } = new();
    public IReadOnlyList<BoundLimit> Bound { get; private set; } = Array.Empty<BoundLimit>();
    public IReadOnlyList<string> ActiveProfiles { get; private set; } = Array.Empty<string>();

    // throttle heuristic state
    SensorDesc? _load, _clk, _tctl;
    double _tctlWarn = 85;
    readonly double[] _clkRing = new double[60], _scratch = new double[60];
    int _clkN, _clkI;
    long _thrSince, _thrStart;
    bool _throttling;

    public LimitEvaluator(Config cfg, Writer writer, ILogger<LimitEvaluator> log)
    {
        _cfg = cfg; _writer = writer; _log = log;
        var dir = Path.Combine(AppContext.BaseDirectory, "profiles");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.json").Order())
                try
                {
                    var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(f), Config.Json)!;
                    p.File = Path.GetFileName(f);
                    Profiles.Add(p);
                }
                catch (Exception ex) { _log.LogError(ex, "Bad profile {f}", f); }
    }

    /// <summary>Bind limits to the current sensor set. Precedence: config overrides → matched hardware profiles → generic.</summary>
    public void Bind(List<HwGroup> groups)
    {
        var sensors = groups.SelectMany(g => g.Sensors).ToList();
        var hwNames = groups.Select(g => g.Name).ToList();
        var active = Profiles.Where(p => p.HardwareRegex == null || hwNames.Any(n => Regex.IsMatch(n, p.HardwareRegex, RegexOptions.IgnoreCase))).ToList();
        var ordered = new List<(string profile, LimitDef def)>();
        ordered.AddRange(_cfg.LimitOverrides.Select(d => ("config.json", d)));
        ordered.AddRange(active.Where(p => p.HardwareRegex != null).SelectMany(p => p.Limits.Select(d => (p.Name, d))));
        ordered.AddRange(active.Where(p => p.HardwareRegex == null).SelectMany(p => p.Limits.Select(d => (p.Name, d))));

        var bound = new List<BoundLimit>();
        foreach (var s in sensors)
        {
            foreach (var (profile, d) in ordered)
            {
                bool match = d.SensorRegex != null
                    ? Regex.IsMatch(s.Group.Name + "/" + s.Name, d.SensorRegex, RegexOptions.IgnoreCase)
                    : d.Role != null && d.Role == s.Role;
                if (!match) continue;
                var w = d.WhileRole == null ? null : sensors.FirstOrDefault(x => x.Role == d.WhileRole);
                bound.Add(new BoundLimit { Sensor = s, Def = d, ProfileName = profile, While = w });
                break; // first (highest precedence) match wins
            }
        }
        Bound = bound;
        ActiveProfiles = active.Select(p => p.Name).ToList();
        _load = sensors.FirstOrDefault(s => s.Role == Roles.CpuLoad);
        _clk = sensors.FirstOrDefault(s => s.Role == Roles.CpuClockAvg);
        _tctl = sensors.FirstOrDefault(s => s.Role == Roles.CpuTctl);
        _tctlWarn = bound.FirstOrDefault(b => b.Sensor == _tctl)?.Def.Warn ?? 85;
        _log.LogInformation("Limits bound: {n} (profiles: {p})", bound.Count, string.Join(", ", ActiveProfiles));
    }

    public void Evaluate(long ts)
    {
        foreach (var b in Bound)
        {
            var s = b.Sensor;
            if (s.LastTs != ts || double.IsNaN(s.Last)) continue; // only fresh values (tier-2 sensors every N s)
            double v = s.Last;
            var (x, warnT, critT) = b.Metric(v);
            int raw = x >= critT ? 2 : x >= warnT ? 1 : 0;
            int target = raw;
            if (target < b.Level)
            {
                // hysteresis: only step down once clearly below the threshold of the current level
                double thr = b.Level == 2 ? critT : warnT;
                double hyst = b.Def.Kind == "zero_while" ? 0.5 : b.Def.Hysteresis;
                if (x >= thr - hyst) target = b.Level;
            }
            if (b.Level > 0 && b.Worse(v, b.Peak)) b.Peak = v;

            if (target > b.Level)
            {
                if (b.Pending != target) { b.Pending = target; b.PendingSince = ts; }
                if (ts - b.PendingSince >= b.Def.MinDurationS * 1000)
                {
                    if (b.Level == 0) { b.BreachStart = b.PendingSince; b.Peak = v; }
                    b.Level = target;
                    _writer.Event("limit_breach", target == 2 ? "crit" : "warn",
                        $"{Label(b)} {(target == 2 ? "CRIT" : "WARN")}: {v:0.##} {s.Unit} ({Thresholds(b)}; {b.Def.Source})",
                        sensorId: s.Id, value: v, ts: b.PendingSince);
                    b.Pending = 0;
                }
            }
            else
            {
                b.Pending = 0;
                if (target < b.Level)
                {
                    if (target == 0)
                        _writer.Event("limit_end", "info",
                            $"{Label(b)} back in spec after {(ts - b.BreachStart) / 1000.0:0}s, peak {b.Peak:0.##} {s.Unit}",
                            sensorId: s.Id, value: b.Peak, ts: ts);
                    b.Level = target;
                }
            }
        }
        EvaluateThrottle(ts);
    }

    void EvaluateThrottle(long ts)
    {
        if (_clk == null || _load == null || _tctl == null || _clk.LastTs != ts) return;
        double clk = _clk.Last;
        double median = double.NaN;
        if (_clkN >= 10)
        {
            Array.Copy(_clkRing, _scratch, _clkN);
            Array.Sort(_scratch, 0, _clkN);
            median = _scratch[_clkN / 2];
        }
        _clkRing[_clkI] = clk; _clkI = (_clkI + 1) % _clkRing.Length; if (_clkN < _clkRing.Length) _clkN++;
        bool cond = !double.IsNaN(median) && _load.Last > 80 && clk < median * 0.85 && _tctl.Last >= _tctlWarn;
        if (cond)
        {
            if (_thrSince == 0) _thrSince = ts;
            if (!_throttling && ts - _thrSince >= 3000)
            {
                _throttling = true; _thrStart = _thrSince;
                _writer.Event("throttle", "warn", $"Possible CPU throttling: avg clock {clk:0} MHz vs 60 s median {median:0} MHz, load {_load.Last:0}%, Tctl {_tctl.Last:0.#} °C",
                    sensorId: _clk.Id, value: clk, ts: _thrSince);
            }
        }
        else
        {
            _thrSince = 0;
            if (_throttling)
            {
                _throttling = false;
                _writer.Event("throttle_end", "info", $"Throttling ended after {(ts - _thrStart) / 1000.0:0}s", sensorId: _clk.Id, ts: ts);
            }
        }
    }

    static string Label(BoundLimit b) => b.Def.Label ?? $"{b.Sensor.Group.Name} {b.Sensor.Name}";
    static string Thresholds(BoundLimit b) => b.Def.Kind switch
    {
        "band" => $"nominal {b.Def.Nominal} ±{b.Def.WarnPct}%/±{b.Def.CritPct}%",
        "low" => $"warn ≤ {b.Def.Warn}, crit ≤ {b.Def.Crit}",
        "zero_while" => $"stalled while {b.Def.WhileRole} > {b.Def.WhileAbove}",
        _ => $"warn ≥ {b.Def.Warn}, crit ≥ {b.Def.Crit}",
    };
}
