using System.Text;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Transfer.Services;

public sealed class AndroidWhatsAppExtractor
{
    private const string WhatsAppPackage = "com.whatsapp";
    private static readonly string[] MediaRemoteRoots =
    [
        "/sdcard/Android/media/com.whatsapp/WhatsApp/Media",
        "/storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media",
        "/sdcard/WhatsApp/Media",
        "/storage/emulated/0/WhatsApp/Media"
    ];

    private static readonly string[] DbRootPaths =
    [
        "/data/data/com.whatsapp/databases/msgstore.db",
        "/data/user/0/com.whatsapp/databases/msgstore.db"
    ];

    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly IElevatedShellService _elevated;
    private readonly AndroidBackupArchiveReader _archiveReader;
    private readonly WhatsAppApkDowngrader _downgrader;
    private readonly ILogger _logger;

    public AndroidWhatsAppExtractor(
        IAdbService adb,
        IAdbSyncService sync,
        IElevatedShellService elevated,
        AndroidBackupArchiveReader archiveReader,
        WhatsAppApkDowngrader downgrader,
        ILogger? logger = null)
    {
        _adb = adb;
        _sync = sync;
        _elevated = elevated;
        _archiveReader = archiveReader;
        _downgrader = downgrader;
        _logger = logger ?? Log.ForContext<AndroidWhatsAppExtractor>();
    }

    public async Task<WhatsAppAndroidExtractResult> ExtractAsync(
        string sessionDirectory,
        bool includeMedia,
        bool tryAdbBackup,
        bool tryDowngradeOnBackupFailure,
        string? legacyApkPath,
        IProgress<WhatsAppTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("Android cihaz seçili değil. USB hata ayıklama açık bir cihaz bağlayın.");

        Directory.CreateDirectory(sessionDirectory);
        var androidDir = Path.Combine(sessionDirectory, "android");
        Directory.CreateDirectory(androidDir);

        Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 5, "WhatsApp paketi kontrol ediliyor…");

        var installed = await IsWhatsAppInstalledAsync(cancellationToken).ConfigureAwait(false);
        if (!installed)
            throw new InvalidOperationException("Cihazda WhatsApp yüklü değil.");

        var warnings = new List<string>();
        string? msgStorePath = null;
        var usedRoot = false;
        var usedBackup = false;
        var usedDowngrade = false;
        string? rootDetail = null;

        Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 15,
            "Root ile msgstore.db aranıyor — Magisk Superuser izni istenirse telefonda onaylayın…");
        (msgStorePath, rootDetail) = await TryRootDatabaseCopyAsync(androidDir, progress, cancellationToken)
            .ConfigureAwait(false);
        usedRoot = msgStorePath is not null;
        if (!usedRoot && rootDetail is not null)
            warnings.Add("Root: " + rootDetail);

        if (msgStorePath is null && tryAdbBackup)
        {
            Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 28,
                "ADB yedekleme — telefonda onay penceresini bekleyin…");
            var (adbPath, adbDetail) = await TryAdbBackupAsync(androidDir, "whatsapp.ab", progress, cancellationToken)
                .ConfigureAwait(false);
            msgStorePath = adbPath;
            usedBackup = msgStorePath is not null;
            if (!usedBackup && adbDetail is not null)
                warnings.Add("ADB: " + adbDetail);

            if (msgStorePath is null && tryDowngradeOnBackupFailure)
            {
                var legacyApk = _downgrader.ResolveLegacyApkPath(legacyApkPath);
                if (legacyApk is not null)
                {
                    Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 30,
                        "ADB yedek başarısız — legacy WhatsApp downgrade deneniyor…");
                    try
                    {
                        var abFromDowngrade = await _downgrader.RunDowngradeBackupAsync(
                                androidDir,
                                legacyApk,
                                progress,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (abFromDowngrade is not null)
                        {
                            var extractDir = Path.Combine(androidDir, "ab_extract_downgrade");
                            msgStorePath = await _archiveReader.ExtractWhatsAppDatabaseAsync(
                                    abFromDowngrade,
                                    extractDir,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            usedDowngrade = msgStorePath is not null;
                            usedBackup = usedDowngrade;
                            if (!usedDowngrade)
                                warnings.Add(
                                    "Legacy downgrade yedeği alındı ama içinde msgstore.db yok.");
                        }
                        else
                        {
                            warnings.Add("Legacy downgrade ADB yedeği alınamadı.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "WhatsApp downgrade backup failed");
                        warnings.Add($"Downgrade yedekleme başarısız: {ex.Message}");
                    }
                }
                else
                {
                    warnings.Add(
                        "Legacy WhatsApp APK dosyası yok. Klasör simgesinden 2.21.x APK seçin " +
                        "veya tools/whatsapp/WhatsApp-legacy-backup.apk koyup uygulamayı yeniden başlatın.");
                }
            }
        }

        if (msgStorePath is null)
        {
            Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 32,
                "Root ile son bir deneme yapılıyor…");
            (msgStorePath, rootDetail) = await TryRootDatabaseCopyAsync(
                    androidDir,
                    progress,
                    cancellationToken,
                    waitForRootSeconds: 90)
                .ConfigureAwait(false);
            usedRoot = msgStorePath is not null;
            if (!usedRoot && rootDetail is not null &&
                !warnings.Any(w => w.StartsWith("Root:", StringComparison.Ordinal)))
                warnings.Add("Root (son deneme): " + rootDetail);
        }

        if (msgStorePath is null)
        {
            var detail = warnings.Count > 0
                ? "\n\n" + string.Join("\n", warnings)
                : string.Empty;
            throw new InvalidOperationException(
                "msgstore.db alınamadı.\n\n" +
                "• Magisk → Superuser → Shell (veya adb) için İzin Ver + Kalıcı\n" +
                "• ADB 'Verilerimi yedekle' güncel WhatsApp'ta sohbet DB vermez\n" +
                "• Legacy APK (2.21.x) seçerek downgrade yedeklemeyi deneyin" +
                detail);
        }

        var (messageCount, chatCount) = await CountAndroidStatsAsync(msgStorePath, cancellationToken)
            .ConfigureAwait(false);

        string? mediaDir = null;
        if (includeMedia)
        {
            Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 70, "Medya dosyaları çekiliyor…");
            mediaDir = await PullMediaAsync(androidDir, progress, cancellationToken).ConfigureAwait(false);
            if (mediaDir is null)
                warnings.Add("WhatsApp medya klasörü bulunamadı veya boş.");
        }

        Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 95, "Oturum kaydediliyor…");

        return new WhatsAppAndroidExtractResult
        {
            SessionDirectory = sessionDirectory,
            MsgStorePath = msgStorePath,
            MediaDirectory = mediaDir,
            MessageCount = messageCount,
            ChatCount = chatCount,
            UsedRootCopy = usedRoot,
            UsedAdbBackup = usedBackup,
            UsedDowngrade = usedDowngrade,
            Warnings = warnings
        };
    }

    private async Task<bool> IsWhatsAppInstalledAsync(CancellationToken cancellationToken)
    {
        var output = await _adb.ExecuteShellAsync($"pm path {WhatsAppPackage}", cancellationToken)
            .ConfigureAwait(false);
        return output.Contains("package:", StringComparison.Ordinal);
    }

    private async Task<(string? Path, string? Detail)> TryRootDatabaseCopyAsync(
        string androidDir,
        IProgress<WhatsAppTransferProgress>? progress,
        CancellationToken cancellationToken,
        int waitForRootSeconds = 180)
    {
        if (!await WaitForRootAccessAsync(progress, waitForRootSeconds, cancellationToken).ConfigureAwait(false))
        {
            _logger.Warning("Root erişimi doğrulanamadı (Magisk izni verilmedi veya su yok)");
            return (null,
                "Magisk Superuser doğrulanamadı. Magisk → Superuser → Shell/adb için İzin Ver + Kalıcı seçin. " +
                "(ADB 'Verilerimi yedekle' root değildir.)");
        }

        await _adb.ExecuteShellAsync("mkdir -p /sdcard/AndroidManager", cancellationToken).ConfigureAwait(false);

        Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 20, "WhatsApp kapatılıyor (root)…");
        await PushAndRunRootScriptAsync(
                "am force-stop com.whatsapp 2>/dev/null; echo STOP_OK",
                cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

        var remoteTemp = "/sdcard/AndroidManager/wa_msgstore.db";
        var candidatePaths = await DiscoverMsgStorePathsAsync(cancellationToken).ConfigureAwait(false);
        _logger.Information("Root msgstore adayları: {Paths}", string.Join(" | ", candidatePaths));

        if (candidatePaths.Count == 0)
            return (null, "Root çalıştı ama msgstore.db bulunamadı (WhatsApp veri klasörü yok?).");

        var lastCopyOut = string.Empty;
        foreach (var dbPath in candidatePaths)
        {
            Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 22,
                $"Root ile kopyalanıyor: {dbPath}");

            var script = BuildRootCopyScript(dbPath, remoteTemp);
            var result = await PushAndRunRootScriptAsync(script, cancellationToken).ConfigureAwait(false);
            lastCopyOut = result.Trim();
            _logger.Debug("Root copy script output for {Remote}: {Out}", dbPath, lastCopyOut);

            if (lastCopyOut.Contains("COPY_FAIL", StringComparison.Ordinal) ||
                lastCopyOut.Contains("MISSING", StringComparison.Ordinal))
                continue;

            var sizeLine = lastCopyOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .LastOrDefault(l => l.StartsWith("SIZE=", StringComparison.Ordinal));
            if (sizeLine is null ||
                !long.TryParse(sizeLine["SIZE=".Length..], out var remoteSize) ||
                remoteSize < 4096)
            {
                _logger.Warning("Root copy too small for {Remote}: {Out}", dbPath, lastCopyOut);
                continue;
            }

            var localPath = Path.Combine(androidDir, "msgstore.db");
            try
            {
                await _sync.PullAsync(remoteTemp, localPath, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await _adb.ExecuteShellAsync($"rm -f \"{remoteTemp}\"", cancellationToken).ConfigureAwait(false);

                await TryPullSidecarAsync(dbPath + "-wal", localPath + "-wal", cancellationToken)
                    .ConfigureAwait(false);
                await TryPullSidecarAsync(dbPath + "-shm", localPath + "-shm", cancellationToken)
                    .ConfigureAwait(false);

                if (File.Exists(localPath) && new FileInfo(localPath).Length > 4096)
                {
                    _logger.Information("WhatsApp DB copied via root from {Remote} ({Bytes} bytes)", dbPath, remoteSize);
                    return (localPath, null);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Root DB pull failed for {Remote}", dbPath);
                lastCopyOut = "pull: " + ex.Message;
            }
        }

        var snippet = string.IsNullOrWhiteSpace(lastCopyOut)
            ? "çıktı yok"
            : (lastCopyOut.Length <= 180 ? lastCopyOut : lastCopyOut[..180]);
        return (null, $"Root doğrulandı ama dosya kopyalanamadı ({snippet}).");
    }

    private static string BuildRootCopyScript(string dbPath, string remoteTemp) =>
        $$"""
          set -e
          SRC='{{dbPath}}'
          DST='{{remoteTemp}}'
          rm -f "$DST"
          if [ ! -f "$SRC" ]; then
            echo MISSING
            exit 0
          fi
          if cp -f "$SRC" "$DST" 2>/dev/null; then
            :
          elif cat "$SRC" > "$DST" 2>/dev/null; then
            :
          elif dd if="$SRC" of="$DST" bs=1M 2>/dev/null; then
            :
          else
            echo COPY_FAIL
            exit 0
          fi
          chmod 644 "$DST" 2>/dev/null
          if [ -f "$DST" ]; then
            echo SIZE=$(stat -c%s "$DST" 2>/dev/null || wc -c < "$DST")
            echo COPY_OK
          else
            echo COPY_FAIL
          fi
          """;

    private async Task TryPullSidecarAsync(
        string remoteDbPath,
        string localPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var remoteTemp = "/sdcard/AndroidManager/" + Path.GetFileName(localPath);
            var script =
                $"SRC='{remoteDbPath}'; DST='{remoteTemp}'; " +
                "if [ -f \"$SRC\" ]; then cp -f \"$SRC\" \"$DST\" && chmod 644 \"$DST\" && echo SIDE_OK; else echo SIDE_SKIP; fi";
            var outp = await PushAndRunRootScriptAsync(script, cancellationToken).ConfigureAwait(false);
            if (!outp.Contains("SIDE_OK", StringComparison.Ordinal))
                return;

            await _sync.PullAsync(remoteTemp, localPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await _adb.ExecuteShellAsync($"rm -f \"{remoteTemp}\"", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Sidecar pull skipped for {Remote}", remoteDbPath);
        }
    }

    private async Task<string> PushAndRunRootScriptAsync(string script, CancellationToken cancellationToken)
    {
        const string remoteScript = "/data/local/tmp/am_wa_root_job.sh";
        var localScript = Path.Combine(Path.GetTempPath(), $"am_wa_root_{Guid.NewGuid():N}.sh");
        try
        {
            await File.WriteAllTextAsync(
                    localScript,
                    script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);

            await _sync.PushAsync(localScript, remoteScript, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await _adb.ExecuteShellAsync($"chmod 755 \"{remoteScript}\"", cancellationToken)
                .ConfigureAwait(false);

            return await _elevated.RunAsync(
                    $"sh \"{remoteScript}\"",
                    TimeSpan.FromMinutes(3),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(localScript))
                    File.Delete(localScript);
            }
            catch
            {
            }

            try
            {
                await _adb.ExecuteShellAsync($"rm -f \"{remoteScript}\"", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task<bool> WaitForRootAccessAsync(
        IProgress<WhatsAppTransferProgress>? progress,
        int maxWaitSeconds,
        CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var attempt = 0;

        while ((Environment.TickCount64 - started) / 1000 < maxWaitSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            var elapsed = (int)((Environment.TickCount64 - started) / 1000);
            Report(progress, WhatsAppTransferPhase.ExtractingAndroid, Math.Min(15 + elapsed / 6, 26),
                $"Magisk Superuser izni bekleniyor… ({elapsed} sn)",
                "Bu ADB yedek onayı değil. Magisk bildirimine dokunun → Shell için İzin Ver.");

            var rootTask = _elevated.RunAsync("id -u; id", TimeSpan.FromMinutes(3), cancellationToken);
            while (!rootTask.IsCompleted)
            {
                await Task.WhenAny(rootTask, Task.Delay(2000, cancellationToken)).ConfigureAwait(false);
                if (!rootTask.IsCompleted)
                {
                    elapsed = (int)((Environment.TickCount64 - started) / 1000);
                    Report(progress, WhatsAppTransferPhase.ExtractingAndroid, Math.Min(15 + elapsed / 6, 26),
                        $"Magisk Superuser izni bekleniyor… ({elapsed} sn)",
                        "Magisk → Superuser listesinde Shell = Allow (kalıcı).");
                }
            }

            var idOutput = await rootTask.ConfigureAwait(false);
            if (idOutput.Contains("uid=0", StringComparison.OrdinalIgnoreCase) ||
                idOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(l => l.Trim() == "0"))
            {
                _logger.Information("Root doğrulandı (deneme {Attempt}, {Elapsed}s): {Out}",
                    attempt, elapsed, idOutput.Trim());
                return true;
            }

            _logger.Debug("Root henüz yok (deneme {Attempt}): {Output}", attempt, idOutput.Trim());
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<IReadOnlyList<string>> DiscoverMsgStorePathsAsync(CancellationToken cancellationToken)
    {
        var paths = new List<string>();

        var findScript = """
            for d in /data/data/com.whatsapp /data/user/0/com.whatsapp /data/user/*/com.whatsapp; do
              [ -f "$d/databases/msgstore.db" ] && echo "$d/databases/msgstore.db"
            done
            find /data/data/com.whatsapp /data/user -name msgstore.db 2>/dev/null | head -8
            """;

        var findOutput = await PushAndRunRootScriptAsync(findScript, cancellationToken).ConfigureAwait(false);

        foreach (var line in findOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('/') && trimmed.EndsWith("msgstore.db", StringComparison.Ordinal) &&
                !paths.Contains(trimmed, StringComparer.Ordinal))
                paths.Add(trimmed);
        }

        foreach (var known in DbRootPaths)
        {
            if (!paths.Contains(known, StringComparer.Ordinal))
                paths.Add(known);
        }

        return paths;
    }

    private async Task<(string? Path, string? Detail)> TryAdbBackupAsync(
        string androidDir,
        string abFileName,
        IProgress<WhatsAppTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var abPath = Path.GetFullPath(Path.Combine(androidDir, abFileName));

        var backupProgress = progress is null
            ? null
            : new Progress<AdbAppBackupProgress>(p =>
            {
                var percent = Math.Clamp(28 + Math.Min(p.ElapsedSeconds, 240) / 4, 28, 65);
                progress.Report(new WhatsAppTransferProgress
                {
                    Phase = WhatsAppTransferPhase.ExtractingAndroid,
                    Percent = percent,
                    Message = p.Message,
                    Detail = p.WaitingForUserConfirmation
                        ? $"Deneme {p.Attempt}/{p.MaxAttempts} — telefon ekranında onay verin (şifre kullanmayın)."
                        : p.FileSizeKb > 0 ? $"İndirilen yedek: {p.FileSizeKb} KB" : null
                });
            });

        var result = await _adb.RunInteractiveAppBackupAsync(
                WhatsAppPackage,
                abPath,
                backupProgress,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.Debug("adb backup result: success={Success} attempts={Attempts} msg={Message}",
            result.Success, result.AttemptsUsed, result.Message);

        if (!result.Success)
        {
            Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 30, result.Message,
                "Telefonda 'Verilerimi yedekle' butonuna basın; şifre alanını boş bırakın.");
            return (null, result.Message);
        }

        var abSizeKb = File.Exists(abPath) ? new FileInfo(abPath).Length / 1024 : 0;

        try
        {
            var extractDir = Path.Combine(androidDir, abFileName.Contains("downgrade") ? "ab_extract_downgrade" : "ab_extract");
            var db = await _archiveReader.ExtractWhatsAppDatabaseAsync(abPath, extractDir, cancellationToken)
                .ConfigureAwait(false);
            if (db is null)
            {
                return (null,
                    $"Yedek alındı ({abSizeKb} KB) ama içinde msgstore.db yok. " +
                    "Güncel WhatsApp ADB yedeğe sohbet veritabanı koymaz — Magisk root veya legacy APK gerekir.");
            }

            return (db, null);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "AB extraction failed");
            var hint = ex.Message.Contains("şifre", StringComparison.OrdinalIgnoreCase)
                ? ex.Message
                : $"Yedek alındı ({abSizeKb} KB) ancak açılamadı: {ex.Message}";
            Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 30, hint, ex.Message);
            return (null, hint);
        }
    }

    private async Task<string?> PullMediaAsync(
        string androidDir,
        IProgress<WhatsAppTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var remote in MediaRemoteRoots)
        {
            var test = await _adb.ExecuteShellAsync($"ls \"{remote}\" 2>/dev/null | head -1", cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(test))
                continue;

            var localMedia = Path.Combine(androidDir, "media");
            Directory.CreateDirectory(localMedia);

            try
            {
                await _sync.PullAsync(remote, localMedia, progress: null, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (Directory.EnumerateFileSystemEntries(localMedia).Any())
                {
                    Report(progress, WhatsAppTransferPhase.ExtractingAndroid, 85, "Medya indirildi.");
                    return localMedia;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Media pull failed for {Remote}", remote);
            }
        }

        return null;
    }

    private static async Task<(int Messages, int Chats)> CountAndroidStatsAsync(
        string msgStorePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={msgStorePath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var messages = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM message", cancellationToken)
                .ConfigureAwait(false);
            if (messages == 0)
                messages = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM messages", cancellationToken)
                    .ConfigureAwait(false);

            var chats = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM chat", cancellationToken)
                .ConfigureAwait(false);
            if (chats == 0)
                chats = await ScalarIntAsync(
                        connection,
                        "SELECT COUNT(DISTINCT key_remote_jid) FROM messages",
                        cancellationToken)
                    .ConfigureAwait(false);

            return (messages, chats);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static async Task<int> ScalarIntAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long l ? (int)l : Convert.ToInt32(result ?? 0);
    }

    private static void Report(
        IProgress<WhatsAppTransferProgress>? progress,
        WhatsAppTransferPhase phase,
        int percent,
        string message,
        string? detail = null) =>
        progress?.Report(new WhatsAppTransferProgress
        {
            Phase = phase,
            Percent = percent,
            Message = message,
            Detail = detail
        });
}
