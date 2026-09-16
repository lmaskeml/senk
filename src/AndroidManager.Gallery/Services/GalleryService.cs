using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Parsing;
using AndroidManager.Gallery.Imaging;
using Serilog;

namespace AndroidManager.Gallery.Services;

public sealed class GalleryService : IGalleryService
{
    private static readonly string ThumbRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidManager",
        "GalleryThumbs");

    private static readonly string OriginalCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidManager",
        "GalleryCache");

    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly ILogger _logger;
    /// <summary>Limits concurrent thumbnail work (MediaStore mini thumbs).</summary>
    private readonly SemaphoreSlim _thumbGate = new(3, 3);
    /// <summary>Original/preview pulls — separate so thumbs never block preview for minutes.</summary>
    private readonly SemaphoreSlim _originalGate = new(1, 1);
    /// <summary>After first successful content-URI pull, skip slow sync/cat fallbacks.</summary>
    private int _preferContentUri; // 0 unknown, 1 yes, -1 no


    public GalleryService(IAdbService adb, IAdbSyncService sync, ILogger? logger = null)
    {
        _adb = adb;
        _sync = sync;
        _logger = logger ?? Log.ForContext<GalleryService>();
        Directory.CreateDirectory(ThumbRoot);
        Directory.CreateDirectory(OriginalCacheRoot);
    }

    public async Task<IReadOnlyList<GalleryMediaItem>> GetMediaAsync(
        GalleryMediaKind? kind = null,
        int limit = 1000,
        CancellationToken cancellationToken = default,
        MediaSortOrder sort = MediaSortOrder.DateDesc)
    {
        EnsureDevice();
        var take = Math.Clamp(limit, 1, 5000);

        Task<IReadOnlyList<GalleryMediaItem>>? imagesTask = null;
        Task<IReadOnlyList<GalleryMediaItem>>? videosTask = null;

        if (kind is null or GalleryMediaKind.Image)
        {
            imagesTask = QueryMediaAsync(
                "content://media/external/images/media",
                "_id:_data:_display_name:mime_type:_size:datetaken:date_added:width:height:bucket_display_name",
                GalleryMediaKind.Image,
                cancellationToken);
        }

        if (kind is null or GalleryMediaKind.Video)
        {
            videosTask = QueryMediaAsync(
                "content://media/external/video/media",
                "_id:_data:_display_name:mime_type:_size:datetaken:date_added:width:height:duration:bucket_display_name",
                GalleryMediaKind.Video,
                cancellationToken);
        }

        var items = new List<GalleryMediaItem>();
        if (imagesTask is not null && videosTask is not null)
        {
            await Task.WhenAll(imagesTask, videosTask).ConfigureAwait(false);
            items.AddRange(await imagesTask.ConfigureAwait(false));
            items.AddRange(await videosTask.ConfigureAwait(false));
        }
        else if (imagesTask is not null)
        {
            items.AddRange(await imagesTask.ConfigureAwait(false));
        }
        else if (videosTask is not null)
        {
            items.AddRange(await videosTask.ConfigureAwait(false));
        }

        return ApplySort(items.Where(i => i.HasRemotePath), sort)
            .Take(take)
            .ToList();
    }

    public async Task<IReadOnlyList<MediaAlbum>> GetAlbumsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();

        var photos = await QueryMediaAsync(
                "content://media/external/images/media",
                "_id:_data:_display_name:mime_type:_size:datetaken:date_added:width:height:bucket_display_name",
                GalleryMediaKind.Image,
                cancellationToken)
            .ConfigureAwait(false);

        var videos = await QueryMediaAsync(
                "content://media/external/video/media",
                "_id:_data:_display_name:mime_type:_size:datetaken:date_added:width:height:duration:bucket_display_name",
                GalleryMediaKind.Video,
                cancellationToken)
            .ConfigureAwait(false);

        var albums = new Dictionary<string, (int Count, string Cover, string Path)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in photos.Concat(videos).Where(i => i.HasRemotePath))
        {
            var name = string.IsNullOrWhiteSpace(item.Album)
                ? InferAlbumName(item.RemotePath)
                : item.Album.Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = "Diğer";

            var dir = GetParentPath(item.RemotePath);
            if (!albums.TryGetValue(name, out var existing))
            {
                albums[name] = (1, item.RemotePath, dir);
            }
            else
            {
                albums[name] = (existing.Count + 1, existing.Cover, existing.Path);
            }
        }

        return albums
            .Select(kv => new MediaAlbum
            {
                Name = kv.Key,
                Path = kv.Value.Path,
                Count = kv.Value.Count,
                CoverPath = kv.Value.Cover
            })
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GalleryMediaItem>> GetAlbumItemsAsync(
        string albumPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(albumPath))
            return Array.Empty<GalleryMediaItem>();

        var all = await GetMediaAsync(
                kind: null,
                limit: 5000,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var pathKey = albumPath.Trim().TrimEnd('/');
        var nameKey = Path.GetFileName(pathKey);

        return all
            .Where(i =>
            {
                var parent = GetParentPath(i.RemotePath);
                if (parent.StartsWith(pathKey, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(parent, pathKey, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrWhiteSpace(nameKey)
                    && i.Album.Equals(nameKey, StringComparison.OrdinalIgnoreCase))
                    return true;

                return i.Album.Equals(albumPath.Trim(), StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(i => i.DateTakenMs)
            .ToList();
    }

    public async Task SaveToLocalAsync(
        IEnumerable<GalleryMediaItem> items,
        string localDir,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool convertImagesToJpeg = false)
    {
        EnsureDevice();
        ArgumentException.ThrowIfNullOrWhiteSpace(localDir);
        Directory.CreateDirectory(localDir);

        var list = items.Where(i => i.HasRemotePath || i.Id > 0).ToList();
        if (list.Count == 0)
            return;

        var started = DateTime.UtcNow;
        long bytesDone = 0;
        long bytesTotal = list.Sum(i => Math.Max(i.SizeBytes, 1));

        for (var i = 0; i < list.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = list[i];
            var fileName = SanitizeFileName(
                string.IsNullOrWhiteSpace(item.DisplayName)
                    ? Path.GetFileName(item.RemotePath)
                    : item.DisplayName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = $"media_{item.Id}";

            if (convertImagesToJpeg
                && item.Kind == GalleryMediaKind.Image
                && GalleryImageCodec.NeedsJpegConversion(fileName))
            {
                fileName = Path.ChangeExtension(fileName, ".jpg");
            }

            var dest = Path.Combine(localDir, fileName);
            if (File.Exists(dest))
            {
                dest = Path.Combine(
                    localDir,
                    $"{Path.GetFileNameWithoutExtension(fileName)}_{i}{Path.GetExtension(fileName)}");
            }

            try
            {
                var cached = await PullOriginalAsync(item, cancellationToken).ConfigureAwait(false);

                var convert = convertImagesToJpeg
                    && item.Kind == GalleryMediaKind.Image
                    && (GalleryImageCodec.NeedsJpegConversion(cached)
                        || GalleryImageCodec.NeedsJpegConversion(fileName)
                        || LooksLikeWebpByHeader(cached));

                if (convert)
                {
                    dest = Path.ChangeExtension(dest, ".jpg");
                    if (File.Exists(dest))
                    {
                        dest = Path.Combine(
                            localDir,
                            $"{Path.GetFileNameWithoutExtension(fileName)}_{i}.jpg");
                    }

                    await GalleryImageCodec.WriteFullJpegAsync(cached, dest, cancellationToken)
                        .ConfigureAwait(false);
                    fileName = Path.GetFileName(dest);
                }
                else
                {
                    File.Copy(cached, dest, overwrite: true);
                }

                bytesDone += Math.Max(item.SizeBytes, new FileInfo(dest).Length);
                var elapsed = Math.Max((DateTime.UtcNow - started).TotalSeconds, 0.001);
                progress?.Report(new TransferProgress
                {
                    FileName = fileName,
                    BytesTransferred = bytesDone,
                    TotalBytes = bytesTotal,
                    Speed = $"{(i + 1) / elapsed:F1} dosya/s"
                });
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Medya kaydedilemedi: {Path}", item.RemotePath);
            }
        }

        _logger.Information("Gallery save complete: {Count} items → {Dir}", list.Count, localDir);
    }

    public async Task<string> EnsureLocalJpegAsync(
        string localPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            return localPath;

        if (!GalleryImageCodec.NeedsJpegConversion(localPath)
            && !LooksLikeWebpByHeader(localPath))
            return localPath;

        if (!GalleryImageCodec.IsLikelyRasterImageFile(localPath))
            return localPath;

        var jpegPath = Path.ChangeExtension(localPath, ".jpg");
        if (string.Equals(Path.GetFullPath(localPath), Path.GetFullPath(jpegPath), StringComparison.OrdinalIgnoreCase))
            return localPath;

        try
        {
            await GalleryImageCodec.WriteFullJpegAsync(localPath, jpegPath, cancellationToken)
                .ConfigureAwait(false);
            try { File.Delete(localPath); } catch { /* keep jpeg even if delete fails */ }
            return jpegPath;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "JPEG dönüşümü başarısız: {Path}", localPath);
            return localPath;
        }
    }

    private static bool LooksLikeWebpByHeader(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            using var fs = File.OpenRead(path);
            if (fs.Read(header) < 12) return false;
            return header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                   && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetThumbnailPathAsync(
        GalleryMediaItem item,
        int maxEdge = 256,
        CancellationToken cancellationToken = default)
    {
        if (!item.HasRemotePath)
            return null;

        var thumbPath = Path.Combine(ThumbRoot, BuildCacheFileName(item, maxEdge, ".jpg"));
        if (File.Exists(thumbPath) && new FileInfo(thumbPath).Length > 0)
            return thumbPath;

        await _thumbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(thumbPath) && new FileInfo(thumbPath).Length > 0)
                return thumbPath;

            // 1) Prefer MediaStore mini-thumbnail — never pull full originals for grid thumbs
            //    (full pulls over Wi‑Fi ADB are minutes each and starved preview).
            var storeThumb = await TryPullMediaStoreThumbnailAsync(item, thumbPath, cancellationToken)
                .ConfigureAwait(false);
            if (storeThumb is not null)
                return storeThumb;

            return null;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Thumbnail failed for {Path}", item.RemotePath);
            return null;
        }
        finally
        {
            _thumbGate.Release();
        }
    }

    public async Task<string> PullOriginalAsync(
        GalleryMediaItem item,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        if (!item.HasRemotePath && item.Id <= 0)
            throw new InvalidOperationException("Medya yolu yok.");

        var local = Path.Combine(OriginalCacheRoot, BuildCacheFileName(item, 0, GuessExtension(item)));
        if (File.Exists(local) && IsValidPulledMedia(local, item))
            return local;

        TryDelete(local);

        // Do NOT share the thumbnail gate — preview must not wait behind grid thumb queue.
        await _originalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(local) && IsValidPulledMedia(local, item))
                return local;

            var ok = await TryPullMediaAsync(item, local, cancellationToken).ConfigureAwait(false);
            if (!ok || !IsValidPulledMedia(local, item))
            {
                TryDelete(local);
                throw new IOException(
                    "Dosya cihazda ADB ile okunamadı (MediaStore yolu geçersiz veya scoped storage). " +
                    "USB ile tekrar deneyin veya Galeri’yi yenileyin.");
            }

            return local;
        }
        finally
        {
            _originalGate.Release();
        }
    }

    private async Task<bool> TryPullFileAsync(
        string remotePath,
        string localPath,
        CancellationToken cancellationToken)
        => await TryPullPathAsync(remotePath, localPath, expectImage: true, allowSlowFallback: false, cancellationToken)
            .ConfigureAwait(false);

    private async Task<bool> TryPullMediaAsync(
        GalleryMediaItem item,
        string localPath,
        CancellationToken cancellationToken)
    {
        var expectImage = item.Kind == GalleryMediaKind.Image;
        var preferContent = Volatile.Read(ref _preferContentUri);

        // Fast path: content URI only (scoped storage). Avoids minutes of failed sync/cat.
        if (item.Id > 0 && preferContent >= 0)
        {
            var uris = item.Kind == GalleryMediaKind.Video
                ? new[]
                {
                    $"content://media/external/video/media/{item.Id}",
                    $"content://media/external/file/{item.Id}"
                }
                : new[]
                {
                    $"content://media/external/images/media/{item.Id}",
                    $"content://media/external/file/{item.Id}"
                };

            foreach (var uri in uris)
            {
                if (await TryPullContentUriAsync(uri, localPath, expectImage, cancellationToken).ConfigureAwait(false))
                {
                    Volatile.Write(ref _preferContentUri, 1);
                    return true;
                }
            }

            if (preferContent == 1)
                return false; // content URI used to work; don't burn minutes on dead paths
        }

        // Slow fallbacks (short timeouts) — only when content URI never succeeded.
        foreach (var path in EnumerateRemotePathCandidates(item.RemotePath))
        {
            if (await TryPullPathAsync(path, localPath, expectImage, allowSlowFallback: true, cancellationToken)
                    .ConfigureAwait(false))
            {
                Volatile.Write(ref _preferContentUri, -1);
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateRemotePathCandidates(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
            yield break;

        yield return remotePath;

        const string emulated = "/storage/emulated/0/";
        if (remotePath.StartsWith(emulated, StringComparison.OrdinalIgnoreCase))
            yield return "/sdcard/" + remotePath[emulated.Length..];

        if (remotePath.StartsWith("/sdcard/", StringComparison.OrdinalIgnoreCase))
            yield return "/storage/emulated/0/" + remotePath["/sdcard/".Length..];
    }

    private async Task<bool> TryPullPathAsync(
        string remotePath,
        string localPath,
        bool expectImage,
        bool allowSlowFallback,
        CancellationToken cancellationToken)
    {
        // Sync protocol hangs for a long time on missing scoped-storage paths — hard timeout.
        try
        {
            using var syncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            syncCts.CancelAfter(TimeSpan.FromSeconds(6));
            await _sync.PullAsync(remotePath, localPath, cancellationToken: syncCts.Token)
                .ConfigureAwait(false);
            if (IsValidPulledFile(localPath, expectImage))
                return true;
            TryDelete(localPath);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Sync pull failed/timeout: {Path}", remotePath);
            TryDelete(localPath);
        }

        if (!allowSlowFallback)
            return false;

        try
        {
            using var catCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            catCts.CancelAfter(TimeSpan.FromSeconds(8));
            await PullViaExecOutCatAsync(remotePath, localPath, catCts.Token).ConfigureAwait(false);
            if (IsValidPulledFile(localPath, expectImage))
                return true;
            TryDelete(localPath);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "exec-out cat failed/timeout: {Path}", remotePath);
            TryDelete(localPath);
        }

        return false;
    }

    private async Task<bool> TryPullContentUriAsync(
        string contentUri,
        string localPath,
        bool expectImage,
        CancellationToken cancellationToken)
    {
        // Single command — second variant only if first fails quickly.
        foreach (var argsTemplate in new[]
                 {
                     "exec-out content read --uri {uri}",
                     "exec-out cmd content read --uri {uri}"
                 })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(45)); // large photos over Wi‑Fi still need headroom
                var args = argsTemplate.Replace("{uri}", contentUri, StringComparison.Ordinal);
                var sw = Stopwatch.StartNew();
                await PullViaExecOutArgsAsync(args, localPath, cts.Token).ConfigureAwait(false);
                if (IsValidPulledFile(localPath, expectImage))
                {
                    _logger.Information(
                        "Pulled content URI {Uri} in {Ms}ms ({Bytes} bytes)",
                        contentUri, sw.ElapsedMilliseconds, new FileInfo(localPath).Length);
                    return true;
                }

                TryDelete(localPath);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "content URI pull failed: {Uri}", contentUri);
                TryDelete(localPath);
            }
        }

        return false;
    }

    private bool IsValidPulledMedia(string localPath, GalleryMediaItem item) =>
        IsValidPulledFile(localPath, expectImage: item.Kind == GalleryMediaKind.Image);

    private static bool IsValidPulledFile(string localPath, bool expectImage)
    {
        if (!File.Exists(localPath))
            return false;

        var length = new FileInfo(localPath).Length;
        if (length < 24)
            return false;

        if (expectImage)
            return GalleryImageCodec.IsLikelyRasterImageFile(localPath);

        // Videos: reject tiny/error payloads; accept anything reasonably large.
        if (GalleryImageCodec.IsLikelyRasterImageFile(localPath))
            return true; // some "videos" might be mis-tagged stills

        return length > 1024 && !FileStartsWithAscii(localPath, "cat:");
    }

    private static bool FileStartsWithAscii(string path, string prefix)
    {
        try
        {
            Span<byte> buf = stackalloc byte[prefix.Length];
            using var fs = File.OpenRead(path);
            if (fs.Read(buf) < prefix.Length)
                return false;
            for (var i = 0; i < prefix.Length; i++)
            {
                if (buf[i] != (byte)prefix[i])
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task PullViaExecOutCatAsync(
        string remotePath,
        string localPath,
        CancellationToken cancellationToken)
    {
        var serial = _adb.SelectedDevice?.Serial
                     ?? throw new InvalidOperationException("Cihaz seçili değil.");
        await PullViaExecOutArgsAsync(
                $"exec-out cat {ShellQuote(remotePath)}",
                localPath,
                cancellationToken,
                serial)
            .ConfigureAwait(false);
    }

    private async Task PullViaExecOutArgsAsync(
        string adbArgsWithoutSerial,
        string localPath,
        CancellationToken cancellationToken,
        string? serial = null)
    {
        serial ??= _adb.SelectedDevice?.Serial
                   ?? throw new InvalidOperationException("Cihaz seçili değil.");
        var adbPath = ResolveAdbExe();
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await _adb.RunExclusiveAsync(async ct =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = $"-s \"{serial}\" {adbArgsWithoutSerial}",
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
                await process.StandardOutput.BaseStream.CopyToAsync(fs, ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    var err = await process.StandardError.ReadToEndAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    throw new IOException($"adb exit {process.ExitCode}: {err}");
                }
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                TryDelete(localPath);
                throw;
            }
            finally
            {
                if (!process.HasExited)
                    TryKillProcess(process);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task PullViaExecOutAsync(
        string remotePath,
        string localPath,
        CancellationToken cancellationToken)
        => await PullViaExecOutCatAsync(remotePath, localPath, cancellationToken).ConfigureAwait(false);

    private static void TryKillProcess(Process process)
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

    private async Task<string?> TryPullMediaStoreThumbnailAsync(
        GalleryMediaItem item,
        string destPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri = item.Kind == GalleryMediaKind.Video
                ? "content://media/external/video/thumbnails"
                : "content://media/external/images/thumbnails";
            var idField = item.Kind == GalleryMediaKind.Video ? "video_id" : "image_id";

            var raw = await _adb.ExecuteShellAsync(
                    $"content query --uri {uri} --projection _data:{idField}:kind --where \"{idField}={item.Id}\"",
                    cancellationToken)
                .ConfigureAwait(false);

            var path = AdbContentQueryParser.ParseRows(raw)
                .Select(r =>
                {
                    r.TryGetValue("_data", out var data);
                    r.TryGetValue("kind", out var kind);
                    return (data, kind);
                })
                .Where(t => !string.IsNullOrWhiteSpace(t.data))
                .OrderByDescending(t => t.kind) // prefer larger kind when present
                .Select(t => t.data!.Trim())
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(path))
                return null;

            var temp = Path.Combine(Path.GetTempPath(), $"am-msthumb-{item.Id}-{Guid.NewGuid():N}.jpg");
            try
            {
                if (!await TryPullFileAsync(path, temp, cancellationToken).ConfigureAwait(false))
                    return null;

                // Re-encode so WPF never sees raw WebP even for MediaStore thumbs.
                try
                {
                    await GalleryImageCodec.WriteJpegThumbnailAsync(temp, destPath, maxEdge: 256, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "MediaStore thumb re-encode failed; copying bytes");
                    File.Copy(temp, destPath, overwrite: true);
                }

                return File.Exists(destPath) && new FileInfo(destPath).Length > 0 ? destPath : null;
            }
            finally
            {
                TryDelete(temp);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "MediaStore thumbnail failed for id {Id}", item.Id);
            return null;
        }
    }

    private async Task<IReadOnlyList<GalleryMediaItem>> QueryMediaAsync(
        string uri,
        string projection,
        GalleryMediaKind kind,
        CancellationToken cancellationToken)
    {
        // date_added is reliable on modern Android; datetaken is often empty / breaks --sort.
        var raw = await QueryContentAsync(uri, projection, "date_added DESC", cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw) || LooksLikeFailure(raw))
        {
            raw = await QueryContentAsync(uri, projection, "datetaken DESC", cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(raw) || LooksLikeFailure(raw))
        {
            raw = await QueryContentAsync(uri, projection, sort: null, cancellationToken)
                .ConfigureAwait(false);
        }

        return AdbContentQueryParser.ParseRows(raw)
            .Select(row => ParseItem(row, kind))
            .Where(i => i is not null)
            .Cast<GalleryMediaItem>()
            .ToList();
    }

    private async Task<string> QueryContentAsync(
        string uri,
        string projection,
        string? sort,
        CancellationToken cancellationToken)
    {
        var cmd = sort is null
            ? $"content query --uri {uri} --projection {projection}"
            : $"content query --uri {uri} --projection {projection} --sort \"{sort}\"";
        try
        {
            return await _adb.ExecuteShellAsync(cmd, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "content query failed: {Uri}", uri);
            return string.Empty;
        }
    }

    private static GalleryMediaItem? ParseItem(Dictionary<string, string> row, GalleryMediaKind kind)
    {
        row.TryGetValue("_data", out var data);
        row.TryGetValue("_display_name", out var name);
        row.TryGetValue("mime_type", out var mime);
        row.TryGetValue("_id", out var idRaw);
        row.TryGetValue("_size", out var sizeRaw);
        row.TryGetValue("datetaken", out var takenRaw);
        row.TryGetValue("date_added", out var addedRaw);
        row.TryGetValue("width", out var wRaw);
        row.TryGetValue("height", out var hRaw);
        row.TryGetValue("duration", out var durationRaw);
        row.TryGetValue("bucket_display_name", out var albumRaw);

        if (string.IsNullOrWhiteSpace(data))
            return null;

        _ = long.TryParse(idRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id);
        _ = long.TryParse(sizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
        _ = long.TryParse(takenRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var taken);
        if (taken <= 0
            && long.TryParse(addedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var added)
            && added > 0)
        {
            taken = added > 10_000_000_000L ? added : added * 1000L;
        }

        _ = int.TryParse(wRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width);
        _ = int.TryParse(hRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height);
        _ = long.TryParse(durationRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var durationMs);

        var album = albumRaw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(album) && !string.IsNullOrWhiteSpace(data))
            album = InferAlbumName(data.Trim());

        return new GalleryMediaItem
        {
            Id = id,
            RemotePath = data.Trim(),
            // Prefer real path file name — MediaStore _display_name often disagrees (e.g. .webp label).
            DisplayName = ResolveDisplayName(data.Trim(), name, mime),
            MimeType = mime?.Trim() ?? string.Empty,
            SizeBytes = size,
            DateTakenMs = taken,
            Width = width,
            Height = height,
            Kind = kind,
            Album = album,
            DurationMs = durationMs
        };
    }

    private static IEnumerable<GalleryMediaItem> ApplySort(
        IEnumerable<GalleryMediaItem> items,
        MediaSortOrder sort) => sort switch
    {
        MediaSortOrder.DateAsc => items.OrderBy(i => i.DateTakenMs),
        MediaSortOrder.NameAsc => items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase),
        MediaSortOrder.SizeDesc => items.OrderByDescending(i => i.SizeBytes),
        _ => items.OrderByDescending(i => i.DateTakenMs)
    };

    private static string InferAlbumName(string remotePath)
    {
        var parent = GetParentPath(remotePath);
        var name = Path.GetFileName(parent.Replace('/', Path.DirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "Diğer" : name;
    }

    private static string GetParentPath(string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/').TrimEnd('/');
        var idx = normalized.LastIndexOf('/');
        return idx > 0 ? normalized[..idx] : "/sdcard/DCIM";
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "media";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (invalid.Contains(chars[i]))
                chars[i] = '_';
        }

        var clean = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "media" : clean;
    }

    private static string ResolveDisplayName(string remotePath, string? mediaStoreName, string? mime)
    {
        var fromPath = Path.GetFileName(remotePath);
        if (string.IsNullOrWhiteSpace(fromPath))
            fromPath = mediaStoreName?.Trim() ?? "media";

        // Path is source of truth for extension (WhatsApp / OEMs may lie in _display_name).
        var pathExt = Path.GetExtension(fromPath);
        if (!string.IsNullOrWhiteSpace(pathExt))
            return fromPath;

        var storeName = mediaStoreName?.Trim();
        if (!string.IsNullOrWhiteSpace(storeName) && !string.IsNullOrWhiteSpace(Path.GetExtension(storeName)))
            return storeName;

        var mimeExt = mime?.Trim() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/heic" or "image/heif" => ".heic",
            "video/mp4" => ".mp4",
            _ => string.Empty
        };

        return string.IsNullOrEmpty(mimeExt) ? fromPath : fromPath + mimeExt;
    }

    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

    private static string BuildCacheFileName(GalleryMediaItem item, int maxEdge, string extension)
    {
        var key = $"{item.Kind}:{item.Id}:{item.RemotePath}:{maxEdge}";
        
        int maxKeyBytes = Encoding.UTF8.GetMaxByteCount(key.Length);
        byte[]? rentedBytes = null;
        Span<byte> keyBytes = maxKeyBytes <= 256 
            ? stackalloc byte[256] 
            : (rentedBytes = System.Buffers.ArrayPool<byte>.Shared.Rent(maxKeyBytes));

        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(key, keyBytes);
            Span<byte> hashBytes = stackalloc byte[32];
            SHA256.HashData(keyBytes[..bytesWritten], hashBytes);
            var hash = Convert.ToHexString(hashBytes)[..20];

            var displayName = item.DisplayName;
            var safeName = string.Create(displayName.Length, (displayName, InvalidFileChars), (span, state) =>
            {
                var (orig, badChars) = state;
                for (int i = 0; i < span.Length; i++)
                {
                    char c = orig[i];
                    span[i] = badChars.Contains(c) ? '_' : c;
                }
            });

            if (safeName.Length > 40)
                safeName = safeName[..40];
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "media";

            return $"{safeName}_{hash}{extension}";
        }
        finally
        {
            if (rentedBytes is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBytes);
        }
    }

    private static string GuessExtension(GalleryMediaItem item)
    {
        var fromName = Path.GetExtension(item.DisplayName);
        if (!string.IsNullOrWhiteSpace(fromName))
            return fromName;
        var fromPath = Path.GetExtension(item.RemotePath);
        if (!string.IsNullOrWhiteSpace(fromPath))
            return fromPath;
        if (item.MimeType.Contains("png", StringComparison.OrdinalIgnoreCase))
            return ".png";
        if (item.MimeType.Contains("webp", StringComparison.OrdinalIgnoreCase))
            return ".webp";
        if (item.MimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase))
            return ".mp4";
        if (item.Kind == GalleryMediaKind.Video)
            return ".mp4";
        return ".jpg";
    }

    private static string ShellQuote(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "''";
        return "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string ResolveAdbExe()
    {
        var env = Environment.GetEnvironmentVariable("ADB_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb", "adb.exe");
        if (File.Exists(bundled))
            return bundled;

        return "adb";
    }

    private void EnsureDevice()
    {
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("Galeri için bağlı bir cihaz seçin.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static bool LooksLikeFailure(string output) =>
        output.Contains("Exception", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Error:", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase)
        || output.Contains("No result found", StringComparison.OrdinalIgnoreCase);
}
