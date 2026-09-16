using System.Globalization;
using System.Text.RegularExpressions;
using AndroidManager.Core.Models;

namespace AndroidManager.Device.Parsing;

public static partial class DeviceOutputParsers
{
    public static BatteryInfo ParseBattery(string dumpsysBatteryOutput)
    {
        return new BatteryInfo
        {
            Level = ParseIntAfterKey(dumpsysBatteryOutput, "level:"),
            Voltage = ParseIntAfterKey(dumpsysBatteryOutput, "voltage:"),
            Temperature = ParseIntAfterKey(dumpsysBatteryOutput, "temperature:") / 10.0,
            IsCharging = dumpsysBatteryOutput.Contains("AC powered: true", StringComparison.OrdinalIgnoreCase)
                         || dumpsysBatteryOutput.Contains("USB powered: true", StringComparison.OrdinalIgnoreCase)
                         || dumpsysBatteryOutput.Contains("Wireless powered: true", StringComparison.OrdinalIgnoreCase),
            Health = MapBatteryHealth(ParseStringAfterKey(dumpsysBatteryOutput, "health:")),
            ChargeCounter = ParseIntAfterKey(dumpsysBatteryOutput, "Charge counter:"),
            Technology = ParseStringAfterKey(dumpsysBatteryOutput, "technology:"),
            Status = MapBatteryStatus(ParseStringAfterKey(dumpsysBatteryOutput, "status:"))
        };
    }

    public static string? ParseIpv4Address(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var match = Ipv4Regex().Match(output);
        return match.Success ? match.Value : null;
    }

    public static int ParseCycleCount(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return 0;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value is > 0 and < 100_000)
                return value;
        }

        return 0;
    }

    public static IReadOnlyList<string> ParseSensors(string sensorserviceOutput)
    {
        if (string.IsNullOrWhiteSpace(sensorserviceOutput))
            return [];

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SensorNameRegex().Matches(sensorserviceOutput))
        {
            var name = match.Groups[1].Value.Trim();
            if (name.Length is > 1 and < 80)
                names.Add(name);
        }

        foreach (Match match in AndroidSensorTypeRegex().Matches(sensorserviceOutput))
            names.Add(match.Value.Trim());

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(40).ToList();
    }

    private static string MapBatteryHealth(string raw) => raw switch
    {
        "2" or "Good" => "İyi",
        "3" or "Overheat" => "Aşırı sıcak",
        "4" or "Dead" => "Ölü",
        "5" or "Over voltage" => "Aşırı voltaj",
        "6" or "Unspecified failure" => "Belirsiz hata",
        "7" or "Cold" => "Soğuk",
        _ => string.IsNullOrWhiteSpace(raw) ? "—" : raw
    };

    private static string MapBatteryStatus(string raw) => raw switch
    {
        "2" or "Charging" => "Şarj oluyor",
        "3" or "Discharging" => "Boşalıyor",
        "4" or "Not charging" => "Şarj olmuyor",
        "5" or "Full" => "Dolu",
        "1" or "Unknown" => "Bilinmiyor",
        _ => string.IsNullOrWhiteSpace(raw) ? "—" : raw
    };

    public static StorageInfo ParseDf(string dfOutput)
    {
        var lines = dfOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return new StorageInfo();

        // Prefer the data line that includes numbers; skip header.
        for (var i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                continue;

            // df columns: Filesystem Size Used Available Use% Mounted
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
                continue;
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used))
                continue;
            if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var free))
                continue;

            return new StorageInfo
            {
                TotalKb = total,
                UsedKb = used,
                FreeKb = free
            };
        }

        return new StorageInfo();
    }

    public static RamInfo ParseMemInfo(string memInfoOutput)
    {
        return new RamInfo
        {
            TotalKb = ParseMemInfoValue(memInfoOutput, "MemTotal:"),
            FreeKb = ParseMemInfoValue(memInfoOutput, "MemFree:"),
            AvailableKb = ParseMemInfoValue(memInfoOutput, "MemAvailable:")
        };
    }

    public static double ParseCpuUsage(string topOutput)
    {
        var cpuLine = topOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.Contains("%cpu", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("CPU:", StringComparison.OrdinalIgnoreCase));
        if (cpuLine is null)
            return 0;

        var totalMatch = TotalCpuRegex().Match(cpuLine);
        var idleMatch = IdlePercentRegex().Match(cpuLine);
        if (totalMatch.Success
            && idleMatch.Success
            && double.TryParse(totalMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total)
            && double.TryParse(idleMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var idle)
            && total > 0)
        {
            return Math.Clamp((total - idle) / total * 100.0, 0, 100);
        }

        if (idleMatch.Success
            && double.TryParse(idleMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out idle))
        {
            return Math.Clamp(100 - idle, 0, 100);
        }

        var usageMatch = UsagePercentRegex().Match(cpuLine);
        if (usageMatch.Success
            && double.TryParse(usageMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var usage))
        {
            return Math.Clamp(usage, 0, 100);
        }

        return 0;
    }

    private static long ParseMemInfoValue(string text, string key)
    {
        var match = Regex.Match(text, $@"{Regex.Escape(key)}\s+(\d+)", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    public static int ParseIntAfterKey(string text, string key)
    {
        var value = ParseStringAfterKey(text, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static string ParseStringAfterKey(string text, string key)
    {
        var idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;
        var remainder = text[(idx + key.Length)..];
        var lineEnd = remainder.IndexOfAny(['\r', '\n']);
        if (lineEnd >= 0)
            remainder = remainder[..lineEnd];
        return remainder.Trim();
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%\s*cpu", RegexOptions.IgnoreCase)]
    private static partial Regex TotalCpuRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%\s*idle", RegexOptions.IgnoreCase)]
    private static partial Regex IdlePercentRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%", RegexOptions.IgnoreCase)]
    private static partial Regex UsagePercentRegex();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\b")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"(?:name|StringType)\s*[=:]\s*""?([^""\r\n,}]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex SensorNameRegex();

    [GeneratedRegex(@"android\.sensor\.[a-z0-9_.]+", RegexOptions.IgnoreCase)]
    private static partial Regex AndroidSensorTypeRegex();
}
