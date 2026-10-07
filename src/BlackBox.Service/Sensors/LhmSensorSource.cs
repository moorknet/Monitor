using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;

namespace BlackBox.Sensors;

/// <summary>LibreHardwareMonitorLib backend (PawnIO driver). Only the groups the spec needs are enabled.</summary>
[SupportedOSPlatform("windows")]
public sealed class LhmSensorSource : ISensorSource
{
    readonly ILogger _log;
    Computer? _computer;
    volatile bool _changed;

    public LhmSensorSource(ILogger log) => _log = log;
    public bool Changed => _changed;

    public string DriverInfo
    {
        get
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            var ver = k?.GetValue("DisplayVersion") as string;
            return k == null ? "PawnIO NOT installed (CPU/Super I/O sensors will be missing)" : $"PawnIO {ver}";
        }
    }

    void Open()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsMemoryEnabled = true, IsStorageEnabled = true,
            IsNetworkEnabled = false, IsControllerEnabled = false, IsPsuEnabled = false, IsBatteryEnabled = false, IsPowerMonitorEnabled = false,
        };
        _computer.HardwareAdded += _ => _changed = true;
        _computer.HardwareRemoved += _ => _changed = true;
        _computer.Open();
    }

    public List<HwGroup> Enumerate()
    {
        if (_computer == null) Open();
        _changed = false;
        var groups = new List<HwGroup>();
        foreach (var h in _computer!.Hardware) Add(h, groups);
        return groups;
    }

    void Add(IHardware h, List<HwGroup> groups)
    {
        // LHM activates many sensors only after the first Update(), so update before reading the sensor list.
        try { h.Update(); } catch (Exception ex) { _log.LogWarning(ex, "Initial update of {hw} failed", h.Name); }
        h.SensorAdded -= OnSensorChange; h.SensorAdded += OnSensorChange;
        h.SensorRemoved -= OnSensorChange; h.SensorRemoved += OnSensorChange;

        var g = new HwGroup { Identifier = h.Identifier.ToString(), Name = h.Name, Kind = h.HardwareType.ToString(), Update = h.Update };
        foreach (var s in h.Sensors)
        {
            string type = s.SensorType.ToString();
            var (unit, q) = SensorClassifier.UnitFor(type);
            var (tier, role) = SensorClassifier.Classify(g.Kind, h.Name, type, s.Name);
            var sensor = s; // closure captured once at enumeration, not per tick
            g.Sensors.Add(new SensorDesc
            {
                Identifier = s.Identifier.ToString(), Name = s.Name, Type = type, Unit = unit, Quantum = q,
                Group = g, Read = () => sensor.Value, Tier = tier, Role = role,
            });
        }
        groups.Add(g);
        foreach (var sub in h.SubHardware) Add(sub, groups);
    }

    void OnSensorChange(ISensor _) => _changed = true;

    public void Reset()
    {
        try { _computer?.Close(); } catch (Exception ex) { _log.LogWarning(ex, "LHM close failed"); }
        _computer = null;
        _changed = true;
    }

    public void Dispose() { try { _computer?.Close(); } catch { } }
}
