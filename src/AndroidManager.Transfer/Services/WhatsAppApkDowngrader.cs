using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Transfer.Services;

/// <summary>
/// Temporarily installs a legacy WhatsApp APK that still allows unencrypted ADB backup, then restores the original APK.
/// </summary>
public sealed class WhatsAppApkDowngrader
{
    private const string WhatsAppPackage = "com.whatsapp";

    /// <summary>ADB backup-friendly legacy build (user-supplied). See tools/whatsapp/README.txt.</summary>
    public static readonly string DefaultLegacyApkRelative = Path.Combine("tools", "whatsapp", "WhatsApp-legacy-backup.apk");

    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly ILogger _logger;

    public WhatsAppApkDowngrader(IAdbService adb, IAdbSyncService sync, ILogger? logger = null)
    {
        _adb = adb;
        _sync = sync;
        _logger = logger ?? Log.ForContext<WhatsAppApkDowngrader>();
    }

    public string? ResolveLegacyApkPath(string? userProvidedPath)
    {
        if (!string.IsNullOrWhiteSpace(userProvidedPath))
        {
            if (File.Exists(userProvidedPath))
                return Path.GetFullPath(userProvidedPath);

            var relative = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, userProvidedPath));
            if (File.Exists(relative))
                return relative;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "whatsapp", "WhatsApp-legacy-backup.apk"),
            Path.Combine(AppContext.BaseDirectory, DefaultLegacyApkRelative),
            Path.Combine(AppContext.BaseDirectory, "tools", "whatsapp", "WhatsApp-legacy-backup.apk"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "whatsapp", "WhatsApp-legacy-backup.apk"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<string?> RunDowngradeBackupAsync(
        string androidDir,
        string legacyApkPath,
        IProgress<WhatsAppTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("Cihaz seçili değil.");

        if (!File.Exists(legacyApkPath))
            throw new FileNotFoundException("Legacy WhatsApp APK bulunamadı.", legacyApkPath);

        var apkBackupDir = Path.Combine(androidDir, "apk_backup");
        Directory.CreateDirectory(apkBackupDir);

        Report(progress, 32, "Mevcut WhatsApp APK yedekleniyor…");
        var backedUpApks = await BackupInstalledApkAsync(apkBackupDir, cancellationToken).ConfigureAwait(false);
        if (backedUpApks.Count == 0)
            throw new InvalidOperationException("Mevcut WhatsApp APK yedeklenemedi (pm path başarısız).");

        try
        {
            Report(progress, 38, "Legacy WhatsApp yükleniyor (downgrade)…");
            await InstallWithDowngradeAsync(legacyApkPath, cancellationToken).ConfigureAwait(false);

            Report(progress, 45, "Legacy WhatsApp ile ADB yedekleme — telefonda onaylayın…");
            var abPath = Path.GetFullPath(Path.Combine(androidDir, "whatsapp_downgrade.ab"));

            var backupProgress = progress is null
                ? null
                : new Progress<AdbAppBackupProgress>(p =>
                {
                    progress.Report(new WhatsAppTransferProgress
                    {
                        Phase = WhatsAppTransferPhase.ExtractingAndroid,
                        Percent = Math.Clamp(45 + Math.Min(p.ElapsedSeconds, 120) / 3, 45, 54),
                        Message = p.Message,
                        Detail = p.WaitingForUserConfirmation
                            ? "Legacy WhatsApp — telefonda 'Verilerimi yedekle' onayını verin."
                            : null
                    });
                });

            var backupResult = await _adb.RunInteractiveAppBackupAsync(
                    WhatsAppPackage,
                    abPath,
                    backupProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Debug("Downgrade adb backup: {Message}", backupResult.Message);
            return backupResult.Success ? abPath : null;
        }
        finally
        {
            try
            {
                Report(progress, 55, "Orijinal WhatsApp sürümü geri yükleniyor…");
                await RestoreBackedUpApksAsync(backedUpApks, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed restoring original WhatsApp APK — cihazda Play Store'dan güncelleyin");
            }
        }
    }

    private async Task<IReadOnlyList<string>> BackupInstalledApkAsync(
        string backupDir,
        CancellationToken cancellationToken)
    {
        var output = await _adb.ExecuteShellAsync($"pm path {WhatsAppPackage}", cancellationToken)
            .ConfigureAwait(false);

        var remotePaths = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("package:", StringComparison.Ordinal))
            .Select(line => line["package:".Length..].Trim())
            .Where(path => path.Length > 0)
            .ToList();

        var localPaths = new List<string>();
        for (var i = 0; i < remotePaths.Count; i++)
        {
            var remote = remotePaths[i];
            var local = Path.Combine(backupDir, i == 0 ? "base.apk" : $"split_{i}.apk");
            await _sync.PullAsync(remote, local, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (File.Exists(local))
                localPaths.Add(local);
        }

        await File.WriteAllLinesAsync(
            Path.Combine(backupDir, "paths.txt"),
            remotePaths,
            cancellationToken).ConfigureAwait(false);

        return localPaths;
    }

    private async Task InstallWithDowngradeAsync(string apkPath, CancellationToken cancellationToken)
    {
        var serial = _adb.SelectedDevice!.Serial;
        var (exitCode, output) = await _adb.RunHostAdbAsync(
            serial,
            $"install -d -r \"{apkPath}\"",
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
            throw new InvalidOperationException($"Legacy WhatsApp yüklenemedi: {output}");
    }

    private async Task RestoreBackedUpApksAsync(
        IReadOnlyList<string> localApkPaths,
        CancellationToken cancellationToken)
    {
        if (localApkPaths.Count == 0)
            return;

        var result = await _adb.InstallLocalPackagesAsync(localApkPaths, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Orijinal WhatsApp geri yüklenemedi: {result.Message}");
    }

    private static void Report(IProgress<WhatsAppTransferProgress>? progress, int percent, string message) =>
        progress?.Report(new WhatsAppTransferProgress
        {
            Phase = WhatsAppTransferPhase.ExtractingAndroid,
            Percent = percent,
            Message = message
        });
}
