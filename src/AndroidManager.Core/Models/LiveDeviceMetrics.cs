namespace AndroidManager.Core.Models;

public sealed class LiveDeviceMetrics
{
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public double CpuPercent { get; init; }
    public double RamPercent { get; init; }
    public long RamTotalKb { get; init; }
    public long RamAvailableKb { get; init; }
    public int BatteryPercent { get; init; }
    public double TemperatureC { get; init; }
    public bool IsCharging { get; init; }
    public int VoltageMv { get; init; }
    public string WifiIp { get; init; } = "";
    public string WifiSsid { get; init; } = "";
    public int? WifiRssi { get; init; }
    public string SecurityPatch { get; init; } = "";
    public string BootloaderLocked { get; init; } = "";

    public string RamUsedFormatted
    {
        get
        {
            var usedKb = Math.Max(0, RamTotalKb - RamAvailableKb);
            return FormatSize(usedKb * 1024);
        }
    }

    public string RamTotalFormatted => FormatSize(RamTotalKb * 1024);

    public string WifiRssiText => WifiRssi is int r ? $"{r} dBm" : "—";

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes <= 0) return "—";
        return $"{bytes / 1024.0:F1} KB";
    }
}

public sealed class MetricSample
{
    public DateTime At { get; init; }
    public double Value { get; init; }
}
