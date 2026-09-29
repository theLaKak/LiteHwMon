using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LiteHwMon.Core;
using LiteHwMon.Ui;

namespace LiteHwMon;

/// <summary>
/// 游戏 OSD 悬浮窗：紧凑只读显示，与主窗口共用同一个 MonitorEngine（不新增采样线程）。
/// 支持拖动、置顶、透明度与字体调整；位置与配置持久化到 settings.json。
/// </summary>
public partial class OsdWindow : Window
{
    private readonly MonitorEngine _engine;
    private readonly List<OsdMetricCell> _cells = new();
    private List<OsdHardwareDef> _catalog = new();
    private bool _dragging;
    private bool _rowsBuilt;
    private bool _locked;
    private OsdSettingsWindow? _settingsWindow;

    /// <summary>当前探测到的硬件分组（供设置窗口动态渲染两级选择）。</summary>
    public IReadOnlyList<OsdHardwareDef> Catalog => _catalog;

    /// <summary>硬件目录已（重新）构建完成；设置窗口据此在首次枚举完成后补上勾选项。</summary>
    public event Action? CatalogChanged;

    public OsdWindow(MonitorEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        ShowActivated = false;
        SourceInitialized += (_, _) => { MakeNoActivate(); HookHitTest(); };
        Loaded += (_, _) => ClampToScreen();               // 实际尺寸已知后再钳制一次
        SizeChanged += (_, _) => ClampToScreen();          // 内容/布局变化导致尺寸改变时同步钳制
        ApplySettings();
    }

    // ---------------------------------------------------------------- 显示设置

    /// <summary>文字描边：ShadowDepth=0 时投影变成四周对称的光晕，复杂画面上依然清晰。</summary>
    private static readonly DropShadowEffect TextOutline = new()
    {
        BlurRadius = 4,
        ShadowDepth = 0,
        Direction = 315,
        Opacity = 1.0,
        Color = Colors.Black,
    };

    public void ApplySettings()
    {
        var s = App.Settings;
        Topmost = s.OsdTopmost;
        // 关键：整窗 Opacity 恒为 1，透明只作用于背景板，文字/数值永远是纯亮色
        Opacity = 1.0;
        FontSize = Math.Clamp(s.OsdFontSize, 10, 26);
        ApplyBacking();
        if (s.OsdLeft == 0 && s.OsdTop == 0)
            PlaceDefault();
        else
        {
            Left = s.OsdLeft;
            Top = s.OsdTop;
        }
        ClampToScreen();
        BuildRows();
        ApplyLock();
    }

    /// <summary>
    /// 锁定模式：保持置顶、鼠标点击穿透、禁止拖动。
    /// 解锁后恢复拖动与命中测试。
    /// </summary>
    public void ApplyLock()
    {
        bool locked = App.Settings.OsdLocked;
        LockBtn.Content = locked ? "🔒" : "🔓";
        LockBtn.ToolTip = locked
            ? "已锁定：置顶 + 鼠标点击穿透（点此解锁后恢复拖动）"
            : "锁定 OSD：保持置顶、鼠标点击穿透、禁止拖动";
        if (locked) Topmost = true;                 // 锁定即置顶，与“窗口置顶”设置无关
        else Topmost = App.Settings.OsdTopmost;
        _locked = locked;
        // 锁定后按钮仍可点击（否则无法解锁），其余区域穿透
        LockBtn.Opacity = locked ? 1.0 : 0.9;
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.OsdLocked = !App.Settings.OsdLocked;
        App.Settings.Save();
        ApplyLock();
    }

    /// <summary>
    /// 背景板：深浅（OsdBackingLevel，1 为近黑）决定底色，不透明度（OsdOpacity）只影响背景板本身。
    /// 底色越深、对比越强，文字因此不必依赖背景也能看清。
    /// 不透明度为 0 时连描边一起去掉，成为真正的“纯文字悬浮”。
    /// </summary>
    public void ApplyBacking()
    {
        var s = App.Settings;
        double level = Math.Clamp(s.OsdBackingLevel, 0, 1);
        byte r = Lerp(0x34, 0x07, level);
        byte g = Lerp(0x3C, 0x0A, level);
        byte b = Lerp(0x4B, 0x11, level);

        // 至少保留 8% 底色：分层窗口里完全透明的区域无法命中鼠标，会导致 OSD 拖不动
        double requested = Math.Clamp(s.OsdOpacity, 0, 1);
        double alpha = Math.Clamp(0.08 + 0.92 * requested, 0.08, 1.0);
        byte a = (byte)Math.Round(alpha * 255);

        // 上浅下深的一点点渐变：面板不再是一块死灰，文字区域显得更“透亮”
        byte topBoost = (byte)Math.Round(10 * alpha);
        byte bottomDrop = (byte)Math.Round(8 * alpha);
        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(a, Add(r, topBoost), Add(g, topBoost), Add(b, topBoost)), 0),
                new GradientStop(Color.FromArgb(a, r, g, b), 0.55),
                new GradientStop(Color.FromArgb(a, Sub(r, bottomDrop), Sub(g, bottomDrop), Sub(b, bottomDrop)), 1),
            },
        };
        fill.Freeze();
        Backing.Background = fill;

        // 细亮边：给背景板一个清晰的轮廓，与复杂游戏画面分离；0% 时不画边，避免出现“空框”
        if (requested <= 0.001)
        {
            Backing.BorderThickness = new Thickness(0);
        }
        else
        {
            Backing.BorderThickness = new Thickness(1);
            byte edgeAlpha = (byte)Math.Round(Math.Clamp(0.46 - 0.14 * level, 0.22, 0.46) * (0.35 + 0.65 * alpha) * 255);
            var edge = new SolidColorBrush(Color.FromArgb(edgeAlpha, 0xFF, 0xFF, 0xFF));
            edge.Freeze();
            Backing.BorderBrush = edge;
        }
    }

    private static byte Lerp(int from, int to, double t) =>
        (byte)Math.Round(from + (to - from) * t);

    private static byte Add(byte v, int d) => (byte)Math.Min(255, v + d);
    private static byte Sub(byte v, int d) => (byte)Math.Max(0, v - d);

    /// <summary>只更新文字描边（一个 Effect），不重建行。</summary>
    public void ApplyTextEffect() => RowsHost.Effect = App.Settings.OsdTextShadow ? TextOutline : null;

    /// <summary>只更新置顶状态，不重建行。</summary>
    public void ApplyTopmost() => Topmost = App.Settings.OsdTopmost;


    /// <summary>九宫格快速定位（row/col 均 0..2）。</summary>
    public void MoveToAnchor(int row, int col)
    {
        var wa = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 340;
        double h = ActualHeight > 0 ? ActualHeight : 120;
        Left = wa.Left + Math.Max(0, (wa.Width - w) * col / 2.0);
        Top = wa.Top + Math.Max(0, (wa.Height - h) * row / 2.0);
        ClampToScreen();
        SavePosition();
    }

    /// <summary>默认位置：主窗口右上角外侧；主窗口不可用时落在主屏右上。</summary>
    private void PlaceDefault()
    {
        var main = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (main != null)
        {
            Left = main.Left + Math.Max(0, main.ActualWidth - 330);
            Top = main.Top + 64;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - 340;
            Top = SystemParameters.WorkArea.Top + 60;
        }
    }

    public void SavePosition()
    {
        var s = App.Settings;
        s.OsdLeft = Left;
        s.OsdTop = Top;
        s.Save();
    }

    private void ClampToScreen()
    {
        try
        {
            // 窗口还没接入呈现源时拿不到正确的 DPI 变换，此时钳制会用错比例尺。
            // 直接跳过，交给 Loaded / SizeChanged 再做（那时比例尺已可靠）。
            if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } ct) return;

            double w = ActualWidth > 0 ? ActualWidth : 340;
            double h = ActualHeight > 0 ? ActualHeight : 120;

            double scale = ct.TransformFromDevice.M11; // 物理像素 -> DIU
            if (scale <= 0 || double.IsNaN(scale)) return;

            double vaL = GetSystemMetrics(SM_XVIRTUALSCREEN) * scale;
            double vaT = GetSystemMetrics(SM_YVIRTUALSCREEN) * scale;
            double vaR = vaL + GetSystemMetrics(SM_CXVIRTUALSCREEN) * scale;
            double vaB = vaT + GetSystemMetrics(SM_CYVIRTUALSCREEN) * scale;

            Left = Math.Clamp(Left, vaL, Math.Max(vaL, vaR - w));
            Top = Math.Clamp(Top, vaT, Math.Max(vaT, vaB - h));
        }
        catch { }
    }

    // ---------------------------------------------------------------- 行构建

    /// <summary>按“硬件 + 参数”两级配置与布局设置重建；engine 数据就绪前后都可调用。</summary>
    public void BuildRows()
    {
        if (_engine.Specs.Count == 0) return; // 硬件尚未枚举完成，等首轮 BuildCards 后再建
        _rowsBuilt = true;
        _catalog = OsdCatalog.Build(_engine);
        _cells.Clear();
        RowsHost.Children.Clear();
        var s = App.Settings;

        RowsHost.Effect = s.OsdTextShadow ? TextOutline : null; // 复杂画面上提升可读性

        // 组装启用的硬件组
        var groups = new List<(OsdHardwareDef Def, List<ReadingSpec> Specs)>();
        foreach (var def in _catalog)
        {
            if (!OsdCatalog.HardwareEnabled(s, def.Id)) continue;
            var enabledSpecs = def.Parameters.Where(p => OsdCatalog.ParameterEnabled(s, def.Id, p.Id)).ToList();
            if (enabledSpecs.Count == 0) continue;
            groups.Add((def, enabledSpecs));
        }

        if (groups.Count == 0)
        {
            RowsHost.Children.Add(new TextBlock
            {
                Text = "OSD：请在设置中勾选硬件与参数",
                Foreground = OsdPalette.Hint,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2),
            });
            return;
        }

        switch (s.OsdLayout)
        {
            case "Horizontal":
            case "Wrap":
            {
                var wp = new WrapPanel { Orientation = Orientation.Horizontal };
                if (s.OsdLayout == "Wrap")
                    wp.MaxWidth = Math.Max(360, SystemParameters.WorkArea.Width * 0.66);
                foreach (var (def, specs) in groups)
                    wp.Children.Add(BuildGroup(def, specs, horizontal: true));
                RowsHost.Children.Add(wp);
                break;
            }
            default: // Vertical 竖排分行
            {
                bool first = true;
                foreach (var (def, specs) in groups)
                {
                    var g = BuildGroup(def, specs, horizontal: false);
                    g.Margin = new Thickness(0, first ? 0 : Math.Max(2, s.OsdRowSpacing), 0, 0);
                    first = false;
                    RowsHost.Children.Add(g);
                }
                break;
            }
        }

        // 行内容变化会改变窗口尺寸：布局完成后再钳制一次，避免指标被挤出屏幕外
        Dispatcher.BeginInvoke(new Action(ClampToScreen), DispatcherPriority.Loaded);
        try { CatalogChanged?.Invoke(); } catch { }
    }

    /// <summary>构建一个硬件组：色条 + 名称 + 指标单元格序列。</summary>
    private StackPanel BuildGroup(OsdHardwareDef def, List<ReadingSpec> specs, bool horizontal)
    {
        var s = App.Settings;
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        double valueSize = Math.Max(9, FontSize * Math.Clamp(s.OsdValueScale, 0.6, 2.0));
        double titleSize = Math.Max(9, FontSize * 0.95 * Math.Clamp(s.OsdTitleScale, 0.6, 2.0));
        // 前缀是单字中文，太小就分不清“温/功/频”，下限给足
        double labelSize = Math.Max(10, valueSize * 0.76);

        var bar = new System.Windows.Shapes.Rectangle
        {
            Width = 3.5,
            RadiusX = 1.75,
            RadiusY = 1.75,
            Fill = OsdModules.AccentOf(def.Kind),
            Margin = new Thickness(0, 1.5, 7, 1.5),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        row.Children.Add(bar);

        if (s.OsdShowTitle)
        {
            row.Children.Add(new TextBlock
            {
                Text = def.Title,
                Foreground = OsdModules.AccentOf(def.Kind),
                FontWeight = FontWeights.Bold,
                FontSize = titleSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
            });
        }

        bool firstCell = true;
        foreach (var spec in specs)
        {
            var cell = new OsdMetricCell { Spec = spec, Label = OsdCatalog.ParameterLabel(spec) };
            cell.Refresh();
            _cells.Add(cell);

            var cellPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, firstCell ? Math.Max(2, s.OsdCellSpacing * 0.6) : s.OsdCellSpacing, 0),
            };

            if (s.OsdShowLabels)
            {
                cellPanel.Children.Add(new TextBlock
                {
                    // 完整指标名 + 全角冒号，例如「温度：65°C」；关闭前缀时只显示数值
                    Text = cell.Label + "：",
                    Foreground = OsdPalette.Label,
                    FontSize = labelSize,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 2, 0),
                });
            }

            var valueText = new TextBlock
            {
                DataContext = cell,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                FontSize = valueSize,
            };
            valueText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding
            {
                Path = new System.Windows.PropertyPath(nameof(OsdMetricCell.Text)),
                Source = cell,
                Mode = System.Windows.Data.BindingMode.OneWay,
            });
            valueText.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding
            {
                Path = new System.Windows.PropertyPath(nameof(OsdMetricCell.Foreground)),
                Source = cell,
                Mode = System.Windows.Data.BindingMode.OneWay,
            });
            cellPanel.Children.Add(valueText);
            row.Children.Add(cellPanel);
            firstCell = false;
        }

        if (horizontal)
            row.Margin = new Thickness(0, 0, Math.Max(8, s.OsdRowSpacing * 1.8), Math.Max(2, s.OsdRowSpacing * 0.4));

        return row;
    }

    /// <summary>每轮采样后刷新数值（由主窗口转发调用；隐藏时不做任何事）。</summary>
    public void RefreshValues()
    {
        if (!IsVisible) return;
        if (!_rowsBuilt && _engine.Specs.Count > 0) BuildRows();
        foreach (var c in _cells) c.Refresh();
    }

    // ---------------------------------------------------------------- 交互

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return; // 锁定模式禁止拖动，避免误操作
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            _dragging = true;
            try { DragMove(); } catch { }
            _dragging = false;
            SavePosition();
        }
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragging = false;

    // ---------------------------------------------------------------- 鼠标穿透

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private void HookHitTest()
    {
        try
        {
            if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(WndProc);
        }
        catch { }
    }

    /// <summary>
    /// 锁定模式下让整块 OSD 对鼠标透明（点击穿透到下层游戏/窗口），
    /// 仅保留右上角按钮条可命中 —— 否则无法再点按钮解锁。
    /// 使用 HTTRANSPARENT 而不是 WS_EX_TRANSPARENT，正是为了保留这条例外。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST && _locked)
        {
            long lp = lParam.ToInt64();
            int sx = (short)(lp & 0xFFFF);
            int sy = (short)((lp >> 16) & 0xFFFF);
            if (!IsOverButtonStrip(sx, sy))
            {
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            }
        }
        return IntPtr.Zero;
    }

    private bool IsOverButtonStrip(int screenX, int screenY)
    {
        try
        {
            if (ButtonStrip.ActualWidth <= 0 || ButtonStrip.ActualHeight <= 0) return false;
            var tl = ButtonStrip.PointToScreen(new Point(0, 0));
            const double padX = 8, padY = 6; // 命中余量，方便点中
            return screenX >= tl.X - padX && screenX <= tl.X + ButtonStrip.ActualWidth + padX
                && screenY >= tl.Y - padY && screenY <= tl.Y + ButtonStrip.ActualHeight + padY;
        }
        catch { return false; }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>打开（或激活）OSD 设置窗口。托盘菜单与悬浮窗齿轮共用。</summary>
    public void OpenSettings()
    {
        // 已经打开就激活，避免同时出现两个设置窗口互相覆盖同一份配置
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        // 主窗口在托盘里（隐藏）时不要把它设为 Owner，否则设置窗口会跟着不可见
        var owner = App.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
        var win = new OsdSettingsWindow(this);
        if (owner != null) win.Owner = owner;
        _settingsWindow = win;
        win.Closed += (_, _) => _settingsWindow = null;
        win.Show();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.OsdVisible = false;
        App.Settings.Save();
        Hide();
    }

    private void MakeNoActivate()
    {
        // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW：不抢焦点、不出现在 Alt+Tab，兼容游戏覆盖
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_dragging) SavePosition();
        base.OnClosing(e);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
