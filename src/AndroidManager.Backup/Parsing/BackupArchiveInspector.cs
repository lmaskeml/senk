using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using AndroidManager.Core.Models;

namespace AndroidManager.Backup.Parsing;

public static partial class BackupArchiveInspector
{
    private static readonly string[] SystemPrefixes =
    [
        "android.",
        "com.android.",
        "com.google.android.",
        "com.qualcomm.",
        "com.qti.",
        "vendor.qti.",
        "com.xiaomi.",
        "com.miui.",
        "com.mi.",
        "com.mediatek.",
        "com.fingerprints.",
        "org.codeaurora.",
        "com.samsung.",
        "com.sec.",
        "com.huawei.",
        "com.hihonor.",
        "com.oppo.",
        "com.oneplus.",
        "com.oplus.",
        "com.realme.",
        "com.vivo.",
        "com.bbk.",
        "com.nothing.",
        "com.sonyericsson.",
        "com.sonymobile."
    ];

    public static IReadOnlyList<BackupAppEntry> Inspect(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            return [];

        if (File.Exists(backupPath)
            && backupPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return InspectZip(backupPath);

        if (Directory.Exists(backupPath))
            return InspectDirectory(backupPath);

        return [];
    }

    public static bool IsLikelySystemPackage(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return false;

        foreach (var prefix in SystemPrefixes)
        {
            if (packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsPackageName(string packageName) =>
        PackageNameRegex().IsMatch(packageName);

    private static IReadOnlyList<BackupAppEntry> InspectZip(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var map = new Dictionary<string, MutableEntry>(StringComparer.Ordinal);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)
                || string.Equals(Path.GetFileName(entry.FullName), "_empty.txt", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryClassifyEntry(entry.FullName, out var kind, out var relative))
                continue;

            ApplyEntry(map, kind, relative, entry.Length);
        }

        return ToEntries(map);
    }

    private static IReadOnlyList<BackupAppEntry> InspectDirectory(string root)
    {
        var map = new Dictionary<string, MutableEntry>(StringComparer.Ordinal);
        var appsDir = FindNamedDirectory(root, "Apps");
        var dataDir = FindNamedDirectory(root, "AppData");

        if (appsDir is not null)
        {
            foreach (var file in Directory.EnumerateFiles(appsDir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), "_empty.txt", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = Path.GetRelativePath(appsDir, file);
                ApplyEntry(map, "Apps", relative, new FileInfo(file).Length);
            }
        }

        if (dataDir is not null)
        {
            foreach (var file in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), "_empty.txt", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = Path.GetRelativePath(dataDir, file);
                ApplyEntry(map, "AppData", relative, new FileInfo(file).Length);
            }
        }

        return ToEntries(map);
    }

    public static bool TryClassifyEntry(string fullName, out string kind, out string relative)
    {
        kind = string.Empty;
        relative = string.Empty;
        var normalized = fullName.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Equals("Apps", StringComparison.OrdinalIgnoreCase)
                && i + 1 < parts.Length)
            {
                kind = "Apps";
                relative = string.Join('/', parts.Skip(i + 1));
                return true;
            }

            if (parts[i].Equals("AppData", StringComparison.OrdinalIgnoreCase)
                && i + 1 < parts.Length)
            {
                kind = "AppData";
                relative = string.Join('/', parts.Skip(i + 1));
                return true;
            }
        }

        return false;
    }

    public static string? PackageFromRelative(string kind, string relative)
    {
        var normalized = relative.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var fileName = Path.GetFileName(normalized);
        if (string.Equals(fileName, "_empty.txt", StringComparison.OrdinalIgnoreCase))
            return null;

        if (kind.Equals("AppData", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Contains('/', StringComparison.Ordinal))
                return FirstSegment(normalized);

            return StripArchiveSuffix(fileName);
        }

        if (normalized.Contains('/', StringComparison.Ordinal))
            return FirstSegment(normalized);

        if (fileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".apks", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".xapk", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(fileName);

        return null;
    }

    private static void ApplyEntry(
        Dictionary<string, MutableEntry> map,
        string kind,
        string relative,
        long length)
    {
        var package = PackageFromRelative(kind, relative);
        if (package is null || !IsPackageName(package))
            return;

        if (!map.TryGetValue(package, out var item))
        {
            item = new MutableEntry { PackageName = package };
            map[package] = item;
        }

        if (kind.Equals("Apps", StringComparison.OrdinalIgnoreCase))
        {
            item.HasApk = true;
            item.ApkBytes += Math.Max(0, length);
        }
        else
        {
            item.HasData = true;
            item.DataBytes += Math.Max(0, length);
        }
    }

    private static IReadOnlyList<BackupAppEntry> ToEntries(Dictionary<string, MutableEntry> map) =>
        map.Values
            .OrderBy(e => e.IsLikelySystem)
            .ThenBy(e => e.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new BackupAppEntry
            {
                PackageName = e.PackageName,
                HasApk = e.HasApk,
                HasData = e.HasData,
                IsLikelySystem = e.IsLikelySystem,
                ApkBytes = e.ApkBytes,
                DataBytes = e.DataBytes
            })
            .ToList();

    private static string? FindNamedDirectory(string root, string name)
    {
        var direct = Path.Combine(root, name);
        if (Directory.Exists(direct))
            return direct;

        foreach (var child in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(child).Equals(name, StringComparison.OrdinalIgnoreCase))
                return child;

            var nested = Path.Combine(child, name);
            if (Directory.Exists(nested))
                return nested;
        }

        return null;
    }

    private static string FirstSegment(string relative)
    {
        var slash = relative.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? relative : relative[..slash];
    }

    private static string StripArchiveSuffix(string fileName)
    {
        var name = fileName;
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            name = name[..^7];
        else if (name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        else if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        else
            name = Path.GetFileNameWithoutExtension(name);

        return name;
    }

    private sealed class MutableEntry
    {
        public string PackageName { get; set; } = string.Empty;
        public bool HasApk { get; set; }
        public bool HasData { get; set; }
        public long ApkBytes { get; set; }
        public long DataBytes { get; set; }
        public bool IsLikelySystem => IsLikelySystemPackage(PackageName);
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z0-9_]+)+$")]
    private static partial Regex PackageNameRegex();
}
