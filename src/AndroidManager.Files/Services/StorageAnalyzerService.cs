using System.Globalization;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Files.Services;

public sealed class StorageAnalyzerService : IStorageAnalyzerService
{
    private static readonly string[] KnownRoots =
    [
        "DCIM", "Download", "Downloads", "Pictures", "Movies", "Music", "Documents",
        "Android", "WhatsApp", "Telegram", "Movies", "Podcasts", "Notifications",
        "Alarms", "Ringtones", "Audiobooks"
    ];

    private readonly IAdbService _adb;
    private readonly IDeviceInfoService _deviceInfo;
    private readonly ILogger _logger;

    public StorageAnalyzerService(IAdbService adb, IDeviceInfoService deviceInfo, ILogger? logger = null)
    {
        _adb = adb;
        _deviceInfo = deviceInfo;
        _logger = logger ?? Log.ForContext<StorageAnalyzerService>();
    }

    public async Task<StorageAnalysisResult> AnalyzeAsync(
        string rootPath = "/sdcard",
        long largeFileMinBytes = 50L * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("Cihaz bağlı değil.");

        var root = string.IsNullOrWhiteSpace(rootPath) ? "/sdcard" : rootPath.TrimEnd('/');
        var volume = await _deviceInfo.GetStorageInfoAsync(cancellationToken).ConfigureAwait(false);

        var foldersTask = CollectFoldersAsync(root, volume.UsedKb, cancellationToken);
        var largeTask = CollectLargeFilesAsync(root, largeFileMinBytes, cancellationToken);
        await Task.WhenAll(foldersTask, largeTask).ConfigureAwait(false);

        return new StorageAnalysisResult
        {
            Volume = volume,
            RootPath = root,
            Folders = await foldersTask.ConfigureAwait(false),
            LargeFiles = await largeTask.ConfigureAwait(false),
            CapturedAt = DateTime.Now
        };
    }

    private async Task<IReadOnlyList<StorageFolderUsage>> CollectFoldersAsync(
        string root,
        long usedKb,
        CancellationToken ct)
    {
        // Prefer one-level du; fall back to known roots if OEM busybox is limited.
        var raw = await SafeShellAsync($"du -d 1 -k \"{root}\" 2>/dev/null | sort -nr", ct)
            .ConfigureAwait(false);

        var parsed = ParseDu(raw, root, usedKb);
        if (parsed.Count > 0)
            return parsed;

        _logger.Debug("du -d 1 failed/empty; probing known roots under {Root}", root);
        var lines = new List<string>();
        foreach (var name in KnownRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = $"{root}/{name}";
            var outLine = await SafeShellAsync($"du -sk \"{path}\" 2>/dev/null", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(outLine))
                lines.Add(outLine.Trim());
        }

        return ParseDu(string.Join('\n', lines), root, usedKb);
    }

    private async Task<IReadOnlyList<StorageLargeFile>> CollectLargeFilesAsync(
        string root,
        long minBytes,
        CancellationToken ct)
    {
        var minKb = Math.Max(1, minBytes / 1024);
        // toybox/busybox: -size +Nk (kilobytes)
        var raw = await SafeShellAsync(
                $"find \"{root}\" -type f -size +{minKb}k -exec ls -l {{}} \\; 2>/dev/null | head -n 80",
                ct)
            .ConfigureAwait(false);

        var files = ParseLsLarge(raw);
        if (files.Count > 0)
            return files;

        // Fallback: printf style (some Android builds)
        raw = await SafeShellAsync(
                $"find \"{root}\" -type f -size +{minKb}k -printf '%s %p\\n' 2>/dev/null | sort -nr | head -n 60",
                ct)
            .ConfigureAwait(false);

        return ParsePrintfLarge(raw);
    }

    internal static List<StorageFolderUsage> ParseDu(string raw, string root, long usedKb)
    {
        var list = new List<StorageFolderUsage>();
        if (string.IsNullOrWhiteSpace(raw))
            return list;

        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(['\t', ' '], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                continue;

            var path = parts[1].Trim();
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(root + "/", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = path.TrimEnd('/').Split('/').LastOrDefault() ?? path;
            list.Add(new StorageFolderUsage
            {
                Path = path,
                Name = name,
                SizeKb = kb,
                PercentOfUsed = usedKb > 0 ? Math.Clamp(kb / (double)usedKb * 100, 0, 100) : 0
            });
        }

        return list
            .OrderByDescending(f => f.SizeKb)
            .Take(40)
            .ToList();
    }

    internal static List<StorageLargeFile> ParseLsLarge(string raw)
    {
        var list = new List<StorageLargeFile>();
        if (string.IsNullOrWhiteSpace(raw))
            return list;

        // -rw-rw---- 1 u0_a123 media_rw 123456789 2024-01-01 12:00 /sdcard/foo.mp4
        var rx = new Regex(@"\s(\d+)\s+\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}\s+(.+)$");
        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = rx.Match(line);
            if (!m.Success) continue;
            if (!long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                continue;

            var path = m.Groups[2].Value.Trim();
            list.Add(new StorageLargeFile
            {
                Path = path,
                Name = path.Split('/').LastOrDefault() ?? path,
                SizeBytes = size
            });
        }

        return list.OrderByDescending(f => f.SizeBytes).Take(50).ToList();
    }

    internal static List<StorageLargeFile> ParsePrintfLarge(string raw)
    {
        var list = new List<StorageLargeFile>();
        if (string.IsNullOrWhiteSpace(raw))
            return list;

        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split([' '], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                continue;

            var path = parts[1].Trim();
            list.Add(new StorageLargeFile
            {
                Path = path,
                Name = path.Split('/').LastOrDefault() ?? path,
                SizeBytes = size
            });
        }

        return list.OrderByDescending(f => f.SizeBytes).Take(50).ToList();
    }

    private async Task<string> SafeShellAsync(string command, CancellationToken ct)
    {
        try
        {
            return await _adb.ExecuteShellAsync(command, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Storage analyzer shell failed: {Command}", command);
            return "";
        }
    }
}
