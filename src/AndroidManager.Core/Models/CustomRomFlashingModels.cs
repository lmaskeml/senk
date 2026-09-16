namespace AndroidManager.Core.Models;

public enum CustomRomFlashStage
{
    Idle,
    Extracting,
    DumpingPayload,
    PreparingFastboot,
    EnteringFastbootd,
    Flashing,
    Finalizing,
    Completed,
    Failed,
    Cancelled
}

public sealed class FlashingProgressReport
{
    public CustomRomFlashStage Stage { get; init; } = CustomRomFlashStage.Idle;
    public int Percent { get; init; }
    public string Message { get; init; } = "";
    public string? DetailLine { get; init; }
    public string? Partition { get; init; }
}

public sealed class CustomRomFlashRequest
{
    public required string ZipPath { get; init; }
    public string? DeviceSerial { get; init; }
    /// <summary>Örn. vili — Xiaomi A/B flash_all.bat profili için.</summary>
    public string? DeviceCodename { get; init; }
    public string ActiveSlot { get; init; } = "a";
    public bool WipeUserData { get; init; }
    /// <summary>Geçici çalışma klasörü; boş bırakılırsa zip önbelleği kullanılır.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Var olan çıkarılmış imajları kullan (aynı zip — test/tekrar flash).</summary>
    public bool ReuseCachedExtraction { get; init; } = true;

    /// <summary>Caller may supply adb → bootloader transition (Core stays device-agnostic).</summary>
    public Func<CancellationToken, Task>? EnsureBootloaderAsync { get; init; }
}

public sealed class CustomRomFlashResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> FlashedPartitions { get; init; } = [];
    public string? ExtractedRoot { get; init; }
    public string? ImagesDirectory { get; init; }
}

public sealed class ExtractedRomPackage
{
    public required string RootPath { get; init; }
    public string? PayloadBinPath { get; init; }
    public bool HasPayload => !string.IsNullOrWhiteSpace(PayloadBinPath);
}

public sealed class PayloadPartitionImage
{
    public required string PartitionName { get; init; }
    public required string ImagePath { get; init; }
    public long SizeBytes { get; init; }
}

internal sealed class PayloadManifest
{
    public long BlockSize { get; set; } = 4096;
    public List<PayloadPartitionUpdate> Partitions { get; } = [];
}

internal sealed class PayloadPartitionUpdate
{
    public string PartitionName { get; set; } = "";
    public List<PayloadInstallOperation> Operations { get; } = [];
}

internal sealed class PayloadInstallOperation
{
    public int Type { get; set; }
    public ulong DataOffset { get; set; }
    public ulong DataLength { get; set; }
    public List<PayloadExtent> DstExtents { get; } = [];
}

internal sealed class PayloadExtent
{
    public ulong StartBlock { get; set; }
    public ulong NumBlocks { get; set; }
}
