using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EstherLink.UI.Converters;

public sealed class BoolToStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isActive = value is bool b && b;
        return isActive
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}