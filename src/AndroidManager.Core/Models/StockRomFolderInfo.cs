namespace AndroidManager.Core.Models;

/// <summary>Xiaomi fastboot ROM klasörü (images\boot.img + super.img).</summary>
public sealed class StockRomFolderInfo
{
    public required string FolderPath { get; init; }
    public required string ImagesPath { get; init; }
    public bool HasBoot { get; init; }
    public bool HasSuper { get; init; }
    public bool HasVendorBoot { get; init; }
    public bool HasTwrp { get; init; }
    public bool HasMagisk { get; init; }
    public bool HasFlashAll { get; init; }
    public bool HasInjectScript { get; init; }
    public bool HasMagiskboot { get; init; }
    public string? TwrpPath { get; init; }
    public string? MagiskPath { get; init; }
    public bool IsValid => HasBoot && HasSuper;
}
