using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NetRelay.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ConnectedBrush = new(Color.FromRgb(39, 181, 136));
    private static readonly SolidColorBrush DisconnectedBrush = new(Color.FromRgb(133, 145, 163));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isConnected = value switch
        {
            bool boolean => boolean,
            int count => count > 0,
            _ => false
        };
        return isConnected ? ConnectedBrush : DisconnectedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
