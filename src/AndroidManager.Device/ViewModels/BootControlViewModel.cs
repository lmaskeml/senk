using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidManager.Device.ViewModels;

public partial class BootControlViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adb;
    private readonly IBootControlService _boot;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppDialogService _dialogs;
    private bool _disposed;

    [ObservableProperty] private BootStatusInfo? _status;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusMessage = "Cihaz bekleniyor…";

    public BootControlViewModel(
        IAdbService adb,
        IBootControlService boot,
        IUiDispatcher dispatcher,
        IAppDialogService dialogs)
    {
        _adb = adb;
        _boot = boot;
        _dispatcher = dispatcher;
        _dialogs = dialogs;

        _adb.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
    }

    public async Task InitializeAsync()
    {
        IsConnected = _adb.SelectedDevice is not null;
        NotifyCommands();
        if (IsConnected)
            await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            IsConnected = false;
            Status = null;
            StatusMessage = "Cihaz bağlı değil";
            NotifyCommands();
            return;
        }

        try
        {
            IsBusy = true;
            Status = await _boot.GetStatusAsync();
            IsConnected = Status.IsConnected;
            StatusMessage = Status.IsConnected
                ? $"{Status.Serial} — boot durumu güncel"
                : "Cihaz bağlı değil";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RebootSystemAsync()
    {
        if (!await ConfirmAsync("Yeniden başlat", "Cihaz yeniden başlatılsın mı?")) return;
        await RunAsync(() => _boot.RebootAsync(DeviceRebootMode.System));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RebootRecoveryAsync()
    {
        if (!await ConfirmAsync("Recovery", "Cihaz Recovery moduna alınsın mı?")) return;
        await RunAsync(() => _boot.RebootAsync(DeviceRebootMode.Recovery));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RebootBootloaderAsync()
    {
        if (!await ConfirmAsync("Bootloader", "Cihaz Bootloader/Fastboot’a alınsın mı?")) return;
        await RunAsync(() => _boot.RebootAsync(DeviceRebootMode.Bootloader));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task PowerOffAsync()
    {
        if (!await ConfirmAsync("Kapat", "Cihaz kapatılsın mı?")) return;
        await RunAsync(() => _boot.PowerOffAsync());
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SoftRebootAsync()
    {
        if (!await ConfirmAsync(
                "Yumuşak reboot",
                "Android çerçevesi (zygote) yeniden başlatılsın mı?\nÇoğu cihazda root gerekir."))
            return;
        await RunAsync(() => _boot.SoftRebootAsync());
    }

    private bool CanRun() => IsConnected && !IsBusy;

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnIsConnectedChanged(bool value) => NotifyCommands();

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        RebootSystemCommand.NotifyCanExecuteChanged();
        RebootRecoveryCommand.NotifyCanExecuteChanged();
        RebootBootloaderCommand.NotifyCanExecuteChanged();
        PowerOffCommand.NotifyCanExecuteChanged();
        SoftRebootCommand.NotifyCanExecuteChanged();
    }

    private async Task RunAsync(Func<Task<DeviceToolResult>> action)
    {
        try
        {
            IsBusy = true;
            var result = await action();
            StatusMessage = result.Message;
            if (!result.Success)
                await _dialogs.ShowMessageAsync("Boot Control", result.Message);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Boot Control", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private Task<bool> ConfirmAsync(string title, string message) =>
        _dialogs.ShowConfirmationAsync(title, message);

    private void OnDeviceConnectionChanged(object? sender, Core.Events.DeviceConnectionChangedEventArgs e)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = e.IsConnected || _adb.SelectedDevice is not null;
            if (IsConnected) await RefreshAsync();
            else
            {
                Status = null;
                StatusMessage = "Cihaz bağlantısı kesildi";
                NotifyCommands();
            }
        });
    }

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = device is not null;
            if (device is not null) await RefreshAsync();
            else
            {
                Status = null;
                StatusMessage = "Cihaz bağlı değil";
                NotifyCommands();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adb.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged -= OnSelectedDeviceChanged;
    }
}
