using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IAppService
{
    Task<IReadOnlyList<AndroidApp>> GetInstalledAppsAsync(bool includeSystem = false, CancellationToken cancellationToken = default);
    Task<InstallResult> InstallApkAsync(string localApkPath, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    Task<bool> UninstallAsync(string packageName, CancellationToken cancellationToken = default);
    Task<string> ExtractApkAsync(string packageName, string saveDir, CancellationToken cancellationToken = default);
    Task<byte[]?> GetAppIconAsync(string packageName, CancellationToken cancellationToken = default);
    Task ForceStopAsync(string packageName, CancellationToken cancellationToken = default);
    Task ClearDataAsync(string packageName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DebloatCandidate>> GetDebloatCandidatesAsync(CancellationToken cancellationToken = default);
    Task<DebloatResult> DisableForUserAsync(string packageName, CancellationToken cancellationToken = default);
    Task<DebloatResult> RestoreForUserAsync(string packageName, CancellationToken cancellationToken = default);
}
