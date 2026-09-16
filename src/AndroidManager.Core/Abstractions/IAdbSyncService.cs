using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

/// <summary>
/// File sync over ADB. Implementation lives in Device module so AdvancedSharpAdbClient stays encapsulated.
/// </summary>
public interface IAdbSyncService
{
    Task PullAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task PushAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
}
