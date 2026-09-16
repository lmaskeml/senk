using System.Collections.ObjectModel;
using System.IO;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Security.ViewModels;

public sealed partial class RecoveryManagerViewModel : ObservableObject
{
    private readonly IRecoveryManagerService _recovery;
    private readonly IRootAnalysisService _analysis;
    private readonly IAppDialogService _dialogs;
    private readonly ILogger _logger;

    [ObservableProperty] private RecoveryStatus? _status;
    [ObservableProperty] private DeviceProfile? _profile;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Recovery durumu bekleniyor…";

    // Flash recovery image
    [ObservableProperty] private string? _recoveryImagePath;
    [ObservableProperty] private int _flashMethodIndex = 1;
    [ObservableProperty] private string _flashMethodHint = "";
    [ObservableProperty] private string _flashStatusMessage = "";
    [ObservableProperty] private bool _hasFlashStatusMessage;

    // Recovery zip install
    [ObservableProperty] private int _zipInstallMethodIndex = 0;
    [ObservableProperty] private string _zipInstallHint = "";
    [ObservableProperty] private string _zipInstallStatusMessage = "";
    [ObservableProperty] private bool _hasZipInstallStatusMessage;

    public ObservableCollection<string> SelectedZipPaths { get; } = new();

    public bool IsFastbootFlashRecoverySelected
    {
        get => FlashMethodIndex == 0;
        set { if (value) FlashMethodIndex = 0; }
    }

    public bool IsFastbootFlashBootSelected
    {
        get => FlashMethodIndex == 1;
        set { if (value) FlashMethodIndex = 1; }
    }

    public bool IsFastbootBootSelected
    {
        get => FlashMethodIndex == 2;
        set { if (value) FlashMethodIndex = 2; }
    }

    public bool IsRootDdSelected
    {
        get => FlashMethodIndex == 3;
        set { if (value) FlashMethodIndex = 3; }
    }

    public bool IsTwrpAdbInstallSelected
    {
        get => ZipInstallMethodIndex == 0;
        set { if (value) ZipInstallMethodIndex = 0; }
    }

    public bool IsRootOtaInstallSelected
    {
        get => ZipInstallMethodIndex == 1;
        set { if (value) ZipInstallMethodIndex = 1; }
    }

    public string RecoveryImageName => string.IsNullOrWhiteSpace(RecoveryImagePath)
        ? "Dosya seçilmedi"
        : Path.GetFileName(RecoveryImagePath);

    public bool CanFlash => !string.IsNullOrWhiteSpace(RecoveryImagePath) && File.Exists(RecoveryImagePath);

    private bool CanInstallZips
    {
        get
        {
            if (IsBusy)
                return false;

            if (SelectedZipPaths.Count == 0)
                return false;

            if (Status is null)
                return false;

            return ZipInstallMethodIndex switch
            {
                0 => Status.ConnectionMode == DeviceConnectionMode.Recovery
                     && Status.Type is RecoveryType.Twrp or RecoveryType.OrangeFox,
                _ => Status.IsRooted && SelectedZipPaths.Count == 1
            };
        }
    }

    partial void OnZipInstallMethodIndexChanged(int value) => UpdateZipInstallHint();

    public RecoveryManagerViewModel(
        IRecoveryManagerService recovery,
        IRootAnalysisService analysis,
        IAppDialogService dialogs,
        ILogger? logger = null)
    {
        _recovery = recovery;
        _analysis = analysis;
        _dialogs = dialogs;
        _logger = logger ?? Log.ForContext<RecoveryManagerViewModel>();
    }

    public Task InitializeAsync() => RefreshAsync();

    partial void OnRecoveryImagePathChanged(string? value)
    {
        OnPropertyChanged(nameof(RecoveryImageName));
        OnPropertyChanged(nameof(CanFlash));
        FlashRecoveryCommand.NotifyCanExecuteChanged();
    }

    partial void OnFlashMethodIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFastbootFlashRecoverySelected));
        OnPropertyChanged(nameof(IsFastbootFlashBootSelected));
        OnPropertyChanged(nameof(IsFastbootBootSelected));
        OnPropertyChanged(nameof(IsRootDdSelected));
        UpdateFlashMethodHint();
    }

    partial void OnStatusChanged(RecoveryStatus? value)
    {
        UpdateFlashMethodHint();
        UpdateZipInstallHint();
    }

    partial void OnFlashStatusMessageChanged(string value)
    {
        HasFlashStatusMessage = !string.IsNullOrWhiteSpace(value);
    }

    partial void OnZipInstallStatusMessageChanged(string value)
    {
        HasZipInstallStatusMessage = !string.IsNullOrWhiteSpace(value);
    }

    private void UpdateFlashMethodHint()
    {
        FlashMethodHint = FlashMethodIndex switch
        {
            0 => "Kalıcı flash: fastboot flash recovery. Küçük recovery imajları için.",
            1 => "Önerilen (Xiaomi / özel ROM): fastboot flash boot — recovery boyutu kısıtlı ROM'larda TWRP/OrangeFox boot_b'ye yazılır.",
            2 => "Geçici boot: İmaj bölüme yazılmaz, RAM'den bir kez açılır.",
            _ => "Root DD: Cihaz açıkken root ile recovery bölümüne yazılır."
        };

        var isRooted = Status?.IsRooted ?? false;
        var blUnlocked = Status?.BootloaderUnlocked ?? false;

        if (FlashMethodIndex == 3 && !isRooted)
            FlashMethodHint += " ⚠ Root yok — bu yöntem çalışmaz.";
        else if (FlashMethodIndex != 3 && !blUnlocked)
            FlashMethodHint += " ⚠ Bootloader kilitli olabilir.";
    }

    private void UpdateZipInstallHint()
    {
        ZipInstallHint = ZipInstallMethodIndex switch
        {
            0 => "Recovery içinde (TWRP/OrangeFox ADB) zip'i doğrudan kur.",
            _ => "Root ile canlı: zip'i kopyalayıp recovery command dosyasına yazar, ardından recovery'e yeniden başlatır."
        };

        if (Status is null)
            return;

        if (ZipInstallMethodIndex == 0)
        {
            if (Status.ConnectionMode != DeviceConnectionMode.Recovery)
                ZipInstallHint += " ⚠ Recovery modunda ADB gerekli.";
            else if (Status.Type is RecoveryType.Stock or RecoveryType.Unknown)
                ZipInstallHint += " ⚠ Stock/Unknown recovery'de komutlar çalışmayabilir.";
        }
        else
        {
            if (!Status.IsRooted)
                ZipInstallHint += " ⚠ Root erişimi yok (Magisk/KernelSU/APatch gerekir).";
            else if (SelectedZipPaths.Count != 1)
                ZipInstallHint += " ⚠ Root canlı yöntem tek seferde 1 zip kabul eder.";
        }
    }

    private static RecoveryFlashMethod MethodFromIndex(int index) => index switch
    {
        0 => RecoveryFlashMethod.Fastboot,
        1 => RecoveryFlashMethod.FastbootFlashBoot,
        2 => RecoveryFlashMethod.FastbootBootOnce,
        _ => RecoveryFlashMethod.RootDd
    };

    private static string MethodLabel(int index) => index switch
    {
        0 => "Fastboot Flash Recovery",
        1 => "Fastboot Flash Boot",
        2 => "Fastboot Boot (Geçici)",
        _ => "Root DD"
    };

    private static bool IsImageTooLargeError(string message) =>
        message.Contains("more than max allowed", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Geçici Boot", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Flash Boot", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Profile = await _analysis.AnalyzeDeviceAsync();
            Status = await _recovery.GetStatusAsync();
            StatusMessage = Status.Label;
            UpdateFlashMethodHint();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _logger.Warning(ex, "[Recovery] Status failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RebootRecoveryAsync()
    {
        if (!await _dialogs.ShowConfirmationAsync("Recovery", "Cihaz recovery moduna alınsın mı? ADB bağlantısı kesilir."))
            return;

        IsBusy = true;
        try
        {
            var result = await _recovery.RebootRecoveryAsync();
            StatusMessage = result.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BrowseRecoveryImageAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "Recovery İmajı Seç",
            "Recovery İmaj Dosyaları|*.img|Tüm Dosyalar|*.*",
            multiSelect: false);

        if (files is { Count: > 0 })
            RecoveryImagePath = files[0];
    }

    [RelayCommand(CanExecute = nameof(CanFlash))]
    private async Task FlashRecoveryAsync()
    {
        var method = MethodFromIndex(FlashMethodIndex);
        var methodLabel = MethodLabel(FlashMethodIndex);

        var actionText = method switch
        {
            RecoveryFlashMethod.FastbootBootOnce =>
                "Recovery imajı geçici olarak boot edilecek (bölüme yazılmaz).",
            RecoveryFlashMethod.FastbootFlashBoot =>
                "Recovery imajı boot bölümüne yazılacak (fastboot flash boot). " +
                "Xiaomi / A-B cihazlarda aktif slota (boot_a/boot_b) yüklenir.",
            RecoveryFlashMethod.RootDd =>
                "Root ile recovery bölümüne yazılacak.",
            _ =>
                "Recovery bölümüne kalıcı yazılacak (fastboot flash recovery)."
        };

        var confirmed = await _dialogs.ShowConfirmationAsync(
            method == RecoveryFlashMethod.FastbootBootOnce ? "Geçici Recovery Boot" : "Recovery Yükle",
            $"Recovery imajı {methodLabel} yöntemiyle işlenecek.\n\n" +
            $"Dosya: {RecoveryImageName}\n\n" +
            $"{actionText}\n\nDevam edilsin mi?");

        if (!confirmed) return;

        IsBusy = true;
        FlashStatusMessage = $"{methodLabel} çalışıyor…";
        try
        {
            var result = await _recovery.FlashRecoveryImageAsync(RecoveryImagePath!, method);
            FlashStatusMessage = result.Message;

            if (result.Success)
            {
                await _dialogs.ShowMessageAsync("Başarılı", result.Message);
                return;
            }

            if (method == RecoveryFlashMethod.Fastboot && IsImageTooLargeError(result.Message))
            {
                if (await _dialogs.ShowConfirmationAsync(
                        "İmaj çok büyük",
                        "Recovery bölümüne yazılamadı. «Fastboot Flash Boot» (fastboot flash boot) ile denemek ister misiniz?"))
                {
                    FlashMethodIndex = 1;
                    FlashStatusMessage = "Boot bölümüne yazılıyor…";
                    result = await _recovery.FlashRecoveryImageAsync(
                        RecoveryImagePath!, RecoveryFlashMethod.FastbootFlashBoot);
                    FlashStatusMessage = result.Message;
                    if (result.Success)
                    {
                        await _dialogs.ShowMessageAsync("Başarılı", result.Message);
                        return;
                    }
                }
                else if (await _dialogs.ShowConfirmationAsync(
                             "Alternatif",
                             "«Fastboot Boot (Geçici)» ile denemek ister misiniz?"))
                {
                    FlashMethodIndex = 2;
                    FlashStatusMessage = "Geçici boot deneniyor…";
                    result = await _recovery.FlashRecoveryImageAsync(
                        RecoveryImagePath!, RecoveryFlashMethod.FastbootBootOnce);
                    FlashStatusMessage = result.Message;
                    if (result.Success)
                    {
                        await _dialogs.ShowMessageAsync("Başarılı", result.Message);
                        return;
                    }
                }
            }

            await _dialogs.ShowMessageAsync("Hata", result.Message);
        }
        catch (Exception ex)
        {
            FlashStatusMessage = $"Hata: {ex.Message}";
            _logger.Error(ex, "[Recovery] Flash failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private RecoveryZipInstallMethod ZipMethodFromIndex(int index) => index switch
    {
        0 => RecoveryZipInstallMethod.TWRPAdbInstall,
        _ => RecoveryZipInstallMethod.RootOtaUpdatePackage
    };

    private string ZipMethodLabel(int index) => index switch
    {
        0 => "TWRP/OrangeFox ADB kurulum",
        _ => "Root canlı (OTA-command)"
    };

    [RelayCommand]
    private async Task PickZipFilesAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "Recovery ZIP seç",
            "ZIP paketleri (*.zip)|*.zip",
            multiSelect: true);

        if (files is null || files.Count == 0)
            return;

        SelectedZipPaths.Clear();
        foreach (var f in files)
            SelectedZipPaths.Add(f);

        UpdateZipInstallHint();
        InstallZipsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanInstallZips))]
    private async Task InstallZipsAsync()
    {
        if (SelectedZipPaths.Count == 0)
            return;

        var methodIndex = ZipInstallMethodIndex;
        var method = ZipMethodFromIndex(methodIndex);
        var methodLabel = ZipMethodLabel(methodIndex);

        var confirm = await _dialogs.ShowConfirmationAsync(
            "Recovery ZIP kurulumu",
            $"{methodLabel} ile kurulum yapılacak.\n\n" +
            $"Dosyalar: {string.Join(", ", SelectedZipPaths)}\n\n" +
            "Devam edilsin mi?");

        if (!confirm)
            return;

        IsBusy = true;
        ZipInstallStatusMessage = "Kurulum başlatılıyor…";
        try
        {
            var zipList = new string[SelectedZipPaths.Count];
            SelectedZipPaths.CopyTo(zipList, 0);
            var result = await _recovery.InstallRecoveryZipsAsync(
                zipList,
                method);

            ZipInstallStatusMessage = result.Message;
            if (result.Success)
            {
                await _dialogs.ShowMessageAsync("Başarılı", result.Message);
            }
            else
            {
                await _dialogs.ShowMessageAsync("Hata", result.Message);
            }
        }
        catch (Exception ex)
        {
            ZipInstallStatusMessage = $"Hata: {ex.Message}";
            _logger.Error(ex, "[Recovery] ZIP install UI failed");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
