using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AndroidManager.Core.Models;

namespace AndroidManager.Device.Converters;

public sealed class LogcatLevelBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Verbose = Brush("#9E9E9E");
    private static readonly SolidColorBrush Debug = Brush("#1976D2");
    private static readonly SolidColorBrush Info = Brush("#2E7D32");
    private static readonly SolidColorBrush Warn = Brush("#F57C00");
    private static readonly SolidColorBrush Error = Brush("#C62828");
    private static readonly SolidColorBrush Fatal = Brush("#6A1B9A");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            LogcatLevel.Verbose => Verbose,
            LogcatLevel.Debug => Debug,
            LogcatLevel.Info => Info,
            LogcatLevel.Warn => Warn,
            LogcatLevel.Error => Error,
            LogcatLevel.Fatal => Fatal,
            _ => Verbose
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        brush.Freeze();
        return brush;
    }
}
