using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IMagiskModuleService
{
    /// <summary>
    /// Magisk uygulaması gizlenmiş (yeniden adlandırılmış) olsa da
    /// /data/adb/modules üzerinden okur — paket adına bakmaz.
    /// </summary>
    Task<MagiskModuleSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MagiskModuleInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<MagiskModuleInstallResult> InstallAsync(
        string zipPath,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    Task<bool> SetEnabledAsync(string moduleId, bool enabled, CancellationToken cancellationToken = default);

    Task<bool> RequestRemoveAsync(string moduleId, CancellationToken cancellationToken = default);
}
