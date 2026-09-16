using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

/// <summary>
/// Custom ROM öncesi AVB kapatma — stock flash_all.bat ile aynı komut:
/// <c>fastboot flash vbmeta_ab vbmeta.img --disable-verity --disable-verification</c>
/// </summary>
internal static class CustomRomAvbDisableStep
{
    public static string? ResolveUserImagesDirectory(string? userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath))
            return null;

        if (File.Exists(userPath))
        {
            var name = Path.GetFileName(userPath);
            return name.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(userPath)
                : null;
        }

        if (!Directory.Exists(userPath))
            return null;

        if (File.Exists(Path.Combine(userPath, "vbmeta.img")))
            return userPath;

        var images = Path.Combine(userPath, "images");
        return File.Exists(Path.Combine(images, "vbmeta.img")) ? images : null;
    }

    public static async Task<string?> EnsureRomImagesDirectoryAsync(
        string? zipPath,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return null;

        if (!CustomRomPackageInspectorLite.HasPayloadBin(zipPath))
            return null;

        var workDir = RomExtractionCache.ResolveCacheDirectory(zipPath, null);
        var imagesDir = Path.Combine(workDir, "images");
        Directory.CreateDirectory(imagesDir);

        var existing = Path.Combine(imagesDir, "vbmeta.img");
        if (File.Exists(existing) && new FileInfo(existing).Length > 256)
            return imagesDir;

        Report(progress, 4, "ROM payload'dan vbmeta çıkarılıyor…");
        var payloadDir = Path.Combine(workDir, "payload_only");
        var payloadPath = await RomZipExtractor
            .ExtractPayloadBinOnlyAsync(zipPath, payloadDir, cancellationToken)
            .ConfigureAwait(false);
        if (payloadPath is null)
            return null;

        var dumper = new PayloadBinDumper();
        await dumper
            .DumpPartitionsAsync(
                payloadPath,
                imagesDir,
                ["vbmeta", "vbmeta_system"],
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        return Directory.Exists(imagesDir) ? imagesDir : null;
    }

    public static async Task<(bool Success, string Message)> RunAsync(
        FastbootFlashRunner fastboot,
        string serial,
        string? zipPath,
        string? userVbmetaPath,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var imagesDir = ResolveUserImagesDirectory(userVbmetaPath);
        if (imagesDir is null)
        {
            imagesDir = await EnsureRomImagesDirectoryAsync(zipPath, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var workDir = imagesDir
                      ?? (string.IsNullOrWhiteSpace(zipPath)
                          ? Path.Combine(Path.GetTempPath(), "AndroidManager", "avb")
                          : RomExtractionCache.ResolveCacheDirectory(zipPath, null));
        Directory.CreateDirectory(workDir);

        Report(progress, 5,
            "Dijital imza kontrolü kapatılıyor (vbmeta --disable-verity --disable-verification)…");

        return await BootloaderVbmetaFlasher
            .FlashDisableVerityAsync(fastboot, serial, workDir, imagesDir, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void Report(IProgress<FlashingProgressReport>? progress, int percent, string message) =>
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = percent,
            Message = message
        });
}
