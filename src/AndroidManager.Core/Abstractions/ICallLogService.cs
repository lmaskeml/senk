using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface ICallLogService
{
    Task<IReadOnlyList<CallLogEntry>> GetCallsAsync(
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CallLogEntry>> SearchAsync(
        string query,
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task<string> ExportToJsonAsync(
        string savePath,
        CancellationToken cancellationToken = default);

    Task<string> ExportToCsvAsync(
        string savePath,
        CancellationToken cancellationToken = default);

    Task<string> ExportToExcelAsync(
        string savePath,
        CancellationToken cancellationToken = default);
}
