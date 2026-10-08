// Milestone 1: list every LHM sensor on this machine with live values.
// Usage: BlackBox.SensorDump.exe [--watch] [--json out.json]
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

bool watch = args.Contains("--watch");
string? jsonPath = args.SkipWhile(a => a != "--json").Skip(1).FirstOrDefault();

Console.WriteLine($"PawnIO installed: {PawnIoInstalled()}");
var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsMotherboardEnabled = true,
    IsMemoryEnabled = true,
    IsStorageEnabled = true,
    IsNetworkEnabled = false,
    IsControllerEnabled = false,
    IsPsuEnabled = false,
    IsBatteryEnabled = false,
    IsPowerMonitorEnabled = false,
};
var sw = System.Diagnostics.Stopwatch.StartNew();
computer.Open();
Console.WriteLine($"Computer.Open(): {sw.ElapsedMilliseconds} ms");

var all = new List<IHardware>();
void Collect(IHardware h) { all.Add(h); foreach (var s in h.SubHardware) Collect(s); }
foreach (var h in computer.Hardware) Collect(h);

do
{
    if (watch) Console.Clear();
    var rows = new List<object>();
    foreach (var h in all)
    {
        sw.Restart();
        h.Update();
        long ms = sw.ElapsedMilliseconds;
        Console.WriteLine($"\n[{h.HardwareType}] {h.Name}  ({h.Identifier})  Update={ms} ms");
        foreach (var s in h.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
        {
            Console.WriteLine($"  {s.SensorType,-12} {s.Name,-32} {Fmt(s.Value),12}   {s.Identifier}");
            rows.Add(new { hardware = h.Name, hwType = h.HardwareType.ToString(), hwId = h.Identifier.ToString(),
                           type = s.SensorType.ToString(), name = s.Name, id = s.Identifier.ToString(), value = s.Value });
        }
    }
    CheckExpected();
    if (jsonPath != null)
    {
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nwrote {jsonPath}");
        jsonPath = null;
    }
    if (watch) Thread.Sleep(1000);
} while (watch);
computer.Close();

static string Fmt(float? v) => v.HasValue ? v.Value.ToString("0.###") : "-";

static bool PawnIoInstalled()
{
    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
    return k != null;
}

void CheckExpected()
{
    // Spec §10.1: confirm GPU (RDNA4) and Super I/O sensors appear; report what is missing.
    var sensors = all.SelectMany(h => h.Sensors).ToList();
    bool Has(HardwareType hw, SensorType t, string rx) => sensors.Any(s => s.Hardware.HardwareType == hw && s.SensorType == t
        && System.Text.RegularExpressions.Regex.IsMatch(s.Name, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    static bool IsGpu(IHardware h) => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;
    static bool Regex(string s, string rx) => System.Text.RegularExpressions.Regex.IsMatch(s, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var checks = new (string what, bool ok)[]
    {
        ("CPU temperature (Tctl/Tdie or CPU Package)", Has(HardwareType.Cpu, SensorType.Temperature, "tctl|tdie|^cpu package$")),
        ("CPU package power", Has(HardwareType.Cpu, SensorType.Power, "package")),
        ("GPU present", all.Any(IsGpu)),
        ("GPU core temp", sensors.Any(s => IsGpu(s.Hardware) && s.SensorType == SensorType.Temperature)),
        ("GPU hotspot temp (not on every card)", sensors.Any(s => IsGpu(s.Hardware) && s.SensorType == SensorType.Temperature && Regex(s.Name, "hot ?spot"))),
        ("GPU memory temp (not on every card)", sensors.Any(s => IsGpu(s.Hardware) && s.SensorType == SensorType.Temperature && Regex(s.Name, "memory"))),
        ("GPU power", sensors.Any(s => IsGpu(s.Hardware) && s.SensorType == SensorType.Power)),
        ("Motherboard sensor chip (Super I/O)", all.Any(h => h.HardwareType == HardwareType.SuperIO)),
        ("Fan RPMs", sensors.Any(s => s.SensorType == SensorType.Fan)),
        ("VRM temp (board-specific)", sensors.Any(s => s.SensorType == SensorType.Temperature && Regex(s.Name, "vrm|mos"))),
        ("+12V rail", sensors.Any(s => s.SensorType == SensorType.Voltage && s.Name.Contains("12", StringComparison.Ordinal))),
        ("NVMe temp", Has(HardwareType.Storage, SensorType.Temperature, ".")),
        ("DIMM temp", Has(HardwareType.Memory, SensorType.Temperature, ".")),
    };
    Console.WriteLine("\n== Spec coverage ==");
    foreach (var (what, ok) in checks) Console.WriteLine($"  [{(ok ? " OK " : "MISS")}] {what}");
}
