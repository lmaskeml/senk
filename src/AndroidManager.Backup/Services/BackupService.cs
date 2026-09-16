using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AndroidManager.Backup.Data;
using AndroidManager.Backup.Parsing;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Backup.Services;

public sealed partial class BackupService : IBackupService
{
    private static readonly string[] MediaSearchRoots =
    [
        "/sdcard/DCIM",
        "/sdcard/Pictures",
        "/sdcard/Movies",
        "/sdcard/Download",
        "/sdcard/Camera",
        "/storage/emulated/0/DCIM",
        "/storage/emulated/0/Pictures",
        "/storage/emulated/0/Movies",
        "/storage/emulated/0/Download"
    ];

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".heic", ".webp", ".gif", ".bmp"];

    private static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".webm", ".3gp"];

    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly IDeviceInfoService _deviceInfo;
    private readonly IGalleryService _gallery;
    private readonly ISmsService _sms;
    private readonly IContactsService _contacts;
    private readonly IElevatedShellService _elevated;
    private readonly BackupRepository _repo;
    private readonly ILogger _logger;

    private static readonly TimeSpan AppDataRootTimeout = TimeSpan.FromMinutes(10);
    private const string RemoteAppDataDir = "/sdcard/AndroidManager/appdata";
    private const int MaxUserPackages = 250;

    public BackupService(
        IAdbService adb,
        IAdbSyncService sync,
        IDeviceInfoService deviceInfo,
        IGalleryService gallery,
        ISmsService sms,
        IContactsService contacts,
        IElevatedShellService elevated,
        BackupRepository repo,
        ILogger? logger = null)
    {
        _adb = adb;
        _sync = sync;
        _deviceInfo = deviceInfo;
        _gallery = gallery;
        _sms = sms;
        _contacts = contacts;
        _elevated = elevated;
        _repo = repo;
        _logger = logger ?? Log.ForContext<BackupService>();
    }

    public async Task<BackupJob> CreateBackupAsync(
        BackupOptions options,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var device = await _deviceInfo.GetDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
        var job = new BackupJob
        {
            DeviceModel = device.Model,
            DeviceSerial = device.Serial,
            Type = options.Type,
            SavePath = options.SavePath,
            CreatedAt = DateTime.Now,
            Status = BackupStatus.Running,
            Note = options.Note
        };

        job.Id = await _repo.InsertJobAsync(job, cancellationToken).ConfigureAwait(false);

        try
        {
            var backupDir = Path.Combine(
                options.SavePath,
                $"backup_{DateTime.Now:yyyyMMdd_HHmmss}_{Sanitize(device.Serial)}");
            Directory.CreateDirectory(backupDir);

            var totalSteps = Math.Max(1, CountFlags(options.Type));
            var step = 0;
            var categories = new List<BackupCategoryResult>();

            if (options.Type.HasFlag(BackupType.Sms))
            {
                Report(progress, "SMS yedekleniyor...", BackupType.Sms, step * 100 / totalSteps);
                categories.Add(await BackupSmsCategoryAsync(backupDir, cancellationToken).ConfigureAwait(false));
                step++;
            }

            if (options.Type.HasFlag(BackupType.Contacts))
            {
                Report(progress, "Rehber yedekleniyor...", BackupType.Contacts, step * 100 / totalSteps);
                categories.Add(await BackupContactsCategoryAsync(backupDir, cancellationToken).ConfigureAwait(false));
                step++;
            }

            if (options.Type.HasFlag(BackupType.Photos))
            {
                Report(progress, "Fotoğraflar yedekleniyor...", BackupType.Photos, step * 100 / totalSteps);
                var photoCount = await BackupGalleryOrFindAsync(
                    GalleryMediaKind.Image,
                    backupDir,
                    "Photos",
                    ImageExtensions,
                    BackupType.Photos,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                categories.Add(new BackupCategoryResult(
                    "Foto",
                    photoCount,
                    photoCount == 0
                        ? "MedyaStore/DCIM/Pictures altında foto bulunamadı"
                        : null));
                step++;
            }

            if (options.Type.HasFlag(BackupType.Videos))
            {
                Report(progress, "Videolar yedekleniyor...", BackupType.Videos, step * 100 / totalSteps);
                var videoCount = await BackupGalleryOrFindAsync(
                    GalleryMediaKind.Video,
                    backupDir,
                    "Videos",
                    VideoExtensions,
                    BackupType.Videos,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                categories.Add(new BackupCategoryResult(
                    "Video",
                    videoCount,
                    videoCount == 0
                        ? "MedyaStore/Movies/DCIM altında video bulunamadı"
                        : null));
                step++;
            }

            if (options.Type.HasFlag(BackupType.Apps))
            {
                Report(progress, "Uygulamalar yedekleniyor...", BackupType.Apps, step * 100 / totalSteps);
                var apkCount = await BackupAppsAsync(backupDir, progress, cancellationToken).ConfigureAwait(false);
                categories.Add(new BackupCategoryResult(
                    "APK",
                    apkCount,
                    apkCount == 0 ? "Kullanıcı uygulaması bulunamadı" : null));
                step++;
            }

            if (options.Type.HasFlag(BackupType.AppData))
            {
                Report(progress, "Uygulama/oyun verisi yedekleniyor (root)...", BackupType.AppData,
                    step * 100 / totalSteps);
                var (dataCount, dataWarning) = await BackupAppDataAsync(backupDir, progress, cancellationToken)
                    .ConfigureAwait(false);
                categories.Add(new BackupCategoryResult("Veri", dataCount, dataWarning));
            }

            await WriteManifestAsync(backupDir, device, options, categories, cancellationToken)
                .ConfigureAwait(false);

            var summary = FormatSummary(categories);
            var hasEmpty = categories.Any(c => c.Count == 0 || !string.IsNullOrWhiteSpace(c.Warning));
            job.Note = string.IsNullOrWhiteSpace(options.Note)
                ? summary
                : $"{options.Note} | {summary}";

            if (options.Compress)
            {
                Report(progress, "Sıkıştırılıyor...", options.Type, 95);
                var zipPath = backupDir + ".zip";
                await CreateZipWithCancellationAsync(backupDir, zipPath, cancellationToken)
                    .ConfigureAwait(false);
                Directory.Delete(backupDir, recursive: true);
                job.SavePath = zipPath;
                job.SizeBytes = new FileInfo(zipPath).Length;
            }
            else
            {
                job.SavePath = backupDir;
                job.SizeBytes = GetDirectorySize(backupDir);
            }

            job.Status = BackupStatus.Completed;
            await _repo.UpdateJobAsync(job, cancellationToken).ConfigureAwait(false);
            Report(
                progress,
                hasEmpty
                    ? $"Kısmi tamamlandı — {job.SizeFormatted} ({summary})"
                    : $"Yedekleme tamamlandı — {job.SizeFormatted} ({summary})",
                options.Type,
                100);
        }
        catch (OperationCanceledException)
        {
            job.Status = BackupStatus.Cancelled;
            await _repo.UpdateJobAsync(job, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup failed: {Id}", job.Id);
            job.Status = BackupStatus.Failed;
            await _repo.UpdateJobAsync(job, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return job;
    }

    public async Task<RestoreResult> RestoreBackupAsync(
        string backupPath,
        RestoreOptions options,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workDir = backupPath;
        var tempExtract = false;

        if (backupPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            workDir = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(backupPath) + "_" + Guid.NewGuid().ToString("N"));
            Report(progress, "Arşiv açılıyor...", options.Type, 5);
            await ExtractZipWithCancellationAsync(backupPath, workDir, cancellationToken)
                .ConfigureAwait(false);
            tempExtract = true;
        }

        var restored = 0;
        try
        {
            if (options.Type.HasFlag(BackupType.Contacts))
            {
                var vcfPath = Path.Combine(workDir, "contacts.vcf");
                if (File.Exists(vcfPath))
                {
                    const string dest = "/sdcard/restore_contacts.vcf";
                    await _sync.PushAsync(vcfPath, dest, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await _adb.ExecuteShellAsync(
                            $"am start -a android.intent.action.VIEW -t text/x-vcard -d file://{dest}",
                            cancellationToken)
                        .ConfigureAwait(false);
                    restored++;
                }
            }

            if (options.Type.HasFlag(BackupType.Photos))
            {
                restored += await RestoreMediaFolderAsync(
                    Path.Combine(workDir, "Photos"),
                    "/sdcard/DCIM/Restored",
                    BackupType.Photos,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            if (options.Type.HasFlag(BackupType.Videos))
            {
                restored += await RestoreMediaFolderAsync(
                    Path.Combine(workDir, "Videos"),
                    "/sdcard/DCIM/RestoredVideos",
                    BackupType.Videos,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            if (options.Type.HasFlag(BackupType.Apps))
            {
                restored += await RestoreApksAsync(workDir, options, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (options.Type.HasFlag(BackupType.AppData))
            {
                if (!options.Type.HasFlag(BackupType.Apps))
                {
                    restored += await EnsureApksInstalledForAppDataAsync(
                            workDir, options, progress, cancellationToken)
                        .ConfigureAwait(false);
                }

                restored += await RestoreAppDataAsync(workDir, options, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (options.Type.HasFlag(BackupType.Sms))
                _logger.Information("SMS restore requires root/companion app; skipped.");
        }
        finally
        {
            if (tempExtract && Directory.Exists(workDir))
            {
                try { Directory.Delete(workDir, true); } catch { /* ignore */ }
            }
        }

        return new RestoreResult
        {
            Success = true,
            Message = $"{restored} öğe geri yüklendi",
            ItemRestored = restored
        };
    }

    public Task<IReadOnlyList<BackupAppEntry>> InspectBackupAppsAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => BackupArchiveInspector.Inspect(backupPath), cancellationToken);
    }

    public Task<IReadOnlyList<BackupJob>> GetBackupHistoryAsync(CancellationToken cancellationToken = default) =>
        _repo.GetAllJobsAsync(cancellationToken);

    public Task DeleteBackupAsync(int jobId, CancellationToken cancellationToken = default) =>
        _repo.DeleteJobAsync(jobId, cancellationToken);

    public async Task<IReadOnlyList<SmsMessage>> GetSmsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _sms.GetMessagesAsync(2000, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SMS list via ISmsService failed; falling back to raw query");
            var raw = await _adb.ExecuteShellAsync(
                    "content query --uri content://sms --projection _id,address,body,date,type",
                    cancellationToken)
                .ConfigureAwait(false);
            return BackupOutputParsers.ParseSmsOutput(raw);
        }
    }

    public async Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var phoneContacts = await _contacts.GetContactsAsync(cancellationToken).ConfigureAwait(false);
            return phoneContacts.Select(MapContact).ToList();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Contacts list via IContactsService failed; falling back to raw query");
            var raw = await _adb.ExecuteShellAsync(
                    "content query --uri content://com.android.contacts/data/phones --projection contact_id,display_name,data1",
                    cancellationToken)
                .ConfigureAwait(false);
            return BackupOutputParsers.ParseContacts(raw);
        }
    }

    public async Task<IReadOnlyList<MediaFile>> GetPhotosAsync(CancellationToken cancellationToken = default)
    {
        var fromGallery = await _gallery.GetMediaAsync(GalleryMediaKind.Image, 1000, cancellationToken)
            .ConfigureAwait(false);
        if (fromGallery.Count > 0)
            return fromGallery.Select(MapMedia).ToList();

        return await GetMediaFilesAsync(ImageExtensions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaFile>> GetVideosAsync(CancellationToken cancellationToken = default)
    {
        var fromGallery = await _gallery.GetMediaAsync(GalleryMediaKind.Video, 1000, cancellationToken)
            .ConfigureAwait(false);
        if (fromGallery.Count > 0)
            return fromGallery.Select(MapMedia).ToList();

        return await GetMediaFilesAsync(VideoExtensions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BackupCategoryResult> BackupSmsCategoryAsync(
        string backupDir,
        CancellationToken cancellationToken)
    {
        try
        {
            var messages = await GetSmsAsync(cancellationToken).ConfigureAwait(false);
            await SaveSmsAsync(messages, backupDir, cancellationToken).ConfigureAwait(false);
            return new BackupCategoryResult(
                "SMS",
                messages.Count,
                messages.Count == 0
                    ? "SMS yok (TV kutusu / telefon özelliği olmayabilir)"
                    : null);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SMS backup category failed");
            await SaveSmsAsync([], backupDir, cancellationToken).ConfigureAwait(false);
            return new BackupCategoryResult("SMS", 0, Truncate(ex.Message, 120));
        }
    }

    private async Task<BackupCategoryResult> BackupContactsCategoryAsync(
        string backupDir,
        CancellationToken cancellationToken)
    {
        try
        {
            var phoneContacts = await _contacts.GetContactsAsync(cancellationToken).ConfigureAwait(false);
            var contacts = phoneContacts.Select(MapContact).ToList();
            await SaveContactsAsync(contacts, backupDir, cancellationToken).ConfigureAwait(false);
            return new BackupCategoryResult(
                "Rehber",
                contacts.Count,
                contacts.Count == 0 ? "Rehber boş veya okunamadı" : null);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Contacts export via service failed; using fallback parser");
            try
            {
                var raw = await _adb.ExecuteShellAsync(
                        "content query --uri content://com.android.contacts/data/phones --projection contact_id,display_name,data1",
                        cancellationToken)
                    .ConfigureAwait(false);
                var contacts = BackupOutputParsers.ParseContacts(raw);
                await SaveContactsAsync(contacts, backupDir, cancellationToken).ConfigureAwait(false);
                return new BackupCategoryResult(
                    "Rehber",
                    contacts.Count,
                    contacts.Count == 0
                        ? Truncate(ex.Message, 120)
                        : null);
            }
            catch (Exception inner)
            {
                await SaveContactsAsync([], backupDir, cancellationToken).ConfigureAwait(false);
                return new BackupCategoryResult("Rehber", 0, Truncate(inner.Message, 120));
            }
        }
    }

    private async Task<int> BackupGalleryOrFindAsync(
        GalleryMediaKind kind,
        string saveDir,
        string subFolder,
        string[] extensions,
        BackupType type,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destDir = Path.Combine(saveDir, subFolder);
        Directory.CreateDirectory(destDir);

        try
        {
            var items = await _gallery.GetMediaAsync(kind, limit: 1000, cancellationToken)
                .ConfigureAwait(false);
            if (items.Count > 0)
            {
                await _gallery.SaveToLocalAsync(
                    items,
                    destDir,
                    new Progress<TransferProgress>(tp =>
                    {
                        Report(
                            progress,
                            $"{subFolder}: {tp.FileName}",
                            type,
                            items.Count == 0
                                ? 0
                                : (int)(tp.BytesTransferred * 100 / Math.Max(1, tp.TotalBytes)),
                            0,
                            items.Count);
                    }),
                    cancellationToken,
                    convertImagesToJpeg: kind == GalleryMediaKind.Image).ConfigureAwait(false);

                var saved = Directory.Exists(destDir)
                    ? Directory.GetFiles(destDir, "*", SearchOption.AllDirectories).Length
                    : 0;
                if (saved > 0)
                    return saved;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Gallery MediaStore backup failed for {Kind}; falling back to find", kind);
        }

        return await BackupMediaFindAsync(destDir, extensions, type, subFolder, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> BackupMediaFindAsync(
        string destDir,
        string[] extensions,
        BackupType type,
        string subFolder,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destDir);
        var roots = string.Join(' ', MediaSearchRoots.Select(r => $"\"{r}\""));
        var raw = await _adb.ExecuteShellAsync($"find {roots} -type f 2>/dev/null", cancellationToken)
            .ConfigureAwait(false);

        var files = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();

        var pulled = 0;
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remotePath = files[i];
            var fileName = Path.GetFileName(remotePath);
            var localPath = Path.Combine(destDir, fileName);
            if (File.Exists(localPath))
                localPath = Path.Combine(destDir, $"{i}_{fileName}");

            try
            {
                await _sync.PullAsync(remotePath, localPath, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                {
                    if (type == BackupType.Photos)
                        localPath = await _gallery.EnsureLocalJpegAsync(localPath, cancellationToken)
                            .ConfigureAwait(false);

                    if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                        pulled++;

                    fileName = Path.GetFileName(localPath);
                }

                Report(progress, $"{subFolder}: {fileName}", type,
                    (i + 1) * 100 / Math.Max(1, files.Count), i + 1, files.Count);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Media pull failed: {Path}", remotePath);
            }
        }

        // Keep empty folder visible after ZIP via tiny marker when nothing pulled.
        if (pulled == 0)
        {
            await File.WriteAllTextAsync(
                    Path.Combine(destDir, "_empty.txt"),
                    "Bu kategoride yedeklenecek dosya bulunamadı.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return pulled;
    }

    private async Task<int> BackupAppsAsync(
        string saveDir,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var apkDir = Path.Combine(saveDir, "Apps");
        Directory.CreateDirectory(apkDir);

        var packages = await ListUserPackagesAsync(cancellationToken).ConfigureAwait(false);
        var pulled = 0;
        for (var i = 0; i < packages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (apkPath, pkgName) = packages[i];
            var dest = Path.Combine(apkDir, $"{pkgName}.apk");

            try
            {
                await _sync.PullAsync(apkPath, dest, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                    pulled++;
                Report(progress, $"APK: {pkgName}", BackupType.Apps,
                    (i + 1) * 100 / Math.Max(1, packages.Count), i + 1, packages.Count);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "APK backup failed: {Pkg}", pkgName);
            }
        }

        return pulled;
    }

    private async Task<(int Count, string? Warning)> BackupAppDataAsync(
        string saveDir,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!await _elevated.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return (0, "Root gerekli — Magisk/su izni olmadan oyun/uygulama verisi alınamaz");
        }

        var dataDir = Path.Combine(saveDir, "AppData");
        Directory.CreateDirectory(dataDir);

        await _adb.ExecuteShellAsync($"mkdir -p \"{RemoteAppDataDir}\"", cancellationToken)
            .ConfigureAwait(false);

        var packages = await ListUserPackagesAsync(cancellationToken).ConfigureAwait(false);
        if (packages.Count == 0)
            return (0, "Kullanıcı uygulaması bulunamadı");

        var pulled = 0;
        var failures = 0;

        for (var i = 0; i < packages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pkgName = packages[i].Package;
            if (!IsSafePackageName(pkgName))
            {
                failures++;
                continue;
            }

            Report(progress, $"Veri: {pkgName}", BackupType.AppData,
                (i + 1) * 100 / Math.Max(1, packages.Count), i + 1, packages.Count);

            var remoteTar = $"{RemoteAppDataDir}/{pkgName}.tar.gz";
            var localTar = Path.Combine(dataDir, $"{pkgName}.tar.gz");

            try
            {
                var script = BuildAppDataBackupScript(pkgName, remoteTar);
                var pushed = await PushAndRunRootScriptAsync(script, cancellationToken).ConfigureAwait(false);
                var output = pushed;

                if (output.Contains("AM_APPDATA_EMPTY", StringComparison.Ordinal))
                {
                    _logger.Debug("App data empty: {Pkg}", pkgName);
                    continue;
                }

                var sizeCheck = await _adb.ExecuteShellAsync(
                        $"ls -l \"{remoteTar}\" 2>/dev/null | awk '{{print $5}}'",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!long.TryParse(sizeCheck.Trim(), out var remoteSize) || remoteSize < 32)
                {
                    failures++;
                    _logger.Warning("App data archive missing/small for {Pkg}: {Out}", pkgName, output.Trim());
                    continue;
                }

                await _sync.PullAsync(remoteTar, localTar, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (File.Exists(localTar) && new FileInfo(localTar).Length > 0)
                    pulled++;
                else
                    failures++;
            }
            catch (Exception ex)
            {
                failures++;
                _logger.Warning(ex, "App data backup failed: {Pkg}", pkgName);
            }
            finally
            {
                try
                {
                    await _adb.ExecuteShellAsync($"rm -f \"{remoteTar}\"", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // ignore cleanup
                }
            }
        }

        string? warning = null;
        if (pulled == 0)
            warning = failures > 0
                ? "Hiçbir uygulama verisi alınamadı (root izni veya boş veri)"
                : "Yedeklenecek uygulama verisi yok";
        else if (failures > 0)
            warning = $"{failures} uygulama verisi atlandı";

        return (pulled, warning);
    }

    private async Task<int> RestoreApksAsync(
        string workDir,
        RestoreOptions options,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var apkDir = Path.Combine(workDir, "Apps");
        if (!Directory.Exists(apkDir))
            return 0;

        var sets = EnumerateApkSets(apkDir)
            .Where(s => IncludePackage(s.Package, options))
            .ToList();
        var restored = 0;

        for (var i = 0; i < sets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (pkgName, apkPaths) = sets[i];

            if (!options.OverwriteApps && await IsPackageInstalledAsync(pkgName, cancellationToken).ConfigureAwait(false))
            {
                Report(progress, $"APK zaten kurulu: {pkgName}", BackupType.Apps,
                    50 + (i + 1) * 30 / Math.Max(1, sets.Count), i + 1, sets.Count);
                continue;
            }

            try
            {
                Report(progress, $"APK: {pkgName}", BackupType.Apps,
                    50 + (i + 1) * 30 / Math.Max(1, sets.Count), i + 1, sets.Count);
                var result = await _adb.InstallLocalPackagesAsync(apkPaths, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Success)
                    restored++;
                else
                    _logger.Warning("APK install failed for {Pkg}: {Msg}", pkgName, result.Message);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "APK restore failed: {Pkg}", pkgName);
            }
        }

        return restored;
    }

    private async Task<int> RestoreAppDataAsync(
        string workDir,
        RestoreOptions options,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var dataDir = Path.Combine(workDir, "AppData");
        if (!Directory.Exists(dataDir))
            return 0;

        if (!await _elevated.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.Warning("App data restore skipped — root unavailable");
            Report(progress, "Uygulama verisi için root gerekli", BackupType.AppData, 90);
            return 0;
        }

        await _adb.ExecuteShellAsync($"mkdir -p \"{RemoteAppDataDir}\"", cancellationToken)
            .ConfigureAwait(false);

        var archives = Directory.GetFiles(dataDir, "*.tar.gz")
            .Where(path =>
            {
                var pkgName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
                return IsSafePackageName(pkgName) && IncludePackage(pkgName, options);
            })
            .ToArray();
        var restored = 0;

        for (var i = 0; i < archives.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pkgName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(archives[i]));
            if (!IsSafePackageName(pkgName))
                continue;

            Report(progress, $"Veri geri yükle: {pkgName}", BackupType.AppData,
                85 + (i + 1) * 15 / Math.Max(1, archives.Length), i + 1, archives.Length);

            var remoteTar = $"{RemoteAppDataDir}/restore_{pkgName}.tar.gz";
            try
            {
                // Paket kurulu değilse veri dizinleri oluşmaz
                var installed = await _adb.ExecuteShellAsync(
                        $"pm path {pkgName} 2>/dev/null",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!installed.Contains("package:", StringComparison.Ordinal))
                {
                    _logger.Warning("Skip app data restore — not installed: {Pkg}", pkgName);
                    continue;
                }

                await _sync.PushAsync(archives[i], remoteTar, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var script = BuildAppDataRestoreScript(pkgName, remoteTar);
                var output = await PushAndRunRootScriptAsync(script, cancellationToken).ConfigureAwait(false);

                if (output.Contains("AM_APPDATA_OK", StringComparison.Ordinal))
                    restored++;
                else
                    _logger.Warning("App data restore uncertain for {Pkg}: {Out}", pkgName, output.Trim());
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "App data restore failed: {Pkg}", pkgName);
            }
            finally
            {
                try
                {
                    await _adb.ExecuteShellAsync($"rm -f \"{remoteTar}\"", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }
        }

        return restored;
    }

    private async Task<int> EnsureApksInstalledForAppDataAsync(
        string workDir,
        RestoreOptions options,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var dataDir = Path.Combine(workDir, "AppData");
        var apkDir = Path.Combine(workDir, "Apps");
        if (!Directory.Exists(dataDir) || !Directory.Exists(apkDir))
            return 0;

        var sets = EnumerateApkSets(apkDir)
            .Where(s => IncludePackage(s.Package, options))
            .ToDictionary(s => s.Package, s => s.ApkPaths, StringComparer.Ordinal);

        var installed = 0;
        foreach (var archive in Directory.GetFiles(dataDir, "*.tar.gz"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pkgName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(archive));
            if (!IsSafePackageName(pkgName) || !IncludePackage(pkgName, options))
                continue;

            if (!sets.TryGetValue(pkgName, out var apkPaths) || apkPaths.Count == 0)
                continue;

            if (await IsPackageInstalledAsync(pkgName, cancellationToken).ConfigureAwait(false))
                continue;

            try
            {
                Report(progress, $"APK kur (veri için): {pkgName}", BackupType.AppData, 70);
                var result = await _adb.InstallLocalPackagesAsync(apkPaths, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Success)
                    installed++;
                else
                    _logger.Warning("APK install for app-data restore failed: {Pkg}: {Msg}", pkgName, result.Message);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "APK install for app-data restore failed: {Pkg}", pkgName);
            }
        }

        return installed;
    }

    private static IReadOnlyList<(string Package, IReadOnlyList<string> ApkPaths)> EnumerateApkSets(string apkDir)
    {
        var byPackage = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(apkDir, "*.apk", SearchOption.TopDirectoryOnly))
        {
            var pkgName = Path.GetFileNameWithoutExtension(file);
            if (!IsSafePackageName(pkgName))
                continue;
            AddApkPath(byPackage, pkgName, file);
        }

        foreach (var dir in Directory.GetDirectories(apkDir))
        {
            var pkgName = Path.GetFileName(dir);
            if (!IsSafePackageName(pkgName))
                continue;

            foreach (var apk in Directory.GetFiles(dir, "*.apk", SearchOption.AllDirectories))
                AddApkPath(byPackage, pkgName, apk);
        }

        return byPackage
            .Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value))
            .ToList();
    }

    private static void AddApkPath(Dictionary<string, List<string>> map, string package, string path)
    {
        if (!map.TryGetValue(package, out var list))
        {
            list = [];
            map[package] = list;
        }

        if (!list.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            list.Add(path);
    }

    private async Task<bool> IsPackageInstalledAsync(string packageName, CancellationToken cancellationToken)
    {
        var pathOut = await _adb.ExecuteShellAsync($"pm path {packageName} 2>/dev/null", cancellationToken)
            .ConfigureAwait(false);
        return pathOut.Contains("package:", StringComparison.Ordinal);
    }

    private static bool IncludePackage(string packageName, RestoreOptions options)
    {
        if (options.PackageNames is null)
            return true;
        if (options.PackageNames.Count == 0)
            return false;

        foreach (var name in options.PackageNames)
        {
            if (string.Equals(name, packageName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private async Task<IReadOnlyList<(string ApkPath, string Package)>> ListUserPackagesAsync(
        CancellationToken cancellationToken)
    {
        var raw = await _adb.ExecuteShellAsync("pm list packages -3 -f", cancellationToken)
            .ConfigureAwait(false);
        var list = new List<(string, string)>();
        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("package:", StringComparison.Ordinal))
                continue;
            var match = PackageLineRegex().Match(line);
            if (!match.Success)
                continue;
            var apkPath = match.Groups[1].Value.Trim();
            var pkgName = match.Groups[2].Value.Trim();
            if (!IsSafePackageName(pkgName))
                continue;
            list.Add((apkPath, pkgName));
            if (list.Count >= MaxUserPackages)
                break;
        }

        return list;
    }

    private async Task<string> PushAndRunRootScriptAsync(string script, CancellationToken cancellationToken)
    {
        const string remoteScript = "/data/local/tmp/am_appdata_job.sh";
        var localScript = Path.Combine(Path.GetTempPath(), $"am_appdata_{Guid.NewGuid():N}.sh");
        try
        {
            // LF line endings for Android sh
            await File.WriteAllTextAsync(
                    localScript,
                    script.Replace("\r\n", "\n", StringComparison.Ordinal),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);

            await _sync.PushAsync(localScript, remoteScript, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await _adb.ExecuteShellAsync($"chmod 755 \"{remoteScript}\"", cancellationToken)
                .ConfigureAwait(false);

            return await _elevated.RunAsync(
                    $"sh \"{remoteScript}\"",
                    AppDataRootTimeout,
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
                // ignore
            }

            try
            {
                await _adb.ExecuteShellAsync($"rm -f \"{remoteScript}\"", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static string BuildAppDataBackupScript(string pkg, string remoteTar) =>
        $$"""
          PKG='{{pkg}}'
          OUT='{{remoteTar}}'
          STAGE="/data/local/tmp/am_ad_$$"
          rm -rf "$STAGE" "$OUT"
          mkdir -p "$STAGE/data" "$STAGE/android_data" "$STAGE/android_obb"
          am force-stop "$PKG" 2>/dev/null
          HAS=0
          if [ -d "/data/data/$PKG" ]; then cp -a "/data/data/$PKG/." "$STAGE/data/" 2>/dev/null && HAS=1; fi
          if [ -d "/sdcard/Android/data/$PKG" ]; then cp -a "/sdcard/Android/data/$PKG/." "$STAGE/android_data/" 2>/dev/null && HAS=1; fi
          if [ -d "/storage/emulated/0/Android/data/$PKG" ] && [ ! -d "/sdcard/Android/data/$PKG" ]; then cp -a "/storage/emulated/0/Android/data/$PKG/." "$STAGE/android_data/" 2>/dev/null && HAS=1; fi
          if [ -d "/sdcard/Android/obb/$PKG" ]; then cp -a "/sdcard/Android/obb/$PKG/." "$STAGE/android_obb/" 2>/dev/null && HAS=1; fi
          if [ -d "/storage/emulated/0/Android/obb/$PKG" ] && [ ! -d "/sdcard/Android/obb/$PKG" ]; then cp -a "/storage/emulated/0/Android/obb/$PKG/." "$STAGE/android_obb/" 2>/dev/null && HAS=1; fi
          if [ "$HAS" = "0" ]; then echo AM_APPDATA_EMPTY; rm -rf "$STAGE"; exit 0; fi
          tar -czf "$OUT" -C "$STAGE" . 2>/dev/null
          rm -rf "$STAGE"
          if [ -f "$OUT" ]; then echo AM_APPDATA_OK; else echo AM_APPDATA_FAIL; fi
          """;

    private static string BuildAppDataRestoreScript(string pkg, string remoteTar) =>
        $$"""
          PKG='{{pkg}}'
          TAR='{{remoteTar}}'
          STAGE="/data/local/tmp/am_adr_$$"
          rm -rf "$STAGE"
          mkdir -p "$STAGE"
          am force-stop "$PKG" 2>/dev/null
          tar -xzf "$TAR" -C "$STAGE" 2>/dev/null || { echo AM_APPDATA_FAIL; exit 0; }
          if [ -d "$STAGE/data" ]; then
            mkdir -p "/data/data/$PKG"
            OWN_UID=$(stat -c %u "/data/data/$PKG" 2>/dev/null || echo 0)
            OWN_GID=$(stat -c %g "/data/data/$PKG" 2>/dev/null || echo "$OWN_UID")
            find "/data/data/$PKG" -mindepth 1 -maxdepth 1 -exec rm -rf {} + 2>/dev/null
            cp -a "$STAGE/data/." "/data/data/$PKG/"
            chown -R "$OWN_UID:$OWN_GID" "/data/data/$PKG" 2>/dev/null
            restorecon -R "/data/data/$PKG" 2>/dev/null
          fi
          if [ -d "$STAGE/android_data" ] && [ "$(ls -A "$STAGE/android_data" 2>/dev/null)" ]; then
            mkdir -p "/sdcard/Android/data/$PKG"
            cp -a "$STAGE/android_data/." "/sdcard/Android/data/$PKG/"
          fi
          if [ -d "$STAGE/android_obb" ] && [ "$(ls -A "$STAGE/android_obb" 2>/dev/null)" ]; then
            mkdir -p "/sdcard/Android/obb/$PKG"
            cp -a "$STAGE/android_obb/." "/sdcard/Android/obb/$PKG/"
          fi
          rm -rf "$STAGE"
          echo AM_APPDATA_OK
          """;

    private static bool IsSafePackageName(string pkg) =>
        PackageNameRegex().IsMatch(pkg);

    private async Task<int> RestoreMediaFolderAsync(
        string localDir,
        string remoteDir,
        BackupType type,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(localDir))
            return 0;

        await _adb.ExecuteShellAsync($"mkdir -p \"{remoteDir}\"", cancellationToken).ConfigureAwait(false);
        var files = Directory.GetFiles(localDir)
            .Where(f => !string.Equals(Path.GetFileName(f), "_empty.txt", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var restored = 0;

        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dest = $"{remoteDir.TrimEnd('/')}/{Path.GetFileName(files[i])}";
            try
            {
                await _sync.PushAsync(files[i], dest, cancellationToken: cancellationToken).ConfigureAwait(false);
                restored++;
                Report(progress, Path.GetFileName(files[i]), type,
                    20 + (i + 1) * 60 / Math.Max(1, files.Length), i + 1, files.Length);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Restore push failed: {File}", files[i]);
            }
        }

        return restored;
    }

    private async Task<IReadOnlyList<MediaFile>> GetMediaFilesAsync(
        string[] exts,
        CancellationToken cancellationToken)
    {
        var roots = string.Join(' ', MediaSearchRoots.Select(r => $"\"{r}\""));
        var raw = await _adb.ExecuteShellAsync($"find {roots} -type f 2>/dev/null", cancellationToken)
            .ConfigureAwait(false);
        return raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => new MediaFile
            {
                RemotePath = f,
                Name = Path.GetFileName(f)
            })
            .ToList();
    }

    private static async Task SaveSmsAsync(IReadOnlyList<SmsMessage> messages, string dir, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(dir, "sms.json"), json, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private static async Task SaveContactsAsync(IReadOnlyList<Contact> contacts, string dir, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var c in contacts)
        {
            sb.AppendLine("BEGIN:VCARD");
            sb.AppendLine("VERSION:3.0");
            sb.AppendLine($"FN:{c.DisplayName}");
            sb.AppendLine($"TEL:{c.PhoneNumber}");
            if (!string.IsNullOrEmpty(c.Email))
                sb.AppendLine($"EMAIL:{c.Email}");
            sb.AppendLine("END:VCARD");
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "contacts.vcf"), sb.ToString(), Encoding.UTF8, ct)
            .ConfigureAwait(false);

        var json = JsonSerializer.Serialize(contacts, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(dir, "contacts.json"), json, Encoding.UTF8, ct)
            .ConfigureAwait(false);
    }

    private static async Task WriteManifestAsync(
        string backupDir,
        DeviceInfo device,
        BackupOptions options,
        IReadOnlyList<BackupCategoryResult> categories,
        CancellationToken cancellationToken)
    {
        var manifest = new
        {
            CreatedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
            Device = device.Model,
            Serial = device.Serial,
            Type = options.Type.ToString(),
            Categories = categories.Select(c => new
            {
                c.Name,
                c.Count,
                c.Warning
            }).ToList()
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
                Path.Combine(backupDir, "backup_manifest.json"),
                json,
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CreateZipWithCancellationAsync(
        string sourceDir,
        string zipPath,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            try
            {
                using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryName = Path.GetRelativePath(sourceDir, file);
                    archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(zipPath))
                        File.Delete(zipPath);
                }
                catch
                {
                    // ignore cleanup failure
                }

                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractZipWithCancellationAsync(
        string zipPath,
        string destinationDir,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(destinationDir);
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(Path.Combine(destinationDir, entry.FullName));
                    continue;
                }

                var dest = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
                if (!dest.StartsWith(Path.GetFullPath(destinationDir), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Zip entry path escapes destination.");

                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                if (!string.IsNullOrEmpty(entry.Name))
                    entry.ExtractToFile(dest, overwrite: true);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void Report(
        IProgress<BackupProgress>? progress,
        string step,
        BackupType type,
        int percentage,
        int done = 0,
        int total = 0) =>
        progress?.Report(new BackupProgress
        {
            CurrentStep = step,
            CurrentType = type,
            Percentage = Math.Clamp(percentage, 0, 100),
            ItemsDone = done,
            ItemsTotal = total
        });

    private static int CountFlags(BackupType type) =>
        Enum.GetValues<BackupType>()
            .Where(v => v is not BackupType.None and not BackupType.All)
            .Count(v => type.HasFlag(v));

    private static long GetDirectorySize(string dir) =>
        new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "device" : value;
    }

    private static string FormatSummary(IReadOnlyList<BackupCategoryResult> categories) =>
        string.Join(", ", categories.Select(c =>
            string.IsNullOrWhiteSpace(c.Warning)
                ? $"{c.Name}:{c.Count}"
                : $"{c.Name}:{c.Count}!"));

    private static string Truncate(string message, int max) =>
        string.IsNullOrWhiteSpace(message)
            ? "Hata"
            : message.Length <= max
                ? message
                : message[..(max - 1)] + "…";

    private static Contact MapContact(PhoneContact c) => new()
    {
        Id = c.ContactId,
        DisplayName = c.DisplayName,
        PhoneNumber = c.PhoneNumber,
        Email = c.Email
    };

    private static MediaFile MapMedia(GalleryMediaItem item) => new()
    {
        RemotePath = item.RemotePath,
        Name = string.IsNullOrWhiteSpace(item.DisplayName)
            ? Path.GetFileName(item.RemotePath)
            : item.DisplayName,
        Size = item.SizeBytes,
        MimeType = item.MimeType
    };

    [GeneratedRegex(@"package:(.+)=(.+)")]
    private static partial Regex PackageLineRegex();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z0-9_]+)+$")]
    private static partial Regex PackageNameRegex();

    private sealed record BackupCategoryResult(string Name, int Count, string? Warning);
}
