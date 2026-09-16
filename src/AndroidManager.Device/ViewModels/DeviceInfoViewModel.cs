using System.Collections.ObjectModel;
using System.Windows;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidManager.Device.ViewModels;

public partial class DeviceInfoViewModel : ObservableObject, IDisposable
{
    private const int HistoryCapacity = 30;

    private readonly IAdbService _adbService;
    private readonly IDeviceInfoService _deviceInfoService;
    private readonly IDeviceToolsService _tools;
    private readonly IDiagnosticReportService _reportService;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppDialogService _dialogs;
    private readonly ISecurityService? _security;
    private readonly IRootSecurityService? _rootSecurity;
    private CancellationTokenSource? _liveCts;
    private int _liveGeneration;
    private bool _disposed;

    [ObservableProperty] private DeviceInfo? _deviceInfo;
    [ObservableProperty] private DeviceDiagnostics? _diagnostics;
    [ObservableProperty] private LiveDeviceMetrics? _live;
    [ObservableProperty] private DeviceSecuritySummary? _securitySummary;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLiveMonitoring = true;
    [ObservableProperty] private string _statusMessage = "Cihaz bekleniyor...";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _sensorsSummary = "—";
    [ObservableProperty] private string _clipboardPreview = "";
    [ObservableProperty] private bool _hasClipboardPreview;
    [ObservableProperty] private bool _stayAwake;

    public ObservableCollection<double> CpuHistory { get; } = [];
    public ObservableCollection<double> RamHistory { get; } = [];
    public ObservableCollection<double> TempHistory { get; } = [];
    public ObservableCollection<double> BatteryHistory { get; } = [];

    public string StayAwakeLabel => StayAwake ? "Stay-awake: Açık" : "Stay-awake: Kapalı";
    public string DeepScanLabel =>
        SecuritySummary?.DeepScanAvailable == true ? "Derin tarama: açık" : "Derin tarama: kapalı";

    partial void OnSecuritySummaryChanged(DeviceSecuritySummary? value) =>
        OnPropertyChanged(nameof(DeepScanLabel));

    public DeviceInfoViewModel(
        IAdbService adbService,
        IDeviceInfoService deviceInfoService,
        IDeviceToolsService tools,
        IDiagnosticReportService reportService,
        IUiDispatcher dispatcher,
        IAppDialogService dialogs,
        ISecurityService? security = null,
        IRootSecurityService? rootSecurity = null)
    {
        _adbService = adbService;
        _deviceInfoService = deviceInfoService;
        _tools = tools;
        _reportService = reportService;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _security = security;
        _rootSecurity = rootSecurity;

        _adbService.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _adbService.SelectedDeviceChanged += OnSelectedDeviceChanged;
    }

    public async Task InitializeAsync()
    {
        IsConnected = _adbService.SelectedDevice is not null;
        NotifyToolCommands();
        if (IsConnected)
        {
            await RefreshAsync();
            StartLiveLoop();
        }
    }

    partial void OnIsLiveMonitoringChanged(bool value)
    {
        if (value && IsConnected) StartLiveLoop();
        else StopLiveLoop();
    }

    partial void OnIsConnectedChanged(bool value) => NotifyToolCommands();

    partial void OnIsBusyChanged(bool value) => NotifyToolCommands();

    partial void OnStayAwakeChanged(bool value) => OnPropertyChanged(nameof(StayAwakeLabel));

    partial void OnClipboardPreviewChanged(string value) =>
        HasClipboardPreview = !string.IsNullOrWhiteSpace(value);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_adbService.SelectedDevice is null)
        {
            ClearDeviceState("Cihaz bağlı değil.");
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Cihaz bilgisi alınıyor...";
            DeviceInfo = await _deviceInfoService.GetDeviceInfoAsync();
            Diagnostics = await _deviceInfoService.GetDiagnosticsAsync();
            SensorsSummary = FormatSensors(Diagnostics.Sensors);
            await SyncStayAwakeStateAsync();
            await RefreshSecuritySummaryAsync();
            IsConnected = true;
            StatusMessage = $"{DeviceInfo.Manufacturer} {DeviceInfo.Model} hazır";
            if (IsLiveMonitoring) StartLiveLoop();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Hata: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SyncStayAwakeStateAsync()
    {
        try
        {
            var raw = (await _adbService.ExecuteShellAsync("settings get global stay_on_while_plugged_in")).Trim();
            StayAwake = int.TryParse(raw, out var value) && value > 0;
        }
        catch
        {
            // leave previous StayAwake value
        }
    }

    private async Task RefreshSecuritySummaryAsync()
    {
        if (_security is null && _rootSecurity is null)
        {
            SecuritySummary = null;
            return;
        }

        try
        {
            RootAccess root = RootAccess.None;
            var bootloaderUnlocked = false;
            var deep = false;

            if (_rootSecurity is not null)
            {
                var rootStatus = await _rootSecurity.GetRootStatusAsync().ConfigureAwait(true);
                root = rootStatus.Access;
                deep = root != RootAccess.None;

                try
                {
                    var boot = await _rootSecurity.GetBootSecurityAsync().ConfigureAwait(true);
                    bootloaderUnlocked = !boot.BootloaderLocked;
                }
                catch
                {
                    // optional
                }
            }

            var threatCount = 0;
            DateTime? lastScan = null;
            if (_security is not null)
            {
                var status = await _security.GetSecurityStatusAsync().ConfigureAwait(true);
                threatCount = status.ThreatCount;
                lastScan = status.LastScanDate == default ? null : status.LastScanDate;
            }

            SecuritySummary = new DeviceSecuritySummary
            {
                RootAccess = root,
                BootloaderUnlocked = bootloaderUnlocked,
                PlayIntegrity = "Bilinmiyor",
                ThreatCount = threatCount,
                LastScanAt = lastScan,
                DeepScanAvailable = deep
            };
        }
        catch
        {
            SecuritySummary = new DeviceSecuritySummary
            {
                PlayIntegrity = "Bilinmiyor"
            };
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveReport))]
    private async Task SaveReportAsync()
    {
        if (Diagnostics is null || DeviceInfo is null)
        {
            StatusMessage = "Önce cihaz bilgisini yenileyin.";
            return;
        }

        var folder = await _dialogs.PickFolderAsync("Tanı raporu kayıt klasörü");
        if (string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "Rapor oluşturuluyor…";
            var txt = await _reportService.GenerateTxtAsync(DeviceInfo, Diagnostics, folder);
            var pdf = await _reportService.GeneratePdfAsync(DeviceInfo, Diagnostics, folder);
            StatusMessage = $"Rapor kaydedildi: {pdf}";
            await _dialogs.ShowMessageAsync("Tanı Raporu", $"TXT:\n{txt}\n\nPDF:\n{pdf}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rapor kaydedilemedi: {ex.Message}";
            await _dialogs.ShowMessageAsync("Tanı Raporu", StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveReport() => IsConnected && DeviceInfo is not null && Diagnostics is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task RebootAsync()
    {
        if (!await ConfirmAsync("Yeniden başlat", "Cihaz yeniden başlatılsın mı?")) return;
        await RunToolAsync(() => _tools.RebootAsync(DeviceRebootMode.System));
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task RebootRecoveryAsync()
    {
        if (!await ConfirmAsync("Recovery", "Cihaz Recovery moduna alınsın mı?")) return;
        await RunToolAsync(() => _tools.RebootAsync(DeviceRebootMode.Recovery));
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task RebootBootloaderAsync()
    {
        if (!await ConfirmAsync("Bootloader", "Cihaz Bootloader/Fastboot’a alınsın mı?")) return;
        await RunToolAsync(() => _tools.RebootAsync(DeviceRebootMode.Bootloader));
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private Task LockScreenAsync() => RunToolAsync(() => _tools.LockScreenAsync());

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task ToggleStayAwakeAsync()
    {
        var next = !StayAwake;
        var result = await RunToolAsync(() => _tools.SetStayAwakeAsync(next));
        if (result) StayAwake = next;
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private Task WifiOnAsync() => RunToolAsync(() => _tools.SetWifiEnabledAsync(true));

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private Task WifiOffAsync() => RunToolAsync(() => _tools.SetWifiEnabledAsync(false));

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private Task BluetoothOnAsync() => RunToolAsync(() => _tools.SetBluetoothEnabledAsync(true));

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private Task BluetoothOffAsync() => RunToolAsync(() => _tools.SetBluetoothEnabledAsync(false));

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task ScreenshotAsync()
    {
        try
        {
            IsBusy = true;
            var result = await _tools.CaptureScreenshotAsync();
            StatusMessage = result.Message;
            await _dialogs.ShowMessageAsync("Ekran görüntüsü", result.Message);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Ekran görüntüsü", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task ClipboardFromDeviceAsync()
    {
        try
        {
            IsBusy = true;
            var result = await _tools.GetClipboardAsync();
            if (!result.Success || result.PathOrPayload is null)
            {
                StatusMessage = result.Message;
                await _dialogs.ShowMessageAsync("Clipboard", result.Message);
                return;
            }

            var text = result.PathOrPayload;
            ClipboardPreview = text.Length > 120 ? text[..120] + "…" : text;

            await _dispatcher.InvokeAsync(() =>
            {
                Clipboard.SetText(text);
            });
            StatusMessage = "Android clipboard → PC panosu";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Clipboard alınamadı: {ex.Message}";
            await _dialogs.ShowMessageAsync("Clipboard", StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task ClipboardToDeviceAsync()
    {
        string text = "";
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                text = Clipboard.GetText() ?? "";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"PC panosu okunamadı: {ex.Message}";
            await _dialogs.ShowMessageAsync("Clipboard", StatusMessage);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "PC panosu boş";
            await _dialogs.ShowMessageAsync("Clipboard", "PC panosunda metin yok.");
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _tools.SetClipboardAsync(text);
            ClipboardPreview = text.Length > 120 ? text[..120] + "…" : text;
            StatusMessage = result.Message;
            if (!result.Success)
                await _dialogs.ShowMessageAsync("Clipboard", result.Message);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Clipboard", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunTool() => IsConnected && !IsBusy;

    private async Task<bool> RunToolAsync(Func<Task<DeviceToolResult>> action)
    {
        if (_adbService.SelectedDevice is null)
        {
            StatusMessage = "Cihaz bağlı değil";
            return false;
        }

        try
        {
            IsBusy = true;
            var result = await action();
            StatusMessage = result.Message;
            if (!result.Success)
                await _dialogs.ShowMessageAsync("Hızlı araç", result.Message);
            return result.Success;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message) =>
        await _dialogs.ShowConfirmationAsync(title, message);

    private void StartLiveLoop()
    {
        if (_disposed || !IsLiveMonitoring || !IsConnected) return;
        StopLiveLoop();
        var cts = new CancellationTokenSource();
        _liveCts = cts;
        var generation = Interlocked.Increment(ref _liveGeneration);
        ObservedTask.Run(LiveLoopAsync(generation, cts.Token));
    }

    private void StopLiveLoop()
    {
        Interlocked.Increment(ref _liveGeneration);
        try { _liveCts?.Cancel(); } catch { /* ignore */ }
        _liveCts?.Dispose();
        _liveCts = null;
    }

    private async Task LiveLoopAsync(int generation, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed && generation == _liveGeneration && IsLiveMonitoring)
        {
            try
            {
                if (_adbService.SelectedDevice is null)
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }

                var metrics = await _tools.GetLiveMetricsAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || generation != _liveGeneration)
                    break;

                await _dispatcher.InvokeAsync(() =>
                {
                    if (generation != _liveGeneration || _disposed)
                        return;

                    Live = metrics;
                    PushHistory(CpuHistory, metrics.CpuPercent);
                    PushHistory(RamHistory, metrics.RamPercent);
                    PushHistory(TempHistory, Math.Clamp(metrics.TemperatureC, 0, 80));
                    PushHistory(BatteryHistory, metrics.BatteryPercent);

                    if (DeviceInfo is not null)
                    {
                        DeviceInfo.Battery.Level = metrics.BatteryPercent;
                        DeviceInfo.Battery.Temperature = metrics.TemperatureC;
                        DeviceInfo.Battery.IsCharging = metrics.IsCharging;
                        DeviceInfo.Battery.Voltage = metrics.VoltageMv;
                        DeviceInfo.Cpu.UsagePercent = metrics.CpuPercent;
                        if (metrics.RamTotalKb > 0)
                        {
                            DeviceInfo.Ram.TotalKb = metrics.RamTotalKb;
                            DeviceInfo.Ram.AvailableKb = metrics.RamAvailableKb;
                            DeviceInfo.Ram.FreeKb = metrics.RamAvailableKb;
                        }

                        OnPropertyChanged(nameof(DeviceInfo));
                    }
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // keep polling through transient ADB blips
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void PushHistory(ObservableCollection<double> series, double value)
    {
        series.Add(value);
        while (series.Count > HistoryCapacity)
            series.RemoveAt(0);
    }

    private void ClearHistory()
    {
        CpuHistory.Clear();
        RamHistory.Clear();
        TempHistory.Clear();
        BatteryHistory.Clear();
    }

    private void ClearDeviceState(string message)
    {
        StatusMessage = message;
        IsConnected = false;
        DeviceInfo = null;
        Diagnostics = null;
        Live = null;
        SecuritySummary = null;
        SensorsSummary = "—";
        ClipboardPreview = "";
        ClearHistory();
        StopLiveLoop();
    }

    private void NotifyToolCommands()
    {
        RebootCommand.NotifyCanExecuteChanged();
        RebootRecoveryCommand.NotifyCanExecuteChanged();
        RebootBootloaderCommand.NotifyCanExecuteChanged();
        LockScreenCommand.NotifyCanExecuteChanged();
        ToggleStayAwakeCommand.NotifyCanExecuteChanged();
        WifiOnCommand.NotifyCanExecuteChanged();
        WifiOffCommand.NotifyCanExecuteChanged();
        BluetoothOnCommand.NotifyCanExecuteChanged();
        BluetoothOffCommand.NotifyCanExecuteChanged();
        ScreenshotCommand.NotifyCanExecuteChanged();
        ClipboardFromDeviceCommand.NotifyCanExecuteChanged();
        ClipboardToDeviceCommand.NotifyCanExecuteChanged();
        SaveReportCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private static string FormatSensors(IReadOnlyList<string> sensors)
    {
        if (sensors.Count == 0)
            return "Sensör listesi alınamadı";

        var head = string.Join(", ", sensors.Take(8));
        return sensors.Count > 8 ? $"{head} (+{sensors.Count - 8})" : head;
    }

    private void OnDeviceConnectionChanged(object? sender, Core.Events.DeviceConnectionChangedEventArgs e)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = e.IsConnected || _adbService.SelectedDevice is not null;
            StatusMessage = e.IsConnected
                ? $"{e.Device} bağlandı"
                : $"{e.Device.Serial} bağlantısı kesildi";

            if (e.IsConnected)
            {
                ClearHistory();
                await RefreshAsync();
            }
            else if (_adbService.SelectedDevice is null)
            {
                ClearDeviceState($"{e.Device.Serial} bağlantısı kesildi");
            }
        });
    }

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = device is not null;
            if (device is not null)
            {
                ClearHistory();
                await RefreshAsync();
            }
            else
            {
                ClearDeviceState("Cihaz bağlı değil.");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopLiveLoop();
        _adbService.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _adbService.SelectedDeviceChanged -= OnSelectedDeviceChanged;
    }
}
