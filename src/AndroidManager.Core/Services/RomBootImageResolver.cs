using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

public sealed record RomBootImages(
    string BootImagePath,
    string? VendorBootImagePath,
    string? InitBootImagePath);

/// <summary>Payload ROM'dan boot/vendor_boot imajlarını çözer (önbellek veya seçici payload dump).</summary>
public sealed class RomBootImageResolver
{
    private static readonly string[] BootPartitionNames = ["boot", "vendor_boot", "init_boot"];

    private readonly PayloadBinDumper _dumper = new();

    public async Task<RomBootImages?> ResolveAsync(
        string zipPath,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return null;

        var workDir = RomExtractionCache.ResolveCacheDirectory(zipPath, null);
        var imagesDir = Path.Combine(workDir, "images");
        Directory.CreateDirectory(imagesDir);

        if (TryLoadFromDisk(imagesDir, out var cached))
            return cached;

        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Extracting,
            Percent = 82,
            Message = "Sistem dosyaları hazırlanıyor (boot)…"
        });

        var payloadPath = RomZipExtractor.FindPayloadBin(workDir);
        if (payloadPath is null)
        {
            payloadPath = await RomZipExtractor
                .ExtractPayloadBinOnlyAsync(zipPath, Path.Combine(workDir, "payload_only"), cancellationToken)
                .ConfigureAwait(false);
        }

        if (payloadPath is null)
            return null;

        var dumped = await _dumper
            .DumpPartitionsAsync(payloadPath, imagesDir, BootPartitionNames, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!dumped.Any(p => p.PartitionName.Equals("boot", StringComparison.OrdinalIgnoreCase)))
            return null;

        return TryLoadFromDisk(imagesDir, out var resolved) ? resolved : null;
    }

    private static bool TryLoadFromDisk(string imagesDir, out RomBootImages? images)
    {
        images = null;
        if (!Directory.Exists(imagesDir))
            return false;

        string? Boot(string name)
        {
            var path = Path.Combine(imagesDir, $"{name}.img");
            return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
        }

        var boot = Boot("boot");
        if (boot is null)
            return false;

        images = new RomBootImages(boot, Boot("vendor_boot"), Boot("init_boot"));
        return true;
    }
}
