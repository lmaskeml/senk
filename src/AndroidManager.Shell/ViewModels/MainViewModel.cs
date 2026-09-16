using System.Collections.ObjectModel;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Events;
using AndroidManager.Core.Models;
using AndroidManager.Core.Navigation;
using AndroidManager.Shell.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Prism.Navigation.Regions;

namespace AndroidManager.Shell.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IFastbootDiscoveryService _fastbootDiscovery;
    private readonly IDeviceInfoService _deviceInfoService;
    private readonly IAppDialogService _dialogService;
    private readonly IRegionManager _regionManager;
    private readonly IUiDispatcher _dispatcher;
    private readonly IConnectionWatchdog _watchdog;
    private readonly ISettingsService _settings;
    private bool _disposed;
    private int _lastNavIndex;

    public ObservableCollection<NavItem> NavItems { get; } =
    [
        new() { Label = "CİHAZ" },
        new() { Label = "Genel Bakış", ViewName = ViewNames.DeviceInfo, IconKind = "ViewDashboard" },
        new() { Label = "ADB Terminal", ViewName = ViewNames.Terminal, IconKind = "Console" },
        new() { Label = "Logcat", ViewName = ViewNames.Logcat, IconKind = "TextBoxOutline" },
        new() { Label = "Boot", ViewName = ViewNames.BootControl, IconKind = "Power" },
        new() { Label = "Dosyalar", ViewName = ViewNames.Files, IconKind = "FolderOutline" },
        new() { Label = "Depolama", ViewName = ViewNames.StorageAnalyzer, IconKind = "Harddisk" },
        new() { Label = "Uygulamalar", ViewName = ViewNames.Apps, IconKind = "Apps" },
        new() { Label = "APK", ViewName = ViewNames.ApkAnalyzer, IconKind = "Android" },
        new() { Label = "Ekran Yansıtma", ViewName = ViewNames.Mirror, IconKind = "Cast" },
        new() { Label = "Galeri", ViewName = ViewNames.Gallery, IconKind = "ImageMultipleOutline" },
        new() { Label = "SMS", ViewName = ViewNames.Sms, IconKind = "MessageTextOutline" },
        new() { Label = "Kişiler", ViewName = ViewNames.Contacts, IconKind = "AccountMultipleOutline" },
        new() { Label = "Arama", ViewName = ViewNames.CallLog, IconKind = "PhoneOutline" },
        new() { Label = "WhatsApp Web", ViewName = ViewNames.WhatsAppWeb, IconKind = "Whatsapp" },
        new() { Label = "WhatsApp Aktarım", ViewName = ViewNames.WhatsAppTransfer, IconKind = "Transfer" },
        new() { Label = "SİSTEM" },
        new() { Label = "Root Manager", ViewName = ViewNames.RootSecurity, IconKind = "ShieldKeyOutline" },
        new() { Label = "Recovery Manager", ViewName = ViewNames.RecoveryManager, IconKind = "BackupRestore" },
        new() { Label = "Custom ROM", ViewName = ViewNames.CustomRomWizard, IconKind = "ZipBox" },
        new() { Label = "Orijinal ROM", ViewName = ViewNames.StockRomFlash, IconKind = "CellphoneArrowDown" },
        new() { Label = "Anti-Brick Deposu", ViewName = ViewNames.RomBackupVault, IconKind = "DatabaseExport" },
        new() { Label = "Rescue Center", ViewName = ViewNames.RescueCenter, IconKind = "Lifebuoy" },
        new() { Label = "Debloater", ViewName = ViewNames.Apps, IconKind = "DeleteSweepOutline", InitialTab = "debloat" },
        new() { Label = "GÜVENLİK" },
        new() { Label = "Virüs Tarama", ViewName = ViewNames.Security, IconKind = "ShieldSearch" },
        new() { Label = "VirusTotal", ViewName = ViewNames.VirusTotal, IconKind = "VirusOutline" },
        new() { Label = "Kalıntı Temizleme", ViewName = ViewNames.ResidueCleaner, IconKind = "Broom" },
        new() { Label = "YEDEKLEME" },
        new() { Label = "Backup", ViewName = ViewNames.Backup, IconKind = "CloudDownloadOutline" },
        new() { Label = "AYARLAR" },
        new() { Label = "Ayarlar", ViewName = ViewNames.Settings, IconKind = "CogOutline" }
    ];

    [ObservableProperty] private DeviceInfo? _deviceInfo;
    [ObservableProperty] private ObservableCollection<ConnectedDevice> _connectedDevices = [];
    [ObservableProperty] private ConnectedDevice? _selectedDevice;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusMessage = "Cihaz bekleniyor...";
    [ObservableProperty] private string _wirelessLinkMessage = string.Empty;
    [ObservableProperty] private string _endpointChangedBanner = string.Empty;
    [ObservableProperty] private bool _showEndpointChangedBanner;
    [ObservableProperty] private bool _showOpenWirelessDebug;
    [ObservableProperty] private bool _showReconnectWireless;
    [ObservableProperty] private bool _showUsbRecoverWireless;
    [ObservableProperty] private string _wirelessPresenceHint = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageTitle))]
    private int _selectedNavIndex;
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private string _connectionStatusLabel = "Çevrimdışı";
    [ObservableProperty] private string _connectionStatusKind = "Offline";

    public string CurrentPageTitle
    {
        get
        {
            if (SelectedNavIndex < 0 || SelectedNavIndex >= NavItems.Count)
                return "";
            var item = NavItems[SelectedNavIndex];
            return item.IsHeader ? "" : item.Label;
        }
    }

    private bool _syncingSelection;

    public MainViewModel(
        IAdbService adbService,
        IFastbootDiscoveryService fastbootDiscovery,
        IDeviceInfoService deviceInfoService,
        IAppDialogService dialogService,
        IRegionManager regionManager,
        IUiDispatcher dispatcher,
        IConnectionWatchdog watchdog,
        ISettingsService settings)
    {
        _adbService = adbService;
        _fastbootDiscovery = fastbootDiscovery;
        _deviceInfoService = deviceInfoService;
        _dialogService = dialogService;
        _regionManager = regionManager;
        _dispatcher = dispatcher;
        _watchdog = watchdog;
        _settings = settings;

        _adbService.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _adbService.SelectedDeviceChanged += OnAdbSelectedDeviceChanged;
        _watchdog.StatusChanged += OnWirelessStatusChanged;
    }

    public async Task InitializeAsync()
    {
        await SyncDevicesAsync();
        if (IsConnected)
            await RefreshDeviceInfoAsync();
    }

    partial void OnSelectedDeviceChanged(ConnectedDevice? value)
    {
        if (_syncingSelection || value is null)
            return;

        if (value.IsFastboot)
        {
            IsConnected = true;
            ApplyUsbConnectionBadge();
            StatusMessage = $"Fastboot: {value.Serial}";
            DeviceInfo = null;
            return;
        }

        if (_adbService.SelectedDevice?.Serial == value.Serial)
            return;

        _adbService.SelectDevice(value);
        ObservedTask.Run(RefreshDeviceInfoAsync());
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        if (value < 0 || value >= NavItems.Count)
            return;

        var item = NavItems[value];
        if (item.IsHeader)
        {
            SelectedNavIndex = _lastNavIndex;
            return;
        }

        _lastNavIndex = value;
        if (!string.IsNullOrWhiteSpace(item.InitialTab))
        {
            var parameters = new Prism.Navigation.NavigationParameters
            {
                { "tab", item.InitialTab }
            };
            _regionManager.RequestNavigate(RegionNames.ContentRegion, item.ViewName!, parameters);
        }
        else
        {
            _regionManager.RequestNavigate(RegionNames.ContentRegion, item.ViewName!);
        }
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        try
        {
            StatusMessage = "Cihazlar taranıyor...";
            if (!_adbService.IsRunning)
                await _adbService.StartAsync();

            await SyncDevicesAsync();
            if (IsConnected)
                await RefreshDeviceInfoAsync();
            else
                StatusMessage = "Cihaz bulunamadı";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Hata: {ex.Message}";
            await _dialogService.ShowMessageAsync("Bağlantı Hatası", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshDeviceInfoAsync()
    {
        if (_adbService.SelectedDevice is null)
            return;

        try
        {
            DeviceInfo = await _deviceInfoService.GetDeviceInfoAsync();
            StatusMessage = $"{DeviceInfo.Model} bağlandı";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Bilgi alınamadı: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ConnectWifiAsync()
    {
        var result = await _dialogService.ShowWifiConnectDialogAsync();
        if (result is null)
            return;

        if (result.Success)
        {
            await SyncDevicesAsync();
            await RefreshDeviceInfoAsync();
            StatusMessage = string.IsNullOrWhiteSpace(result.DeviceName)
                ? $"WiFi bağlantısı kuruldu ({result.IpAddress}:{result.Port})"
                : $"{result.DeviceName} bağlandı";
        }
        else
        {
            StatusMessage = string.IsNullOrWhiteSpace(result.Message)
                ? "WiFi bağlantısı başarısız"
                : result.Message;
            await _dialogService.ShowMessageAsync("WiFi Bağlantısı", StatusMessage);
        }
    }

    private async Task SyncDevicesAsync()
    {
        IReadOnlyList<ConnectedDevice> devices;
        if (_adbService.IsRunning)
            devices = await _adbService.GetDevicesAsync();
        else
            devices = _adbService.Devices;

        var merged = devices.ToList();
        var hasAdbReady = merged.Any(d => d.IsAdbReady);
        if (!hasAdbReady)
        {
            var fastboot = await _fastbootDiscovery.GetDevicesAsync();
            foreach (var fb in fastboot)
            {
                if (merged.All(d => !d.Serial.Equals(fb.Serial, StringComparison.OrdinalIgnoreCase)))
                    merged.Add(fb);
            }
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _syncingSelection = true;
            try
            {
                ConnectedDevices.Clear();
                foreach (var device in merged)
                    ConnectedDevices.Add(device);

                SelectedDevice = _adbService.SelectedDevice
                                 ?? merged.FirstOrDefault(d => d.IsFastboot)
                                 ?? merged.FirstOrDefault();
                IsConnected = SelectedDevice is not null;
                ApplyUsbConnectionBadge();
                if (SelectedDevice is { IsFastboot: true })
                    StatusMessage = $"Fastboot bağlı ({SelectedDevice.Serial})";
                else if (IsConnected && SelectedDevice is not null)
                    StatusMessage = SelectedDevice.IsRecovery || SelectedDevice.IsSideload
                        ? $"TWRP ADB bağlı ({SelectedDevice.State})"
                        : $"{SelectedDevice} bağlı";
                else if (merged.Count > 0 && string.IsNullOrWhiteSpace(_fastbootDiscovery.ResolvedExecutablePath))
                    StatusMessage = "fastboot.exe bulunamadı — Ayarlar'dan yol girin";
            }
            finally
            {
                _syncingSelection = false;
            }
        });
    }

    private void OnDeviceConnectionChanged(object? sender, DeviceConnectionChangedEventArgs e)
    {
        _dispatcher.Observe(async () =>
        {
            await SyncDevicesAsync();
            StatusMessage = e.IsConnected
                ? $"{e.Device} bağlandı"
                : $"{e.Device.Serial} bağlantısı kesildi";

            if (e.IsConnected)
                await RefreshDeviceInfoAsync();
            else if (!IsConnected)
                DeviceInfo = null;
        });
    }

    private void OnAdbSelectedDeviceChanged(object? sender, ConnectedDevice? device)
    {
        _dispatcher.Observe(async () =>
        {
            _syncingSelection = true;
            try
            {
                SelectedDevice = device;
                IsConnected = device is not null;
            }
            finally
            {
                _syncingSelection = false;
            }

            if (device is not null)
                await RefreshDeviceInfoAsync();
            else
                DeviceInfo = null;
        });
    }

    [RelayCommand]
    private async Task OpenWirelessDebugSettingsAsync()
    {
        StatusMessage = "Telefonda Kablosuz hata ayıklama ayarı açılıyor…";
        var ok = await _watchdog.TryOpenWirelessDebugSettingsAsync();
        if (!ok)
        {
            await _dialogService.ShowMessageAsync(
                "Kablosuz hata ayıklama",
                "Ayar ekranı açılamadı.\n\nTelefonda: Geliştirici seçenekleri → Kablosuz hata ayıklama → Açık.\n\nCompanion uygulaması çalışıyorsa port otomatik bulunur.");
        }
    }

    [RelayCommand]
    private async Task RecoverWirelessViaUsbAsync()
    {
        StatusMessage = "USB üzerinden kablosuz ADB kurtarma deneniyor…";
        var ok = await _watchdog.TryRecoverViaUsbAsync();
        StatusMessage = ok
            ? "USB kurtarma başarılı — Wi‑Fi ADB yeniden bağlandı"
            : "USB kurtarma başarısız — USB bağlı ve root/WD toggle kontrol edin";
    }

    [RelayCommand]
    private void ReconnectWireless()
    {
        if (!_watchdog.IsWatching)
        {
            StatusMessage = "İzlenen kablosuz cihaz yok — önce Wi‑Fi Bağlan kullanın";
            return;
        }

        if (!_watchdog.RequestReconnect())
        {
            StatusMessage = "Yeniden bağlanma zaten sürüyor…";
            return;
        }

        StatusMessage = "Yeniden bağlanılıyor…";
        ShowReconnectWireless = false;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    /// <returns>true if USB/recovery ADB owns the badge (wireless Offline must not hide TWRP).</returns>
    private bool ApplyUsbConnectionBadge()
    {
        var device = SelectedDevice ?? _adbService.SelectedDevice;
        if (device is { IsRecovery: true })
        {
            ConnectionStatusLabel = "TWRP / Recovery";
            ConnectionStatusKind = "Connected";
            return true;
        }

        if (device is { IsSideload: true })
        {
            ConnectionStatusLabel = "Sideload";
            ConnectionStatusKind = "Connected";
            return true;
        }

        if (device is { IsFastboot: true })
        {
            ConnectionStatusLabel = "Fastboot";
            ConnectionStatusKind = "Connected";
            return true;
        }

        if (device is { IsOnline: true })
        {
            ConnectionStatusLabel = "Bağlı";
            ConnectionStatusKind = "Connected";
            return true;
        }

        return false;
    }

    private static string FormatLastSeen(DateTime lastSeen)
    {
        var span = DateTime.Now - lastSeen;
        if (span.TotalMinutes < 1)
            return "az önce";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes} dk önce";
        if (span.TotalHours < 24)
            return $"{(int)span.TotalHours} saat önce";
        return $"{(int)span.TotalDays} gün önce";
    }

    private void OnWirelessStatusChanged(object? sender, WirelessLinkStatus status)
    {
        _dispatcher.Observe(() =>
        {
            WirelessLinkMessage = status.Message;
            WirelessPresenceHint = status.LastSeen.HasValue
                ? $"Son görülme: {FormatLastSeen(status.LastSeen.Value)}"
                    + (string.IsNullOrWhiteSpace(status.LastNetworkType)
                        ? ""
                        : $" · {status.LastNetworkType}")
                : "";

            ShowOpenWirelessDebug = status.State is WirelessLinkState.NeedsWirelessDebug
                or WirelessLinkState.AdbUnavailable
                or WirelessLinkState.Failed
                or WirelessLinkState.Searching
                or WirelessLinkState.Discovering;

            ShowReconnectWireless = _watchdog.IsWatching && status.State is
                WirelessLinkState.Failed
                or WirelessLinkState.DeviceOffline
                or WirelessLinkState.AdbUnavailable
                or WirelessLinkState.NeedsWirelessDebug
                or WirelessLinkState.PairingRequired
                or WirelessLinkState.Searching;

            ShowUsbRecoverWireless = _watchdog.IsWatching && status.State is
                WirelessLinkState.Failed
                or WirelessLinkState.DeviceOffline
                or WirelessLinkState.AdbUnavailable
                or WirelessLinkState.NeedsWirelessDebug;

            if (!ApplyUsbConnectionBadge())
            {
                (ConnectionStatusLabel, ConnectionStatusKind) = status.State switch
                {
                    WirelessLinkState.Connected => ("Bağlı", "Connected"),
                    WirelessLinkState.Reconnecting or WirelessLinkState.Searching
                        or WirelessLinkState.Discovering or WirelessLinkState.Unstable
                        or WirelessLinkState.Degraded => ("Yeniden bağlanıyor", "Recovering"),
                    WirelessLinkState.PairingRequired => ("Eşleştirme gerekli", "Pairing"),
                    WirelessLinkState.NeedsWirelessDebug or WirelessLinkState.AdbUnavailable
                        => ("Kablosuz hata ayıklama kapalı", "Disabled"),
                    WirelessLinkState.DeviceOffline or WirelessLinkState.Failed
                        => ("Çevrimdışı", "Offline"),
                    _ => (IsConnected ? "Bağlı" : "Çevrimdışı", IsConnected ? "Connected" : "Offline")
                };
            }

            if (status.EndpointChanged && _settings.Current.WirelessShowEndpointChanges)
            {
                EndpointChangedBanner =
                    $"Uç nokta değişti: {status.PreviousEndpoint} → {status.Endpoint}";
                ShowEndpointChangedBanner = true;
            }
            else if (status.State is WirelessLinkState.Idle
                or WirelessLinkState.Failed
                or WirelessLinkState.DeviceOffline
                or WirelessLinkState.PairingRequired)
            {
                ShowEndpointChangedBanner = false;
                EndpointChangedBanner = string.Empty;
            }

            if (status.State is WirelessLinkState.Connected or WirelessLinkState.Reconnecting
                or WirelessLinkState.Unstable or WirelessLinkState.Searching
                or WirelessLinkState.Discovering or WirelessLinkState.AdbUnavailable
                or WirelessLinkState.NeedsWirelessDebug or WirelessLinkState.Failed
                or WirelessLinkState.DeviceOffline or WirelessLinkState.PairingRequired
                or WirelessLinkState.Degraded)
            {
                StatusMessage = status.Message;
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adbService.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _adbService.SelectedDeviceChanged -= OnAdbSelectedDeviceChanged;
        _watchdog.StatusChanged -= OnWirelessStatusChanged;
    }
}
