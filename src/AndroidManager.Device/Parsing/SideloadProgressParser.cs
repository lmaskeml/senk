using System.Globalization;
using System.Text.RegularExpressions;

namespace AndroidManager.Device.Parsing;

public static partial class SideloadProgressParser
{
    [GeneratedRegex(@"(\d{1,3})\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"Total\s+xfer:\s*([\d.]+)\s*x", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TotalXferRegex();

    public static int? TryParsePercent(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var xfer = TotalXferRegex().Match(line);
        if (xfer.Success
            && double.TryParse(xfer.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)
            && ratio >= 0.99)
        {
            return 100;
        }

        var percent = PercentRegex().Match(line);
        if (!percent.Success)
            return null;

        if (!int.TryParse(percent.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return null;

        return Math.Clamp(value, 0, 100);
    }

    public static bool IsSuccessMarker(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.Contains("Total xfer", StringComparison.OrdinalIgnoreCase)
               || line.Contains("sideload complete", StringComparison.OrdinalIgnoreCase)
               || line.Contains("script succeeded", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTransientDeviceError(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.Contains("no devices", StringComparison.OrdinalIgnoreCase)
               || line.Contains("device not found", StringComparison.OrdinalIgnoreCase)
               || line.Contains("offline", StringComparison.OrdinalIgnoreCase)
               || line.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }
}
