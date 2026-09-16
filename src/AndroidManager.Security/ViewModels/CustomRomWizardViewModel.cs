using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Security.ViewModels;

public sealed partial class CustomRomWizardViewModel : ObservableObject
{
    private readonly ICustomRomWizardService _wizard;
    private readonly IRecoveryManagerService _recovery;
    private readonly IRootAnalysisService _analysis;
    private readonly IAdbService _adb;
    private readonly IFastbootDiscoveryService _fastboot;
    private readonly IDeviceToolsService _deviceTools;
    private readonly IAppDialogService _dialogs;
    private readonly ILogger _logger;
    private CancellationTokenSource? _installCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartInstallCommand))]
    [NotifyPropertyChangedFor(nameof(IsPackageStep))]
    [NotifyPropertyChangedFor(nameof(IsPrepareStep))]
    [NotifyPropertyChangedFor(nameof(IsFlashStep))]
    private CustomRomWizardStep _currentStep = CustomRomWizardStep.Package;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartInstallCommand))]
    [NotifyPropertyChangedFor(nameof(IsCompatible))]
    [NotifyPropertyChangedFor(nameof(IsUnverifiable))]
    [NotifyPropertyChangedFor(nameof(IsIncompatible))]
    [NotifyPropertyChangedFor(nameof(HasPackage))]
    [NotifyPropertyChangedFor(nameof(HasPayloadRom))]
    [NotifyPropertyChangedFor(nameof(ShowSafeInstallCard))]
    [NotifyPropertyChangedFor(nameof(VbmetaSourceDisplay))]
    [NotifyPropertyChangedFor(nameof(DeviceCodename))]
    [NotifyPropertyChangedFor(nameof(DeviceDisplayName))]
    private CustomRomPackageInfo? _package;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartInstallCommand))]
    private CustomRomCompatibilityResult? _compatibility;

    [ObservableProperty] private DeviceProfile? _profile;
    [ObservableProperty] private RecoveryStatus? _recoveryStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartInstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickRomCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadPackageCommand))]
    private bool _isBusy;

    /// <summary>Cihaz/fastboot taraması — ZIP seçimini engellemez.</summary>
    [ObservableProperty] private bool _isRefreshingDevice;

    [ObservableProperty] private bool _wipeCache = true;
    [ObservableProperty] private bool _wipeDalvik = true;
    [ObservableProperty] private bool _formatData = true;
    [ObservableProperty] private bool _flashDfeZip;
    [ObservableProperty] private string? _dfeZipPath;
    [ObservableProperty] private bool _patchFstabDisableForceEncrypt = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VbmetaSourceDisplay))]
    private bool _disableAvbVerity = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VbmetaSourceDisplay))]
    private string? _stockVbmetaPath;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTwrpFlashSection))]
    [NotifyPropertyChangedFor(nameof(ShowAdvancedPrepareOptions))]
    [NotifyPropertyChangedFor(nameof(ShowSafeInstallCard))]
    [NotifyPropertyChangedFor(nameof(FlashStepTitle))]
    [NotifyPropertyChangedFor(nameof(SuccessBannerText))]
    [NotifyPropertyChangedFor(nameof(StartInstallButtonText))]
    [NotifyCanExecuteChangedFor(nameof(StartInstallCommand))]
    private bool _useSafeFastbootInstall;
    /// <summary>0=zaten TWRP, 1=geçici fastboot boot (önerilen), 2=kalıcı flash (gelişmiş)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPermanentTwrpFlashOptions))]
    [NotifyPropertyChangedFor(nameof(TwrpLaunchHint))]
    private int _twrpLaunchModeIndex = 1;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartInstallCommand))]
    private string? _twrpImagePath;
    /// <summary>0=recovery, 1=boot — yalnızca kalıcı flash modunda</summary>
    [ObservableProperty] private int _twrpFlashMethodIndex = 1;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private bool _installSucceeded;
    [ObservableProperty] private string _statusMessage = "Custom ROM zip dosyasını bırakın veya seçin.";
    [ObservableProperty] private string _activityLog = "";
    [ObservableProperty] private bool _isFastbootConnected;
    [ObservableProperty] private string? _fastbootSerial;
    [ObservableProperty] private string? _fastbootProduct;

    public string DeviceDisplayName =>
        Profile?.DisplayName
        ?? (IsFastbootConnected && !string.IsNullOrWhiteSpace(FastbootProduct)
            ? $"{FastbootProduct} (fastboot)"
            : IsFastbootConnected && !string.IsNullOrWhiteSpace(FastbootSerial)
                ? $"Fastboot · {FastbootSerial}"
                : null)
        ?? "Cihaz bağlı değil";

    public string DeviceCodename
    {
        get
        {
            var fromProfile = Profile?.Codename?.Trim();
            if (!string.IsNullOrWhiteSpace(fromProfile)
                && !CustomRomPackageInspector.IsGenericHardwareToken(fromProfile))
                return fromProfile;

            if (Package is not null)
            {
                var resolved = CustomRomPackageInspector.ResolveReportedCodename(FastbootProduct, Package);
                if (!string.IsNullOrWhiteSpace(resolved)
                    && !CustomRomPackageInspector.IsGenericHardwareToken(resolved))
                    return resolved;
            }

            return FastbootProduct ?? "—";
        }
    }

    public string ConnectionModeLabel =>
        IsFastbootConnected && _adb.SelectedDevice is null
            ? "Fastboot"
            : RecoveryStatus?.ConnectionMode.ToString() ?? "—";

    public bool IsPackageStep => CurrentStep == CustomRomWizardStep.Package;
    public bool IsPrepareStep => CurrentStep == CustomRomWizardStep.Prepare;
    public bool IsFlashStep => CurrentStep == CustomRomWizardStep.Flash;
    public bool HasPackage => Package is not null;
    public bool HasPayloadRom => Package?.HasPayloadBin == true;
    public bool IsTwrpRecommended =>
        RecoveryStatus?.Type is RecoveryType.Stock or RecoveryType.Unknown or null;
    public bool ShowTwrpFlashSection => !UseSafeFastbootInstall;
    public bool ShowAdvancedPrepareOptions => !UseSafeFastbootInstall;
    public bool ShowSafeInstallCard => HasPayloadRom && UseSafeFastbootInstall;
    public string FlashStepTitle => UseSafeFastbootInstall ? "GÜVENLİ YÜKLEME" : "SIDELOAD";
    public string StartInstallButtonText => UseSafeFastbootInstall
        ? "Güvenli yüklemeyi başlat"
        : "Yüklemeyi başlat";
    public string SuccessBannerText => UseSafeFastbootInstall
        ? "Güvenli yükleme tamam. TWRP yazılmadı; ROM dosyalarına (fstab/DFE/Magisk) dokunulmadı. İlk açılış birkaç dakika sürebilir."
        : "Kurulum tamamlandı. TWRP'den «Reboot System» yapın. İlk açılış birkaç dakika sürebilir; /data FBE ile yeniden şifrelenir.";
    public bool ShowPermanentTwrpFlashOptions => TwrpLaunchModeIndex == 2;
    public string TwrpLaunchHint => TwrpLaunchModeIndex switch
    {
        0 => "ADB recovery/TWRP — fastboot veya imaj gerekmez",
        1 => "Önerilen: fastboot boot (RAM) ile kurulum. ROM ve boot yazıldıktan sonra seçilen TWRP kalıcı yüklenir",
        2 => "Gelişmiş: kurulum başında kalıcı flash — ROM sonrası TWRP yine yazılır",
        _ => ""
    };
    public string ResolvedDfeZipPath =>
        DfeZipResolver.ResolveDfeZip(Package?.DetectedAndroidMajor, DfeZipPath) ?? "";

    public string EncryptionPrepHint =>
        PatchFstabDisableForceEncrypt
            ? "Android 15 EROFS: fstab PC'de düzenlenir, ROM sideload, sonra fastbootd vendor flash (adb push yazamaz)."
            : DfeZipResolver.DescribeStrategy(Package?.DetectedAndroidMajor, ResolvedDfeZipPath);

    public string DfeZipDisplay =>
        string.IsNullOrWhiteSpace(ResolvedDfeZipPath)
            ? "DFE zip seçilmedi / tools/dfe/ boş"
            : Path.GetFileName(ResolvedDfeZipPath);

    public string VbmetaSourceDisplay
    {
        get
        {
            if (!DisableAvbVerity)
                return "Kapalı — AVB açık kalır, custom ROM / TWRP açılmayabilir";
            if (!string.IsNullOrWhiteSpace(StockVbmetaPath))
                return StockVbmetaPath;
            return HasPayloadRom
                ? "ROM payload (vbmeta.img) otomatik çıkarılacak"
                : "Stok images klasörü seçin; yoksa AVB-disabled stub";
        }
    }

    public string TwrpFlashMethodHint => TwrpFlashMethodIndex switch
    {
        0 => "Kalıcı: fastboot flash recovery",
        1 => "Kalıcı: fastboot flash boot (Xiaomi A/B boot_a/boot_b)",
        _ => ""
    };
    public bool IsCompatible => Compatibility?.Compatibility == CustomRomCompatibility.Compatible;
    public bool IsUnverifiable => Compatibility?.Compatibility == CustomRomCompatibility.Unverifiable;
    public bool IsIncompatible => Compatibility?.Compatibility == CustomRomCompatibility.Incompatible;

    public CustomRomWizardViewModel(
        ICustomRomWizardService wizard,
        IRecoveryManagerService recovery,
        IRootAnalysisService analysis,
        IAdbService adb,
        IFastbootDiscoveryService fastboot,
        IDeviceToolsService deviceTools,
        IAppDialogService dialogs,
        ILogger? logger = null)
    {
        _wizard = wizard;
        _recovery = recovery;
        _analysis = analysis;
        _adb = adb;
        _fastboot = fastboot;
        _deviceTools = deviceTools;
        _dialogs = dialogs;
        _logger = logger ?? Log.ForContext<CustomRomWizardViewModel>();
    }

    public Task InitializeAsync()
    {
        StatusMessage = "ROM zip seçin. payload.bin varsa önerilen: güvenli fastboot (TWRP yok).";
        ObservedTask.Run(RefreshDeviceAsync());
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshDeviceAsync()
    {
        IsRefreshingDevice = true;
        try
        {
            IsFastbootConnected = false;
            FastbootSerial = null;
            FastbootProduct = null;

            if (_adb.SelectedDevice is null)
                ObservedTask.Run(_adb.WaitForAdbDeviceAsync(TimeSpan.FromSeconds(2)));

            var fastbootDevices = await _fastboot.GetDevicesAsync();
            if (fastbootDevices.Count > 0)
            {
                var fb = fastbootDevices[0];
                IsFastbootConnected = true;
                FastbootSerial = fb.Serial;
                FastbootProduct = string.IsNullOrWhiteSpace(fb.Product) ? fb.Model : fb.Product;
            }

            if (_adb.SelectedDevice is null)
            {
                Profile = null;
                RecoveryStatus = await _recovery.GetStatusAsync();
                OnPropertyChanged(nameof(DeviceDisplayName));
                OnPropertyChanged(nameof(DeviceCodename));
                OnPropertyChanged(nameof(ConnectionModeLabel));

                if (IsFastbootConnected)
                {
                    if (Package is null)
                        StatusMessage =
                            $"Fastboot bağlı ({FastbootSerial}). ROM zip seçin — kurulumda geçici TWRP (fastboot boot) önerilir.";
                    else
                        ReevaluateCompatibility();
                    if (TwrpLaunchModeIndex != 0)
                        TwrpLaunchModeIndex = 1;
                    return;
                }

                if (Package is null)
                {
                    StatusMessage = string.IsNullOrWhiteSpace(_fastboot.ResolvedExecutablePath)
                        ? "ROM zip seçin. fastboot.exe yolu Ayarlar'da tanımlı olmalı (bat ile aynı klasör)."
                        : "ROM zip seçin. Telefon fastboot ekranındayken USB takın, ardından Cihazı yenile.";
                }
                else
                    ReevaluateCompatibility();
                return;
            }

            try
            {
                Profile = await _analysis.AnalyzeDeviceAsync();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[CustomROM] Profile skipped in recovery");
                Profile = null;
            }

            RecoveryStatus = await _recovery.GetStatusAsync();
            if (IsDeviceInRecovery() && TwrpLaunchModeIndex == 1)
                TwrpLaunchModeIndex = 0;
            var name = Profile is not null
                ? $"{Profile.DisplayName} ({Profile.Codename})"
                : _adb.SelectedDevice.Serial;
            StatusMessage = $"{name} · {RecoveryStatus.Label} · ADB {_adb.SelectedDevice.State}";
            OnPropertyChanged(nameof(DeviceDisplayName));
            OnPropertyChanged(nameof(DeviceCodename));
            OnPropertyChanged(nameof(ConnectionModeLabel));
            ReevaluateCompatibility();
        }
        catch (Exception ex)
        {
            Profile = null;
            RecoveryStatus = null;
            if (Package is null)
                StatusMessage = $"Cihaz okunamadı: {ex.Message}";
            _logger.Warning(ex, "[CustomROM] Device refresh failed");
        }
        finally
        {
            IsRefreshingDevice = false;
            PickRomCommand.NotifyCanExecuteChanged();
            LoadPackageCommand.NotifyCanExecuteChanged();
        }
    }

    private void ReevaluateCompatibility()
    {
        if (Package is null)
            return;

        Compatibility = _wizard.EvaluateCompatibility(Package, ResolveCodenameForCompatibility());
        StatusMessage = Compatibility.Reason;
        if (Package.HasPayloadBin)
            AppendLog("Payload ROM — önerilen: güvenli custom ROM yükleme (yalnızca fastboot, TWRP yok).");
    }

    private string ResolveCodenameForCompatibility()
    {
        var fromProfile = Profile?.Codename?.Trim();
        if (!string.IsNullOrWhiteSpace(fromProfile)
            && !CustomRomPackageInspector.IsGenericHardwareToken(fromProfile))
            return fromProfile;

        var fromFastboot = FastbootProduct?.Trim() ?? "";
        if (Package is not null)
            return CustomRomPackageInspector.ResolveReportedCodename(fromFastboot, Package);

        return fromFastboot;
    }

    [RelayCommand(CanExecute = nameof(CanPickOrLoad))]
    private async Task PickRomAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "Custom ROM seç",
            "ROM paketleri (*.zip)|*.zip",
            multiSelect: false);
        if (files is null || files.Count == 0)
            return;

        await LoadPackageAsync(files[0]);
    }

    [RelayCommand(CanExecute = nameof(CanPickOrLoad))]
    private async Task LoadPackageAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        IsBusy = true;
        try
        {
            Package = await _wizard.InspectPackageAsync(path);
            ReevaluateCompatibility();
            AppendLog($"Paket: {Package.FileName} ({Package.SizeFormatted})");
            AppendLog(Package.Summary);
            foreach (var warning in Package.Warnings)
                AppendLog("Uyarı: " + warning);

            if (Package.DetectedAndroidMajor is int major)
            {
                AppendLog($"Hedef Android sürümü (tahmin): {major}");
                AppendLog(EncryptionPrepHint);
                if (major >= 15)
                    FlashDfeZip = false;
            }

            var bundledDfe = DfeZipResolver.ResolveDfeZip(Package.DetectedAndroidMajor, null);
            if (bundledDfe is not null)
                AppendLog($"DFE hazır: {Path.GetFileName(bundledDfe)}");

            if (Package.HasPayloadBin)
                UseSafeFastbootInstall = true;
            else
                UseSafeFastbootInstall = false;

            CurrentStep = CustomRomWizardStep.Package;
        }
        catch (Exception ex)
        {
            Package = null;
            Compatibility = null;
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Custom ROM", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task GoNextAsync()
    {
        if (CurrentStep != CustomRomWizardStep.Package || Package is null || Compatibility is null)
            return;

        if (Compatibility.Compatibility == CustomRomCompatibility.Incompatible)
        {
            await _dialogs.ShowMessageAsync("Uyumsuz ROM", Compatibility.Reason);
            return;
        }

        if (Compatibility.Compatibility == CustomRomCompatibility.Unverifiable)
        {
            var ok = await _dialogs.ShowConfirmationAsync(
                "Doğrulanamayan paket",
                Compatibility.Reason + "\n\nYine de devam edilsin mi? Yanlış ROM cihazı kullanılamaz hale getirebilir.");
            if (!ok)
                return;
        }

        CurrentStep = CustomRomWizardStep.Prepare;
        FormatData = true;
        if (HasPayloadRom)
            UseSafeFastbootInstall = true;
        if (IsDeviceInRecovery())
            TwrpLaunchModeIndex = 0;
        else if (IsFastbootConnected)
            TwrpLaunchModeIndex = 1;

        StatusMessage = UseSafeFastbootInstall
            ? "Güvenli yükleme: yalnızca fastboot. TWRP yazılmaz, ROM dosyalarına dokunulmaz."
            : TwrpLaunchModeIndex == 0
            ? "Recovery ADB bağlı — wipe/format seçeneklerini ayarlayıp kurulumu başlatın."
            : string.IsNullOrWhiteSpace(TwrpImagePath)
                ? "Önce üstteki «TWRP .img seç» ile imaj seçin, sonra kurulumu başlatın."
                : "Önerilen: geçici TWRP → Format Data → sideload → boot → TWRP kalıcı yazılır.";
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (CurrentStep == CustomRomWizardStep.Prepare)
        {
            CurrentStep = CustomRomWizardStep.Package;
            StatusMessage = Compatibility?.Reason ?? "Paketi kontrol edin.";
        }
        else if (CurrentStep == CustomRomWizardStep.Flash && !IsBusy)
        {
            CurrentStep = CustomRomWizardStep.Prepare;
            InstallSucceeded = false;
            ProgressPercent = 0;
        }
    }

    [RelayCommand]
    private async Task PickTwrpImageAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "TWRP / OrangeFox imajı",
            "Recovery imajları (*.img)|*.img",
            multiSelect: false);
        if (files is null || files.Count == 0)
            return;

        TwrpImagePath = files[0];
        if (!IsDeviceInRecovery())
            TwrpLaunchModeIndex = 1;
        var imageSize = new FileInfo(TwrpImagePath).Length;
        if (TwrpLaunchModeIndex == 2)
        {
            TwrpFlashMethodIndex = RecoveryImageFlashPlanner.SuggestWizardFlashMethodIndex(
                imageSize, DeviceCodename is "—" ? null : DeviceCodename);
        }
        AppendLog($"TWRP: {Path.GetFileName(TwrpImagePath)} ({imageSize / 1024 / 1024} MB)");
        if (imageSize > 196608L * 1024)
        {
            AppendLog("Uyarı: boot bölüm kapasitesinden büyük TWRP — geçici fastboot boot deneyin.");
        }
    }

    [RelayCommand]
    private async Task PickDfeZipAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "DFE zip seç (Disable Force Encrypt)",
            "Zip (*.zip)|*.zip",
            multiSelect: false);
        if (files is null || files.Count == 0)
            return;

        DfeZipPath = files[0];
        FlashDfeZip = true;
        AppendLog($"DFE: {Path.GetFileName(DfeZipPath)}");
    }

    [RelayCommand]
    private async Task PickStockVbmetaAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Stok ROM klasörü (images\\vbmeta.img)");
        if (string.IsNullOrWhiteSpace(folder))
            return;

        StockVbmetaPath = folder;
        DisableAvbVerity = true;
        AppendLog($"vbmeta kaynağı: {StockVbmetaPath}");
    }

    [RelayCommand]
    private void ClearStockVbmeta()
    {
        StockVbmetaPath = null;
        AppendLog("vbmeta kaynağı: ROM payload / stub");
    }

    [RelayCommand(CanExecute = nameof(CanStartInstall))]
    private async Task StartInstallAsync()
    {
        if (Package is null)
            return;

        await RefreshDeviceAsync();

        if (!UseSafeFastbootInstall)
        {
            if (TwrpLaunchModeIndex is 1 or 2
                && (string.IsNullOrWhiteSpace(TwrpImagePath) || !File.Exists(TwrpImagePath)))
            {
                // Prepare adımında seçici yukarıda; kullanıcı aşağıda "Kurulumu başlat"a basınca
                // sadece Tamam diyen diyalog çıkıyordu — doğrudan dosya seçici aç.
                StatusMessage = "TWRP .img seçin (geçici/kalıcı kurulum için gerekli)…";
                await PickTwrpImageAsync().ConfigureAwait(true);

                if (string.IsNullOrWhiteSpace(TwrpImagePath) || !File.Exists(TwrpImagePath))
                {
                    await _dialogs.ShowMessageAsync(
                        "TWRP imajı gerekli",
                        "Kuruluma devam etmek için TWRP .img seçmelisiniz.\n\n" +
                        "• Üstteki «TWRP .img seç» ile dosya seçin, veya\n" +
                        "• Zaten TWRP'deyseniz listeden «Zaten TWRP / recovery'deyim» seçin.");
                    return;
                }
            }

            if (TwrpLaunchModeIndex == 0 && !IsDeviceInRecovery())
            {
                var reboot = await _dialogs.ShowConfirmationAsync(
                    "Recovery gerekli",
                    "«Zaten TWRP'deyim» seçildi ama cihaz recovery ADB'de görünmüyor.\n\n" +
                    "• Fastboot'taysanız «Geçici TWRP başlat» seçin\n" +
                    "• TWRP'deyseniz USB + Yenile\n\nRecovery'ye yeniden başlatılsın mı?");
                if (!reboot)
                    return;

                var result = await _recovery.RebootRecoveryAsync();
                StatusMessage = result.Message + " TWRP açılınca ADB bekleniyor…";
                var found = await _adb.WaitForAdbDeviceAsync(TimeSpan.FromSeconds(60));
                if (found is null || !IsDeviceInRecovery())
                {
                    await RefreshDeviceAsync();
                    await _dialogs.ShowMessageAsync(
                        "TWRP görünmüyor",
                        "Telefon TWRP'de olsa da ADB gelmedi.\n\n• TWRP ana menüde kalın\n• USB kablo/port değiştirin\n• Yenile → tekrar deneyin");
                    return;
                }

                await RefreshDeviceAsync();
            }

            if (TwrpLaunchModeIndex == 1 && !IsFastbootConnected && !IsDeviceInRecovery())
            {
                await _dialogs.ShowMessageAsync(
                    "Fastboot veya TWRP gerekli",
                    "Geçici TWRP modu: telefon fastboot'ta olabilir veya TWRP'de takılıysanız kurulumu başlatın — otomatik fastboot'a geçer.\n\nZaten hazırsanız «Zaten TWRP'deyim» de seçilebilir.");
                return;
            }
        }
        else if (_adb.SelectedDevice is null && !IsFastbootConnected)
        {
            var ok = await _dialogs.ShowConfirmationAsync(
                "Fastboot modu",
                "Ne ADB ne fastboot cihazı görünüyor.\n\nTelefonu fastboot/bootloader ekranında USB ile bağlayın. Bat dosyanızın kullandığı fastboot.exe yolunu Ayarlar'a girin.\n\nYine de denemek istiyor musunuz?");
            if (!ok)
                return;
        }
        else if (_adb.SelectedDevice is null && IsFastbootConnected)
        {
            AppendLog($"Fastboot hazır: {FastbootSerial}");
        }

        if (!UseSafeFastbootInstall && FormatData)
        {
            var wipeOk = await _dialogs.ShowConfirmationAsync(
                "Verileriniz silinecek",
                "Manager önce fastboot'tan userdata/metadata siler (Format Data), ardından TWRP'yi açar.\n\n" +
                "Tüm kişisel veri silinir. Daha önce formatladıysanız Hayır deyip yalnızca ROM yükleyin.");
            if (!wipeOk)
                FormatData = false;
        }

        if (UseSafeFastbootInstall && FormatData)
        {
            var wipeOk = await _dialogs.ShowConfirmationAsync(
                "Verileriniz silinecek",
                "Güvenli yükleme TWRP yazmaz. Format Data userdata'yı fastboot'tan siler.\n\n" +
                "Tüm kişisel veri silinir. Daha önce sildiyseniz Hayır deyin.");
            if (!wipeOk)
                FormatData = false;
        }

        var twrpLine = !UseSafeFastbootInstall
            ? $"\nTWRP: {TwrpLaunchHint}" +
              (string.IsNullOrWhiteSpace(TwrpImagePath) ? "" : $" ({Path.GetFileName(TwrpImagePath)})")
            : "";

        var confirm = await _dialogs.ShowConfirmationAsync(
            UseSafeFastbootInstall ? "Güvenli custom ROM yükleme" : "Custom ROM yükle (TWRP)",
            UseSafeFastbootInstall
                ? $"{Package.FileName} → yalnızca fastboot/fastbootd.\n" +
                  "TWRP yüklenmez. ROM zip'ine fstab / DFE / Magisk eklenmez.\n" +
                  "AVB: vbmeta --disable-verity (imza; ROM dosyası değişmez).\n" +
                  $"Format Data: {(FormatData ? "Evet" : "Hayır")}\n" +
                  $"Cihaz: {DeviceCodename}\nBootloader açık olmalı.\n\nDevam?"
                : $"{Package.FileName} → TWRP sideload.{twrpLine}\n" +
                  $"AVB/imza: {(DisableAvbVerity ? "kapatılacak (vbmeta --disable-verity)" : "açık bırakılacak")}\n" +
                  $"Format Data: {(FormatData ? "Evet (otomatik)" : "Hayır")}\n" +
                  $"Fstab patch: {(PatchFstabDisableForceEncrypt ? "Evet (forceencrypt→encryptable)" : "Hayır")}\n" +
                  $"DFE zip: {(FlashDfeZip ? DfeZipDisplay : "Hayır")}\n" +
                  $"Cihaz: {DeviceCodename}\n\nROM zip'i siz seçtiniz; uyumluluk sorumluluğu size aittir.\n\nDevam?");
        if (!confirm)
            return;

        _installCts?.Cancel();
        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        CurrentStep = CustomRomWizardStep.Flash;
        IsBusy = true;
        InstallSucceeded = false;
        ProgressPercent = 0;
        StatusMessage = "Yükleme başlıyor…";

        var codename = DeviceCodename;
        if (codename == "—")
            codename = null;

        var options = new CustomRomInstallOptions
        {
            WipeCache = WipeCache,
            WipeDalvik = WipeDalvik,
            FormatData = FormatData,
            FlashDfeZip = FlashDfeZip && !UseSafeFastbootInstall,
            DfeZipPath = DfeZipPath,
            TargetAndroidMajor = Package?.DetectedAndroidMajor,
            PatchFstabDisableForceEncrypt = PatchFstabDisableForceEncrypt && !UseSafeFastbootInstall,
            SafeUnmodifiedInstall = UseSafeFastbootInstall,
            UseFastbootPayloadEngine = UseSafeFastbootInstall,
            DeviceCodename = codename,
            TwrpLaunch = MapTwrpLaunch(TwrpLaunchModeIndex),
            TwrpImagePath = UseSafeFastbootInstall ? null : TwrpImagePath,
            PermanentTwrpFlashMethod = MapPermanentTwrpFlashMethod(TwrpFlashMethodIndex),
            DisableAvbVerity = UseSafeFastbootInstall || DisableAvbVerity,
            StockVbmetaImagePath = StockVbmetaPath
        };

        var progress = new Progress<CustomRomInstallProgress>(p =>
        {
            ProgressPercent = p.Percent;
            StatusMessage = p.Message;
            AppendLog(p.Message);
        });

        try
        {
            var packagePath = Package!.FilePath;
            var result = await _wizard.InstallAsync(packagePath, options, progress, _installCts.Token);
            InstallSucceeded = result.Success;
            StatusMessage = result.Message;
            if (options.FormatData)
                FormatData = false;
            AppendLog(result.Success ? "Tamamlandı." : "Hata: " + result.Message);
            if (!result.Success)
                await _dialogs.ShowMessageAsync("Custom ROM", result.Message);
        }
        catch (Exception ex)
        {
            InstallSucceeded = false;
            StatusMessage = ex.Message;
            _logger.Error(ex, "[CustomROM] Install UI failed");
            await _dialogs.ShowMessageAsync("Custom ROM", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RebootFastbootAsync()
    {
        if (IsFastbootConnected)
        {
            StatusMessage = $"Cihaz zaten fastboot'ta ({FastbootSerial}).";
            return;
        }

        if (!await _dialogs.ShowConfirmationAsync("Fastboot", "Cihaz bootloader/fastboot moduna alınsın mı?"))
            return;

        var result = await _deviceTools.RebootAsync(DeviceRebootMode.Bootloader);
        StatusMessage = result.Message;
        if (result.Success)
            await RefreshDeviceAsync();
    }

    [RelayCommand]
    private void ResetWizard()
    {
        _installCts?.Cancel();
        Package = null;
        Compatibility = null;
        CurrentStep = CustomRomWizardStep.Package;
        ProgressPercent = 0;
        InstallSucceeded = false;
        FormatData = true;
        UseSafeFastbootInstall = false;
        TwrpLaunchModeIndex = 1;
        TwrpImagePath = null;
        TwrpFlashMethodIndex = 1;
        WipeCache = true;
        WipeDalvik = true;
        DisableAvbVerity = true;
        StockVbmetaPath = null;
        StatusMessage = "Custom ROM zip dosyasını bırakın veya seçin.";
        ActivityLog = "";
    }

    private bool CanPickOrLoad() => !IsBusy;

    private bool CanGoNext() =>
        !IsBusy
        && CurrentStep == CustomRomWizardStep.Package
        && Package is not null
        && (Compatibility is null
            || Compatibility.Compatibility != CustomRomCompatibility.Incompatible);

    private bool CanGoBack() =>
        !IsBusy && CurrentStep is CustomRomWizardStep.Prepare or CustomRomWizardStep.Flash;

    private bool CanStartInstall() =>
        !IsBusy
        && CurrentStep == CustomRomWizardStep.Prepare
        && Package is not null
        && Compatibility is { CanInstall: true }
        && (!UseSafeFastbootInstall || Package.HasPayloadBin);

    private static TwrpLaunchStrategy MapTwrpLaunch(int index) => index switch
    {
        0 => TwrpLaunchStrategy.AlreadyInRecovery,
        2 => TwrpLaunchStrategy.FlashPermanent,
        _ => TwrpLaunchStrategy.BootOnceFromFastboot
    };

    private static RecoveryFlashMethod MapPermanentTwrpFlashMethod(int index) => index switch
    {
        0 => RecoveryFlashMethod.Fastboot,
        _ => RecoveryFlashMethod.FastbootFlashBoot
    };

    private bool IsDeviceInRecovery()
    {
        var device = _adb.SelectedDevice;
        if (device is { IsRecovery: true } || device is { IsSideload: true })
            return true;
        return RecoveryStatus?.ConnectionMode == DeviceConnectionMode.Recovery;
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var next = $"[{stamp}] {line.Trim()}";
        ActivityLog = string.IsNullOrEmpty(ActivityLog)
            ? next
            : ActivityLog + Environment.NewLine + next;

        if (ActivityLog.Length > 12_000)
            ActivityLog = ActivityLog[^8_000..];
    }
}
