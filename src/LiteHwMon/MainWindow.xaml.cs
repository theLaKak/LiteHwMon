using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using LiteHwMon.Core;
using LiteHwMon.Ui;

namespace LiteHwMon;

public partial class MainWindow : Window
{
    private readonly MonitorEngine _engine;
    private readonly MainVM _vm = new();
    private readonly List<MetricVM> _allMetrics = new();
    private CardVM? _fanCard;
    private MetricVM? _cpuTempMetric;
    private MetricKind _metric = MetricKind.Temperature;
    private bool _engineStarted;
    private bool _driverChipSet;
    private int _lastFanRowCount = -1;
    private bool _servicesReleased;
    private bool _trayHintShown;
    private bool _pawnIoInstalling;
    private int _uiUpdateQueued;

    private readonly DispatcherTimer _selfStatsTimer;
    private readonly Process _self = Process.GetCurrentProcess();
    private double _lastCpuTotal;
    private DateTime _lastWall = DateTime.UtcNow;
    private OsdWindow? _osd;

    private TaskbarIcon TrayIcon => (TaskbarIcon)FindResource("TrayIcon");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        Width = App.Settings.WindowWidth;
        Height = App.Settings.WindowHeight;

        _vm.ElevChip = App.IsElevated ? "管理员" : "普通权限";
        if (!App.IsElevated)
        {
            ElevChipBorder.Visibility = Visibility.Visible;
            if (App.ElevatedLaunchDeclined) ElevateBanner.Visibility = Visibility.Visible;
        }

        _engine = new MonitorEngine();
        _engine.HardwareReady += () => Dispatcher.BeginInvoke(BuildCards);
        _engine.PollCompleted += OnPollCompleted;

        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;

        _selfStatsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _selfStatsTimer.Tick += (_, _) => UpdateSelfStats();
    }

    // ---------------------------------------------------------------- 启动

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var item in IntervalBox.Items.Cast<ComboBoxItem>())
            if ((string)item.Tag == App.Settings.IntervalMs.ToString()) { item.IsSelected = true; break; }
        IntervalBox.SelectionChanged += Interval_SelectionChanged;

        // 选中默认页签与时间窗口（触发对应事件重建图例）
        var tab = ItemsHost(_metric.ToString());
        if (tab != null) tab.IsChecked = true;
        var win = FindWindowRadio(App.Settings.WindowMinutes);
        if (win != null) win.IsChecked = true;
        _vm.LegendHeader = $"序列（点击隐藏/显示） · 统计窗口 近 {App.Settings.WindowMinutes} 分钟";

        if (!_engineStarted)
        {
            _engineStarted = true;
            _engine.Start(App.Settings.IntervalMs);
        }
        _selfStatsTimer.Start();
        UpdateSelfStats();

        if (App.Settings.OsdVisible)
        {
            _osd = new OsdWindow(_engine);
            _osd.Show();
        }

        // 调试/演示：--osd-settings 直接打开 OSD 设置窗口
        if (Environment.GetCommandLineArgs().Contains("--osd-settings"))
        {
            _osd ??= new OsdWindow(_engine);
            _osd.Show();
            new OsdSettingsWindow(_osd).Show();
        }
    }

    private RadioButton? ItemsHost(string tag)
    {
        // 指标页签在右上卡片的第一行 StackPanel 内
        if (Chart.Parent is Grid g &&
            g.Children[0] is StackPanel sp)
            return sp.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == tag);
        return null;
    }

    private RadioButton? FindWindowRadio(int minutes)
    {
        if (Chart.Parent is Grid g && g.Children.OfType<StackPanel>().LastOrDefault() is StackPanel sp)
            return sp.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == minutes.ToString());
        return null;
    }

    // ---------------------------------------------------------------- 卡片构建

    private void BuildCards()
    {
        _vm.Cards.Clear();
        _allMetrics.Clear();
        _vm.AllSeries.Clear();
        _fanCard = null;
        _lastFanRowCount = -1;

        var specs = _engine.Specs;
        int color = 0;

        // CPU
        var cpu = new CardVM { Title = "处理器", Subtitle = _engine.CpuName };
        AddOrdered(cpu, specs.Where(s => s.Device == DeviceKind.Cpu),
            ["温度", "占用率", "频率", "功耗"], ref color);
        _vm.Cards.Add(cpu);

        // GPU：按硬件分组（FallbackIndex2 = 硬件序号，与 NVML 设备号解耦）
        int gi = 0;
        foreach (var name in _engine.GpuNames)
        {
            var card = new CardVM { Title = GpuTitle(name), Subtitle = "显卡" };
            AddOrdered(card, specs.Where(s => s.Device == DeviceKind.Gpu && s.FallbackIndex2 == gi),
                ["温度", "占用率", "核心频率", "功耗", "显存占用", "显存占用率"], ref color);
            _vm.Cards.Add(card);
            gi++;
        }

        // 内存
        var mem = new CardVM { Title = "内存", Subtitle = _engine.Sys.MemoryDetailText };
        AddOrdered(mem, specs.Where(s => s.Device == DeviceKind.Memory),
            ["已用", "可用", "占用率", "频率"], ref color);
        _vm.Cards.Add(mem);

        // 硬盘：按物理磁盘分组（构建顺序即磁盘顺序）
        var diskGroups = specs.Where(s => s.Device == DeviceKind.Disk)
                              .GroupBy(s => s.FallbackIndex).ToList();
        for (int d = 0; d < diskGroups.Count; d++)
        {
            var grp = diskGroups[d];
            string model = d < _engine.DiskNames.Count ? _engine.DiskNames[d] : $"磁盘 {grp.Key}";
            string size = _engine.Sys.DiskSizeTexts.TryGetValue(grp.Key, out var sz) ? $" · {sz}" : "";
            var card = new CardVM { Title = $"磁盘 {grp.Key}", Subtitle = model + size };
            AddOrdered(card, grp, ["温度", "活动率", "读取速度", "写入速度", "已用空间"], ref color);
            _vm.Cards.Add(card);
        }
        if (diskGroups.Count == 0)
            _vm.Cards.Add(new CardVM { Title = "硬盘", Subtitle = "未检测到" });

        // 风扇
        _fanCard = new CardVM { Title = "风扇", Subtitle = "" };
        _vm.Cards.Add(_fanCard);
        RebuildFanRows();

        _vm.StatusText = "监控中";
        if (!_driverChipSet)
        {
            _vm.DriverChip = "驱动：" + _engine.DriverStatus;
            _driverChipSet = true;
        }
        BuildLegend();
        Chart.Refresh();
        _osd?.BuildRows();
    }

    private static string GpuTitle(string name)
    {
        if (name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase))
            return "AMD 核显";
        string s = name
            .Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "")
            .Replace("AMD Radeon(TM) ", "").Replace("AMD Radeon ", "").Replace("AMD ", "")
            .Replace("Intel(R) ", "").Replace("Intel ", "");
        return s.Length > 0 ? s : name;
    }

    private void AddOrdered(CardVM card, IEnumerable<ReadingSpec> specs, string[] order, ref int color)
    {
        var list = specs.ToList();
        foreach (var label in order)
            foreach (var spec in list.Where(s => s.Name == label))
                AddMetric(card, spec, ref color);
        foreach (var spec in list.Where(s => !order.Contains(s.Name)))
            AddMetric(card, spec, ref color);
    }

    private void AddMetric(CardVM card, ReadingSpec spec, ref int color)
    {
        var vm = new MetricVM { Spec = spec, Label = spec.Name };
        vm.UpdateFromSpec();
        card.Metrics.Add(vm);
        _allMetrics.Add(vm);
        if (spec.Device == DeviceKind.Cpu && spec.Metric == MetricKind.Temperature) _cpuTempMetric = vm;

        var (line, fill) = SeriesPalette.Get(color++);
        _vm.AllSeries.Add(new SeriesVM { Spec = spec, Brush = line, FillBrush = fill });
    }

    /// <summary>
    /// CPU 温度不可用时，在磁贴上给出**原因**（悬停可见），
    /// 明确区分「缺 PawnIO（可安装解决）」与「平台不提供该传感器（无法解决）」。
    /// </summary>
    private void UpdateCpuTempHint()
    {
        if (_cpuTempMetric == null) return;
        if (_cpuTempMetric.Available)
        {
            _cpuTempMetric.Hint = null;
            return;
        }

        string? hint = null;
        if (_engine.CpuTempNeedsPawnIo)
            hint = "未检测到 PawnIO 驱动。\n" +
                   "LibreHardwareMonitor 0.9.5 起通过 PawnIO 访问硬件，AMD 的 CPU 温度（Tctl/Tdie）无法用其它方式读取。\n" +
                   "点击主窗口提示条上的「安装 PawnIO」自动安装，安装后需重启本程序。";
        else if (_engine.CpuTelemetrySuspect)
            hint = "已读到该平台的温度读数，但自检发现它不可信：\n" + _engine.CpuTelemetryEvidence +
                   "\n因此不显示该数值。CPU 占用率 / 频率 / 功耗不受影响。";
        else if (_engine.CpuTempUnsupported)
            hint = "PawnIO 已在位，驱动可用，但该平台/主板不暴露 CPU 温度传感器。\n" +
                   "这属于硬件与固件限制，安装任何组件都无法解决。";

        _cpuTempMetric.Hint = hint;
    }

    /// <summary>风扇行随数据出现/消失而重建（未接风扇的引脚不显示）。</summary>
    private void RebuildFanRows()
    {
        if (_fanCard == null) return;
        var visible = _engine.Specs.Where(s => s.Device == DeviceKind.Fan && !s.Hidden).ToList();
        var any = _engine.Specs.Where(s => s.Device == DeviceKind.Fan).ToList();
        if (visible.Count == _lastFanRowCount && visible.Count > 0) return;
        _lastFanRowCount = visible.Count;

        _fanCard.Metrics.Clear();
        int color = _allMetrics.Count;
        if (visible.Count == 0)
        {
            if (any.Count > 0)
            {
                // 有风扇传感器但都没转速 → 明确显示不可用
                if (_vm.AllSeries.All(s => s.Spec != any[0]))
                {
                    var (line, fill) = SeriesPalette.Get(color);
                    _vm.AllSeries.Add(new SeriesVM { Spec = any[0], Brush = line, FillBrush = fill });
                }
                var vm = new MetricVM { Spec = any[0], Label = "风扇" };
                vm.UpdateFromSpec();
                _fanCard.Metrics.Add(vm);
            }
            _fanCard.Subtitle = "未检测到转速数据";
        }
        else
        {
            foreach (var spec in visible)
            {
                if (_vm.AllSeries.All(s => s.Spec != spec))
                {
                    var (line, fill) = SeriesPalette.Get(color++);
                    _vm.AllSeries.Add(new SeriesVM { Spec = spec, Brush = line, FillBrush = fill });
                }
                var vm = new MetricVM { Spec = spec, Label = spec.Name };
                vm.UpdateFromSpec();
                _fanCard.Metrics.Add(vm);
            }
            _fanCard.Subtitle = "主板 / SuperIO 传感器";
        }
    }

    // ---------------------------------------------------------------- 每轮刷新

    /// <summary>
    /// 采样线程回调：BeginInvoke 不会合并请求，采样比 UI 快时会积压，
    /// 因此用一个标志保证同一时刻只排一次 UI 更新。
    /// </summary>
    private void OnPollCompleted()
    {
        if (Interlocked.CompareExchange(ref _uiUpdateQueued, 1, 0) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Volatile.Write(ref _uiUpdateQueued, 0);
            UpdateUi();
        }));
    }

    private void UpdateUi()
    {
        if (!IsVisible && !(_osd?.IsVisible ?? false)) return; // 主窗口与 OSD 都隐藏时不做 UI 更新，数据采集照常

        foreach (var m in _allMetrics) m.UpdateFromSpec();
        RebuildFanRows();

        long now = Environment.TickCount64;
        long win = (long)(_vm.WindowSeconds * 1000);
        foreach (var s in _vm.Legend) s.UpdateStats(now, win);
        if (IsVisible) Chart.Refresh();
        _osd?.RefreshValues();

        var chip = "驱动：" + _engine.DriverStatus;
        if (chip != _vm.DriverChip) _vm.DriverChip = chip;

        UpdateCpuTempHint();
        UpdateHintBanners();
    }

    // ---------------------------------------------------------------- 图例 / 页签 / 窗口

    private void BuildLegend()
    {
        _vm.Legend.Clear();
        foreach (var s in _vm.AllSeries.Where(s => s.Spec.Metric == _metric && !s.Spec.Hidden))
            _vm.Legend.Add(s);

        long now = Environment.TickCount64;
        long win = (long)(_vm.WindowSeconds * 1000);
        foreach (var s in _vm.Legend) s.UpdateStats(now, win);
    }

    private void MetricTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } &&
            Enum.TryParse(tag, out MetricKind kind))
        {
            _metric = kind;
            App.Settings.MetricTab = tag;
            BuildLegend();
            Chart.Refresh();
        }
    }

    private void Window_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int min))
        {
            _vm.WindowSeconds = min * 60;
            _vm.LegendHeader = $"序列（点击隐藏/显示） · 统计窗口 近 {min} 分钟";
            App.Settings.WindowMinutes = min;
        }
    }

    private void LegendRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SeriesVM s })
        {
            s.Visible = !s.Visible;
            Chart.Refresh();
        }
    }

    private void Interval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int ms))
        {
            App.Settings.IntervalMs = ms;
            _engine.SetInterval(ms);
        }
    }

    // ---------------------------------------------------------------- 窗口行为

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => App.RequestExit(confirm: true);

    private void BtnOsd_Click(object sender, RoutedEventArgs e) => ToggleOsd();

    /// <summary>OSD 悬浮窗开关（标题栏按钮与托盘菜单共用）。</summary>
    private void ToggleOsd()
    {
        if (App.IsExiting) return;
        if (_osd == null) _osd = new OsdWindow(_engine);
        if (_osd.IsVisible)
        {
            _osd.Close(); // Close 会保存位置；重新打开时重建窗口
            _osd = null;
            App.Settings.OsdVisible = false;
        }
        else
        {
            _osd.ApplySettings();
            _osd.Show();
            App.Settings.OsdVisible = true;
        }
        App.Settings.Save();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // 最小化一律转入系统托盘：进程继续在后台采样，不结束进程
        if (App.IsExiting) return;
        if (WindowState != WindowState.Minimized) return;

        Hide();
        TrimWorkingSet();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            try
            {
                TrayIcon.ShowBalloonTip("LiteHwMon 仍在后台运行",
                    "双击托盘图标可恢复主窗口，右键托盘图标可完全退出应用。",
                    BalloonIcon.Info);
            }
            catch { /* 系统禁用通知气泡时忽略 */ }
        }
    }

    // ---------------------------------------------------------------- 托盘

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void TrayOsd_Click(object sender, RoutedEventArgs e) => ToggleOsd();

    private void TrayOsdLock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        App.Settings.OsdLocked = item.IsChecked;
        App.Settings.Save();
        _osd?.ApplyLock();
    }

    private void TrayOsdSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_osd == null || !_osd.IsVisible) ToggleOsd(); // 设置窗口需要一个 OSD 实例
        if (_osd?.IsVisible == true) _osd.OpenSettings();
    }

    private void TrayElevate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        App.Settings.Elevate = item.IsChecked;
        App.Settings.Save();
        _vm.StatusText = item.IsChecked ? "监控中（下次启动会请求管理员权限）" : "监控中（下次启动不再请求提权）";
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e) => App.RequestExit(confirm: true);

    /// <summary>右键菜单弹出前同步勾选状态。</summary>
    private void TrayIcon_MenuOpen(object sender, RoutedEventArgs e)
    {
        if (TrayIcon.ContextMenu == null) return;
        foreach (var item in TrayIcon.ContextMenu.Items.OfType<MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "osd": item.IsChecked = _osd?.IsVisible == true; break;
                case "osdlock": item.IsChecked = App.Settings.OsdLocked; break;
                case "elevate": item.IsChecked = App.Settings.Elevate; break;
            }
        }
    }

    private void RestoreFromTray()
    {
        if (App.IsExiting) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        try { Topmost = true; Topmost = false; } catch { } // 从托盘/其他实例唤起时确保窗口真的到前台
    }

    private void BannerElevate_Click(object sender, RoutedEventArgs e)
    {
        if (App.TryRelaunchElevated()) App.RequestExit(confirm: false);
    }

    private void BannerPawnIo_Click(object sender, RoutedEventArgs e) => OpenUrl(PrivilegedBackend.PawnIoHomeUrl);

    /// <summary>手动安装步骤（自动安装被拦截、或需要离线安装时使用）。</summary>
    private void BannerPawnIoSteps_Click(object sender, RoutedEventArgs e)
    {
        const string steps =
            "CPU 温度与风扇转速依赖 PawnIO 驱动（LibreHardwareMonitor 0.9.5 起不再支持 WinRing0 的硬件访问）。\n\n" +
            "手动安装步骤：\n" +
            "1. 打开下载页：\n" +
            "   https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe\n" +
            "   （发布页：https://pawnio.eu/）\n" +
            "2. 运行下载到的 PawnIO_setup.exe，按向导完成安装（需要管理员权限）。\n" +
            "3. 关闭并重新启动本程序 —— LibreHardwareMonitor 在启动时才连接该驱动。\n\n" +
            "安装后仍未显示温度时，说明该平台不暴露 CPU 温度传感器，\n" +
            "此时状态栏会显示「该平台不提供 CPU 温度传感器」，与缺少 PawnIO 是两种不同情况。\n\n" +
            "本程序不会安装任何未签名的驱动；自动安装会先校验 Authenticode 签名与签名者。";

        if (PawnIoBanner.IsVisible)
            MessageBox.Show(this, steps, "PawnIO 安装说明", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(steps, "PawnIO 安装说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 一键安装 PawnIO：后台线程下载 → WinVerifyTrust 验签 → 以 -install -silent 安装。
    /// 全程给出可见进度；失败时把原因写在提示条上，不会静默失败。
    /// </summary>
    private void BannerPawnIoInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_pawnIoInstalling) return;
        _pawnIoInstalling = true;
        PawnIoInstallBtn.IsEnabled = false;
        PawnIoInstallBtn.Content = "准备中…";
        PawnIoBannerText.Text = "正在准备安装 PawnIO…";

        var progress = new Progress<string>(msg =>
        {
            PawnIoInstallBtn.Content = msg;
            PawnIoBannerText.Text = msg;
        });

        Task.Run(() =>
        {
            var result = PawnIoInstaller.Install(progress);
            Dispatcher.BeginInvoke(new Action(() => OnPawnIoInstallFinished(result)));
        });
    }

    private void OnPawnIoInstallFinished(PawnIoInstaller.Result result)
    {
        _pawnIoInstalling = false;
        PawnIoInstallBtn.IsEnabled = true;
        PawnIoBannerText.Text = result.Message;

        if (!result.Ok)
        {
            PawnIoInstallBtn.Content = "重试安装 →";
            return;
        }

        // 安装成功后 LHM 需要重新打开后端才能拿到 CPU 温度
        PawnIoInstallBtn.Content = "立即重启 →";
        try { PawnIoInstallBtn.Click -= BannerPawnIoInstall_Click; } catch { }
        PawnIoInstallBtn.Click += BannerRestart_Click;
    }

    private void BannerRestart_Click(object sender, RoutedEventArgs e)
    {
        if (App.TryRelaunchElevated()) App.RequestExit(confirm: false);
        else App.RequestExit(confirm: true);
    }

    private void BannerPawnIoHide_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.ShowPawnIoHint = false;
        App.Settings.Save();
        PawnIoBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>两条提示互斥显示，避免信息堆叠；只在状态变化时切换可见性。</summary>
    private void UpdateHintBanners()
    {
        // 未提权提示优先级更高：先解决权限，再谈 PawnIO
        bool showElevate = !App.IsElevated && App.ElevatedLaunchDeclined;
        bool showPawnIo = !showElevate
                          && App.Settings.ShowPawnIoHint
                          && _engine.CpuTempNeedsPawnIo;

        if (showElevate != (ElevateBanner.Visibility == Visibility.Visible))
            ElevateBanner.Visibility = showElevate ? Visibility.Visible : Visibility.Collapsed;
        if (showPawnIo != (PawnIoBanner.Visibility == Visibility.Visible))
            PawnIoBanner.Visibility = showPawnIo ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* 没有默认浏览器时忽略 */ }
    }

    /// <summary>
    /// 退出流程中的资源释放（幂等）：窗口位置/尺寸持久化、自身统计定时器、
    /// 监控引擎后台线程、托盘图标、OSD 悬浮窗。
    /// </summary>
    public void ShutdownServices()
    {
        if (_servicesReleased) return;
        _servicesReleased = true;

        var bounds = RestoreBounds;
        App.Settings.WindowWidth = bounds.Width;
        App.Settings.WindowHeight = bounds.Height;
        App.Settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            App.Settings.WindowLeft = Left;
            App.Settings.WindowTop = Top;
        }
        else if (!double.IsNaN(bounds.Left))
        {
            App.Settings.WindowLeft = bounds.Left;
            App.Settings.WindowTop = bounds.Top;
        }
        App.Settings.Save();

        try { _selfStatsTimer.Stop(); } catch { }
        try { _osd?.Close(); } catch { }
        _osd = null;
        try { TrayIcon.Dispose(); } catch { } // 先摘掉托盘图标，避免退出后残留幽灵图标
        try { _engine.Dispose(); } catch { }  // 停轮询定时器 + 后台释放 LHM 驱动句柄
        try { _self.Dispose(); } catch { }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.IsExiting) return; // 统一退出流程正在关闭窗口：放行

        // 不在 Closing 内直接 Shutdown（会重入），先取消关闭，再交给统一退出流程确认
        e.Cancel = true;
        Dispatcher.BeginInvoke(new Action(() => App.RequestExit(confirm: true)));
    }

    private void UpdateSelfStats()
    {
        try
        {
            double wall = (DateTime.UtcNow - _lastWall).TotalSeconds;
            // 首次采样、以及系统休眠/挂起恢复后间隔异常：只重置基线，不报一个假的 0%
            if (wall < 0.5 || wall > 10)
            {
                _self.Refresh();
                _lastCpuTotal = _self.TotalProcessorTime.TotalSeconds;
                _lastWall = DateTime.UtcNow;
                return;
            }

            _self.Refresh();
            double cpu = (_self.TotalProcessorTime.TotalSeconds - _lastCpuTotal) / wall / Environment.ProcessorCount * 100;
            _lastCpuTotal = _self.TotalProcessorTime.TotalSeconds;
            _lastWall = DateTime.UtcNow;
            _vm.SelfStats = $"本程序 CPU {Math.Clamp(cpu, 0, 100):0.0}% · 内存 {_self.WorkingSet64 / 1024.0 / 1024:0} MB";
        }
        catch { }
    }

    private static void TrimWorkingSet()
    {
        try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1); } catch { }
    }

    // ---------------------------------------------------------------- 最大化修复 / 圆角

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = (HwndSource)PresentationSource.FromVisual(this)!;
        src.AddHook(WndProc);

        // 恢复上次位置（若仍在当前虚拟桌面范围内），否则居中于主屏工作区
        if (!App.Settings.WindowMaximized)
        {
            try
            {
                if (TryRestorePosition()) { /* 已恢复到记忆位置 */ }
                else
                {
                    var wa = SystemParameters.WorkArea;
                    Left = wa.Left + Math.Max(0, (wa.Width - ActualWidth) / 2);
                    Top = wa.Top + Math.Max(0, (wa.Height - ActualHeight) / 2);
                }
            }
            catch { }
        }
        if (App.Settings.WindowMaximized) WindowState = WindowState.Maximized;
        TryRoundCorners(src.Handle);
    }

    /// <summary>把上次保存的窗口位置恢复回来；位置已不在当前桌面内则返回 false（改为居中）。</summary>
    private bool TryRestorePosition()
    {
        if (App.Settings.WindowLeft is not { } left || App.Settings.WindowTop is not { } top) return false;
        if (double.IsNaN(left) || double.IsNaN(top)) return false;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;

        double scale = 1.0;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
            scale = ct.TransformFromDevice.M11;
        if (scale <= 0 || double.IsNaN(scale)) return false;

        // GetSystemMetrics 为物理像素，换算成 DIU 后再判断是否可见
        double vaL = GetSystemMetrics(SM_XVIRTUALSCREEN) * scale;
        double vaT = GetSystemMetrics(SM_YVIRTUALSCREEN) * scale;
        double vaR = vaL + GetSystemMetrics(SM_CXVIRTUALSCREEN) * scale;
        double vaB = vaT + GetSystemMetrics(SM_CYVIRTUALSCREEN) * scale;

        // 至少要有 120 DIU 落在桌面内，避免显示器拔掉后窗口跑到看不见的地方
        const double minVisible = 120;
        if (left + w < vaL + minVisible || left > vaR - minVisible) return false;
        if (top + h < vaT + minVisible || top > vaB - minVisible) return false;

        Left = Math.Clamp(left, vaL, Math.Max(vaL, vaR - w));
        Top = Math.Clamp(top, vaT, Math.Max(vaT, vaB - h));
        return true;
    }

    private static void TryRoundCorners(IntPtr hwnd)
    {
        try
        {
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        }
        catch { /* Win10 无此 API，忽略 */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 另一个实例启动时投递的“显示窗口”请求：从托盘/后台恢复到前台
        if (msg == SingleInstanceGuard.RestoreMessageId)
        {
            RestoreFromTray();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == 0x0024) // WM_GETMINMAXINFO：无框窗口最大化时不要盖住任务栏
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, 1 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    double scale = 1;
                    if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
                        scale = ct.TransformToDevice.M11;
                    int pad = (int)Math.Ceiling(6 * scale); // 与 ResizeBorderThickness 一致
                    mmi.ptMaxPosition.x = info.rcWork.left - pad;
                    mmi.ptMaxPosition.y = info.rcWork.top - pad;
                    mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left + 2 * pad;
                    mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top + 2 * pad;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);
}
