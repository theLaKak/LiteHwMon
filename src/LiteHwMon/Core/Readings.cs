using LibreHardwareMonitor.Hardware;

namespace LiteHwMon.Core;

public enum DeviceKind { Cpu, Gpu, Memory, Disk, Fan }

public enum MetricKind { Temperature, Load, Clock, Power, Fan, DataRate, Data }

public enum FallbackKind
{
    None, CpuLoad, CpuMaxClock, CpuFreqEstimate,
    CpuRapPower,
    MemUsed, MemAvailable, MemLoad,
    GpuUtil, GpuMemUsed, GpuMemPct,
    NvmlTemp, NvmlLoad, NvmlClock, NvmlPower, NvmlMemUsed, NvmlMemPct,
    DiskRead, DiskWrite, DiskActivity
}

/// <summary>单个监控项：来自 LibreHardwareMonitor 传感器，或无需驱动的系统计数器回退。</summary>
public sealed class ReadingSpec
{
    /// <summary>0.5s 采样间隔下容纳 30 分钟历史。</summary>
    public const int HistoryCapacity = 3660;

    public required string Id { get; init; }
    /// <summary>卡片磁贴上的短标签，如“温度”。</summary>
    public required string Name { get; init; }
    /// <summary>曲线图例里的名称，如“RTX 4060 温度”。</summary>
    public required string LegendName { get; init; }
    public required DeviceKind Device { get; init; }
    public required MetricKind Metric { get; init; }
    public ISensor? Sensor { get; init; }
    /// <summary>取最大值 / 参与计算用的附加传感器。</summary>
    public List<ISensor>? ExtraSensors { get; init; }
    public FallbackKind Fallback { get; init; }
    /// <summary>回退源实例序号（GPU 序号 / 物理磁盘号）。</summary>
    public int FallbackIndex { get; init; }
    /// <summary>所属硬件序号（GPU 卡片分组用，与 NVML 设备号解耦）。</summary>
    public int FallbackIndex2 { get; init; }
    public SensorHistory History { get; } = new(HistoryCapacity);
    public float? Value { get; internal set; }
    /// <summary>静态项（如内存频率），Open 时一次性赋值。</summary>
    public bool IsStatic { get; init; }
    /// <summary>曲线图例中隐藏（如未连接的风扇）。</summary>
    public bool Hidden { get; internal set; }

    public (string Num, string Unit) FormatParts(float v) => Metric switch
    {
        MetricKind.Temperature => (v.ToString("0"), "°C"),
        MetricKind.Load => (v.ToString("0"), "%"),
        MetricKind.Clock => v >= 10000 ? ((v / 1000).ToString("0.00"), "GHz") : (v.ToString("0"), "MHz"),
        MetricKind.Power => v >= 100 ? (v.ToString("0"), "W") : (v.ToString("0.0"), "W"),
        MetricKind.Fan => (v.ToString("0"), "RPM"),
        MetricKind.DataRate => v >= 1_073_741_824 ? ((v / 1_073_741_824).ToString("0.00"), "GB/s")
                             : v >= 1_048_576 ? ((v / 1_048_576).ToString("0.0"), "MB/s")
                             : ((v / 1024).ToString("0"), "KB/s"),
        MetricKind.Data => (v.ToString("0.00"), "GB"),
        _ => (v.ToString("0.##"), ""),
    };

    /// <summary>图例 / OSD 里的紧凑格式（带单位，可直接作为“无前缀”显示）。</summary>
    public string FormatShort(float v) => Metric switch
    {
        MetricKind.Temperature => $"{v:0}°C",
        MetricKind.Load => $"{v:0}%",
        MetricKind.Clock => v >= 10000 ? $"{v / 1000:0.00}G" : $"{v:0}M",
        MetricKind.Power => $"{v:0.0}W",
        MetricKind.Fan => $"{v:0}",
        MetricKind.DataRate => v >= 1_073_741_824 ? $"{v / 1_073_741_824:0.00}GB/s"
                             : v >= 1_048_576 ? $"{v / 1_048_576:0.0}MB/s"
                             : $"{v / 1024:0}KB/s",
        MetricKind.Data => $"{v:0.0}GB",
        _ => v.ToString("0.##"),
    };
}

/// <summary>
/// 固定容量环形历史缓冲：写入在采样线程（定时器回调），读取在 UI 线程（图例统计与曲线重绘）。
/// 用一把轻量锁保证读写不交叉——代价约每秒几十次极短的临界区，换来不会出现
/// “把刚被覆盖的旧样本当成最新点”或 (tick,value) 错配导致的曲线尖刺。
/// 仍然零分配。
/// </summary>
public sealed class SensorHistory
{
    private readonly float[] _values;
    private readonly long[] _ticks;
    private readonly object _sync = new();
    private int _head;

    public int Count { get; private set; }
    public long LastTick { get; private set; } = long.MinValue;

    public SensorHistory(int capacity)
    {
        _values = new float[capacity];
        _ticks = new long[capacity];
    }

    public void Append(long tickMs, float v)
    {
        lock (_sync)
        {
            _values[_head] = v;
            _ticks[_head] = tickMs;
            _head = (_head + 1) % _values.Length;
            if (Count < _values.Length) Count++;
            LastTick = tickMs;
        }
    }

    /// <summary>窗口内统计：(点数, 最小, 最大, 总和)。扫描从最新往回，遇到超窗即止。</summary>
    public (int N, float Min, float Max, double Sum) Stats(long nowMs, long windowMs)
    {
        lock (_sync)
        {
            int n = 0;
            float min = float.MaxValue, max = float.MinValue;
            double sum = 0;
            int idx = _head;
            for (int i = 0; i < Count; i++)
            {
                if (--idx < 0) idx = _values.Length - 1;
                long t = _ticks[idx];
                if (nowMs - t > windowMs) break;
                float v = _values[idx];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
                n++;
            }
            return n == 0 ? (0, 0f, 0f, 0.0) : (n, min, max, sum);
        }
    }

    /// <summary>把窗口内数据按时间升序复制进调用方缓冲区，返回点数（无分配）。</summary>
    public int CopyWindow(long nowMs, long windowMs, long[] ticks, float[] values)
    {
        lock (_sync)
        {
            int n = 0;
            int idx = _head;
            for (int i = 0; i < Count; i++)
            {
                if (--idx < 0) idx = _values.Length - 1;
                long t = _ticks[idx];
                if (nowMs - t > windowMs) break;
                if (n >= ticks.Length) break;
                ticks[n] = t;
                values[n] = _values[idx];
                n++;
            }
            for (int a = 0, b = n - 1; a < b; a++, b--)
            {
                (ticks[a], ticks[b]) = (ticks[b], ticks[a]);
                (values[a], values[b]) = (values[b], values[a]);
            }
            return n;
        }
    }
}
