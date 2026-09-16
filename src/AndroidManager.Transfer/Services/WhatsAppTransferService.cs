using System.Text.Json;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Transfer.Services;

public sealed class WhatsAppTransferService : IWhatsAppTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly AndroidWhatsAppExtractor _androidExtractor;
    private readonly WhatsAppSchemaConverter _converter;
    private readonly IosBackupManipulator _iosManipulator;
    private readonly IIosDeviceService _iosDevices;
    private readonly IDeviceInfoService _deviceInfo;
    private readonly ILogger _logger;

    public WhatsAppTransferService(
        AndroidWhatsAppExtractor androidExtractor,
        WhatsAppSchemaConverter converter,
        IosBackupManipulator iosManipulator,
        IIosDeviceService iosDevices,
        IDeviceInfoService deviceInfo,
        ILogger? logger = null)
    {
        _androidExtractor = androidExtractor;
        _converter = converter;
        _iosManipulator = iosManipulator;
        _iosDevices = iosDevices;
        _deviceInfo = deviceInfo;
        _logger = logger ?? Log.ForContext<WhatsAppTransferService>();
    }

    public string DefaultSessionRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AndroidManager",
            "WhatsAppTransfer");

    public async Task<WhatsAppAndroidExtractResult> ExtractFromAndroidAsync(
        string? sessionDirectory = null,
        bool includeMedia = true,
        bool tryAdbBackup = true,
        bool tryDowngradeOnBackupFailure = true,
        string? legacyApkPath = null,
        IProgress<WhatsAppTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        sessionDirectory ??= CreateNewSessionDirectory();
        Directory.CreateDirectory(sessionDirectory);

        var device = await _deviceInfo.GetDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
        var result = await _androidExtractor.ExtractAsync(
                sessionDirectory,
                includeMedia,
                tryAdbBackup,
                tryDowngradeOnBackupFailure,
                legacyApkPath,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        var session = new WhatsAppTransferSession
        {
            SessionDirectory = sessionDirectory,
            AndroidDeviceModel = device.Model,
            AndroidDeviceSerial = device.Serial,
            MsgStorePath = result.MsgStorePath,
            MediaDirectory = result.MediaDirectory,
            MessageCount = result.MessageCount,
            ChatCount = result.ChatCount,
            Phase = WhatsAppTransferPhase.AndroidReady
        };
        await SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);

        progress?.Report(new WhatsAppTransferProgress
        {
            Phase = WhatsAppTransferPhase.AndroidReady,
            Percent = 100,
            Message = $"Android verisi hazır — {result.MessageCount} mesaj, {result.ChatCount} sohbet"
        });

        return result;
    }

    public async Task<WhatsAppConversionResult> ConvertToIosDatabaseAsync(
        WhatsAppTransferSession session,
        IProgress<WhatsAppTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.MsgStorePath) || !File.Exists(session.MsgStorePath))
            throw new InvalidOperationException("Önce Android veritabanını PC'ye çekin.");

        var iosDir = Path.Combine(session.SessionDirectory, "ios");
        var result = await _converter.ConvertAsync(
                session.MsgStorePath,
                iosDir,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        var updated = session with
        {
            ChatStoragePath = result.ChatStoragePath,
            Phase = WhatsAppTransferPhase.WaitingForIphone
        };
        await SaveSessionAsync(updated, cancellationToken).ConfigureAwait(false);

        progress?.Report(new WhatsAppTransferProgress
        {
            Phase = WhatsAppTransferPhase.WaitingForIphone,
            Percent = 100,
            Message = "iOS veritabanı hazır — iPhone'u bağlayabilirsiniz"
        });

        return result;
    }

    public async Task<WhatsAppIosInjectResult> InjectToIphoneAsync(
        WhatsAppTransferSession session,
        string iosDeviceUdid,
        bool triggerRestore = true,
        IProgress<WhatsAppTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.ChatStoragePath) || !File.Exists(session.ChatStoragePath))
        {
            var conversion = await ConvertToIosDatabaseAsync(session, progress, cancellationToken)
                .ConfigureAwait(false);
            session = session with { ChatStoragePath = conversion.ChatStoragePath };
        }

        var devices = await _iosDevices.GetConnectedDevicesAsync(cancellationToken).ConfigureAwait(false);
        var device = devices.FirstOrDefault(d =>
            string.Equals(d.Udid, iosDeviceUdid, StringComparison.OrdinalIgnoreCase));
        if (device is null)
            throw new InvalidOperationException("Seçilen iPhone bulunamadı. USB bağlantısını kontrol edin.");

        var backupRoot = Path.Combine(session.SessionDirectory, "ios_backup");
        var backupDir = await _iosDevices.CreateUnencryptedBackupAsync(
                iosDeviceUdid,
                backupRoot,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        var (mediaCopied, injectWarnings) = await _iosManipulator.InjectWhatsAppDataAsync(
                backupDir,
                session.ChatStoragePath!,
                session.MediaDirectory,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        var restoreTriggered = false;
        var warnings = injectWarnings.ToList();

        if (triggerRestore)
        {
            try
            {
                await _iosDevices.RestoreBackupAsync(iosDeviceUdid, backupDir, progress, cancellationToken)
                    .ConfigureAwait(false);
                restoreTriggered = true;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "iOS restore failed");
                warnings.Add($"Otomatik geri yükleme başarısız: {ex.Message}. Yedek klasörünü manuel restore edebilirsiniz.");
            }
        }

        var updated = session with
        {
            IosDeviceUdid = iosDeviceUdid,
            IosDeviceName = device.DeviceName,
            Phase = restoreTriggered ? WhatsAppTransferPhase.Completed : WhatsAppTransferPhase.InjectingIos
        };
        await SaveSessionAsync(updated, cancellationToken).ConfigureAwait(false);

        progress?.Report(new WhatsAppTransferProgress
        {
            Phase = updated.Phase,
            Percent = 100,
            Message = restoreTriggered
                ? "WhatsApp verisi iPhone'a aktarıldı"
                : "Yedek hazır — geri yükleme manuel gerekebilir"
        });

        return new WhatsAppIosInjectResult
        {
            BackupDirectory = backupDir,
            MediaFilesCopied = mediaCopied,
            RestoreTriggered = restoreTriggered,
            Warnings = warnings
        };
    }

    public async Task<IReadOnlyList<WhatsAppTransferSession>> ListSavedSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DefaultSessionRoot))
            return [];

        var sessions = new List<WhatsAppTransferSession>();
        foreach (var dir in Directory.GetDirectories(DefaultSessionRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await LoadSessionAsync(dir, cancellationToken).ConfigureAwait(false);
            if (session is not null)
                sessions.Add(session);
        }

        return sessions.OrderByDescending(s => s.CreatedUtc).ToList();
    }

    public async Task<WhatsAppTransferSession?> LoadSessionAsync(
        string sessionDirectory,
        CancellationToken cancellationToken = default)
    {
        var manifest = Path.Combine(sessionDirectory, "session.json");
        if (!File.Exists(manifest))
            return null;

        await using var stream = File.OpenRead(manifest);
        return await JsonSerializer.DeserializeAsync<WhatsAppTransferSession>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveSessionAsync(
        WhatsAppTransferSession session,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(session.SessionDirectory);
        var manifest = Path.Combine(session.SessionDirectory, "session.json");
        await using var stream = File.Create(manifest);
        await JsonSerializer.SerializeAsync(stream, session, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private string CreateNewSessionDirectory()
    {
        Directory.CreateDirectory(DefaultSessionRoot);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(DefaultSessionRoot, stamp);
    }
}
