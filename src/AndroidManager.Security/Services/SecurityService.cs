using System.IO;
using System.Text.Json;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Data;
using AndroidManager.Security.Engines;
using AndroidManager.Security.Scanners;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed class SecurityService : ISecurityService
{
    private readonly IAdbService _adb;
    private readonly ThreatDatabase _db;
    private readonly AppScanner _appScanner;
    private readonly FileScanner _fileScanner;
    private readonly PrivilegeScanner _privilegeScanner;
    private readonly PersistenceScanner _persistenceScanner;
    private readonly SystemIntegrityScanner _integrityScanner;
    private readonly QuarantineManager _quarantine;
    private readonly IPlatformRepository _platform;
    private readonly ILogger _logger;

    private ScanSession? _lastSession;

    private static readonly string HistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidManager", "security", "scans");

    public SecurityService(IAdbService adb, IAdbSyncService sync, IPlatformRepository platform)
    {
        _adb = adb;
        _platform = platform;
        _db = new ThreatDatabase();
        _db.Load();
        _appScanner = new AppScanner(adb, sync, _db);
        _fileScanner = new FileScanner(adb, _db);
        _privilegeScanner = new PrivilegeScanner(adb);
        _persistenceScanner = new PersistenceScanner(adb);
        _integrityScanner = new SystemIntegrityScanner(adb);
        _quarantine = new QuarantineManager(adb, sync, platform);
        _logger = Log.ForContext<SecurityService>();
    }

    public Task<ScanSession> StartQuickScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        StartFullScanAsync(new ScanOptions
        {
            ScanApps = true,
            ScanFiles = false,
            ScanNetwork = false,
            ScanSystemFiles = false,
            ScanPrivileges = true,
            ScanPersistence = false,
            ScanIntegrity = true,
            DeepScan = false
        }, progress, cancellationToken);

    public Task<ScanSession> StartRescueScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        StartFullScanAsync(new ScanOptions
        {
            ScanApps = true,
            ScanFiles = true,
            ScanNetwork = true,
            ScanPrivileges = true,
            ScanPersistence = true,
            ScanIntegrity = true,
            DeepScan = false,
            RescueMode = true
        }, progress, cancellationToken);

    public async Task<ScanSession> StartFullScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.Now;
        var allThreats = new List<ThreatItem>();
        var riskFactors = new List<ThreatItem>();
        var deviceModel = await GetDeviceModelAsync(cancellationToken).ConfigureAwait(false);
        var staticC2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _logger.Information("Güvenlik taraması başladı (rescue={Rescue})", options.RescueMode);

        try
        {
            if (options.ScanApps)
            {
                Report(progress, "Uygulamalar taranıyor", 5);
                var appThreats = await _appScanner.ScanAllAppsAsync(
                    progress, options.DeepScan, options.RescueMode, cancellationToken).ConfigureAwait(false);
                allThreats.AddRange(appThreats);
                foreach (var n in appThreats.SelectMany(t => t.NetworkIndicators))
                    staticC2.Add(n);
            }

            if (options.ScanFiles)
            {
                Report(progress, "Dosyalar taranıyor", 30);
                allThreats.AddRange(await _fileScanner.ScanFilesAsync(progress, cancellationToken)
                    .ConfigureAwait(false));
            }

            if (options.ScanNetwork)
            {
                Report(progress, "Ağ analiz ediliyor", 50);
                var net = new NetworkScanner(_adb, _db, staticC2);
                allThreats.AddRange(await net.ScanNetworkAsync(progress, cancellationToken)
                    .ConfigureAwait(false));
            }

            if (options.ScanPrivileges)
            {
                Report(progress, "Ayrıcalıklar taranıyor", 65);
                allThreats.AddRange(await _privilegeScanner.ScanAsync(progress, cancellationToken)
                    .ConfigureAwait(false));
            }

            if (options.ScanPersistence)
            {
                Report(progress, "Kalıcılık taranıyor", 78);
                var focus = allThreats
                    .Where(t => !string.IsNullOrWhiteSpace(t.PackageName))
                    .Select(t => t.PackageName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                allThreats.AddRange(await _persistenceScanner.ScanAsync(focus, progress, cancellationToken)
                    .ConfigureAwait(false));
            }

            if (options.ScanIntegrity || options.ScanSystemFiles)
            {
                Report(progress, "Sistem bütünlüğü", 88);
                var (t, r) = await _integrityScanner.ScanAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
                allThreats.AddRange(t);
                riskFactors.AddRange(r);
            }

            var uniqueThreats = Deduplicate(allThreats)
                .Where(t => t.Type != ThreatType.RiskFactor)
                .Where(RiskEngine.MeetsThreatThreshold)
                .OrderByDescending(t => t.Severity)
                .ThenByDescending(t => t.RiskScore)
                .ToList();

            var uniqueRisks = Deduplicate(riskFactors)
                .OrderByDescending(t => t.Severity)
                .ToList();

            var score = RiskEngine.BuildScoreCard(uniqueThreats, uniqueRisks);
            var session = new ScanSession
            {
                StartedAt = started,
                CompletedAt = DateTime.Now,
                Threats = uniqueThreats,
                RiskFactors = uniqueRisks,
                Status = ScanStatus.Completed,
                DeviceModel = deviceModel,
                Score = score,
                ScannedApps = uniqueThreats.Count(t => t.Category == ThreatCategory.App)
            };

            _lastSession = session;
            await SaveScanHistoryAsync(session, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ScanProgress
            {
                Phase = uniqueThreats.Count == 0
                    ? "Bilinen tehdit bulunamadı"
                    : $"{uniqueThreats.Count} tehdit bulundu",
                Percentage = 100,
                ThreatsFound = uniqueThreats.Count,
                ElapsedTime = session.Duration.ToString(@"mm\:ss")
            });

            return session;
        }
        catch (OperationCanceledException)
        {
            return new ScanSession
            {
                StartedAt = started,
                CompletedAt = DateTime.Now,
                Status = ScanStatus.Cancelled,
                DeviceModel = deviceModel,
                Threats = allThreats,
                RiskFactors = riskFactors
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Tarama başarısız");
            return new ScanSession
            {
                StartedAt = started,
                CompletedAt = DateTime.Now,
                Status = ScanStatus.Failed,
                DeviceModel = deviceModel,
                Threats = allThreats,
                RiskFactors = riskFactors
            };
        }
    }

    public async Task<CleanResult> CleanThreatAsync(
        ThreatItem threat,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(threat.PackageName) &&
                threat.Category is ThreatCategory.App or ThreatCategory.Privilege or ThreatCategory.Persistence
                    or ThreatCategory.Permission)
            {
                if (threat.IsSystem)
                {
                    var disable = await _adb.ExecuteShellAsync(
                        $"pm disable-user --user 0 {threat.PackageName}", cancellationToken)
                        .ConfigureAwait(false);
                    var ok = disable.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
                             disable.Contains("new state", StringComparison.OrdinalIgnoreCase);
                    return new CleanResult
                    {
                        Threat = threat,
                        Success = ok,
                        ActionTaken = CleanAction.Disabled,
                        Message = ok
                            ? $"{threat.PackageName} devre dışı bırakıldı"
                            : $"Devre dışı bırakılamadı: {disable.Trim()}"
                    };
                }

                var uninstall = await _adb.ExecuteShellAsync(
                    $"pm uninstall {threat.PackageName}", cancellationToken).ConfigureAwait(false);
                var success = uninstall.Contains("Success", StringComparison.OrdinalIgnoreCase);
                if (!success)
                {
                    uninstall = await _adb.ExecuteShellAsync(
                        $"pm uninstall -k --user 0 {threat.PackageName}", cancellationToken)
                        .ConfigureAwait(false);
                    success = uninstall.Contains("Success", StringComparison.OrdinalIgnoreCase);
                }

                return new CleanResult
                {
                    Threat = threat,
                    Success = success,
                    ActionTaken = CleanAction.Uninstalled,
                    Message = success
                        ? $"{threat.PackageName} kaldırıldı"
                        : $"Kaldırılamadı — {threat.RemediationAdvice}"
                };
            }

            if (!string.IsNullOrWhiteSpace(threat.FilePath) && threat.Category == ThreatCategory.File)
            {
                await _adb.ExecuteShellAsync($"rm -f \"{threat.FilePath}\"", cancellationToken)
                    .ConfigureAwait(false);
                var check = await _adb.ExecuteShellAsync(
                    $"[ -e \"{threat.FilePath}\" ] && echo EXISTS || echo GONE", cancellationToken)
                    .ConfigureAwait(false);
                var ok = check.Trim().Contains("GONE", StringComparison.OrdinalIgnoreCase);
                return new CleanResult
                {
                    Threat = threat,
                    Success = ok,
                    ActionTaken = CleanAction.FileDeleted,
                    Message = ok ? $"Dosya silindi: {threat.FilePath}" : "Dosya silinemedi (yetki gerekebilir)"
                };
            }

            return new CleanResult
            {
                Threat = threat,
                Success = false,
                ActionTaken = CleanAction.ManualRequired,
                Message = "Manuel müdahale gerekiyor"
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Temizleme hatası: {Name}", threat.Name);
            return new CleanResult
            {
                Threat = threat,
                Success = false,
                Message = $"Temizleme başarısız: {ex.Message}"
            };
        }
    }

    public async Task<IReadOnlyList<CleanResult>> CleanAllThreatsAsync(
        IEnumerable<ThreatItem> threats,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = threats.ToList();
        var results = new List<CleanResult>();
        for (var i = 0; i < list.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CleanProgress
            {
                CurrentThreat = list[i].Name,
                Percentage = (i + 1) * 100 / list.Count,
                Done = i + 1,
                Total = list.Count
            });
            results.Add(await CleanThreatAsync(list[i], cancellationToken).ConfigureAwait(false));
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    public Task<bool> QuarantineAsync(ThreatItem threat, CancellationToken cancellationToken = default) =>
        _quarantine.QuarantineAsync(threat, cancellationToken);

    public Task<bool> RestoreFromQuarantineAsync(
        ThreatItem threat,
        CancellationToken cancellationToken = default) =>
        _quarantine.RestoreAsync(threat, StillLooksLikeThreatAsync, cancellationToken);

    public async Task<IReadOnlyList<ThreatItem>> GetQuarantineListAsync(
        CancellationToken cancellationToken = default) =>
        await _quarantine.GetListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<SecurityStatus> GetSecurityStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var quarantine = await _quarantine.GetListAsync(cancellationToken).ConfigureAwait(false);
        return new SecurityStatus
        {
            LastScanDate = _lastSession?.CompletedAt ?? default,
            IsClean = _lastSession?.IsClean ?? true,
            ThreatCount = _lastSession?.Threats.Count ?? 0,
            DefinitionsDate = _db.LastUpdated,
            DefinitionsVersion = _db.Version,
            DefinitionsUpToDate = (DateTime.UtcNow - _db.LastUpdated.ToUniversalTime()).TotalDays < 30,
            QuarantineCount = quarantine.Count,
            Score = _lastSession?.Score
        };
    }

    public async Task<bool> UpdateDefinitionsAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report(20);
            // Offline-first: refresh builtin into local custom store timestamp.
            var defs = ThreatDatabase.GetBuiltinDefinitions();
            defs.LastUpdated = DateTime.UtcNow;
            defs.Version = $"{defs.Version}+local";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AndroidManager", "security", "custom_definitions.json"))!);

            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AndroidManager", "security", "custom_definitions.json");
            progress?.Report(60);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(defs, new JsonSerializerOptions
            {
                WriteIndented = true
            }), cancellationToken).ConfigureAwait(false);
            _db.Load();
            progress?.Report(100);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Tanım güncelleme hatası");
            return false;
        }
    }

    public async Task<IReadOnlyList<ScanHistoryEntry>> GetScanHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await MigrateLegacyScanHistoryAsync(cancellationToken).ConfigureAwait(false);
        return await _platform.ListScanHistoryAsync(30, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveScanHistoryAsync(ScanSession session, CancellationToken cancellationToken)
    {
        try
        {
            await _platform.SaveScanHistoryAsync(new ScanHistoryEntry
            {
                SessionId = session.Id,
                StartedAt = session.StartedAt,
                CompletedAt = session.CompletedAt,
                ThreatCount = session.Threats.Count,
                CriticalCount = session.CriticalCount,
                IsClean = session.IsClean,
                Score = session.Score?.Score ?? 0,
                DeviceModel = session.DeviceModel
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Security] Scan history SQLite yazılamadı");
        }
    }

    private async Task MigrateLegacyScanHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(HistoryDir))
                return;

            var files = Directory.GetFiles(HistoryDir, "scan_*.json");
            if (files.Length == 0)
                return;

            foreach (var f in files.OrderByDescending(x => x).Take(30))
            {
                try
                {
                    var session = JsonSerializer.Deserialize<ScanSession>(
                        await File.ReadAllTextAsync(f, cancellationToken).ConfigureAwait(false));
                    if (session is null) continue;

                    await _platform.SaveScanHistoryAsync(new ScanHistoryEntry
                    {
                        SessionId = session.Id,
                        StartedAt = session.StartedAt,
                        CompletedAt = session.CompletedAt,
                        ThreatCount = session.Threats.Count,
                        CriticalCount = session.CriticalCount,
                        IsClean = session.IsClean,
                        Score = session.Score?.Score ?? 0,
                        DeviceModel = session.DeviceModel
                    }, cancellationToken).ConfigureAwait(false);

                    File.Move(f, f + ".migrated.bak", overwrite: true);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[Security] Legacy scan migrate skip {File}", f);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Security] Legacy scan history migration skipped");
        }
    }

    private async Task<bool> StillLooksLikeThreatAsync(ThreatItem threat, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(threat.PackageName))
        {
            var def = _db.CheckPackage(threat.PackageName);
            if (def is not null && def.Severity >= ThreatSeverity.High)
                return true;
        }

        if (!string.IsNullOrWhiteSpace(threat.HashSha256) || !string.IsNullOrWhiteSpace(threat.HashMd5))
        {
            var def = _db.CheckHash(threat.HashMd5, threat.HashSha256);
            if (def is not null) return true;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return false;
    }

    private async Task<string> GetDeviceModelAsync(CancellationToken ct)
    {
        try
        {
            return (await _adb.ExecuteShellAsync("getprop ro.product.model", ct).ConfigureAwait(false)).Trim();
        }
        catch
        {
            return "Bilinmiyor";
        }
    }

    private static List<ThreatItem> Deduplicate(List<ThreatItem> items) =>
        items
            .GroupBy(t => $"{t.PackageName}|{t.FilePath}|{t.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(t => t.RiskScore).ThenByDescending(t => t.Severity).First())
            .ToList();

    private static void Report(IProgress<ScanProgress>? progress, string phase, int pct) =>
        progress?.Report(new ScanProgress { Phase = phase, Percentage = pct });
}
