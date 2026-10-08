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
        Fan = "fan", FanCpu = "fan.cpu", Nvme = "nvme.temp", NvmeMax = "nvme.max", Dimm = "dimm.temp", PowerTotal = "power.total";
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
    public int DefaultTier { get; set; } = 2;        // before the user's Settings override
    public string? DefaultRole { get; set; }
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
        // constants some devices report as "sensors" (RAM SPD hub: "Thermal Sensor High Limit", "Temperature Sensor Resolution")
        if (M(name, @"\blimit\b|resolution|threshold")) return (0, null);
        switch (type)
        {
            case "Temperature":
                // AMD: "Core (Tctl/Tdie)"; Intel: "CPU Package"
                if (cpu && ((M(name, @"tctl|tdie") && !M(name, "ccd")) || M(name, @"^cpu package$"))) return (1, Roles.CpuTctl);
                if (cpu && M(name, @"^ccd\s*\d")) return (1, Roles.CpuCcd);
                if (cpu && M(name, @"^core max$")) return (1, null);                       // Intel hottest core
                // memory before hotspot: Nvidia calls it "GPU Memory Junction"
                if (gpu && M(name, @"memory|vram|\bmem\b")) return (1, Roles.GpuMem);
                if (gpu && M(name, @"hot\s*spot|junction")) return (1, Roles.GpuHotspot);
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
                // AMD "Core #1", Intel "CPU Core #1", hybrid Intel "CPU P-Core #1" / "E-Core #1"
                if (cpu && M(name, @"^(cpu )?([pe]-)?core #\d+$")) return (2, Roles.CpuCoreClock);
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
                if (cpu && M(name, @"^core\b|^cpu core$|vddcr_cpu") && !M(name, @"#\d")) return (1, Roles.Vcore);
                if ((sio || cpu) && M(name, @"^v?soc\b|\bsoc\b|vddcr_soc")) return (1, Roles.Vsoc);
                return (2, null);
            default:
                return (2, null);
        }
    }

    /// <summary>Post-pass over all groups: pick one discrete GPU for gpu.* roles; ensure a board-power role exists.</summary>
    public static void Resolve(List<HwGroup> groups, IReadOnlyDictionary<string, int> tierOverrides, IReadOnlyDictionary<string, int>? userTiers = null)
    {
        var gpus = groups.Where(g => g.Kind.StartsWith("Gpu")).ToList();
        // discrete card wins over the iGPU ("AMD Radeon(TM) Graphics", "Intel UHD/Arc Graphics")
        static int Rank(HwGroup g) => M(g.Name, @"\bRTX\b|\bGTX\b|\bRX\b|Quadro|Radeon Pro") ? 3 : M(g.Name, @"\bArc\b.*\b[AB]\d{3}") ? 2
            : g.Kind == "GpuNvidia" ? 2 : M(g.Name, @"\(TM\) Graphics|UHD|Iris|Arc\(TM\) Graphics") ? 0 : 1;
        var primary = gpus.OrderByDescending(Rank).FirstOrDefault();
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
        foreach (var s in groups.SelectMany(g => g.Sensors)) { s.DefaultTier = s.Tier; s.DefaultRole = s.Role; }

        // Settings tab: per-sensor tier by LHM identifier. Off (0) also drops the role, so a misreporting sensor
        // stops feeding charts, limits, derived values and the energy meter; a fallback takes its role if one exists.
        if (userTiers != null)
            foreach (var s in groups.SelectMany(g => g.Sensors))
                if (userTiers.TryGetValue(s.Identifier, out var t) && t is >= 0 and <= 2)
                {
                    s.Tier = t;
                    if (t == 0) s.Role = null;
                }
        if (primary != null && !groups.SelectMany(g => g.Sensors).Any(s => s.Role == Roles.GpuPower))
        {
            var p = primary.Sensors.FirstOrDefault(s => s.Type == "Power" && s.Tier != 0);
            if (p != null) { p.Role = Roles.GpuPower; if (p.Tier == 2 && userTiers?.ContainsKey(p.Identifier) != true) p.Tier = 1; }
        }
        if (!groups.SelectMany(g => g.Sensors).Any(s => s.Role == Roles.CpuTctl))
        {
            var t = groups.Where(g => g.Kind == "Cpu").SelectMany(g => g.Sensors)
                .FirstOrDefault(s => s.Type == "Temperature" && s.Tier != 0 && M(s.Name, "tctl|tdie|package|ccd"));
            if (t != null) t.Role = Roles.CpuTctl;
        }

        foreach (var g in groups)
        {
            g.HasTier1 = g.Sensors.Any(s => s.Tier == 1 || (s.Tier != 0 && (s.Role == Roles.CpuCoreClock || s.Role == Roles.CpuCoreLoad)));
            g.HasTier2 = g.Sensors.Any(s => s.Tier == 2);
        }
    }
}
