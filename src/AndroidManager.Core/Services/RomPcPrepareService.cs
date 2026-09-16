using System.Text.Json;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

/// <summary>
/// ROM kurulumundan önce vendor fstab PC'de düzenlenir.
/// Android 15 EROFS: mkfs.erofs ile vendor yeniden paketlenir — sideload sonrası fastbootd flash.
/// </summary>
public sealed class RomPcPrepareService
{
    private const string PreparedManifestFile = ".prepared.json";
    private const string PatchedFstabDirName = "patched_fstab";

    private readonly PayloadBinDumper _dumper = new();
    private readonly VendorImageFstabPatcher _vendorPatcher = new();

    private const int PreparedSchemaVersion = 4;

    private sealed class PreparedManifest
    {
        public int SchemaVersion { get; init; }
        public long SourceSizeBytes { get; init; }
        public long SourceWriteUtcTicks { get; init; }
        public bool FstabPatchedOnPc { get; init; }
        public string? PatchedVendorImagePath { get; init; }
        public List<PatchedFstabFileEntry> FstabFiles { get; init; } = [];
    }

    private sealed class PatchedFstabFileEntry
    {
        public string LocalPath { get; init; } = "";
        public string DevicePath { get; init; } = "";
    }

    public async Task<RomPcPrepareResult> PrepareForSideloadAsync(
        string zipPath,
        bool patchFstab,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return Fail(zipPath, "ROM zip dosyası bulunamadı.");

        if (!CustomRomPackageInspectorLite.HasPayloadBin(zipPath))
        {
            return Ok(zipPath, "Payload ROM değil — orijinal zip kullanılacak.");
        }

        if (!patchFstab)
            return Ok(zipPath, "Fstab patch kapalı — orijinal zip kullanılacak.");

        var workDir = RomExtractionCache.ResolveCacheDirectory(zipPath, null);
        Directory.CreateDirectory(workDir);

        if (TryLoadCachedPrepared(zipPath, workDir, out var cached))
        {
            Report(progress, 4, "Fstab hazırlığı önbellekten yüklendi.");
            return cached;
        }

        Report(progress, 2, "ROM PC'de hazırlanıyor (vendor fstab)…");

        var payloadDir = Path.Combine(workDir, "payload_only");
        var payloadPath = await RomZipExtractor
            .ExtractPayloadBinOnlyAsync(zipPath, payloadDir, cancellationToken)
            .ConfigureAwait(false);
        if (payloadPath is null)
            return Fail(zipPath, "Zip içinden payload.bin çıkarılamadı.");

        Report(progress, 5, "vendor.img + vbmeta çıkarılıyor…");
        var imagesDir = Path.Combine(workDir, "images");
        // vbmeta de çıkar — fastbootd için stub yerine ROM vbmeta + --disable-verity gerekir.
        var vendorDump = await _dumper
            .DumpPartitionsAsync(
                payloadPath,
                imagesDir,
                ["vendor", "vbmeta", "vbmeta_system"],
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        var vendorImage = vendorDump.FirstOrDefault(p =>
            p.PartitionName.Equals("vendor", StringComparison.OrdinalIgnoreCase))?.ImagePath;
        if (vendorImage is null || !File.Exists(vendorImage))
            return Fail(zipPath, "payload içinden vendor.img çıkarılamadı.");

        var format = VendorImageFormatProbe.Detect(vendorImage);
        Report(progress, 7,
            format == VendorImageFormat.Erofs
                ? "Android 15 EROFS vendor — fstab ayıklanıyor (extract.erofs)…"
                : "vendor fstab düzenleniyor…");

        var fstabOutDir = Path.Combine(workDir, PatchedFstabDirName);
        var patchResult = await _vendorPatcher
            .PreparePatchedFstabFilesAsync(vendorImage, fstabOutDir, cancellationToken)
            .ConfigureAwait(false);
        if (!patchResult.Success)
        {
            var skipMessage =
                "⚠️ DFE uygulanamadı — ROM sideload devam eder, şifreleme açık kalabilir." +
                Environment.NewLine + patchResult.Message;
            Report(progress, 10, skipMessage);
            return new RomPcPrepareResult
            {
                Success = true,
                SideloadZipPath = zipPath,
                SkipDeviceFstabPatch = true,
                DfeNeededButSkipped = patchResult.HadForceEncrypt,
                Message = skipMessage
            };
        }

        SavePreparedManifest(workDir, zipPath, patchResult.HadForceEncrypt, patchResult);

        if (!patchResult.HadForceEncrypt)
        {
            Report(progress, 10, patchResult.Message);
            return new RomPcPrepareResult
            {
                Success = true,
                SideloadZipPath = zipPath,
                SkipDeviceFstabPatch = true,
                Message = patchResult.Message
            };
        }

        Report(progress, 12, patchResult.Message);
        return new RomPcPrepareResult
        {
            Success = true,
            SideloadZipPath = zipPath,
            FstabPatchedOnPc = true,
            SkipDeviceFstabPatch = true,
            FstabFilesToPush = patchResult.RequiresPushAfterSideload ? patchResult.PatchedFiles : [],
            PatchedVendorImagePath = patchResult.PatchedVendorImagePath,
            Message =
                patchResult.Message + Environment.NewLine +
                (patchResult.PatchedVendorImagePath is not null
                    ? "Orijinal ROM sideload edilecek; DFE vendor fastbootd ile yazılacak (EROFS adb push desteklemez)."
                    : "Orijinal ROM sideload edilecek; fstab adb push ile gidecek.")
        };
    }

    private static bool TryLoadCachedPrepared(string zipPath, string workDir, out RomPcPrepareResult result)
    {
        result = null!;
        var manifestPath = Path.Combine(workDir, PreparedManifestFile);
        if (!File.Exists(manifestPath))
            return false;

        try
        {
            var fi = new FileInfo(zipPath);
            var manifest = JsonSerializer.Deserialize<PreparedManifest>(File.ReadAllText(manifestPath));
            if (manifest is null
                || manifest.SchemaVersion != PreparedSchemaVersion
                || manifest.SourceSizeBytes != fi.Length
                || manifest.SourceWriteUtcTicks != fi.LastWriteTimeUtc.Ticks)
            {
                return false;
            }

            var files = manifest.FstabFiles
                .Where(f => File.Exists(f.LocalPath))
                .Select(f => new PatchedFstabFile { LocalPath = f.LocalPath, DevicePath = f.DevicePath })
                .ToList();

            result = new RomPcPrepareResult
            {
                Success = true,
                SideloadZipPath = zipPath,
                FstabPatchedOnPc = manifest.FstabPatchedOnPc && (files.Count > 0 || !string.IsNullOrWhiteSpace(manifest.PatchedVendorImagePath)),
                SkipDeviceFstabPatch = true,
                FstabFilesToPush = files,
                PatchedVendorImagePath = File.Exists(manifest.PatchedVendorImagePath ?? "")
                    ? manifest.PatchedVendorImagePath
                    : null,
                Message = !string.IsNullOrWhiteSpace(manifest.PatchedVendorImagePath)
                    ? "Fstab hazırlığı önbellekten yüklendi — sideload sonrası vendor flash yapılacak."
                    : files.Count > 0
                        ? "Fstab hazırlığı önbellekten yüklendi — sideload sonrası push yapılacak."
                        : "ROM önbellekte — DFE gerekmedi."
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SavePreparedManifest(
        string workDir,
        string zipPath,
        bool patched,
        VendorFstabPatchResult patchResult)
    {
        var fi = new FileInfo(zipPath);
        var manifest = new PreparedManifest
        {
            SchemaVersion = PreparedSchemaVersion,
            SourceSizeBytes = fi.Length,
            SourceWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
            FstabPatchedOnPc = patched,
            PatchedVendorImagePath = patchResult.PatchedVendorImagePath,
            FstabFiles = patchResult.PatchedFiles
                .Select(f => new PatchedFstabFileEntry { LocalPath = f.LocalPath, DevicePath = f.DevicePath })
                .ToList()
        };
        File.WriteAllText(Path.Combine(workDir, PreparedManifestFile), JsonSerializer.Serialize(manifest));
    }

    private static RomPcPrepareResult Ok(string zipPath, string message) =>
        new() { Success = true, SideloadZipPath = zipPath, SkipDeviceFstabPatch = false, Message = message };

    private static RomPcPrepareResult Fail(string zipPath, string message) =>
        new() { Success = false, SideloadZipPath = zipPath, Message = message };

    private static void Report(IProgress<FlashingProgressReport>? progress, int percent, string message) =>
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Extracting,
            Percent = percent,
            Message = message
        });
}

internal static class CustomRomPackageInspectorLite
{
    public static bool HasPayloadBin(string zipPath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        return archive.Entries.Any(e =>
            e.Name.Equals("payload.bin", StringComparison.OrdinalIgnoreCase)
            || e.FullName.EndsWith("/payload.bin", StringComparison.OrdinalIgnoreCase));
    }
}
