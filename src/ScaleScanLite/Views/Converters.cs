using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ScaleScanLite.Core.Workflow;

namespace ScaleScanLite.Views;

/// <summary>true → xanh (ổn), false → đỏ.</summary>
internal sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Frozen("#22C55E");
    private static readonly Brush Bad = Frozen("#EF4444");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Ok : Bad;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

internal sealed class NoticeLevelBrushConverter : IValueConverter
{
    private static readonly Brush Info = StatusBrushConverter.Frozen("#4F6EF7");
    private static readonly Brush Success = StatusBrushConverter.Frozen("#16A34A");
    private static readonly Brush Warning = StatusBrushConverter.Frozen("#D97706");
    private static readonly Brush Error = StatusBrushConverter.Frozen("#DC2626");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is NoticeLevel level
            ? level switch
            {
                NoticeLevel.Success => Success,
                NoticeLevel.Warning => Warning,
                NoticeLevel.Error => Error,
                _ => Info
            }
            : Info;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

internal sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Hiện chữ khi chuỗi không rỗng.</summary>
internal sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
