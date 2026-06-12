using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NetRelay.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ConnectedBrush = new(System.Windows.Media.Color.FromRgb(39, 181, 136));
    private static readonly SolidColorBrush DisconnectedBrush = new(System.Windows.Media.Color.FromRgb(133, 145, 163));
    private static readonly SolidColorBrush WarningBrush = new(System.Windows.Media.Color.FromRgb(213, 155, 45));
    private static readonly SolidColorBrush FailedBrush = new(System.Windows.Media.Color.FromRgb(232, 92, 99));
    private static readonly SolidColorBrush PendingBrush = new(System.Windows.Media.Color.FromRgb(83, 110, 242));

    private static readonly SolidColorBrush EnableActionBrush = new(System.Windows.Media.Color.FromRgb(83, 110, 242)); // Blue
    private static readonly SolidColorBrush DisableActionBrush = new(System.Windows.Media.Color.FromRgb(232, 92, 99)); // Red

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is string paramStr && paramStr == "ActionBrush")
        {
            return value is true ? DisableActionBrush : EnableActionBrush;
        }

        if (value is string statusLabel)
        {
            return statusLabel switch
            {
                "可联网" or "可访问互联网" or "链路正常" or "已启用" => ConnectedBrush,
                "联网失败" or "联网探测失败" => FailedBrush,
                "未检测" or "等待检测" => PendingBrush,
                "仅本地" or "仅本地网络" => DisconnectedBrush,
                "链路断开" => WarningBrush,
                _ => DisconnectedBrush
            };
        }

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
