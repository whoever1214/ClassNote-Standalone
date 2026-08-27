using System.Globalization;
using System.Windows.Data;

namespace ClassNote.Converters;

/// <summary>Maps server status codes to friendly Chinese labels.</summary>
public class StatusDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string status)
        {
            return status switch
            {
                "recording"  => "录音中",
                "uploaded"   => "处理中",
                "processing" => "处理中",
                "ended"      => "已结束",
                "completed"  => "已完成",
                "failed"     => "失败",
                _            => status,
            };
        }
        return value ?? "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
