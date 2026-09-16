using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Security.ViewModels;

public sealed partial class RescueCenterViewModel : ObservableObject
{
    private readonly IRescueCenterService _rescue;
    private readonly IRecoveryManagerService _recovery;
    private readonly IAppDialogService _dialogs;
    private readonly ILogger _logger;

    [ObservableProperty] private RescueSnapshot? _snapshot;
    [ObservableProperty] private PartitionBackupRecord? _selectedBackup;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Kurtarma merkezi hazır";
    [ObservableProperty] private string _fastbootHint = "algılanmadı";

    public RescueCenterViewModel(
        IRescueCenterService rescue,
        IRecoveryManagerService recovery,
        IAppDialogService dialogs,
        ILogger? logger = null)
    {
        _rescue = rescue;
        _recovery = recovery;
        _dialogs = dialogs;
        _logger = logger ?? Log.ForContext<RescueCenterViewModel>();
    }

    public Task InitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Snapshot = await _rescue.GetSnapshotAsync();
            StatusMessage = Snapshot.Summary;
            FastbootHint = Snapshot.Fastboot switch
            {
                DeviceConnectionMode.Fastboot => "cihaz fastboot'ta",
                DeviceConnectionMode.Offline => "cihaz yok",
                _ => "bilinmiyor"
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Rescue] Yenileme başarısız");
            StatusMessage = $"Kurtarma durumu okunamadı: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RebootSystemAsync()
    {
        if (!await _dialogs.ShowConfirmationAsync("Yeniden Başlat", "Cihaz normal moda yeniden başlatılsın mı?"))
            return;
        var r = await _rescue.RebootSystemAsync();
        StatusMessage = r.Message;
    }

    [RelayCommand]
    private async Task RebootFastbootAsync()
    {
        if (!await _dialogs.ShowConfirmationAsync("Fastboot", "Cihaz bootloader/fastboot moduna alınsın mı?"))
            return;
        var r = await _rescue.RebootFastbootAsync();
        StatusMessage = r.Message;
    }

    [RelayCommand]
    private async Task RebootRecoveryAsync()
    {
        if (!await _dialogs.ShowConfirmationAsync("Recovery", "Recovery moduna alınsın mı?"))
            return;
        var r = await _recovery.RebootRecoveryAsync();
        StatusMessage = r.Message;
    }
}
