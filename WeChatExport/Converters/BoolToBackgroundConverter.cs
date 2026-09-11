using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace WeChatExport.Converters;

public class BoolToBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isFromSelf)
        {
            return isFromSelf ? new SolidColorBrush(Color.Parse("#E3F2FD")) : new SolidColorBrush(Colors.White);
        }
        return new SolidColorBrush(Colors.White);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
