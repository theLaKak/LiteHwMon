using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace LiteHwMon.Core;

/// <summary>
/// 监控引擎：LibreHardwareMonitor（温度/功耗/频率/风扇，需内核驱动）为主，
/// 无需驱动的系统计数器为回退；每个 ReadingSpec 带独立历史缓冲，按配置间隔轮询。
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsControllerEnabled = false,
        IsNetworkEnabled = false,
    };
    private readonly List<ReadingSpec> _specs = new();
    private readonly SystemFallbacks _sys = new();
    private readonly object _timerLock = new();
    private System.Threading.Timer? _timer;
    private int _polling;
    private int _intervalMs = 1000;
    private int _gpuHwCount;
    private readonly List<ReadingSpec> _gpuFallbackSpecs = new();
    private bool _firstPollDone;
    private volatile bool _started;
    // LHM 更新看门狗 / 自适应降频状态
    private volatile bool _lhmActive = true;
    private int _lhmFailures;
    private int _lhmEveryNPolls = 1;
    private int _lhmPollCounter;
    private int _lhmPassRunning;
    private DateTime _nextLhmRetry = DateTime.MinValue;
    private RaplPowerReader? _rapl;
    private readonly CpuTelemetryCheck _telemetry = new();
    private volatile int _disposed;

    public string CpuName { get; private set; } = "";
    public string DriverStatus { get; private set; } = "初始化中";
    public bool DriverOk { get; private set; }
    // 性能统计（供冒烟测试/状态栏观察引擎开销）
    public double OpenMs { get; private set; }
    public double FirstPollMs { get; private set; }
    public double LastPollMs { get; private set; }
    public int PollCount { get; private set; }
    public double LhmCpuMs { get; private set; }
    public double LhmGpuMs { get; private set; }
    public double LhmStorageMs { get; private set; }
    public double LhmBoardMs { get; private set; }
    public double SysMs { get; private set; }
    public string LastUpdatingHardware { get; private set; } = "";
    /// <summary>启动流程阶段标记，用于诊断初始化卡点。</summary>
    public string StartupPhase { get; private set; } = "not-started";
    public string? LastError { get; private set; }
    /// <summary>CPU 温度读不到且原因是缺少 PawnIO 驱动（AMD 平台），UI 据此给出一条可执行的提示。</summary>
    public bool CpuTempNeedsPawnIo { get; private set; }
    /// <summary>
    /// CPU 温度读不到、但**不是**缺少 PawnIO 造成的（PawnIO 已在位或非 AMD 平台，且驱动可用）。
    /// 属于该平台/主板不暴露该传感器，安装任何组件都无法解决；UI 据此避免给出无效建议。
    /// </summary>
    public bool CpuTempUnsupported { get; private set; }
    /// <summary>CPU 温度/功耗遥测自检：功耗在负载大范围变化时仍恒定 → 该平台读数不可信。</summary>
    public bool CpuTelemetrySuspect => _telemetry.Suspect;
    public string CpuTelemetryEvidence => _telemetry.Evidence;
    /// <summary>GPU 硬件名（与 _gpuFallbackSpecs 的序号一一对应）。</summary>
    public List<string> GpuNames { get; } = new();
    public List<string> DiskNames { get; } = new();
    public IReadOnlyList<ReadingSpec> Specs => _specs;
    public SystemFallbacks Sys => _sys;

    /// <summary>硬件枚举完成（UI 可据此构建卡片），后台线程触发。</summary>
    public event Action? HardwareReady;
    /// <summary>每轮采样完成，后台线程触发。</summary>
    public event Action? PollCompleted;

    public void Start(int intervalMs)
    {
        _intervalMs = Math.Clamp(intervalMs, 250, 60000);
        StartupPhase = "task-queued";
        Task.Run(() =>
        {
            try
            {
                _rapl = new RaplPowerReader(); // 可选回退；驱动不可用时 Available=false
                var sw = System.Diagnostics.Stopwatch.StartNew();
                StartupPhase = "opening";
                _computer.Open();
                OpenMs = sw.ElapsedMilliseconds;
                StartupPhase = "static-init";
                _sys.Initialize(); // 先于规格构建：内存频率/磁盘容量等静态数据要先就绪
                StartupPhase = "build-specs";
                BuildSpecs();
                StartupPhase = "start-counters";
                _sys.StartCounters(); // 规格确定需求后创建性能计数器
            }
            catch
            {
                // 即使 LibreHardwareMonitor 初始化失败，回退数据源仍可工作
                StartupPhase = "open-failed";
                try { BuildFallbackOnlySpecs(); } catch { }
            }
            StartupPhase = "ready";
            HardwareReady?.Invoke();
            _started = true;
            SetInterval(_intervalMs);
        });
    }

    public void SetInterval(int ms)
    {
        _intervalMs = Math.Clamp(ms, 250, 60000);
        lock (_timerLock)
        {
            if (!_started) return; // Start() 前只记录间隔
            if (_timer == null)
                _timer = new System.Threading.Timer(_ => Poll(), null, _intervalMs, _intervalMs);
            else
                _timer.Change(_intervalMs, _intervalMs);
        }
    }

    // ---------------------------------------------------------------- 规格构建

    private void AddSpec(ReadingSpec spec) => _specs.Add(spec);

    private void BuildSpecs()
    {
        BuildCpuSpecs();
        BuildGpuSpecs();
        BuildMemorySpecs();
        BuildDiskSpecs();
        BuildFanSpecs();
    }

    /// <summary>LibreHardwareMonitor 完全不可用时，仍提供系统级基础数据。</summary>
    private void BuildFallbackOnlySpecs()
    {
        if (_specs.Count > 0) return;
        AddSpec(new ReadingSpec
        {
            Id = "/sys/cpu/load", Name = "占用率", LegendName = "CPU 占用率",
            Device = DeviceKind.Cpu, Metric = MetricKind.Load, Fallback = FallbackKind.CpuLoad,
        });
        BuildMemorySpecs();
    }

    private void BuildCpuSpecs()
    {
        bool hasPerCoreClocks = false;
        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu != null)
        {
            CpuName = cpu.Name;

            var temps = cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
            var temp = temps.FirstOrDefault(s => s.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
                    ?? temps.FirstOrDefault(s => s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                    ?? temps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                    ?? temps.FirstOrDefault(s => s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                    ?? temps.FirstOrDefault();
            if (temp != null)
                AddSpec(new ReadingSpec
                {
                    Id = temp.Identifier.ToString(), Name = "温度", LegendName = "CPU 温度",
                    Device = DeviceKind.Cpu, Metric = MetricKind.Temperature, Sensor = temp,
                });

            var pows = cpu.Sensors.Where(s => s.SensorType == SensorType.Power).ToList();
            var pow = pows.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)) ?? pows.FirstOrDefault();
            // 功耗优先用 LibreHardwareMonitor 传感器；读不到时回退到 RAPL MSR（真实封装功耗）。
            AddSpec(new ReadingSpec
            {
                Id = pow != null ? pow.Identifier.ToString() : "/sys/cpu/rapl-power",
                Name = "功耗", LegendName = "CPU 功耗",
                Device = DeviceKind.Cpu, Metric = MetricKind.Power,
                Sensor = pow, Fallback = FallbackKind.CpuRapPower,
            });

            var clocks = cpu.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase)).ToList();
            hasPerCoreClocks = clocks.Count > 0;
            _needCpuFreq = true; // 始终启用估算源，驱动读不到时钟时兜底
            if (hasPerCoreClocks)
                AddSpec(new ReadingSpec
                {
                    Id = "/cpu/maxclock", Name = "频率", LegendName = "CPU 频率（最大核心）",
                    Device = DeviceKind.Cpu, Metric = MetricKind.Clock,
                    Fallback = FallbackKind.CpuMaxClock, ExtraSensors = clocks,
                });
        }

        // CPU 占用率始终用系统计数器：无需驱动、更可靠
        AddSpec(new ReadingSpec
        {
            Id = "/sys/cpu/load", Name = "占用率", LegendName = "CPU 占用率",
            Device = DeviceKind.Cpu, Metric = MetricKind.Load, Fallback = FallbackKind.CpuLoad,
        });

        if (!hasPerCoreClocks)
            AddSpec(new ReadingSpec
            {
                Id = "/sys/cpu/freq", Name = "频率", LegendName = "CPU 频率（估算）",
                Device = DeviceKind.Cpu, Metric = MetricKind.Clock, Fallback = FallbackKind.CpuFreqEstimate,
            });
    }

    private bool _needCpuFreq;

    private void BuildGpuSpecs()
    {
        int gi = 0;
        bool needGpuFallback = false;
        foreach (var hw in _computer.Hardware.Where(h =>
                     h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
        {
            string shortName = ShortGpuName(hw.Name);
            GpuNames.Add(hw.Name);

            var temp = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("temperature", StringComparison.OrdinalIgnoreCase))
                    ?? hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
            var load = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase));
            var clock = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase));
            var power = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Contains("power", StringComparison.OrdinalIgnoreCase))
                     ?? hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Contains("package", StringComparison.OrdinalIgnoreCase));
            var memUsed = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase));
            var memTotal = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase));

            if (hw.HardwareType == HardwareType.GpuNvidia)
            {
                // NVIDIA：驱动自带 NVML 直连（无需提权、不经过易阻塞的内核路径），LHM 传感器作为备用源
                int nvIdx = ParseNvIndex(hw.Identifier) ?? 0;
                AddSpec(new ReadingSpec
                {
                    Id = $"/nvml/{nvIdx}/temp", Name = "温度", LegendName = $"{shortName} 温度",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Temperature,
                    Fallback = FallbackKind.NvmlTemp, FallbackIndex = nvIdx, Sensor = temp, FallbackIndex2 = gi,
                });
                AddSpec(new ReadingSpec
                {
                    Id = $"/nvml/{nvIdx}/load", Name = "占用率", LegendName = $"{shortName} 占用率",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Load,
                    Fallback = FallbackKind.NvmlLoad, FallbackIndex = nvIdx, Sensor = load, FallbackIndex2 = gi,
                });
                AddSpec(new ReadingSpec
                {
                    Id = $"/nvml/{nvIdx}/clock", Name = "核心频率", LegendName = $"{shortName} 核心频率",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Clock,
                    Fallback = FallbackKind.NvmlClock, FallbackIndex = nvIdx, Sensor = clock, FallbackIndex2 = gi,
                });
                AddSpec(new ReadingSpec
                {
                    Id = $"/nvml/{nvIdx}/power", Name = "功耗", LegendName = $"{shortName} 功耗",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Power,
                    Fallback = FallbackKind.NvmlPower, FallbackIndex = nvIdx, Sensor = power, FallbackIndex2 = gi,
                });
                AddSpec(new ReadingSpec
                {
                    Id = $"/nvml/{nvIdx}/used", Name = "显存占用", LegendName = $"{shortName} 显存占用",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Data,
                    Fallback = FallbackKind.NvmlMemUsed, FallbackIndex = nvIdx, Sensor = memUsed, FallbackIndex2 = gi,
                });
                AddSpec(new ReadingSpec
                {
                    Id = $"/nvml/{nvIdx}/pct", Name = "显存占用率", LegendName = $"{shortName} 显存占用率",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Load,
                    Fallback = FallbackKind.NvmlMemPct, FallbackIndex = nvIdx,
                    Sensor = memUsed, ExtraSensors = memTotal != null ? [memTotal] : null, FallbackIndex2 = gi,
                });
                gi++;
                continue;
            }

            // AMD / Intel：LHM 传感器 + PDH 回退
            // 注意：FallbackIndex 用于 PDH 适配器配对，FallbackIndex2 用于 UI 分组（必须等于枚举序号 gi），
            // 缺少 FallbackIndex2 会让多显卡机型的所有 GPU 规格挤到 0 号卡片/分组里。
            if (temp != null)
                AddSpec(new ReadingSpec
                {
                    Id = temp.Identifier.ToString(), Name = "温度", LegendName = $"{shortName} 温度",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Temperature,
                    Sensor = temp, FallbackIndex = gi, FallbackIndex2 = gi,
                });

            if (load != null)
                AddSpec(new ReadingSpec
                {
                    Id = load.Identifier.ToString(), Name = "占用率", LegendName = $"{shortName} 占用率",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Load,
                    Sensor = load, FallbackIndex = gi, FallbackIndex2 = gi,
                });
            else
            {
                needGpuFallback = true;
                AddSpec(NewGpuFallback(gi, "占用率", $"{shortName} 占用率", MetricKind.Load, FallbackKind.GpuUtil));
            }

            if (clock != null)
                AddSpec(new ReadingSpec
                {
                    Id = clock.Identifier.ToString(), Name = "核心频率", LegendName = $"{shortName} 核心频率",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Clock,
                    Sensor = clock, FallbackIndex = gi, FallbackIndex2 = gi,
                });

            if (power != null)
                AddSpec(new ReadingSpec
                {
                    Id = power.Identifier.ToString(), Name = "功耗", LegendName = $"{shortName} 功耗",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Power,
                    Sensor = power, FallbackIndex = gi, FallbackIndex2 = gi,
                });

            if (memUsed != null)
            {
                AddSpec(new ReadingSpec
                {
                    Id = memUsed.Identifier.ToString(), Name = "显存占用", LegendName = $"{shortName} 显存占用",
                    Device = DeviceKind.Gpu, Metric = MetricKind.Data,
                    Sensor = memUsed, FallbackIndex = gi, FallbackIndex2 = gi,
                });
                if (memTotal != null)
                    AddSpec(new ReadingSpec
                    {
                        Id = memUsed.Identifier + "/pct", Name = "显存占用率", LegendName = $"{shortName} 显存占用率",
                        Device = DeviceKind.Gpu, Metric = MetricKind.Load,
                        Sensor = memUsed, ExtraSensors = [memTotal],
                        Fallback = FallbackKind.GpuMemPct, FallbackIndex = gi, FallbackIndex2 = gi,
                    });
            }
            else
            {
                needGpuFallback = true;
                AddSpec(NewGpuFallback(gi, "显存占用", $"{shortName} 显存占用", MetricKind.Data, FallbackKind.GpuMemUsed));
            }
            gi++;
        }
        _gpuHwCount = gi;
        _needGpuFlag = needGpuFallback;
    }

    private static int? ParseNvIndex(Identifier id)
    {
        var parts = id.ToString().Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if ((parts[i] == "nvidiagpu" || parts[i] == "nvapi") && int.TryParse(parts[i + 1], out int v))
                return v;
        return null;
    }

    private bool _needGpuFlag;

    private ReadingSpec NewGpuFallback(int gpuIndex, string name, string legend, MetricKind metric, FallbackKind kind)
    {
        var spec = new ReadingSpec
        {
            Id = $"/sys/gpu/{gpuIndex}/{kind}", Name = name, LegendName = legend,
            Device = DeviceKind.Gpu, Metric = metric, Fallback = kind,
            FallbackIndex = gpuIndex, FallbackIndex2 = gpuIndex,
        };
        _gpuFallbackSpecs.Add(spec);
        return spec;
    }

    private void BuildMemorySpecs()
    {
        AddSpec(new ReadingSpec
        {
            Id = "/sys/mem/used", Name = "已用", LegendName = "内存已用",
            Device = DeviceKind.Memory, Metric = MetricKind.Data, Fallback = FallbackKind.MemUsed,
        });
        AddSpec(new ReadingSpec
        {
            Id = "/sys/mem/avail", Name = "可用", LegendName = "内存可用",
            Device = DeviceKind.Memory, Metric = MetricKind.Data, Fallback = FallbackKind.MemAvailable,
        });
        AddSpec(new ReadingSpec
        {
            Id = "/sys/mem/load", Name = "占用率", LegendName = "内存占用率",
            Device = DeviceKind.Memory, Metric = MetricKind.Load, Fallback = FallbackKind.MemLoad,
        });
        var freq = new ReadingSpec
        {
            Id = "/sys/mem/freq", Name = "频率", LegendName = "内存频率",
            Device = DeviceKind.Memory, Metric = MetricKind.Clock, IsStatic = true,
        };
        if (float.TryParse(_sys.MemoryFrequencyText.Replace(" MHz", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float mhz))
            freq.Value = mhz;
        AddSpec(freq);
    }

    private void BuildDiskSpecs()
    {
        int di = 0;
        bool needDisk = false;
        foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
        {
            DiskNames.Add(hw.Name);
            int physIdx = ParseHddIndex(hw.Identifier) ?? di;

            var temp = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
            if (temp != null)
                AddSpec(new ReadingSpec
                {
                    Id = temp.Identifier.ToString(), Name = "温度", LegendName = $"磁盘{physIdx} 温度",
                    Device = DeviceKind.Disk, Metric = MetricKind.Temperature, Sensor = temp, FallbackIndex = physIdx,
                });

            var activity = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("activity", StringComparison.OrdinalIgnoreCase));
            if (activity != null)
                AddSpec(new ReadingSpec
                {
                    Id = activity.Identifier.ToString(), Name = "活动率", LegendName = $"磁盘{physIdx} 活动率",
                    Device = DeviceKind.Disk, Metric = MetricKind.Load, Sensor = activity, FallbackIndex = physIdx,
                });
            else
            {
                needDisk = true;
                AddSpec(new ReadingSpec
                {
                    Id = $"/sys/disk/{physIdx}/activity", Name = "活动率", LegendName = $"磁盘{physIdx} 活动率",
                    Device = DeviceKind.Disk, Metric = MetricKind.Load, Fallback = FallbackKind.DiskActivity, FallbackIndex = physIdx,
                });
            }

            var read = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Contains("read", StringComparison.OrdinalIgnoreCase));
            if (read != null)
                AddSpec(new ReadingSpec
                {
                    Id = read.Identifier.ToString(), Name = "读取速度", LegendName = $"磁盘{physIdx} 读取",
                    Device = DeviceKind.Disk, Metric = MetricKind.DataRate, Sensor = read, FallbackIndex = physIdx,
                });
            else
            {
                needDisk = true;
                AddSpec(new ReadingSpec
                {
                    Id = $"/sys/disk/{physIdx}/read", Name = "读取速度", LegendName = $"磁盘{physIdx} 读取",
                    Device = DeviceKind.Disk, Metric = MetricKind.DataRate, Fallback = FallbackKind.DiskRead, FallbackIndex = physIdx,
                });
            }

            var write = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Contains("write", StringComparison.OrdinalIgnoreCase));
            if (write != null)
                AddSpec(new ReadingSpec
                {
                    Id = write.Identifier.ToString(), Name = "写入速度", LegendName = $"磁盘{physIdx} 写入",
                    Device = DeviceKind.Disk, Metric = MetricKind.DataRate, Sensor = write, FallbackIndex = physIdx,
                });
            else
            {
                needDisk = true;
                AddSpec(new ReadingSpec
                {
                    Id = $"/sys/disk/{physIdx}/write", Name = "写入速度", LegendName = $"磁盘{physIdx} 写入",
                    Device = DeviceKind.Disk, Metric = MetricKind.DataRate, Fallback = FallbackKind.DiskWrite, FallbackIndex = physIdx,
                });
            }

            var used = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.Contains("used space", StringComparison.OrdinalIgnoreCase));
            if (used != null)
                AddSpec(new ReadingSpec
                {
                    Id = used.Identifier.ToString(), Name = "已用空间", LegendName = $"磁盘{physIdx} 已用空间",
                    Device = DeviceKind.Disk, Metric = MetricKind.Load, Sensor = used, FallbackIndex = physIdx,
                });
            di++;
        }
        _sys.SetRequirements(needGpu: _needGpuFlag, needDisk: needDisk, needCpuFreq: _needCpuFreq);
    }

    private void BuildFanSpecs()
    {
        foreach (var hw in _computer.Hardware.Where(h =>
                     h.HardwareType is HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController))
        {
            CollectFans(hw);
            foreach (var sub in hw.SubHardware) CollectFans(sub);
        }
    }

    private void CollectFans(IHardware hw)
    {
        foreach (var fan in hw.Sensors.Where(s => s.SensorType == SensorType.Fan))
        {
            string label = fan.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase) ? "CPU 风扇" : fan.Name;
            AddSpec(new ReadingSpec
            {
                Id = fan.Identifier.ToString(), Name = label, LegendName = label,
                Device = DeviceKind.Fan, Metric = MetricKind.Fan, Sensor = fan,
            });
        }
    }

    private static int? ParseHddIndex(Identifier id)
    {
        // 形如 /hdd/0/...
        var parts = id.ToString().Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i] == "hdd" && int.TryParse(parts[i + 1], out int v))
                return v;
        return null;
    }

    private static string ShortGpuName(string name)
    {
        if (name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase))
            return "AMD 核显"; // AMD iGPU 的通用名太泛化，如 “AMD Radeon(TM) Graphics”
        string s = name
            .Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "")
            .Replace("AMD Radeon(TM) ", "").Replace("AMD Radeon ", "").Replace("AMD ", "")
            .Replace("Intel(R) ", "").Replace("Intel ", "")
            .Replace(" with Radeon Graphics", "").Replace(" Graphics", "")
            .Trim();
        return s.Length > 0 ? s : name;
    }

    // ---------------------------------------------------------------- 轮询

    private void Poll()
    {
        if (_disposed != 0) return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 取到并发令牌后再确认一次：Dispose() 可能刚把 _computer 关掉
            if (_disposed != 0) return;
            // ---- LibreHardwareMonitor 更新（带看门狗：厂商驱动/内核服务可能阻塞）----
            // 超时或连续失败时自动降级为系统数据源；期间低频重试以便自动恢复。
            _lhmPollCounter++;
            bool wantLhm = _lhmActive || DateTime.UtcNow >= _nextLhmRetry;
            bool scheduledLhm = wantLhm && (_lhmEveryNPolls <= 1 || _lhmPollCounter % _lhmEveryNPolls == 0);
            if (scheduledLhm)
            {
                if (!_lhmActive) _lhmActive = true; // 重试窗口
                LhmCpuMs = LhmGpuMs = LhmStorageMs = LhmBoardMs = 0;

                // 同一时刻只允许一轮 LHM 刷新在跑：上一轮若仍卡在驱动 IO 里，
                // 本轮直接跳过并计入失败，避免两个线程并发调用同一个硬件、
                // 也避免并发修改看门狗集合（HashSet 非线程安全）。
                bool started = Interlocked.CompareExchange(ref _lhmPassRunning, 1, 0) == 0;
                bool finished = false;
                if (started)
                {
                    var t = Task.Run(() =>
                    {
                        try { UpdateAllHardware(); }
                        catch { /* 单次驱动 IO 失败不应中断轮询 */ }
                        finally { Volatile.Write(ref _lhmPassRunning, 0); }
                    });
                    double budget = Math.Max(2.0, _intervalMs / 1000.0 * 2.5);
                    // 逐硬件自身已有 1.5s 预算，全局预算必须给多硬件机型留足余量，
                    // 否则“多个硬件各慢一点”会被误判成驱动无响应
                    budget = Math.Max(budget, Math.Max(1, _computer.Hardware.Count) * 1.6);
                    finished = t.Wait(TimeSpan.FromSeconds(budget));
                }

                if (finished)
                {
                    if (_lhmFailures > 0) { _lhmFailures = 0; _lhmEveryNPolls = 1; }
                    // 单次过慢时自适应降频，避免拖垮整体轮询节奏
                    double lhmMs = LhmCpuMs + LhmGpuMs + LhmStorageMs + LhmBoardMs;
                    _lhmEveryNPolls = lhmMs > 1500 ? 5 : lhmMs > 600 ? 2 : 1;
                }
                else
                {
                    _lhmFailures++;
                    _lhmEveryNPolls = 5;
                    if (_lhmFailures >= 5)
                    {
                        // 内核驱动/厂商接口持续无响应：本会话彻底停用 LHM 更新，避免阻塞线程累积
                        _lhmActive = false;
                        _nextLhmRetry = DateTime.MaxValue;
                        _firstPollDone = false;
                        // 否则传感器对象会一直保留最后一次读到的值，界面显示“冻结”的旧数据
                        InvalidateSensorValues();
                    }
                    else
                    {
                        _nextLhmRetry = DateTime.UtcNow.AddSeconds(10);
                        _firstPollDone = false;
                    }
                }
            }

            sw.Restart();
            _sys.Update();
            SysMs = sw.ElapsedMilliseconds;

            long now = Environment.TickCount64;
            foreach (var s in _specs)
            {
                float? v = ReadSpec(s);
                s.Value = v;
                if (v.HasValue) s.History.Append(now, v.Value);
            }

            if (!_firstPollDone)
            {
                _firstPollDone = true;
                FirstPollMs = sw.ElapsedMilliseconds;
                EvaluateDriverStatus();
            }

            // 遥测自检需要"负载跨度 + 功耗读数"配对，用 OS 计数器（可靠）做负载基准
            var powerSpec = _specs.FirstOrDefault(s => s.Device == DeviceKind.Cpu && s.Metric == MetricKind.Power);
            _telemetry.Add(_sys.CpuLoadPct, powerSpec?.Value);
            // 驱动状态与遥测自检结论会随后续采样变化（如 PawnIO 缺失 → 装好后恢复），定期刷新
            if (PollCount % 5 == 0) EvaluateDriverStatus();

            HideSilentFans();
            PollCount++;
            LastPollMs = sw.ElapsedMilliseconds;
            PollCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            // 记录并吞掉异常，保证轮询线程存活
            LastError = ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace;
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private float? ReadSpec(ReadingSpec s)
    {
        switch (s.Fallback)
        {
            case FallbackKind.None when s.Device == DeviceKind.Cpu && s.Metric == MetricKind.Temperature:
                // CPU 温度为 0 意味着驱动读取失败而非真实温度
                return s.Sensor?.Value is { } tv && tv > 0 ? tv : null;

            case FallbackKind.CpuLoad:
                return Num(_sys.CpuLoadPct);

            case FallbackKind.CpuRapPower:
                // 优先 LHM 传感器，读不到时回退 RAPL MSR（真实封装功耗）
                float? raplPower = _rapl?.Sample(); // 每轮都采样，保持差值窗口新鲜
                if (s.Sensor?.Value is { } pv && pv > 0) return pv;
                return raplPower;

            case FallbackKind.CpuMaxClock:
            {
                float max = float.NaN;
                foreach (var c in s.ExtraSensors ?? [])
                    if (c.Value is { } v && v > 0 && (float.IsNaN(max) || v > max)) max = v;
                // 驱动不可用时各核时钟读不到，退回系统计数器估算
                return float.IsNaN(max) ? Num(_sys.CpuFreqMhz) : max;
            }

            case FallbackKind.MemUsed: return Num(_sys.MemUsedGB);
            case FallbackKind.MemAvailable: return Num(_sys.MemAvailGB);
            case FallbackKind.MemLoad: return Num(_sys.MemLoadPct);

            case FallbackKind.GpuUtil:
            case FallbackKind.GpuMemUsed:
            {
                var adapter = ResolveGpuAdapter(s.FallbackIndex);
                return s.Fallback == FallbackKind.GpuUtil ? Num(adapter?.UtilPct) : Num(adapter?.DedicatedGB);
            }

            case FallbackKind.NvmlTemp:
                if (Nvml.TryGetTemperature((uint)s.FallbackIndex, out float nvT)) return nvT;
                return s.Sensor?.Value is { } tv2 && tv2 > 0 ? tv2 : null; // LHM 备用

            case FallbackKind.NvmlLoad:
                if (Nvml.TryGetUtilization((uint)s.FallbackIndex, out float nvU)) return nvU;
                return s.Sensor?.Value;

            case FallbackKind.NvmlClock:
                if (Nvml.TryGetClock((uint)s.FallbackIndex, out float nvC)) return nvC;
                return s.Sensor?.Value;

            case FallbackKind.NvmlPower:
                if (Nvml.TryGetPower((uint)s.FallbackIndex, out float nvP)) return nvP;
                return s.Sensor?.Value is { } pv2 && pv2 > 0 ? pv2 : null;

            case FallbackKind.NvmlMemUsed:
                if (Nvml.TryGetMemory((uint)s.FallbackIndex, out float u2, out _)) return u2;
                return s.Sensor?.Value;

            case FallbackKind.NvmlMemPct:
                if (Nvml.TryGetMemory((uint)s.FallbackIndex, out float uu2, out float tt2) && tt2 > 0)
                    return Math.Clamp(uu2 / tt2 * 100f, 0f, 100f);
                if (s.Sensor?.Value is { } used2 && s.ExtraSensors is [{ Value: not null } tot2] && tot2.Value.Value > 0)
                    return Math.Clamp(used2 / tot2.Value.Value * 100f, 0f, 100f);
                return null;

            case FallbackKind.CpuFreqEstimate:
                return Num(_sys.CpuFreqMhz);

            case FallbackKind.GpuMemPct:
            {
                float used = s.Sensor?.Value ?? float.NaN;
                float total = s.ExtraSensors is [{ Value: not null } t] ? t.Value.Value : float.NaN;
                if (float.IsNaN(used) || float.IsNaN(total) || total <= 0) return null;
                return Math.Clamp(used / total * 100f, 0f, 100f);
            }

            case FallbackKind.DiskRead:
                return _sys.Disks.TryGetValue(s.FallbackIndex, out var d1) ? Num(d1.ReadBps) : null;
            case FallbackKind.DiskWrite:
                return _sys.Disks.TryGetValue(s.FallbackIndex, out var d2) ? Num(d2.WriteBps) : null;
            case FallbackKind.DiskActivity:
                return _sys.Disks.TryGetValue(s.FallbackIndex, out var d3) ? Num(d3.ActivityPct) : null;

            default:
            {
                if (s.IsStatic) return s.Value;
                // 直接传感器；带 ExtraSensors 时取最大值（如 CPU 各核心频率）
                float? v = s.Sensor?.Value;
                if (s.ExtraSensors == null) return v;
                float max = v ?? float.NaN;
                foreach (var c in s.ExtraSensors)
                    if (c.Value is { } cv && (float.IsNaN(max) || cv > max)) max = cv;
                return float.IsNaN(max) ? null : max;
            }
        }
    }

    private static float? Num(float? v) => v.HasValue && !float.IsNaN(v.Value) ? v : null;

    /// <summary>手动触发所有硬件（含子硬件）刷新；单个硬件卡死时拉黑跳过，不拖累其他硬件。</summary>
    private void UpdateAllHardware()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var hw in _computer.Hardware)
        {
            string key = hw.Identifier.ToString();
            if (_deadHardware.Contains(key))
            {
                // 拉黑后按退避策略低频重试，避免一次慢速刷新就把整个硬件永久判死
                if (_deadHardwareRetryAt.TryGetValue(key, out var retryAt) && DateTime.UtcNow >= retryAt)
                {
                    _deadHardware.Remove(key);
                    _deadHardwareRetryAt.Remove(key);
                }
                else
                {
                    continue;
                }
            }
            LastUpdatingHardware = $"{hw.HardwareType}:{hw.Name}";
            sw.Restart();
            var t = Task.Run(() =>
            {
                try
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware)
                    {
                        try { sub.Update(); } catch { }
                    }
                }
                catch { }
            });
            if (!t.Wait(1500))
            {
                // 该硬件刷新持续阻塞：暂时拉黑并安排退避重试，其传感器标记为不可用
                _deadHardware.Add(key);
                int fails = _deadHardwareFailures.TryGetValue(key, out var f) ? f + 1 : 1;
                _deadHardwareFailures[key] = fails;
                _deadHardwareRetryAt[key] = DateTime.UtcNow.AddSeconds(fails switch { 1 => 10, 2 => 30, _ => 120 });
                foreach (var s in _specs)
                    if (s.Sensor?.Hardware != null && s.Sensor.Hardware.Identifier.ToString() == key)
                        s.Value = null;
                LastDeadHardware = $"{hw.HardwareType}:{hw.Name}";
                continue;
            }
            _deadHardwareFailures.Remove(key); // 重试成功，清空失败计数
            double ms = sw.ElapsedMilliseconds;
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu: LhmCpuMs += ms; break;
                case HardwareType.Storage: LhmStorageMs += ms; break;
                case HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController: LhmBoardMs += ms; break;
                default: LhmGpuMs += ms; break;
            }
        }
    }

    private readonly HashSet<string> _deadHardware = new(StringComparer.Ordinal);

    /// <summary>
    /// 停止使用 LHM 时把传感器读数清空（显示为“不可用”）。
    /// 否则 ISensor.Value 会保留最后一次成功读取的数值，界面看起来还在更新、实际是冻结的旧数据。
    /// </summary>
    private void InvalidateSensorValues()
    {
        foreach (var s in _specs)
            if (s.Sensor != null)
                s.Value = null;
    }
    private readonly Dictionary<string, int> _deadHardwareFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _deadHardwareRetryAt = new(StringComparer.Ordinal);
    public string LastDeadHardware { get; private set; } = "";

    /// <summary>
    /// 把系统 GPU 适配器（按专用显存降序）与缺少传感器的 GPU 硬件对号入座：
    /// 已被厂商 SDK 覆盖的独显通常专用显存最大，先把它排除，剩余适配器按序分配。
    /// </summary>
    private GpuAdapterFallback? ResolveGpuAdapter(int gpuIndex)
    {
        var adapters = _sys.GpuAdapters;
        if (adapters.Count == 0) return null;
        if (_gpuHwCount <= 1 || adapters.Count == 1)
            return adapters[Math.Min(gpuIndex, adapters.Count - 1)];
        // 多显卡：已被厂商 SDK 覆盖的 GPU 通常专用显存最大（排最前），先排除它
        int coveredGpus = _gpuHwCount - _gpuFallbackSpecs.Select(s => s.FallbackIndex).Distinct().Count();
        var pool = coveredGpus > 0 && adapters.Count > coveredGpus
            ? adapters.Skip(coveredGpus).ToList()
            : adapters.ToList();
        // gpuIndex 是 LHM 枚举序号；只对缺少传感器的 GPU 按序分配
        int order = 0;
        for (int k = 0; k < gpuIndex; k++)
            if (_gpuFallbackSpecs.Any(s => s.FallbackIndex == k)) order++;
        return pool[Math.Min(order, pool.Count - 1)];
    }

    private void EvaluateDriverStatus()
    {
        var tempSpec = _specs.FirstOrDefault(s => s.Device == DeviceKind.Cpu && s.Metric == MetricKind.Temperature);
        // 只有驱动传感器直接给出有效值才算“驱动读到温度”，回退值不算
        bool sensorTempOk = tempSpec?.Sensor?.Value is { } sv && sv > 0;
        bool cpuTempOk = tempSpec?.Value != null;
        bool cpuPowerOk = _specs.Any(s => s.Device == DeviceKind.Cpu && s.Metric == MetricKind.Power && s.Value != null);
        DriverOk = sensorTempOk || cpuPowerOk;

        bool elevated = Environment.OSVersion.Platform == PlatformID.Win32NT &&
                        new System.Security.Principal.WindowsPrincipal(
                            System.Security.Principal.WindowsIdentity.GetCurrent())
                            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        // AMD 的 Tctl/Tdie 只能经 SMN 读取，而 LHM 0.9.5+ 的硬件访问已完全依赖 PawnIO
        CpuTempNeedsPawnIo = !sensorTempOk
                             && PrivilegedBackend.IsAmdPlatform(CpuName)
                             && !PrivilegedBackend.PawnIoInstalled;

        // 与上面区分开：PawnIO 已在位（或非 AMD 平台），驱动本身可用（功耗能读到），
        // 但依然拿不到 CPU 温度 —— 属于该平台/该主板不暴露该传感器，用户无法通过安装组件解决。
        CpuTempUnsupported = !sensorTempOk
                             && !CpuTempNeedsPawnIo
                             && cpuPowerOk
                             && _lhmActive;

        if (!_lhmActive) DriverStatus = "驱动无响应 · 已切换系统数据源";
        else if (_deadHardware.Count > 0) DriverStatus = "驱动部分可用";
        else if (CpuTelemetrySuspect) DriverStatus = "CPU 遥测不可信（该平台 SMU 读数异常）";
        else if (sensorTempOk) DriverStatus = "驱动正常";
        else if (CpuTempNeedsPawnIo) DriverStatus = cpuPowerOk ? "温度需 PawnIO · 功耗走 RAPL" : "温度需安装 PawnIO 驱动";
        else if (CpuTempUnsupported) DriverStatus = "该平台不提供 CPU 温度传感器";
        else if (cpuPowerOk) DriverStatus = "CPU 功耗回退 RAPL";
        else if (!elevated) DriverStatus = "未提权 · 温度不可用";
        else DriverStatus = "驱动未加载";
    }

    /// <summary>未连接的风扇（空值或 0 RPM）在图例中隐藏；之后每轮若读到转速会重新显示。</summary>
    private void HideSilentFans()
    {
        foreach (var s in _specs)
            if (s.Device == DeviceKind.Fan)
                s.Hidden = s.Value is null or <= 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        // 繁重/可能阻塞的收尾放到后台线程：绝不阻塞 UI 关闭流程，
        // 即使 LHM 驱动清理卡住，进程也能正常退出。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _computer.Close(); } catch { }
            try { _sys.Dispose(); } catch { }
            try { Nvml.Shutdown(); } catch { }
            try { _rapl?.Dispose(); } catch { }
        });
    }
}
