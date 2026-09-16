namespace AndroidManager.Core.Models;

public sealed class DeviceInfo
{
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string AndroidVersion { get; set; } = string.Empty;
    public string ApiLevel { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    public BatteryInfo Battery { get; set; } = new();
    public StorageInfo Storage { get; set; } = new();
    public RamInfo Ram { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
}

public sealed class BatteryInfo
{
    public int Level { get; set; }
    public int Voltage { get; set; }
    public double Temperature { get; set; }
    public bool IsCharging { get; set; }
    public string Health { get; set; } = string.Empty;
    public int ChargeCounter { get; set; }
    public string Technology { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class StorageInfo
{
    public long TotalKb { get; set; }
    public long UsedKb { get; set; }
    public long FreeKb { get; set; }

    public double UsagePercent => TotalKb > 0 ? UsedKb / (double)TotalKb * 100 : 0;
    public string TotalFormatted => FormatSize(TotalKb * 1024);
    public string UsedFormatted => FormatSize(UsedKb * 1024);
    public string FreeFormatted => FormatSize(FreeKb * 1024);

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        return $"{bytes / 1024.0:F1} KB";
    }
}

public sealed class RamInfo
{
    public long TotalKb { get; set; }
    public long FreeKb { get; set; }
    public long AvailableKb { get; set; }

    public long UsedKb => Math.Max(0, TotalKb - AvailableKb);
    public double UsagePercent => TotalKb > 0 ? UsedKb / (double)TotalKb * 100 : 0;
    public string TotalFormatted => FormatSize(TotalKb * 1024);
    public string UsedFormatted => FormatSize(UsedKb * 1024);
    public string AvailableFormatted => FormatSize(AvailableKb * 1024);

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        return $"{bytes / 1024.0:F1} KB";
    }
}

public sealed class CpuInfo
{
    public double UsagePercent { get; set; }
}
