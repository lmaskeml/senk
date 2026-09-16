using System.IO;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Files.Parsing;
using Serilog;

namespace AndroidManager.Files.Services;

public sealed class FileService : IFileService
{
    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly ILogger _logger;

    public FileService(IAdbService adb, IAdbSyncService sync, ILogger? logger = null)
    {
        _adb = adb;
        _sync = sync;
        _logger = logger ?? Log.ForContext<FileService>();
    }

    public async Task<IReadOnlyList<FileSystemItem>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "/sdcard" : path.TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
            normalized = "/";

        var raw = await _adb.ExecuteShellAsync(
                $"stat -c '%F|%s|%Y|%n' \"{normalized}\"/* 2>/dev/null || ls -la \"{normalized}\"",
                cancellationToken)
            .ConfigureAwait(false);

        return raw.Contains('|', StringComparison.Ordinal)
            ? FileListingParsers.ParseStatOutput(raw, normalized)
            : FileListingParsers.ParseLsOutput(raw, normalized);
    }

    public async Task PullAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (await IsRemoteDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            await PullDirectoryAsync(remotePath, localPath, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _sync.PullAsync(remotePath, localPath, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task PushAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(localPath))
        {
            await PushDirectoryAsync(localPath, remotePath, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _sync.PushAsync(localPath, remotePath, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string remotePath, CancellationToken cancellationToken = default) =>
        _adb.ExecuteShellAsync($"rm -rf \"{remotePath}\"", cancellationToken);

    public Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
        _adb.ExecuteShellAsync($"mkdir -p \"{remotePath}\"", cancellationToken);

    public async Task RenameAsync(string remotePath, string newName, CancellationToken cancellationToken = default)
    {
        var slash = remotePath.LastIndexOf('/');
        var dir = slash >= 0 ? remotePath[..slash] : "/sdcard";
        if (string.IsNullOrEmpty(dir)) dir = "/";
        var newPath = $"{dir.TrimEnd('/')}/{newName}";
        await _adb.ExecuteShellAsync($"mv \"{remotePath}\" \"{newPath}\"", cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var result = await _adb.ExecuteShellAsync(
                $"[ -e \"{remotePath}\" ] && echo YES || echo NO",
                cancellationToken)
            .ConfigureAwait(false);
        return result.Trim().Equals("YES", StringComparison.OrdinalIgnoreCase);
    }

    private async Task PullDirectoryAsync(
        string remoteDir,
        string localDir,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localDir);
        var totalFiles = await CountRemoteFilesAsync(remoteDir, cancellationToken).ConfigureAwait(false);
        var fileTotal = Math.Max(totalFiles, 1);
        var done = 0;
        var folderName = Path.GetFileName(remoteDir.TrimEnd('/'));

        progress?.Report(new TransferProgress
        {
            FileName = folderName,
            FileCounter = $"0/{fileTotal} dosya",
            BytesTransferred = 0,
            TotalBytes = fileTotal
        });

        await PullDirectoryRecursiveAsync(
                remoteDir,
                localDir,
                (fileName) =>
                {
                    done++;
                    progress?.Report(new TransferProgress
                    {
                        FileName = fileName,
                        FileCounter = $"{done}/{fileTotal} dosya",
                        BytesTransferred = done,
                        TotalBytes = fileTotal
                    });
                },
                cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new TransferProgress
        {
            FileName = folderName,
            FileCounter = $"{done}/{Math.Max(done, 1)} dosya",
            BytesTransferred = Math.Max(done, 1),
            TotalBytes = Math.Max(done, 1)
        });

        _logger.Information("Directory pulled recursively: {Remote} → {Local} ({Count} files)",
            remoteDir, localDir, done);
    }

    private async Task PullDirectoryRecursiveAsync(
        string remoteDir,
        string localDir,
        Action<string> onFileDone,
        CancellationToken cancellationToken)
    {
        var items = await ListDirectoryAsync(remoteDir, cancellationToken).ConfigureAwait(false);
        foreach (var dir in items.Where(i => i.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subLocal = Path.Combine(localDir, dir.Name);
            Directory.CreateDirectory(subLocal);
            await PullDirectoryRecursiveAsync(dir.FullPath, subLocal, onFileDone, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var file in items.Where(i => !i.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dest = Path.Combine(localDir, file.Name);
            await _sync.PullAsync(file.FullPath, dest, progress: null, cancellationToken)
                .ConfigureAwait(false);
            onFileDone(file.Name);
        }
    }

    private async Task PushDirectoryAsync(
        string localDir,
        string remoteDir,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Per-file push: reliable on Wi‑Fi ADB and reports real progress.
        // Bulk `adb push folder` often hangs at 0% with no streamed progress.
        var files = Directory.GetFiles(localDir, "*", SearchOption.AllDirectories);
        var folderName = Path.GetFileName(localDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var fileTotal = Math.Max(files.Length, 1);
        long totalBytes = 0;
        foreach (var f in files)
            totalBytes += new FileInfo(f).Length;

        // Prefer byte progress; fall back to file-count when folder is all empty files.
        var useBytes = totalBytes > 0;
        var totalUnits = useBytes ? totalBytes : fileTotal;

        await _adb.ExecuteShellAsync($"mkdir -p \"{remoteDir}\"", cancellationToken).ConfigureAwait(false);

        // Create empty subdirectories so structure is preserved even with no files.
        foreach (var dir in Directory.GetDirectories(localDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(localDir, dir).Replace('\\', '/');
            await _adb.ExecuteShellAsync(
                    $"mkdir -p \"{remoteDir.TrimEnd('/')}/{relative}\"",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report(new TransferProgress
        {
            FileName = folderName,
            FileCounter = $"0/{files.Length} dosya",
            BytesTransferred = 0,
            TotalBytes = totalUnits
        });

        long transferred = 0;
        var done = 0;
        foreach (var localFile in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(localDir, localFile).Replace('\\', '/');
            var remoteFile = $"{remoteDir.TrimEnd('/')}/{relative}";
            var remoteParent = remoteFile[..remoteFile.LastIndexOf('/')];
            var fileName = Path.GetFileName(localFile);
            var fileSize = new FileInfo(localFile).Length;

            await _adb.ExecuteShellAsync($"mkdir -p \"{remoteParent}\"", cancellationToken).ConfigureAwait(false);

            var baseTransferred = transferred;
            await _sync.PushAsync(
                    localFile,
                    remoteFile,
                    new Progress<TransferProgress>(p =>
                    {
                        if (!useBytes) return;
                        progress?.Report(new TransferProgress
                        {
                            FileName = fileName,
                            FileCounter = $"{done + 1}/{files.Length} dosya",
                            BytesTransferred = Math.Min(baseTransferred + p.BytesTransferred, totalUnits),
                            TotalBytes = totalUnits,
                            Speed = p.Speed
                        });
                    }),
                    cancellationToken)
                .ConfigureAwait(false);

            transferred += fileSize;
            done++;
            progress?.Report(new TransferProgress
            {
                FileName = fileName,
                FileCounter = $"{done}/{files.Length} dosya",
                BytesTransferred = useBytes ? transferred : done,
                TotalBytes = totalUnits
            });
        }

        progress?.Report(new TransferProgress
        {
            FileName = folderName,
            FileCounter = $"{done}/{files.Length} dosya",
            BytesTransferred = totalUnits,
            TotalBytes = totalUnits
        });

        _logger.Information("Directory pushed recursively: {Local} → {Remote} ({Count} files, {Bytes} bytes)",
            localDir, remoteDir, done, totalBytes);
    }

    private async Task<bool> IsRemoteDirectoryAsync(string remotePath, CancellationToken cancellationToken)
    {
        var raw = await _adb.ExecuteShellAsync(
                $"[ -d \"{remotePath}\" ] && echo DIR || echo NO",
                cancellationToken)
            .ConfigureAwait(false);
        return raw.Trim().StartsWith("DIR", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> CountRemoteFilesAsync(string remoteDir, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _adb.ExecuteShellAsync(
                    $"find \"{remoteDir}\" -type f 2>/dev/null | wc -l",
                    cancellationToken)
                .ConfigureAwait(false);
            if (int.TryParse(raw.Trim(), out var count))
                return Math.Max(count, 0);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Remote file count failed for {Dir}", remoteDir);
        }

        return 0;
    }
}
