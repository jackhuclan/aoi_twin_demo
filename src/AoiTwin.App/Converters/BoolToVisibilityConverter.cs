using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AoiTwin.App.Converters;

/// <summary>布尔 → 可见性；Inverse=True 时取反（空状态/有数据状态成对使用）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        return flag ^ Inverse ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
