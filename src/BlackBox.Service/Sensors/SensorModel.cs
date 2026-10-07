using System.Text.RegularExpressions;

namespace BlackBox.Sensors;

/// <summary>Well-known sensor roles. Limits, derived sensors, power attribution and the viewer key off these.</summary>
public static class Roles
{
    public const string CpuTctl = "cpu.tctl", CpuCcd = "cpu.ccd", CpuPower = "cpu.package_power", CpuLoad = "cpu.load",
        CpuCoreClock = "cpu.core_clock", CpuCoreLoad = "cpu.core_load", CpuClockMax = "cpu.clock_max", CpuClockAvg = "cpu.clock_avg",
        CpuCoreLoadMax = "cpu.core_load_max", Vcore = "mb.vcore", Vsoc = "mb.vsoc", V12 = "mb.12v",
        GpuPower = "gpu.board_power", GpuEdge = "gpu.edge", GpuHotspot = "gpu.hotspot", GpuMem = "gpu.mem_temp",
        GpuClock = "gpu.clock", GpuLoad = "gpu.load", Vrm = "mb.vrm", Chipset = "mb.chipset",
        Fan = "fan", FanCpu = "fan.cpu", Nvme = "nvme.temp", Dimm = "dimm.temp", PowerTotal = "power.total";
}

public sealed class SensorDesc
{
    public required string Identifier { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }        // LHM SensorType name
    public required string Unit { get; init; }
    public required HwGroup Group { get; init; }
    public required Func<float?> Read { get; init; }
    public int Tier { get; set; } = 2;               // 0 = not stored, 1 = every tick, 2 = every Tier2EveryS
    public string? Role { get; set; }
    public double Quantum { get; init; } = 0.01;
    public int Id { get; set; }                       // sensor.id in DB

    // live state (written by sampler thread only, read by evaluator/process sampler)
    public double Last = double.NaN;
    public long LastTs;
}

public sealed class HwGroup
{
    public required string Identifier { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required Action Update { get; init; }
    public List<SensorDesc> Sensors { get; } = new();
    public int Id { get; set; }
    public bool HasTier1, HasTier2;
    public int Failures;
}

public interface ISensorSource : IDisposable
{
    /// <summary>Open (first call) and enumerate hardware + sensors. Must call Update once before enumerating.</summary>
    List<HwGroup> Enumerate();
    /// <summary>True when the hardware/sensor set changed since the last Enumerate (sensor added/removed, device reset).</summary>
    bool Changed { get; }
    /// <summary>Close and reopen the backend (after repeated failures, e.g. GPU driver reset).</summary>
    void Reset();
    string DriverInfo { get; }
}

public static class SensorClassifier
{
    static readonly RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static bool M(string s, string rx) => Regex.IsMatch(s, rx, I);

    public static (string unit, double quantum) UnitFor(string type) => type switch
    {
        "Temperature" => ("°C", 0.1),
        "Power" => ("W", 0.1),
        "Clock" => ("MHz", 1),
        "Frequency" => ("Hz", 1),
        "Fan" => ("RPM", 1),
        "Voltage" => ("V", 0.001),
        "Current" => ("A", 0.01),
        "Load" or "Control" or "Level" => ("%", 0.1),
        "Data" => ("GB", 0.01),
        "SmallData" => ("MB", 1),
        "Throughput" => ("B/s", 1),
        "Energy" => ("mWh", 1),
        "Flow" => ("L/h", 0.1),
        "Factor" => ("", 0.001),
        _ => ("", 0.01),
    };

    /// <summary>Assign tier + role for one sensor (spec §4.1).</summary>
    public static (int tier, string? role) Classify(string hwKind, string hwName, string type, string name)
    {
        bool cpu = hwKind == "Cpu", gpu = hwKind.StartsWith("Gpu"), sio = hwKind is "SuperIO" or "EmbeddedController" or "Motherboard";
        switch (type)
        {
            case "Temperature":
                if (cpu && M(name, @"tctl|tdie") && !M(name, "ccd")) return (1, Roles.CpuTctl);
                if (cpu && M(name, @"^ccd\s*\d")) return (1, Roles.CpuCcd);
                if (gpu && M(name, @"hot\s*spot|junction")) return (1, Roles.GpuHotspot);
                if (gpu && M(name, @"memory|vram|mem")) return (1, Roles.GpuMem);
                if (gpu && M(name, @"^gpu core$|edge|^gpu$")) return (1, Roles.GpuEdge);
                if (sio && M(name, "vrm|mos")) return (1, Roles.Vrm);
                if (sio && M(name, "chipset|pch")) return (2, Roles.Chipset);
                if (hwKind == "Storage" && M(name, @"composite|^temperature$|temperature 1")) return (2, Roles.Nvme);
                if (hwKind == "Memory") return (2, Roles.Dimm);
                return (2, null);
            case "Power":
                if (cpu && M(name, "package|ppt")) return (1, Roles.CpuPower);
                if (gpu && M(name, "package|board|total|ppt|tbp")) return (1, Roles.GpuPower);
                return (2, null);
            case "Clock":
                if (cpu && M(name, @"^core #\d+$")) return (2, Roles.CpuCoreClock);
                if (gpu && M(name, @"^gpu core$|^core$|shader|graphics")) return (1, Roles.GpuClock);
                return (2, null);
            case "Load":
                if (cpu && M(name, "total")) return (1, Roles.CpuLoad);
                if (cpu && M(name, @"core #\d+$")) return (2, Roles.CpuCoreLoad);
                if (gpu && M(name, @"^gpu core$|^core$|^gpu$")) return (1, Roles.GpuLoad);
                return (2, null);
            case "Fan":
                return (1, M(name, @"^cpu(\s|_)?fan|^cpu$|cpu fan") ? Roles.FanCpu : Roles.Fan);
            case "Voltage":
                if (M(name, @"\+?12\s*v")) return (1, Roles.V12);
                if (sio && M(name, @"vcore|cpu core")) return (1, Roles.Vcore);
                if (cpu && M(name, @"^core\b|vddcr_cpu") && !M(name, @"#\d")) return (1, Roles.Vcore);
                if ((sio || cpu) && M(name, @"^v?soc\b|\bsoc\b|vddcr_soc")) return (1, Roles.Vsoc);
                return (2, null);
            default:
                return (2, null);
        }
    }

    /// <summary>Post-pass over all groups: pick one discrete GPU for gpu.* roles; ensure a board-power role exists.</summary>
    public static void Resolve(List<HwGroup> groups, IReadOnlyDictionary<string, int> tierOverrides)
    {
        var gpus = groups.Where(g => g.Kind.StartsWith("Gpu")).ToList();
        var primary = gpus.OrderByDescending(g => M(g.Name, @"\bRX\b|\bRTX\b|\bGTX\b|\bArc\b|\d{4}") ? 1 : 0).FirstOrDefault();
        foreach (var g in gpus.Where(g => g != primary))
            foreach (var s in g.Sensors.Where(s => s.Role?.StartsWith("gpu.") == true)) { s.Role = null; s.Tier = 2; }
        if (primary != null && !primary.Sensors.Any(s => s.Role == Roles.GpuPower))
        {
            var p = primary.Sensors.FirstOrDefault(s => s.Type == "Power");
            if (p != null) { p.Role = Roles.GpuPower; p.Tier = 1; }
        }
        // only one Tctl role (prefer the combined "Tctl/Tdie" sensor)
        var tctl = groups.SelectMany(g => g.Sensors).Where(s => s.Role == Roles.CpuTctl).ToList();
        if (tctl.Count > 1)
            foreach (var s in tctl.OrderByDescending(s => s.Name.Contains("Tctl/Tdie") ? 1 : 0).Skip(1)) { s.Role = null; s.Tier = 2; }

        foreach (var (rx, tier) in tierOverrides)
        {
            var re = new Regex(rx, I);
            foreach (var s in groups.SelectMany(g => g.Sensors))
                if (re.IsMatch(s.Identifier) || re.IsMatch(s.Group.Name + "/" + s.Name)) s.Tier = tier;
        }
        foreach (var g in groups)
        {
            g.HasTier1 = g.Sensors.Any(s => s.Tier == 1 || s.Role == Roles.CpuCoreClock || s.Role == Roles.CpuCoreLoad);
            g.HasTier2 = g.Sensors.Any(s => s.Tier == 2);
        }
    }
}
