using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LiteHwMon.Core;

namespace LiteHwMon.Ui;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>卡片上的一个数值磁贴。</summary>
public sealed class MetricVM : ObservableObject
{
    public required ReadingSpec Spec { get; init; }

    private string _valueText = "—";
    private string _unit = "";
    private string? _hint;
    private bool _available;

    public string Label { get; init; } = "";

    /// <summary>鼠标悬停说明；null 表示不显示提示（例如数值正常时）。</summary>
    public string? Hint { get => _hint; set { if (_hint == value) return; _hint = value; Raise(); } }

    // 赋值前比较：数值没变就不发通知，避免每秒对每个磁贴重复触发 WPF 绑定更新
    public string ValueText { get => _valueText; private set { if (_valueText == value) return; _valueText = value; Raise(); } }
    public string Unit { get => _unit; private set { if (_unit == value) return; _unit = value; Raise(); } }
    public bool Available { get => _available; private set { if (_available == value) return; _available = value; Raise(); } }

    public void UpdateFromSpec()
    {
        if (Spec.Value is { } v)
        {
            var (num, unit) = Spec.FormatParts(v);
            ValueText = num;
            Unit = unit;
            Available = true;
        }
        else
        {
            ValueText = "不可用";
            Unit = "";
            Available = false;
        }
    }
}

/// <summary>左侧设备卡片。</summary>
public sealed class CardVM
{
    public required string Title { get; init; }
    public string Subtitle { get; set; } = "";
    public ObservableCollection<MetricVM> Metrics { get; } = new();
}

/// <summary>曲线图例中的一条序列。</summary>
public sealed class SeriesVM : ObservableObject
{
    public required ReadingSpec Spec { get; init; }
    public Brush Brush { get; init; } = Brushes.Gray;

    private bool _visible = true;
    private string _cur = "—", _min = "—", _max = "—", _avg = "—";
    private bool _available;

    public string Name => Spec.LegendName;
    /// <summary>曲线上色用的低透明度刷子。</summary>
    public Brush FillBrush { get; init; } = Brushes.Transparent;

    public bool Visible { get => _visible; set { _visible = value; Raise(); } }
    // 同理：数值没变就不发通知，静态项（如内存频率）不再每秒触发一次重排
    public string Cur { get => _cur; private set { if (_cur == value) return; _cur = value; Raise(); } }
    public string Min { get => _min; private set { if (_min == value) return; _min = value; Raise(); } }
    public string Max { get => _max; private set { if (_max == value) return; _max = value; Raise(); } }
    public string Avg { get => _avg; private set { if (_avg == value) return; _avg = value; Raise(); } }
    public bool Available { get => _available; private set { if (_available == value) return; _available = value; Raise(); } }

    /// <summary>该序列自己的采样暂存区，避免每次重绘分配。</summary>
    internal readonly long[] TickBuf = new long[ReadingSpec.HistoryCapacity];
    internal readonly float[] ValBuf = new float[ReadingSpec.HistoryCapacity];

    public void UpdateStats(long nowMs, long windowMs)
    {
        var (n, min, max, sum) = Spec.History.Stats(nowMs, windowMs);
        Available = n > 0 && Spec.Value != null;
        if (n == 0)
        {
            Cur = Min = Max = Avg = Spec.Value == null ? "不可用" : "—";
        }
        else
        {
            Cur = Spec.FormatShort(Spec.Value!.Value);
            Min = Spec.FormatShort(min);
            Max = Spec.FormatShort(max);
            Avg = Spec.FormatShort((float)(sum / n));
        }
    }
}

/// <summary>主窗口视图模型。</summary>
public sealed class MainVM : ObservableObject
{
    public ObservableCollection<CardVM> Cards { get; } = new();
    public ObservableCollection<SeriesVM> Legend { get; } = new();
    public List<SeriesVM> AllSeries { get; } = new();

    private string _statusText = "正在初始化硬件…";
    private string _driverChip = "驱动：…";
    private string _elevChip = "";
    private string _selfStats = "";
    private string _legendHeader = "序列（点击隐藏/显示）";
    private double _windowSeconds = 300;

    public string StatusText { get => _statusText; set { _statusText = value; Raise(); } }
    public string DriverChip { get => _driverChip; set { _driverChip = value; Raise(); } }
    public string ElevChip { get => _elevChip; set { _elevChip = value; Raise(); } }
    public string SelfStats { get => _selfStats; set { _selfStats = value; Raise(); } }
    /// <summary>图例表头：明确 min/max/avg 的统计窗口，避免误读。</summary>
    public string LegendHeader { get => _legendHeader; set { _legendHeader = value; Raise(); } }
    public double WindowSeconds { get => _windowSeconds; set { _windowSeconds = value; Raise(); } }
}

public static class SeriesPalette
{
    private static readonly Color[] Colors =
    [
        Color.FromRgb(0x4C, 0x8D, 0xFF),
        Color.FromRgb(0x2E, 0xD2, 0x8F),
        Color.FromRgb(0xFF, 0xB0, 0x20),
        Color.FromRgb(0xFF, 0x7A, 0x6B),
        Color.FromRgb(0xA0, 0x6B, 0xFF),
        Color.FromRgb(0x3E, 0xD7, 0xD0),
        Color.FromRgb(0xE8, 0x63, 0x9B),
        Color.FromRgb(0x7B, 0x8C, 0xDE),
    ];

    public static (Brush Line, Brush Fill) Get(int index)
    {
        var c = Colors[index % Colors.Length];
        var line = new SolidColorBrush(c);
        line.Freeze();
        var fill = new SolidColorBrush(Color.FromArgb(26, c.R, c.G, c.B));
        fill.Freeze();
        return (line, fill);
    }
}
