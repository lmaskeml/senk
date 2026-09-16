using System.Text;
using AndroidManager.Core.Models;
using DiscUtils.Ext;

namespace AndroidManager.Core.Services;

public sealed record VendorFstabPatchResult(
    bool Success,
    string Message,
    IReadOnlyList<PatchedFstabFile> PatchedFiles,
    bool HadForceEncrypt,
    bool RequiresPushAfterSideload,
    string? PatchedVendorImagePath = null);

/// <summary>
/// vendor.img içinden fstab dosyalarını PC'de düzenler.
/// Android 15 EROFS: mkfs.erofs ile yeniden paketlenir — sideload sonrası fastbootd vendor flash.
/// </summary>
public sealed class VendorImageFstabPatcher
{
    public async Task<VendorFstabPatchResult> PreparePatchedFstabFilesAsync(
        string vendorImagePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(vendorImagePath) || !File.Exists(vendorImagePath))
        {
            return new VendorFstabPatchResult(
                false,
                "vendor.img bulunamadı.",
                [],
                false,
                false);
        }

        Directory.CreateDirectory(outputDirectory);
        var format = VendorImageFormatProbe.Detect(vendorImagePath);

        return format switch
        {
            VendorImageFormat.Erofs => await PatchFromErofsAsync(vendorImagePath, outputDirectory, cancellationToken)
                .ConfigureAwait(false),
            VendorImageFormat.Ext4 => PatchFromExt4Async(vendorImagePath, outputDirectory),
            _ => new VendorFstabPatchResult(
                false,
                "vendor.img formatı tanınamadı (EROFS/ext4 değil). " +
                "Android 15 için tools/erofs/extract.erofs.exe gerekir.",
                [],
                false,
                false)
        };
    }

    /// <summary>ext4 vendor — doğrudan imaj içinde patch (eski ROM'lar).</summary>
    public VendorFstabPatchResult PatchImage(string vendorImagePath) =>
        PatchFromExt4Async(vendorImagePath, Path.Combine(Path.GetTempPath(), $"am_vendor_{Guid.NewGuid():N}"));

    private async Task<VendorFstabPatchResult> PatchFromErofsAsync(
        string vendorImagePath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var extractRoot = Path.Combine(outputDirectory, "_erofs_extract");
        var extract = await ErofsExtractRunner
            .ExtractAsync(vendorImagePath, extractRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!extract.Success || extract.ExtractRoot is null)
        {
            return new VendorFstabPatchResult(false, extract.Message, [], false, false);
        }

        var etcDir = Path.Combine(extract.ExtractRoot, "etc");
        if (!Directory.Exists(etcDir))
        {
            return new VendorFstabPatchResult(
                false,
                "vendor/etc klasörü bulunamadı.",
                [],
                false,
                false);
        }

        var patchResult = PatchFstabFilesFromDirectory(
            etcDir,
            outputDirectory,
            requiresPush: false,
            writeBackToSource: true);
        if (!patchResult.Success || !patchResult.HadForceEncrypt || patchResult.PatchedFiles.Count == 0)
            return patchResult;

        var patchedVendorPath = Path.Combine(outputDirectory, "patched_vendor.img");
        var pack = await ErofsPackRunner
            .PackAsync(extract.ExtractRoot, patchedVendorPath, cancellationToken)
            .ConfigureAwait(false);
        if (!pack.Success)
        {
            return new VendorFstabPatchResult(
                false,
                pack.Message + Environment.NewLine +
                "EROFS vendor adb push ile yazılamaz (salt okunur). mkfs.erofs gerekir.",
                patchResult.PatchedFiles,
                true,
                false);
        }

        return patchResult with
        {
            Message =
                $"✅ DFE PC'de hazır ({patchResult.PatchedFiles.Count} dosya) — sideload sonrası fastbootd vendor flash.",
            RequiresPushAfterSideload = false,
            PatchedVendorImagePath = patchedVendorPath
        };
    }

    private VendorFstabPatchResult PatchFromExt4Async(string vendorImagePath, string outputDirectory)
    {
        try
        {
            using var stream = new FileStream(
                vendorImagePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read);

            using var fs = new ExtFileSystem(stream);
            var patchedFiles = new List<PatchedFstabFile>();
            var hadForceEncrypt = false;

            foreach (var relative in FstabContentPatcher.KnownFstabRelativePaths)
            {
                var path = "/" + relative;
                if (!fs.FileExists(path))
                    continue;

                var fileName = Path.GetFileName(relative);
                if (!FstabContentPatcher.IsMainFstabFileName(fileName))
                    continue;

                var original = ReadAllText(fs, path);
                if (string.IsNullOrWhiteSpace(original))
                    continue;

                if (!FstabContentPatcher.NeedsPatch(original))
                    continue;

                hadForceEncrypt = true;
                var updated = FstabContentPatcher.PatchContent(original);
                WriteAllText(fs, path, updated);

                Directory.CreateDirectory(outputDirectory);
                var localCopy = Path.Combine(outputDirectory, fileName);
                File.WriteAllText(localCopy, updated, Encoding.UTF8);
                patchedFiles.Add(new PatchedFstabFile
                {
                    LocalPath = localCopy,
                    DevicePath = FstabContentPatcher.DevicePathForFileName(fileName)
                });
            }

            if (!hadForceEncrypt)
            {
                return new VendorFstabPatchResult(
                    true,
                    "✅ vendor.img içinde forceencrypt yok — ek düzenleme gerekmedi.",
                    [],
                    false,
                    false);
            }

            if (patchedFiles.Count == 0)
            {
                return new VendorFstabPatchResult(
                    false,
                    "forceencrypt bulundu ancak fstab dosyaları yazılamadı.",
                    [],
                    true,
                    false);
            }

            return new VendorFstabPatchResult(
                true,
                $"✅ ext4 vendor PC'de düzenlendi ({patchedFiles.Count} dosya) — sideload sonrası fastbootd vendor flash.",
                patchedFiles,
                true,
                false,
                PatchedVendorImagePath: vendorImagePath);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return new VendorFstabPatchResult(
                false,
                $"vendor.img ext4 açılamadı: {ex.Message}",
                [],
                false,
                false);
        }
    }

    private static VendorFstabPatchResult PatchFstabFilesFromDirectory(
        string etcDir,
        string outputDirectory,
        bool requiresPush,
        bool writeBackToSource = false)
    {
        Directory.CreateDirectory(outputDirectory);
        var patchedFiles = new List<PatchedFstabFile>();
        var scanned = new List<string>();

        foreach (var file in Directory.EnumerateFiles(etcDir, "fstab*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (!FstabContentPatcher.IsMainFstabFileName(fileName))
                continue;

            scanned.Add(fileName);
            var original = File.ReadAllText(file);
            if (!FstabContentPatcher.NeedsPatch(original))
                continue;

            var updated = FstabContentPatcher.PatchContent(original);
            var localPath = Path.Combine(outputDirectory, fileName);
            File.WriteAllText(localPath, updated, Encoding.UTF8);
            if (writeBackToSource)
                File.WriteAllText(file, updated, Encoding.UTF8);

            patchedFiles.Add(new PatchedFstabFile
            {
                LocalPath = localPath,
                DevicePath = FstabContentPatcher.DevicePathForFileName(fileName)
            });
        }

        if (patchedFiles.Count == 0)
        {
            var found = scanned.Count == 0
                ? "fstab dosyası yok"
                : "tarandı: " + string.Join(", ", scanned);
            return new VendorFstabPatchResult(
                true,
                $"✅ {found} — fileencryption/forceencrypt yok, DFE gerekmedi.",
                [],
                false,
                false);
        }

        return new VendorFstabPatchResult(
            true,
            requiresPush
                ? $"✅ DFE PC'de hazır ({patchedFiles.Count} dosya: {string.Join(", ", patchedFiles.Select(p => Path.GetFileName(p.LocalPath)))}) — sideload sonrası adb push."
                : $"✅ DFE PC'de hazır ({patchedFiles.Count} dosya).",
            patchedFiles,
            true,
            requiresPush);
    }

    private static string ReadAllText(ExtFileSystem fs, string path)
    {
        using var file = fs.OpenFile(path, FileMode.Open, FileAccess.Read);
        using var ms = new MemoryStream();
        file.CopyTo(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteAllText(ExtFileSystem fs, string path, string content)
    {
        using var file = fs.OpenFile(path, FileMode.Create, FileAccess.Write);
        var bytes = Encoding.UTF8.GetBytes(content);
        file.Write(bytes, 0, bytes.Length);
        file.Flush();
    }
}
