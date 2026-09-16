using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Services;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class AdbSyncService : IAdbSyncService
{
    /// <summary>
    /// Shared with <see cref="AdbService.DeviceChannel"/> — shell + sync must not overlap on Wi‑Fi.
    /// </summary>
    private readonly SemaphoreSlim _channel;

    private readonly AdbService _adbService;
    private readonly ILogger _logger;
    private long _lastReportTicks;

    public AdbSyncService(AdbService adbService, ILogger? logger = null)
    {
        _adbService = adbService;
        _channel = adbService.DeviceChannel;
        _logger = logger ?? Log.ForContext<AdbSyncService>();
    }

    public async Task PullAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        await _channel.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var expectedSize = await TryGetRemoteSizeAsync(remotePath, cancellationToken).ConfigureAwait(false);
            ReportIndeterminate(progress, Path.GetFileName(remotePath), expectedSize);

            Exception? lastError = null;

            // 1) Official adb pull CLI — most reliable on Wi‑Fi / Android 14 (SyncService often fails OpenAsync).
            foreach (var candidate in EnumerateRemotePathCandidates(remotePath))
            {
                try
                {
                    TryDelete(localPath);
                    await PullViaAdbCliAsync(candidate, localPath, progress, expectedSize, cancellationToken)
                        .ConfigureAwait(false);
                    if (IsSuccessfulPull(localPath, expectedSize))
                    {
                        var bytes = GetLocalByteCount(localPath);
                        ReportDone(progress, Path.GetFileName(remotePath), bytes);
                        _logger.Information("Pulled via adb CLI {Remote} → {Local} ({Bytes} bytes)",
                            candidate, localPath, bytes);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.Debug(ex, "adb pull CLI failed for {Remote}", candidate);
                    TryDelete(localPath);
                }
            }

            // 2) Stage to /data/local/tmp (shell can often read what sync cannot), then pull.
            try
            {
                TryDelete(localPath);
                if (await PullViaTmpStageAsync(remotePath, localPath, expectedSize, cancellationToken)
                        .ConfigureAwait(false))
                {
                    var bytes = GetLocalByteCount(localPath);
                    ReportDone(progress, Path.GetFileName(remotePath), bytes);
                    _logger.Information("Pulled via tmp stage {Remote} → {Local} ({Bytes} bytes)",
                        remotePath, localPath, bytes);
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.Debug(ex, "tmp-stage pull failed for {Remote}", remotePath);
                TryDelete(localPath);
            }

            // 3) exec-out cat
            foreach (var candidate in EnumerateRemotePathCandidates(remotePath))
            {
                try
                {
                    TryDelete(localPath);
                    await PullViaExecOutAsync(candidate, localPath, progress, cancellationToken)
                        .ConfigureAwait(false);
                    if (IsSuccessfulPull(localPath, expectedSize))
                    {
                        var bytes = GetLocalByteCount(localPath);
                        ReportDone(progress, Path.GetFileName(remotePath), bytes);
                        _logger.Information("Pulled via exec-out {Remote} → {Local} ({Bytes} bytes)",
                            candidate, localPath, bytes);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.Debug(ex, "exec-out pull failed for {Remote}", candidate);
                    TryDelete(localPath);
                }
            }

            // 4) Library sync last (often broken on wireless debugging).
            try
            {
                TryDelete(localPath);
                await PullViaSyncAsync(remotePath, localPath, progress, cancellationToken).ConfigureAwait(false);
                if (IsSuccessfulPull(localPath, expectedSize))
                {
                    ReportDone(progress, Path.GetFileName(remotePath), GetLocalByteCount(localPath));
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.Debug(ex, "Sync pull failed for {Remote}", remotePath);
                TryDelete(localPath);
            }

            var detail = lastError is null ? string.Empty : $" ({lastError.Message})";
            throw new IOException(
                $"Dosya indirilemedi: {remotePath}{detail}. " +
                "Android 14 kablosuz ADB bazen engeller — USB ile tekrar deneyin.");
        }
        finally
        {
            _channel.Release();
        }
    }

    public async Task PushAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(localPath))
        {
            await PushDirectoryViaCliAsync(localPath, remotePath, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!File.Exists(localPath))
            throw new FileNotFoundException("Yerel dosya bulunamadı.", localPath);

        var localLen = new FileInfo(localPath).Length;
        var fileName = Path.GetFileName(localPath);

        await _channel.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Empty files are valid (placeholders); create remotely without adb push body.
            if (localLen <= 0)
            {
                var remoteDir = GetRemoteDirectory(remotePath);
                if (!string.IsNullOrEmpty(remoteDir))
                    await _adbService.ExecuteShellUngatedAsync($"mkdir -p \"{remoteDir}\"", cancellationToken)
                        .ConfigureAwait(false);

                await _adbService.ExecuteShellUngatedAsync(
                        $": > \"{remotePath}\" && chmod 0644 \"{remotePath}\"",
                        cancellationToken)
                    .ConfigureAwait(false);

                ReportDone(progress, fileName, 0);
                _logger.Information("Created empty remote file {Remote}", remotePath);
                return;
            }

            ReportIndeterminate(progress, fileName, localLen);
            Exception? lastError = null;

            // 1) Official adb push
            foreach (var candidate in EnumerateRemotePathCandidates(remotePath))
            {
                try
                {
                    var remoteDir = GetRemoteDirectory(candidate);
                    if (!string.IsNullOrEmpty(remoteDir))
                        await _adbService.ExecuteShellUngatedAsync($"mkdir -p \"{remoteDir}\"", cancellationToken)
                            .ConfigureAwait(false);

                    await PushViaAdbCliAsync(localPath, candidate, progress, localLen, cancellationToken)
                        .ConfigureAwait(false);
                    var remoteSize = await TryGetRemoteSizeAsync(candidate, cancellationToken).ConfigureAwait(false);
                    if (remoteSize is null || remoteSize >= localLen)
                    {
                        ReportDone(progress, fileName, localLen);
                        _logger.Information("Pushed via adb CLI {Local} → {Remote}", localPath, candidate);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.Debug(ex, "adb push CLI failed for {Remote}", candidate);
                }
            }

            // 2) Push to tmp then shell cp
            try
            {
                var tmp = $"/data/local/tmp/am_push_{Guid.NewGuid():N}";
                await PushViaAdbCliAsync(localPath, tmp, progress, localLen, cancellationToken).ConfigureAwait(false);
                var remoteDir = GetRemoteDirectory(remotePath);
                if (!string.IsNullOrEmpty(remoteDir))
                    await _adbService.ExecuteShellUngatedAsync($"mkdir -p \"{remoteDir}\"", cancellationToken)
                        .ConfigureAwait(false);

                await _adbService.ExecuteShellUngatedAsync(
                        $"cp \"{tmp}\" \"{remotePath}\" && chmod 0644 \"{remotePath}\"; rm -f \"{tmp}\"",
                        cancellationToken)
                    .ConfigureAwait(false);

                var remoteSize = await TryGetRemoteSizeAsync(remotePath, cancellationToken).ConfigureAwait(false);
                if (remoteSize is null || remoteSize >= localLen)
                {
                    ReportDone(progress, fileName, localLen);
                    _logger.Information("Pushed via tmp stage {Local} → {Remote}", localPath, remotePath);
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.Debug(ex, "tmp-stage push failed for {Remote}", remotePath);
            }

            // 3) Library sync
            try
            {
                var device = _adbService.GetSelectedDeviceData();
                await using var stream = File.OpenRead(localPath);
                using var sync = new SyncService(new AdbClient(), device);
                await sync.OpenAsync(cancellationToken).ConfigureAwait(false);

                var started = DateTime.UtcNow;
                var timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(localPath));
                Volatile.Write(ref _lastReportTicks, 0);
                await sync.PushAsync(
                        stream,
                        remotePath,
                        UnixFileStatus.DefaultFileMode,
                        timestamp,
                        e => ReportProgress(progress, fileName, e, started),
                        false,
                        cancellationToken)
                    .ConfigureAwait(false);

                ReportDone(progress, fileName, localLen);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.Debug(ex, "Sync push failed for {Remote}", remotePath);
            }

            var detail = lastError is null ? string.Empty : $" ({lastError.Message})";
            throw new IOException($"Dosya gönderilemedi: {remotePath}{detail}");
        }
        finally
        {
            _channel.Release();
        }
    }

    private async Task<bool> PullViaTmpStageAsync(
        string remotePath,
        string localPath,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        var tmp = $"/data/local/tmp/am_pull_{Guid.NewGuid():N}";
        try
        {
            // Prefer cp -f; fall back to cat redirect if cp fails on some OEMs.
            var copyOut = await _adbService.ExecuteShellUngatedAsync(
                    $"cp -f \"{remotePath}\" \"{tmp}\" 2>/dev/null || cat \"{remotePath}\" > \"{tmp}\"; " +
                    $"chmod 0644 \"{tmp}\" 2>/dev/null; stat -c %s \"{tmp}\" 2>/dev/null",
                    TimeSpan.FromMinutes(10),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!long.TryParse(copyOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(),
                    out var stagedSize) || stagedSize <= 0)
                return false;

            await PullViaAdbCliAsync(tmp, localPath, progress: null, expectedSize: stagedSize, cancellationToken)
                .ConfigureAwait(false);
            return IsSuccessfulPull(localPath, expectedSize ?? stagedSize);
        }
        finally
        {
            try
            {
                await _adbService.ExecuteShellUngatedAsync($"rm -f \"{tmp}\"", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore cleanup
            }
        }
    }

    private async Task PullViaAdbCliAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        var serial = RequireSerial();
        var adb = RequireAdbExe();
        var fileName = Path.GetFileName(remotePath.TrimEnd('/'));
        var args = $"-s \"{serial}\" pull \"{remotePath}\" \"{localPath}\"";
        var started = DateTime.UtcNow;
        var (exit, stdout, stderr) = await RunAdbProcessAsync(
                adb,
                args,
                cancellationToken,
                line => TryReportCliPercent(progress, fileName, line, expectedSize, started))
            .ConfigureAwait(false);
        if (exit != 0)
            throw new IOException($"adb pull failed (exit {exit}): {TrimErr(stderr, stdout)}");
    }

    private async Task PushViaAdbCliAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var serial = RequireSerial();
        var adb = RequireAdbExe();
        var fileName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var args = $"-s \"{serial}\" push \"{localPath}\" \"{remotePath}\"";
        var started = DateTime.UtcNow;
        var (exit, stdout, stderr) = await RunAdbProcessAsync(
                adb,
                args,
                cancellationToken,
                line => TryReportCliPercent(progress, fileName, line, expectedSize > 0 ? expectedSize : null, started))
            .ConfigureAwait(false);
        if (exit != 0)
            throw new IOException($"adb push failed (exit {exit}): {TrimErr(stderr, stdout)}");
    }

    private void TryReportCliPercent(
        IProgress<TransferProgress>? progress,
        string fileName,
        string line,
        long? totalBytes,
        DateTime startedUtc)
    {
        if (progress is null || string.IsNullOrWhiteSpace(line))
            return;

        // adb often prints: "[ 42%] /path/file"
        var open = line.IndexOf('[', StringComparison.Ordinal);
        var close = open >= 0 ? line.IndexOf('%', open) : -1;
        if (open < 0 || close < 0)
            return;

        var num = line[(open + 1)..close].Trim();
        if (!int.TryParse(num, out var percent))
            return;

        percent = Math.Clamp(percent, 0, 100);
        if (totalBytes is > 0)
        {
            var transferred = totalBytes.Value * percent / 100;
            ReportByteProgress(progress, fileName, transferred, totalBytes.Value, startedUtc);
        }
        else
        {
            ReportByteProgress(progress, fileName, percent, 100, startedUtc);
        }
    }

    private async Task PullViaSyncAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var device = _adbService.GetSelectedDeviceData();
        await using (var stream = File.Open(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var sync = new SyncService(new AdbClient(), device);
            await sync.OpenAsync(cancellationToken).ConfigureAwait(false);

            var started = DateTime.UtcNow;
            Volatile.Write(ref _lastReportTicks, 0);
            await sync.PullAsync(
                    remotePath,
                    stream,
                    e => ReportProgress(progress, Path.GetFileName(remotePath), e, started),
                    false,
                    cancellationToken)
                .ConfigureAwait(false);

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PullViaExecOutAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var serial = RequireSerial();
        var adbPath = RequireAdbExe();

        var psi = new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = $"-s \"{serial}\" exec-out cat {ShellQuote(remotePath)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("adb exec-out başlatılamadı.");
        try
        {
            await using var fs = File.Create(localPath);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var total = 0L;
                var started = DateTime.UtcNow;
                var stdout = process.StandardOutput.BaseStream;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await stdout.ReadAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0)
                        break;

                    await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    ReportByteProgress(progress, Path.GetFileName(remotePath), total, Math.Max(total, 1), started);
                }

                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    var err = await process.StandardError.ReadToEndAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    throw new IOException($"adb exec-out exit {process.ExitCode}: {err}");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDelete(localPath);
            throw;
        }
        finally
        {
            if (!process.HasExited)
                TryKill(process);
        }
    }

    private async Task<long?> TryGetRemoteSizeAsync(string remotePath, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var candidate in EnumerateRemotePathCandidates(remotePath))
            {
                var raw = await _adbService.ExecuteShellUngatedAsync(
                        $"stat -c %s \"{candidate}\" 2>/dev/null",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (long.TryParse(raw.Trim(), out var size) && size >= 0)
                    return size;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "stat size failed for {Path}", remotePath);
        }

        return null;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAdbProcessAsync(
        string adbPath,
        string arguments,
        CancellationToken cancellationToken,
        Action<string>? onStderrLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("adb başlatılamadı.");

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        async Task ReadStdoutAsync()
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                stdoutBuilder.AppendLine(line);
            }
        }

        async Task ReadStderrAsync()
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                stderrBuilder.AppendLine(line);
                onStderrLine?.Invoke(line);
            }
        }

        var stdoutTask = ReadStdoutAsync();
        var stderrTask = ReadStderrAsync();

        try
        {
            // Large folder/file transfers over Wi‑Fi can take a while; still bound so UI cannot hang forever.
            await ProcessWaitHelper.WaitOrKillAsync(process, TimeSpan.FromMinutes(60), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("adb push/pull zaman aşımına uğradı (60 dk). Bağlantıyı kontrol edip tekrar deneyin.");
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    private string RequireSerial() =>
        _adbService.SelectedDevice?.Serial
        ?? throw new InvalidOperationException("Cihaz seçili değil.");

    private static string RequireAdbExe()
    {
        var adb = AdbService.ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adb) || !File.Exists(adb))
            throw new FileNotFoundException("adb.exe bulunamadı.", adb);
        return adb;
    }

    private static IEnumerable<string> EnumerateRemotePathCandidates(string remotePath)
    {
        yield return remotePath;

        const string emulated = "/storage/emulated/0/";
        if (remotePath.StartsWith(emulated, StringComparison.OrdinalIgnoreCase))
            yield return "/sdcard/" + remotePath[emulated.Length..];

        if (remotePath.StartsWith("/sdcard/", StringComparison.OrdinalIgnoreCase))
            yield return "/storage/emulated/0/" + remotePath["/sdcard/".Length..];
    }

    private static string GetRemoteDirectory(string remotePath)
    {
        var idx = remotePath.LastIndexOf('/');
        if (idx <= 0)
            return string.Empty;
        return remotePath[..idx];
    }

    private async Task PushDirectoryViaCliAsync(
        string localDir,
        string remoteDir,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _channel.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var totalBytes = GetLocalByteCount(localDir);
            var folderName = Path.GetFileName(localDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            ReportIndeterminate(progress, folderName, totalBytes > 0 ? totalBytes : null);

            // Create dest, then push contents (trailing \.) to avoid nesting folder/folder.
            await _adbService.ExecuteShellUngatedAsync($"mkdir -p \"{remoteDir}\"", cancellationToken)
                .ConfigureAwait(false);
            var contentSrc = localDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar
                             + ".";
            await PushViaAdbCliAsync(contentSrc, remoteDir, progress, totalBytes, cancellationToken)
                .ConfigureAwait(false);
            ReportDone(progress, folderName, totalBytes);
            _logger.Information("Pushed directory via adb CLI {Local} → {Remote}", localDir, remoteDir);
        }
        finally
        {
            _channel.Release();
        }
    }

    private static bool IsSuccessfulPull(string localPath, long? expectedSize)
    {
        // adb pull of a remote directory creates a local directory tree.
        if (Directory.Exists(localPath))
            return true;

        if (!File.Exists(localPath))
            return false;

        var length = new FileInfo(localPath).Length;
        if (length <= 0)
            return false;

        try
        {
            Span<byte> header = stackalloc byte[4];
            using var fs = File.OpenRead(localPath);
            if (fs.Read(header) >= 4
                && header[0] == (byte)'c'
                && header[1] == (byte)'a'
                && header[2] == (byte)'t'
                && header[3] == (byte)':')
                return false;
        }
        catch
        {
            return false;
        }

        // If we know remote size, require at least 90% (some OEMs report slight mismatch).
        if (expectedSize is > 0 && length < expectedSize.Value * 9 / 10)
            return false;

        return true;
    }

    private static long GetLocalByteCount(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                long total = 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    total += new FileInfo(file).Length;
                return total;
            }

            if (File.Exists(path))
                return new FileInfo(path).Length;
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static string ShellQuote(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "''";
        return "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string TrimErr(string stderr, string stdout)
    {
        var s = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        s = s.Trim();
        return s.Length <= 300 ? s : s[..300] + "…";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }

    private void ReportIndeterminate(IProgress<TransferProgress>? progress, string fileName, long? total)
    {
        progress?.Report(new TransferProgress
        {
            FileName = fileName,
            BytesTransferred = 0,
            TotalBytes = total is > 0 ? total.Value : 0,
            Speed = string.Empty
        });
    }

    private void ReportDone(IProgress<TransferProgress>? progress, string fileName, long bytes)
    {
        var total = bytes > 0 ? bytes : 1;
        progress?.Report(new TransferProgress
        {
            FileName = fileName,
            BytesTransferred = total,
            TotalBytes = total,
            Speed = string.Empty
        });
    }

    private void ReportByteProgress(
        IProgress<TransferProgress>? progress,
        string fileName,
        long transferred,
        long total,
        DateTime startedUtc)
    {
        if (progress is null) return;

        var currentTicks = DateTime.UtcNow.Ticks;
        if (currentTicks - Volatile.Read(ref _lastReportTicks) < TimeSpan.FromMilliseconds(150).Ticks)
            return;

        Volatile.Write(ref _lastReportTicks, currentTicks);
        var elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
        progress.Report(new TransferProgress
        {
            FileName = fileName,
            BytesTransferred = transferred,
            TotalBytes = total,
            Speed = elapsed > 0 ? $"{transferred / elapsed / 1024:F0} KB/s" : string.Empty
        });
    }

    private void ReportProgress(
        IProgress<TransferProgress>? progress,
        string fileName,
        SyncProgressChangedEventArgs e,
        DateTime startedUtc)
    {
        if (progress is null) return;

        var currentTicks = DateTime.UtcNow.Ticks;
        var elapsedTicksSinceLastReport = currentTicks - Volatile.Read(ref _lastReportTicks);
        var isCompleted = e.ReceivedBytesSize >= e.TotalBytesToReceive;

        if (!isCompleted && elapsedTicksSinceLastReport < TimeSpan.FromMilliseconds(150).Ticks)
            return;

        Volatile.Write(ref _lastReportTicks, currentTicks);

        var elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
        var transferred = (long)e.ReceivedBytesSize;
        var total = (long)e.TotalBytesToReceive;
        progress.Report(new TransferProgress
        {
            FileName = fileName,
            BytesTransferred = transferred,
            TotalBytes = total,
            Speed = elapsed > 0 ? $"{transferred / elapsed / 1024:F0} KB/s" : string.Empty
        });
    }
}
