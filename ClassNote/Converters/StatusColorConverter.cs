using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ClassNote.Converters;

public class StatusColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string status)
        {
            return status switch
            {
                "recording"  => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), // Green 录音中
                "completed"  => new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)), // Blue 已完成
                "failed"     => new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)), // Red 失败
                "uploaded"   => new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)), // Orange 处理中
                "processing" => new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)), // Orange 处理中
                "ended"      => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), // Gray 已结束（终端态）
                _            => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), // Default gray
            };
        }
        return new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
