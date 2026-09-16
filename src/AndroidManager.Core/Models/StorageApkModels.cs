namespace AndroidManager.Core.Models;

public sealed class StorageAnalysisResult
{
    public StorageInfo Volume { get; init; } = new();
    public IReadOnlyList<StorageFolderUsage> Folders { get; init; } = [];
    public IReadOnlyList<StorageLargeFile> LargeFiles { get; init; } = [];
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public string RootPath { get; init; } = "/sdcard";
}

public sealed class StorageFolderUsage
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long SizeKb { get; init; }
    public double PercentOfUsed { get; init; }

    public string SizeFormatted => FormatKb(SizeKb);

    private static string FormatKb(long kb)
    {
        var bytes = kb * 1024d;
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824:F2} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576:F1} MB";
        return $"{kb:N0} KB";
    }
}

public sealed class StorageLargeFile
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long SizeBytes { get; init; }

    public string SizeFormatted => SizeBytes switch
    {
        >= 1_073_741_824 => $"{SizeBytes / 1_073_741_824d:F2} GB",
        >= 1_048_576 => $"{SizeBytes / 1_048_576d:F1} MB",
        _ => $"{SizeBytes / 1024d:F0} KB"
    };
}

public sealed class ApkAnalysisResult
{
    public string Source { get; init; } = "";
    public string PackageName { get; init; } = "";
    public string AppLabel { get; init; } = "";
    public string VersionName { get; init; } = "";
    public string VersionCode { get; init; } = "";
    public string MinSdk { get; init; } = "";
    public string TargetSdk { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public long ApkSizeBytes { get; init; }
    public bool IsDebuggable { get; init; }
    public bool SupportsRtl { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public IReadOnlyList<string> Activities { get; init; } = [];
    public IReadOnlyList<string> Services { get; init; } = [];
    public IReadOnlyList<string> Receivers { get; init; } = [];
    public IReadOnlyList<string> NativeLibs { get; init; } = [];
    public IReadOnlyList<ApkZipEntryInfo> LargestEntries { get; init; } = [];
    public string SignerSubject { get; init; } = "";
    public string CertificateIssuer { get; init; } = "";
    public string CertificateSha256 { get; init; } = "";
    public string SigningScheme { get; init; } = "";
    public string Notes { get; init; } = "";

    public string ApkSizeFormatted => ApkSizeBytes switch
    {
        >= 1_073_741_824 => $"{ApkSizeBytes / 1_073_741_824d:F2} GB",
        >= 1_048_576 => $"{ApkSizeBytes / 1_048_576d:F1} MB",
        > 0 => $"{ApkSizeBytes / 1024d:F0} KB",
        _ => "—"
    };

    public bool HasCertificate => !string.IsNullOrWhiteSpace(CertificateSha256) || !string.IsNullOrWhiteSpace(SignerSubject);
}

public sealed class ApkZipEntryInfo
{
    public string Name { get; init; } = "";
    public long CompressedBytes { get; init; }
    public long UncompressedBytes { get; init; }

    public string SizeFormatted => UncompressedBytes switch
    {
        >= 1_048_576 => $"{UncompressedBytes / 1_048_576d:F1} MB",
        _ => $"{UncompressedBytes / 1024d:F0} KB"
    };
}
