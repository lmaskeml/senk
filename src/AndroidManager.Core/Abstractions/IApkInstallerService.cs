using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

/// <summary>
/// Installs .apk / .xapk / .apks / .apkm with staged progress.
/// <see cref="IAppService.InstallApkAsync"/> remains the primary entry used by the UI today.
/// </summary>
public interface IApkInstallerService
{
    Task<InstallResult> InstallAsync(
        string filePath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    bool CanInstall(string filePath);

    ApkFileType GetFileType(string filePath);
}
