using System.IO.Compression;
using System.Text.Json;

namespace AndroidManager.Core.Models;

public static class SplitPackageExtractor
{
    public static SplitPackageContents Extract(string archivePath)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Paket bulunamadı.", archivePath);

        if (!AndroidPackageFormats.IsSplitArchive(archivePath))
            throw new ArgumentException("Dosya XAPK/APKS/APKM değil.", nameof(archivePath));

        var extractDir = Path.Combine(
            Path.GetTempPath(),
            "AndroidManager",
            "split-install",
            $"{Path.GetFileNameWithoutExtension(archivePath)}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);

        ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);

        var apkPaths = Directory.GetFiles(extractDir, "*.apk", SearchOption.AllDirectories)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (apkPaths.Count == 0)
        {
            TryDeleteDirectory(extractDir);
            throw new InvalidOperationException("Arşivde .apk bulunamadı.");
        }

        // Prefer base.apk first for install-multiple.
        apkPaths = apkPaths
            .OrderByDescending(p => Path.GetFileName(p).Equals("base.apk", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var packageName = TryReadPackageName(extractDir);
        var obbFiles = CollectObbFiles(extractDir, packageName);

        return new SplitPackageContents
        {
            SourcePath = archivePath,
            ExtractDirectory = extractDir,
            PackageName = packageName,
            ApkPaths = apkPaths,
            ObbFiles = obbFiles
        };
    }

    public static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static string? TryReadPackageName(string extractDir)
    {
        foreach (var name in new[] { "manifest.json", "info.json", "APKM_installer.info" })
        {
            var path = Directory.GetFiles(extractDir, name, SearchOption.AllDirectories).FirstOrDefault();
            if (path is null) continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("package_name", out var pn))
                    return pn.GetString();
                if (root.TryGetProperty("packageName", out var pn2))
                    return pn2.GetString();
                if (root.TryGetProperty("pname", out var pn3))
                    return pn3.GetString();
            }
            catch
            {
                // ignore malformed manifests
            }
        }

        return null;
    }

    private static IReadOnlyList<ObbFile> CollectObbFiles(string extractDir, string? packageName)
    {
        var list = new List<ObbFile>();
        foreach (var path in Directory.GetFiles(extractDir, "*.obb", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extractDir, path).Replace('\\', '/');
            string? remoteRelative = null;

            // XAPK often: Android/obb/com.pkg/main.*.obb
            var marker = "Android/obb/";
            var idx = relative.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                remoteRelative = relative[(idx + marker.Length)..];
            }
            else if (!string.IsNullOrWhiteSpace(packageName))
            {
                remoteRelative = $"{packageName}/{Path.GetFileName(path)}";
            }

            list.Add(new ObbFile
            {
                LocalPath = path,
                FileName = Path.GetFileName(path),
                RelativeRemotePath = remoteRelative
            });
        }

        return list;
    }
}
