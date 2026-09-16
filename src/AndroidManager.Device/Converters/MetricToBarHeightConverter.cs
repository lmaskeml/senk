using System.Globalization;
using System.Windows.Data;

namespace AndroidManager.Device.Converters;

/// <summary>Maps 0–100 (or 0–80 for temp) to bar height in px.</summary>
public sealed class MetricToBarHeightConverter : IValueConverter
{
    public double MaxHeight { get; set; } = 36;
    public double MaxValue { get; set; } = 100;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var maxH = MaxHeight;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            maxH = p;

        var v = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0d
        };

        var ratio = MaxValue <= 0 ? 0 : Math.Clamp(v / MaxValue, 0, 1);
        return Math.Max(2, ratio * maxH);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
