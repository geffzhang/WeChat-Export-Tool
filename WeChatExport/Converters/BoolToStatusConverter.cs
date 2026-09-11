using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace WeChatExport.Converters;

public class BoolToStatusConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isConnected)
        {
            return isConnected ? "Connected" : "Not Connected";
        }
        return "Not Connected";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
