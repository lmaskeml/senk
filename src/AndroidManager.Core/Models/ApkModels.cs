namespace AndroidManager.Core.Models;

public enum ApkFileType
{
    Apk,
    Xapk,
    Apks,
    Apkm,
    Unknown
}

public sealed class InstallProgress
{
    public string Stage { get; init; } = string.Empty;
    public int Percentage { get; init; }
    public string FileName { get; init; } = string.Empty;
}
