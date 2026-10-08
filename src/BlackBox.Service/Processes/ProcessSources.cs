using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace BlackBox.Processes;

public struct ProcSnap
{
    public int Pid;
    public long CreateTime;   // 100 ns FILETIME, identifies PID reuse
    public long CpuTime;      // kernel + user, 100 ns
    public long WorkingSet;   // bytes
    public long IoBytes;      // read + write transfer bytes (cumulative)
    public int Offset;        // source-private (entry offset for lazy name decode)
}

public interface IProcessSource : IDisposable
{
    /// <summary>Fill <paramref name="buf"/> with all processes; grows the array only when needed. Returns count.</summary>
    int Snapshot(ref ProcSnap[] buf);
    /// <summary>Image name of entry <paramref name="index"/> of the last snapshot (allocates; call only for new PIDs).</summary>
    string GetName(in ProcSnap snap);
    string? GetPath(int pid);
    /// <summary>Per-PID GPU utilisation % (3D + compute engines). Reuses the dictionary.</summary>
    void CollectGpu(Dictionary<int, double> gpuByPid);
}

/// <summary>One NtQuerySystemInformation(SystemProcessInformation) syscall per sample; no per-process handles.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class NtProcessSource : IProcessSource
{
    const int SystemProcessInformation = 5;
    const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    byte* _buf; int _len = 512 * 1024;
    readonly PdhGpu _gpu = new();

    [DllImport("ntdll.dll")] static extern uint NtQuerySystemInformation(int cls, void* buf, int len, out int retLen);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, char* buf, ref int size);

    public NtProcessSource() => _buf = (byte*)NativeMemory.Alloc((nuint)_len);

    public int Snapshot(ref ProcSnap[] arr)
    {
        uint st;
        while ((st = NtQuerySystemInformation(SystemProcessInformation, _buf, _len, out int need)) == STATUS_INFO_LENGTH_MISMATCH)
        {
            NativeMemory.Free(_buf);
            _len = Math.Max(_len * 2, need + 64 * 1024);
            _buf = (byte*)NativeMemory.Alloc((nuint)_len);
        }
        if (st != 0) return 0;

        // SYSTEM_PROCESS_INFORMATION (x64) offsets
        int n = 0, off = 0;
        while (true)
        {
            byte* p = _buf + off;
            if (n == arr.Length) Array.Resize(ref arr, arr.Length * 2);
            ref var s = ref arr[n++];
            s.Offset = off;
            s.CreateTime = *(long*)(p + 0x20);
            s.CpuTime = *(long*)(p + 0x28) + *(long*)(p + 0x30);       // UserTime + KernelTime
            s.Pid = (int)*(nint*)(p + 0x50);                           // UniqueProcessId
            s.WorkingSet = (long)*(nuint*)(p + 0x90);                  // WorkingSetSize
            s.IoBytes = *(long*)(p + 0xE8) + *(long*)(p + 0xF0);       // ReadTransferCount + WriteTransferCount
            uint next = *(uint*)p;
            if (next == 0) break;
            off += (int)next;
        }
        return n;
    }

    public string GetName(in ProcSnap s)
    {
        byte* p = _buf + s.Offset;
        ushort len = *(ushort*)(p + 0x38);
        char* str = *(char**)(p + 0x40);
        if (s.Pid == 0) return "Idle";
        return str == null || len == 0 ? (s.Pid == 4 ? "System" : $"pid {s.Pid}") : new string(str, 0, len / 2);
    }

    public string? GetPath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            char* buf = stackalloc char[1024];
            int size = 1024;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    public void CollectGpu(Dictionary<int, double> gpuByPid) => _gpu.Collect(gpuByPid);

    public void Dispose()
    {
        if (_buf != null) { NativeMemory.Free(_buf); _buf = null; }
        _gpu.Dispose();
    }
}

/// <summary>PDH wildcard query on \GPU Engine(*)\Utilization Percentage; one query handle reused for the process lifetime.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class PdhGpu : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? src, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint fmt, ref uint bufSize, out uint count, byte* items);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr q);

    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;
    IntPtr _q, _c;
    byte* _buf; uint _len = 256 * 1024;
    public bool Ok { get; }

    public PdhGpu()
    {
        Ok = PdhOpenQueryW(null, IntPtr.Zero, out _q) == 0
             && PdhAddEnglishCounterW(_q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _c) == 0;
        _buf = (byte*)NativeMemory.Alloc(_len);
        if (Ok) PdhCollectQueryData(_q); // rate counter needs a baseline
    }

    public void Collect(Dictionary<int, double> byPid)
    {
        byPid.Clear();
        if (!Ok || PdhCollectQueryData(_q) != 0) return;
        uint size = _len, count;
        uint st = PdhGetFormattedCounterArrayW(_c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, _buf);
        if (st == PDH_MORE_DATA)
        {
            NativeMemory.Free(_buf);
            _len = size + 64 * 1024;
            _buf = (byte*)NativeMemory.Alloc(_len);
            size = _len;
            st = PdhGetFormattedCounterArrayW(_c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, _buf);
        }
        if (st != 0) return;
        // PDH_FMT_COUNTERVALUE_ITEM_W: { wchar* szName; { DWORD CStatus; double value } } = 24 bytes on x64
        for (uint i = 0; i < count; i++)
        {
            byte* item = _buf + i * 24;
            uint cstatus = *(uint*)(item + 8);
            if (cstatus > 1) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA only
            double v = *(double*)(item + 16);
            if (!(v > 0) || !double.IsFinite(v)) continue; // also drops NaN
            var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(*(char**)item);
            // "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D"
            int e = name.IndexOf("engtype_");
            if (e < 0) continue;
            var eng = name[(e + 8)..];
            if (!eng.StartsWith("3D") && !eng.StartsWith("Compute")) continue;
            if (!name.StartsWith("pid_")) continue;
            int pid = 0;
            for (int k = 4; k < name.Length && char.IsAsciiDigit(name[k]); k++) pid = pid * 10 + (name[k] - '0');
            ref double slot = ref CollectionsMarshal.GetValueRefOrAddDefault(byPid, pid, out _);
            slot = Math.Min(100, slot + v);
        }
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) { PdhCloseQuery(_q); _q = IntPtr.Zero; }
        if (_buf != null) { NativeMemory.Free(_buf); _buf = null; }
    }
}

/// <summary>Synthetic processes driven by the simulated sensor load.</summary>
public sealed class SimProcessSource : IProcessSource
{
    static readonly string[] Names = ["System", "Diablo IV.exe", "Battle.net.exe", "explorer.exe", "msedge.exe", "msedge.exe",
        "Discord.exe", "dwm.exe", "svchost.exe", "svchost.exe", "svchost.exe", "MsMpEng.exe", "audiodg.exe", "Spotify.exe", "OneDrive.exe"];
    readonly long[] _cpu = new long[Names.Length], _io = new long[Names.Length];
    readonly Random _rnd = new(7);
    readonly long _created = DateTime.UtcNow.ToFileTimeUtc();

    bool GameRunning => Sensors.SimSensorSource.GameLoad > 0.5;

    public int Snapshot(ref ProcSnap[] buf)
    {
        int n = 0;
        double cores = Environment.ProcessorCount, dt100ns = 2 * 1e7;
        for (int i = 0; i < Names.Length; i++)
        {
            if (i == 1 && !GameRunning) continue;
            double share = i switch
            {
                1 => Sensors.SimSensorSource.CpuLoad * 0.8,
                4 or 5 => 1.5 + _rnd.NextDouble() * 3,
                7 => 1 + (GameRunning ? 2 : 0),
                11 => _rnd.NextDouble() < 0.05 ? 12 : 0.2,
                _ => _rnd.NextDouble() * 0.8,
            } / 100;
            _cpu[i] += (long)(share * cores * dt100ns);
            _io[i] += (long)(_rnd.NextDouble() * (i == 1 ? 40e6 : 1e6));
            if (n == buf.Length) Array.Resize(ref buf, buf.Length * 2);
            buf[n++] = new ProcSnap { Pid = 1000 + i * 4, CreateTime = _created, CpuTime = _cpu[i], WorkingSet = (i == 1 ? 6_000L : 150L + i * 30) << 20, IoBytes = _io[i], Offset = i };
        }
        return n;
    }

    public string GetName(in ProcSnap s) => Names[s.Offset];
    public string? GetPath(int pid) => pid == 1004 ? @"C:\Program Files (x86)\Diablo IV\Diablo IV.exe" : null;

    public void CollectGpu(Dictionary<int, double> byPid)
    {
        byPid.Clear();
        if (GameRunning) byPid[1004] = Sensors.SimSensorSource.GpuLoad * 0.97;
        byPid[1028] = 1.5;                         // dwm
        byPid[1016] = 0.5 + _rnd.NextDouble();     // msedge
    }

    public void Dispose() { }
}
