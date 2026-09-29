using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LiteHwMon.Core;

namespace LiteHwMon.Ui;

/// <summary>
/// 轻量实时曲线：自绘 FrameworkElement，无第三方图表库。
/// 每次重绘把各序列窗口内数据拷入预分配缓冲区，构建 StreamGeometry 绘制。
/// </summary>
public sealed class SensorChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesSourceProperty = DependencyProperty.Register(
        nameof(SeriesSource), typeof(System.Collections.IEnumerable), typeof(SensorChart),
        new PropertyMetadata(null, (_, _) => { }));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(double), typeof(SensorChart),
        new PropertyMetadata(300.0, (_, _) => { }));

    public System.Collections.IEnumerable? SeriesSource
    {
        get => (System.Collections.IEnumerable?)GetValue(SeriesSourceProperty);
        set => SetValue(SeriesSourceProperty, value);
    }

    public double WindowSeconds
    {
        get => (double)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    private static readonly Typeface TypeFace = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TypeFaceBold = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Brush GridBrush = MakeBrush(Color.FromArgb(0x2E, 0x8E, 0x93, 0x9C));
    private static readonly Brush GridTextBrush = MakeBrush(Color.FromRgb(0x8E, 0x93, 0x9C));
    private static readonly Brush EmptyTextBrush = MakeBrush(Color.FromRgb(0x6A, 0x6F, 0x78));

    private static SolidColorBrush MakeBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public SensorChart() => Loaded += (_, _) => InvalidateVisual();

    public void Refresh() => InvalidateVisual();

    private const double MarginLeft = 52;
    private const double MarginRight = 10;
    private const double MarginTop = 10;
    private const double MarginBottom = 24;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 60) return;

        long now = Environment.TickCount64;
        long win = (long)Math.Max(1, WindowSeconds * 1000);
        PrepareDpi();
        var series = SeriesSource?.OfType<SeriesVM>().Where(s => s.Visible).ToList();

        double plotW = w - MarginLeft - MarginRight;
        double plotH = h - MarginTop - MarginBottom;
        var plotRect = new Rect(MarginLeft, MarginTop, plotW, plotH);

        // ---- 收集窗口内数据并求值域 ----
        var plots = new List<(SeriesVM Vm, int N)>();
        float dMin = float.MaxValue, dMax = float.MinValue;
        bool any = false;
        if (series != null)
        {
            foreach (var vm in series)
            {
                int n = vm.Spec.History.CopyWindow(now, win, vm.TickBuf, vm.ValBuf);
                if (n >= 2)
                {
                    plots.Add((vm, n));
                    for (int i = 0; i < n; i++)
                    {
                        float v = vm.ValBuf[i];
                        if (float.IsNaN(v)) continue;
                        if (v < dMin) dMin = v;
                        if (v > dMax) dMax = v;
                        any = true;
                    }
                }
            }
        }

        if (!any)
        {
            var t = Fmt("暂无数据", 13, EmptyTextBrush, TypeFace);
            dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
            return;
        }

        // ---- 值域 ----
        double yMin, yMax;
        if (string.Equals(series![0].Spec.Name, "占用率", StringComparison.Ordinal) ||
            series[0].Spec.Metric == MetricKind.Load)
        {
            yMin = 0;
            yMax = 100;
        }
        else
        {
            double pad = (dMax - dMin) * 0.12;
            if (pad <= 0) pad = Math.Abs(dMax) * 0.1 + 1;
            yMin = dMin - pad;
            yMax = dMax + pad;
        }

        double Y(float v) => plotRect.Top + plotH * (1 - (v - yMin) / (yMax - yMin));
        double X(long t) => plotRect.Right - plotW * Math.Clamp((double)(now - t) / win, 0, 1);

        // ---- 网格与 Y 轴刻度 ----
        const int gridLines = 5;
        for (int i = 0; i <= gridLines; i++)
        {
            double frac = (double)i / gridLines;
            double y = plotRect.Top + plotH * frac;
            dc.DrawLine(new Pen(GridBrush, 1), new Point(plotRect.Left, y), new Point(plotRect.Right, y));
            double val = yMax - (yMax - yMin) * frac;
            string label = TickLabel(val, yMax - yMin);
            var ft = Fmt(label, 10, GridTextBrush, TypeFace);
            dc.DrawText(ft, new Point(plotRect.Left - ft.Width - 6, y - ft.Height / 2));
        }

        // ---- X 轴时间刻度 ----
        var xPen = new Pen(GridBrush, 1);
        bool useMinutes = win >= 60000; // 单位统一，避免 5 分钟窗口里混进一个 "-50s"
        for (int i = 0; i <= 6; i++)
        {
            double frac = (double)i / 6;
            double x = plotRect.Right - plotW * frac;
            long ago = (long)(win * frac);
            string label = ago == 0 ? "现在"
                         : useMinutes ? $"-{ago / 60000.0:0.#}m"
                         : $"-{ago / 1000.0:0.#}s";
            var ft = Fmt(label, 10, GridTextBrush, TypeFace);
            if (i > 0) dc.DrawLine(xPen, new Point(x, plotRect.Bottom), new Point(x, plotRect.Top));
            dc.DrawText(ft, new Point(x - ft.Width / 2, plotRect.Bottom + 5));
        }

        // ---- 各序列 ----
        var penCache = new Dictionary<SeriesVM, Pen>();
        foreach (var (vm, n) in plots)
        {
            var geo = new StreamGeometry();
            var fill = new StreamGeometry();
            double firstX = 0, firstY = 0, lastX = 0, lastY = 0;
            using (var ctx = geo.Open())
            using (var fctx = fill.Open())
            {
                bool started = false;
                for (int i = 0; i < n; i++)
                {
                    float v = vm.ValBuf[i];
                    if (float.IsNaN(v)) continue;
                    double x = X(vm.TickBuf[i]);
                    double y = Y(Math.Clamp(v, (float)yMin, (float)yMax));
                    if (!started) { ctx.BeginFigure(new Point(x, y), false, false); started = true; firstX = x; firstY = y; }
                    else ctx.LineTo(new Point(x, y), true, false);
                    lastX = x;
                    lastY = y;
                }
                if (started)
                {
                    fctx.BeginFigure(new Point(firstX, plotRect.Bottom), true, true);
                    fctx.LineTo(new Point(firstX, firstY), true, false);
                    fctx.LineTo(new Point(lastX, lastY), true, false);
                    fctx.LineTo(new Point(lastX, plotRect.Bottom), true, false);
                }
            }
            geo.Freeze();
            fill.Freeze();

            if (!penCache.TryGetValue(vm, out var pen))
            {
                pen = new Pen(vm.Brush, 1.7);
                pen.Freeze();
                penCache[vm] = pen;
            }
            dc.DrawGeometry(vm.FillBrush, null, fill);
            dc.DrawGeometry(null, pen, geo);

            // 末端当前值点
            dc.DrawEllipse(vm.Brush, null, new Point(lastX, lastY), 3, 3);
        }

        // ---- 图表外框 ----
        dc.DrawRectangle(null, new Pen(GridBrush, 1), plotRect);
    }

    private static string TickLabel(double v, double range)
    {
        if (range >= 100000) return $"{v / 1000:0.#}k";
        if (range >= 1000) return $"{v:0}";
        if (range >= 100) return $"{v:0}";
        return $"{v:0.#}";
    }

    private FormattedText Fmt(string s, double size, Brush brush, Typeface face) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, _dpi);

    private double _dpi = 1.0;

    /// <summary>渲染入口统一取一次 DPI（避免每次格式化文本都查询）。</summary>
    private void PrepareDpi()
    {
        try { _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch { _dpi = 1.0; }
    }
}
