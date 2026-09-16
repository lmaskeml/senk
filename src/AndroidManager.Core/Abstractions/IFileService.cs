using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IFileService
{
    Task<IReadOnlyList<FileSystemItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task PullAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task PushAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(string remotePath, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default);
    Task RenameAsync(string remotePath, string newName, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken = default);
}
