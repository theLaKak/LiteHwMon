using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace LiteHwMon.Core;

public sealed class GpuAdapterFallback
{
    public string Key = "";
    public float UtilPct = float.NaN;
    public float DedicatedGB = float.NaN;
}

public sealed class DiskCounterFallback
{
    public float ReadBps = float.NaN;
    public float WriteBps = float.NaN;
    public float ActivityPct = float.NaN;
}

/// <summary>PDH 通配符计数器批量查询：一条路径读取全部实例，无每实例对象、自动跟随实例增减。</summary>
internal sealed class PdhWildcardQuery : IDisposable
{
    private const uint FmtDouble = 0x00000200;
    private const int MoreData = unchecked((int)0x800007D2);
    private const int ItemSize = 24; // x64: 指针8 + CStatus4 + 填充4 + double8

    private readonly IntPtr _query;
    private readonly IntPtr[] _counters;
    private readonly List<Dictionary<string, double>> _results = new();
    private IntPtr _buffer;
    private int _bufferSize;

    private PdhWildcardQuery(IntPtr query, IntPtr[] counters) { _query = query; _counters = counters; }

    public static PdhWildcardQuery? TryCreate(params string[] counterPaths)
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out IntPtr q) != 0) return null;
            var counters = new IntPtr[counterPaths.Length];
            for (int i = 0; i < counterPaths.Length; i++)
                if (PdhAddEnglishCounter(q, counterPaths[i], IntPtr.Zero, out counters[i]) != 0)
                {
                    PdhCloseQuery(q);
                    return null;
                }
            return new PdhWildcardQuery(q, counters);
        }
        catch { return null; }
    }

    /// <summary>采集一轮，返回每个计数器的 “实例名 → 值” 字典。字典跨轮复用，避免每秒产生垃圾。</summary>
    public IReadOnlyList<Dictionary<string, double>> Collect()
    {
        if (PdhCollectQueryData(_query) != 0) return _results;
        while (_results.Count < _counters.Length)
            _results.Add(new Dictionary<string, double>(32, StringComparer.OrdinalIgnoreCase));

        for (int ci = 0; ci < _counters.Length; ci++)
        {
            var map = _results[ci];
            map.Clear();
            int size = 0, count = 0;
            int err = PdhGetFormattedCounterArrayW(_counters[ci], FmtDouble, ref size, ref count, IntPtr.Zero);
            if (err != MoreData || size <= 0) continue;
            if (size > _bufferSize)
            {
                if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                _buffer = Marshal.AllocHGlobal(size);
                _bufferSize = size;
            }
            count = 0;
            if (PdhGetFormattedCounterArrayW(_counters[ci], FmtDouble, ref _bufferSize, ref count, _buffer) != 0) continue;
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(_buffer + i * ItemSize);
                if (item.szName == null) continue;
                // CStatus 0=初始有效 1=有效；其余（无效/计算错误）跳过
                if (item.FmtValue.CStatus > 1) continue;
                map[item.szName] = item.FmtValue.Value;
            }
        }
        return _results;
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string szName;
        public PdhFmtCounterValue FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref int bufferSize, ref int itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}

/// <summary>
/// 无需内核驱动的系统级数据源：CPU 占用/频率估算（PDH）、内存（GlobalMemoryStatusEx + WMI）、
/// GPU 引擎占用与专用显存、磁盘吞吐（均为 PDH 通配符批量查询）。
/// 仅当 LibreHardwareMonitor 未提供对应传感器时作为回退使用。
/// </summary>
public sealed class SystemFallbacks : IDisposable
{
    private PerformanceCounter? _cpu;
    private PerformanceCounter? _cpuPerf;
    private uint _cpuMaxClockMhz;
    private PdhWildcardQuery? _gpuQuery;
    private PdhWildcardQuery? _diskQuery;
    private readonly Dictionary<string, GpuAdapterFallback> _gpuByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DiskCounterFallback> _disksByIndex = new();
    private bool _needGpu;
    private bool _needDisk;
    private bool _needCpuFreq;

    public float CpuLoadPct = float.NaN;
    /// <summary>估算的有效频率（% Processor Performance × 标称频率），无驱动时的回退。</summary>
    public float CpuFreqMhz = float.NaN;
    public float MemTotalGB = float.NaN;
    public float MemUsedGB = float.NaN;
    public float MemAvailGB = float.NaN;
    public float MemLoadPct = float.NaN;
    /// <summary>如 “16 GB DDR5 4800 MHz · 2 模块”。</summary>
    public string MemoryDetailText = "";
    public string MemoryFrequencyText = "";
    /// <summary>物理磁盘号 → 容量文本（如 “931.5 GB”）。</summary>
    public Dictionary<int, string> DiskSizeTexts = new();

    public IReadOnlyList<GpuAdapterFallback> GpuAdapters => _orderedAdapters;
    public IReadOnlyDictionary<int, DiskCounterFallback> Disks => _disksByIndex;

    private readonly List<GpuAdapterFallback> _orderedAdapters = new();

    public void SetRequirements(bool needGpu, bool needDisk, bool needCpuFreq = false)
    {
        _needGpu = needGpu;
        _needDisk = needDisk;
        _needCpuFreq = needCpuFreq;
    }

    public void Initialize()
    {
        UpdateMemory();
        QueryRamModules();
        QueryDiskSizes();
    }

    /// <summary>在规格构建确定需求后创建性能计数器资源。</summary>
    public void StartCounters()
    {
        try
        {
            _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _cpu.NextValue(); // 预热：首次读数为 0
        }
        catch { /* 计数器不可用时 CPU 占用保持不可用 */ }

        if (_needCpuFreq) InitCpuFreq();
        if (_needGpu)
            _gpuQuery = PdhWildcardQuery.TryCreate(
                @"\GPU Engine(*)\Utilization Percentage",
                @"\GPU Adapter Memory(*)\Dedicated Usage");
        if (_needDisk)
            _diskQuery = PdhWildcardQuery.TryCreate(
                @"\PhysicalDisk(*)\Read Bytes/sec",
                @"\PhysicalDisk(*)\Write Bytes/sec",
                @"\PhysicalDisk(*)\% Idle Time");
    }

    private void InitCpuFreq()
    {
        try
        {
            _cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);
            _cpuPerf.NextValue();
        }
        catch
        {
            try
            {
                // 部分系统实例名为 “0,_Total”
                _cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "0,_Total", readOnly: true);
                _cpuPerf.NextValue();
            }
            catch { _cpuPerf = null; }
        }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (var o in s.Get())
            {
                using var mo = (ManagementObject)o;
                _cpuMaxClockMhz = CastUInt(mo["MaxClockSpeed"]);
                break;
            }
        }
        catch { }
    }

    public void Update()
    {
        if (_cpu != null)
        {
            try
            {
                float v = _cpu.NextValue();
                if (v >= 0) CpuLoadPct = Math.Clamp(v, 0f, 100f);
            }
            catch { }
        }
        if (_cpuPerf != null && _cpuMaxClockMhz > 0)
        {
            try
            {
                float pct = _cpuPerf.NextValue();
                if (pct > 0) CpuFreqMhz = _cpuMaxClockMhz * Math.Clamp(pct, 1f, 400f) / 100f;
            }
            catch { }
        }
        UpdateMemory();
        UpdateGpu();
        UpdateDisks();
    }

    private void UpdateGpu()
    {
        if (_gpuQuery == null) return;
        foreach (var g in _gpuByKey.Values) { g.UtilPct = float.NaN; g.DedicatedGB = float.NaN; }
        var maps = _gpuQuery.Collect();
        if (maps.Count >= 1)
        {
            foreach (var (inst, v) in maps[0])
            {
                float fv = (float)v;
                if (float.IsNaN(fv) || ExtractLuidKey(inst) is not { } key) continue;
                if (!_gpuByKey.TryGetValue(key, out var g)) _gpuByKey[key] = g = new GpuAdapterFallback { Key = key };
                g.UtilPct = float.IsNaN(g.UtilPct) ? fv : g.UtilPct + fv; // 多进程 3D 引擎求和
            }
        }
        if (maps.Count >= 2)
        {
            foreach (var (inst, v) in maps[1])
            {
                float fv = (float)v;
                if (float.IsNaN(fv) || ExtractLuidKey(inst) is not { } key) continue;
                if (!_gpuByKey.TryGetValue(key, out var g)) _gpuByKey[key] = g = new GpuAdapterFallback { Key = key };
                g.DedicatedGB = fv / (1024f * 1024 * 1024);
            }
        }
        foreach (var g in _gpuByKey.Values)
            g.UtilPct = float.IsNaN(g.UtilPct) ? g.UtilPct : Math.Clamp(g.UtilPct, 0f, 100f);
        ReorderAdapters();
    }

    private void UpdateDisks()
    {
        if (_diskQuery == null) return;
        var maps = _diskQuery.Collect();
        if (maps.Count < 3) return;
        foreach (var d in _disksByIndex.Values) { d.ReadBps = float.NaN; d.WriteBps = float.NaN; d.ActivityPct = float.NaN; }
        FillDisk(maps[0], v => (float)v, (d, v) => d.ReadBps = v);
        FillDisk(maps[1], v => (float)v, (d, v) => d.WriteBps = v);
        FillDisk(maps[2], v => (float)v, (d, v) => d.ActivityPct = Math.Clamp(100f - v, 0f, 100f));
    }

    private void FillDisk(Dictionary<string, double> map, Func<double, float> conv, Action<DiskCounterFallback, float> assign)
    {
        foreach (var (inst, raw) in map)
        {
            float v = conv(raw);
            if (float.IsNaN(v) || inst == "_Total") continue;
            int idx = DiskIndexOf(inst);
            if (idx < 0) continue;
            if (!_disksByIndex.TryGetValue(idx, out var d)) _disksByIndex[idx] = d = new DiskCounterFallback();
            assign(d, v);
        }
    }

    private void ReorderAdapters()
    {
        _orderedAdapters.Clear();
        _orderedAdapters.AddRange(_gpuByKey.Values);
        // 专用显存大的排前面（通常是独立显卡），供引擎把回退源对号入座
        _orderedAdapters.Sort((a, b) =>
        {
            float av = float.IsNaN(a.DedicatedGB) ? -1 : a.DedicatedGB;
            float bv = float.IsNaN(b.DedicatedGB) ? -1 : b.DedicatedGB;
            return bv.CompareTo(av);
        });
    }

    private void UpdateMemory()
    {
        var st = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(st))
        {
            const double gb = 1024.0 * 1024 * 1024;
            MemTotalGB = (float)(st.ullTotalPhys / gb);
            MemAvailGB = (float)(st.ullAvailPhys / gb);
            MemUsedGB = MemTotalGB - MemAvailGB;
            MemLoadPct = Math.Clamp((float)st.dwMemoryLoad, 0f, 100f);
        }
    }

    private void QueryRamModules()
    {
        try
        {
            double totalGB = 0;
            uint speed = 0;
            int n = 0;
            string ddr = "";
            using var s = new ManagementObjectSearcher(
                "SELECT Capacity, Speed, ConfiguredClockSpeed, SmbiosMemoryType, Manufacturer FROM Win32_PhysicalMemory");
            foreach (var o in s.Get())
            {
                using var mo = (ManagementObject)o;
                totalGB += Convert.ToDouble(mo["Capacity"] ?? 0UL) / (1024.0 * 1024 * 1024);
                uint cs = CastUInt(mo["ConfiguredClockSpeed"]);
                uint sp = CastUInt(mo["Speed"]);
                if (cs > speed) speed = cs;
                if (speed == 0 && sp > speed) speed = sp;
                ddr = ((ushort)CastUInt(mo["SmbiosMemoryType"])) switch
                {
                    34 => "DDR5",
                    26 => "DDR4",
                    24 => "DDR3",
                    _ => ddr,
                };
                n++;
            }
            MemoryFrequencyText = speed > 0 ? $"{speed} MHz" : "";
            MemoryDetailText = totalGB > 0
                ? $"{totalGB:0.#} GB" + (ddr.Length > 0 ? $" {ddr}" : "") + (speed > 0 ? $" {speed} MHz" : "") + $" · {n} 模块"
                : "";
        }
        catch { /* WMI 不可用时内存细节留空 */ }
    }

    private void QueryDiskSizes()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Index, Size FROM Win32_DiskDrive");
            foreach (var o in s.Get())
            {
                using var mo = (ManagementObject)o;
                int idx = (int)(uint)mo["Index"];
                double bytes = Convert.ToDouble(mo["Size"] ?? 0.0);
                if (bytes > 0)
                {
                    double gb = bytes / (1024.0 * 1024 * 1024);
                    DiskSizeTexts[idx] = gb >= 1000 ? $"{gb / 1024:0.00} TB" : $"{gb:0} GB";
                }
            }
        }
        catch { }
    }

    private static uint CastUInt(object? v) => v switch
    {
        null => 0,
        uint u => u,
        _ => uint.TryParse(v.ToString(), out var r) ? r : 0,
    };

    /// <summary>实例名形如 “0 C:”，取前导物理磁盘号。</summary>
    private static int DiskIndexOf(string instance)
    {
        int sp = instance.IndexOf(' ');
        if (sp <= 0) return -1;
        return int.TryParse(instance.AsSpan(0, sp), out int idx) ? idx : -1;
    }

    /// <summary>GPU 实例名形如 “pid_1234_luid_0x…_phys_0_eng_0_engtype_3D”，提取 “luid_…_phys_0” 作为适配器键。</summary>
    private static string? ExtractLuidKey(string instance)
    {
        int i = instance.IndexOf("luid_", StringComparison.Ordinal);
        if (i < 0) return null;
        int j = instance.IndexOf("_eng_", i, StringComparison.Ordinal);
        return j < 0 ? instance[i..] : instance[i..j];
    }

    public void Dispose()
    {
        _cpu?.Dispose();
        _cpuPerf?.Dispose();
        _gpuQuery?.Dispose();
        _diskQuery?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);
}
