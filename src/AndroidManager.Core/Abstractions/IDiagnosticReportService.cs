using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IDiagnosticReportService
{
    Task<string> GeneratePdfAsync(
        DeviceInfo info,
        DeviceDiagnostics diagnostics,
        string saveDirectory,
        CancellationToken cancellationToken = default);

    Task<string> GenerateTxtAsync(
        DeviceInfo info,
        DeviceDiagnostics diagnostics,
        string saveDirectory,
        CancellationToken cancellationToken = default);
}
