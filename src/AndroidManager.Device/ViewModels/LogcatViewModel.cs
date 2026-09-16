using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidManager.Device.ViewModels;

public partial class LogcatViewModel : ObservableObject, IDisposable
{
    private const int MaxLines = 5_000;

    private readonly IAdbService _adb;
    private readonly ILogcatService _logcat;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppDialogService _dialogs;
    private readonly List<LogcatLine> _pending = [];
    private readonly object _pendingLock = new();
    private bool _flushScheduled;
    private bool _disposed;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _clearBeforeStart = true;
    [ObservableProperty] private string _statusMessage = "Cihaz bekleniyor…";
    [ObservableProperty] private string _tagFilter = "";
    [ObservableProperty] private string _textFilter = "";
    [ObservableProperty] private string _selectedBuffer = "main";
    [ObservableProperty] private LogcatLevel _minLevel = LogcatLevel.Verbose;
    [ObservableProperty] private LogcatLine? _selectedLine;

    public ObservableCollection<LogcatLine> Lines { get; } = [];
    public IReadOnlyList<string> Buffers { get; } = ["main", "system", "crash", "events", "all"];
    public IReadOnlyList<LogcatLevel> Levels { get; } =
    [
        LogcatLevel.Verbose,
        LogcatLevel.Debug,
        LogcatLevel.Info,
        LogcatLevel.Warn,
        LogcatLevel.Error,
        LogcatLevel.Fatal
    ];

    public LogcatViewModel(
        IAdbService adb,
        ILogcatService logcat,
        IUiDispatcher dispatcher,
        IAppDialogService dialogs)
    {
        _adb = adb;
        _logcat = logcat;
        _dispatcher = dispatcher;
        _dialogs = dialogs;

        _logcat.LineReceived += OnLineReceived;
        _logcat.Stopped += OnStopped;
        _adb.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
    }

    public Task InitializeAsync()
    {
        IsConnected = _adb.SelectedDevice is not null;
        StatusMessage = IsConnected
            ? $"{_adb.SelectedDevice!.Serial} — logcat hazır"
            : "Cihaz bağlı değil";
        NotifyCommands();
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        try
        {
            IsBusy = true;
            await _logcat.StartAsync(new LogcatStartOptions
            {
                MinLevel = MinLevel,
                TagFilter = string.IsNullOrWhiteSpace(TagFilter) ? null : TagFilter.Trim(),
                TextFilter = string.IsNullOrWhiteSpace(TextFilter) ? null : TextFilter.Trim(),
                Buffer = SelectedBuffer,
                ClearBeforeStart = ClearBeforeStart
            });
            IsRunning = true;
            IsPaused = false;
            StatusMessage = $"Logcat çalışıyor — {_adb.SelectedDevice?.Serial}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Logcat Studio", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        try
        {
            IsBusy = true;
            await _logcat.StopAsync();
            IsRunning = false;
            StatusMessage = "Logcat durduruldu";
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

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        StatusMessage = IsPaused ? "Duraklatıldı (yeni satırlar birikiyor)" : "Devam ediyor";
        if (!IsPaused)
            ScheduleFlush();
    }

    [RelayCommand]
    private void ClearLines()
    {
        Lines.Clear();
        lock (_pendingLock) _pending.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanClearDevice))]
    private async Task ClearDeviceAsync()
    {
        try
        {
            IsBusy = true;
            await _logcat.ClearDeviceLogAsync();
            ClearLines();
            StatusMessage = "Cihaz log tamponu temizlendi";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Logcat Studio", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Logcat kayıt klasörü");
        if (string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            IsBusy = true;
            var path = Path.Combine(folder, $"logcat-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var sb = new StringBuilder(Lines.Count * 120);
            foreach (var line in Lines)
                sb.AppendLine(line.Raw);
            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
            StatusMessage = $"Kaydedildi: {path}";
            await _dialogs.ShowMessageAsync("Logcat Studio", StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Logcat Studio", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private bool CanStart() => IsConnected && !IsRunning && !IsBusy;
    private bool CanStop() => IsRunning && !IsBusy;
    private bool CanClearDevice() => IsConnected && !IsBusy;
    private bool CanSave() => Lines.Count > 0 && !IsBusy;

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnIsConnectedChanged(bool value) => NotifyCommands();
    partial void OnIsRunningChanged(bool value) => NotifyCommands();

    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ClearDeviceCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void OnLineReceived(object? sender, LogcatLine line)
    {
        lock (_pendingLock)
        {
            _pending.Add(line);
            while (_pending.Count > MaxLines)
                _pending.RemoveAt(0);
        }

        if (!IsPaused)
            ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;
        _dispatcher.Observe(() =>
        {
            _flushScheduled = false;
            if (IsPaused) return;

            List<LogcatLine> batch;
            lock (_pendingLock)
            {
                if (_pending.Count == 0) return;
                batch = [.._pending];
                _pending.Clear();
            }

            foreach (var line in batch)
                Lines.Add(line);

            while (Lines.Count > MaxLines)
                Lines.RemoveAt(0);

            SaveCommand.NotifyCanExecuteChanged();
            StatusMessage = $"Logcat — {Lines.Count:N0} satır";
        });
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        _dispatcher.Observe(() =>
        {
            IsRunning = false;
            StatusMessage = "Logcat durdu";
            NotifyCommands();
        });
    }

    private void OnDeviceConnectionChanged(object? sender, Core.Events.DeviceConnectionChangedEventArgs e)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = e.IsConnected || _adb.SelectedDevice is not null;
            if (!IsConnected && IsRunning)
                await StopAsync();
            StatusMessage = IsConnected
                ? $"{_adb.SelectedDevice?.Serial} bağlandı"
                : "Cihaz bağlantısı kesildi";
            NotifyCommands();
        });
    }

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = device is not null;
            if (IsRunning)
                await StopAsync();
            StatusMessage = device is null ? "Cihaz bağlı değil" : $"{device.Serial} — logcat hazır";
            NotifyCommands();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _logcat.LineReceived -= OnLineReceived;
        _logcat.Stopped -= OnStopped;
        _adb.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged -= OnSelectedDeviceChanged;
        ObservedTask.Run(_logcat.StopAsync());
    }
}
