using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Shell.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Shell.ViewModels;

public sealed partial class WifiConnectViewModel : ObservableObject, IDisposable
{
    private readonly ICompanionDiscoveryService _discovery;
    private readonly IWirelessDebugDiscoveryService _wirelessDiscovery;
    private readonly ICameraQRService _camera;
    private readonly IAdbService _adb;
    private readonly IConnectionPersistService _persist;
    private readonly IConnectionWatchdog _watchdog;
    private readonly INotificationService _notifications;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenSource? _scanCts;
    private bool _disposed;

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private ObservableCollection<DiscoveredDeviceItem> _discoveredDevices = [];
    [ObservableProperty] private DiscoveredDeviceItem? _selectedDevice;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private ObservableCollection<SavedConnection> _savedConnections = [];

    [ObservableProperty] private string _manualIp = "192.168.1.100";
    [ObservableProperty] private string _manualPortText = "5555";
    [ObservableProperty] private string _manualPairingPortText = string.Empty;
    [ObservableProperty] private string _manualPairCode = string.Empty;

    [ObservableProperty] private BitmapSource? _cameraFrame;
    [ObservableProperty] private bool _isCameraActive;
    [ObservableProperty] private bool _isCameraAvailable;
    [ObservableProperty] private string _qrPasteContent = string.Empty;
    [ObservableProperty] private string _qrStatusMessage = string.Empty;

    [ObservableProperty] private string _usbWifiPortText = "5555";
    [ObservableProperty] private string _usbWifiStatusMessage = string.Empty;

    [ObservableProperty] private string _statusMessage = "Hazır";
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectedDeviceName = string.Empty;

    public WifiConnectSessionResult? Result { get; private set; }
    public Action? CloseAccepted { get; set; }
    public Action? CloseCancelled { get; set; }

    public WifiConnectViewModel(
        ICompanionDiscoveryService discovery,
        IWirelessDebugDiscoveryService wirelessDiscovery,
        ICameraQRService camera,
        IAdbService adb,
        IConnectionPersistService persist,
        IConnectionWatchdog watchdog,
        INotificationService notifications,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher)
    {
        _discovery = discovery;
        _wirelessDiscovery = wirelessDiscovery;
        _camera = camera;
        _adb = adb;
        _persist = persist;
        _watchdog = watchdog;
        _notifications = notifications;
        _dialogs = dialogs;
        _dispatcher = dispatcher;

        IsCameraAvailable = camera.IsCameraAvailable;
        _discovery.DeviceDiscovered += OnDeviceDiscovered;
        _wirelessDiscovery.DeviceDiscovered += OnWirelessDeviceDiscovered;
        _camera.FrameCaptured += OnFrameCaptured;
        _camera.QRDecoded += OnQrDecoded;
    }

    public async Task OnOpenedAsync()
    {
        await ReloadSavedAsync().ConfigureAwait(true);
        var last = await _persist.GetLastAsync().ConfigureAwait(true);
        if (last is not null)
        {
            ManualIp = last.IpAddress;
            ManualPortText = last.Port.ToString(CultureInfo.InvariantCulture);
        }

        await StartScanAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        StopScan();
        DiscoveredDevices.Clear();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        StatusMessage = "Companion + Android 9 ADB + mDNS taranıyor...";

        try
        {
            if (!_adb.IsRunning)
                await _adb.StartAsync(_scanCts.Token);

            await _discovery.StartListeningAsync(_scanCts.Token);
            _wirelessDiscovery.HintKnownEndpoints(
                SavedConnections
                    .Where(s => !string.IsNullOrWhiteSpace(s.IpAddress) && s.Port > 0)
                    .Select(s => (s.IpAddress, s.Port))
                    .ToList());
            await _wirelessDiscovery.StartScanningAsync(_scanCts.Token);

            foreach (var cached in _discovery.GetCachedDevices())
                OnDeviceDiscovered(_discovery, cached);

            try
            {
                var mdns = await _wirelessDiscovery.GetDiscoveredDevicesAsync(_scanCts.Token);
                foreach (var device in mdns)
                    OnWirelessDeviceDiscovered(_wirelessDiscovery, device);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Initial mDNS snapshot failed");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Discovery start failed");
            StatusMessage = $"Tarama başlatılamadı: {ex.Message}";
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void StopScan()
    {
        _scanCts?.Cancel();
        _discovery.StopListening();
        _wirelessDiscovery.StopScanning();
        IsScanning = false;
        if (StatusMessage.Contains("taranıyor", StringComparison.OrdinalIgnoreCase)
            || StatusMessage.Contains("aranıyor", StringComparison.OrdinalIgnoreCase))
            StatusMessage = "Tarama durduruldu";
    }

    [RelayCommand]
    private async Task ConnectToSelectedAsync()
    {
        if (SelectedDevice is null)
        {
            StatusMessage = "Listeden cihaz seçin";
            return;
        }

        await ConnectDiscoveredAsync(SelectedDevice);
    }

    [RelayCommand]
    private async Task ConnectDiscoveredAsync(DiscoveredDeviceItem device)
    {
        if (device.Source == DeviceDiscoverySource.WirelessDebug)
        {
            await ConnectWirelessAsync(device);
            return;
        }

        // Android 11+/14: Companion open ≠ ADB listening. Guide before futile 5555 connect.
        if (!device.IsAdbReachable)
        {
            var proceed = await _dialogs.ShowConfirmationAsync(
                "Android 11+ / 14 — Companion yetmez",
                "TV kutusu (Android 8) gibi eski cihazlar ADB’yi doğrudan Wi‑Fi’de açabilir.\n\n" +
                "Telefonunuzda (Android 11+) Companion yalnızca keşif/eşleştirme içindir.\n" +
                "Bağlanmak için:\n\n" +
                "1) Geliştirici seçenekleri → Kablosuz hata ayıklama AÇIK\n" +
                "2) «Bu cihazı eşleştir» → 6 haneli kod\n" +
                "3) Bu pencerede Otomatik taramada mDNS satırına Bağlan\n\n" +
                "veya USB takıp «USB → WiFi» kullanın.\n\n" +
                "Yine de Companion eşleştirmesini denemek ister misiniz?");

            if (!proceed)
            {
                SelectedTab = 0;
                StatusMessage = "Kablosuz hata ayıklamayı açıp taramayı yenileyin (mDNS)";
                await StartScanAsync();
                return;
            }
        }

        var pairCode = await _dialogs.PromptPairCodeAsync(device.DeviceName);
        if (string.IsNullOrWhiteSpace(pairCode))
        {
            StatusMessage = "İptal edildi";
            return;
        }

        IsConnecting = true;
        StatusMessage = $"Bağlanılıyor: {device.DeviceName}...";

        var result = await _discovery.ConnectWithPairCodeAsync(
            device.IpAddress,
            device.TcpPort,
            pairCode.Trim(),
            Environment.MachineName);

        await ApplyResultAsync(result);

        if (!result.Success)
        {
            StatusMessage = "Companion eşleşti ama ADB yok — kablosuz debug / USB→WiFi deneyin";
            SelectedTab = 0;
            await StartScanAsync();
        }
    }

    private async Task ConnectWirelessAsync(DiscoveredDeviceItem device)
    {
        IsConnecting = true;
        StatusMessage = $"Kablosuz debug: {device.DeviceName}...";

        string? pairCode = null;
        var needsPairPrompt = device.NeedsPairing || !device.CanConnectDirectly;
        if (needsPairPrompt)
        {
            pairCode = await _dialogs.PromptPairCodeAsync(
                $"{device.DeviceName}\n(Geliştirici seçenekleri → Kablosuz hata ayıklama → Eşleştirme kodu)");
            if (string.IsNullOrWhiteSpace(pairCode))
            {
                IsConnecting = false;
                StatusMessage = "İptal edildi";
                return;
            }
        }

        var wireless = device.ToWirelessDebugDevice();
        var result = await _wirelessDiscovery.ConnectAsync(wireless, pairCode);

        // Connect without code failed — ask for pairing code and retry.
        if (!result.Success
            && string.IsNullOrWhiteSpace(pairCode)
            && device.HasPairingPort)
        {
            pairCode = await _dialogs.PromptPairCodeAsync(
                $"{device.DeviceName}\nBağlantı başarısız — eşleştirme kodunu girin");
            if (!string.IsNullOrWhiteSpace(pairCode))
                result = await _wirelessDiscovery.ConnectAsync(wireless, pairCode);
        }

        await ApplyResultAsync(result);
    }

    [RelayCommand]
    private async Task ConnectManualAsync()
    {
        var ip = ManualIp.Trim();
        if (string.IsNullOrWhiteSpace(ip))
        {
            StatusMessage = "IP adresi girin";
            return;
        }

        if (ip.Contains(':', StringComparison.Ordinal))
        {
            var parts = ip.Split(':', 2);
            ip = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out var embeddedPort))
                ManualPortText = embeddedPort.ToString(CultureInfo.InvariantCulture);
        }

        if (!int.TryParse(ManualPortText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            StatusMessage = "Geçerli bir bağlantı portu girin (1-65535)";
            return;
        }

        IsConnecting = true;
        StatusMessage = $"Bağlanılıyor: {ip}:{port}...";

        CompanionConnectResult result;

        // Pair code → Android 11+ wireless debugging (adb pair), NOT Companion TCP.
        // Companion “Bağlı” yalnızca kendi kodunu kabul etti demektir; ADB değildir.
        if (!string.IsNullOrWhiteSpace(ManualPairCode))
        {
            result = await ConnectManualWirelessAsync(ip, port, ManualPairCode.Trim());
        }
        else
        {
            var ok = await _adb.ConnectWifiAsync(ip, port);
            result = new CompanionConnectResult
            {
                Success = ok,
                Message = ok
                    ? "ADB bağlantısı kuruldu"
                    : port == CompanionPorts.DefaultAdb
                        ? "5555 kapalı. Android 11+: Kablosuz hata ayıklamadaki IP:PORT’u kullanın (5555 değil) veya eşleştirme kodu + eşleştirme portunu girin."
                        : $"adb connect {ip}:{port} başarısız",
                IpAddress = ip,
                Port = port
            };
        }

        await ApplyResultAsync(result);
    }

    /// <summary>
    /// Manual wireless-debug pair+connect. Uses mDNS when possible; falls back to explicit pairing port.
    /// </summary>
    private async Task<CompanionConnectResult> ConnectManualWirelessAsync(string ip, int connectPortHint, string pairCode)
    {
        try
        {
            if (!_adb.IsRunning)
                await _adb.StartAsync();

            IReadOnlyList<WirelessDebugDevice> mdns = [];
            try
            {
                mdns = await _wirelessDiscovery.GetDiscoveredDevicesAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "mDNS lookup during manual connect failed");
            }

            var match = mdns.FirstOrDefault(d =>
                string.Equals(d.IpAddress, ip, StringComparison.OrdinalIgnoreCase));

            int? pairingPort = null;
            if (int.TryParse(ManualPairingPortText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var typedPairPort)
                && typedPairPort is >= 1 and <= 65535)
            {
                pairingPort = typedPairPort;
            }

            pairingPort ??= match?.PairingPort;

            // Prefer mDNS connect port when user still has legacy 5555 default.
            var connectPort = connectPortHint;
            if (match?.ConnectPort is > 0 && (connectPortHint == CompanionPorts.DefaultAdb || connectPortHint <= 0))
                connectPort = match.ConnectPort.Value;
            else if (match?.ConnectPort is > 0 && connectPortHint == match.ConnectPort.Value)
                connectPort = match.ConnectPort.Value;

            if (pairingPort is null or <= 0)
            {
                return new CompanionConnectResult
                {
                    Success = false,
                    Message =
                        "Kablosuz hata ayıklama açık olsa da eşleştirme portu bulunamadı.\n\n" +
                        "Telefonda: Kablosuz hata ayıklama → «Eşleştirme koduyla eşleştir»\n" +
                        "• 6 haneli kod → Pair Code\n" +
                        "• Oradaki eşleştirme PORTU → Eşleştirme portu (5555 / Companion kodu değil)\n" +
                        "• Ana ekrandaki IP:PORT → Bağlantı portu\n\n" +
                        "veya Otomatik sekmesinde mDNS satırına Bağlan.\n\n" +
                        "Not: Companion uygulamasındaki «Bağlı» ADB değildir.",
                    IpAddress = ip,
                    Port = connectPort
                };
            }

            var device = new WirelessDebugDevice
            {
                DeviceId = match?.DeviceId ?? $"manual-{ip}",
                DisplayName = match?.DisplayName ?? ip,
                IpAddress = ip,
                ConnectPort = connectPort > 0 ? connectPort : match?.ConnectPort,
                PairingPort = pairingPort,
                IsLegacyTcp = false
            };

            StatusMessage = $"adb pair {ip}:{pairingPort}...";
            var result = await _wirelessDiscovery.ConnectAsync(device, pairCode);

            // If connect port was still wrong (5555), retry after pair using refreshed mDNS.
            if (!result.Success && connectPortHint == CompanionPorts.DefaultAdb)
            {
                await Task.Delay(800);
                var refreshed = (await _wirelessDiscovery.GetDiscoveredDevicesAsync())
                    .FirstOrDefault(d => string.Equals(d.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
                if (refreshed?.ConnectPort is > 0 and not CompanionPorts.DefaultAdb)
                {
                    StatusMessage = $"adb connect {ip}:{refreshed.ConnectPort}...";
                    var ok = await _adb.ConnectWifiAsync(ip, refreshed.ConnectPort.Value);
                    return new CompanionConnectResult
                    {
                        Success = ok,
                        Message = ok
                            ? $"Kablosuz hata ayıklama bağlandı: {ip}:{refreshed.ConnectPort}"
                            : result.Message,
                        IpAddress = ip,
                        Port = refreshed.ConnectPort.Value,
                        DeviceName = refreshed.DisplayName
                    };
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return new CompanionConnectResult
            {
                Success = false,
                Message = ex.Message,
                IpAddress = ip,
                Port = connectPortHint
            };
        }
    }

    [RelayCommand]
    private async Task ConnectSavedAsync(SavedConnection? conn)
    {
        if (conn is null) return;
        ManualIp = conn.IpAddress;
        ManualPortText = conn.Port.ToString(CultureInfo.InvariantCulture);
        ManualPairCode = string.Empty;
        SelectedTab = 1;
        await ConnectManualAsync();
    }

    [RelayCommand]
    private async Task SwitchUsbToWifiAsync()
    {
        if (!int.TryParse(UsbWifiPortText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            UsbWifiStatusMessage = "Geçerli bir port girin (1-65535)";
            StatusMessage = UsbWifiStatusMessage;
            return;
        }

        if (_adb.SelectedDevice is null)
        {
            UsbWifiStatusMessage = "Önce USB ile bir cihaz bağlayın.";
            StatusMessage = UsbWifiStatusMessage;
            return;
        }

        IsConnecting = true;
        UsbWifiStatusMessage = $"tcpip {port} açılıyor ve Wi‑Fi’ye geçiliyor...";
        StatusMessage = UsbWifiStatusMessage;

        try
        {
            if (!_adb.IsRunning)
                await _adb.StartAsync();

            var result = await _adb.SwitchUsbToWifiAsync(port);
            UsbWifiStatusMessage = result.Message;
            StatusMessage = result.Message;

            if (!result.Success)
            {
                IsConnecting = false;
                return;
            }

            await ApplyResultAsync(new CompanionConnectResult
            {
                Success = true,
                Message = result.Message,
                IpAddress = result.IpAddress,
                Port = result.Port,
                DeviceName = _adb.SelectedDevice?.Model ?? result.IpAddress,
                Serial = _adb.SelectedDevice?.Serial ?? string.Empty,
                Transport = WirelessTransportKind.LegacyTcp
            });
        }
        catch (Exception ex)
        {
            IsConnecting = false;
            UsbWifiStatusMessage = ex.Message;
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RemoveSavedAsync(SavedConnection? conn)
    {
        if (conn is null) return;
        await _persist.RemoveAsync(conn.IpAddress).ConfigureAwait(true);
        await ReloadSavedAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StartCameraAsync()
    {
        if (!IsCameraAvailable)
        {
            QrStatusMessage = "Kamera bulunamadı — dosya veya clipboard kullanın";
            return;
        }

        try
        {
            IsCameraActive = true;
            QrStatusMessage = "QR kodu kameraya gösterin...";
            await _camera.StartAsync();
        }
        catch (Exception ex)
        {
            IsCameraActive = false;
            QrStatusMessage = $"Kamera hatası: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopCamera()
    {
        _camera.Stop();
        IsCameraActive = false;
        QrStatusMessage = "Kamera durduruldu";
    }

    [RelayCommand]
    private async Task ConnectFromQrPasteAsync()
    {
        if (string.IsNullOrWhiteSpace(QrPasteContent))
        {
            QrStatusMessage = "QR JSON içeriğini yapıştırın";
            return;
        }

        await ProcessQrAsync(QrPasteContent.Trim());
    }

    [RelayCommand]
    private async Task ReadQrFromFileAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "QR kod içeren resmi seçin",
            "Resim|*.png;*.jpg;*.jpeg;*.bmp|Tüm Dosyalar|*.*");
        if (files is not { Count: > 0 })
            return;

        QrStatusMessage = "Okunuyor...";
        var content = await _camera.DecodeFromFileAsync(files[0]);
        if (string.IsNullOrWhiteSpace(content))
        {
            QrStatusMessage = "QR kod okunamadı";
            return;
        }

        QrPasteContent = content;
        QrStatusMessage = "QR okundu";
        await ProcessQrAsync(content);
    }

    [RelayCommand]
    private async Task ReadQrFromClipboardAsync()
    {
        QrStatusMessage = "Clipboard kontrol ediliyor...";
        var content = await _camera.DecodeFromClipboardAsync();
        if (string.IsNullOrWhiteSpace(content))
        {
            QrStatusMessage = "Clipboard'da QR bulunamadı";
            return;
        }

        QrPasteContent = content;
        QrStatusMessage = "QR okundu";
        await ProcessQrAsync(content);
    }

    [RelayCommand]
    private void Cancel()
    {
        StopScan();
        StopCamera();
        Result = null;
        CloseCancelled?.Invoke();
    }

    private async Task ProcessQrAsync(string content)
    {
        IsConnecting = true;
        StatusMessage = "QR işleniyor...";
        var result = await _discovery.ConnectFromQrAsync(content);
        await ApplyResultAsync(result);
        if (!result.Success)
            QrStatusMessage = result.Message;
    }

    private async Task ApplyResultAsync(CompanionConnectResult result)
    {
        IsConnecting = false;
        StatusMessage = result.Message;
        Result = new WifiConnectSessionResult
        {
            Success = result.Success,
            IpAddress = result.IpAddress,
            Port = result.Port,
            Message = result.Message,
            DeviceName = result.DeviceName
        };

        if (!result.Success)
        {
            if (result.Message.Contains("ADB portu kapalı", StringComparison.OrdinalIgnoreCase)
                || result.Message.Length > 80)
            {
                await _dialogs.ShowMessageAsync("WiFi bağlantısı", result.Message).ConfigureAwait(true);
            }

            return;
        }

        IsConnected = true;
        ConnectedDeviceName = string.IsNullOrWhiteSpace(result.DeviceName)
            ? result.IpAddress
            : result.DeviceName;

        await _persist.SaveAsync(new SavedConnection
        {
            DeviceId = result.DeviceId,
            StableDeviceId = string.IsNullOrWhiteSpace(result.DeviceId) ? "" : $"cid:{result.DeviceId}",
            Serial = result.Serial,
            IpAddress = result.IpAddress,
            Port = result.Port > 0 ? result.Port : CompanionPorts.DefaultAdb,
            DeviceName = ConnectedDeviceName,
            Transport = result.Transport,
            AutoReconnect = true,
            LastSeen = DateTime.Now,
            PairingState = PairingState.Paired
        }).ConfigureAwait(true);
        await ReloadSavedAsync().ConfigureAwait(true);

        _watchdog.Start(new WirelessWatchTarget
        {
            IpAddress = result.IpAddress,
            Port = result.Port > 0 ? result.Port : CompanionPorts.DefaultAdb,
            DeviceId = result.DeviceId,
            StableId = string.IsNullOrWhiteSpace(result.DeviceId) ? "" : $"cid:{result.DeviceId}",
            SerialHint = result.Serial,
            DeviceName = ConnectedDeviceName,
            Transport = result.Transport,
            AutoReconnect = true
        }, intervalSeconds: 3);
        _notifications.ShowDeviceConnected(ConnectedDeviceName);

        StopScan();
        StopCamera();
        CloseAccepted?.Invoke();
    }

    private async Task ReloadSavedAsync()
    {
        var all = await _persist.GetAllAsync().ConfigureAwait(true);
        SavedConnections = new ObservableCollection<SavedConnection>(all);
    }
    private void OnDeviceDiscovered(object? sender, CompanionDevice device)
    {
        _dispatcher.Observe(() =>
        {
            var existing = DiscoveredDevices.FirstOrDefault(d =>
                d.Source == DeviceDiscoverySource.Companion
                && string.Equals(d.IpAddress, device.IpAddress, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var refreshed = DiscoveredDeviceItem.FromCompanion(device);
                existing.AdbPort = refreshed.AdbPort;
                existing.TcpPort = refreshed.TcpPort;
                existing.IsAdbReachable = refreshed.IsAdbReachable;
                existing.SubTitle = refreshed.SubTitle;
                existing.SourceLabel = refreshed.SourceLabel;
                existing.Touch();
                return;
            }

            DiscoveredDevices.Add(DiscoveredDeviceItem.FromCompanion(device));
            StatusMessage = $"Companion: {device.DeviceName} bulundu";
        });
    }

    private void OnWirelessDeviceDiscovered(object? sender, WirelessDebugDevice device)
    {
        _dispatcher.Observe(() =>
        {
            var existing = DiscoveredDevices.FirstOrDefault(d =>
                d.Source == DeviceDiscoverySource.WirelessDebug
                && (string.Equals(d.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(d.IpAddress, device.IpAddress, StringComparison.OrdinalIgnoreCase)));

            if (existing is not null)
            {
                existing.UpdateFromWireless(device);
                existing.Touch();
                StatusMessage = $"mDNS: {device.DisplayName} güncellendi";
                return;
            }

            DiscoveredDevices.Add(DiscoveredDeviceItem.FromWireless(device));
            StatusMessage = device.IsLegacyTcp
                ? $"ADB TCP: {device.DisplayName} bulundu"
                : $"mDNS: {device.DisplayName} bulundu (Companion gerekmez)";
        });
    }

    private void OnFrameCaptured(object? sender, BitmapSource frame)
    {
        _dispatcher.Observe(() => CameraFrame = frame);
    }

    private void OnQrDecoded(object? sender, string content)
    {
        _dispatcher.Observe(async () =>
        {
            IsCameraActive = false;
            QrPasteContent = content;
            QrStatusMessage = "QR okundu — bağlanılıyor...";
            await ProcessQrAsync(content);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _discovery.DeviceDiscovered -= OnDeviceDiscovered;
        _wirelessDiscovery.DeviceDiscovered -= OnWirelessDeviceDiscovered;
        _camera.FrameCaptured -= OnFrameCaptured;
        _camera.QRDecoded -= OnQrDecoded;
        StopScan();
        StopCamera();
        // Watchdog stays running after successful connect (singleton).
    }
}

public sealed partial class DiscoveredDeviceItem : ObservableObject
{
    public DeviceDiscoverySource Source { get; private set; }
    public string DeviceId { get; private set; } = string.Empty;
    public string IpAddress { get; private set; } = string.Empty;
    public int AdbPort { get; set; }
    public int TcpPort { get; set; }
    public int? PairingPort { get; private set; }
    public string DeviceName { get; private set; } = string.Empty;
    public string AndroidVersion { get; private set; } = string.Empty;
    public bool IsLegacyTcp { get; private set; }
    public bool IsAdbReachable { get; set; }

    [ObservableProperty] private DateTime _lastSeen = DateTime.Now;
    [ObservableProperty] private string _subTitle = string.Empty;
    [ObservableProperty] private string _sourceLabel = string.Empty;

    public string LastSeenFormatted => $"Son: {LastSeen:HH:mm:ss}";
    public bool CanConnectDirectly => AdbPort > 0;
    public bool NeedsPairing => PairingPort is > 0 && AdbPort <= 0;
    public bool HasPairingPort => PairingPort is > 0;

    public static DiscoveredDeviceItem FromCompanion(CompanionDevice d)
    {
        var connectPort = d.WirelessAdbPort ?? d.AdbPort;
        var adbHint = d.IsAdbReachable
            ? $"ADB açık • {d.IpAddress}:{connectPort}"
            : d.WirelessAdbPort is > 0
                ? $"kablosuz port {d.WirelessAdbPort} okundu — eşleştirme gerekebilir"
                : $"ADB kapalı — kablosuz debug / USB→WiFi gerekli • eşleştirme {d.IpAddress}:{d.TcpPort}";

        return new()
        {
            Source = DeviceDiscoverySource.Companion,
            DeviceId = d.IpAddress,
            IpAddress = d.IpAddress,
            AdbPort = connectPort,
            TcpPort = d.TcpPort,
            PairingPort = d.WirelessPairingPort,
            DeviceName = d.DeviceName,
            AndroidVersion = d.AndroidVersion,
            IsAdbReachable = d.IsAdbReachable,
            SubTitle = $"Companion • Android {d.AndroidVersion} • {adbHint}",
            SourceLabel = d.IsAdbReachable
                ? "Companion (ADB hazır)"
                : d.WirelessAdbPort is > 0
                    ? "Companion (kablosuz port var)"
                    : "Companion (yalnızca eşleştirme)"
        };
    }

    public static DiscoveredDeviceItem FromWireless(WirelessDebugDevice d)
    {
        var item = new DiscoveredDeviceItem
        {
            Source = DeviceDiscoverySource.WirelessDebug,
            DeviceId = d.DeviceId,
            IpAddress = d.IpAddress,
            DeviceName = d.DisplayName,
            IsLegacyTcp = d.IsLegacyTcp,
            SourceLabel = "Kablosuz Debug (mDNS)"
        };
        item.UpdateFromWireless(d);
        return item;
    }

    public void UpdateFromWireless(WirelessDebugDevice d)
    {
        DeviceId = d.DeviceId;
        IpAddress = d.IpAddress;
        DeviceName = d.DisplayName;
        AdbPort = d.ConnectPort ?? 0;
        PairingPort = d.PairingPort;
        IsLegacyTcp = d.IsLegacyTcp;
        SubTitle = d.SubTitle;
        SourceLabel = d.IsLegacyTcp ? "ADB TCP (5555 / mDNS)" : "Kablosuz Debug (mDNS)";
    }

    public WirelessDebugDevice ToWirelessDebugDevice() => new()
    {
        DeviceId = DeviceId,
        DisplayName = DeviceName,
        IpAddress = IpAddress,
        ConnectPort = AdbPort > 0 ? AdbPort : null,
        PairingPort = PairingPort,
        IsLegacyTcp = IsLegacyTcp
    };

    public void Touch() => LastSeen = DateTime.Now;
}
