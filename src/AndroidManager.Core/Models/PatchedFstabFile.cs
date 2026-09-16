namespace AndroidManager.Core.Models;

/// <summary>PC'de düzenlenmiş fstab — sideload sonrası TWRP'ye adb push ile gider.</summary>
public sealed class PatchedFstabFile
{
    public required string LocalPath { get; init; }
    public required string DevicePath { get; init; }
}
