using System.Collections.ObjectModel;
using System.Windows.Media;
using LiteHwMon.Core;

namespace LiteHwMon.Ui;

/// <summary>OSD 上一个指标单元格（绑定一个 ReadingSpec）。</summary>
public sealed class OsdMetricCell : ObservableObject
{
    public required ReadingSpec Spec { get; init; }
    public required string Label { get; init; }

    private string _text = "—";
    public string Text { get => _text; private set { if (_text == value) return; _text = value; Raise(); } }

    private Brush _foreground = OsdPalette.Unavailable;
    public Brush Foreground { get => _foreground; private set { if (ReferenceEquals(_foreground, value)) return; _foreground = value; Raise(); } }

    private bool _available;
    public bool Available { get => _available; private set { if (_available == value) return; _available = value; Raise(); } }

    public void Refresh()
    {
        if (Spec.Value is { } v)
        {
            Text = Spec.FormatShort(v);
            Foreground = OsdPalette.Value;
            Available = true;
        }
        else
        {
            Text = "—";
            Foreground = OsdPalette.Unavailable;
            Available = false;
        }
    }
}

/// <summary>
/// OSD 配色：数值用纯白、名称用强调色、前缀用亮灰、不可用项用减弱灰。
/// 所有前景色都不受背景板透明度影响（整窗 Opacity 恒为 1），
/// 因此无论游戏画面多复杂，数值都是最亮的一层。
/// </summary>
public static class OsdPalette
{
    /// <summary>指标数值：纯白，最高对比。</summary>
    public static readonly Brush Value = Frozen("#FFFFFF");
    /// <summary>指标前缀（温/占/频）：亮灰蓝，比数值暗一档以形成层级。</summary>
    public static readonly Brush Label = Frozen("#BCC8DC");
    /// <summary>不可用（—）：明显减弱但仍可辨认。</summary>
    public static readonly Brush Unavailable = Frozen("#8B95A8");
    /// <summary>提示文字。</summary>
    public static readonly Brush Hint = Frozen("#A7B3C7");

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>运行时探测到的 OSD 硬件分组（CPU / GPU×N / 内存 / 硬盘×N / 风扇×N）。</summary>
public sealed class OsdHardwareDef
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required DeviceKind Kind { get; init; }
    public List<ReadingSpec> Parameters { get; set; } = new();
}

/// <summary>
/// 基于 MonitorEngine 现有 ReadingSpec 构建“硬件 + 参数”两级目录，
/// 并负责 OSD 配置的读取/默认/持久化判定。OSD 不再依赖任何硬编码模块清单。
/// </summary>
public static class OsdCatalog
{
    public static List<OsdHardwareDef> Build(MonitorEngine engine)
    {
        var specs = engine.Specs;
        var list = new List<OsdHardwareDef>();

        var cpu = specs.Where(s => s.Device == DeviceKind.Cpu).ToList();
        if (cpu.Count > 0)
            list.Add(new OsdHardwareDef { Id = "cpu", Title = OsdModules.CpuRowTitle(engine.CpuName), Kind = DeviceKind.Cpu, Parameters = cpu });

        for (int gi = 0; gi < engine.GpuNames.Count; gi++)
        {
            var g = specs.Where(s => s.Device == DeviceKind.Gpu && s.FallbackIndex2 == gi).ToList();
            if (g.Count == 0) continue;
            string title = OsdModules.GpuRowTitle(engine.GpuNames[gi], gi, engine.GpuNames.Count);
            list.Add(new OsdHardwareDef { Id = $"gpu:{gi}", Title = title, Kind = DeviceKind.Gpu, Parameters = g });
        }

        var mem = specs.Where(s => s.Device == DeviceKind.Memory).ToList();
        if (mem.Count > 0)
            list.Add(new OsdHardwareDef { Id = "memory", Title = "内存", Kind = DeviceKind.Memory, Parameters = mem });

        foreach (var grp in specs.Where(s => s.Device == DeviceKind.Disk).GroupBy(s => s.FallbackIndex))
            list.Add(new OsdHardwareDef { Id = $"disk:{grp.Key}", Title = $"磁盘 {grp.Key}", Kind = DeviceKind.Disk, Parameters = grp.ToList() });

        var fans = specs.Where(s => s.Device == DeviceKind.Fan).ToList();
        for (int fi = 0; fi < fans.Count; fi++)
        {
            string title = fans.Count > 1 ? $"风扇 {fi + 1}" : "风扇";
            list.Add(new OsdHardwareDef { Id = $"fan:{fi}", Title = title, Kind = DeviceKind.Fan, Parameters = new List<ReadingSpec> { fans[fi] } });
        }

        return list;
    }

    public static OsdHardwareEntry? EntryFor(AppSettings settings, string id) =>
        settings.OsdHardware.FirstOrDefault(e => e.Id == id);

    public static OsdHardwareEntry EnsureEntry(AppSettings settings, string id, IEnumerable<ReadingSpec> allParams)
    {
        var entry = EntryFor(settings, id);
        if (entry != null) return entry;
        entry = new OsdHardwareEntry { Id = id, Enabled = true, Parameters = allParams.Select(p => p.Id).ToList() };
        settings.OsdHardware.Add(entry);
        return entry;
    }

    /// <summary>硬件模块是否启用（尚未保存过配置时默认启用）。</summary>
    public static bool HardwareEnabled(AppSettings settings, string id) =>
        EntryFor(settings, id)?.Enabled ?? true;

    /// <summary>参数是否启用（尚未保存过配置时默认全部启用）。</summary>
    public static bool ParameterEnabled(AppSettings settings, string id, string paramId)
    {
        var entry = EntryFor(settings, id);
        return entry == null || entry.Parameters.Contains(paramId);
    }

    public static string ParameterLabel(ReadingSpec spec)
    {
        if (spec.Device == DeviceKind.Fan)
            return string.IsNullOrEmpty(spec.Name) ? "转速" : spec.Name;
        return string.IsNullOrEmpty(spec.Name) ? spec.Metric.ToString() : spec.Name;
    }
}

/// <summary>OSD 视觉辅助：强调色与硬件/指标命名。</summary>
public static class OsdModules
{
    // 高对比亮色系：在压暗的背景板上依然醒目，且不刺眼
    public static Brush AccentOf(DeviceKind kind) => kind switch
    {
        DeviceKind.Cpu => Make("#8CC6FF"),
        DeviceKind.Gpu => Make("#D2ACFF"),
        DeviceKind.Memory => Make("#5CF0B4"),
        DeviceKind.Disk => Make("#FFDB7D"),
        _ => Make("#6FE6F2"),
    };

    /// <summary>是否使用实际硬件型号作为名称（否则用 CPU / GPU 这类通用名称）。</summary>
    public static bool UseModelName => App.Settings.OsdTitleMode != "Generic";

    /// <summary>CPU 行的显示名称：型号模式下取精简型号（Ryzen 7 7735H），否则 “CPU”。</summary>
    public static string CpuRowTitle(string fullName)
    {
        if (!UseModelName) return "CPU";
        string s = ShortenCpu(fullName);
        return s.Length == 0 ? "CPU" : s;
    }

    /// <summary>GPU 行的显示名称：型号模式下取精简型号（RTX 4060），否则 “GPU”/“GPU2”。</summary>
    public static string GpuRowTitle(string fullName, int index, int total)
    {
        if (!UseModelName) return total <= 1 ? "GPU" : $"GPU{index + 1}";
        string s = ShortenGpu(fullName);
        if (s.Length == 0) s = total <= 1 ? "GPU" : $"GPU{index + 1}";
        return s;
    }

    /// <summary>把厂商全名压成可读短名，过长才截断。</summary>
    private static string ShortenCpu(string fullName)
    {
        string s = fullName
            .Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "")
            .Replace("AMD ", "").Replace("Intel ", "")
            .Replace("with Radeon Graphics", "").Replace(" with ", " ")
            .Trim();
        // Intel 型号常带 "CPU @ 2.60GHz"
        int at = s.IndexOf(" CPU", StringComparison.OrdinalIgnoreCase);
        if (at > 0) s = s[..at].Trim();
        if (s.Length > 18) s = s[..18].TrimEnd();
        return s;
    }

    private static string ShortenGpu(string fullName)
    {
        string s = fullName
            .Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "")
            .Replace("AMD Radeon(TM) Graphics", "核显").Replace("AMD Radeon(TM) ", "")
            .Replace("AMD Radeon Graphics", "核显").Replace("AMD Radeon ", "").Replace("AMD ", "")
            .Replace("Intel(R) ", "").Replace("Intel ", "")
            .Replace(" Laptop GPU", "").Replace(" with Radeon Graphics", "").Trim();
        if (s.Length > 16) s = s[..16].TrimEnd();
        return s;
    }

    private static SolidColorBrush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
