using System.IO;
using System.Text.Json;

namespace LiteHwMon.Core;

/// <summary>OSD 中一个硬件模块的启用状态与其勾选的参数（参数以 ReadingSpec.Id 持久化）。</summary>
public sealed class OsdHardwareEntry
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> Parameters { get; set; } = new();
}

public sealed class AppSettings
{
    public int IntervalMs { get; set; } = 1000;
    /// <summary>启动时自动请求管理员权限（温度/功耗/风扇需要内核驱动）。</summary>
    public bool Elevate { get; set; } = true;
    public string MetricTab { get; set; } = nameof(MetricKind.Temperature);
    public int WindowMinutes { get; set; } = 5;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }
    /// <summary>主窗口位置（DIU）；null 表示尚未记录，启动时居中。</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    /// <summary>显示“CPU 温度需要 PawnIO 驱动”的提示条（用户点“不再提示”后关闭）。</summary>
    public bool ShowPawnIoHint { get; set; } = true;

    // ---- 游戏 OSD 悬浮窗 ----
    public bool OsdVisible { get; set; } = false;
    public bool OsdTopmost { get; set; } = true;
    public double OsdLeft { get; set; } = 60;
    public double OsdTop { get; set; } = 60;
    /// <summary>背景板不透明度 0–1（不作用于文字：文字与数值始终 100% 亮度）。</summary>
    public double OsdOpacity { get; set; } = 0.78;
    /// <summary>基础字号（DIU）。</summary>
    public double OsdFontSize { get; set; } = 15;
    /// <summary>硬件名称字号倍率（独立于指标字号）。</summary>
    public double OsdTitleScale { get; set; } = 1.0;
    /// <summary>指标数值字号倍率（独立于名称字号）。</summary>
    public double OsdValueScale { get; set; } = 1.0;
    /// <summary>行 / 模块组间距（DIU）。</summary>
    public double OsdRowSpacing { get; set; } = 7;
    /// <summary>指标单元格间距（DIU）。</summary>
    public double OsdCellSpacing { get; set; } = 12;
    /// <summary>排列方式：Vertical 竖排分行 / Horizontal 横排单行 / Wrap 横排自动换行。</summary>
    public string OsdLayout { get; set; } = "Vertical";
    /// <summary>显示硬件名称列。</summary>
    public bool OsdShowTitle { get; set; } = true;
    /// <summary>显示指标前缀单字（温/占/频…）。</summary>
    public bool OsdShowLabels { get; set; } = true;
    /// <summary>文字描边（深色光晕），复杂游戏画面上提升可读性。</summary>
    public bool OsdTextShadow { get; set; } = true;
    /// <summary>背景板深浅 0（近黑，对比最强）–1（石板灰）。</summary>
    public double OsdBackingLevel { get; set; } = 0.40;
    /// <summary>硬件 + 参数两级配置（键为硬件 Id，如 cpu/gpu:0/memory/disk:0/fan:0）。</summary>
    public List<OsdHardwareEntry> OsdHardware { get; set; } = new();

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteHwMon");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 允许 NaN/Infinity 字面量：否则任何 NaN 字段都会让整次序列化抛异常，
        // 被下面的 catch 吞掉后表现为“配置永远保存不上”。
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch { /* 配置损坏时用默认值 */ }
        return new AppSettings();
    }

    /// <summary>把数值收进 UI 允许的范围，避免手改/损坏的配置文件让界面进入无选项可选的状态。</summary>
    private void Normalize()
    {
        IntervalMs = Math.Clamp(IntervalMs, 250, 60000);
        WindowMinutes = WindowMinutes is 1 or 5 or 10 or 30 ? WindowMinutes : 5;
        if (OsdLayout is not ("Vertical" or "Horizontal" or "Wrap")) OsdLayout = "Vertical";
        if (Enum.TryParse<MetricKind>(MetricTab, out _) == false) MetricTab = nameof(MetricKind.Temperature);
        OsdOpacity = Math.Clamp(OsdOpacity, 0, 1);
        OsdBackingLevel = Math.Clamp(OsdBackingLevel, 0, 1);
        OsdFontSize = Math.Clamp(OsdFontSize, 11, 20);
        OsdTitleScale = Math.Clamp(OsdTitleScale, 0.6, 2);
        OsdValueScale = Math.Clamp(OsdValueScale, 0.6, 2);
        OsdRowSpacing = Math.Clamp(OsdRowSpacing, 0, 20);
        OsdCellSpacing = Math.Clamp(OsdCellSpacing, 2, 28);
        WindowWidth = Math.Clamp(WindowWidth, 1020, 10000);
        WindowHeight = Math.Clamp(WindowHeight, 660, 10000);
    }

    private readonly object _saveLock = new();

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                // 先写临时文件再原子替换：退出看门狗会调用 Environment.Exit，
                // 直接 WriteAllText 有被截断的风险，而截断后的配置会被当成“损坏”而整份回退默认值。
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { /* 保存失败不影响运行 */ }
        }
    }
}
