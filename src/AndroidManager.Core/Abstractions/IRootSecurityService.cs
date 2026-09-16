using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IRootSecurityService
{
    Task<RootStatus> GetRootStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>su testi yapar (cihazda izin penceresi açılabilir).</summary>
    Task<bool> EnableRootAsync(
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    Task<SystemScanResult> ScanSystemDeepAsync(
        SystemScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<MountStatus> GetMountStatusAsync(CancellationToken cancellationToken = default);
    Task<BootSecurityInfo> GetBootSecurityAsync(CancellationToken cancellationToken = default);

    Task<CleanResult> CleanThreatAsync(ThreatItem threat, CancellationToken cancellationToken = default);
    Task<CleanResult> RemoveSystemAppAsync(ThreatItem threat, CancellationToken cancellationToken = default);
    Task<CleanResult> QuarantineFileAsync(ThreatItem threat, CancellationToken cancellationToken = default);

    Task<CleanResult> RestoreHostsAsync(CancellationToken cancellationToken = default);
    Task<bool> KillProcessAsync(string packageName, CancellationToken cancellationToken = default);
    Task<CleanResult> ClearAppDataAsync(string packageName, CancellationToken cancellationToken = default);
    Task<bool> SetSystemMountAsync(bool writable, CancellationToken cancellationToken = default);
    Task<bool> RebootDeviceAsync(CancellationToken cancellationToken = default);
}
