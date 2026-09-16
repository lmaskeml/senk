using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IResidueCleanerService
{
    /// <summary>Derin (root'lu) tarama yapılabiliyor mu? (su tespiti — izin penceresi açılmaz)</summary>
    Task<bool> RootAvailableAsync(CancellationToken cancellationToken = default);

    Task<ResidueScanResult> ScanAsync(
        ResidueScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResidueCleanResult>> CleanAsync(
        IEnumerable<ResidueItem> items,
        IProgress<CleanProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<ResidueCleanResult> CleanItemAsync(
        ResidueItem item,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetWhitelistAsync(CancellationToken cancellationToken = default);
    Task<bool> AddWhitelistAsync(string packageName, CancellationToken cancellationToken = default);
    Task<bool> RemoveWhitelistAsync(string packageName, CancellationToken cancellationToken = default);

    /// <summary>pm trim-caches — root gerekmez.</summary>
    Task<long> TrimSystemCachesAsync(CancellationToken cancellationToken = default);
}
