using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IStorageAnalyzerService
{
    Task<StorageAnalysisResult> AnalyzeAsync(
        string rootPath = "/sdcard",
        long largeFileMinBytes = 50L * 1024 * 1024,
        CancellationToken cancellationToken = default);
}

public interface IApkAnalyzerService
{
    Task<ApkAnalysisResult> AnalyzeLocalApkAsync(string localApkPath, CancellationToken cancellationToken = default);
    Task<ApkAnalysisResult> AnalyzeInstalledPackageAsync(string packageName, CancellationToken cancellationToken = default);
}
