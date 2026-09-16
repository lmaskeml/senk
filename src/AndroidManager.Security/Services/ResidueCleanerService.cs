using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Root;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed partial class ResidueCleanerService : IResidueCleanerService
{
    private const long MinCacheBytes = 512 * 1024;
    private const long MinDalvikBytes = 200L * 1024 * 1024;
    private const int DownloadAgeDays = 14;
    private const int PcScanAgeDays = 14;
    private const int QuarantineAgeDays = 30;

    private static readonly string SecurityDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidManager", "security");

    private static readonly string WhitelistPath = Path.Combine(SecurityDir, "residue_whitelist.json");
    private static readonly string PcScansDir = Path.Combine(SecurityDir, "scans");

    private static readonly string[] LogPaths =
    [
        "/data/anr",
        "/data/tombstones",
        "/data/log",
        "/data/system/dropbox"
    ];

    private readonly IAdbService _adb;
    private readonly RootManager _root;
    private readonly ILogger _logger;

    public ResidueCleanerService(IAdbService adb, RootManager root, ILogger? logger = null)
    {
        _adb = adb;
        _logger = logger ?? Log.ForContext<ResidueCleanerService>();
        _root = root;
    }

    public async Task<bool> RootAvailableAsync(CancellationToken cancellationToken = default)
    {
        var status = await _root.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status.Access != RootAccess.None;
    }

    public async Task<ResidueScanResult> ScanAsync(
        ResidueScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("Kalıntı taraması için cihaz bağlayın.");

        var sw = Stopwatch.StartNew();
        var items = new List<ResidueItem>();
        var rootAvailable = await RootAvailableAsync(cancellationToken).ConfigureAwait(false);
        var rootActive = rootAvailable && await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false);
        var scanned = 0;

        var whitelist = options.WhitelistedPackages.Count > 0
            ? new HashSet<string>(options.WhitelistedPackages, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(await GetWhitelistAsync(cancellationToken).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);

        var installed = await GetInstalledPackagesAsync(cancellationToken).ConfigureAwait(false);

        void Report(string phase, int pct)
        {
            progress?.Report(new ScanProgress
            {
                Phase = phase,
                Percentage = pct,
                ScannedCount = items.Count,
                ThreatsFound = items.Count,
                ElapsedTime = sw.Elapsed.ToString(@"mm\:ss")
            });
        }

        if (options.ScanOrphanedData)
        {
            Report("Yetim uygulama verisi", 5);
            items.AddRange(await ScanOrphanedDataAsync(installed, whitelist, rootActive, cancellationToken).ConfigureAwait(false));
            scanned++;
        }

        if (options.ScanLeftoverApks)
        {
            Report("APK kalıntıları", 15);
            items.AddRange(await ScanLeftoverApksAsync(installed, rootActive, cancellationToken).ConfigureAwait(false));
            scanned++;
        }

        if (options.ScanAppCaches)
        {
            Report("Uygulama önbellekleri", 30);
            items.AddRange(await ScanAppCachesAsync(installed, rootActive, cancellationToken).ConfigureAwait(false));
            scanned++;
        }

        if (options.ScanTempFiles)
        {
            Report("Geçici dosyalar", 45);
            items.AddRange(await ScanTempFilesAsync(rootActive, cancellationToken).ConfigureAwait(false));
            scanned++;
        }

        if (options.ScanLogFiles && rootActive)
        {
            Report("Log kalıntıları", 55);
            items.AddRange(await ScanLogFilesAsync(cancellationToken).ConfigureAwait(false));
            scanned++;
        }

        if (options.DeepScan && rootActive)
        {
            Report("Dalvik önbelleği", 65);
            var dalvik = await ScanDalvikCacheAsync(cancellationToken).ConfigureAwait(false);
            if (dalvik is not null)
                items.Add(dalvik);
            scanned++;
        }

        if (options.ScanEmptyDirs && rootActive)
        {
            Report("Boş dizinler", 72);
            items.AddRange(await ScanEmptyDirsAsync(cancellationToken).ConfigureAwait(false));
            scanned++;
        }

        if (options.ScanThumbnails)
        {
            Report("Küçük resim önbelleği", 80);
            items.AddRange(await ScanThumbnailsAsync(cancellationToken).ConfigureAwait(false));
            scanned++;
        }

        if (options.ScanDownloadJunk)
        {
            Report("İndirme kalıntıları", 88);
            items.AddRange(await ScanDownloadJunkAsync(cancellationToken).ConfigureAwait(false));
            scanned++;
        }

        Report("Uygulama kalıntıları", 95);
        items.AddRange(ScanPcLeftovers());
        if (rootActive)
            items.AddRange(await ScanDeviceQuarantineLeftoversAsync(cancellationToken).ConfigureAwait(false));

        sw.Stop();
        Report("Tamamlandı", 100);

        return new ResidueScanResult
        {
            Items = items.OrderByDescending(i => i.SizeBytes).ToList(),
            RootAvailable = rootAvailable,
            RootActive = rootActive,
            Duration = sw.Elapsed,
            ScannedEntries = scanned
        };
    }

    public async Task<IReadOnlyList<ResidueCleanResult>> CleanAsync(
        IEnumerable<ResidueItem> items,
        IProgress<CleanProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var list = items.ToList();
        var results = new List<ResidueCleanResult>(list.Count);

        for (var i = 0; i < list.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await CleanItemAsync(list[i], cancellationToken).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(new CleanProgress
            {
                CurrentThreat = list[i].Name,
                Done = i + 1,
                Total = list.Count,
                Percentage = (i + 1) * 100 / list.Count
            });
        }

        return results;
    }

    public async Task<ResidueCleanResult> CleanItemAsync(
        ResidueItem item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (item.Category == ResidueCategory.AppLeftovers && item.Path.StartsWith("PC:", StringComparison.Ordinal))
            {
                var localPath = item.Path["PC:".Length..];
                if (File.Exists(localPath))
                    File.Delete(localPath);
                else if (Directory.Exists(localPath))
                    Directory.Delete(localPath, recursive: true);
                return Ok(item, "PC kalıntısı silindi");
            }

            if (item.RequiresRoot)
            {
                if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
                    return Fail(item, "Root izni gerekli");

                await _root.Shell.RunRootCommandAsync($"rm -rf {ShellQuote(item.Path)}", cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (item.Path.StartsWith('/'))
            {
                await _adb.ExecuteShellAsync($"rm -rf {ShellQuote(item.Path)}", cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                return Fail(item, "Desteklenmeyen yol");
            }

            var gone = await VerifyGoneAsync(item, cancellationToken).ConfigureAwait(false);
            return gone ? Ok(item, "Silindi") : Fail(item, "Silme doğrulanamadı");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Kalıntı silinemedi: {Path}", item.Path);
            return Fail(item, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> GetWhitelistAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(WhitelistPath))
                return [];

            var list = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(WhitelistPath, cancellationToken).ConfigureAwait(false));
            return list ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<bool> AddWhitelistAsync(string packageName, CancellationToken cancellationToken = default)
    {
        packageName = packageName.Trim();
        if (string.IsNullOrWhiteSpace(packageName))
            return false;

        var list = (await GetWhitelistAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (list.Contains(packageName, StringComparer.OrdinalIgnoreCase))
            return true;

        list.Add(packageName);
        await SaveWhitelistAsync(list, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RemoveWhitelistAsync(string packageName, CancellationToken cancellationToken = default)
    {
        var list = (await GetWhitelistAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var removed = list.RemoveAll(p => p.Equals(packageName, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            await SaveWhitelistAsync(list, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async Task<long> TrimSystemCachesAsync(CancellationToken cancellationToken = default)
    {
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("Cihaz bağlı değil");

        var before = await ReadAvailableStorageAsync(cancellationToken).ConfigureAwait(false);
        var output = await _adb.ExecuteShellAsync("pm trim-caches 999999999999", cancellationToken)
            .ConfigureAwait(false);
        var after = await ReadAvailableStorageAsync(cancellationToken).ConfigureAwait(false);
        var freed = Math.Max(0, after - before);

        _logger.Information("pm trim-caches: freed≈{Freed} output={Out}", freed, output.Trim());
        return freed;
    }

    private async Task<List<ResidueItem>> ScanOrphanedDataAsync(
        HashSet<string> installed,
        HashSet<string> whitelist,
        bool rootActive,
        CancellationToken ct)
    {
        var items = new List<ResidueItem>();

        if (rootActive)
        {
            items.AddRange(await ScanOrphanedUnderAsync(
                "/data/data", installed, whitelist, ResidueCategory.OrphanedData, true, ct)
                .ConfigureAwait(false));
        }

        items.AddRange(await ScanOrphanedUnderAsync(
            "/sdcard/Android/data", installed, whitelist, ResidueCategory.OrphanedData, false, ct)
            .ConfigureAwait(false));
        items.AddRange(await ScanOrphanedUnderAsync(
            "/sdcard/Android/obb", installed, whitelist, ResidueCategory.OrphanedData, false, ct)
            .ConfigureAwait(false));

        return items;
    }

    private async Task<List<ResidueItem>> ScanOrphanedUnderAsync(
        string basePath,
        HashSet<string> installed,
        HashSet<string> whitelist,
        ResidueCategory category,
        bool requiresRoot,
        CancellationToken ct)
    {
        var items = new List<ResidueItem>();
        var entries = await ListDirectoryAsync(basePath, requiresRoot, ct).ConfigureAwait(false);
        foreach (var name in entries)
        {
            if (!name.Contains('.', StringComparison.Ordinal))
                continue;
            if (installed.Contains(name) || whitelist.Contains(name))
                continue;

            var path = $"{basePath}/{name}";
            var size = await GetPathSizeAsync(path, requiresRoot, ct).ConfigureAwait(false);
            items.Add(new ResidueItem
            {
                Name = name,
                Path = path,
                Category = category,
                SizeBytes = size,
                RequiresRoot = requiresRoot,
                Description = "Kaldırılmış uygulamanın kalan veri dizini"
            });
        }

        return items;
    }

    private async Task<List<ResidueItem>> ScanLeftoverApksAsync(
        HashSet<string> installed,
        bool rootActive,
        CancellationToken ct)
    {
        var items = new List<ResidueItem>();
        if (!rootActive)
            return items;

        var entries = await ListDirectoryAsync("/data/app", true, ct).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            if (entry.StartsWith("vmdl", StringComparison.OrdinalIgnoreCase))
            {
                var path = $"/data/app/{entry}";
                items.Add(new ResidueItem
                {
                    Name = entry,
                    Path = path,
                    Category = ResidueCategory.LeftoverApk,
                    SizeBytes = await GetPathSizeAsync(path, true, ct).ConfigureAwait(false),
                    RequiresRoot = true,
                    Description = "Yarım kalmış kurulum (vmdl*)"
                });
                continue;
            }

            var pkg = ExtractPackageFromAppDir(entry);
            if (string.IsNullOrEmpty(pkg) || installed.Contains(pkg))
                continue;

            var full = $"/data/app/{entry}";
            items.Add(new ResidueItem
            {
                Name = pkg,
                Path = full,
                Category = ResidueCategory.LeftoverApk,
                SizeBytes = await GetPathSizeAsync(full, true, ct).ConfigureAwait(false),
                RequiresRoot = true,
                Description = "Kayıtlı olmayan APK dizini"
            });
        }

        return items;
    }

    private async Task<List<ResidueItem>> ScanAppCachesAsync(
        HashSet<string> installed,
        bool rootActive,
        CancellationToken ct)
    {
        var items = new List<ResidueItem>();

        if (rootActive)
        {
            foreach (var pkg in installed)
            {
                foreach (var sub in new[] { "cache", "code_cache" })
                {
                    var path = $"/data/data/{pkg}/{sub}";
                    var size = await GetPathSizeAsync(path, true, ct).ConfigureAwait(false);
                    if (size >= MinCacheBytes)
                    {
                        items.Add(new ResidueItem
                        {
                            Name = $"{pkg}/{sub}",
                            Path = path,
                            Category = ResidueCategory.AppCache,
                            SizeBytes = size,
                            RequiresRoot = true,
                            Description = "Uygulama dahili önbellek"
                        });
                    }
                }
            }
        }

        var sdcardData = await ListDirectoryAsync("/sdcard/Android/data", false, ct).ConfigureAwait(false);
        foreach (var pkg in sdcardData)
        {
            if (!installed.Contains(pkg))
                continue;
            var cachePath = $"/sdcard/Android/data/{pkg}/cache";
            var size = await GetPathSizeAsync(cachePath, false, ct).ConfigureAwait(false);
            if (size >= MinCacheBytes)
            {
                items.Add(new ResidueItem
                {
                    Name = $"{pkg} (sdcard cache)",
                    Path = cachePath,
                    Category = ResidueCategory.AppCache,
                    SizeBytes = size,
                    RequiresRoot = false,
                    Description = "Harici depolama önbelleği"
                });
            }
        }

        return items;
    }

    private async Task<List<ResidueItem>> ScanTempFilesAsync(bool rootActive, CancellationToken ct)
    {
        var items = new List<ResidueItem>();

        if (rootActive)
        {
            var tmpEntries = await ListDirectoryAsync("/data/local/tmp", true, ct).ConfigureAwait(false);
            foreach (var entry in tmpEntries)
            {
                if (entry.Equals("androidmanager_quarantine", StringComparison.OrdinalIgnoreCase))
                    continue;

                var path = $"/data/local/tmp/{entry}";
                items.Add(new ResidueItem
                {
                    Name = entry,
                    Path = path,
                    Category = ResidueCategory.TempFiles,
                    SizeBytes = await GetPathSizeAsync(path, true, ct).ConfigureAwait(false),
                    RequiresRoot = true,
                    Description = "Geçici dosya (/data/local/tmp)"
                });
            }
        }

        var findCmd =
            "find /sdcard -maxdepth 4 \\( -name '*.tmp' -o -name '*.part' -o -name '*.crdownload' \\) -type f 2>/dev/null | head -200";
        var raw = await _adb.ExecuteShellAsync(findCmd, ct).ConfigureAwait(false);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = line.Trim();
            if (path.Length == 0)
                continue;
            items.Add(new ResidueItem
            {
                Name = Path.GetFileName(path),
                Path = path,
                Category = ResidueCategory.TempFiles,
                SizeBytes = await GetPathSizeAsync(path, false, ct).ConfigureAwait(false),
                RequiresRoot = false,
                Description = "SD kart geçici dosyası"
            });
        }

        return items;
    }

    private async Task<List<ResidueItem>> ScanLogFilesAsync(CancellationToken ct)
    {
        var items = new List<ResidueItem>();
        foreach (var path in LogPaths)
        {
            var size = await GetPathSizeAsync(path, true, ct).ConfigureAwait(false);
            if (size <= 0)
                continue;
            items.Add(new ResidueItem
            {
                Name = path.TrimStart('/'),
                Path = path,
                Category = ResidueCategory.LogFiles,
                SizeBytes = size,
                RequiresRoot = true,
                Description = "Sistem log / ANR / tombstone"
            });
        }

        return items;
    }

    private async Task<ResidueItem?> ScanDalvikCacheAsync(CancellationToken ct)
    {
        const string path = "/data/dalvik-cache";
        var size = await GetPathSizeAsync(path, true, ct).ConfigureAwait(false);
        if (size < MinDalvikBytes)
            return null;

        return new ResidueItem
        {
            Name = "dalvik-cache",
            Path = path,
            Category = ResidueCategory.DalvikCache,
            SizeBytes = size,
            RequiresRoot = true,
            Description = "Dalvik/ART önbelleği (silinince yeniden oluşur)"
        };
    }

    private async Task<List<ResidueItem>> ScanEmptyDirsAsync(CancellationToken ct)
    {
        var items = new List<ResidueItem>();
        var cmd =
            "find /data/data /data/app /sdcard/Android/data -mindepth 1 -maxdepth 2 -type d -empty 2>/dev/null | head -100";
        var raw = await _root.Shell.RunRootCommandAsync(cmd, ct).ConfigureAwait(false);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = line.Trim();
            if (path.Length == 0)
                continue;
            items.Add(new ResidueItem
            {
                Name = path,
                Path = path,
                Category = ResidueCategory.EmptyDirectory,
                SizeBytes = 0,
                RequiresRoot = true,
                Description = "Boş dizin"
            });
        }

        return items;
    }

    private async Task<List<ResidueItem>> ScanThumbnailsAsync(CancellationToken ct)
    {
        var items = new List<ResidueItem>();
        var cmd = "find /sdcard -type d -name '.thumbnails' 2>/dev/null | head -20";
        var raw = await _adb.ExecuteShellAsync(cmd, ct).ConfigureAwait(false);
        foreach (var path in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
        {
            var size = await GetPathSizeAsync(path, false, ct).ConfigureAwait(false);
            if (size <= 0)
                continue;
            items.Add(new ResidueItem
            {
                Name = ".thumbnails",
                Path = path,
                Category = ResidueCategory.Thumbnails,
                SizeBytes = size,
                RequiresRoot = false,
                Description = "Galeri küçük resim önbelleği"
            });
        }

        return items;
    }

    private async Task<List<ResidueItem>> ScanDownloadJunkAsync(CancellationToken ct)
    {
        var items = new List<ResidueItem>();
        var cmd =
            "find /sdcard/Download -type f \\( -iname '*.apk' -o -iname '*.xapk' -o -iname '*.apks' \\) 2>/dev/null | head -100";
        var raw = await _adb.ExecuteShellAsync(cmd, ct).ConfigureAwait(false);
        var cutoff = DateTime.UtcNow.AddDays(-DownloadAgeDays);

        foreach (var path in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
        {
            var stat = await _adb.ExecuteShellAsync($"stat -c %Y {ShellQuote(path)} 2>/dev/null", ct).ConfigureAwait(false);
            if (long.TryParse(stat.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                var modified = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                if (modified > cutoff)
                    continue;
            }

            items.Add(new ResidueItem
            {
                Name = Path.GetFileName(path),
                Path = path,
                Category = ResidueCategory.DownloadJunk,
                SizeBytes = await GetPathSizeAsync(path, false, ct).ConfigureAwait(false),
                RequiresRoot = false,
                Description = $"Download'da {DownloadAgeDays}+ günlük kurulum dosyası"
            });
        }

        return items;
    }

    private List<ResidueItem> ScanPcLeftovers()
    {
        var items = new List<ResidueItem>();
        if (!Directory.Exists(PcScansDir))
            return items;

        var cutoff = DateTime.Now.AddDays(-PcScanAgeDays);
        foreach (var file in Directory.GetFiles(PcScansDir, "scan_*.json"))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTime > cutoff)
                continue;

            items.Add(new ResidueItem
            {
                Name = info.Name,
                Path = $"PC:{file}",
                Category = ResidueCategory.AppLeftovers,
                SizeBytes = info.Length,
                RequiresRoot = false,
                Description = "Eski PC tarama kaydı"
            });
        }

        return items;
    }

    private async Task<List<ResidueItem>> ScanDeviceQuarantineLeftoversAsync(CancellationToken ct)
    {
        var items = new List<ResidueItem>();
        var qDir = RootCleaner.QuarantineDir;
        var entries = await ListDirectoryAsync(qDir, true, ct).ConfigureAwait(false);
        var cutoff = DateTime.UtcNow.AddDays(-QuarantineAgeDays);

        foreach (var entry in entries)
        {
            var path = $"{qDir}/{entry}";
            var stat = await _root.Shell.RunRootCommandAsync($"stat -c %Y {ShellQuote(path)} 2>/dev/null", ct).ConfigureAwait(false);
            if (long.TryParse(stat.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                if (DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime > cutoff)
                    continue;
            }

            items.Add(new ResidueItem
            {
                Name = entry,
                Path = path,
                Category = ResidueCategory.AppLeftovers,
                SizeBytes = await GetPathSizeAsync(path, true, ct).ConfigureAwait(false),
                RequiresRoot = true,
                Description = "Eski karantina yedeği (30+ gün)"
            });
        }

        return items;
    }

    private async Task<HashSet<string>> GetInstalledPackagesAsync(CancellationToken ct)
    {
        var raw = await _adb.ExecuteShellAsync("pm list packages", ct).ConfigureAwait(false);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("package:", StringComparison.Ordinal))
                set.Add(t["package:".Length..].Trim());
        }

        return set;
    }

    private async Task<IReadOnlyList<string>> ListDirectoryAsync(
        string path,
        bool useRoot,
        CancellationToken ct)
    {
        var cmd = $"ls -1 {ShellQuote(path)} 2>/dev/null";
        var raw = useRoot
            ? await _root.Shell.RunRootCommandAsync(cmd, ct).ConfigureAwait(false)
            : await _adb.ExecuteShellAsync(cmd, ct).ConfigureAwait(false);

        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != "." && l != "..")
            .ToList();
    }

    private async Task<long> GetPathSizeAsync(string path, bool useRoot, CancellationToken ct)
    {
        var cmd = $"du -sk {ShellQuote(path)} 2>/dev/null | cut -f1";
        var raw = useRoot
            ? await _root.Shell.RunRootCommandAsync(cmd, ct).ConfigureAwait(false)
            : await _adb.ExecuteShellAsync(cmd, ct).ConfigureAwait(false);

        var line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
            return kb * 1024;

        return 0;
    }

    private async Task<bool> VerifyGoneAsync(ResidueItem item, CancellationToken ct)
    {
        if (item.Path.StartsWith("PC:", StringComparison.Ordinal))
            return !File.Exists(item.Path["PC:".Length..]) && !Directory.Exists(item.Path["PC:".Length..]);

        var cmd = $"test ! -e {ShellQuote(item.Path)} && echo GONE || echo EXISTS";
        var raw = item.RequiresRoot
            ? await _root.Shell.RunRootCommandAsync(cmd, ct).ConfigureAwait(false)
            : await _adb.ExecuteShellAsync(cmd, ct).ConfigureAwait(false);
        return raw.Contains("GONE", StringComparison.Ordinal);
    }

    private async Task<long> ReadAvailableStorageAsync(CancellationToken ct)
    {
        var raw = await _adb.ExecuteShellAsync("df /data 2>/dev/null | tail -1", ct).ConfigureAwait(false);
        var parts = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var availKb))
            return availKb * 1024;
        return 0;
    }

    private static string? ExtractPackageFromAppDir(string dirName)
    {
        var m = PackageFromAppDirRegex().Match(dirName);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static ResidueCleanResult Ok(ResidueItem item, string message) =>
        new() { Item = item, Success = true, Message = message };

    private static ResidueCleanResult Fail(ResidueItem item, string message) =>
        new() { Item = item, Success = false, Message = message };

    private static string ShellQuote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private async Task SaveWhitelistAsync(List<string> list, CancellationToken ct)
    {
        Directory.CreateDirectory(SecurityDir);
        await File.WriteAllTextAsync(
            WhitelistPath,
            JsonSerializer.Serialize(list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p),
                new JsonSerializerOptions { WriteIndented = true }),
            ct).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^(?:~~[^=]+==)?([a-zA-Z][\w.]*)")]
    private static partial Regex PackageFromAppDirRegex();
}
