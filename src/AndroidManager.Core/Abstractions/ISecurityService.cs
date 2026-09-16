using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface ISecurityService
{
    Task<ScanSession> StartFullScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ScanSession> StartQuickScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ScanSession> StartRescueScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<CleanResult> CleanThreatAsync(
        ThreatItem threat,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CleanResult>> CleanAllThreatsAsync(
        IEnumerable<ThreatItem> threats,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> QuarantineAsync(
        ThreatItem threat,
        CancellationToken cancellationToken = default);

    Task<bool> RestoreFromQuarantineAsync(
        ThreatItem threat,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ThreatItem>> GetQuarantineListAsync(
        CancellationToken cancellationToken = default);

    Task<bool> UpdateDefinitionsAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SecurityStatus> GetSecurityStatusAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScanHistoryEntry>> GetScanHistoryAsync(
        CancellationToken cancellationToken = default);
}
