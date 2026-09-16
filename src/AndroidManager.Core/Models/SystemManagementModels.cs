namespace AndroidManager.Core.Models;

public enum RootMethodType
{
    Magisk,
    KernelSU,
    Apatch,
    Unknown
}

public enum RootCompatibilityLevel
{
    Unsupported,
    Experimental,
    LikelySupported,
    Supported,
    Unknown
}

public enum RecoveryType
{
    Unknown,
    Stock,
    Twrp,
    OrangeFox,
    Custom
}

public enum DeviceConnectionMode
{
    Unknown,
    Adb,
    Fastboot,
    Recovery,
    Offline
}

public enum RootStepStatus
{
    Pending,
    Running,
    Completed,
    Error,
    Skipped
}

public sealed record DeviceProfile
{
    public string StableId { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string Codename { get; init; } = "";
    public int ApiLevel { get; init; }
    public string AndroidVersion { get; init; } = "";
    public string Chipset { get; init; } = "";
    public string Architecture { get; init; } = "";
    public bool IsAbPartition { get; init; }
    public bool HasVbmeta { get; init; }
    public bool HasInitBoot { get; init; }
    public bool HasVendorBoot { get; init; }
    public bool HasRecovery { get; init; }
    public bool IsGki { get; init; }
    public string ActiveSlot { get; init; } = "";
    public bool BootloaderUnlocked { get; init; }
    public bool IsRooted { get; init; }
    public RootAccess CurrentRoot { get; init; } = RootAccess.None;
    public RecoveryType DetectedRecovery { get; init; } = RecoveryType.Unknown;
    public RootCompatibilityLevel Compatibility { get; init; } = RootCompatibilityLevel.Unknown;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Model) ? SerialNumber : $"{Manufacturer} {Model}".Trim();
}

public sealed class RootMethodRecommendation
{
    public RootMethodType Method { get; init; } = RootMethodType.Unknown;
    public RootCompatibilityLevel Level { get; init; } = RootCompatibilityLevel.Unknown;
    public string DisplayName { get; init; } = "";
    public string PatchTarget { get; init; } = "";
    public string Summary { get; init; } = "";
    public bool IsRecommended { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Requirements { get; init; } = [];
}

public sealed class RootPreflightCheck
{
    public string Title { get; init; } = "";
    public bool Passed { get; init; }
    public string Detail { get; init; } = "";
    public bool IsCritical { get; init; }
}

public sealed class RootPreflightResult
{
    public bool CanProceed { get; init; }
    public IReadOnlyList<RootPreflightCheck> Checks { get; init; } = [];
    public string Summary { get; init; } = "";
}

public sealed class RootStep
{
    public int Order { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public RootStepStatus Status { get; init; } = RootStepStatus.Pending;
    public string? ErrorMessage { get; init; }
    public string? WarningMessage { get; init; }
    public int ProgressPercent { get; init; }
}

public sealed class PartitionBackupRecord
{
    public int Id { get; init; }
    public string StableDeviceId { get; init; } = "";
    public string DeviceSerial { get; init; } = "";
    public string DeviceModel { get; init; } = "";
    public string PartitionName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Source { get; init; } = "";
}

public sealed class RecoveryStatus
{
    public RecoveryType Type { get; init; } = RecoveryType.Unknown;
    public string Label { get; init; } = "Bilinmiyor";
    public bool BootloaderUnlocked { get; init; }
    public bool IsRooted { get; init; }
    public string Detail { get; init; } = "";
    public DeviceConnectionMode ConnectionMode { get; init; } = DeviceConnectionMode.Unknown;
}

public sealed class RescueSnapshot
{
    public DeviceConnectionMode Adb { get; init; } = DeviceConnectionMode.Offline;
    public DeviceConnectionMode Fastboot { get; init; } = DeviceConnectionMode.Offline;
    public DeviceConnectionMode Recovery { get; init; } = DeviceConnectionMode.Offline;
    public IReadOnlyList<PartitionBackupRecord> AvailableBackups { get; init; } = [];
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> SafeActions { get; init; } = [];
}

public sealed class DeviceSecuritySummary
{
    public RootAccess RootAccess { get; init; } = RootAccess.None;
    public bool BootloaderUnlocked { get; init; }
    public string PlayIntegrity { get; init; } = "Bilinmiyor";
    public int ThreatCount { get; init; }
    public DateTime? LastScanAt { get; init; }
    public bool DeepScanAvailable { get; init; }
}
