namespace AndroidManager.Core.Models;

public sealed class AndroidApp
{
    public string PackageName { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string VersionName { get; set; } = string.Empty;
    public int VersionCode { get; set; }
    public string ApkPath { get; set; } = string.Empty;
    public long ApkSize { get; set; }
    public bool IsSystemApp { get; set; }
    public DateTime InstallDate { get; set; }
    public byte[]? IconBytes { get; set; }

    public string ApkSizeFormatted => ApkSize switch
    {
        < 1_048_576 => $"{ApkSize / 1024.0:F1} KB",
        < 1_073_741_824 => $"{ApkSize / 1_048_576.0:F1} MB",
        _ => $"{ApkSize / 1_073_741_824.0:F2} GB"
    };
}

public sealed class InstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class AdbAppBackupProgress
{
    public string Message { get; init; } = string.Empty;
    public int Attempt { get; init; }
    public int MaxAttempts { get; init; }
    public int ElapsedSeconds { get; init; }
    public int FileSizeKb { get; init; }
    public bool WaitingForUserConfirmation { get; init; }
}

public sealed class AdbAppBackupResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? OutputAbPath { get; init; }
    public int ExitCode { get; init; }
    public int AttemptsUsed { get; init; }
}
