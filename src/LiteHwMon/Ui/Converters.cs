using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LiteHwMon.Ui;

/// <summary>bool → 透明度：隐藏的序列在图例中变暗但仍可点击恢复。</summary>
public sealed class BoolToOpacity : IValueConverter
{
    public static readonly BoolToOpacity Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.4;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → Visibility：只区分显示/隐藏，不占用布局空间。</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public static readonly BoolToVisibility Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
