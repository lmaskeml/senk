namespace AndroidManager.Core.Models;

public sealed class RomPcPrepareResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";

    /// <summary>Sideload için kullanılacak zip (her zaman orijinal — payload repack yapılmaz).</summary>
    public required string SideloadZipPath { get; init; }

    public bool FstabPatchedOnPc { get; init; }

    /// <summary>vendor PC'de incelendi — yavaş TWRP find/sed atlanır.</summary>
    public bool SkipDeviceFstabPatch { get; init; }

    /// <summary>Sideload sonrası TWRP'ye adb push edilecek fstab dosyaları (ext4 yedek yol).</summary>
    public IReadOnlyList<PatchedFstabFile> FstabFilesToPush { get; init; } = [];

    /// <summary>PC'de DFE uygulanmış vendor.img — sideload sonrası fastbootd flash.</summary>
    public string? PatchedVendorImagePath { get; init; }

    /// <summary>fstab'da FBE vardı ama EROFS paketleme/yazma başarısız — DFE uygulanmadı.</summary>
    public bool DfeNeededButSkipped { get; init; }

    public bool UsedOriginalZip { get; init; } = true;
}
