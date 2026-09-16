using System.Collections.ObjectModel;
using System.Text;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidManager.Device.ViewModels;

public partial class TerminalViewModel : ObservableObject, IDisposable
{
    private const int MaxOutputChars = 400_000;
    private readonly IAdbService _adb;
    private readonly IAdbTerminalService _terminal;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppDialogService _dialogs;
    private readonly StringBuilder _buffer = new();
    private readonly object _bufferLock = new();
    private bool _disposed;
    private bool _flushScheduled;

    [ObservableProperty] private string _outputText = "";
    [ObservableProperty] private string _commandText = "";
    [ObservableProperty] private string _statusMessage = "Cihaz bekleniyor…";
    [ObservableProperty] private bool _isSessionOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;

    public ObservableCollection<string> CommandHistory { get; } = [];
    private int _historyIndex = -1;

    public TerminalViewModel(
        IAdbService adb,
        IAdbTerminalService terminal,
        IUiDispatcher dispatcher,
        IAppDialogService dialogs)
    {
        _adb = adb;
        _terminal = terminal;
        _dispatcher = dispatcher;
        _dialogs = dialogs;

        _terminal.OutputReceived += OnOutputReceived;
        _terminal.SessionClosed += OnSessionClosed;
        _adb.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
    }

    public Task InitializeAsync()
    {
        IsConnected = _adb.SelectedDevice is not null;
        StatusMessage = IsConnected
            ? $"{_adb.SelectedDevice!.Serial} — oturum açın"
            : "Cihaz bağlı değil";
        NotifyCommands();
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenSessionAsync()
    {
        try
        {
            IsBusy = true;
            await OpenSessionCoreAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("ADB Terminal", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task OpenSessionCoreAsync()
    {
        await _terminal.OpenSessionAsync();
        IsSessionOpen = true;
        StatusMessage = $"Shell açık — {_adb.SelectedDevice?.Serial}";
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task CloseSessionAsync()
    {
        try
        {
            IsBusy = true;
            await _terminal.CloseSessionAsync();
            IsSessionOpen = false;
            StatusMessage = "Oturum kapatıldı";
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

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var line = CommandText.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            IsBusy = true;
            if (!IsSessionOpen)
                await OpenSessionCoreAsync();

            if (!IsSessionOpen)
                return;

            await _terminal.WriteLineAsync(line);
            PushHistory(line);
            CommandText = "";
            _historyIndex = -1;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("ADB Terminal", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand]
    private void ClearOutput()
    {
        lock (_bufferLock)
        {
            _buffer.Clear();
            OutputText = "";
        }
    }

    public void HistoryUp()
    {
        if (CommandHistory.Count == 0) return;
        if (_historyIndex < 0) _historyIndex = CommandHistory.Count;
        _historyIndex = Math.Max(0, _historyIndex - 1);
        CommandText = CommandHistory[_historyIndex];
    }

    public void HistoryDown()
    {
        if (CommandHistory.Count == 0) return;
        if (_historyIndex < 0) return;
        _historyIndex++;
        if (_historyIndex >= CommandHistory.Count)
        {
            _historyIndex = -1;
            CommandText = "";
            return;
        }

        CommandText = CommandHistory[_historyIndex];
    }

    private void PushHistory(string line)
    {
        if (CommandHistory.Count == 0 || CommandHistory[^1] != line)
            CommandHistory.Add(line);
        while (CommandHistory.Count > 100)
            CommandHistory.RemoveAt(0);
    }

    private bool CanOpen() => IsConnected && !IsSessionOpen && !IsBusy;
    private bool CanClose() => IsSessionOpen && !IsBusy;
    private bool CanSend() => IsConnected && !IsBusy;

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnIsConnectedChanged(bool value) => NotifyCommands();
    partial void OnIsSessionOpenChanged(bool value) => NotifyCommands();

    private void NotifyCommands()
    {
        OpenSessionCommand.NotifyCanExecuteChanged();
        CloseSessionCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    private void OnOutputReceived(object? sender, string chunk)
    {
        lock (_bufferLock)
        {
            _buffer.Append(chunk);
            if (_buffer.Length > MaxOutputChars)
                _buffer.Remove(0, _buffer.Length - MaxOutputChars);
        }

        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;
        _dispatcher.Observe(() =>
        {
            _flushScheduled = false;
            lock (_bufferLock)
            {
                OutputText = _buffer.ToString();
            }
        });
    }

    private void OnSessionClosed(object? sender, EventArgs e)
    {
        _dispatcher.Observe(() =>
        {
            IsSessionOpen = false;
            StatusMessage = "Oturum kapandı";
            NotifyCommands();
        });
    }

    private void OnDeviceConnectionChanged(object? sender, Core.Events.DeviceConnectionChangedEventArgs e)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = e.IsConnected || _adb.SelectedDevice is not null;
            if (!IsConnected)
            {
                await _terminal.CloseSessionAsync();
                IsSessionOpen = false;
                StatusMessage = "Cihaz bağlantısı kesildi";
            }
            else
            {
                StatusMessage = $"{_adb.SelectedDevice?.Serial} bağlandı";
            }

            NotifyCommands();
        });
    }

    private void OnSelectedDeviceChanged(object? sender, Core.Models.ConnectedDevice? device)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = device is not null;
            if (IsSessionOpen)
            {
                await _terminal.CloseSessionAsync();
                IsSessionOpen = false;
            }

            StatusMessage = device is null ? "Cihaz bağlı değil" : $"{device.Serial} — oturum açın";
            NotifyCommands();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _terminal.OutputReceived -= OnOutputReceived;
        _terminal.SessionClosed -= OnSessionClosed;
        _adb.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged -= OnSelectedDeviceChanged;
        ObservedTask.Run(_terminal.CloseSessionAsync());
    }
}
