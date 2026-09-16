namespace AndroidManager.Core.Models;

public sealed class SplitPackageContents
{
    public string SourcePath { get; init; } = string.Empty;
    public string ExtractDirectory { get; init; } = string.Empty;
    public string? PackageName { get; init; }
    public IReadOnlyList<string> ApkPaths { get; init; } = [];
    public IReadOnlyList<ObbFile> ObbFiles { get; init; } = [];
}

public sealed class ObbFile
{
    public string LocalPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    /// <summary>Relative path under Android/obb when known (e.g. com.foo/main.obb).</summary>
    public string? RelativeRemotePath { get; init; }
}

public static class AndroidPackageFormats
{
    public static readonly HashSet<string> SplitArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xapk", ".apks", ".apkm"
    };

    public static readonly HashSet<string> InstallableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".apk", ".xapk", ".apks", ".apkm"
    };

    public static bool IsSplitArchive(string path) =>
        SplitArchiveExtensions.Contains(Path.GetExtension(path));

    public static bool IsInstallablePackage(string path) =>
        InstallableExtensions.Contains(Path.GetExtension(path));

    public static ApkFileType GetFileType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".apk" => ApkFileType.Apk,
            ".xapk" => ApkFileType.Xapk,
            ".apks" => ApkFileType.Apks,
            ".apkm" => ApkFileType.Apkm,
            _ => ApkFileType.Unknown
        };

    public const string OpenFileFilter =
        "Android paketleri|*.apk;*.xapk;*.apks;*.apkm|APK (*.apk)|*.apk|Split (*.xapk;*.apks;*.apkm)|*.xapk;*.apks;*.apkm|Tüm Dosyalar|*.*";
}
