using System.Collections.ObjectModel;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidManager.Files.ViewModels;

public partial class StorageAnalyzerViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adb;
    private readonly IStorageAnalyzerService _analyzer;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppDialogService _dialogs;
    private bool _disposed;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusMessage = "Cihaz bekleniyor…";
    [ObservableProperty] private string _rootPath = "/sdcard";
    [ObservableProperty] private StorageAnalysisResult? _result;
    [ObservableProperty] private StorageFolderUsage? _selectedFolder;
    [ObservableProperty] private StorageLargeFile? _selectedLargeFile;

    public ObservableCollection<StorageFolderUsage> Folders { get; } = [];
    public ObservableCollection<StorageLargeFile> LargeFiles { get; } = [];

    public double UsedPercent => Result?.Volume.UsagePercent ?? 0;
    public string VolumeSummary => Result is null
        ? "—"
        : $"{Result.Volume.UsedFormatted} / {Result.Volume.TotalFormatted} (boş {Result.Volume.FreeFormatted})";

    public StorageAnalyzerViewModel(
        IAdbService adb,
        IStorageAnalyzerService analyzer,
        IUiDispatcher dispatcher,
        IAppDialogService dialogs)
    {
        _adb = adb;
        _analyzer = analyzer;
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
            await AnalyzeAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            IsConnected = false;
            StatusMessage = "Cihaz bağlı değil";
            NotifyCommands();
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Depolama taranıyor (du / find)…";
            var result = await _analyzer.AnalyzeAsync(RootPath);
            Result = result;
            Folders.Clear();
            foreach (var f in result.Folders)
                Folders.Add(f);
            LargeFiles.Clear();
            foreach (var f in result.LargeFiles)
                LargeFiles.Add(f);

            OnPropertyChanged(nameof(UsedPercent));
            OnPropertyChanged(nameof(VolumeSummary));
            StatusMessage = Folders.Count == 0 && LargeFiles.Count == 0
                ? "Sonuç zayıf — cihaz du/find kısıtlı olabilir"
                : $"{Folders.Count} klasör · {LargeFiles.Count} büyük dosya · {result.CapturedAt:t}";
            IsConnected = true;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Depolama Analizi", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private bool CanAnalyze() => IsConnected && !IsBusy;

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnIsConnectedChanged(bool value) => NotifyCommands();

    private void NotifyCommands() => AnalyzeCommand.NotifyCanExecuteChanged();

    private void OnDeviceConnectionChanged(object? sender, Core.Events.DeviceConnectionChangedEventArgs e)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = e.IsConnected || _adb.SelectedDevice is not null;
            if (IsConnected) await AnalyzeAsync();
            else
            {
                Result = null;
                Folders.Clear();
                LargeFiles.Clear();
                StatusMessage = "Cihaz bağlantısı kesildi";
                OnPropertyChanged(nameof(UsedPercent));
                OnPropertyChanged(nameof(VolumeSummary));
                NotifyCommands();
            }
        });
    }

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = device is not null;
            if (device is not null) await AnalyzeAsync();
            else
            {
                Result = null;
                Folders.Clear();
                LargeFiles.Clear();
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
