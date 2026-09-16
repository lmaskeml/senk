using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IWhatsAppTransferService
{
    string DefaultSessionRoot { get; }

    Task<WhatsAppAndroidExtractResult> ExtractFromAndroidAsync(
        string? sessionDirectory = null,
        bool includeMedia = true,
        bool tryAdbBackup = true,
        bool tryDowngradeOnBackupFailure = true,
        string? legacyApkPath = null,
        IProgress<WhatsAppTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<WhatsAppConversionResult> ConvertToIosDatabaseAsync(
        WhatsAppTransferSession session,
        IProgress<WhatsAppTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<WhatsAppIosInjectResult> InjectToIphoneAsync(
        WhatsAppTransferSession session,
        string iosDeviceUdid,
        bool triggerRestore = true,
        IProgress<WhatsAppTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WhatsAppTransferSession>> ListSavedSessionsAsync(
        CancellationToken cancellationToken = default);

    Task<WhatsAppTransferSession?> LoadSessionAsync(
        string sessionDirectory,
        CancellationToken cancellationToken = default);

    Task SaveSessionAsync(
        WhatsAppTransferSession session,
        CancellationToken cancellationToken = default);
}

public interface IIosDeviceService
{
    bool ToolsAvailable { get; }

    /// <summary>Where the app looks for idevice_*.exe (for UI hints).</summary>
    string ToolsSearchHint { get; }

    /// <summary>Apple Mobile Device Service kurulu ve çalışıyor mu?</summary>
    bool IsAppleMobileDeviceServiceAvailable { get; }

    Task<IReadOnlyList<IosDeviceInfo>> GetConnectedDevicesAsync(
        CancellationToken cancellationToken = default);

    Task<string> CreateUnencryptedBackupAsync(
        string udid,
        string backupDirectory,
        IProgress<WhatsAppTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task RestoreBackupAsync(
        string udid,
        string backupDirectory,
        IProgress<WhatsAppTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
