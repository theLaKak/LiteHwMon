using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LiteHwMon.Core;
using LiteHwMon.Ui;

namespace LiteHwMon;

/// <summary>
/// OSD 设置：硬件/参数两级勾选 + 布局排列 + 独立字号 + 外观 + 九宫格定位；
/// 所有更改实时生效；滑杆拖动结束后统一持久化。
/// </summary>
public partial class OsdSettingsWindow : Window
{
    private readonly OsdWindow _osd;
    // 初始为 true：XAML 解析时 Slider 的 ValueChanged 会先于 LoadSettings 触发
    private bool _loading = true;

    /// <summary>硬盘活动率的说明（与主窗口磁贴上的 ? 共用同一份文案）。</summary>
    private const string DiskActivityTip = MetricHelp.DiskActivity;

    public OsdSettingsWindow(OsdWindow osd)
    {
        InitializeComponent();
        _osd = osd;
        Loaded += (_, _) => LoadSettings();
        // 首次打开时硬件可能还没枚举完（目录为空），枚举完成后补上勾选项
        _osd.CatalogChanged += OnCatalogChanged;
        Closed += (_, _) =>
        {
            _osd.CatalogChanged -= OnCatalogChanged;
            App.Settings.Save();
        };
    }

    private void OnCatalogChanged()
    {
        if (HardwareHost.Children.Count == 0) LoadSettings();
    }

    private void LoadSettings()
    {
        if (_osd.Catalog.Count == 0) _osd.BuildRows();

        _loading = true;
        HardwareHost.Children.Clear();
        var s = App.Settings;

        foreach (var def in _osd.Catalog)
        {
            var group = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

            var entry = OsdCatalog.EntryFor(s, def.Id);
            bool moduleOn = entry?.Enabled ?? true;

            var moduleCheck = new CheckBox
            {
                Content = def.Title,
                Style = (Style)FindResource("Chk"),
                FontWeight = FontWeights.SemiBold,
                Tag = def.Id,
                IsChecked = moduleOn,
            };
            moduleCheck.Checked += ModuleCheck_Changed;
            moduleCheck.Unchecked += ModuleCheck_Changed;
            group.Children.Add(moduleCheck);

            var metricsPanel = new WrapPanel { Margin = new Thickness(18, 0, 0, 4) };
            foreach (var p in def.Parameters)
            {
                bool paramOn = entry == null || entry.Parameters.Contains(p.Id);
                var chk = new CheckBox
                {
                    Content = OsdCatalog.ParameterLabel(p),
                    Style = (Style)FindResource("Chk"),
                    Tag = def.Id + "|" + p.Id,
                    IsEnabled = moduleOn,
                    IsChecked = paramOn,
                };
                chk.Checked += MetricCheck_Changed;
                chk.Unchecked += MetricCheck_Changed;
                metricsPanel.Children.Add(chk);

                // 硬盘活动率语义容易误解，旁边给一个 ? 说明
                if (p.Device == DeviceKind.Disk && p.Metric == MetricKind.Load)
                    metricsPanel.Children.Add(MakeInfoIcon(DiskActivityTip));
            }
            group.Children.Add(metricsPanel);

            HardwareHost.Children.Add(new Border { Style = (Style)FindResource("GroupBorder"), Child = group });
        }

        foreach (ComboBoxItem item in LayoutCombo.Items)
            if ((string)item.Tag == s.OsdLayout) { item.IsSelected = true; break; }
        foreach (ComboBoxItem item in TitleModeCombo.Items)
            if ((string)item.Tag == s.OsdTitleMode) { item.IsSelected = true; break; }
        RowSpacingSlider.Value = s.OsdRowSpacing;
        CellSpacingSlider.Value = s.OsdCellSpacing;
        ShowTitleCheck.IsChecked = s.OsdShowTitle;
        ShowLabelsCheck.IsChecked = s.OsdShowLabels;

        FontSlider.Value = s.OsdFontSize;
        TitleScaleSlider.Value = s.OsdTitleScale;
        ValueScaleSlider.Value = s.OsdValueScale;

        OpacitySlider.Value = s.OsdOpacity;
        BackingSlider.Value = s.OsdBackingLevel;
        ShadowCheck.IsChecked = s.OsdTextShadow;
        TopmostCheck.IsChecked = s.OsdTopmost;
        _loading = false;
    }

    // ---------------------------------------------------------------- 显示内容

    private void ModuleCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not CheckBox { Tag: string id } chk) return;

        var def = _osd.Catalog.FirstOrDefault(d => d.Id == id);
        if (def == null) return;

        bool on = chk.IsChecked == true;
        var entry = OsdCatalog.EnsureEntry(App.Settings, id, def.Parameters);
        entry.Enabled = on;
        if (on && entry.Parameters.Count == 0)
            entry.Parameters = def.Parameters.Select(p => p.Id).ToList();

        RefreshMetricEnabled(id, on);
        ApplyAndSave();
    }

    private void MetricCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not CheckBox { Tag: string tag } chk) return;

        string id = tag.Split('|')[0];
        string paramId = tag.Split('|')[1];
        var def = _osd.Catalog.FirstOrDefault(d => d.Id == id);
        if (def == null) return;

        var entry = OsdCatalog.EnsureEntry(App.Settings, id, def.Parameters);
        if (chk.IsChecked == true)
        {
            if (!entry.Parameters.Contains(paramId)) entry.Parameters.Add(paramId);
        }
        else
        {
            entry.Parameters.Remove(paramId);
        }
        ApplyAndSave();
    }

    private void RefreshMetricEnabled(string key, bool enabled)
    {
        _loading = true;
        foreach (var border in HardwareHost.Children.OfType<Border>())
        {
            if (border.Child is not StackPanel group ||
                group.Children[0] is not CheckBox { Tag: string id } moduleCheck ||
                id != key) continue;

            var list = OsdCatalog.EntryFor(App.Settings, key)?.Parameters ?? new List<string>();
            foreach (var chk in ((WrapPanel)group.Children[1]).Children.OfType<CheckBox>())
            {
                chk.IsEnabled = enabled;
                string paramId = ((string)chk.Tag).Split('|')[1];
                chk.IsChecked = enabled && list.Contains(paramId);
            }
        }
        _loading = false;
    }

    // ---------------------------------------------------------------- 布局

    private void Layout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (LayoutCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            App.Settings.OsdLayout = tag;
            ApplyAndSave();
        }
    }

    private void RowSpacing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        App.Settings.OsdRowSpacing = Math.Clamp(RowSpacingSlider.Value, 0, 20);
        RowSpacingValue.Text = $"{App.Settings.OsdRowSpacing:0}";
        _osd.ApplySettings();
    }

    private void CellSpacing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        App.Settings.OsdCellSpacing = Math.Clamp(CellSpacingSlider.Value, 2, 28);
        CellSpacingValue.Text = $"{App.Settings.OsdCellSpacing:0}";
        _osd.ApplySettings();
    }

    private void ShowTitle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.OsdShowTitle = ShowTitleCheck.IsChecked == true;
        ApplyAndSave();
    }

    private void ShowLabels_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.OsdShowLabels = ShowLabelsCheck.IsChecked == true;
        ApplyAndSave();
    }

    private void TitleMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (TitleModeCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            App.Settings.OsdTitleMode = tag;
            ApplyAndSave(); // 名称模式影响行内容，需要重建
        }
    }

    /// <summary>指标旁的 ? 信息图标：悬停显示说明，不参与勾选，也不影响 OSD 本身的鼠标交互。</summary>
    private FrameworkElement MakeInfoIcon(string tip)
    {
        var mark = new TextBlock
        {
            Text = "?",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = TryBrush("TextDimBrush", Brushes.Gray),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // HelpText 写到 TextBlock 上：UIA 只暴露 TextBlock（Border 没有自动化对等体），
        // 这样读屏软件与自动化都能读到完整说明
        System.Windows.Automation.AutomationProperties.SetHelpText(mark, tip);
        System.Windows.Automation.AutomationProperties.SetName(mark, "硬盘活动率说明");
        var icon = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = TryBrush("LineBrush", Brushes.Gray),
            Background = TryBrush("ChipBrush", Brushes.Transparent),
            Margin = new Thickness(2, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Help,
            Child = mark,
            // 用 ToolTip 对象以便控制换行宽度
            ToolTip = new ToolTip { Content = tip, MaxWidth = 380 },
        };
        return icon;
    }

    private static Brush TryBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    // ---------------------------------------------------------------- 字号

    private void Font_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        App.Settings.OsdFontSize = Math.Clamp(FontSlider.Value, 11, 20);
        FontValue.Text = $"{App.Settings.OsdFontSize:0}";
        _osd.ApplySettings();
    }

    private void TitleScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        App.Settings.OsdTitleScale = Math.Clamp(TitleScaleSlider.Value, 0.6, 2.0);
        TitleScaleValue.Text = $"×{App.Settings.OsdTitleScale:0.00}";
        _osd.ApplySettings();
    }

    private void ValueScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        App.Settings.OsdValueScale = Math.Clamp(ValueScaleSlider.Value, 0.6, 2.0);
        ValueScaleValue.Text = $"×{App.Settings.OsdValueScale:0.00}";
        _osd.ApplySettings();
    }

    // ---------------------------------------------------------------- 外观

    private void Opacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        App.Settings.OsdOpacity = Math.Clamp(OpacitySlider.Value, 0, 1);
        OpacityValue.Text = $"{App.Settings.OsdOpacity * 100:0}%";
        _osd.ApplyBacking(); // 只重画背景板：不重建行，拖动才跟手
    }

    private void Backing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        App.Settings.OsdBackingLevel = Math.Clamp(BackingSlider.Value, 0, 1);
        BackingValue.Text = $"{App.Settings.OsdBackingLevel * 100:0}%";
        _osd.ApplyBacking();
    }

    private void Shadow_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.OsdTextShadow = ShadowCheck.IsChecked == true;
        _osd.ApplyTextEffect(); // 描边只影响一个 Effect，无需重建行
        App.Settings.Save();
    }

    private void Topmost_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.OsdTopmost = TopmostCheck.IsChecked == true;
        _osd.ApplyTopmost(); // 只改一个窗口属性
        App.Settings.Save();
    }

    // ---------------------------------------------------------------- 位置与持久化

    private void Pos_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var parts = tag.Split(',');
        _osd.MoveToAnchor(int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        App.Settings.Save();
    }

    private void ApplyAndSave()
    {
        _osd.ApplySettings();
        App.Settings.Save();
    }
}
