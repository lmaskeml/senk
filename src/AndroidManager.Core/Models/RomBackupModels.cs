namespace AndroidManager.Core.Models;

public enum RomBackupMethod
{
    None,
    TwrpDd,
    LiveRoot
}

public enum RomBackupCategory
{
    KernelRecovery,
    RadioImei,
    SuperSystem,
    ExtraFirmware
}

public sealed class BlockDumpResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public long BytesWritten { get; init; }
    public string Sha256 { get; init; } = "";
    public string LocalPath { get; init; } = "";
}

public sealed class DevicePartitionInfo
{
    public string Name { get; init; } = "";
    public string BlockPath { get; init; } = "";
    public string? KernelBlock { get; init; }
    public long SizeBytes { get; init; }
    public RomBackupCategory Category { get; init; }
    public bool IsSlotAlias { get; init; }

    public string SizeFormatted => SizeBytes switch
    {
        <= 0 => "?",
        < 1_048_576 => $"{SizeBytes / 1024.0:F0} KB",
        < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
    };
}

public sealed class RomBackupCapability
{
    public RomBackupMethod Method { get; init; } = RomBackupMethod.None;
    public bool CanBackup => Method is RomBackupMethod.TwrpDd or RomBackupMethod.LiveRoot;
    public bool FastbootAvailable { get; init; }
    public string Codename { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Model { get; init; } = "";
    public string Slot { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class RomBackupRequest
{
    public string OutputDirectory { get; init; } = "";
    public IReadOnlyList<string> PartitionNames { get; init; } = [];
}

public sealed class RomBackupProgress
{
    public string PartitionName { get; init; } = "";
    public int CurrentIndex { get; init; }
    public int TotalCount { get; init; }
    public int Percent { get; init; }
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public string Message { get; init; } = "";
}

public sealed class RomBackupPartitionFile
{
    public string Name { get; init; } = "";
    public string FileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public RomBackupCategory Category { get; init; }
}

public sealed class RomBackupSession
{
    public int Version { get; init; } = 1;
    public string Folder { get; init; } = "";
    public string Codename { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Model { get; init; } = "";
    public string Slot { get; init; } = "";
    public RomBackupMethod Method { get; init; }
    public DateTime CreatedAt { get; init; }
    public IReadOnlyList<RomBackupPartitionFile> Partitions { get; init; } = [];
    public string? Error { get; init; }

    public string DisplayName =>
        $"{CreatedAt:yyyy-MM-dd HH:mm} · {Codename} · {Partitions.Count} bölüm";
}

public sealed class RomBackupRestoreRequest
{
    public string Folder { get; init; } = "";
    public IReadOnlyList<string> PartitionNames { get; init; } = [];
    public string? FastbootSerial { get; init; }
}
