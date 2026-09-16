using System.IO;
using System.Text.Json;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Engines;
using Serilog;

namespace AndroidManager.Security.Services;

/// <summary>
/// Windows-side quarantine: pull APK snapshot, hash, then disable/remove on device.
/// Index persisted in platform.db (QuarantineItems); legacy index.json migrated once.
/// </summary>
public sealed class QuarantineManager
{
    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly IPlatformRepository _platform;
    private readonly ILogger _logger;
    private int _legacyMigrated;

    private static readonly string QuarantineDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidManager", "security", "quarantine");

    private static readonly string IndexPath = Path.Combine(QuarantineDir, "index.json");

    public QuarantineManager(IAdbService adb, IAdbSyncService sync, IPlatformRepository platform, ILogger? logger = null)
    {
        _adb = adb;
        _sync = sync;
        _platform = platform;
        _logger = logger ?? Log.ForContext<QuarantineManager>();
        Directory.CreateDirectory(QuarantineDir);
    }

    public async Task<bool> QuarantineAsync(ThreatItem threat, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(threat.PackageName))
            {
                var apkPath = threat.FilePath;
                if (string.IsNullOrWhiteSpace(apkPath) || apkPath is "unknown")
                {
                    var pathRaw = await _adb.ExecuteShellAsync($"pm path {threat.PackageName}", cancellationToken)
                        .ConfigureAwait(false);
                    apkPath = pathRaw.Replace("package:", "", StringComparison.OrdinalIgnoreCase).Trim()
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault() ?? "";
                }

                var localName = $"{threat.PackageName}_{DateTime.Now:yyyyMMdd_HHmmss}.apk";
                var localPath = Path.Combine(QuarantineDir, localName);

                if (!string.IsNullOrWhiteSpace(apkPath))
                {
                    try
                    {
                        await _sync.PullAsync(apkPath, localPath, null, cancellationToken).ConfigureAwait(false);
                        if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                        {
                            var (md5, sha256) = await HashHelper.HashFileAsync(localPath, cancellationToken)
                                .ConfigureAwait(false);
                            threat.HashMd5 = md5;
                            threat.HashSha256 = sha256;
                            threat.QuarantineLocalPath = localPath;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "APK karantina kopyası alınamadı: {Pkg}", threat.PackageName);
                    }
                }

                var result = await _adb.ExecuteShellAsync(
                    $"pm disable-user --user 0 {threat.PackageName}", cancellationToken).ConfigureAwait(false);
                var ok = result.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("new state", StringComparison.OrdinalIgnoreCase);
                if (ok)
                {
                    threat.IsQuarantined = true;
                    await _platform.UpsertQuarantineItemAsync(threat, cancellationToken).ConfigureAwait(false);
                }

                return ok;
            }

            if (!string.IsNullOrWhiteSpace(threat.FilePath))
            {
                var destName = $"{Guid.NewGuid():N}_{Path.GetFileName(threat.FilePath)}";
                var localPath = Path.Combine(QuarantineDir, destName);
                try
                {
                    await _sync.PullAsync(threat.FilePath, localPath, null, cancellationToken).ConfigureAwait(false);
                    threat.QuarantineLocalPath = localPath;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Dosya karantina kopyası alınamadı");
                }

                await _adb.ExecuteShellAsync($"rm -f \"{threat.FilePath}\"", cancellationToken).ConfigureAwait(false);
                var check = await _adb.ExecuteShellAsync(
                    $"[ -e \"{threat.FilePath}\" ] && echo EXISTS || echo GONE", cancellationToken)
                    .ConfigureAwait(false);
                var ok = check.Trim().Contains("GONE", StringComparison.OrdinalIgnoreCase);
                if (ok)
                {
                    threat.IsQuarantined = true;
                    await _platform.UpsertQuarantineItemAsync(threat, cancellationToken).ConfigureAwait(false);
                }

                return ok;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Karantina hatası: {Name}", threat.Name);
            return false;
        }
    }

    public async Task<bool> RestoreAsync(
        ThreatItem threat,
        Func<ThreatItem, CancellationToken, Task<bool>> stillThreatAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await stillThreatAsync(threat, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warning("Karantinadan geri yükleme engellendi — tehdit hâlâ geçerli: {Name}", threat.Name);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(threat.PackageName))
            {
                var result = await _adb.ExecuteShellAsync($"pm enable {threat.PackageName}", cancellationToken)
                    .ConfigureAwait(false);
                var ok = result.Contains("enabled", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("new state", StringComparison.OrdinalIgnoreCase);
                if (ok)
                    await _platform.RemoveQuarantineItemAsync(threat.Id, cancellationToken).ConfigureAwait(false);
                return ok;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Karantina geri yükleme hatası");
            return false;
        }
    }

    public async Task<List<ThreatItem>> GetListAsync(CancellationToken cancellationToken = default)
    {
        await MigrateLegacyIndexAsync(cancellationToken).ConfigureAwait(false);
        var items = await _platform.ListQuarantineItemsAsync(cancellationToken).ConfigureAwait(false);
        return items.ToList();
    }

    private async Task MigrateLegacyIndexAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _legacyMigrated, 1) != 0)
            return;

        try
        {
            if (!File.Exists(IndexPath))
                return;

            var json = await File.ReadAllTextAsync(IndexPath, cancellationToken).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<List<ThreatItem>>(json) ?? [];
            if (list.Count == 0)
                return;

            foreach (var item in list)
                await _platform.UpsertQuarantineItemAsync(item, cancellationToken).ConfigureAwait(false);

            var bak = IndexPath + ".migrated.bak";
            File.Move(IndexPath, bak, overwrite: true);
            _logger.Information("[Quarantine] Legacy index.json → SQLite ({Count} kayıt)", list.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Quarantine] Legacy index migration skipped");
        }
    }
}
