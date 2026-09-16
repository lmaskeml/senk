using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Root;
using AndroidManager.Security.Scanners;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed class RootSecurityService : IRootSecurityService
{
    private readonly RootManager _rootManager;
    private readonly DeepSystemScanner _systemScanner;
    private readonly RootCleaner _rootCleaner;
    private readonly ILogger _logger;

    public RootSecurityService(IAdbService adb, RootManager rootManager, ILogger? logger = null)
    {
        _logger = logger ?? Log.ForContext<RootSecurityService>();
        _rootManager = rootManager;
        _systemScanner = new DeepSystemScanner(adb, _rootManager, _logger);
        _rootCleaner = new RootCleaner(adb, _rootManager, _logger);
    }

    public Task<RootStatus> GetRootStatusAsync(CancellationToken cancellationToken = default) =>
        _rootManager.GetStatusAsync(cancellationToken);

    public async Task<bool> EnableRootAsync(
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        status?.Report("Root tespit ediliyor…");
        _rootManager.InvalidateCache();
        var st = await _rootManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (st.Access == RootAccess.None)
        {
            status?.Report("su binary bulunamadı — cihaz root'lu değil.");
            return false;
        }

        status?.Report("Root izni test ediliyor — cihazda açılan pencereye İzin Ver deyin…");
        var ok = await _rootManager.EnsureActiveAsync(cancellationToken).ConfigureAwait(false);
        var fresh = await _rootManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        status?.Report(ok
            ? $"Root aktif — {fresh.StatusText}"
            : "İzin verilmedi veya zaman aşımı. Magisk/SuperSU'dan izin verip tekrar deneyin.");

        _logger.Information("Root etkinleştirme: {Ok}", ok);
        return ok;
    }

    public Task<SystemScanResult> ScanSystemDeepAsync(
        SystemScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _systemScanner.ScanAsync(options, progress, cancellationToken);

    public Task<MountStatus> GetMountStatusAsync(CancellationToken cancellationToken = default) =>
        _systemScanner.GetMountStatusAsync(cancellationToken);

    public Task<BootSecurityInfo> GetBootSecurityAsync(CancellationToken cancellationToken = default) =>
        _systemScanner.GetBootSecurityAsync(cancellationToken);

    public Task<CleanResult> CleanThreatAsync(ThreatItem threat, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(threat.PackageName) &&
            (threat.IsSystem || threat.Category is ThreatCategory.System or ThreatCategory.Privilege))
            return RemoveSystemAppAsync(threat, cancellationToken);

        if (!string.IsNullOrEmpty(threat.FilePath))
            return QuarantineFileAsync(threat, cancellationToken);

        return Task.FromResult(new CleanResult
        {
            Threat = threat,
            Success = false,
            ActionTaken = CleanAction.ManualRequired,
            Message = "Bu tehdit türü root temizleme gerektirmiyor"
        });
    }

    public Task<CleanResult> RemoveSystemAppAsync(ThreatItem threat, CancellationToken cancellationToken = default) =>
        _rootCleaner.RemoveSystemAppAsync(threat, cancellationToken);

    public Task<CleanResult> QuarantineFileAsync(ThreatItem threat, CancellationToken cancellationToken = default) =>
        _rootCleaner.QuarantineSystemFileAsync(threat, cancellationToken);

    public Task<CleanResult> RestoreHostsAsync(CancellationToken cancellationToken = default) =>
        _rootCleaner.RestoreHostsAsync(cancellationToken);

    public Task<bool> KillProcessAsync(string packageName, CancellationToken cancellationToken = default) =>
        _rootCleaner.KillProcessAsync(packageName, cancellationToken);

    public Task<CleanResult> ClearAppDataAsync(string packageName, CancellationToken cancellationToken = default) =>
        _rootCleaner.ClearAppDataAsync(packageName, cancellationToken);

    public Task<bool> SetSystemMountAsync(bool writable, CancellationToken cancellationToken = default) =>
        _rootCleaner.SetSystemMountAsync(writable, cancellationToken);

    public Task<bool> RebootDeviceAsync(CancellationToken cancellationToken = default) =>
        _rootCleaner.RebootAsync(cancellationToken);
}
