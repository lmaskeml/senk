using System.Text.Json;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

/// <summary>Payload çıkarımını zip başına kalıcı önbellekte tutar — tekrar flash'ta zaman kazanır.</summary>
internal static class RomExtractionCache
{
    private const string CacheRootFolder = "rom_cache";
    private const string ManifestFileName = ".extraction.json";

    private sealed class ExtractionManifest
    {
        public long SizeBytes { get; init; }
        public long LastWriteUtcTicks { get; init; }
        public int ImageCount { get; init; }
    }

    public static string ResolveCacheDirectory(string zipPath, string? overrideDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return overrideDirectory;

        var key = ComputeCacheKey(zipPath);
        return Path.Combine(Path.GetTempPath(), "AndroidManager", CacheRootFolder, key);
    }

    public static bool TryLoadImages(string workDir, string zipPath, out Dictionary<string, string> imageMap)
    {
        imageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var imagesDir = Path.Combine(workDir, "images");
        if (!Directory.Exists(imagesDir) || !File.Exists(zipPath))
            return false;

        if (!IsZipFingerprintValid(workDir, zipPath))
            return false;

        foreach (var file in Directory.EnumerateFiles(imagesDir, "*.img"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var info = new FileInfo(file);
            if (info.Length > 0)
                imageMap[name] = file;
        }

        return imageMap.Count >= 4
               && imageMap.ContainsKey("system")
               && imageMap.ContainsKey("boot");
    }

    public static void SaveManifest(string workDir, string zipPath, int imageCount)
    {
        var fi = new FileInfo(zipPath);
        var manifest = new ExtractionManifest
        {
            SizeBytes = fi.Length,
            LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
            ImageCount = imageCount
        };

        var json = JsonSerializer.Serialize(manifest);
        File.WriteAllText(Path.Combine(workDir, ManifestFileName), json);
    }

    public static IReadOnlyList<PayloadPartitionImage> ToPartitionImages(IReadOnlyDictionary<string, string> imageMap) =>
        imageMap
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new PayloadPartitionImage
            {
                PartitionName = kv.Key,
                ImagePath = kv.Value,
                SizeBytes = new FileInfo(kv.Value).Length
            })
            .ToArray();

    private static bool IsZipFingerprintValid(string workDir, string zipPath)
    {
        var manifestPath = Path.Combine(workDir, ManifestFileName);
        if (!File.Exists(manifestPath))
            return true;

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<ExtractionManifest>(json);
            if (manifest is null)
                return false;

            var fi = new FileInfo(zipPath);
            return fi.Length == manifest.SizeBytes
                   && fi.LastWriteTimeUtc.Ticks == manifest.LastWriteUtcTicks;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeCacheKey(string zipPath)
    {
        var fi = new FileInfo(zipPath);
        var baseName = Path.GetFileNameWithoutExtension(fi.Name);
        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');

        if (baseName.Length > 56)
            baseName = baseName[..56];

        return $"{baseName}_{fi.Length:x}_{fi.LastWriteTimeUtc.Ticks:x}";
    }
}
