using System.IO.Compression;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

public static class RomZipExtractor
{
    private static readonly string[] PayloadNames = ["payload.bin", "payload.bin.gz"];

    public static async Task<ExtractedRomPackage> ExtractAsync(
        string zipPath,
        string outputDirectory,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default,
        bool reuseExisting = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!File.Exists(zipPath))
            throw new FileNotFoundException("ROM zip bulunamadı.", zipPath);

        if (reuseExisting)
        {
            var existingPayload = FindPayloadBin(outputDirectory);
            if (existingPayload is not null)
            {
                progress?.Report(new FlashingProgressReport
                {
                    Stage = CustomRomFlashStage.Extracting,
                    Percent = 8,
                    Message = $"Zip zaten ayıklı — atlandı ({Path.GetFileName(existingPayload)})"
                });

                return new ExtractedRomPackage
                {
                    RootPath = outputDirectory,
                    PayloadBinPath = existingPayload
                };
            }
        }

        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        Directory.CreateDirectory(outputDirectory);

        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Extracting,
            Percent = 2,
            Message = "ROM zip ayıklanıyor…"
        });

        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var dest = Path.Combine(outputDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrWhiteSpace(destDir))
                    Directory.CreateDirectory(destDir);

                if (entry.Length == 0 && entry.FullName.EndsWith('/'))
                    continue;

                entry.ExtractToFile(dest, overwrite: true);
            }
        }, cancellationToken).ConfigureAwait(false);

        var payloadPath = FindPayloadBin(outputDirectory);
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Extracting,
            Percent = 8,
            Message = payloadPath is null
                ? "Zip ayıklandı — payload.bin bulunamadı."
                : $"Zip ayıklandı — payload.bin: {Path.GetFileName(payloadPath)}"
        });

        return new ExtractedRomPackage
        {
            RootPath = outputDirectory,
            PayloadBinPath = payloadPath
        };
    }

    /// <summary>Zip'ten yalnızca payload.bin çıkarır (tam zip ayıklamadan).</summary>
    public static async Task<string?> ExtractPayloadBinOnlyAsync(
        string zipPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        Directory.CreateDirectory(outputDirectory);

        var dest = Path.Combine(outputDirectory, "payload.bin");
        if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            return dest;

        if (!File.Exists(zipPath))
            throw new FileNotFoundException("ROM zip bulunamadı.", zipPath);

        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals("payload.bin", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith("/payload.bin", StringComparison.OrdinalIgnoreCase));

            if (entry is null)
                throw new InvalidOperationException("Zip içinde payload.bin bulunamadı.");

            entry.ExtractToFile(dest, overwrite: true);
        }, cancellationToken).ConfigureAwait(false);

        return File.Exists(dest) ? dest : null;
    }

    public static string? FindPayloadBin(string rootDirectory)
    {
        foreach (var name in PayloadNames)
        {
            var direct = Path.Combine(rootDirectory, name);
            if (File.Exists(direct))
                return direct;
        }

        foreach (var file in Directory.EnumerateFiles(rootDirectory, "payload.bin", SearchOption.AllDirectories))
            return file;

        return null;
    }
}
