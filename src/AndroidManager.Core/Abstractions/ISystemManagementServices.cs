using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IRootAnalysisService
{
    Task<DeviceProfile> AnalyzeDeviceAsync(CancellationToken cancellationToken = default);
    Task<RootMethodRecommendation> RecommendMethodAsync(DeviceProfile profile, CancellationToken cancellationToken = default);
    Task<RootPreflightResult> RunPreflightAsync(DeviceProfile profile, RootMethodType method, CancellationToken cancellationToken = default);
}

public interface IRootMethodProvider
{
    RootMethodType MethodType { get; }
    bool SupportsDevice(DeviceProfile profile);
    Task<string> DownloadManagerApkAsync(string targetDir, IProgress<int>? progress, CancellationToken cancellationToken = default);
    Task<string> WaitForPatchResultAsync(string serial, string remoteBootPath, string workingDir, IProgress<RootStep>? progress, CancellationToken cancellationToken = default);
    Task<bool> VerifyRootAsync(string serial, CancellationToken cancellationToken = default);
}

public interface IRecoveryManagerService
{
    Task<RecoveryStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<DeviceToolResult> RebootRecoveryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovery imajını cihaza yükler.
    /// <paramref name="method"/> ile fastboot veya root (dd) yöntemi seçilir.
    /// </summary>
    Task<DeviceToolResult> FlashRecoveryImageAsync(
        string imagePath,
        RecoveryFlashMethod method,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovery içinde/Android tarafında zip yükler ve uygulatır.
    /// </summary>
    Task<DeviceToolResult> InstallRecoveryZipsAsync(
        IReadOnlyList<string> zipPaths,
        RecoveryZipInstallMethod method,
        CancellationToken cancellationToken = default);
}

public enum RecoveryFlashMethod
{
    /// <summary>Kalıcı: fastboot flash recovery</summary>
    Fastboot,
    /// <summary>Kalıcı: fastboot flash boot (Xiaomi / A-B — recovery boyutu kısıtlı ROM'larda)</summary>
    FastbootFlashBoot,
    /// <summary>Geçici: fastboot boot (RAM'den bir kez açılır, bölüme yazmaz)</summary>
    FastbootBootOnce,
    RootDd
}

public enum RecoveryZipInstallMethod
{
    /// <summary>
    /// Cihaz recovery'de ve ADB çalışır durumda: TWRP/OrangeFox komutu ile doğrudan zip kurulum.
    /// </summary>
    TWRPAdbInstall,

    /// <summary>
    /// Android tarafında root var: Zip'i cihaza kopyalayıp recovery command dosyasına yazıp cihazı recovery'e yeniden başlat.
    /// (Magisk/OTA ziplere uygundur, çoğu recovery tek paket bekler.)
    /// </summary>
    RootOtaUpdatePackage
}

public interface IRescueCenterService
{
    Task<RescueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PartitionBackupRecord>> ListPartitionBackupsAsync(string? stableDeviceId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetFastbootDevicesAsync(CancellationToken cancellationToken = default);
    Task<bool> IsDeviceInFastbootAsync(string serial, CancellationToken cancellationToken = default);
    Task<DeviceToolResult> RebootSystemAsync(CancellationToken cancellationToken = default);
    Task<DeviceToolResult> RebootFastbootAsync(CancellationToken cancellationToken = default);
}

public interface IPlatformRepository
{
    Task<int> SavePartitionBackupAsync(PartitionBackupRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PartitionBackupRecord>> ListPartitionBackupsAsync(string? stableDeviceId = null, CancellationToken cancellationToken = default);

    Task SaveScanHistoryAsync(ScanHistoryEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanHistoryEntry>> ListScanHistoryAsync(int limit = 30, CancellationToken cancellationToken = default);

    Task UpsertQuarantineItemAsync(ThreatItem threat, CancellationToken cancellationToken = default);
    Task RemoveQuarantineItemAsync(string threatId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ThreatItem>> ListQuarantineItemsAsync(CancellationToken cancellationToken = default);

    Task UpsertDevicePresenceAsync(DevicePresenceRecord record, CancellationToken cancellationToken = default);
    Task<DevicePresenceRecord?> GetDevicePresenceAsync(string stableId, CancellationToken cancellationToken = default);
    Task InvalidateDevicePresenceEndpointAsync(string stableId, CancellationToken cancellationToken = default);
}
