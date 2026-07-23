using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace VoiceBoothKiosk.Config.Controls;

public sealed class ContrastTextBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value?.ToString() ?? string.Empty)!;
            var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;
            return luminance > 0.5
                ? new SolidColorBrush(Color.FromRgb(15, 29, 48))
                : Brushes.White;
        }
        catch
        {
            return Brushes.White;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
