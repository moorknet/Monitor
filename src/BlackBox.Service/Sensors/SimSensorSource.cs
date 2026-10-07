namespace BlackBox.Sensors;

/// <summary>
/// Synthetic hardware that mimics the target machine (7800X3D / RX 9070 XT / X670E Hero). Used with --simulate for
/// development and for exercising the DB/limits/viewer without the driver. Load follows a "gaming session" pattern.
/// </summary>
public sealed class SimSensorSource : ISensorSource
{
    readonly Random _rnd = new(42);
    // shared scenario state, recomputed by the CPU group's Update
    public static double GameLoad, CpuLoad, GpuLoad, CpuPower, GpuPower;
    public static long? FakeNowMs;   // set by the history generator
    readonly float[] _coreClock = new float[8], _coreLoad = new float[8];
    double _tctl = 45, _hot = 40, _edge = 35, _mem = 50, _vrm = 40, _nvme = 40;

    public bool Changed => false;
    public string DriverInfo => "simulated";
    public void Reset() { }
    public void Dispose() { }

    void Step()
    {
        double t = (FakeNowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000.0;
        double phase = t % 600;                                   // 7 min gaming, 3 min idle
        GameLoad = phase < 420 ? 0.85 + 0.15 * Math.Sin(t / 7) : 0.03;
        CpuLoad = Math.Clamp(GameLoad * 45 + 3 + _rnd.NextDouble() * 6, 0, 100);
        GpuLoad = Math.Clamp(GameLoad * 98 + _rnd.NextDouble() * 2, 0, 100);
        CpuPower = 28 + CpuLoad * 0.9 + _rnd.NextDouble() * 3;
        GpuPower = 15 + GpuLoad * 2.95 + (phase is > 300 and < 320 ? 40 : 0) + _rnd.NextDouble() * 5; // transient spike
        for (int i = 0; i < 8; i++)
        {
            _coreLoad[i] = (float)Math.Clamp(CpuLoad * (0.5 + i % 3 * 0.4) + _rnd.NextDouble() * 10, 0, 100);
            _coreClock[i] = (float)(GameLoad > 0.5 ? 4900 + _rnd.Next(-150, 150) : 3000 + _rnd.Next(-800, 1500));
        }
        static double Toward(double cur, double target, double k) => cur + (target - cur) * k;
        _tctl = Toward(_tctl, 42 + CpuPower * 0.45, 0.08);
        _hot = Toward(_hot, 38 + GpuPower * 0.21, 0.05);
        _edge = Toward(_edge, 33 + GpuPower * 0.14, 0.05);
        _mem = Toward(_mem, 48 + GpuLoad * 0.36, 0.03);
        _vrm = Toward(_vrm, 38 + CpuPower * 0.3, 0.02);
        _nvme = Toward(_nvme, 38 + GameLoad * 8, 0.01);
    }

    float N(double v, double noise = 0.3) => (float)(v + (_rnd.NextDouble() - 0.5) * noise);

    public List<HwGroup> Enumerate()
    {
        Step();
        var groups = new List<HwGroup>();
        HwGroup G(string id, string name, string kind, Action? upd = null)
        {
            var g = new HwGroup { Identifier = id, Name = name, Kind = kind, Update = upd ?? (() => { }) };
            groups.Add(g);
            return g;
        }
        void S(HwGroup g, string type, string name, int idx, Func<float?> read)
        {
            var (unit, q) = SensorClassifier.UnitFor(type);
            var (tier, role) = SensorClassifier.Classify(g.Kind, g.Name, type, name);
            g.Sensors.Add(new SensorDesc { Identifier = $"{g.Identifier}/{type.ToLowerInvariant()}/{idx}", Name = name, Type = type,
                Unit = unit, Quantum = q, Group = g, Read = read, Tier = tier, Role = role });
        }

        var cpu = G("/amdcpu/0", "AMD Ryzen 7 7800X3D", "Cpu", Step);
        S(cpu, "Temperature", "Core (Tctl/Tdie)", 2, () => N(_tctl));
        S(cpu, "Temperature", "CCD1 (Tdie)", 3, () => N(_tctl - 2));
        S(cpu, "Power", "Package", 0, () => N(CpuPower, 1));
        S(cpu, "Load", "CPU Total", 0, () => N(CpuLoad, 1));
        S(cpu, "Voltage", "Core (SVI3 TFN)", 0, () => N(GameLoad > 0.5 ? 1.15 : 0.95, 0.02));
        for (int i = 0; i < 8; i++)
        {
            int c = i;
            S(cpu, "Clock", $"Core #{i + 1}", i + 1, () => _coreClock[c]);
            S(cpu, "Load", $"CPU Core #{i + 1}", i + 1, () => _coreLoad[c]);
        }

        var gpu = G("/gpu-amd/0", "AMD Radeon RX 9070 XT", "GpuAmd");
        S(gpu, "Temperature", "GPU Core", 0, () => N(_edge));
        S(gpu, "Temperature", "GPU Hot Spot", 1, () => N(_hot));
        S(gpu, "Temperature", "GPU Memory", 2, () => N(_mem));
        S(gpu, "Power", "GPU Package", 0, () => N(GpuPower, 2));
        S(gpu, "Clock", "GPU Core", 0, () => N(GpuLoad > 50 ? 2970 : 500, 30));
        S(gpu, "Clock", "GPU Memory", 1, () => N(GpuLoad > 50 ? 2518 : 96, 1));
        S(gpu, "Load", "GPU Core", 0, () => N(GpuLoad, 1));
        S(gpu, "Fan", "GPU Fan", 0, () => N(GpuLoad > 50 ? 1600 : 0, 20));

        G("/motherboard", "ASUS ROG CROSSHAIR X670E HERO", "Motherboard");
        var sio = G("/lpc/nct6799d/0", "Nuvoton NCT6799D", "SuperIO");
        S(sio, "Temperature", "VRM", 0, () => N(_vrm));
        S(sio, "Temperature", "Chipset", 1, () => N(52));
        S(sio, "Temperature", "Motherboard", 2, () => N(34));
        S(sio, "Fan", "CPU Fan", 0, () => N(600 + _tctl * 12, 15));
        S(sio, "Fan", "Chassis Fan #1", 1, () => N(800, 10));
        S(sio, "Fan", "Chassis Fan #2", 2, () => N(800, 10));
        S(sio, "Voltage", "+12V", 0, () => N(12.1 - GpuPower * 0.0006, 0.04));
        S(sio, "Voltage", "Vcore", 1, () => N(GameLoad > 0.5 ? 1.16 : 0.96, 0.02));
        S(sio, "Voltage", "VSOC", 2, () => N(1.25, 0.005));
        S(sio, "Voltage", "+5V", 3, () => N(5.04, 0.02));
        S(sio, "Voltage", "+3.3V", 4, () => N(3.34, 0.01));
        S(sio, "Control", "CPU Fan", 0, () => N(30 + _tctl * 0.5, 1));

        var nvme = G("/nvme/0", "Samsung SSD 990 PRO 2TB", "Storage");
        S(nvme, "Temperature", "Composite Temperature", 0, () => N(_nvme));
        S(nvme, "Temperature", "Temperature #2", 1, () => N(_nvme + 6));
        S(nvme, "Load", "Used Space", 0, () => 61.2f);
        S(nvme, "Data", "Data Read", 0, () => 51234f);

        var mem = G("/ram", "Generic Memory", "Memory");
        S(mem, "Temperature", "DIMM #1", 0, () => N(44 + GameLoad * 4));
        S(mem, "Temperature", "DIMM #2", 1, () => N(45 + GameLoad * 4));
        S(mem, "Load", "Memory", 0, () => N(38 + GameLoad * 20, 0.5));
        return groups;
    }
}
