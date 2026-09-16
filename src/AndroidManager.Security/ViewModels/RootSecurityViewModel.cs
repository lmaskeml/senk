using System.Collections.ObjectModel;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Security.ViewModels;

public sealed partial class RootSecurityViewModel : ObservableObject, IDisposable
{
    private readonly IRootSecurityService _root;
    private readonly IRootAnalysisService _analysis;
    private readonly INotificationService _notification;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAdbService _adb;
    private readonly IMagiskModuleService _magisk;
    private readonly IMagiskDenyListService _denyList;
    private readonly IBankRootHideService _bankHide;
    private readonly ILogger _logger;
    private CancellationTokenSource? _scanCts;
    private bool _disposed;

    [ObservableProperty] private RootStatus? _rootStatus;
    [ObservableProperty] private MountStatus? _mountStatus;
    [ObservableProperty] private BootSecurityInfo? _bootInfo;
    [ObservableProperty] private ObservableCollection<ThreatViewModel> _systemThreats = [];
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ScanProgress _scanProgress = new();
    [ObservableProperty] private string _statusMessage = "Root modu henüz kontrol edilmedi";
    [ObservableProperty] private bool _scanRecentFiles = true;
    [ObservableProperty] private bool _scanInitScripts = true;
    [ObservableProperty] private bool _scanMagiskModules = true;
    [ObservableProperty] private bool _scanHosts = true;
    [ObservableProperty] private bool _scanAccessibility = true;
    [ObservableProperty] private string _targetPackage = "";
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private DeviceProfile? _deviceProfile;
    [ObservableProperty] private RootMethodRecommendation? _rootRecommendation;
    [ObservableProperty] private RootPreflightResult? _preflightResult;
    [ObservableProperty] private ObservableCollection<MagiskModuleItem> _magiskModules = [];
    [ObservableProperty] private string _magiskStatus = "Moduller henuz listelenmedi";
    [ObservableProperty] private string _magiskEnvironmentNote = "";
    [ObservableProperty] private bool _magiskManagerHidden;
    [ObservableProperty] private bool _magiskBusy;
    [ObservableProperty] private ObservableCollection<string> _denyListEntries = [];
    [ObservableProperty] private string _denyListStatus = "DenyList henuz okunmadi";
    [ObservableProperty] private bool _denyListEnforce;
    [ObservableProperty] private ObservableCollection<BankHideCheckItemViewModel> _bankHideChecks = [];
    [ObservableProperty] private string _bankHideSummary = "YKB banka gizleme denetimi henuz calistirilmadi";
    [ObservableProperty] private string _bankHideGuide = BankRootHideService.HmaTemplateHint;

    public RootSecurityViewModel(
        IRootSecurityService root,
        IRootAnalysisService analysis,
        INotificationService notification,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher,
        IAdbService adb,
        IMagiskModuleService magisk,
        IMagiskDenyListService denyList,
        IBankRootHideService bankHide)
    {
        _root = root;
        _analysis = analysis;
        _notification = notification;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _adb = adb;
        _magisk = magisk;
        _denyList = denyList;
        _bankHide = bankHide;
        _logger = Log.ForContext<RootSecurityViewModel>();
        _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
    }

    public Task InitializeAsync() => CheckRootAsync();

    [RelayCommand]
    private async Task AnalyzeRootWizardAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Önce bir cihaz bağlayın";
            return;
        }

        IsBusy = true;
        try
        {
            DeviceProfile = await _analysis.AnalyzeDeviceAsync();
            RootRecommendation = await _analysis.RecommendMethodAsync(DeviceProfile);
            PreflightResult = await _analysis.RunPreflightAsync(
                DeviceProfile, RootRecommendation.Method);
            StatusMessage = RootRecommendation.Summary;
            SelectedTab = 5;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _logger.Error(ex, "[Root] Analysis failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckRootAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            RootStatus = null;
            StatusMessage = "Önce bir cihaz bağlayın";
            return;
        }

        IsBusy = true;
        StatusMessage = "Root durumu kontrol ediliyor…";
        try
        {
            RootStatus = await _root.GetRootStatusAsync();
            MountStatus = await _root.GetMountStatusAsync();
            BootInfo = await _root.GetBootSecurityAsync();

            StatusMessage = RootStatus.IsRooted
                ? "Root erişimi doğrulanmış — derin tarama hazır"
                : RootStatus.Access == RootAccess.None
                    ? "Root bulunamadı — derin sistem taraması çalışmaz"
                    : "su bulundu — «Root'u Etkinleştir» ile izin testi yapın";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Kontrol hatası: {ex.Message}";
            _logger.Error(ex, "Root kontrol hatası");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnableRootAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Önce bir cihaz bağlayın";
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _root.EnableRootAsync(new Progress<string>(s => StatusMessage = s));
            RootStatus = await _root.GetRootStatusAsync();
            MountStatus = await _root.GetMountStatusAsync();
            BootInfo = await _root.GetBootSecurityAsync();

            if (ok)
                _notification.ShowInfo("Root Modu Aktif",
                    "Derin sistem taraması ve root temizleme kullanılabilir.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Root hatası: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanSystemAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Önce bir cihaz bağlayın";
            return;
        }

        if (RootStatus?.IsRooted != true)
        {
            StatusMessage = "Önce root erişimini etkinleştirin";
            return;
        }

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        SystemThreats.Clear();
        StatusMessage = "Derin sistem taraması başlatılıyor…";

        try
        {
            var options = new SystemScanOptions
            {
                ScanRecentFiles = ScanRecentFiles,
                ScanInitScripts = ScanInitScripts,
                ScanMagiskModules = ScanMagiskModules,
                ScanHosts = ScanHosts,
                ScanAccessibility = ScanAccessibility,
                ScanDataLocalTmp = true
            };

            var result = await _root.ScanSystemDeepAsync(
                options,
                new Progress<ScanProgress>(p => _dispatcher.Observe(() => ScanProgress = p)),
                _scanCts.Token);

            SystemThreats = new ObservableCollection<ThreatViewModel>(
                result.Threats.Select(t => new ThreatViewModel(t)));
            BootInfo = result.Boot;
            MountStatus = result.Mounts;
            StatusMessage = result.Summary;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Tarama iptal edildi";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Tarama hatası: {ex.Message}";
            _logger.Error(ex, "Derin sistem tarama hatası");
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var t in SystemThreats)
            t.IsSelected = true;
    }

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        var toClean = SystemThreats.Where(t => t.IsSelected).Select(t => t.Model).ToList();
        if (toClean.Count == 0)
        {
            StatusMessage = "Temizlenecek öğe seçin";
            return;
        }

        if (!await _dialogs.ShowConfirmationAsync(
                "Root Temizleme",
                $"{toClean.Count} sistem öğesi temizlenecek.\n\n" +
                "• Sistem uygulamaları: disable + APK karantina\n" +
                "• Sistem dosyaları: karantina dizinine taşınır (silinmez)\n\n" +
                "Cihaz kararlılığını etkileyebilir. Devam edilsin mi?"))
            return;

        IsBusy = true;
        try
        {
            var results = new List<CleanResult>();
            foreach (var threat in toClean)
            {
                var r = await _root.CleanThreatAsync(threat);
                results.Add(r);
                StatusMessage = r.Message;
            }

            var okCount = results.Count(r => r.Success);
            var fail = results.Count(r => !r.Success);
            var cleaned = results.Where(r => r.Success).Select(r => r.Threat.Id).ToHashSet();

            foreach (var t in SystemThreats.Where(t => cleaned.Contains(t.Model.Id)).ToList())
                SystemThreats.Remove(t);

            StatusMessage = $"{okCount} temizlendi" + (fail > 0 ? $", {fail} başarısız" : "");
            _notification.ShowInfo("Root Temizleme", $"{okCount} sistem öğesi karantinaya alındı");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Temizleme hatası: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanSingleAsync(ThreatViewModel? item)
    {
        if (item is null) return;
        if (!await _dialogs.ShowConfirmationAsync(
                "Root Temizleme",
                $"«{item.Name}» temizlenecek.\nSistem öğeleri karantinaya taşınır. Devam?"))
            return;

        IsBusy = true;
        try
        {
            var r = await _root.CleanThreatAsync(item.Model);
            StatusMessage = r.Message;
            if (r.Success)
                SystemThreats.Remove(item);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearDataAsync(ThreatViewModel? item)
    {
        if (item is null || string.IsNullOrEmpty(item.PackageName)) return;
        IsBusy = true;
        try
        {
            var r = await _root.ClearAppDataAsync(item.PackageName);
            StatusMessage = r.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task KillTargetAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetPackage)) return;
        IsBusy = true;
        try
        {
            var ok = await _root.KillProcessAsync(TargetPackage.Trim());
            StatusMessage = ok
                ? $"{TargetPackage} işlemleri sonlandırıldı"
                : $"{TargetPackage} için çalışan işlem bulunamadı";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearTargetDataAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetPackage)) return;
        IsBusy = true;
        try
        {
            var r = await _root.ClearAppDataAsync(TargetPackage.Trim());
            StatusMessage = r.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MountRwAsync()
    {
        IsBusy = true;
        try
        {
            var ok = await _root.SetSystemMountAsync(true);
            StatusMessage = ok
                ? "Sistem bölümü yazılabilir (rw) yapıldı"
                : "rw mount başarısız — dm-verity aktif olabilir";
            MountStatus = await _root.GetMountStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MountRoAsync()
    {
        IsBusy = true;
        try
        {
            var ok = await _root.SetSystemMountAsync(false);
            StatusMessage = ok
                ? "Sistem bölümü salt-okunur (ro) yapıldı"
                : "ro mount doğrulanamadı";
            MountStatus = await _root.GetMountStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreHostsAsync()
    {
        if (!await _dialogs.ShowConfirmationAsync(
                "Hosts Onarımı",
                "hosts dosyası varsayılana döndürülecek.\nMevcut hosts önce yedeklenir.\n\nDevam?"))
            return;

        IsBusy = true;
        try
        {
            var r = await _root.RestoreHostsAsync();
            StatusMessage = r.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RebootAsync()
    {
        if (!await _dialogs.ShowConfirmationAsync("Yeniden Başlat", "Cihaz yeniden başlatılacak.\n\nDevam?"))
            return;

        await _root.RebootDeviceAsync();
        StatusMessage = "Cihaz yeniden başlatılıyor…";
    }

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 3)
        {
            _dispatcher.Observe(async () =>
            {
                await RefreshMagiskModulesAsync().ConfigureAwait(true);
                await RefreshDenyListAsync().ConfigureAwait(true);
            });
        }
        else if (value == 4)
        {
            _dispatcher.Observe(async () => await RunBankHideAuditAsync().ConfigureAwait(true));
        }
    }

    [RelayCommand]
    private async Task RunBankHideAuditAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            BankHideSummary = "Once bir cihaz baglayin";
            return;
        }

        MagiskBusy = true;
        try
        {
            var audit = await _bankHide.AuditAsync().ConfigureAwait(true);
            BankHideChecks = new ObservableCollection<BankHideCheckItemViewModel>(
                audit.Checks.Select(c => new BankHideCheckItemViewModel(c)));
            BankHideSummary = audit.Summary;
            StatusMessage = audit.Summary;
        }
        catch (Exception ex)
        {
            BankHideSummary = ex.Message;
            _logger.Warning(ex, "Bank hide audit failed");
        }
        finally
        {
            MagiskBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyBankHideDenyListAsync()
    {
        MagiskBusy = true;
        IsBusy = true;
        try
        {
            var r = await _bankHide.ApplyDenyListLayerAsync().ConfigureAwait(true);
            StatusMessage = r.Message;
            await RefreshDenyListAsync().ConfigureAwait(true);
            await RunBankHideAuditAsync().ConfigureAwait(true);
            await _dialogs.ShowMessageAsync(r.Success ? "DenyList" : "Hata", r.Message);
        }
        finally
        {
            MagiskBusy = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallBankStackModulesAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Once bir cihaz baglayin";
            return;
        }

        await _dialogs.ShowMessageAsync(
            "Banka stack modulleri",
            "Sirayla veya coklu sec:\n" +
            "1) Shamiko (Zygisk)\n" +
            "2) Play Integrity Fork\n" +
            "3) Zygisk LSPosed\n\n" +
            "Sonra: Magisk APK gizle, HMA APK kur (manuel).\n" +
            $"Onerilen gizlenecek paketler: {BankRootHideService.PackagesToHideHint}");

        var files = await _dialogs.PickOpenFilesAsync(
            "Stack modul zip",
            "Magisk modulu (*.zip)|*.zip",
            multiSelect: true);
        if (files is null || files.Count == 0)
            return;

        MagiskBusy = true;
        IsBusy = true;
        var reboot = false;
        try
        {
            foreach (var zip in files)
            {
                var result = await _magisk.InstallAsync(zip, new Progress<string>(s => StatusMessage = s))
                    .ConfigureAwait(true);
                StatusMessage = result.Message;
                if (result.Success)
                    reboot |= result.RebootRequired;
            }

            await RefreshMagiskModulesAsync().ConfigureAwait(true);
            await RunBankHideAuditAsync().ConfigureAwait(true);

            if (reboot && await _dialogs.ShowConfirmationAsync("Reboot", "Moduller icin yeniden baslat?"))
                await _root.RebootDeviceAsync().ConfigureAwait(true);
        }
        finally
        {
            MagiskBusy = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearYkbBankHideAsync()
    {
        if (!await _dialogs.ShowConfirmationAsync(
                "YKB sifirla",
                "Yapı Kredi verileri silinecek (pm clear).\nEski root bayragi temizlenir.\n\nDevam?"))
            return;

        var r = await _bankHide.ClearYkbAppDataAsync().ConfigureAwait(true);
        StatusMessage = r.Message;
        await _dialogs.ShowMessageAsync(r.Success ? "YKB" : "Hata", r.Message);
    }

    [RelayCommand]
    private async Task ClearGmsBankHideAsync()
    {
        if (!await _dialogs.ShowConfirmationAsync(
                "GMS onbellek",
                "Google Play Hizmetleri onbellegi temizlenecek.\nReboot onerilir.\n\nDevam?"))
            return;

        var r = await _bankHide.ClearGmsCacheAsync().ConfigureAwait(true);
        StatusMessage = r.Message;
        await _dialogs.ShowMessageAsync(r.Success ? "GMS" : "Hata", r.Message);
    }

    [RelayCommand]
    private async Task InstallLsposedManagerAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Once bir cihaz baglayin";
            return;
        }

        if (!await _dialogs.ShowConfirmationAsync(
                "LSPosed Manager",
                "Bildirimdeki 'Kabul' parasitik mod — bilinen bug.\n\n" +
                "manager.apk normal uygulama olarak kurulacak.\n" +
                "Sonra uygulama cekmecesinden LSPosed ac.\n\nDevam?"))
            return;

        MagiskBusy = true;
        try
        {
            var r = await _bankHide.InstallLsposedManagerApkAsync().ConfigureAwait(true);
            StatusMessage = r.Message;
            await _dialogs.ShowMessageAsync(r.Success ? "LSPosed Manager" : "Hata", r.Message);
            await RunBankHideAuditAsync().ConfigureAwait(true);
        }
        finally
        {
            MagiskBusy = false;
        }
    }

    [RelayCommand]
    private Task ShowBankHideHmaGuideAsync() =>
        _dialogs.ShowMessageAsync(
            "Hide My Applist (HMA) — YKB icin zorunlu",
            BankRootHideService.HmaTemplateHint + "\n\n" +
            "Gizlenmesi gereken ornek paketler:\n" + BankRootHideService.PackagesToHideHint);

    [RelayCommand]
    private async Task RefreshDenyListAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            DenyListEntries.Clear();
            DenyListStatus = "Once bir cihaz baglayin";
            return;
        }

        MagiskBusy = true;
        try
        {
            var snap = await _denyList.ListAsync().ConfigureAwait(true);
            DenyListEnforce = snap.EnforceEnabled;
            DenyListEntries = new ObservableCollection<string>(
                snap.Entries.Select(e => e.Display));
            DenyListStatus = snap.Entries.Count == 0
                ? "DenyList bos"
                : $"{snap.Entries.Count} kayit | Enforce={(snap.EnforceEnabled ? "ACIK (Shamiko icin kapat)" : "kapali OK")}";
        }
        catch (Exception ex)
        {
            DenyListEntries.Clear();
            DenyListStatus = ex.Message;
            _logger.Warning(ex, "DenyList okunamadi");
        }
        finally
        {
            MagiskBusy = false;
        }
    }

    [RelayCommand]
    private async Task PinBankDenyListAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Once bir cihaz baglayin";
            return;
        }

        if (!await _dialogs.ShowConfirmationAsync(
                "YKB DenyList pin",
                "Magisk Red Listesi tik'i YKB'de kalici OLMAYABILIR (bilinen Magisk bug + dinamik surec).\n\n" +
                "Bu islem:\n" +
                "• Magisk Manager'i durdurur\n" +
                "• CLI+sqlite yazar\n" +
                "• Boot pin + 20sn watchdog kurar\n\n" +
                "Sonra Magisk UI'ya BAKMA. senk DenyList yenile ile dogrula.\n" +
                "Asil test: YKB root uyarisi veriyor mu?\n\nDevam?"))
            return;

        MagiskBusy = true;
        IsBusy = true;
        try
        {
            var result = await _denyList.PinBankPackagesAsync().ConfigureAwait(true);
            DenyListStatus = result.Message;
            StatusMessage = result.Message;
            await RefreshDenyListAsync().ConfigureAwait(true);
            if (result.Success)
                _notification.ShowInfo("DenyList", result.Message);
            else
                await _dialogs.ShowMessageAsync("DenyList", result.Message);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            DenyListStatus = ex.Message;
            _logger.Error(ex, "DenyList pin basarisiz");
        }
        finally
        {
            MagiskBusy = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisableDenyListEnforceAsync()
    {
        MagiskBusy = true;
        try
        {
            var ok = await _denyList.SetEnforceAsync(false).ConfigureAwait(true);
            DenyListStatus = ok
                ? "Enforce DenyList kapatildi (Shamiko OK)"
                : "Enforce kapatilamadi";
            StatusMessage = DenyListStatus;
            await RefreshDenyListAsync().ConfigureAwait(true);
        }
        finally
        {
            MagiskBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshMagiskModulesAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            MagiskModules.Clear();
            MagiskStatus = "Once bir cihaz baglayin";
            return;
        }

        MagiskBusy = true;
        try
        {
            var snap = await _magisk.GetSnapshotAsync().ConfigureAwait(true);
            MagiskModules = new ObservableCollection<MagiskModuleItem>(
                snap.Modules.Select(m => new MagiskModuleItem(m)));
            MagiskManagerHidden = snap.Environment.ManagerHidden;
            MagiskEnvironmentNote = snap.Environment.Summary;
            MagiskStatus = MagiskModules.Count == 0
                ? (snap.Environment.ManagerHidden
                    ? "Magisk gizlenmis; /data/adb/modules bos veya okunamadi"
                    : "Kurulu Magisk/KernelSU modulu yok")
                : MagiskModules.Count + " modul" +
                  (snap.Environment.ManagerHidden ? " (gizli Magisk uygulamasindan bagimsiz)" : "");
        }
        catch (Exception ex)
        {
            MagiskModules.Clear();
            MagiskStatus = ex.Message;
            _logger.Warning(ex, "Magisk modul listesi alinamadi");
        }
        finally
        {
            MagiskBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallMagiskModulesAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Once bir cihaz baglayin";
            return;
        }

        var files = await _dialogs.PickOpenFilesAsync(
            "Magisk modulu sec",
            "Magisk modulu (*.zip)|*.zip|Tum dosyalar|*.*",
            multiSelect: true);
        if (files is null || files.Count == 0)
            return;

        if (!await _dialogs.ShowConfirmationAsync(
                "Magisk modulu kur",
                files.Count + " zip kurulacak.\n\nZip cihaza kopyalanir ve magisk --install-module ile yuklenir.\nModulun aktif olmasi icin cihaz yeniden baslatilmalidir.\n\nDevam?"))
            return;

        MagiskBusy = true;
        IsBusy = true;
        var ok = 0;
        var fail = 0;
        var reboot = false;
        try
        {
            var progress = new Progress<string>(s =>
            {
                MagiskStatus = s;
                StatusMessage = s;
            });

            foreach (var zip in files)
            {
                var result = await _magisk.InstallAsync(zip, progress).ConfigureAwait(true);
                MagiskStatus = result.Message;
                StatusMessage = result.Message;
                if (result.Success)
                {
                    ok++;
                    reboot |= result.RebootRequired;
                }
                else
                {
                    fail++;
                    _logger.Warning("Magisk install failed: {File} {Msg}", zip, result.Message);
                }
            }

            await RefreshMagiskModulesAsync().ConfigureAwait(true);

            if (ok > 0)
                _notification.ShowInfo("Magisk", ok + " modul kuruldu" + (fail > 0 ? ", " + fail + " basarisiz" : ""));

            if (reboot && await _dialogs.ShowConfirmationAsync(
                    "Yeniden baslat",
                    "Kurulan moduller reboot sonrasi aktif olur.\n\nCihaz simdi yeniden baslatilsin mi?"))
            {
                await _root.RebootDeviceAsync().ConfigureAwait(true);
                StatusMessage = "Cihaz yeniden baslatiliyor...";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            MagiskStatus = ex.Message;
            _logger.Error(ex, "Magisk modul kurulumu basarisiz");
        }
        finally
        {
            MagiskBusy = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleMagiskModuleAsync(MagiskModuleItem? item)
    {
        if (item is null || !item.CanToggle)
            return;

        MagiskBusy = true;
        try
        {
            var enable = !item.IsEnabled;
            var ok = await _magisk.SetEnabledAsync(item.Id, enable).ConfigureAwait(true);
            MagiskStatus = ok
                ? item.Name + (enable ? " acildi" : " kapatildi") + " - reboot gerekebilir"
                : item.Name + " durumu degistirilemedi";
            StatusMessage = MagiskStatus;
            await RefreshMagiskModulesAsync().ConfigureAwait(true);
        }
        finally
        {
            MagiskBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveMagiskModuleAsync(MagiskModuleItem? item)
    {
        if (item is null)
            return;
        if (!await _dialogs.ShowConfirmationAsync(
                "Modulu kaldir",
                item.Name + " kaldirilacak (reboot sonrasi).\n\nDevam?"))
            return;

        MagiskBusy = true;
        try
        {
            var ok = await _magisk.RequestRemoveAsync(item.Id).ConfigureAwait(true);
            MagiskStatus = ok
                ? item.Name + " kaldirma kuyruguna alindi - reboot gerekli"
                : item.Name + " kaldirilamadi";
            StatusMessage = MagiskStatus;
            await RefreshMagiskModulesAsync().ConfigureAwait(true);
        }
        finally
        {
            MagiskBusy = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _adb.SelectedDeviceChanged -= OnSelectedDeviceChanged;
    }

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device)
    {
        _dispatcher.Observe(async () => await CheckRootAsync().ConfigureAwait(true));
    }
}
