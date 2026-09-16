using System.Collections.ObjectModel;
using System.IO;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Transfer.ViewModels;

public sealed partial class TransferWizardViewModel : ObservableObject
{
    private readonly IWhatsAppTransferService _transfer;
    private readonly IIosDeviceService _iosDevices;
    private readonly IAdbService _adb;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private int _wizardStep;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _includeMedia = true;
    [ObservableProperty] private bool _tryAdbBackup = true;
    [ObservableProperty] private bool _tryDowngrade = true;
    [ObservableProperty] private string _legacyApkPath = string.Empty;
    [ObservableProperty] private string _legacyApkHint = "tools/whatsapp/WhatsApp-legacy-backup.apk (APKMirror vb.)";
    [ObservableProperty] private bool _legacyApkMissing = true;
    [ObservableProperty] private bool _autoRestore = true;
    [ObservableProperty] private string _statusMessage = "Android cihazını USB ile bağlayın.";
    [ObservableProperty] private string _detailMessage = string.Empty;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private WhatsAppTransferPhase _currentPhase = WhatsAppTransferPhase.Idle;

    [ObservableProperty] private string _sessionDirectory = string.Empty;
    [ObservableProperty] private int _messageCount;
    [ObservableProperty] private int _chatCount;
    [ObservableProperty] private string _androidSummary = "Henüz veri çekilmedi.";

    [ObservableProperty] private ObservableCollection<IosDeviceInfo> _iosDevicesList = [];
    [ObservableProperty] private IosDeviceInfo? _selectedIosDevice;
    [ObservableProperty] private bool _iosToolsAvailable;
    [ObservableProperty] private string _iosToolsHint = string.Empty;

    [ObservableProperty] private ObservableCollection<WhatsAppTransferSession> _savedSessions = [];
    [ObservableProperty] private WhatsAppTransferSession? _selectedSession;

    private WhatsAppTransferSession? _activeSession;

    public TransferWizardViewModel(
        IWhatsAppTransferService transfer,
        IIosDeviceService iosDevices,
        IAdbService adb,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher)
    {
        _transfer = transfer;
        _iosDevices = iosDevices;
        _adb = adb;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        SessionDirectory = transfer.DefaultSessionRoot;
        RefreshLegacyApkStatus();
    }

    private void RefreshLegacyApkStatus()
    {
        LegacyApkPath = ResolveDefaultLegacyApkPath();
        LegacyApkMissing = string.IsNullOrWhiteSpace(LegacyApkPath) || !File.Exists(LegacyApkPath);
        LegacyApkHint = LegacyApkMissing
            ? "Legacy APK YOK — klasör simgesinden 2.21.x WhatsApp APK seçin (güncel WA ADB yedeğe DB koymaz)."
            : $"Legacy APK hazır: {LegacyApkPath}";
    }

    private static string ResolveDefaultLegacyApkPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "whatsapp", "WhatsApp-legacy-backup.apk"),
            Path.Combine(AppContext.BaseDirectory, "tools", "whatsapp", "WhatsApp-legacy-backup.apk"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    public async Task InitializeAsync()
    {
        RefreshIosToolsStatus();
        await LoadSavedSessionsAsync().ConfigureAwait(true);
        await RefreshIosDevicesAsync().ConfigureAwait(true);
    }

    private void RefreshIosToolsStatus()
    {
        IosToolsAvailable = _iosDevices.ToolsAvailable;
        var amds = _iosDevices.IsAppleMobileDeviceServiceAvailable;
        if (!IosToolsAvailable)
        {
            IosToolsHint =
                "iOS araçları pakette yok. Uygulamayı yeniden kurun.\n" + _iosDevices.ToolsSearchHint;
            return;
        }

        IosToolsHint = amds
            ? "iOS araçları ve Apple Mobile Device Support hazır. iPhone'u USB ile bağlayıp Yenile'ye basın."
            : "iOS araçları var ama Apple Mobile Device Support eksik.\n" +
              "Kurulum: winget install Apple.AppleMobileDeviceSupport\n" +
              "Sonra iPhone kablosunu çıkarıp takın ve 'Güven' deyin.";
    }

    [RelayCommand(CanExecute = nameof(CanRunAndroidExtract))]
    private async Task ExtractAndroidAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            await _dialogs.ShowMessageAsync(
                "Cihaz gerekli",
                "USB hata ayıklama açık bir Android cihaz seçin.");
            return;
        }

        await RunJobAsync(async ct =>
        {
            WizardStep = 0;
            var sessionDir = Path.Combine(
                _transfer.DefaultSessionRoot,
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));

            var result = await _transfer.ExtractFromAndroidAsync(
                    sessionDir,
                    IncludeMedia,
                    TryAdbBackup,
                    TryDowngrade,
                    string.IsNullOrWhiteSpace(LegacyApkPath) ? null : LegacyApkPath,
                    CreateProgressReporter(),
                    ct)
                .ConfigureAwait(true);

            _activeSession = await _transfer.LoadSessionAsync(sessionDir, ct).ConfigureAwait(true);
            SessionDirectory = sessionDir;
            MessageCount = result.MessageCount;
            ChatCount = result.ChatCount;
            AndroidSummary =
                $"PC'ye kaydedildi: {result.MessageCount} mesaj, {result.ChatCount} sohbet" +
                (result.UsedRootCopy ? " (root)" :
                    result.UsedDowngrade ? " (legacy downgrade + ADB)" :
                    result.UsedAdbBackup ? " (ADB yedek)" : "");

            if (result.Warnings.Count > 0)
                DetailMessage = string.Join("\n", result.Warnings);

            var conversion = await _transfer.ConvertToIosDatabaseAsync(_activeSession!, CreateProgressReporter(), ct)
                .ConfigureAwait(true);
            _activeSession = await _transfer.LoadSessionAsync(sessionDir, ct).ConfigureAwait(true);

            StatusMessage = $"Android verisi hazır — {conversion.ConvertedMessages} mesaj dönüştürüldü.";
            WizardStep = 1;
            await LoadSavedSessionsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshIos))]
    private async Task RefreshIosDevicesAsync()
    {
        RefreshIosToolsStatus();
        if (!IosToolsAvailable)
        {
            StatusMessage = "iOS araçları yok — " + _iosDevices.ToolsSearchHint;
            IosDevicesList = [];
            SelectedIosDevice = null;
            return;
        }

        try
        {
            var devices = await _iosDevices.GetConnectedDevicesAsync().ConfigureAwait(true);
            IosDevicesList = new ObservableCollection<IosDeviceInfo>(devices);
            SelectedIosDevice = devices.FirstOrDefault();
            StatusMessage = devices.Count > 0
                ? $"{devices.Count} iPhone bulundu."
                : "iPhone bulunamadı — USB kablosu, 'Bu bilgisayara güven' ve Apple Mobile Device Support kontrol edin.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "iOS device list failed");
            StatusMessage = ex.Message;
            IosDevicesList = [];
            SelectedIosDevice = null;
            DetailMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunIosInject))]
    private async Task InjectToIphoneAsync()
    {
        if (SelectedIosDevice is null)
        {
            await _dialogs.ShowMessageAsync("iPhone gerekli", "Aktarım için bir iPhone seçin.");
            return;
        }

        var session = _activeSession ?? SelectedSession;
        if (session is null || string.IsNullOrWhiteSpace(session.MsgStorePath))
        {
            await _dialogs.ShowMessageAsync(
                "Veri yok",
                "Önce Android adımını tamamlayın veya kayıtlı bir oturum seçin.");
            return;
        }

        var confirm = await _dialogs.ShowConfirmationAsync(
            "iPhone'a aktar",
            "Bu işlem iPhone'da WhatsApp verisini değiştirebilir.\n" +
            "Devam etmeden önce mevcut WhatsApp yedeğinizi alın.\n\nDevam edilsin mi?");
        if (!confirm)
            return;

        await RunJobAsync(async ct =>
        {
            WizardStep = 2;
            var result = await _transfer.InjectToIphoneAsync(
                    session,
                    SelectedIosDevice!.Udid,
                    AutoRestore,
                    CreateProgressReporter(),
                    ct)
                .ConfigureAwait(true);

            StatusMessage = result.RestoreTriggered
                ? "Aktarım tamamlandı — iPhone'da WhatsApp'ı açın."
                : "Yedek hazırlandı — otomatik geri yükleme başarısız olabilir.";
            DetailMessage = result.Warnings.Count > 0
                ? string.Join("\n", result.Warnings)
                : $"Yedek: {result.BackupDirectory}";

            CurrentPhase = result.RestoreTriggered
                ? WhatsAppTransferPhase.Completed
                : WhatsAppTransferPhase.InjectingIos;
            ProgressPercent = 100;
            WizardStep = 3;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task BrowseLegacyApkAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "Legacy WhatsApp APK",
            "APK dosyaları|*.apk",
            multiSelect: false).ConfigureAwait(true);
        if (files is not { Count: > 0 })
            return;

        LegacyApkPath = files[0];
        LegacyApkMissing = !File.Exists(LegacyApkPath);
        LegacyApkHint = LegacyApkMissing
            ? "Seçilen APK bulunamadı."
            : $"Legacy APK hazır: {LegacyApkPath}";
    }

    [RelayCommand]
    private async Task LoadSavedSessionsAsync()
    {
        var sessions = await _transfer.ListSavedSessionsAsync().ConfigureAwait(true);
        SavedSessions = new ObservableCollection<WhatsAppTransferSession>(sessions);
    }

    [RelayCommand]
    private async Task UseSelectedSessionAsync()
    {
        if (SelectedSession is null)
            return;

        _activeSession = SelectedSession;
        SessionDirectory = SelectedSession.SessionDirectory;
        MessageCount = SelectedSession.MessageCount;
        ChatCount = SelectedSession.ChatCount;
        AndroidSummary = $"Kayıtlı oturum — {SelectedSession.MessageCount} mesaj";
        WizardStep = string.IsNullOrWhiteSpace(SelectedSession.ChatStoragePath) ? 0 : 1;
        StatusMessage = "Kayıtlı oturum yüklendi — iPhone adımına geçebilirsiniz.";
        await Task.CompletedTask.ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenSessionFolder()
    {
        if (string.IsNullOrWhiteSpace(SessionDirectory) || !Directory.Exists(SessionDirectory))
            return;
        ProcessHelper.OpenFolder(SessionDirectory);
    }

    [RelayCommand]
    private void GoToAndroidStep() => WizardStep = 0;

    [RelayCommand]
    private void GoToIphoneStep()
    {
        WizardStep = 1;
        RefreshIosToolsStatus();
    }

    private bool CanRunAndroidExtract() => !IsBusy;
    private bool CanRefreshIos() => !IsBusy;
    private bool CanRunIosInject() => !IsBusy && SelectedIosDevice is not null;

    partial void OnIsBusyChanged(bool value)
    {
        ExtractAndroidCommand.NotifyCanExecuteChanged();
        RefreshIosDevicesCommand.NotifyCanExecuteChanged();
        InjectToIphoneCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedIosDeviceChanged(IosDeviceInfo? value) =>
        InjectToIphoneCommand.NotifyCanExecuteChanged();

    private async Task RunJobAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        DetailMessage = string.Empty;

        try
        {
            await action(_cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "İşlem iptal edildi.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WhatsApp transfer failed");
            CurrentPhase = WhatsAppTransferPhase.Failed;
            StatusMessage = $"Hata: {ex.Message}";
            await _dialogs.ShowMessageAsync("WhatsApp Aktarım", ex.Message).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IProgress<WhatsAppTransferProgress> CreateProgressReporter() =>
        new Progress<WhatsAppTransferProgress>(p =>
            _dispatcher.Observe(() =>
            {
                CurrentPhase = p.Phase;
                ProgressPercent = p.Percent;
                StatusMessage = p.Message;
                if (!string.IsNullOrWhiteSpace(p.Detail))
                    DetailMessage = p.Detail;
            }));

    [RelayCommand(CanExecute = nameof(CanRunAndroidExtract))]
    private void CancelJob()
    {
        _cts?.Cancel();
    }
}

internal static class ProcessHelper
{
    public static void OpenFolder(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}
