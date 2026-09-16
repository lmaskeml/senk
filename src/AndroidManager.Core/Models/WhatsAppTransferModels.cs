namespace AndroidManager.Core.Models;

public enum WhatsAppTransferPhase
{
    Idle,
    ExtractingAndroid,
    AndroidReady,
    ConvertingDatabase,
    WaitingForIphone,
    PreparingIosBackup,
    InjectingIos,
    RestoringIos,
    Completed,
    Failed
}

public sealed class WhatsAppTransferProgress
{
    public WhatsAppTransferPhase Phase { get; init; }
    public int Percent { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }
}

public sealed class WhatsAppAndroidExtractResult
{
    public required string SessionDirectory { get; init; }
    public string? MsgStorePath { get; init; }
    public string? MediaDirectory { get; init; }
    public int MessageCount { get; init; }
    public int ChatCount { get; init; }
    public bool UsedRootCopy { get; init; }
    public bool UsedAdbBackup { get; init; }
    public bool UsedDowngrade { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class WhatsAppConversionResult
{
    public required string ChatStoragePath { get; init; }
    public int ConvertedMessages { get; init; }
    public int ConvertedChats { get; init; }
    public int SkippedMessages { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class WhatsAppIosInjectResult
{
    public required string BackupDirectory { get; init; }
    public int MediaFilesCopied { get; init; }
    public bool RestoreTriggered { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record WhatsAppTransferSession
{
    public required string SessionDirectory { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public string? AndroidDeviceModel { get; init; }
    public string? AndroidDeviceSerial { get; init; }
    public string? MsgStorePath { get; init; }
    public string? MediaDirectory { get; init; }
    public string? ChatStoragePath { get; init; }
    public string? IosDeviceUdid { get; init; }
    public string? IosDeviceName { get; init; }
    public WhatsAppTransferPhase Phase { get; init; } = WhatsAppTransferPhase.Idle;
    public int MessageCount { get; init; }
    public int ChatCount { get; init; }
}

public sealed class IosDeviceInfo
{
    public required string Udid { get; init; }
    public string? DeviceName { get; set; }
    public string? ProductType { get; set; }
    public string? ProductVersion { get; set; }
}
