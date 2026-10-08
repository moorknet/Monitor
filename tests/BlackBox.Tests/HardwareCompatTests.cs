using BlackBox;
using BlackBox.Db;
using BlackBox.Sensors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// Sensor names below are what LibreHardwareMonitor reports for each vendor; classification must find the
// roles the charts, limits and cost meter depend on without any per-machine configuration.
public class HardwareCompatTests
{
    static HwGroup G(string id, string name, string kind, params (string type, string name)[] sensors)
    {
        var g = new HwGroup { Identifier = id, Name = name, Kind = kind, Update = () => { } };
        int i = 0;
        foreach (var (type, sname) in sensors)
        {
            var (tier, role) = SensorClassifier.Classify(kind, name, type, sname);
            g.Sensors.Add(new SensorDesc { Identifier = $"{id}/{type.ToLowerInvariant()}/{i++}", Name = sname, Type = type, Unit = "",
                Group = g, Read = () => 1f, Tier = tier, Role = role });
        }
        return g;
    }

    static Dictionary<string, List<string>> Roles(List<HwGroup> groups) => groups.SelectMany(g => g.Sensors).Where(s => s.Role != null)
        .GroupBy(s => s.Role!).ToDictionary(x => x.Key, x => x.Select(s => $"{s.Group.Name}/{s.Name}").ToList());

    static List<HwGroup> IntelNvidia() =>
    [
        G("/intelcpu/0", "Intel Core i7-13700K", "Cpu",
            ("Temperature", "CPU Package"), ("Temperature", "Core Max"), ("Temperature", "Core Average"), ("Temperature", "CPU P-Core #1"),
            ("Temperature", "CPU Core #1 Distance to TjMax"), ("Power", "CPU Package"), ("Power", "CPU Cores"), ("Power", "CPU Memory"),
            ("Clock", "CPU P-Core #1"), ("Clock", "CPU P-Core #2"), ("Clock", "CPU E-Core #1"), ("Clock", "Bus Speed"),
            ("Load", "CPU Total"), ("Load", "CPU Core Max"), ("Load", "CPU P-Core #1"), ("Voltage", "CPU Core"), ("Voltage", "CPU P-Core #1")),
        G("/gpu-intel-integrated/0", "Intel(R) UHD Graphics 770", "GpuIntel", ("Power", "GPU Power"), ("Load", "D3D 3D")),
        G("/gpu-nvidia/0", "NVIDIA GeForce RTX 4070", "GpuNvidia",
            ("Temperature", "GPU Core"), ("Temperature", "GPU Hot Spot"), ("Temperature", "GPU Memory Junction"),
            ("Power", "GPU Package"), ("Clock", "GPU Core"), ("Clock", "GPU Memory"), ("Load", "GPU Core"), ("Load", "GPU Memory Controller"),
            ("Fan", "GPU Fan 1"), ("Fan", "GPU Fan 2")),
        G("/lpc/it8689e/0", "ITE IT8689E", "SuperIO",
            ("Temperature", "System #1"), ("Temperature", "VRM MOS"), ("Temperature", "PCH"), ("Temperature", "CPU Socket"),
            ("Fan", "CPU Fan"), ("Fan", "System Fan #1"), ("Voltage", "Vcore"), ("Voltage", "+12V"), ("Voltage", "+3.3V")),
        G("/nvme/0", "WD_BLACK SN850X 2000GB", "Storage", ("Temperature", "Composite Temperature")),
    ];

    [Fact]
    public void Intel_Nvidia_Gigabyte()
    {
        var groups = IntelNvidia();
        SensorClassifier.Resolve(groups, new Dictionary<string, int>());
        var r = Roles(groups);
        Assert.Equal(["Intel Core i7-13700K/CPU Package"], r[BlackBox.Sensors.Roles.CpuTctl]);
        Assert.Equal(["Intel Core i7-13700K/CPU Package"], r[BlackBox.Sensors.Roles.CpuPower]);
        Assert.Equal(3, r[BlackBox.Sensors.Roles.CpuCoreClock].Count);                       // P- and E-cores, not Bus Speed
        Assert.Equal(["NVIDIA GeForce RTX 4070/GPU Memory Junction"], r[BlackBox.Sensors.Roles.GpuMem]);
        Assert.Equal(["NVIDIA GeForce RTX 4070/GPU Hot Spot"], r[BlackBox.Sensors.Roles.GpuHotspot]);
        Assert.Equal(["NVIDIA GeForce RTX 4070/GPU Package"], r[BlackBox.Sensors.Roles.GpuPower]);   // discrete card, not the iGPU
        Assert.Equal(["NVIDIA GeForce RTX 4070/GPU Core"], r[BlackBox.Sensors.Roles.GpuLoad]);
        Assert.Contains("Intel Core i7-13700K/CPU Core", r[BlackBox.Sensors.Roles.Vcore]);
        Assert.Equal(["ITE IT8689E/VRM MOS"], r[BlackBox.Sensors.Roles.Vrm]);
        Assert.Equal(["ITE IT8689E/PCH"], r[BlackBox.Sensors.Roles.Chipset]);
        Assert.Equal(["ITE IT8689E/CPU Fan"], r[BlackBox.Sensors.Roles.FanCpu]);
        Assert.Equal(["ITE IT8689E/+12V"], r[BlackBox.Sensors.Roles.V12]);
        Assert.Equal(["WD_BLACK SN850X 2000GB/Composite Temperature"], r[BlackBox.Sensors.Roles.Nvme]);
    }

    [Fact]
    public void Amd_Radeon_with_iGpu_picks_discrete_card()
    {
        var groups = new List<HwGroup>
        {
            G("/amdcpu/0", "AMD Ryzen 9 9950X", "Cpu", ("Temperature", "Core (Tctl/Tdie)"), ("Temperature", "CCD1 (Tdie)"), ("Temperature", "CCD2 (Tdie)"),
                ("Power", "Package"), ("Clock", "Core #1"), ("Load", "CPU Total"), ("Voltage", "Core (SVI3 TFN)"), ("Voltage", "SoC (SVI3 TFN)")),
            G("/gpu-amd/1", "AMD Radeon(TM) Graphics", "GpuAmd", ("Temperature", "GPU Core"), ("Power", "GPU Package"), ("Load", "GPU Core")),
            G("/gpu-amd/0", "AMD Radeon RX 7900 XTX", "GpuAmd", ("Temperature", "GPU Core"), ("Temperature", "GPU Hot Spot"),
                ("Temperature", "GPU Memory"), ("Power", "GPU Package"), ("Clock", "GPU Core"), ("Load", "GPU Core")),
        };
        SensorClassifier.Resolve(groups, new Dictionary<string, int>());
        var r = Roles(groups);
        Assert.Equal(["AMD Ryzen 9 9950X/Core (Tctl/Tdie)"], r[BlackBox.Sensors.Roles.CpuTctl]);
        Assert.Equal(2, r[BlackBox.Sensors.Roles.CpuCcd].Count);
        Assert.Equal(["AMD Radeon RX 7900 XTX/GPU Package"], r[BlackBox.Sensors.Roles.GpuPower]);
        Assert.Equal(["AMD Radeon RX 7900 XTX/GPU Core"], r[BlackBox.Sensors.Roles.GpuEdge]);
        Assert.Equal(["AMD Radeon RX 7900 XTX/GPU Memory"], r[BlackBox.Sensors.Roles.GpuMem]);
        Assert.Contains("AMD Ryzen 9 9950X/SoC (SVI3 TFN)", r[BlackBox.Sensors.Roles.Vsoc]);
    }

    [Fact]
    public void Unknown_board_still_records_fans_without_guessing_roles()
    {
        var groups = new List<HwGroup> { G("/lpc/nct6798d/0", "Nuvoton NCT6798D", "SuperIO", ("Temperature", "Temperature #1"), ("Fan", "Fan #1"), ("Voltage", "Voltage #1")) };
        SensorClassifier.Resolve(groups, new Dictionary<string, int>());
        var fan = groups[0].Sensors.Single(s => s.Type == "Fan");
        Assert.Equal((1, BlackBox.Sensors.Roles.Fan), (fan.Tier, fan.Role));
        Assert.Null(groups[0].Sensors.Single(s => s.Type == "Temperature").Role);
    }

    [Theory]
    [InlineData("Intel Core i7-13700K", "cpu.tctl", 95.0, 100.0)]       // 12th-14th gen beats the older-Intel family
    [InlineData("Intel Core Ultra 7 265K", "cpu.tctl", 100.0, 105.0)]
    [InlineData("Intel Core i7-9700K", "cpu.tctl", 90.0, 100.0)]
    [InlineData("AMD Ryzen 9 9950X", "cpu.tctl", 93.0, 96.0)]
    [InlineData("AMD Ryzen 7 5800X3D", "cpu.tctl", 85.0, 89.0)]           // X3D beats the Ryzen 5000 family
    [InlineData("AMD Ryzen 7 7800X3D", "cpu.tctl", 85.0, 89.0)]           // model profile
    [InlineData("AMD Ryzen 9 9950X3D", "cpu.tctl", 90.0, 95.0)]
    [InlineData("NVIDIA GeForce RTX 4070", "gpu.edge", 83.0, 90.0)]
    [InlineData("AMD Radeon RX 7900 XTX", "gpu.hotspot", 100.0, 110.0)]
    [InlineData("Some Future CPU 9000", "cpu.tctl", 85.0, 95.0)]          // generic fallback
    public void Profiles_pick_family_limits(string hwName, string role, double warn, double crit)
    {
        var kind = role.StartsWith("cpu") ? "Cpu" : hwName.Contains("NVIDIA") ? "GpuNvidia" : "GpuAmd";
        var sensorName = role switch { "cpu.tctl" => hwName.StartsWith("Intel") ? "CPU Package" : "Core (Tctl/Tdie)", "gpu.edge" => "GPU Core", _ => "GPU Hot Spot" };
        var groups = new List<HwGroup> { G("/hw/0", hwName, kind, ("Temperature", sensorName)) };
        SensorClassifier.Resolve(groups, new Dictionary<string, int>());
        var cfg = new Config { DataDir = Path.GetTempPath() };
        var le = new LimitEvaluator(cfg, new Writer(cfg, NullLogger<Writer>.Instance), NullLogger<LimitEvaluator>.Instance);
        le.Bind(groups);
        var b = Assert.Single(le.Bound, x => x.Sensor.Role == role);
        Assert.Equal((warn, crit), (b.Def.Warn!.Value, b.Def.Crit!.Value));
    }
}
