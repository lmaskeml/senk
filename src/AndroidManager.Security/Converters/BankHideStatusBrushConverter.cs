using System.Windows.Data;
using System.Globalization;
using AndroidManager.Core.Models;

namespace AndroidManager.Security.Converters;

public sealed class BankHideStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not BankHideCheckStatus s)
            return "#9E9E9E";

        return s switch
        {
            BankHideCheckStatus.Pass => "#4CAF50",
            BankHideCheckStatus.Fail => "#F44336",
            BankHideCheckStatus.Warn => "#FF9800",
            _ => "#9E9E9E"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
