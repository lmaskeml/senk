using System.Collections.ObjectModel;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidManager.Apps.ViewModels;

public partial class ApkAnalyzerViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adb;
    private readonly IApkAnalyzerService _analyzer;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusMessage = "Yerel APK veya kurulu paket seçin";
    [ObservableProperty] private string _packageName = "";
    [ObservableProperty] private string _localApkPath = "";
    [ObservableProperty] private ApkAnalysisResult? _result;
    [ObservableProperty] private bool _hasResult;

    public ObservableCollection<string> Permissions { get; } = [];
    public ObservableCollection<string> Activities { get; } = [];
    public ObservableCollection<string> Services { get; } = [];
    public ObservableCollection<string> Receivers { get; } = [];
    public ObservableCollection<string> NativeLibs { get; } = [];
    public ObservableCollection<ApkZipEntryInfo> LargestEntries { get; } = [];

    public ApkAnalyzerViewModel(
        IAdbService adb,
        IApkAnalyzerService analyzer,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher)
    {
        _adb = adb;
        _analyzer = analyzer;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _adb.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
    }

    public Task InitializeAsync()
    {
        IsConnected = _adb.SelectedDevice is not null;
        NotifyCommands();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task PickApkAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "APK seç",
            AndroidPackageFormats.OpenFileFilter,
            multiSelect: false);
        var path = files?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
            return;

        LocalApkPath = path;
        await AnalyzeLocalAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAnalyzeLocal))]
    private async Task AnalyzeLocalAsync()
    {
        if (string.IsNullOrWhiteSpace(LocalApkPath))
        {
            await PickApkAsync();
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Yerel APK analiz ediliyor…";
            ApplyResult(await _analyzer.AnalyzeLocalApkAsync(LocalApkPath));
            StatusMessage = $"{Result!.AppLabel} · {Result.PackageName}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("APK Analyzer", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyzeInstalled))]
    private async Task AnalyzeInstalledAsync()
    {
        if (string.IsNullOrWhiteSpace(PackageName))
        {
            StatusMessage = "Paket adı girin";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Kurulu paket analiz ediliyor…";
            ApplyResult(await _analyzer.AnalyzeInstalledPackageAsync(PackageName.Trim()));
            StatusMessage = $"{Result!.AppLabel} · {Result.PackageName}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("APK Analyzer", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private bool CanAnalyzeLocal() => !IsBusy;
    private bool CanAnalyzeInstalled() => IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(PackageName);

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnIsConnectedChanged(bool value) => NotifyCommands();
    partial void OnPackageNameChanged(string value) => NotifyCommands();

    private void NotifyCommands()
    {
        AnalyzeLocalCommand.NotifyCanExecuteChanged();
        AnalyzeInstalledCommand.NotifyCanExecuteChanged();
    }

    private void ApplyResult(ApkAnalysisResult result)
    {
        Result = result;
        HasResult = true;
        Replace(Permissions, result.Permissions);
        Replace(Activities, result.Activities);
        Replace(Services, result.Services);
        Replace(Receivers, result.Receivers);
        Replace(NativeLibs, result.NativeLibs);
        LargestEntries.Clear();
        foreach (var e in result.LargestEntries)
            LargestEntries.Add(e);
    }

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> source)
    {
        target.Clear();
        foreach (var s in source)
            target.Add(s);
    }

    private void OnDeviceConnectionChanged(object? sender, Core.Events.DeviceConnectionChangedEventArgs e)
    {
        _dispatcher.Observe(() =>
        {
            IsConnected = e.IsConnected || _adb.SelectedDevice is not null;
            NotifyCommands();
        });
    }

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device)
    {
        _dispatcher.Observe(() =>
        {
            IsConnected = device is not null;
            NotifyCommands();
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
