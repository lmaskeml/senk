using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface ISmsService
{
    Task<IReadOnlyList<SmsMessage>> GetMessagesAsync(
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SmsThread>> GetThreadsAsync(
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SmsMessage>> GetThreadMessagesAsync(
        string address,
        int limit = 200,
        CancellationToken cancellationToken = default);

    Task<SmsSendResult> SendAsync(
        string address,
        string body,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SmsMessage>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>Writes all conversations to a JSON file under <paramref name="savePath"/> (folder or full file path).</summary>
    Task<string> ExportToJsonAsync(
        string savePath,
        CancellationToken cancellationToken = default);

    /// <summary>Writes all conversations to a CSV file under <paramref name="savePath"/> (folder or full file path).</summary>
    Task<string> ExportToCsvAsync(
        string savePath,
        CancellationToken cancellationToken = default);
}
